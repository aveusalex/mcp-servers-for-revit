using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.QA.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA
{
    public sealed class ParameterPatternRule
    {
        [JsonProperty("parameter")]
        [Description("Parameter to check: {guid} | {builtIn} | {name} or a plain name.")]
        public ParameterRef Parameter { get; set; }

        [JsonProperty("regex")]
        [Description("Regular expression the (non-empty) value must match, e.g. \"^P\\\\d{2}$\".")]
        public string Regex { get; set; }
    }

    public sealed class ParameterRule
    {
        [JsonProperty("categories")]
        [Description("Categories the rule applies to, any language (OST_Doors / Doors / Portas).")]
        public List<string> Categories { get; set; }

        [JsonProperty("required")]
        [Description("Parameters that must have a value (instance first, then type).")]
        public List<ParameterRef> Required { get; set; }

        [JsonProperty("unique")]
        [Description("Parameters whose non-empty values must be unique within each category (e.g. Mark, room Number).")]
        public List<ParameterRef> Unique { get; set; }

        [JsonProperty("patterns")]
        [Description("Parameters whose values must match a regex.")]
        public List<ParameterPatternRule> Patterns { get; set; }
    }

    public sealed class CheckRequiredParametersArgs
    {
        [JsonProperty("rules")]
        [Description("Rules to check. Omit for the defaults: doors and windows need a unique Mark; rooms need a Name and a unique Number.")]
        public List<ParameterRule> Rules { get; set; }

        [JsonProperty("view")]
        [Description("Only elements visible in this view (name or id). Default: the whole model.")]
        public string View { get; set; }

        [JsonProperty("maxItemsPerGroup")]
        [Description("Element ids listed per violation group, 1-1000 (default 50); counts are always complete.")]
        [System.ComponentModel.DataAnnotations.Range(1, 1000)]
        [DefaultValue(50)]
        public int MaxItemsPerGroup { get; set; } = 50;
    }

    /// <summary>QA-12: required parameters, duplicated marks/numbers and value patterns.</summary>
    [McpTool("check_required_parameters", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "qa", Idempotent = true,
        TimeoutSeconds = 120,
        Description = "Check parameter rules per category: required parameters that are empty, values that must be unique " +
                      "(duplicate door/window Marks, room Numbers) and values that must match a regex. Without rules it " +
                      "checks the defaults (doors/windows: unique Mark; rooms: Name and unique Number). Returns violation " +
                      "groups with counts and element ids; fix them with set_parameters. Read-only.")]
    public sealed class CheckRequiredParametersTool : McpTool<CheckRequiredParametersArgs>
    {
        private static List<ParameterRule> Defaults() => new List<ParameterRule>
        {
            new ParameterRule
            {
                Categories = new List<string> { "OST_Doors", "OST_Windows" },
                Required = new List<ParameterRef> { new ParameterRef { BuiltIn = "ALL_MODEL_MARK" } },
                Unique = new List<ParameterRef> { new ParameterRef { BuiltIn = "ALL_MODEL_MARK" } }
            },
            new ParameterRule
            {
                Categories = new List<string> { "OST_Rooms" },
                Required = new List<ParameterRef> { new ParameterRef { BuiltIn = "ROOM_NAME" }, new ParameterRef { BuiltIn = "ROOM_NUMBER" } },
                Unique = new List<ParameterRef> { new ParameterRef { BuiltIn = "ROOM_NUMBER" } }
            }
        };

        protected override void Validate(CheckRequiredParametersArgs a)
        {
            foreach (var r in a.Rules ?? new List<ParameterRule>())
            {
                if (r == null || (r.Categories?.Count ?? 0) == 0)
                    throw new ToolException("INVALID_ARGUMENTS", "Every rule needs categories.");
                if ((r.Required?.Count ?? 0) + (r.Unique?.Count ?? 0) + (r.Patterns?.Count ?? 0) == 0)
                    throw new ToolException("INVALID_ARGUMENTS", "Every rule needs required, unique or patterns.");
                foreach (var p in r.Patterns ?? new List<ParameterPatternRule>())
                {
                    if (p?.Parameter == null || p.Parameter.IsEmpty || string.IsNullOrEmpty(p.Regex))
                        throw new ToolException("INVALID_ARGUMENTS", "Every pattern needs parameter and regex.");
                    try { _ = new Regex(p.Regex); }
                    catch (ArgumentException ex) { throw new ToolException("INVALID_ARGUMENTS", $"'{p.Regex}' is not a valid regex: {ex.Message}"); }
                }
            }
        }

        protected override object Run(ToolContext ctx, CheckRequiredParametersArgs a)
        {
            var doc = ctx.Doc;
            var ps = ctx.Services.Parameters;
            var rules = a.Rules == null || a.Rules.Count == 0 ? Defaults() : a.Rules;
            View view = string.IsNullOrWhiteSpace(a.View) ? null : ctx.Services.Lookup.View(a.View);

            var groups = new List<object>();
            int checkedElements = 0, violations = 0;

            for (int ri = 0; ri < rules.Count; ri++)
            {
                var rule = rules[ri];
                foreach (var cat in ctx.Services.Categories.ResolveAll(rule.Categories))
                {
                    var collector = view == null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, view.Id);
                    var elements = collector.WhereElementIsNotElementType().WherePasses(new ElementCategoryFilter(cat.Id))
                        .ToElements().Where(e => !(e is SpatialElement s) || s.Location != null)
                        .OrderBy(e => e.Id.GetValue()).ToList();
                    checkedElements += elements.Count;

                    foreach (var r in rule.Required ?? new List<ParameterRef>())
                    {
                        var missing = elements.Where(e => string.IsNullOrWhiteSpace(Value(ps, e, r, out _))).ToList();
                        if (missing.Count == 0) continue;
                        violations += missing.Count;
                        groups.Add(new
                        {
                            rule = ri,
                            kind = "missing",
                            category = cat.Name,
                            parameter = r.ToString(),
                            count = missing.Count,
                            elementIds = missing.Take(a.MaxItemsPerGroup).Select(e => e.Id.GetValue()).ToList()
                        });
                    }

                    foreach (var r in rule.Unique ?? new List<ParameterRef>())
                    {
                        var dups = elements.Select(e => new { e, v = Value(ps, e, r, out _) })
                            .Where(x => !string.IsNullOrWhiteSpace(x.v))
                            .GroupBy(x => x.v.Trim(), StringComparer.OrdinalIgnoreCase)
                            .Where(g => g.Count() > 1).ToList();
                        if (dups.Count == 0) continue;
                        violations += dups.Sum(g => g.Count());
                        groups.Add(new
                        {
                            rule = ri,
                            kind = "duplicate",
                            category = cat.Name,
                            parameter = r.ToString(),
                            count = dups.Sum(g => g.Count()),
                            duplicates = dups.Take(a.MaxItemsPerGroup).Select(g => new
                            {
                                value = g.Key,
                                elementIds = g.Select(x => x.e.Id.GetValue()).Take(50).ToList()
                            }).ToList()
                        });
                    }

                    foreach (var p in rule.Patterns ?? new List<ParameterPatternRule>())
                    {
                        var rx = new Regex(p.Regex, RegexOptions.CultureInvariant);
                        var bad = elements.Select(e => new { e, v = Value(ps, e, p.Parameter, out _) })
                            .Where(x => !string.IsNullOrWhiteSpace(x.v) && !rx.IsMatch(x.v)).ToList();
                        if (bad.Count == 0) continue;
                        violations += bad.Count;
                        groups.Add(new
                        {
                            rule = ri,
                            kind = "pattern",
                            category = cat.Name,
                            parameter = p.Parameter.ToString(),
                            regex = p.Regex,
                            count = bad.Count,
                            items = bad.Take(a.MaxItemsPerGroup).Select(x => new { id = x.e.Id.GetValue(), value = x.v }).ToList()
                        });
                    }
                }
            }

            return new
            {
                usedDefaultRules = a.Rules == null || a.Rules.Count == 0,
                scope = view == null ? "model" : $"view '{view.Name}'",
                checkedElements,
                violationCount = violations,
                ok = violations == 0,
                groups,
                next = violations == 0 ? null : "Fix values with set_parameters (by element id)."
            };
        }

        /// <summary>The parameter's value as text (instance first, then type); null when absent.</summary>
        private static string Value(ParameterService ps, Element e, ParameterRef r, out bool found)
        {
            found = false;
            Parameter p = null;
            try
            {
                p = ps.Find(e, r);
                if (p == null)
                {
                    var t = ParameterService.TypeOf(e);
                    if (t != null) p = ps.Find(t, r);
                }
            }
            catch (ToolException)
            {
                return null;
            }
            if (p == null) return null;
            found = true;
            if (!p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString();
                case StorageType.ElementId:
                    var id = p.AsElementId();
                    return id == null || id == ElementId.InvalidElementId ? null : ParameterService.SafeValueString(p) ?? id.GetValue().ToString();
                default:
                    return ParameterService.SafeValueString(p) ?? (p.StorageType == StorageType.Integer ? p.AsInteger().ToString() : p.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}
