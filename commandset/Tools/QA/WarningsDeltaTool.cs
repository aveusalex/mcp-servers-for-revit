using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.QA.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA
{
    public sealed class WarningsDeltaArgs
    {
        [JsonProperty("operation")]
        [Required]
        [Description("baseline: snapshot the current warnings to a file (replaces the previous baseline of that name); delta: compare the current warnings with the baseline.")]
        [EnumValues("baseline", "delta")]
        public string Operation { get; set; }

        [JsonProperty("baselineName")]
        [Description("Name of the baseline (letters, digits, - and _; default \"default\"). Baselines are per document.")]
        [DefaultValue("default")]
        public string BaselineName { get; set; } = "default";

        [JsonProperty("limit")]
        [Description("Delta: new/resolved warnings listed per page, 1-1000 (default 100).")]
        [DefaultValue(100)]
        public int Limit { get; set; } = 100;

        [JsonProperty("offset")]
        [Description("Delta: items to skip in the new/resolved lists (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>
    /// QA-02: warnings baseline and delta. A warning's key is its FailureDefinitionId plus the sorted UniqueIds of its
    /// elements (UniqueIds survive synchronize; ElementIds can change). The baseline is a JSON file under
    /// Documents\RevitMCP\qa\warnings-baselines, so it survives a Revit restart.
    /// </summary>
    [McpTool("warnings_delta", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "qa", TimeoutSeconds = 120,
        Description = "Track warnings across a work session: operation baseline saves the current warnings (per document, " +
                      "to a file in Documents\\RevitMCP\\qa), and operation delta reports which warnings are new and which " +
                      "were resolved since then, grouped by warning type, with element ids. Run baseline before a change " +
                      "and delta after it to prove the change introduced no warnings. Does not change the model.")]
    public sealed class WarningsDeltaTool : McpTool<WarningsDeltaArgs>
    {
        private static readonly Regex SafeName = new Regex("^[A-Za-z0-9_-]{1,64}$");

        private sealed class Entry
        {
            [JsonProperty("key")] public string Key { get; set; }
            [JsonProperty("definitionId")] public string DefinitionId { get; set; }
            [JsonProperty("severity")] public string Severity { get; set; }
            [JsonProperty("text")] public string Text { get; set; }
            [JsonProperty("elementIds")] public List<long> ElementIds { get; set; }
        }

        private sealed class Baseline
        {
            [JsonProperty("document")] public string Document { get; set; }
            [JsonProperty("path")] public string Path { get; set; }
            [JsonProperty("createdAt")] public string CreatedAt { get; set; }
            [JsonProperty("count")] public int Count { get; set; }
            [JsonProperty("warnings")] public List<Entry> Warnings { get; set; }
        }

        protected override void Validate(WarningsDeltaArgs a)
        {
            if (string.IsNullOrWhiteSpace(a.BaselineName)) a.BaselineName = "default";
            if (!SafeName.IsMatch(a.BaselineName))
                throw new ToolException("INVALID_ARGUMENTS", "baselineName may only contain letters, digits, - and _ (max 64).");
            if (a.Limit < 1 || a.Limit > 1000) throw new ToolException("INVALID_ARGUMENTS", "limit must be between 1 and 1000.");
        }

        protected override object Run(ToolContext ctx, WarningsDeltaArgs a)
        {
            var doc = ctx.Doc;
            var file = PathPolicy.Resolve($"{QaSupport.DocumentKey(doc)}-{a.BaselineName}.json",
                System.IO.Path.Combine("qa", "warnings-baselines"), forWrite: true);
            var current = Current(doc);

            if (string.Equals(a.Operation, "baseline", StringComparison.OrdinalIgnoreCase))
            {
                var b = new Baseline
                {
                    Document = doc.Title,
                    Path = doc.PathName,
                    CreatedAt = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                    Count = current.Count,
                    Warnings = current
                };
                File.WriteAllText(file, JsonConvert.SerializeObject(b, Formatting.Indented), new UTF8Encoding(false));
                return new
                {
                    operation = "baseline",
                    baselineName = a.BaselineName,
                    file,
                    warningCount = current.Count,
                    byType = current.GroupBy(w => w.DefinitionId).Count(),
                    next = "After the work, call warnings_delta with operation \"delta\"."
                };
            }

            if (!File.Exists(file))
                throw new ToolException("BASELINE_NOT_FOUND", $"No baseline '{a.BaselineName}' for '{doc.Title}'.",
                    "Call warnings_delta with operation \"baseline\" first.");
            Baseline baseline;
            try
            {
                baseline = JsonConvert.DeserializeObject<Baseline>(File.ReadAllText(file, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                throw new ToolException("BASELINE_UNREADABLE", $"The baseline file is damaged: {ex.Message}", "Take a new baseline.");
            }
            var before = new HashSet<string>((baseline?.Warnings ?? new List<Entry>()).Select(w => w.Key));
            var now = new HashSet<string>(current.Select(w => w.Key));
            var added = current.Where(w => !before.Contains(w.Key)).ToList();
            var resolved = (baseline?.Warnings ?? new List<Entry>()).Where(w => !now.Contains(w.Key)).ToList();

            object Summary(List<Entry> xs) => xs.GroupBy(w => w.DefinitionId)
                .Select(g => new { definitionId = g.Key, description = g.First().Text, count = g.Count(), suggestedFix = WarningFixes.For(Guid.TryParse(g.Key, out var gid) ? gid : Guid.Empty) })
                .OrderByDescending(g => g.count).ToList();

            var addedPage = Paging.Page(added, a.Limit, a.Offset, 1000);
            var resolvedPage = Paging.Page(resolved, a.Limit, a.Offset, 1000);
            return new
            {
                operation = "delta",
                baselineName = a.BaselineName,
                baselineCreatedAt = baseline?.CreatedAt,
                baselineCount = before.Count,
                currentCount = current.Count,
                newCount = added.Count,
                resolvedCount = resolved.Count,
                unchangedCount = current.Count - added.Count,
                verdict = added.Count == 0 ? "no new warnings" : $"{added.Count} new warning(s)",
                newByType = Summary(added),
                resolvedByType = Summary(resolved),
                @new = addedPage.Items.Select(w => new { definitionId = w.DefinitionId, text = w.Text, elementIds = w.ElementIds }).ToList(),
                resolved = resolvedPage.Items.Select(w => new { definitionId = w.DefinitionId, text = w.Text, elementIds = w.ElementIds }).ToList(),
                truncated = addedPage.Truncated || resolvedPage.Truncated
            };
        }

        private static List<Entry> Current(Document doc)
        {
            var list = new List<Entry>();
            foreach (var w in QaSupport.ReadWarnings(doc))
            {
                var ids = w.All.Select(id => id.GetValue()).Distinct().OrderBy(x => x).ToList();
                var uids = w.All.Select(id => doc.GetElement(id)?.UniqueId ?? "id:" + id.GetValue().ToString(CultureInfo.InvariantCulture))
                    .Distinct().OrderBy(s => s, StringComparer.Ordinal);
                list.Add(new Entry
                {
                    Key = w.DefinitionId.ToString("N") + "|" + string.Join(",", uids),
                    DefinitionId = w.DefinitionId.ToString(),
                    Severity = w.Severity,
                    Text = w.Text,
                    ElementIds = ids
                });
            }
            // Identical keys (same type on the same elements twice) are kept once.
            return list.GroupBy(e => e.Key).Select(g => g.First()).ToList();
        }
    }
}
