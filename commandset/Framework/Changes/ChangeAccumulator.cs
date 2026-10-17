using System.Collections.Generic;
using System.Linq;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// Net element membership across the transactions of one call: an element created and then deleted
    /// disappears, an element created and then modified stays "created". Not a parameter diff.
    /// </summary>
    /// <remarks>
    /// No Revit dependency (ids are longs) so it is testable outside Revit. Idea from bimwright's
    /// DocumentChangeAccumulator (Apache-2.0), rewritten.
    /// </remarks>
    public sealed class ChangeAccumulator
    {
        /// <summary>Most ids reported per list; beyond this the change set is marked truncated.</summary>
        public const int DefaultCap = 500;

        private enum Kind
        {
            Created,
            Modified,
            Deleted
        }

        // Insertion order is kept so the reported ids follow the order Revit reported them.
        private readonly Dictionary<long, Kind> _kinds = new Dictionary<long, Kind>();
        private readonly List<long> _order = new List<long>();
        private readonly int _cap;

        public ChangeAccumulator(int cap = DefaultCap)
        {
            _cap = cap;
        }

        public bool IsEmpty => _kinds.Count == 0;

        public void Created(long id)
        {
            Set(id, Kind.Created);
        }

        public void Modified(long id)
        {
            if (_kinds.TryGetValue(id, out var previous) && previous != Kind.Modified)
            {
                // Created stays created; modifying a deleted id can't happen, keep the deletion.
                return;
            }
            Set(id, Kind.Modified);
        }

        public void Deleted(long id)
        {
            if (_kinds.TryGetValue(id, out var previous) && previous == Kind.Created)
            {
                // Created and deleted inside the same call: net no change.
                _kinds.Remove(id);
                _order.Remove(id);
                return;
            }
            Set(id, Kind.Deleted);
        }

        public void Clear()
        {
            _kinds.Clear();
            _order.Clear();
        }

        /// <summary>Writes the net result into <paramref name="target"/>, keeping ids a tool already put there.</summary>
        public void FlushTo(ChangeSet target)
        {
            Fill(target.Created, Kind.Created, target);
            Fill(target.Modified, Kind.Modified, target);
            Fill(target.Deleted, Kind.Deleted, target);
        }

        public ChangeSet ToChangeSet()
        {
            var set = new ChangeSet();
            FlushTo(set);
            return set;
        }

        private void Fill(List<long> list, Kind kind, ChangeSet target)
        {
            var existing = new HashSet<long>(list);
            foreach (var id in _order.Where(i => _kinds[i] == kind))
            {
                if (existing.Contains(id)) continue;
                if (list.Count >= _cap)
                {
                    target.Truncated = true;
                    return;
                }
                list.Add(id);
                existing.Add(id);
            }
        }

        private void Set(long id, Kind kind)
        {
            if (!_kinds.ContainsKey(id)) _order.Add(id);
            _kinds[id] = kind;
        }
    }
}
