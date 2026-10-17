using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query.Support
{
    /// <summary>One DocumentChanged event, reduced to ids.</summary>
    public sealed class ChangeEvent
    {
        public long Seq { get; set; }
        public DateTime Utc { get; set; }
        public string Operation { get; set; }
        public IList<string> Transactions { get; set; }
        public bool ByMcp { get; set; }
        public long[] Added { get; set; }
        public long[] Modified { get; set; }
        public long[] Deleted { get; set; }
    }

    /// <summary>Change log of one document since tracking started.</summary>
    public sealed class DocumentChangeLog
    {
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public List<ChangeEvent> Events { get; } = new List<ChangeEvent>();

        /// <summary>Category id of every id seen added/modified, so deleted ids can still be filtered by category.</summary>
        public Dictionary<long, long> CategoryOf { get; } = new Dictionary<long, long>();

        public int IdCount { get; set; }

        /// <summary>True when old events were dropped to stay under the memory cap.</summary>
        public bool Trimmed { get; set; }
    }

    /// <summary>
    /// UND-09: records <c>Application.DocumentChanged</c> for the whole session (user edits included), per document,
    /// so get_session_changes can tell what changed since a point in time. Revit-thread only. Subscribed lazily on
    /// the first get_session_changes call: changes made before that are not known.
    /// </summary>
    /// <remarks>
    /// Written for this repo; the idea of a DocumentChanged-based dirty tracker comes from sam-aec (GPL, not copied).
    /// The framework's own DocumentChangeTracker only records during a tool call, which is why this log exists.
    /// </remarks>
    public static class SessionChangeLog
    {
        /// <summary>Most ids kept per document across events; oldest events are dropped beyond it.</summary>
        public const int MaxIdsPerDocument = 200000;

        private const int MaxCategoryCache = 200000;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, DocumentChangeLog> Logs = new Dictionary<string, DocumentChangeLog>();
        private static Application _app;
        private static long _seq;

        public static bool IsSubscribed => _app != null;
        public static DateTime? SubscribedUtc { get; private set; }

        public static void EnsureSubscribed(Application app)
        {
            if (app == null || _app != null) return;
            app.DocumentChanged += OnChanged;
            app.DocumentClosing += OnClosing;
            _app = app;
            SubscribedUtc = DateTime.UtcNow;
        }

        public static DocumentChangeLog For(Document doc, bool create)
        {
            var key = KeyOf(doc);
            if (key == null) return null;
            lock (Gate)
            {
                if (!Logs.TryGetValue(key, out var log) && create)
                    Logs[key] = log = new DocumentChangeLog();
                return log;
            }
        }

        public static void Reset(Document doc)
        {
            var key = KeyOf(doc);
            if (key == null) return;
            lock (Gate) Logs[key] = new DocumentChangeLog();
        }

        private static string KeyOf(Document doc)
        {
            if (doc == null) return null;
            return RevitDocumentContext.GetDocId(doc) ?? ("title:" + doc.Title + "|" + doc.PathName);
        }

        private static void OnClosing(object sender, DocumentClosingEventArgs e)
        {
            try
            {
                var key = KeyOf(e.Document);
                if (key != null) lock (Gate) Logs.Remove(key);
            }
            catch
            {
                // never break Revit's close
            }
        }

        private static void OnChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null || doc.IsLinked) return;
                var log = For(doc, true);
                if (log == null) return;

                var names = e.GetTransactionNames()?.ToList() ?? new List<string>();
                var added = e.GetAddedElementIds().Select(i => i.GetValue()).ToArray();
                var modified = e.GetModifiedElementIds().Select(i => i.GetValue()).ToArray();
                var deleted = e.GetDeletedElementIds().Select(i => i.GetValue()).ToArray();
                if (added.Length + modified.Length + deleted.Length == 0) return;

                lock (Gate)
                {
                    if (log.CategoryOf.Count < MaxCategoryCache)
                    {
                        foreach (var id in added.Concat(modified))
                        {
                            if (log.CategoryOf.ContainsKey(id)) continue;
                            var el = doc.GetElement(id.ToRevitElementId());
                            var cat = el?.Category?.Id.GetValue();
                            if (cat.HasValue) log.CategoryOf[id] = cat.Value;
                        }
                    }

                    log.Events.Add(new ChangeEvent
                    {
                        Seq = ++_seq,
                        Utc = DateTime.UtcNow,
                        Operation = e.Operation.ToString(),
                        Transactions = names,
                        ByMcp = names.Any(n => n != null && n.StartsWith("MCP", StringComparison.Ordinal)),
                        Added = added,
                        Modified = modified,
                        Deleted = deleted
                    });
                    log.IdCount += added.Length + modified.Length + deleted.Length;
                    while (log.IdCount > MaxIdsPerDocument && log.Events.Count > 1)
                    {
                        var first = log.Events[0];
                        log.IdCount -= first.Added.Length + first.Modified.Length + first.Deleted.Length;
                        log.Events.RemoveAt(0);
                        log.Trimmed = true;
                    }
                }
            }
            catch
            {
                // diagnostics only: never break a model transaction
            }
        }
    }
}
