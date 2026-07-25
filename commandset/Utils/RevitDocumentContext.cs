using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// <para>Resolves which <see cref="Document"/> a command should act on.</para>
    ///
    /// <para>The plugin's broker dispatcher publishes the resolved target document
    /// id on a process-wide <see cref="AppDomain"/> data slot
    /// (<c>"RevitMCP.TargetDocId"</c>) immediately before running a
    /// doc-agnostic command, and clears it afterwards. A plain AppDomain slot is
    /// used deliberately: the plugin and this command assembly share no compile-time
    /// type (commands are loaded by reflection), and commands are serialized onto
    /// Revit's single UI thread, so a global slot set-run-clear is race-free.</para>
    ///
    /// <para>When no target is set (the classic single-document flow, or a ui-bound
    /// command), this falls back to the active document — so existing behaviour is
    /// unchanged.</para>
    /// </summary>
    public static class RevitDocumentContext
    {
        private const string TargetKey = "RevitMCP.TargetDocId";

        /// <summary>
        /// The document a doc-agnostic command should use: the broker-selected
        /// target if one is set and still open, otherwise the active document.
        /// </summary>
        public static Document ResolveDocument(UIApplication uiApp)
        {
            string targetDocId = AppDomain.CurrentDomain.GetData(TargetKey) as string;

            if (!string.IsNullOrEmpty(targetDocId) && uiApp != null)
            {
                foreach (Document doc in uiApp.Application.Documents)
                {
                    if (doc.IsLinked) continue;
                    try
                    {
                        if (DocIdOf(doc) == targetDocId)
                            return doc;
                    }
                    catch
                    {
                        // family docs etc. have no ProjectInformation; skip
                    }
                }
            }

            // No target, or it is no longer open: fall back to the active document.
            return uiApp?.ActiveUIDocument?.Document;
        }

        /// <summary>docId currently requested by the broker, or null.</summary>
        public static string CurrentTargetDocId =>
            AppDomain.CurrentDomain.GetData(TargetKey) as string;

        private static string DocIdOf(Document doc)
        {
            var creationGuid = doc.GetType().GetProperty("CreationGUID")?.GetValue(doc);
            if (creationGuid is Guid guid && guid != Guid.Empty)
                return guid.ToString("D");

            return doc.ProjectInformation?.UniqueId;
        }
    }
}
