using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.UI;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Bridges broker plan messages (socket thread) and the approval windows (Revit's UI thread).
    /// One non-modal window per pending plan; a plan re-sent on reconnect re-activates its window.
    /// </summary>
    internal sealed class PlanApprovalCoordinator
    {
        public static PlanApprovalCoordinator Instance { get; } = new PlanApprovalCoordinator();

        private readonly Dictionary<string, PlanApprovalWindow> _windows = new Dictionary<string, PlanApprovalWindow>();
        private Dispatcher _dispatcher;
        private IntPtr _owner;
        private Func<string, string, string, string, Task> _sendDecision;
        private ILogger _logger;

        private PlanApprovalCoordinator() { }

        /// <param name="uiDispatcher">Dispatcher of Revit's UI thread.</param>
        /// <param name="owner">Revit main window handle (windows stay above it), or zero.</param>
        /// <param name="sendDecision">(planId, planHash, decision, note) sender.</param>
        public void Initialize(Dispatcher uiDispatcher, IntPtr owner, Func<string, string, string, string, Task> sendDecision, ILogger logger)
        {
            _dispatcher = uiDispatcher;
            _owner = owner;
            _sendDecision = sendDecision;
            _logger = logger;
        }

        /// <summary>Called on the socket thread with a <c>plan_pending</c> plan.</summary>
        public void ShowPlan(JObject plan, string planHash)
        {
            if (plan == null || _dispatcher == null) return;
            var planId = (string)plan["planId"];
            if (string.IsNullOrEmpty(planId)) return;

            _dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_windows.TryGetValue(planId, out var existing))
                    {
                        existing.Activate();
                        return;
                    }

                    PlanApprovalWindow window = null;
                    window = new PlanApprovalWindow(plan, planHash,
                        (decision, note) => SendDecision(window, decision, note),
                        ids => Highlight(window, ids));
                    if (_owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = _owner;
                    window.Closed += (s, e) => _windows.Remove(planId);
                    _windows[planId] = window;
                    window.Show(); // non-modal: Revit stays usable while the person reviews
                    window.Activate();
                }
                catch (Exception ex)
                {
                    _logger?.Error("Could not show plan approval window: {0}", ex.Message);
                }
            }));
        }

        /// <summary>Called on the socket thread with the broker's <c>plan_decision_ack</c>.</summary>
        public void OnDecisionAck(JObject ack)
        {
            if (ack == null || _dispatcher == null) return;
            var planId = (string)ack["planId"];
            bool ok = (bool?)ack["ok"] ?? false;
            string status = (string)ack["status"];
            string error = (string)ack["error"]?["message"];
            string code = (string)ack["error"]?["code"];

            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (planId == null || !_windows.TryGetValue(planId, out var window)) return;
                if (ok)
                {
                    _logger?.Info("Plan {0} {1} by {2}", planId, status, Environment.UserName);
                    window.Close();
                }
                else if (code == "PLAN_NOT_PENDING" || code == "PLAN_NOT_FOUND")
                {
                    window.SetStatus($"This plan can no longer be decided: {error}");
                }
                else
                {
                    window.DecisionFailed($"The broker refused the decision ({code}): {error}");
                }
            }));
        }

        private void SendDecision(PlanApprovalWindow window, string decision, string note)
        {
            var send = _sendDecision;
            if (send == null)
            {
                window.DecisionFailed("Not connected to the MCP broker.");
                return;
            }
            Task.Run(async () =>
            {
                try
                {
                    await send(window.PlanId, window.PlanHash, decision, note);
                }
                catch (Exception ex)
                {
                    _ = _dispatcher.BeginInvoke(new Action(() => window.DecisionFailed(ex.Message)));
                }
            });
        }

        /// <summary>Select and zoom to the affected elements, in a Revit API context.</summary>
        private void Highlight(PlanApprovalWindow window, IList<long> ids)
        {
            string docId = window.DocId;
            bool queued = UiActionQueue.Instance.Post(app =>
            {
                string message = HighlightInRevit(app, docId, ids);
                _dispatcher.BeginInvoke(new Action(() => window.SetStatus(message, message.StartsWith("Could not"))));
            });
            if (!queued) window.SetStatus("Could not highlight: the add-in is still loading; try again in a moment.");
        }

        private static string HighlightInRevit(UIApplication app, string docId, IList<long> ids)
        {
            var uidoc = app?.ActiveUIDocument;
            if (uidoc == null) return "Could not highlight: no active document.";
            if (DocumentSessionManager.DocIdOf(uidoc.Document) != docId)
                return "Could not highlight: this plan targets another document. Activate that project's window first.";

            var doc = uidoc.Document;
            var elementIds = new List<ElementId>();
            foreach (var raw in ids)
            {
                var id = ToElementId(raw);
                if (doc.GetElement(id) != null) elementIds.Add(id);
            }
            if (elementIds.Count == 0) return "Could not highlight: none of the affected elements exist in this document.";

            uidoc.Selection.SetElementIds(elementIds);
            try { uidoc.ShowElements(elementIds); }
            catch { /* not visible in the active view: the selection still shows them in other views */ }
            int missing = ids.Count - elementIds.Count;
            return $"Selected {elementIds.Count} element(s)" + (missing > 0 ? $" ({missing} not found)." : ".");
        }

        private static ElementId ToElementId(long value)
        {
#if NET8_0_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId((int)value);
#endif
        }
    }
}
