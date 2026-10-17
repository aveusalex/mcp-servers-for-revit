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

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class PointMm
    {
        [JsonProperty("x", Required = Required.Always)]
        [Description("X coordinate (project internal origin).")]
        [Unit("mm")]
        public double X { get; set; }

        [JsonProperty("y", Required = Required.Always)]
        [Description("Y coordinate (project internal origin).")]
        [Unit("mm")]
        public double Y { get; set; }

        [JsonProperty("z", Required = Required.Always)]
        [Description("Z coordinate (project internal origin).")]
        [Unit("mm")]
        public double Z { get; set; }
    }

    public sealed class BoxMm
    {
        [JsonProperty("min", Required = Required.Always)]
        [Description("Lower corner.")]
        public PointMm Min { get; set; }

        [JsonProperty("max", Required = Required.Always)]
        [Description("Upper corner.")]
        public PointMm Max { get; set; }
    }

    public sealed class WhereCondition
    {
        [JsonProperty("parameter", Required = Required.Always)]
        [Description("Parameter reference: {guid} | {builtIn} | {name}.")]
        public ParameterRef Parameter { get; set; }

        [JsonProperty("op")]
        [Description("Operator (default eq). Text compares ignore case and accents; numbers compare in wire units (mm, m2, m3, deg, %).")]
        [EnumValues("eq", "neq", "contains", "not_contains", "starts_with", "ends_with", "regex", "gt", "gte", "lt", "lte", "is_empty", "not_empty")]
        [DefaultValue("eq")]
        public string Op { get; set; } = "eq";

        [JsonProperty("value")]
        [Description("Value to compare with (not needed for is_empty / not_empty).")]
        public JToken Value { get; set; }

        [JsonProperty("scope")]
        [Description("Where to read the parameter: auto (instance, then type; default), instance or type.")]
        [EnumValues("auto", "instance", "type")]
        [DefaultValue("auto")]
        public string Scope { get; set; } = "auto";
    }

    public sealed class FindElementsArgs
    {
        [JsonProperty("categories")]
        [Description("Categories, in any language: OST_Walls, Walls or Paredes. Omit for all model categories.")]
        public List<string> Categories { get; set; }

        [JsonProperty("familyName")]
        [Description("Family name (case and accents ignored).")]
        public string FamilyName { get; set; }

        [JsonProperty("typeName")]
        [Description("Type name, or \"Family: Type\" (case and accents ignored).")]
        public string TypeName { get; set; }

        [JsonProperty("level")]
        [Description("Level name or id: only elements associated with that level.")]
        public string Level { get; set; }

        [JsonProperty("workset")]
        [Description("Workset name or id (workshared models only).")]
        public string Workset { get; set; }

        [JsonProperty("view")]
        [Description("View name or id: only elements visible in that view.")]
        public string View { get; set; }

        [JsonProperty("bbox")]
        [Description("Only elements whose bounding box intersects this box, in mm.")]
        public BoxMm Bbox { get; set; }

        [JsonProperty("where")]
        [Description("Parameter conditions, all must match (AND).")]
        public List<WhereCondition> Where { get; set; }

        [JsonProperty("fields")]
        [Description("Parameters to return for each element (wire units), each {guid} | {builtIn} | {name}.")]
        public List<ParameterRef> Fields { get; set; }

        [JsonProperty("elementKind")]
        [Description("instances (default) or types.")]
        [EnumValues("instances", "types")]
        [DefaultValue("instances")]
        public string ElementKind { get; set; } = "instances";

        [JsonProperty("countOnly")]
        [Description("Return only the exact count (default false).")]
        [DefaultValue(false)]
        public bool CountOnly { get; set; }

        [JsonProperty("limit")]
        [Description("Page size, 1-5000 (default 100).")]
        [DefaultValue(100)]
        public int Limit { get; set; } = 100;

        [JsonProperty("offset")]
        [Description("Elements to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>CON-03: deterministic element query with where[] and paging.</summary>
    [McpTool("find_elements", Kind = ToolKind.Read, Toolset = "query", Idempotent = true,
        Description = "Find elements deterministically by category (any language: OST_Walls / Walls / Paredes), family, " +
                      "type, level, workset, view, bounding box in mm and parameter conditions where[] (eq, neq, " +
                      "contains, starts_with, ends_with, regex, gt/gte/lt/lte in wire units mm/m2/m3/deg, is_empty, " +
                      "not_empty), reading each parameter on the instance or its type. Returns ids, names and the " +
                      "requested fields, ordered by id, with an exact total and limit/offset paging (countOnly for just " +
                      "the count). Read-only.")]
    public sealed class FindElementsTool : McpTool<FindElementsArgs>
    {
        protected override void Validate(FindElementsArgs args)
        {
            var kind = (args.ElementKind ?? "instances").ToLowerInvariant();
            if (kind != "instances" && kind != "types")
                throw new ToolException("INVALID_ARGUMENTS", "elementKind must be instances or types.");
            args.ElementKind = kind;
            if (args.Limit < 1 || args.Limit > 5000)
                throw new ToolException("INVALID_ARGUMENTS", "limit must be between 1 and 5000.");
            if (args.Offset < 0)
                throw new ToolException("INVALID_ARGUMENTS", "offset must be 0 or more.");
            foreach (var w in args.Where ?? new List<WhereCondition>())
            {
                if (w?.Parameter == null || w.Parameter.IsEmpty)
                    throw new ToolException("INVALID_ARGUMENTS", "Every where[] condition needs a parameter.");
                w.Op = WhereEvaluator.NormalizeOperator(w.Op);
                WhereEvaluator.ValidateCondition(w.Op, w.Value);
                w.Scope = ParseScope(w.Scope);
            }
            if (args.Bbox != null && (args.Bbox.Min == null || args.Bbox.Max == null))
                throw new ToolException("INVALID_ARGUMENTS", "bbox needs min and max.");
            var hasFilter = (args.Categories?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(args.FamilyName) ||
                            !string.IsNullOrWhiteSpace(args.TypeName) || !string.IsNullOrWhiteSpace(args.Level) ||
                            !string.IsNullOrWhiteSpace(args.Workset) || !string.IsNullOrWhiteSpace(args.View) ||
                            args.Bbox != null || (args.Where?.Count ?? 0) > 0;
            if (!hasFilter)
                throw new ToolException("INVALID_ARGUMENTS", "Give at least one filter (categories, family/type, level, workset, view, bbox or where).",
                    "Start with categories, e.g. [\"OST_Walls\"].");
        }

        private static string ParseScope(string scope)
        {
            var s = (scope ?? "auto").Trim().ToLowerInvariant();
            if (s != "auto" && s != "instance" && s != "type")
                throw new ToolException("INVALID_ARGUMENTS", $"where[].scope must be auto, instance or type (got '{scope}').");
            return s;
        }

        protected override object Run(ToolContext ctx, FindElementsArgs args)
        {
            var doc = ctx.Doc;
            var services = ctx.Services;
            var ps = services.Parameters;

            FilteredElementCollector collector;
            if (!string.IsNullOrWhiteSpace(args.View))
                collector = new FilteredElementCollector(doc, services.Lookup.View(args.View).Id);
            else
                collector = new FilteredElementCollector(doc);
            collector = args.ElementKind == "types" ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType();

            var categories = services.Categories.ResolveAll(args.Categories);
            if (categories.Count > 0)
                collector = collector.WherePasses(new ElementMulticategoryFilter(categories.Select(c => c.Id).ToList()));

            if (!string.IsNullOrWhiteSpace(args.Level))
                collector = collector.WherePasses(new ElementLevelFilter(services.Lookup.Level(args.Level).Id));

            if (!string.IsNullOrWhiteSpace(args.Workset))
                collector = collector.WherePasses(new ElementWorksetFilter(services.Lookup.Workset(args.Workset).Id));

            if (args.Bbox != null)
            {
                var min = ToXyz(args.Bbox.Min);
                var max = ToXyz(args.Bbox.Max);
                var outline = new Outline(new XYZ(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z)),
                    new XYZ(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z)));
                collector = collector.WherePasses(new BoundingBoxIntersectsFilter(outline));
            }

            IEnumerable<Element> elements = collector.ToElements();
            if (categories.Count == 0) elements = elements.Where(e => e.Category != null);

            string familyKey = null, typeKey = null, typeFamilyKey = null;
            if (!string.IsNullOrWhiteSpace(args.FamilyName)) familyKey = TextMatch.Key(args.FamilyName);
            if (!string.IsNullOrWhiteSpace(args.TypeName))
            {
                FamilyTypeName.Parse(args.TypeName, out var fam, out var type);
                typeKey = TextMatch.Key(type);
                if (fam != null) typeFamilyKey = TextMatch.Key(fam);
            }
            if (familyKey != null || typeKey != null)
            {
                elements = elements.Where(e =>
                {
                    var t = ParameterService.TypeOf(e);
                    if (t == null) return false;
                    var famName = TextMatch.Key(LookupService.SafeFamilyName(t));
                    if (familyKey != null && famName != familyKey) return false;
                    if (typeFamilyKey != null && famName != typeFamilyKey) return false;
                    return typeKey == null || TextMatch.Key(t.Name) == typeKey;
                });
            }

            var where = args.Where ?? new List<WhereCondition>();
            if (where.Count > 0)
                elements = elements.Where(e => where.All(w =>
                    WhereEvaluator.Matches(ps.Snapshot(FindScoped(ps, e, w.Parameter, w.Scope)), w.Op, w.Value)));

            var matched = elements.OrderBy(e => e.Id.GetValue()).ToList();
            if (args.CountOnly)
                return new { total = matched.Count };

            var page = Paging.Page(matched, args.Limit, args.Offset);
            var items = page.Items.Select(e => Describe(doc, ps, e, args.Fields)).ToList();
            return new
            {
                items,
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }

        private static Parameter FindScoped(ParameterService ps, Element e, ParameterRef r, string scope)
        {
            if (scope != "type" && !(e is ElementType))
            {
                var p = ps.Find(e, r);
                if (p != null || scope == "instance") return p;
            }
            var t = e is ElementType ? e : ParameterService.TypeOf(e);
            return t == null ? null : ps.Find(t, r);
        }

        private static object Describe(Document doc, ParameterService ps, Element e, List<ParameterRef> fields)
        {
            var type = ParameterService.TypeOf(e);
            Dictionary<string, object> values = null;
            if (fields != null && fields.Count > 0)
            {
                values = new Dictionary<string, object>();
                foreach (var f in fields)
                {
                    var p = FindScoped(ps, e, f, "auto");
                    if (p == null)
                    {
                        values[f.ToString()] = null;
                        continue;
                    }
                    var v = ps.ReadWire(p, out var unit, out _);
                    values[f.ToString()] = unit == null ? v : new Dictionary<string, object> { ["value"] = v, ["unit"] = unit };
                }
            }

            var levelId = e.LevelId;
            return new
            {
                id = e.Id.GetValue(),
                uniqueId = e.UniqueId,
                name = SafeName(e),
                category = e.Category?.Name,
                categoryBuiltIn = CategoryService.BuiltInNameOf(e.Category),
                familyName = type == null ? null : LookupService.SafeFamilyName(type),
                typeName = e is ElementType ? null : type?.Name,
                typeId = e is ElementType ? (long?)null : type?.Id.GetValue(),
                level = levelId != null && levelId != ElementId.InvalidElementId ? doc.GetElement(levelId)?.Name : null,
                fields = values
            };
        }

        private static XYZ ToXyz(PointMm p) => new XYZ(UnitPolicy.ToInternal(p.X, UnitSpec.Length), UnitPolicy.ToInternal(p.Y, UnitSpec.Length), UnitPolicy.ToInternal(p.Z, UnitSpec.Length));

        private static string SafeName(Element e)
        {
            try { return e.Name; }
            catch { return null; }
        }
    }
}
