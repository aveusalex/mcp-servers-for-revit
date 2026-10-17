using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Graphics.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Views
{
    public sealed class ListViewFiltersArgs
    {
        [JsonProperty("kind")]
        [EnumValues("rule", "selection", "all")]
        [DefaultValue("rule")]
        [Description("rule (default): rule-based filters (ParameterFilterElement). selection: selection filters / saved selections. all: both.")]
        public string Kind { get; set; } = "rule";

        [JsonProperty("nameContains")]
        [Description("Only filters whose name contains this text (case-insensitive).")]
        public string NameContains { get; set; }

        [JsonProperty("includeRules")]
        [DefaultValue(true)]
        [Description("Include categories and rules of rule filters. Default true.")]
        public bool IncludeRules { get; set; } = true;

        [JsonProperty("includeUsage")]
        [DefaultValue(true)]
        [Description("Include the views and view templates where each filter is applied. Default true.")]
        public bool IncludeUsage { get; set; } = true;

        [JsonProperty("limit")]
        [DefaultValue(100)]
        [Description("Page size, 1-1000 (default 100).")]
        public int Limit { get; set; } = 100;

        [JsonProperty("offset")]
        [DefaultValue(0)]
        [Description("Filters to skip, for paging (default 0).")]
        public int Offset { get; set; }
    }

    /// <summary>CON-08: view filters, their rules and where they are applied.</summary>
    [McpTool("list_view_filters", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "views", Idempotent = true,
        Description = "List the view filters of the model: rule-based filters with their categories and rules " +
                      "(parameter, operator, value; numeric values in Revit internal units) and/or selection filters " +
                      "(saved selections) with their element count, plus the views and templates that apply each one " +
                      "(visible/enabled) and unused filters. Paged (limit/offset, total). Read only.")]
    public sealed class ListViewFiltersTool : McpTool<ListViewFiltersArgs>
    {
        protected override object Run(ToolContext ctx, ListViewFiltersArgs a)
        {
            var doc = ctx.Doc;
            string kind = (a.Kind ?? "rule").ToLowerInvariant();
            var filters = new FilteredElementCollector(doc).OfClass(typeof(FilterElement)).Cast<FilterElement>()
                .Where(f => kind == "all" || (kind == "rule" ? f is ParameterFilterElement : f is SelectionFilterElement))
                .Where(f => string.IsNullOrWhiteSpace(a.NameContains) || f.Name.IndexOf(a.NameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var usage = new Dictionary<long, List<object>>();
            if (a.IncludeUsage)
            {
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    ICollection<ElementId> ids;
                    try
                    {
                        if (!v.AreGraphicsOverridesAllowed()) continue;
                        ids = v.GetFilters();
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
                    foreach (var id in ids)
                    {
                        if (!usage.TryGetValue(id.GetValue(), out var list)) usage[id.GetValue()] = list = new List<object>();
                        list.Add(new
                        {
                            viewId = v.Id.GetValue(),
                            name = v.Name,
                            isTemplate = v.IsTemplate,
                            visible = v.GetFilterVisibility(id),
                            enabled = v.GetIsFilterEnabled(id)
                        });
                    }
                }
            }

            var page = Paging.Page(filters, a.Limit, a.Offset, 1000);
            var items = page.Items.Select(f =>
            {
                usage.TryGetValue(f.Id.GetValue(), out var used);
                object rules = null, categories = null;
                int? elementCount = null;
                if (f is ParameterFilterElement pf && a.IncludeRules)
                {
                    categories = pf.GetCategories().Select(c => Category.GetCategory(doc, c)?.Name ?? c.GetValue().ToString()).OrderBy(n => n).ToList();
                    try { rules = Describe(doc, pf.GetElementFilter()); }
                    catch (Exception) { rules = null; }
                }
                if (f is SelectionFilterElement sf)
                    elementCount = sf.GetElementIds().Count(id => doc.GetElement(id) != null);
                return new
                {
                    id = f.Id.GetValue(),
                    name = f.Name,
                    kind = f is SelectionFilterElement ? "selection" : "rule",
                    categories,
                    rules,
                    elementCount,
                    usedIn = a.IncludeUsage ? (used ?? new List<object>()) : null,
                    used = a.IncludeUsage ? (bool?)(used != null) : null
                };
            }).ToList();

            return new
            {
                items,
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated,
                unusedCount = a.IncludeUsage ? filters.Count(f => !usage.ContainsKey(f.Id.GetValue())) : (int?)null
            };
        }

        /// <summary>A readable tree of an element filter: and/or groups and parameter rules.</summary>
        private static object Describe(Document doc, ElementFilter filter)
        {
            switch (filter)
            {
                case null:
                    return null;
                case LogicalAndFilter and:
                    return new { and = and.GetFilters().Select(f => Describe(doc, f)).ToList() };
                case LogicalOrFilter or:
                    return new { or = or.GetFilters().Select(f => Describe(doc, f)).ToList() };
                case ElementParameterFilter epf:
                    var rules = epf.GetRules().Select(r => Rule(doc, r)).ToList();
                    return rules.Count == 1 ? rules[0] : new { and = rules };
                default:
                    return new { filter = filter.GetType().Name, inverted = filter.Inverted };
            }
        }

        private static object Rule(Document doc, FilterRule rule)
        {
            bool not = false;
            while (rule is FilterInverseRule inv) { not = !not; rule = inv.GetInnerRule(); }
            string param = SafeParam(doc, rule);
            switch (rule)
            {
                case FilterStringRule s:
                    return new { parameter = param, op = Op(s.GetEvaluator()), not, value = (object)s.RuleString };
                case FilterDoubleRule d:
                    return new { parameter = param, op = Op(d.GetEvaluator()), not, value = (object)d.RuleValue, valueUnits = "internal" };
                case FilterIntegerRule i:
                    return new { parameter = param, op = Op(i.GetEvaluator()), not, value = (object)i.RuleValue };
                case FilterElementIdRule e:
                    var target = doc.GetElement(e.RuleValue);
                    return new { parameter = param, op = Op(e.GetEvaluator()), not, value = (object)(target?.Name ?? e.RuleValue.GetValue().ToString()) };
                default:
                    var name = rule.GetType().Name; // HasValueFilterRule, HasNoValueFilterRule, SharedParameterApplicableRule...
                    return new { parameter = param, op = name.Replace("FilterRule", "").Replace("Rule", ""), not, value = (object)null };
            }
        }

        private static string SafeParam(Document doc, FilterRule rule)
        {
            try { return FilterRules.ParameterLabel(doc, rule.GetRuleParameter()); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        }

        private static string Op(object evaluator)
        {
            var n = evaluator?.GetType().Name ?? "";
            switch (n)
            {
                case "FilterStringEquals": case "FilterNumericEquals": return "equals";
                case "FilterStringContains": return "contains";
                case "FilterStringBeginsWith": return "beginsWith";
                case "FilterStringEndsWith": return "endsWith";
                case "FilterStringGreater": case "FilterNumericGreater": return "greater";
                case "FilterStringGreaterOrEqual": case "FilterNumericGreaterOrEqual": return "greaterOrEqual";
                case "FilterStringLess": case "FilterNumericLess": return "less";
                case "FilterStringLessOrEqual": case "FilterNumericLessOrEqual": return "lessOrEqual";
                default: return n;
            }
        }
    }
}
