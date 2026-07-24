using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// <para>Tracks the set of open Revit documents for this process and which one
    /// is active, and resolves a broker docId back to a live <see cref="Document"/>.</para>
    ///
    /// <para>The routing key is the DOCUMENT, not a port: Revit is single-process /
    /// multi-document, so one plugin session owns every project open in this
    /// Revit.exe. The stable identity is <c>Document.ProjectInformation.UniqueId</c>
    /// — never the title, which changes on "save as".</para>
    /// </summary>
    public class DocumentSessionManager
    {
        private static DocumentSessionManager _instance;
        public static DocumentSessionManager Instance =>
            _instance ?? (_instance = new DocumentSessionManager());

        private ILogger _logger;
        private Application _app;         // full app, for enumerating Documents
        private string _revitVersion = "unknown";
        private string _activeDocId;

        /// <summary>Raised whenever the open-document set or the active document changes.</summary>
        public event Action StateChanged;

        private DocumentSessionManager() { }

        /// <summary>Stable identifier for this Revit process: PID-version.</summary>
        public string SessionId => $"{Process.GetCurrentProcess().Id}-{_revitVersion}";

        public string RevitVersion => _revitVersion;

        /// <summary>
        /// Subscribe to document lifecycle events. Uses a one-shot Idling handler to
        /// capture a <see cref="Application"/>/<see cref="UIApplication"/> at startup,
        /// where only a <see cref="UIControlledApplication"/> is available.
        /// </summary>
        public void Initialize(UIControlledApplication uiCtrlApp, ILogger logger)
        {
            _logger = logger;

            var ctrl = uiCtrlApp.ControlledApplication;
            ctrl.DocumentOpened += OnDocumentChanged;
            ctrl.DocumentCreated += OnDocumentChanged;
            ctrl.DocumentClosed += OnDocumentClosed;
            uiCtrlApp.ViewActivated += OnViewActivated;

            // Grab the full Application (and thus the open-document list) on the first
            // idle tick, then stop listening.
            EventHandler<Autodesk.Revit.UI.Events.IdlingEventArgs> onFirstIdle = null;
            onFirstIdle = (sender, args) =>
            {
                try
                {
                    if (sender is UIApplication uiApp)
                    {
                        _app = uiApp.Application;
                        _revitVersion = _app.VersionNumber;
                        RaiseStateChanged();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error("DocumentSessionManager first-idle capture failed: {0}", ex.Message);
                }
                finally
                {
                    uiCtrlApp.Idling -= onFirstIdle;
                }
            };
            uiCtrlApp.Idling += onFirstIdle;
        }

        // DocumentOpenedEventArgs and DocumentCreatedEventArgs both derive from
        // RevitAPIEventArgs, so one contravariant handler serves both events.
        private void OnDocumentChanged(object sender, RevitAPIEventArgs args)
        {
            if (_app == null && sender is Application app)
            {
                _app = app;
                _revitVersion = app.VersionNumber;
            }
            RaiseStateChanged();
        }

        private void OnDocumentClosed(object sender, DocumentClosedEventArgs args)
        {
            // DocumentClosedEventArgs exposes only DocumentId; enumerating _app.Documents
            // here already excludes the closed document, so a plain rebuild is correct.
            RaiseStateChanged();
        }

        private void OnViewActivated(object sender, ViewActivatedEventArgs args)
        {
            try
            {
                if (sender is UIApplication uiApp)
                    _app = uiApp.Application;
                _activeDocId = DocIdOf(args.Document);
            }
            catch { /* ignore */ }
            RaiseStateChanged();
        }

        private void RaiseStateChanged()
        {
            try { StateChanged?.Invoke(); } catch { /* subscriber errors must not break events */ }
        }

        /// <summary>Stable docId for a document, or null if it has no project information.</summary>
        public static string DocIdOf(Document doc)
        {
            try
            {
                if (doc == null) return null;
                var info = doc.ProjectInformation;
                return info != null ? info.UniqueId : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>docId of the active document, if known.</summary>
        public string ActiveDocId => _activeDocId;

        /// <summary>
        /// A UIApplication built from the captured Application, or null if Revit
        /// has not reached its first idle tick yet. Used to load the command
        /// registry lazily once a document is available.
        /// </summary>
        public UIApplication GetUIApplication() => _app != null ? new UIApplication(_app) : null;

        /// <summary>Resolve a broker docId to the live document, or null if it is gone.</summary>
        public Document ResolveDocument(string docId)
        {
            if (_app == null || string.IsNullOrEmpty(docId)) return null;
            foreach (Document doc in _app.Documents)
            {
                if (doc.IsLinked) continue;
                if (DocIdOf(doc) == docId) return doc;
            }
            return null;
        }

        /// <summary>
        /// Snapshot every open, non-linked document as broker registration payloads.
        /// Called on each lifecycle change and on every heartbeat, so the broker's
        /// view converges even if a single event is missed.
        /// </summary>
        public List<Dictionary<string, object>> BuildDocumentList()
        {
            var list = new List<Dictionary<string, object>>();
            if (_app == null) return list;

            foreach (Document doc in _app.Documents)
            {
                try
                {
                    if (doc.IsLinked) continue;
                    string docId = DocIdOf(doc);
                    if (docId == null) continue;

                    list.Add(new Dictionary<string, object>
                    {
                        ["docId"] = docId,
                        ["title"] = doc.Title,
                        ["pathName"] = doc.PathName ?? "",
                        ["isActive"] = docId == _activeDocId,
                        ["isWorkshared"] = doc.IsWorkshared,
                        ["revitVersion"] = _revitVersion,
                    });
                }
                catch (Exception ex)
                {
                    _logger?.Warning("Skipping a document while building the list: {0}", ex.Message);
                }
            }
            return list;
        }

        public void Shutdown(UIControlledApplication uiCtrlApp)
        {
            try
            {
                var ctrl = uiCtrlApp.ControlledApplication;
                ctrl.DocumentOpened -= OnDocumentChanged;
                ctrl.DocumentCreated -= OnDocumentChanged;
                ctrl.DocumentClosed -= OnDocumentClosed;
                uiCtrlApp.ViewActivated -= OnViewActivated;
            }
            catch { /* ignore */ }
        }
    }
}
