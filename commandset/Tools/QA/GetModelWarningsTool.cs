using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.QA.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA
{
    public sealed class GetModelWarningsArgs
    {
        [JsonProperty("groupBy")]
        [Description("definition (default): one group per warning type (FailureDefinitionId, language-neutral); category: by the category of the first failing element.")]
        [EnumValues("definition", "category")]
        [DefaultValue("definition")]
        public string GroupBy { get; set; } = "definition";

        [JsonProperty("severity")]
        [Description("warning, error or all (default).")]
        [EnumValues("warning", "error", "all")]
        [DefaultValue("all")]
        public string Severity { get; set; } = "all";

        [JsonProperty("categories")]
        [Description("Only warnings involving elements of these categories (any language: OST_Walls / Walls / Paredes).")]
        public List<string> Categories { get; set; }

        [JsonProperty("maxExamplesPerGroup")]
        [Description("Example warnings (with element ids) per group, 0-50 (default 5).")]
        [System.ComponentModel.DataAnnotations.Range(0, 50)]
        [DefaultValue(5)]
        public int MaxExamplesPerGroup { get; set; } = 5;

        [JsonProperty("limit")]
        [Description("Groups per page, 1-500 (default 50), largest groups first.")]
        [DefaultValue(50)]
        public int Limit { get; set; } = 50;

        [JsonProperty("offset")]
        [Description("Groups to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>QA-01: the model's warnings, aggregated by FailureDefinitionId (not by localized text).</summary>
    [McpTool("get_model_warnings", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "qa", Idempotent = true,
        TimeoutSeconds = 120,
        Description = "Summarize the model's warnings (Review Warnings) grouped by warning type - the language-neutral " +
                      "FailureDefinitionId, so pt-BR and English texts group the same - or by category. Each group has " +
                      "count, distinct elements, the text in Revit's language, example element ids and, for common " +
                      "types, a suggested fix. Largest groups first, paged. Read-only.")]
    public sealed class GetModelWarningsTool : McpTool<GetModelWarningsArgs>
    {
        protected override object Run(ToolContext ctx, GetModelWarningsArgs a)
        {
            var doc = ctx.Doc;
            var all = QaSupport.ReadWarnings(doc);
            int total = all.Count;

            var sev = (a.Severity ?? "all").ToLowerInvariant();
            IEnumerable<ModelWarning> ws = all;
            if (sev == "warning") ws = ws.Where(w => w.Severity == FailureSeverity.Warning.ToString());
            else if (sev == "error") ws = ws.Where(w => w.Severity != FailureSeverity.Warning.ToString());

            var cats = ctx.Services.Categories.ResolveAll(a.Categories);
            if (cats.Count > 0)
            {
                var catIds = new HashSet<long>(cats.Select(c => c.Id.GetValue()));
                ws = ws.Where(w => w.All.Any(id => { var c = doc.GetElement(id)?.Category; return c != null && catIds.Contains(c.Id.GetValue()); }));
            }
            var list = ws.ToList();

            bool byCategory = string.Equals(a.GroupBy, "category", StringComparison.OrdinalIgnoreCase);
            var groups = list
                .GroupBy(w => byCategory ? CategoryKey(doc, w) : w.DefinitionId.ToString())
                .Select(g =>
                {
                    var first = g.First();
                    var elements = new HashSet<long>(g.SelectMany(w => w.All).Select(id => id.GetValue()));
                    return new
                    {
                        key = g.Key,
                        definitionId = byCategory ? null : first.DefinitionId.ToString(),
                        description = byCategory ? null : first.Text,
                        descriptions = byCategory ? g.Select(w => w.Text).Distinct().Take(10).ToList() : null,
                        severity = first.Severity,
                        count = g.Count(),
                        elementCount = elements.Count,
                        suggestedFix = byCategory ? null : WarningFixes.For(first.DefinitionId),
                        examples = g.Take(a.MaxExamplesPerGroup).Select(w => new
                        {
                            text = byCategory ? w.Text : null,
                            failing = w.Failing.Select(id => id.GetValue()).ToList(),
                            additional = w.Additional.Count > 0 ? w.Additional.Select(id => id.GetValue()).ToList() : null
                        }).ToList()
                    };
                })
                .OrderByDescending(g => g.count).ThenBy(g => g.key, StringComparer.Ordinal)
                .ToList();

            var page = Paging.Page(groups, a.Limit, a.Offset, 500);
            return new
            {
                totalWarnings = total,
                matchedWarnings = list.Count,
                groupCount = groups.Count,
                groupBy = byCategory ? "category" : "definition",
                groups = page.Items,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated,
                next = page.Truncated ? "Call again with offset = offset + limit." : null
            };
        }

        private static string CategoryKey(Document doc, ModelWarning w)
        {
            foreach (var id in w.Failing)
            {
                var c = doc.GetElement(id)?.Category;
                if (c != null) return CategoryService.BuiltInNameOf(c) ?? c.Name;
            }
            return "(no category)";
        }
    }
}
