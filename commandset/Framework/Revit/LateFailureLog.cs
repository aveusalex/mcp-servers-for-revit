using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// Records the text of failures Revit raises for one document while a transaction is open, through the
    /// application's FailuresProcessing event. It never resolves anything; it only lets a rolled-back call
    /// report why, when the failure didn't pass through the transaction's preprocessor.
    /// </summary>
    internal sealed class LateFailureLog : IDisposable
    {
        private readonly Document _doc;
        private readonly Autodesk.Revit.ApplicationServices.Application _app;

        private LateFailureLog(Document doc)
        {
            _doc = doc;
            _app = doc.Application;
            _app.FailuresProcessing += OnFailuresProcessing;
        }

        public List<string> Messages { get; } = new List<string>();

        public static LateFailureLog Watch(Document doc) => new LateFailureLog(doc);

        private void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            try
            {
                var accessor = e.GetFailuresAccessor();
                var doc = accessor.GetDocument();
                if (!(ReferenceEquals(doc, _doc) || doc.Equals(_doc))) return;
                foreach (var failure in accessor.GetFailureMessages())
                {
                    if (failure.GetSeverity() == FailureSeverity.Warning) continue;
                    var text = failure.GetDescriptionText();
                    if (!string.IsNullOrWhiteSpace(text) && !Messages.Contains(text)) Messages.Add(text);
                }
            }
            catch
            {
                // Diagnostics only: never interfere with Revit's own failure handling.
            }
        }

        public void Dispose()
        {
            _app.FailuresProcessing -= OnFailuresProcessing;
        }
    }
}
