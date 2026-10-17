using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.History.Support;
using RevitMCPCommandSet.Tools.Query.Support;

namespace RevitMCPCommandSet.Tools.History
{
    // ---- get_undo_history (UND-07, read) ------------------------------------------------------------------------

    public sealed class GetUndoHistoryArgs
    {
        [JsonProperty("limit")]
        [Description("Most entries listed per stack, 1-100 (default 20), newest first.")]
        [Range(1, 100)]
        [DefaultValue(20)]
        public int Limit { get; set; } = 20;

        [JsonProperty("onlyByMcp")]
        [Description("List only this MCP's transactions (\"MCP: <tool>\"); the user's own edits are hidden but still counted. Default false.")]
        [DefaultValue(false)]
        public bool OnlyByMcp { get; set; }

        [JsonProperty("maxIdsPerEntry")]
        [Description("Element ids listed per entry and change kind, 0-200 (default 20). Counts are always complete.")]
        [Range(0, 200)]
        [DefaultValue(20)]
        public int MaxIdsPerEntry { get; set; } = 20;
    }

    /// <summary>UND-07: approximate undo/redo stacks rebuilt from the session change log.</summary>
    [McpTool("get_undo_history", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "history", Idempotent = false,
        Description = "Recent transactions of the target document as an approximate undo stack (newest first) and redo " +
                      "stack: transaction name (this MCP's are \"MCP: <tool>\"), byMcp, time and the element ids " +
                      "added/modified/deleted. Revit's API does not expose its real undo stack: this is rebuilt from " +
                      "the changes recorded since the add-in started (earlier edits, edits that changed no element and " +
                      "grouped commits make it approximate). Use it before undo_last/redo_last to see what the next " +
                      "Ctrl+Z would take back. Does not change the model.")]
    public sealed class GetUndoHistoryTool : McpTool<GetUndoHistoryArgs>
    {
        protected override object Run(ToolContext ctx, GetUndoHistoryArgs a)
        {
            SessionChangeLog.EnsureSubscribed(ctx.UiApp?.Application);
            var log = SessionChangeLog.For(ctx.Doc, true);
            if (log == null) throw new ToolException("NOT_AVAILABLE", "Change tracking is not available for this document.");
            var model = UndoStackModel.Build(log.Events.ToList());

            object List(System.Collections.Generic.List<UndoEntry> stack)
            {
                var rows = Enumerable.Range(0, stack.Count).Reverse()
                    .Select(i => new { entry = stack[i], position = stack.Count - i })
                    .Where(x => !a.OnlyByMcp || x.entry.ByMcp)
                    .Take(a.Limit)
                    .Select(x => x.entry.Describe(x.position, a.MaxIdsPerEntry))
                    .ToList();
                return rows;
            }

            return new
            {
                trackingSince = log.StartedUtc.ToString("o", CultureInfo.InvariantCulture),
                historyTrimmed = log.Trimmed,
                approximate = true,
                undoDepthKnown = model.Undo.Count,
                redoDepthKnown = model.Redo.Count,
                nextUndo = model.NextUndo?.Describe(1, a.MaxIdsPerEntry),
                nextRedo = model.NextRedo?.Describe(1, a.MaxIdsPerEntry),
                undoStack = List(model.Undo),
                redoStack = List(model.Redo),
                unmatchedUndoRedo = model.UnmatchedUndoRedo,
                note = "Position 1 = what the next Ctrl+Z (undo_last) / Ctrl+Y (redo_last) would act on, as far as the " +
                       "change log knows. Revit's own Undo drop-down is the authority."
            };
        }
    }

    // ---- undo_last / redo_last (UND-07, UI) ---------------------------------------------------------------------

    public sealed class UndoRedoArgs
    {
        [JsonProperty("count")]
        [Description("Steps to undo/redo. Always 1: Revit runs a posted command only after this call returns and " +
                     "accepts one posted command at a time, so N steps take N calls (check get_undo_history between them).")]
        [Range(1, 1)]
        [DefaultValue(1)]
        public int Count { get; set; } = 1;

        [JsonProperty("expectTransaction")]
        [Description("Refuse unless the next step (per get_undo_history) has this transaction name, e.g. \"MCP: create_walls\" (or just the tool name). Recommended.")]
        public string ExpectTransaction { get; set; }

        [JsonProperty("allowNonMcp")]
        [Description("Allow it when the next known step is not one of this MCP's transactions (a manual edit of the user) or is unknown. Default false.")]
        [DefaultValue(false)]
        public bool AllowNonMcp { get; set; }
    }

    /// <summary>Shared body of undo_last / redo_last.</summary>
    internal static class UndoRedoPoster
    {
        public static object Post(ToolContext ctx, UndoRedoArgs a, bool undo)
        {
            var verb = undo ? "undo" : "redo";
            var doc = ctx.Doc;
            if (doc.IsModifiable)
                throw new ToolException("DOCUMENT_BUSY", $"'{doc.Title}' has an open transaction; Revit can't {verb} now.",
                    "Ask the user to finish the current command in Revit, then retry.");

