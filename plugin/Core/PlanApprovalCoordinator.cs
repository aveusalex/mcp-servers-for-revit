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
                    BringToAttention(window);
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
                else if (code == "PLAN_NOT_PENDING" || code == "PLAN_NOT_FOUND" || code == "NOT_DOCUMENT_OWNER" ||
                         code == "PLAN_EXPIRED")
                {
                    // Final: this window can't succeed any more, so don't offer the buttons again.
                    window.MarkClosed(DecisionMessage(code, error));
                }
                else
                {
                    window.DecisionFailed(DecisionMessage(code, error));
                }
            }));
        }

        /// <summary>The broker answers in English with a stable code; show the person a Portuguese sentence.</summary>
        private static string DecisionMessage(string code, string error)
        {
            switch (code)
            {
                case "NOT_DOCUMENT_OWNER":
                    return "Este plano é de um documento que não está mais aberto neste Revit (foi fechado, ou reaberto " +
                           "com outra identificação). Ele não pode ser executado; feche esta janela e peça à IA um plano novo.";
                case "PLAN_EXPIRED":
                    return "Este plano expirou. Feche esta janela; a IA pode propor um novo.";
                case "PLAN_NOT_PENDING":
                    return "Este plano já foi decidido ou executado.";
                case "PLAN_NOT_FOUND":
                    return "O broker não conhece mais este plano (ele foi reiniciado ou o plano foi removido).";
                case "HASH_MISMATCH":
                case "PLAN_HASH_MISMATCH":
                    return "O plano mudou desde que esta janela abriu; por segurança a decisão foi recusada.";
                default:
                    return $"O broker recusou a decisão ({code}): {error}";
            }
        }

        private void SendDecision(PlanApprovalWindow window, string decision, string note)
        {
            var send = _sendDecision;
            if (send == null)
            {
                window.DecisionFailed("Sem conexão com o broker do MCP.");
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
        /// <summary>
        /// A new plan must not hide behind other windows: raise it above everything once (without staying
        /// topmost) and flash it in the taskbar until the person looks at it.
        /// </summary>
        private static void BringToAttention(PlanApprovalWindow window)
        {
            window.Topmost = true;
            window.Activate();
            window.Topmost = false;

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var info = new FLASHWINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(FLASHWINFO)),
                hwnd = handle,
                dwFlags = 0x3 | 0xC, // FLASHW_ALL | FLASHW_TIMERNOFG: flash until the window comes to the foreground
                uCount = uint.MaxValue,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO info);

        private void Highlight(PlanApprovalWindow window, IList<long> ids)
        {
            string docId = window.DocId;
            bool queued = UiActionQueue.Instance.Post(app =>
            {
                string message = HighlightInRevit(app, docId, ids);
                _dispatcher.BeginInvoke(new Action(() => window.SetStatus(message, message.StartsWith("Não foi possível"))));
            });
            if (!queued) window.SetStatus("Não foi possível destacar: o add-in ainda está carregando; tente de novo em instantes.");
        }

        private static string HighlightInRevit(UIApplication app, string docId, IList<long> ids)
        {
            var uidoc = app?.ActiveUIDocument;
            if (uidoc == null) return "Não foi possível destacar: nenhum documento ativo.";
            if (DocumentSessionManager.DocIdOf(uidoc.Document) != docId)
                return "Não foi possível destacar: este plano é para outro documento. Ative a janela desse projeto primeiro.";

            var doc = uidoc.Document;
            var elementIds = new List<ElementId>();
            foreach (var raw in ids)
            {
                var id = ToElementId(raw);
                if (doc.GetElement(id) != null) elementIds.Add(id);
            }
            if (elementIds.Count == 0) return "Não foi possível destacar: nenhum dos elementos afetados existe neste documento.";

            uidoc.Selection.SetElementIds(elementIds);
            try { uidoc.ShowElements(elementIds); }
            catch { /* not visible in the active view: the selection still shows them in other views */ }
            int missing = ids.Count - elementIds.Count;
            return $"{elementIds.Count} elemento(s) selecionado(s)" + (missing > 0 ? $" ({missing} não encontrado(s))." : ".");
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
