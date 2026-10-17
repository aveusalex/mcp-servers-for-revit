using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.QA.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA
{
    public sealed class NamingPatterns
    {
        [JsonProperty("views")]
        [Description("Regex every view name must match.")]
        public string Views { get; set; }

        [JsonProperty("sheets")]
        [Description("Regex every sheet number must match.")]
        public string Sheets { get; set; }

        [JsonProperty("levels")]
        [Description("Regex every level name must match.")]
        public string Levels { get; set; }

        [JsonProperty("types")]
        [Description("Regex every family type name must match.")]
        public string Types { get; set; }
    }

    public sealed class CheckNamingArgs
    {
        [JsonProperty("kinds")]
        [Description("What to check (default views, sheets, levels): views, sheets, levels, types.")]
        [EnumValues("views", "sheets", "levels", "types")]
        public List<string> Kinds { get; set; }

        [JsonProperty("patterns")]
        [Description("Override the convention with explicit regexes. Default: the office convention in knowledge/nomenclatura.md and folhas.md (REVIT_MCP_KNOWLEDGE_DIR or Documents\\RevitMCP\\knowledge), else hygiene checks only.")]
        public NamingPatterns Patterns { get; set; }

        [JsonProperty("limit")]
        [Description("Offending names listed per kind, 1-2000 (default 100); counts are always complete.")]
        [System.ComponentModel.DataAnnotations.Range(1, 2000)]
        [DefaultValue(100)]
        public int Limit { get; set; } = 100;
    }

    /// <summary>QA-11: names against the office convention plus hygiene checks (copies, stray spaces).</summary>
    [McpTool("check_naming", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "qa", Idempotent = true,
        TimeoutSeconds = 60,
        Description = "Check view names, sheet numbers, level names and family type names against the office naming " +
                      "convention (read from knowledge/nomenclatura.md and folhas.md 'Formato:' lines, or explicit regexes " +
                      "in patterns) and for hygiene problems (\"Copy\"/\"Cópia\"/\"(2)\" leftovers, double or trailing " +
                      "spaces). Returns the rules used and offending names with the problems and a cleaned-up suggestion " +
                      "when one is obvious. Read-only: rename with the renaming tools.")]
    public sealed class CheckNamingTool : McpTool<CheckNamingArgs>
    {
        private static readonly string[] DefaultKinds = { "views", "sheets", "levels" };
        private static readonly Regex CopyLeftover = new Regex(@"(\s*-?\s*\b(copy|c[oó]pia)\b(\s*\d+)?|\s*\(\d+\))\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex CopyPrefix = new Regex(@"^\s*(copy of|c[oó]pia de)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        protected override void Validate(CheckNamingArgs a)
        {
            foreach (var p in new[] { a.Patterns?.Views, a.Patterns?.Sheets, a.Patterns?.Levels, a.Patterns?.Types })
            {
                if (string.IsNullOrEmpty(p)) continue;
                try { _ = new Regex(p); }
                catch (ArgumentException ex) { throw new ToolException("INVALID_ARGUMENTS", $"'{p}' is not a valid regex: {ex.Message}"); }
            }
        }

        protected override object Run(ToolContext ctx, CheckNamingArgs a)
        {
            var doc = ctx.Doc;
            var kinds = new HashSet<string>(a.Kinds == null || a.Kinds.Count == 0 ? DefaultKinds : a.Kinds, StringComparer.OrdinalIgnoreCase);
            var fromKnowledge = NamingConvention.Load(out var sources);
            var rules = new Dictionary<string, NamingRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in fromKnowledge) rules[r.Kind] = r;
            void Override(string kind, string rx)
            {
                if (!string.IsNullOrEmpty(rx))
                    rules[kind] = new NamingRule { Kind = kind, Format = rx, Regex = new Regex(rx, RegexOptions.CultureInvariant), Source = "patterns argument" };
            }
            Override("views", a.Patterns?.Views);
            Override("sheets", a.Patterns?.Sheets);
            Override("levels", a.Patterns?.Levels);
            Override("types", a.Patterns?.Types);

            var result = new Dictionary<string, object>();
            if (kinds.Contains("views"))
            {
                var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && !(v is ViewSheet) && GetModelHealthTool.IsPlaceable(v)).ToList();
                result["views"] = Check("views", views.Select(v => (v.Id.GetValue(), v.Name, v.ViewType.ToString())), rules, a.Limit);
            }
            if (kinds.Contains("sheets"))
            {
                var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
                result["sheets"] = Check("sheets", sheets.Select(s => (s.Id.GetValue(), s.SheetNumber, s.Name)), rules, a.Limit);
            }
            if (kinds.Contains("levels"))
            {
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                result["levels"] = Check("levels", levels.Select(l => (l.Id.GetValue(), l.Name, (string)null)), rules, a.Limit);
            }
            if (kinds.Contains("types"))
            {
                var types = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .Where(t => t.Category != null && t.Category.CategoryType == CategoryType.Model).ToList();
                result["types"] = Check("types", types.Select(t => (t.Id.GetValue(), t.Name, t.FamilyName)), rules, a.Limit);
            }

            return new
            {
                conventionSources = sources,
                rules = rules.Values.Select(r => new { kind = r.Kind, format = r.Format, regex = r.Regex.ToString(), source = r.Source }).ToList(),
                note = rules.Count == 0 ? "No office convention found (knowledge/nomenclatura.md); only hygiene checks ran. Pass patterns or set REVIT_MCP_KNOWLEDGE_DIR." : null,
                results = result
            };
        }

        private static object Check(string kind, IEnumerable<(long id, string name, string extra)> items, Dictionary<string, NamingRule> rules, int limit)
        {
            rules.TryGetValue(kind, out var rule);
            var bad = new List<object>();
            int total = 0, badCount = 0;
            foreach (var (id, name, extra) in items)
            {
                total++;
                var problems = new List<string>();
                var n = name ?? string.Empty;
                if (n != n.Trim()) problems.Add("leading/trailing spaces");
                if (n.Contains("  ")) problems.Add("double spaces");
                if (CopyLeftover.IsMatch(n) || CopyPrefix.IsMatch(n)) problems.Add("looks like a leftover copy");
                if (rule != null && !rule.Regex.IsMatch(n.Trim())) problems.Add($"doesn't follow {rule.Format}");
                if (problems.Count == 0) continue;
                badCount++;
                if (bad.Count >= limit) continue;
                var cleaned = Regex.Replace(CopyPrefix.Replace(CopyLeftover.Replace(n, string.Empty), string.Empty), @"\s{2,}", " ").Trim();
                bad.Add(new
                {
                    id,
                    name,
                    context = extra,
                    problems,
                    suggestion = cleaned != n && cleaned.Length > 0 && (rule == null || rule.Regex.IsMatch(cleaned)) ? cleaned : null
                });
            }
            return new { checkedCount = total, offending = badCount, convention = rule?.Format, items = bad, truncated = badCount > bad.Count };
        }
    }
}
