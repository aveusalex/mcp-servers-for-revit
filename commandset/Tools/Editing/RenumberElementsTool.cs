using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Tools.Parameters.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class RenumberElementsArgs
    {
        [JsonProperty("elementIds")]
        [MaxLength(5000)]
        [Description("Elements to renumber. Alternative to categories / useSelection.")]
        public List<string> ElementIds { get; set; }

        [JsonProperty("categories")]
        [Description("Categories in any language (OST_Rooms, Doors, Janelas...). Narrow with view and level.")]
        public List<string> Categories { get; set; }

        [JsonProperty("view")]
        [Description("Only elements visible in this view (name or id).")]
        public string View { get; set; }

        [JsonProperty("level")]
        [Description("Only elements on this level (name or id).")]
        public string Level { get; set; }

        [JsonProperty("useSelection")]
        [Description("Renumber Revit's current selection (target must be the active document). Default false.")]
        [DefaultValue(false)]
        public bool UseSelection { get; set; }

        [JsonProperty("parameter")]
        [Description("Text parameter that holds the number. Default: Number for rooms/spaces, Mark for everything else.")]
        public ParameterRef Parameter { get; set; }

        [JsonProperty("start")]
        [Description("First number (default 1).")]
        [DefaultValue(1)]
        public int Start { get; set; } = 1;

        [JsonProperty("increment")]
        [Description("Step between numbers (default 1).")]
        [DefaultValue(1)]
        public int Increment { get; set; } = 1;

        [JsonProperty("padding")]
        [Description("Minimum digits, zero-padded (3 → 001). Default 0 (no padding).")]
        [Range(0, 10)]
        [DefaultValue(0)]
        public int Padding { get; set; }

        [JsonProperty("prefix")]
        [Description("Text before the number; {level} is replaced by the element's level name (e.g. \"P-{level}-\").")]
        public string Prefix { get; set; }

        [JsonProperty("suffix")]
        [Description("Text after the number.")]
        public string Suffix { get; set; }

        [JsonProperty("sortBy")]
        [Description("Order: yThenX (rows top to bottom, left to right; default), xThenY (columns left to right, top to bottom), current (natural order of the current values), id.")]
        [EnumValues("yThenX", "xThenY", "current", "id")]
        [DefaultValue("yThenX")]
        public string SortBy { get; set; } = "yThenX";

        [JsonProperty("rowTolerance")]
        [Description("Elements whose Y (or X for xThenY) differ by less than this are in the same row (default 500).")]
        [Unit("mm")]
        [DefaultValue(500)]
        public double RowTolerance { get; set; } = 500;

        [JsonProperty("perLevel")]
        [Description("Restart numbering on each level (levels by elevation). Default false.")]
        [DefaultValue(false)]
        public bool PerLevel { get; set; }

        [JsonProperty("allowDuplicates")]
        [Description("Allow the new numbers to repeat (e.g. perLevel without {level} in the prefix). Default false.")]
        [DefaultValue(false)]
        public bool AllowDuplicates { get; set; }
    }

    /// <summary>EDT-06: renumber by position or order, in two passes (temporary unique values, then the final ones).</summary>
    [McpTool("renumber_elements", Kind = ToolKind.Write, Risk = Risk.Medium, DryRunDefault = true, Toolset = "editing",
        Description = "Renumber rooms, doors, windows, parking spaces or any element with a text number/Mark: by " +
                      "position (rows top-to-bottom then left-to-right, or columns), by current value or by id; start, " +
                      "increment, zero padding, prefix/suffix ({level} = level name) and optional restart per level. " +
                      "Writes in two passes (temporary unique values first) so swapping numbers never collides; warns " +
                      "when a new number already exists on another element of the category. Preview with dryRun:true " +
                      "(no approval), then run (needs approval). Undo: Ctrl+Z or send revert[] to set_parameters.")]
    public sealed class RenumberElementsTool : McpTool<RenumberElementsArgs>
    {
        protected override void Validate(RenumberElementsArgs a)
        {
            if ((a.ElementIds?.Count ?? 0) == 0 && (a.Categories?.Count ?? 0) == 0 && !a.UseSelection)
                throw new ToolException("INVALID_ARGUMENTS", "Say which elements: elementIds, categories or useSelection.");
            if (a.Increment == 0) throw new ToolException("INVALID_ARGUMENTS", "increment must not be 0.");
            if (a.RowTolerance < 0) throw new ToolException("INVALID_ARGUMENTS", "rowTolerance must be 0 or more.");
        }

        protected override object Run(ToolContext ctx, RenumberElementsArgs a)
        {
            var doc = ctx.Doc;
            var ps = ctx.Services.Parameters;
            var elements = ElementScope.Collect(ctx, new ElementScopeRequest
            {
                ElementIds = a.ElementIds, Categories = a.Categories, View = a.View, UseSelection = a.UseSelection
            });
            if (!string.IsNullOrWhiteSpace(a.Level))
            {
                var level = ctx.Services.Lookup.Level(a.Level);
                elements = elements.Where(e => e.LevelId == level.Id).ToList();
            }
            if (elements.Count == 0)
                throw new ToolException("NOTHING_TO_RENUMBER", "No element matches.", "Check the scope with find_elements.", "find_elements");

            // Parameter per element.
            var items = new List<Item>();
            foreach (var e in elements)
            {
                var r = a.Parameter ?? DefaultParameter(e);
                var p = ps.Find(e, r);
                if (p == null)
                    throw new ToolException("PARAMETER_NOT_FOUND", $"Element {e.Id.GetValue()} has no instance parameter '{r}'.",
                        "Pass parameter explicitly (e.g. {builtIn:\"ALL_MODEL_MARK\"}).", "get_element_parameters");
                if (p.StorageType != StorageType.String)
                    throw new ToolException("NOT_TEXT_PARAMETER", $"'{p.Definition?.Name}' is not a text parameter.");
                if (p.IsReadOnly)
                    throw new ToolException("READ_ONLY", $"'{p.Definition?.Name}' is read-only on element {e.Id.GetValue()}.");
                items.Add(new Item { E = e, P = p, Current = p.AsString() ?? string.Empty, At = EditGeometry.Anchor(e), Level = doc.GetElement(e.LevelId) as Level });
            }

            var groups = a.PerLevel
                ? items.GroupBy(i => i.Level?.Id.GetValue() ?? long.MinValue).OrderBy(g => g.First().Level?.Elevation ?? double.MinValue).Select(g => g.ToList()).ToList()
                : new List<List<Item>> { items };

            var tol = EditGeometry.Ft(a.RowTolerance);
            var assignments = new List<(Element e, Parameter p, string old, string next)>();
            foreach (var group in groups)
            {
                int n = a.Start;
                foreach (var i in Order(group, a.SortBy, tol))
                {
                    var number = Math.Abs(n).ToString(CultureInfo.InvariantCulture).PadLeft(a.Padding, '0');
                    if (n < 0) number = "-" + number;
                    var prefix = (a.Prefix ?? string.Empty).Replace("{level}", i.Level?.Name ?? string.Empty);
                    var suffix = (a.Suffix ?? string.Empty).Replace("{level}", i.Level?.Name ?? string.Empty);
                    assignments.Add((i.E, i.P, i.Current, prefix + number + suffix));
                    n += a.Increment;
                }
            }

            var dup = assignments.GroupBy(x => x.next).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dup.Count > 0 && !a.AllowDuplicates)
                throw new ToolException("DUPLICATE_NUMBERS", $"The new numbers repeat: {string.Join(", ", dup.Take(10))}.",
                    "Add {level} to the prefix, or pass allowDuplicates:true.");

            // Values already used by other elements of the same categories (would become duplicates).
            var ids = new HashSet<long>(assignments.Select(x => x.e.Id.GetValue()));
            var newValues = new HashSet<string>(assignments.Select(x => x.next), StringComparer.Ordinal);
            var clashes = new List<(long id, string value)>();
            foreach (var catId in assignments.Select(x => x.e.Category?.Id).Where(c => c != null).GroupBy(c => c.GetValue()).Select(g => g.First()))
            {
                foreach (var other in new FilteredElementCollector(doc).OfCategoryId(catId).WhereElementIsNotElementType())
                {
                    if (ids.Contains(other.Id.GetValue())) continue;
                    var op = ps.Find(other, ParameterService.Identify(assignments.First(x => x.e.Category?.Id == catId).p).ToRef());
                    var v = op?.StorageType == StorageType.String ? op.AsString() : null;
                    if (v != null && newValues.Contains(v)) clashes.Add((other.Id.GetValue(), v));
                }
            }
            if (clashes.Count > 0)
                ctx.Warnings.Add(new ToolWarning($"{clashes.Count} element(s) outside the set already use one of the new numbers.",
                    clashes.Take(50).Select(c => c.id).ToList()));

            // Plan first (captures the old values), then the temporary pass, then the final values.
            var plan = ps.Plan(assignments.Select(x => new ParameterWriteRequest
            {
                Element = x.e.Id.GetValue().ToString(CultureInfo.InvariantCulture),
                Parameter = ParameterService.Identify(x.p).ToRef(),
                Value = x.next
            }).ToList(), new ParameterPlanOptions { Target = ParameterTarget.Instance, AllowCritical = a.AllowDuplicates, MaxItems = 5000 });
            if (plan.HasErrors)
                throw new ToolException("PARAMETER_WRITE_REFUSED", $"Nothing was renumbered. {plan.FailureSummary()}");

            foreach (var x in assignments.Where(x => x.old != x.next))
                if (!x.p.Set("~mcp~" + x.e.Id.GetValue().ToString(CultureInfo.InvariantCulture)))
                    throw new ToolException("SET_FAILED", $"Revit refused a temporary value on element {x.e.Id.GetValue()}.");

            var result = ctx.Services.Parameters.Apply(plan);
            if (result.Failed > 0)
                throw new ToolException("PARAMETER_WRITE_FAILED", "Nothing was renumbered: " +
                    string.Join(" ", result.Items.Where(i => i.Status != "ok" && i.Status != "duplicate").Take(10)
                        .Select(i => $"[element {i.ElementId}] {i.Status}: {i.Message}")));

            return new
            {
                renumbered = assignments.Count(x => x.old != x.next),
                unchanged = assignments.Count(x => x.old == x.next),
                items = assignments.Select(x => new { elementId = x.e.Id.GetValue(), before = x.old, after = x.next }),
                clashesOutsideSet = clashes.Take(50).Select(c => new { elementId = c.id, value = c.value }),
                revert = result.Revert,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"{assignments.Count(x => x.old != x.next)} element(s) renumbered."
            };
        }

        private static ParameterRef DefaultParameter(Element e)
        {
            var bic = CategoryService.BuiltInNameOf(e.Category);
            return bic == "OST_Rooms" || bic == "OST_MEPSpaces" || bic == "OST_Areas"
                ? new ParameterRef { BuiltIn = "ROOM_NUMBER" }
                : new ParameterRef { BuiltIn = "ALL_MODEL_MARK" };
        }

        private sealed class Item
        {
            public Element E;
            public Parameter P;
            public string Current;
            public XYZ At;
            public Level Level;
        }

        private static IEnumerable<Item> Order(List<Item> group, string sortBy, double tol)
        {
            switch (sortBy)
            {
                case "id":
                    return group.OrderBy(i => i.E.Id.GetValue());
                case "current":
                    return group.OrderBy(i => i.Current, NaturalComparer.Instance).ThenBy(i => i.E.Id.GetValue());
                default:
                    bool rowsByY = sortBy != "xThenY";
                    Func<Item, double> major = i => i.At == null ? 0 : rowsByY ? -i.At.Y : i.At.X;
                    Func<Item, double> minor = i => i.At == null ? 0 : rowsByY ? i.At.X : -i.At.Y;
                    var sorted = group.OrderBy(major).ThenBy(minor).ToList();
                    var result = new List<Item>();
                    var row = new List<Item>();
                    double rowKey = double.NaN;
                    foreach (var i in sorted)
                    {
                        var k = major(i);
                        if (row.Count > 0 && Math.Abs(k - rowKey) > tol)
                        {
                            result.AddRange(row.OrderBy(minor));
                            row.Clear();
                        }
                        if (row.Count == 0) rowKey = k;
                        row.Add(i);
                    }
                    result.AddRange(row.OrderBy(minor));
                    return result;
            }
        }
    }

    /// <summary>"2" before "10", "A2" before "A10"; case-insensitive.</summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();
        private static readonly Regex Chunks = new Regex(@"\d+|\D+", RegexOptions.CultureInvariant);

        public int Compare(string x, string y)
        {
            var a = Chunks.Matches(x ?? string.Empty);
            var b = Chunks.Matches(y ?? string.Empty);
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                var sa = a[i].Value;
                var sb = b[i].Value;
                int c;
                if (char.IsDigit(sa[0]) && char.IsDigit(sb[0]))
                {
                    c = sa.TrimStart('0').Length.CompareTo(sb.TrimStart('0').Length);
                    if (c == 0) c = string.CompareOrdinal(sa.TrimStart('0'), sb.TrimStart('0'));
                }
                else c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
            return a.Count.CompareTo(b.Count);
        }
    }
}
