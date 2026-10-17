using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RevitMCPCommandSet.Tools.Query.Support;

namespace RevitMCPCommandSet.Tools.History.Support
{
    /// <summary>One entry of the reconstructed undo/redo stack.</summary>
    public sealed class UndoEntry
    {
        public string Transaction { get; set; }
        public bool ByMcp { get; set; }
        public DateTime Utc { get; set; }
        public long Seq { get; set; }
        public long[] Added { get; set; }
        public long[] Modified { get; set; }
        public long[] Deleted { get; set; }

        public string Tool =>
            ByMcp && Transaction != null && Transaction.StartsWith("MCP: ", StringComparison.Ordinal)
                ? Transaction.Substring(5).Replace(" (dry run)", "").Trim()
                : null;

        public object Describe(int position, int maxIds)
        {
            return new
            {
                position,
                transaction = Transaction,
                byMcp = ByMcp,
                tool = Tool,
                utc = Utc.ToString("o", CultureInfo.InvariantCulture),
                seq = Seq,
                counts = new { added = Added.Length, modified = Modified.Length, deleted = Deleted.Length },
                added = Added.Take(maxIds).ToArray(),
                modified = Modified.Take(maxIds).ToArray(),
                deleted = Deleted.Take(maxIds).ToArray(),
                idsTruncated = Added.Length > maxIds || Modified.Length > maxIds || Deleted.Length > maxIds
            };
        }
    }

    /// <summary>
    /// UND-07: Revit's API does not expose the undo stack, so this rebuilds an approximation of it from the
    /// session change log (DocumentChanged events): commits push, TransactionUndone moves entries to the redo
    /// stack, TransactionRedone moves them back, a new commit clears the redo stack and a rolled-back group
    /// (dryRun preview) drops what it had pushed. Changes made before tracking started, transactions that changed
    /// no element and assimilated groups (one Revit undo step, several commits here) make it approximate.
    /// </summary>
    public sealed class UndoStackModel
    {
        public List<UndoEntry> Undo { get; } = new List<UndoEntry>(); // last = top
        public List<UndoEntry> Redo { get; } = new List<UndoEntry>(); // last = top
        public int UnmatchedUndoRedo { get; private set; }

        public static UndoStackModel Build(IEnumerable<ChangeEvent> events)
        {
            var model = new UndoStackModel();
            foreach (var ev in events) model.Apply(ev);
            return model;
        }

        private void Apply(ChangeEvent ev)
        {
            var names = (ev.Transactions ?? new List<string>()).Where(n => !string.IsNullOrEmpty(n)).ToList();
            switch (ev.Operation)
            {
                case "TransactionCommitted":
                    Redo.Clear();
                    Undo.Add(new UndoEntry
                    {
                        Transaction = names.Count > 0 ? string.Join(" + ", names) : "(unnamed)",
                        ByMcp = ev.ByMcp,
                        Utc = ev.Utc,
                        Seq = ev.Seq,
                        Added = ev.Added ?? new long[0],
                        Modified = ev.Modified ?? new long[0],
                        Deleted = ev.Deleted ?? new long[0]
                    });
                    break;

                case "TransactionUndone":
                    Move(Undo, Redo, Math.Max(1, names.Count));
                    break;

                case "TransactionRedone":
                    Move(Redo, Undo, Math.Max(1, names.Count));
                    break;

                case "TransactionGroupRolledBack":
                    // A rolled-back group (dryRun preview) takes back the commits made inside it: drop the top
                    // entries whose name matches (the group is "MCP: tool (dry run)", its commits "MCP: tool").
                    var bases = new HashSet<string>(names.Select(n => n.Replace(" (dry run)", "").Trim()), StringComparer.Ordinal);
                    var removed = 0;
                    while (Undo.Count > 0 && Undo[Undo.Count - 1].Seq < ev.Seq &&
                           (bases.Count == 0 || bases.Contains(Undo[Undo.Count - 1].Transaction) ||
                            bases.Any(b => Undo[Undo.Count - 1].Transaction.StartsWith(b, StringComparison.Ordinal))))
                    {
                        Undo.RemoveAt(Undo.Count - 1);
                        if (++removed >= 50 || bases.Count == 0) break;
                    }
                    break;
            }
        }

        private void Move(List<UndoEntry> from, List<UndoEntry> to, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (from.Count == 0)
                {
                    UnmatchedUndoRedo++;
                    return;
                }
                var top = from[from.Count - 1];
                from.RemoveAt(from.Count - 1);
                to.Add(top);
            }
        }

        public UndoEntry NextUndo => Undo.Count > 0 ? Undo[Undo.Count - 1] : null;
        public UndoEntry NextRedo => Redo.Count > 0 ? Redo[Redo.Count - 1] : null;
    }
}
