using System;
using System.Collections.Generic;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// Records <c>Application.DocumentChanged</c> into the change accumulators of the calls that are running.
    /// Only active while a call runs; edits the user makes between calls are ignored. Every event and scope lives
    /// on Revit's thread, so no locking is needed.
    /// </summary>
    /// <remarks>Idea from bimwright's McpChangeTracker (Apache-2.0), rewritten.</remarks>
    internal static class DocumentChangeTracker
    {
        private static readonly List<Scope> Scopes = new List<Scope>();
        private static Application _subscribedTo;

        public static void EnsureSubscribed(Application application)
        {
            if (application == null || _subscribedTo != null) return;
            application.DocumentChanged += OnDocumentChanged;
            _subscribedTo = application;
        }

        /// <summary>Records changes of <paramref name="doc"/> into <paramref name="accumulator"/> until disposed.</summary>
        public static IDisposable Track(Document doc, ChangeAccumulator accumulator)
        {
            var scope = new Scope(doc, accumulator);
            Scopes.Add(scope);
            return scope;
        }

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (Scopes.Count == 0) return;
            try
            {
                // Rolled back / undone transactions leave no net change; only commits count.
                if (e.Operation != UndoOperation.TransactionCommitted) return;
                var doc = e.GetDocument();
                foreach (var scope in Scopes.ToArray())
                {
                    if (!scope.Matches(doc)) continue;
                    foreach (var id in e.GetAddedElementIds()) scope.Accumulator.Created(id.GetValue());
                    foreach (var id in e.GetModifiedElementIds()) scope.Accumulator.Modified(id.GetValue());
                    foreach (var id in e.GetDeletedElementIds()) scope.Accumulator.Deleted(id.GetValue());
                }
            }
            catch
            {
                // Change tracking is diagnostics; it must never break a model transaction.
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly Document _doc;

            public Scope(Document doc, ChangeAccumulator accumulator)
            {
                _doc = doc;
                Accumulator = accumulator;
            }

            public ChangeAccumulator Accumulator { get; }

            public bool Matches(Document doc) => doc != null && (ReferenceEquals(doc, _doc) || doc.Equals(_doc));

            public void Dispose()
            {
                Scopes.Remove(this);
            }
        }
    }
}
