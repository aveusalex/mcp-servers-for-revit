using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Framework.Services;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>What a tool gets when it runs. The dispatcher builds it on Revit's thread.</summary>
    public sealed class ToolContext
    {
        public ToolContext(UIApplication uiApp, Document doc, bool dryRun, string requestId, string batchId)
        {
            UiApp = uiApp;
            Doc = doc;
            DryRun = dryRun;
            RequestId = requestId;
            BatchId = batchId;
            Services = new ToolServices(doc);
        }

        public UIApplication UiApp { get; }

        /// <summary>The document the broker targeted (never assume the active one).</summary>
        public Document Doc { get; }

        public bool DryRun { get; }
        public string RequestId { get; }

        /// <summary>Set when the call is a step of execute_batch.</summary>
        public string BatchId { get; }

        public ChangeSet Changes { get; } = new ChangeSet();
        public List<ToolWarning> Warnings { get; } = new List<ToolWarning>();
        public ToolServices Services { get; }
    }
}
