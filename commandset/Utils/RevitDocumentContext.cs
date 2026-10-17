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

        /// <summary>
        /// The open, non-linked document whose docId is <paramref name="docId"/>, or null. Unlike
        /// <see cref="ResolveDocument(UIApplication)"/> it never falls back to the active document, so a call aimed
        /// at a document that was closed can't land on another one. Used by the tool framework, which captures
        /// the docId when the request is queued.
        /// </summary>
        public static Document TryResolveDocument(UIApplication uiApp, string docId)
        {
            if (string.IsNullOrEmpty(docId) || uiApp == null) return null;
            foreach (Document doc in uiApp.Application.Documents)
            {
                if (doc.IsLinked) continue;
                try
                {
                    if (DocIdOf(doc) == docId)
                        return doc;
                }
                catch
                {
                    // documents without ProjectInformation; skip
                }
            }
            return null;
        }

        /// <summary>The broker docId of a document (same rule as the plugin), or null when it has none.</summary>
        public static string GetDocId(Document doc)
        {
            if (doc == null) return null;
            try
            {
                return DocIdOf(doc);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>docId currently requested by the broker, or null.</summary>
        public static string CurrentTargetDocId =>
            AppDomain.CurrentDomain.GetData(TargetKey) as string;

        /// <summary>
        /// Same rule as the plugin's DocumentSessionManager.DocIdOf: the base id (CreationGUID, else
        /// ProjectInformation.UniqueId), suffixed with a path key while another open document shares it.
        /// </summary>
        private static string DocIdOf(Document doc)
        {
            string baseId = BaseIdOf(doc);
            if (baseId == null) return null;

            foreach (Document other in doc.Application.Documents)
            {
                if (other.IsLinked || other.Equals(doc)) continue;
                string otherBase;
                try { otherBase = BaseIdOf(other); }
                catch { continue; }
                if (otherBase == baseId)
                    return baseId + "~" + PathKey(doc);
            }
            return baseId;
        }

        private static string BaseIdOf(Document doc)
        {
            var creationGuid = doc.GetType().GetProperty("CreationGUID")?.GetValue(doc);
            if (creationGuid is Guid guid && guid != Guid.Empty)
                return guid.ToString("D");

            return doc.ProjectInformation?.UniqueId;
        }

        private static string PathKey(Document doc)
        {
            string key = string.IsNullOrEmpty(doc.PathName) ? doc.Title ?? "" : doc.PathName.ToLowerInvariant();
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
                return BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
