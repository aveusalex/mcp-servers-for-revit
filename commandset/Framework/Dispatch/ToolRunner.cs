using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// Runs one tool call on Revit's thread with the lifecycle its kind needs, and always returns an envelope:
    /// <list type="bullet">
    /// <item><b>Read</b>: no transaction.</item>
    /// <item><b>UI</b>: the target must be the active document; no transaction; skipped on dryRun.</item>
    /// <item><b>Write</b>: one transaction "MCP: &lt;tool&gt;" with <see cref="SafeFailuresPreprocessor"/> and
    /// DocumentChanged tracking. dryRun commits inside a TransactionGroup that is then rolled back, so the
    /// returned change set (with real ids) is what would have happened. Inside a batch the batch owns the group.</item>
    /// </list>
    /// </summary>
    internal static class ToolRunner
    {
        public static ToolResult Run(UIApplication app, Document doc, ToolDescriptor tool, JObject args, bool dryRun,
            string requestId, string batchId, bool insideGroup)
        {
            var kind = tool.Metadata.Kind;
            bool isWrite = kind == ToolKind.Write;
            bool effectiveDryRun = isWrite && dryRun;

            if (doc == null && !tool.NoDocumentRequired)
            {
                return ToolErrors.Fail(ToolErrorCodes.DocumentNotFound,
                    "The target document is not open any more.",
                    "Call list_open_documents or get_session_context and target an open document.",
                    "get_session_context");
            }

            var ctx = new ToolContext(app, doc, effectiveDryRun, requestId, batchId);
            var result = new ToolResult { DryRun = effectiveDryRun };
            var trackChanges = isWrite && !tool.SelfTransacted;

            try
            {
                object data;
                switch (kind)
                {
                    case ToolKind.UI:
                        EnsureActive(app, doc, tool.Name);
                        data = dryRun
                            ? new JObject
                            {
                                ["skipped"] = true,
                                ["reason"] = $"'{tool.Name}' acts on the UI, which can't be previewed or rolled back; it did not run."
                            }
                            : tool.CreateInstance().Invoke(ctx, args);
                        break;

                    case ToolKind.Write:
                        data = tool.SelfTransacted
                            ? RunSelfTransacted(ctx, tool, args)
                            : RunInTransaction(ctx, tool, args, insideGroup);
                        break;

                    default:
                        data = tool.CreateInstance().Invoke(ctx, args);
                        break;
                }

                if (data is ToolResult own)
                {
                    // A self-transacted tool (execute_batch) builds its own envelope.
                    own.Warnings = ctx.Warnings.Concat(own.Warnings ?? Enumerable.Empty<ToolWarning>()).ToList();
                    own.DryRun = effectiveDryRun;
                    return own;
                }

                result.Ok = true;
                result.Data = data;
                if (trackChanges) result.Changes = ctx.Changes;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = ToolErrors.FromException(ex);
                if (trackChanges) result.Changes = new ChangeSet(); // rolled back: nothing changed
            }

            result.Warnings = ctx.Warnings;
            return result;
        }

        private static object RunInTransaction(ToolContext ctx, ToolDescriptor tool, JObject args, bool insideGroup)
        {
            var doc = ctx.Doc;
            EnsureWritable(doc);

            var tracked = new ChangeAccumulator();
            TransactionGroup preview = null;
            long logMark = RevitMCPCommandSet.Tools.Query.Support.SessionChangeLog.CurrentSeq;
            try
            {
                object data;
                using (DocumentChangeTracker.Track(doc, tracked))
                {
                    if (ctx.DryRun && !insideGroup)
                    {
                        preview = new TransactionGroup(doc, $"MCP: {tool.Name} (dry run)");
                        preview.Start();
                    }

                    var instance = tool.CreateInstance();
                    int warningsBefore = ctx.Warnings.Count;
                    for (int attempt = 1; ; attempt++)
                    using (var tx = new Transaction(doc, $"MCP: {tool.Name}"))
                    using (var late = LateFailureLog.Watch(doc))
                    {
                        var failures = SafeFailuresPreprocessor.Attach(tx, ctx.Warnings);
                        tx.Start();
                        try
                        {
                            data = instance.Invoke(ctx, args);
                        }
                        catch
                        {
                            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                            throw;
                        }

                        var status = tx.HasEnded() ? tx.GetStatus() : tx.Commit();
                        if (status != TransactionStatus.Committed && attempt == 1 && instance is IRetryAfterRollback retry)
                        {
                            // The tool may know another way: drop the first attempt's errors (keeping them aside in
                            // case it doesn't), let it explain the change of approach, and run it once more.
                            var firstAttempt = ctx.Warnings.Skip(warningsBefore).ToList();
                            while (ctx.Warnings.Count > warningsBefore) ctx.Warnings.RemoveAt(ctx.Warnings.Count - 1);
                            if (retry.PrepareRetry(ctx, failures)) continue;
                            foreach (var w in firstAttempt) ctx.Warnings.Add(w);
                        }
                        if (status != TransactionStatus.Committed)
                        {
                            // Some failures reach FailuresProcessing without passing through the preprocessor.
                            var errors = failures.Errors.Concat(late.Messages).Distinct().ToList();
                            foreach (var m in late.Messages.Where(m => !failures.Errors.Contains(m)))
                                ctx.Warnings.Add(new ToolWarning(m, null, "error"));
                            var why = errors.Count > 0 ? string.Join(" | ", errors) : $"status {status} (Revit gave no reason)";
                            throw new ToolException(ToolErrorCodes.TransactionRolledBack,
                                $"Revit rejected the change and rolled it back: {why}",
                                "Nothing was changed. Fix the cause reported by Revit (see warnings) and try again.");
                        }
                        break;
                    }
                }

                tracked.FlushTo(ctx.Changes);
                return data;
            }
            finally
            {
                if (preview != null)
                {
                    if (preview.HasStarted() && !preview.HasEnded()) preview.RollBack();
                    preview.Dispose();
                    // Revit raises no DocumentChanged for a rolled-back group: forget the preview's commits.
                    RevitMCPCommandSet.Tools.Query.Support.SessionChangeLog.DiscardSince(doc, logMark);
                }
            }
        }

        /// <summary>
        /// Self-transacted tools (stretch_model, manage_assembly, execute_batch...) roll their own group back on dryRun;
        /// Revit raises no DocumentChanged for that, so forget the preview's commits here too.
        /// </summary>
        private static object RunSelfTransacted(ToolContext ctx, ToolDescriptor tool, JObject args)
        {
            long logMark = RevitMCPCommandSet.Tools.Query.Support.SessionChangeLog.CurrentSeq;
            try
            {
                return tool.CreateInstance().Invoke(ctx, args);
            }
            finally
            {
                if (ctx.DryRun) RevitMCPCommandSet.Tools.Query.Support.SessionChangeLog.DiscardSince(ctx.Doc, logMark);
            }
        }

        internal static void EnsureWritable(Document doc)
        {
            if (doc.IsReadOnly)
            {
                throw new ToolException(ToolErrorCodes.DocumentReadOnly,
                    $"'{doc.Title}' is read-only, so it can't be changed.",
                    "Open the source file as a normal document if it should be edited.");
            }
            if (doc.IsModifiable)
            {
                throw new ToolException(ToolErrorCodes.DocumentBusy,
                    $"'{doc.Title}' already has an open transaction (Revit is in the middle of another edit).",
                    "Ask the user to finish or cancel the current command in Revit, then retry.");
            }
        }

        internal static void EnsureActive(UIApplication app, Document doc, string toolName)
        {
            var active = app?.ActiveUIDocument?.Document;
            if (active == null || doc == null || !(ReferenceEquals(active, doc) || active.Equals(doc)))
            {
                throw new ToolException(ToolErrorCodes.RequiresActiveDocument,
                    $"'{toolName}' acts on Revit's UI and needs its target document to be the active one.",
                    "Ask the user to bring that project's window to the front, or target the active document.",
                    "get_session_context");
            }
        }
    }
}
