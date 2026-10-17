using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Graphics.Support
{
    public sealed class FilterRuleSpec
    {
        [JsonProperty("parameter", Required = Required.Always)]
        [Description("Parameter common to every category of the filter: {builtIn} (language-neutral), {guid} (shared) or {name} as Revit shows it.")]
        public ParameterRef Parameter { get; set; }

        [JsonProperty("op", Required = Required.Always)]
        [Description("Operator. Numbers are in wire units (mm, m2, m3, deg); text compares ignore case.")]
        [EnumValues("eq", "neq", "gt", "gte", "lt", "lte", "contains", "not_contains", "begins_with", "not_begins_with",
            "ends_with", "not_ends_with", "has_value", "has_no_value")]
        public string Op { get; set; }

        [JsonProperty("value")]
        [Description("Value to compare with (not for has_value/has_no_value): number in wire units, text, true/false, or an element name/id for element parameters (level, material, phase).")]
        public JToken Value { get; set; }
    }

    /// <summary>Builds rule-based ElementFilters for ParameterFilterElement (view filters).</summary>
    public static class FilterRules
    {
        public static void ValidateSpecs(IList<FilterRuleSpec> rules)
        {
            foreach (var r in rules ?? new List<FilterRuleSpec>())
            {
                if (r == null || r.Parameter == null || r.Parameter.IsEmpty)
                    throw new ToolException("INVALID_ARGUMENTS", "Every rule needs a parameter.");
                var op = (r.Op ?? "").Trim().ToLowerInvariant();
                r.Op = op;
                var needsValue = op != "has_value" && op != "has_no_value";
                if (needsValue && (r.Value == null || r.Value.Type == JTokenType.Null))
                    throw new ToolException("INVALID_ARGUMENTS", $"Rule on '{r.Parameter}' with op {op} needs a value.");
            }
        }

        /// <summary>The filter for the rules, combined with AND or OR; null when there are no rules.</summary>
        public static ElementFilter Build(ToolContext ctx, ICollection<ElementId> categoryIds, IList<FilterRuleSpec> rules, bool or)
        {
            if (rules == null || rules.Count == 0) return null;
            var filterable = ParameterFilterUtilities.GetFilterableParametersInCommon(ctx.Doc, categoryIds);
            var filters = new List<ElementFilter>();
            foreach (var spec in rules)
            {
                var paramId = ResolveParameter(ctx.Doc, filterable, spec.Parameter);
                filters.Add(new ElementParameterFilter(BuildRule(ctx, categoryIds, paramId, spec)));
            }
            if (filters.Count == 1) return filters[0];
            return or ? (ElementFilter)new LogicalOrFilter(filters) : new LogicalAndFilter(filters);
        }

        public static string ParameterLabel(Document doc, ElementId id)
        {
            var v = id.GetValue();
            if (v < 0 && v >= int.MinValue)
            {
                try { return LabelUtils.GetLabelFor((BuiltInParameter)(int)v); }
                catch { return ((BuiltInParameter)(int)v).ToString(); }
            }
            return doc.GetElement(id)?.Name ?? v.ToString();
        }

        private static ElementId ResolveParameter(Document doc, ICollection<ElementId> filterable, ParameterRef r)
        {
            ElementId id = null;
            if (!string.IsNullOrWhiteSpace(r.Guid))
            {
                var spe = SharedParameterElement.Lookup(doc, r.ParsedGuid().Value);
                if (spe == null) throw new ToolException("PARAMETER_NOT_FOUND", $"No shared parameter {r.Guid} in this document.");
                id = spe.Id;
            }
            else if (!string.IsNullOrWhiteSpace(r.BuiltIn))
            {
                if (!Enum.TryParse(r.BuiltIn.Trim(), true, out BuiltInParameter bip))
                    throw new ToolException("INVALID_ARGUMENTS", $"'{r.BuiltIn}' is not a BuiltInParameter name.");
                id = new ElementId(bip);
            }
            if (id != null)
            {
                if (!filterable.Contains(id))
                    throw new ToolException("PARAMETER_NOT_FILTERABLE",
                        $"'{r}' cannot be used by a filter on these categories (it is not common to all of them).",
                        "Use fewer categories, or another parameter.");
                return id;
            }
            var candidates = filterable.Select(f => new NamedCandidate<ElementId>(ParameterLabel(doc, f), f,
                SchedulesBuiltIn(f) ?? "id " + f.GetValue())).ToList();
            try
            {
                return NameMatcher.Single(r.Name, candidates, "filterable parameter");
            }
            catch (ToolException ex) when (ex.Code == "NOT_FOUND")
            {
                throw new ToolException("PARAMETER_NOT_FILTERABLE",
                    $"'{r.Name}' is not a parameter common to all the filter's categories. {ex.Hint}",
                    "Use fewer categories, or check the name (Revit's language) / pass builtIn.");
            }
        }

        private static string SchedulesBuiltIn(ElementId id)
        {
            var v = id.GetValue();
            if (v >= 0 || v < int.MinValue) return null;
            var bip = (BuiltInParameter)(int)v;
            return Enum.IsDefined(typeof(BuiltInParameter), bip) ? bip.ToString() : null;
        }

        /// <summary>Storage type and spec of a parameter, read from an element of the categories (or its definition).</summary>
        private static (StorageType storage, ForgeTypeId spec) Probe(Document doc, ICollection<ElementId> categoryIds, ElementId paramId)
        {
            var elements = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categoryIds))
                .WhereElementIsNotElementType().Take(300).ToList();
            foreach (var e in elements)
            {
                var p = FindById(e, paramId) ?? (doc.GetElement(e.GetTypeId()) is Element t ? FindById(t, paramId) : null);
                if (p != null) return (p.StorageType, UnitsService.SpecOf(p));
            }
            if (doc.GetElement(paramId) is ParameterElement pe)
            {
                var spec = pe.GetDefinition()?.GetDataType();
                var id = spec?.TypeId ?? "";
                if (id.StartsWith("autodesk.spec:spec.string", StringComparison.Ordinal)) return (StorageType.String, spec);
                if (id.StartsWith("autodesk.spec:spec.int64", StringComparison.Ordinal) || id.StartsWith("autodesk.spec:spec.bool", StringComparison.Ordinal))
                    return (StorageType.Integer, spec);
                if (id.Length > 0) return (StorageType.Double, spec);
            }
            return (StorageType.None, null);
        }

        private static Parameter FindById(Element e, ElementId paramId)
        {
            var v = paramId.GetValue();
            if (v < 0 && v >= int.MinValue) return e.get_Parameter((BuiltInParameter)(int)v);
            foreach (Parameter p in e.Parameters)
                if (p.Id == paramId) return p;
            return null;
        }

        private static FilterRule BuildRule(ToolContext ctx, ICollection<ElementId> categoryIds, ElementId pid, FilterRuleSpec spec)
        {
            var op = spec.Op;
            if (op == "has_value") return ParameterFilterRuleFactory.CreateHasValueParameterRule(pid);
            if (op == "has_no_value") return ParameterFilterRuleFactory.CreateHasNoValueParameterRule(pid);

            var (storage, specType) = Probe(ctx.Doc, categoryIds, pid);
            var value = spec.Value;
            if (storage == StorageType.None)
            {
                // No element to learn the type from: infer from the value, only when it is unambiguous.
                if (value.Type == JTokenType.String) storage = StorageType.String;
                else if (value.Type == JTokenType.Boolean || value.Type == JTokenType.Integer) storage = StorageType.Integer;
                else
                    throw new ToolException("PARAMETER_TYPE_UNKNOWN",
                        $"Cannot tell the type/unit of '{spec.Parameter}' (no element of these categories has it).",
                        "Create the filter once elements exist, or use a text value.");
            }

            switch (storage)
            {
                case StorageType.String:
                {
                    var text = value.Type == JTokenType.String ? (string)value : value.ToString(Formatting.None);
                    return StringRule(pid, op, text);
                }
                case StorageType.Integer:
                {
                    int n;
                    if (value.Type == JTokenType.Boolean) n = value.Value<bool>() ? 1 : 0;
                    else if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float) n = (int)Math.Round(value.Value<double>());
                    else if (value.Type == JTokenType.String && bool.TryParse((string)value, out var b)) n = b ? 1 : 0;
                    else if (value.Type == JTokenType.String && int.TryParse((string)value, out var i)) n = i;
                    else throw new ToolException("INVALID_ARGUMENTS", $"'{spec.Parameter}' is an integer/yes-no parameter; '{value}' is not.");
                    return IntRule(pid, op, n);
                }
                case StorageType.Double:
                {
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                        throw new ToolException("INVALID_ARGUMENTS", $"'{spec.Parameter}' is numeric; give a number in wire units (mm, m2, m3, deg).");
                    var kind = ctx.Services.Units.Classify(specType);
                    if (kind == UnitSpec.OtherMeasurable)
                        throw new ToolException("UNIT_UNSUPPORTED", $"'{spec.Parameter}' uses {specType?.TypeId}, which has no wire-unit conversion.");
                    var raw = UnitPolicy.ToInternal(value.Value<double>(), kind);
                    var eps = kind == UnitSpec.Dimensionless ? 1e-6 : Math.Abs(UnitPolicy.ToInternal(0.001, kind));
                    return DoubleRule(pid, op, raw, eps);
                }
                case StorageType.ElementId:
                {
                    var target = ResolveElementValue(ctx, value);
                    switch (op)
                    {
                        case "eq": return ParameterFilterRuleFactory.CreateEqualsRule(pid, target);
                        case "neq": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, target);
                        default: throw new ToolException("INVALID_ARGUMENTS", $"'{spec.Parameter}' references an element; only eq/neq/has_value apply.");
                    }
                }
                default:
                    throw new ToolException("PARAMETER_TYPE_UNKNOWN", $"'{spec.Parameter}' cannot be filtered.");
            }
        }

        private static ElementId ResolveElementValue(ToolContext ctx, JToken value)
        {
            if (value.Type == JTokenType.Integer) return value.Value<long>().ToRevitElementId();
            var text = (string)value;
            foreach (var t in new[] { typeof(Level), typeof(Material), typeof(Phase) })
            {
                var hits = new FilteredElementCollector(ctx.Doc).OfClass(t)
                    .Where(e => TextMatch.Key(e.Name) == TextMatch.Key(text)).ToList();
                if (hits.Count == 1) return hits[0].Id;
            }
            if (long.TryParse(text, out var id)) return id.ToRevitElementId();
            throw new ToolException("NOT_FOUND", $"No level, material or phase named '{text}'.", "Pass the element id.");
        }

        private static FilterRule StringRule(ElementId pid, string op, string text)
        {
#if REVIT2023_OR_GREATER
            switch (op)
            {
                case "eq": return ParameterFilterRuleFactory.CreateEqualsRule(pid, text);
                case "neq": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, text);
                case "gt": return ParameterFilterRuleFactory.CreateGreaterRule(pid, text);
                case "gte": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, text);
                case "lt": return ParameterFilterRuleFactory.CreateLessRule(pid, text);
                case "lte": return ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, text);
                case "contains": return ParameterFilterRuleFactory.CreateContainsRule(pid, text);
                case "not_contains": return ParameterFilterRuleFactory.CreateNotContainsRule(pid, text);
                case "begins_with": return ParameterFilterRuleFactory.CreateBeginsWithRule(pid, text);
                case "not_begins_with": return ParameterFilterRuleFactory.CreateNotBeginsWithRule(pid, text);
                case "ends_with": return ParameterFilterRuleFactory.CreateEndsWithRule(pid, text);
                case "not_ends_with": return ParameterFilterRuleFactory.CreateNotEndsWithRule(pid, text);
            }