            SessionChangeLog.EnsureSubscribed(ctx.UiApp?.Application);
            var log = SessionChangeLog.For(doc, true);
            var model = UndoStackModel.Build(log?.Events.ToList() ?? new System.Collections.Generic.List<ChangeEvent>());
            var next = undo ? model.NextUndo : model.NextRedo;

            if (!string.IsNullOrWhiteSpace(a.ExpectTransaction))
            {
                var want = a.ExpectTransaction.Trim();
                var matches = next != null &&
                              (string.Equals(next.Transaction, want, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(next.Tool, want, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(next.Transaction, "MCP: " + want, StringComparison.OrdinalIgnoreCase));
                if (!matches)
                    throw new ToolException("UNDO_MISMATCH",
                        $"The next {verb} step is {(next == null ? "unknown" : "'" + next.Transaction + "'")}, not '{want}'. Nothing was posted.",
                        "Call get_undo_history to see the stack, then retry with the right expectTransaction.", "get_undo_history");
            }
            if (!a.AllowNonMcp && (next == null || !next.ByMcp))
                throw new ToolException("NOT_AN_MCP_STEP",
                    next == null
                        ? $"The change log knows no step to {verb} (it was made before tracking started, or there is none). Nothing was posted."
                        : $"The next {verb} step is '{next.Transaction}', a change not made by this MCP. Nothing was posted.",
                    $"Ask the user; pass allowNonMcp=true only if they want that step {verb}ne.", "get_undo_history");

            var app = ctx.UiApp ?? throw new ToolException("NOT_AVAILABLE", "No Revit UI application.");
            RevitCommandId id = null;
            string idName = null;
            try
            {
                id = RevitCommandId.LookupPostableCommandId(undo ? PostableCommand.Undo : PostableCommand.Redo);
                idName = id?.Name;
            }
            catch (Exception)
            {
                id = null;
            }
            if (id == null)
            {
                idName = undo ? "ID_EDIT_UNDO" : "ID_EDIT_REDO";
                id = RevitCommandId.LookupCommandId(idName);
            }
            if (id == null)
                throw new ToolException("COMMAND_NOT_FOUND", $"This Revit version has no {verb} command id that an add-in can post.",
                    $"Ask the user to press {(undo ? "Ctrl+Z" : "Ctrl+Y")} in Revit.");
            if (!app.CanPostCommand(id))
                throw new ToolException("COMMAND_NOT_POSTABLE", $"Revit does not allow posting {verb} right now.",
                    $"Ask the user to press {(undo ? "Ctrl+Z" : "Ctrl+Y")} in Revit.");
            try
            {
                app.PostCommand(id);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                throw new ToolException("COMMAND_ALREADY_POSTED", $"Revit refused to queue {verb}: {ex.Message}",
                    "Another posted command is still pending; wait a moment and retry.");
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("COMMAND_NOT_POSTABLE", $"Revit refused to queue {verb}: {ex.Message}",
                    $"Ask the user to press {(undo ? "Ctrl+Z" : "Ctrl+Y")} in Revit.");
            }

            ctx.Warnings.Add(new ToolWarning(
                $"{(undo ? "Undo" : "Redo")} was posted to Revit and runs after this call returns: the result is not known yet. " +
                "Call get_undo_history (or get_session_changes) to confirm what was taken back.", null, "info"));
            return new
            {
                posted = true,
                command = idName,
                document = doc.Title,
                expectedStep = next?.Describe(1, 20),
                steps = 1,
                verifyWith = "get_undo_history"
            };
        }
    }

    [McpTool("undo_last", Kind = ToolKind.UI, Risk = Risk.Medium, Toolset = "history", Batchable = false,
        Description = "Ask Revit to undo the last step of the active document (posts Revit's own Undo command, like " +
                      "Ctrl+Z). By default only when get_undo_history shows that step is one of this MCP's transactions " +
                      "(\"MCP: <tool>\"); pass expectTransaction to be sure what is taken back. One step per call. The " +
                      "command runs AFTER the call returns, so the result says posted=true, not what was undone: verify " +
                      "with get_undo_history. Active document only; not in dryRun or batches. Reverse with redo_last.")]
    public sealed class UndoLastTool : McpTool<UndoRedoArgs>
    {
        protected override object Run(ToolContext ctx, UndoRedoArgs a) => UndoRedoPoster.Post(ctx, a, undo: true);
    }

    [McpTool("redo_last", Kind = ToolKind.UI, Risk = Risk.Medium, Toolset = "history", Batchable = false,
        Description = "Ask Revit to redo the last undone step of the active document (posts Revit's own Redo command, " +
                      "like Ctrl+Y). By default only when get_undo_history shows that step is one of this MCP's " +
                      "transactions; pass expectTransaction to be sure. One step per call. The command runs AFTER the " +
                      "call returns, so the result says posted=true, not what was redone: verify with get_undo_history. " +
                      "Any new change clears Revit's redo list. Active document only; not in dryRun or batches.")]
    public sealed class RedoLastTool : McpTool<UndoRedoArgs>
    {
        protected override object Run(ToolContext ctx, UndoRedoArgs a) => UndoRedoPoster.Post(ctx, a, undo: false);
    }
}
