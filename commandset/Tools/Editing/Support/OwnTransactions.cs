using System;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;

namespace RevitMCPCommandSet.Tools.Editing.Support
{
    /// <summary>
    /// For self-transacted tools that need more than one committed transaction (e.g. an assembly must be committed
    /// before it can be renamed, documents can't be opened while a transaction is open): the steps run inside one
    /// TransactionGroup that is assimilated (one undo item) or, on dryRun or any failure, rolled back.
    /// Each step gets the same silent failure handling as the dispatcher's transaction.
    /// </summary>
    internal sealed class OwnTransactions : IDisposable
    {
        private readonly ToolContext _ctx;
        private readonly TransactionGroup _group;
        private bool _done;

        public OwnTransactions(ToolContext ctx, string toolName)
        {
            _ctx = ctx;
            var doc = ctx.Doc;
            if (doc.IsReadOnly)
                throw new ToolException(ToolErrorCodes.DocumentReadOnly, $"'{doc.Title}' is read-only, so it can't be changed.");
            if (doc.IsModifiable)
                throw new ToolException(ToolErrorCodes.DocumentBusy, $"'{doc.Title}' already has an open transaction.",
                    "Ask the user to finish or cancel the current command in Revit, then retry.");
            _group = new TransactionGroup(doc, $"MCP: {toolName}" + (ctx.DryRun ? " (dry run)" : string.Empty));
            _group.Start();
        }

        /// <summary>Runs <paramref name="action"/> in its own committed transaction; throws if Revit rolls it back.</summary>
        public T Step<T>(string name, Func<T> action)
        {
            using (var tx = new Transaction(_ctx.Doc, name))
            {
                var failures = SafeFailuresPreprocessor.Attach(tx, _ctx.Warnings);
                tx.Start();
                T result;
                try { result = action(); }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
                var status = tx.HasEnded() ? tx.GetStatus() : tx.Commit();
                if (status != TransactionStatus.Committed)
                {
                    var why = failures.Errors.Count > 0 ? string.Join(" | ", failures.Errors.Distinct()) : $"status {status}";
                    throw new ToolException(ToolErrorCodes.TransactionRolledBack, $"Revit rejected '{name}' and rolled it back: {why}",
                        "Nothing was changed. Fix the cause reported by Revit and try again.");
                }
                return result;
            }
        }

        public void Step(string name, Action action) => Step<object>(name, () => { action(); return null; });

        /// <summary>Keeps the changes (one undo item), or rolls everything back on dryRun.</summary>
        public void Complete()
        {
            if (_done) return;
            _done = true;
            if (_ctx.DryRun) _group.RollBack();
            else _group.Assimilate();
        }

        public void Dispose()
        {
            if (!_done && _group.HasStarted() && !_group.HasEnded()) _group.RollBack();
            _group.Dispose();
        }
    }
}