#else
            switch (op)
            {
                case "eq": return ParameterFilterRuleFactory.CreateEqualsRule(pid, text, false);
                case "neq": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, text, false);
                case "gt": return ParameterFilterRuleFactory.CreateGreaterRule(pid, text, false);
                case "gte": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, text, false);
                case "lt": return ParameterFilterRuleFactory.CreateLessRule(pid, text, false);
                case "lte": return ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, text, false);
                case "contains": return ParameterFilterRuleFactory.CreateContainsRule(pid, text, false);
                case "not_contains": return ParameterFilterRuleFactory.CreateNotContainsRule(pid, text, false);
                case "begins_with": return ParameterFilterRuleFactory.CreateBeginsWithRule(pid, text, false);
                case "not_begins_with": return ParameterFilterRuleFactory.CreateNotBeginsWithRule(pid, text, false);
                case "ends_with": return ParameterFilterRuleFactory.CreateEndsWithRule(pid, text, false);
                case "not_ends_with": return ParameterFilterRuleFactory.CreateNotEndsWithRule(pid, text, false);
            }
#endif
            throw new ToolException("INVALID_ARGUMENTS", $"Unknown op '{op}'.");
        }

        private static FilterRule IntRule(ElementId pid, string op, int n)
        {
            switch (op)
            {
                case "eq": return ParameterFilterRuleFactory.CreateEqualsRule(pid, n);
                case "neq": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, n);
                case "gt": return ParameterFilterRuleFactory.CreateGreaterRule(pid, n);
                case "gte": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, n);
                case "lt": return ParameterFilterRuleFactory.CreateLessRule(pid, n);
                case "lte": return ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, n);
                default: throw new ToolException("INVALID_ARGUMENTS", $"op {op} does not apply to an integer/yes-no parameter.");
            }
        }

        private static FilterRule DoubleRule(ElementId pid, string op, double v, double eps)
        {
            switch (op)
            {
                case "eq": return ParameterFilterRuleFactory.CreateEqualsRule(pid, v, eps);
                case "neq": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, v, eps);
                case "gt": return ParameterFilterRuleFactory.CreateGreaterRule(pid, v, eps);
                case "gte": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, v, eps);
                case "lt": return ParameterFilterRuleFactory.CreateLessRule(pid, v, eps);
                case "lte": return ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, v, eps);
                default: throw new ToolException("INVALID_ARGUMENTS", $"op {op} does not apply to a numeric parameter.");
            }
        }
    }
}
