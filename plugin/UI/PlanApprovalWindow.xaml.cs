using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.UI
{
    /// <summary>
    /// Non-modal window that shows a change plan an AI proposed (pushed by the broker as
    /// <c>plan_pending</c>) and lets the person at this Revit approve or reject it. The decision is sent
    /// back by the caller (BrokerClient) as <c>plan_decision</c>; the broker only accepts it from the
    /// plugin session that owns the plan's document, and only for the exact plan hash shown here.
    /// </summary>
    public partial class PlanApprovalWindow : Window
    {
        public sealed class StepRow
        {
            public int Index { get; set; }
            public string Tool { get; set; }
            public string Risk { get; set; }
            public int ElementCount { get; set; }
            public string Args { get; set; }
        }

        private readonly Action<string, string> _onDecision;
        private readonly Action<IList<long>> _onHighlight;
        private readonly List<long> _affectedIds;
        private readonly string _risk;
        private bool _decisionSent;

        public string PlanId { get; }
        public string PlanHash { get; }
        public string DocId { get; }

        /// <param name="plan">The plan object from <c>plan_pending</c>.</param>
        /// <param name="planHash">Hash the decision must carry.</param>
        /// <param name="onDecision">(decision, note) callback: "approved" or "rejected".</param>
        /// <param name="onHighlight">Called with the affected element ids.</param>
        public PlanApprovalWindow(JObject plan, string planHash, Action<string, string> onDecision, Action<IList<long>> onHighlight)
        {
            InitializeComponent();
            _onDecision = onDecision;
            _onHighlight = onHighlight;

            PlanId = (string)plan["planId"];
            PlanHash = planHash ?? (string)plan["planHash"];
            DocId = (string)plan["docId"];
            _risk = ((string)plan["risk"] ?? "medium").ToLowerInvariant();

            var docTitle = Clean((string)plan["docTitle"] ?? DocId, 200);
            var expires = (string)plan["expiresAt"];
            var steps = plan["steps"] as JArray ?? new JArray();
            HeaderText.Text =
                $"Document: {docTitle}   |   Risk: {_risk.ToUpperInvariant()}   |   {steps.Count} step(s)" +
                (string.IsNullOrEmpty(expires) ? "" : $"   |   Expires: {FormatTime(expires)}") +
                $"\nPlan {PlanId} (hash {Short(PlanHash)}). Nothing changes until you approve; a batched plan is undone with one Ctrl+Z.";
            SummaryText.Text = Clean((string)plan["summary"], 4000);

            var reasoning = Clean((string)plan["reasoning"], 8000);
            if (string.IsNullOrWhiteSpace(reasoning)) ReasoningExpander.Visibility = Visibility.Collapsed;
            else ReasoningText.Text = reasoning;

            _affectedIds = new List<long>();
            var rows = new List<StepRow>();
            foreach (var s in steps)
            {
                var ids = (s["affectedElementIds"] as JArray)?.Select(t => (long)t).ToList() ?? new List<long>();
                _affectedIds.AddRange(ids);
                rows.Add(new StepRow
                {
                    Index = (int?)s["index"] ?? rows.Count,
                    Tool = Clean((string)s["tool"], 100),
                    Risk = (string)s["risk"],
                    ElementCount = ids.Count,
                    Args = Clean(s["args"]?.ToString(Formatting.None) ?? "{}", 600),
                });
            }
            StepsGrid.ItemsSource = rows;
            HighlightButton.IsEnabled = _affectedIds.Count > 0;
            if (_affectedIds.Count == 0) HighlightButton.ToolTip = "This plan does not name specific element ids.";

            Title = $"MCP - approve change to {docTitle}?";
        }

        /// <summary>Show a status line (errors from the broker, highlight problems...).</summary>
        public void SetStatus(string text, bool error = true)
        {
            StatusText.Foreground = error
                ? System.Windows.Media.Brushes.Firebrick
                : System.Windows.Media.Brushes.DarkGreen;
            StatusText.Text = text ?? "";
        }

        /// <summary>Re-enable the buttons after a refused decision.</summary>
        public void DecisionFailed(string message)
        {
            _decisionSent = false;
            ApproveButton.IsEnabled = RejectButton.IsEnabled = true;
            SetStatus(message);
        }

        private void Highlight_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("");
            _onHighlight?.Invoke(_affectedIds.Distinct().ToList());
        }

        private void Approve_Click(object sender, RoutedEventArgs e)
        {
            if (_risk == "high")
            {
                var answer = MessageBox.Show(this,
                    "This plan is HIGH risk (for example it runs code or makes irreversible changes).\n\nApprove it?",
                    "Confirm high-risk change", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
            }
            Send("approved");
        }

        private void Reject_Click(object sender, RoutedEventArgs e) => Send("rejected");

        private void Send(string decision)
        {
            if (_decisionSent) return;
            _decisionSent = true;
            ApproveButton.IsEnabled = RejectButton.IsEnabled = false;
            SetStatus(decision == "approved" ? "Sending approval..." : "Sending rejection...", false);
            _onDecision?.Invoke(decision, NoteBox.Text);
        }

        /// <summary>
        /// Text written by the AI is shown as plain text; strip control and bidi-override characters so it
        /// cannot disguise itself, and cap its length.
        /// </summary>
        internal static string Clean(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(Math.Min(text.Length, max));
            foreach (var c in text)
            {
                if (sb.Length >= max) { sb.Append("..."); break; }
                bool bidi = (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩') || c == '‎' || c == '‏';
                if (bidi) continue;
                if (char.IsControl(c) && c != '\n' && c != '\t') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Short(string hash) => string.IsNullOrEmpty(hash) ? "?" : hash.Substring(0, Math.Min(12, hash.Length));

        private static string FormatTime(string iso)
        {
            return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? t.ToLocalTime().ToString("HH:mm")
                : iso;
        }
    }
}
