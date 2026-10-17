using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Query.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class ListSpatialElementsArgs
    {
        [JsonProperty("kind")]
        [Description("room (default), area or space (MEP).")]
        [EnumValues("room", "area", "space")]
        [DefaultValue("room")]
        public string Kind { get; set; } = "room";

        [JsonProperty("level")]
        [Description("Only on this level (name or id).")]
        public string Level { get; set; }

        [JsonProperty("phase")]
        [Description("Only rooms/spaces of this phase (name or id). Areas have no phase.")]
        public string Phase { get; set; }

        [JsonProperty("areaScheme")]
        [Description("Only areas of this area scheme (name or id), e.g. Gross Building / Rentable.")]
        public string AreaScheme { get; set; }

        [JsonProperty("status")]
        [Description("Only these statuses: placed, notPlaced, notEnclosed, redundant. Default: all.")]
        [EnumValues("placed", "notPlaced", "notEnclosed", "redundant")]
        public List<string> Status { get; set; }

        [JsonProperty("nameContains")]
        [Description("Only names containing this text (case and accents ignored).")]
        public string NameContains { get; set; }

        [JsonProperty("numberContains")]
        [Description("Only numbers containing this text.")]
        public string NumberContains { get; set; }

        [JsonProperty("fields")]
        [Description("Extra parameters to return for each item (wire units), each {guid} | {builtIn} | {name}.")]
        public List<ParameterRef> Fields { get; set; }

        [JsonProperty("limit")]
        [Description("Page size, 1-2000 (default 200).")]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Items to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>CON-12: rooms / areas / spaces with placement status.</summary>
    [McpTool("list_spatial_elements", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "List rooms, areas or MEP spaces with number, name, level, phase, area scheme, area (m2), " +
                      "perimeter (mm), volume (m3; 0 unless volume computation is on) and status: placed, notPlaced " +
                      "(deleted from plan but still in schedules), notEnclosed (placed but not bounded, area 0) or " +
                      "redundant (placed in a region another one already occupies). Filter by level, phase, area " +
                      "scheme, status, name/number text. Ordered by level then number, paged with total. Read-only.")]
    public sealed class ListSpatialElementsTool : McpTool<ListSpatialElementsArgs>
    {
        protected override void Validate(ListSpatialElementsArgs args)
        {
            args.Kind = (args.Kind ?? "room").Trim().ToLowerInvariant();
            if (args.Kind != "room" && args.Kind != "area" && args.Kind != "space")
                throw new ToolException("INVALID_ARGUMENTS", "kind must be room, area or space.");
            QueryHelpers.CheckPaging(args.Limit, args.Offset, 2000);
        }

        protected override object Run(ToolContext ctx, ListSpatialElementsArgs args)
        {
            var doc = ctx.Doc;
            var cat = args.Kind == "area" ? BuiltInCategory.OST_Areas
                : args.Kind == "space" ? BuiltInCategory.OST_MEPSpaces : BuiltInCategory.OST_Rooms;
            IEnumerable<SpatialElement> items = new FilteredElementCollector(doc).OfCategory(cat)
                .WhereElementIsNotElementType().OfType<SpatialElement>();

            if (!string.IsNullOrWhiteSpace(args.Level))
            {
                var level = ctx.Services.Lookup.Level(args.Level);
                items = items.Where(s => s.LevelId == level.Id);
            }
            if (!string.IsNullOrWhiteSpace(args.Phase))
            {
                if (args.Kind == "area")
                    throw new ToolException("INVALID_ARGUMENTS", "Areas have no phase; drop the phase filter.");
                var phase = ctx.Services.Lookup.Phase(args.Phase);
                items = items.Where(s => PhaseIdOf(s) == phase.Id);
            }
            if (!string.IsNullOrWhiteSpace(args.AreaScheme))
            {
                if (args.Kind != "area")
                    throw new ToolException("INVALID_ARGUMENTS", "areaScheme only applies to kind=area.");
                var schemes = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().ToList();
                var scheme = ctx.Services.Lookup.ByNameOrId(args.AreaScheme, schemes, "area scheme");
                items = items.Where(s => (s as Area)?.AreaScheme?.Id == scheme.Id);
            }
            if (!string.IsNullOrWhiteSpace(args.NameContains))
                items = items.Where(s => QueryHelpers.Contains(NameOf(s), args.NameContains));
            if (!string.IsNullOrWhiteSpace(args.NumberContains))
                items = items.Where(s => QueryHelpers.Contains(s.Number, args.NumberContains));

            var options = new SpatialElementBoundaryOptions();
            var withStatus = items.Select(s => (s, status: StatusOf(s, options))).ToList();
            var wanted = args.Status != null && args.Status.Count > 0 ? new HashSet<string>(args.Status) : null;
            var counts = withStatus.GroupBy(x => x.status).ToDictionary(g => g.Key, g => g.Count());
            if (wanted != null) withStatus = withStatus.Where(x => wanted.Contains(x.status)).ToList();

            var ordered = withStatus
                .OrderBy(x => LevelElevation(doc, x.s))
                .ThenBy(x => x.s.Number ?? string.Empty, new NaturalComparer())
                .ThenBy(x => x.s.Id.GetValue())
                .ToList();
            var page = Paging.Page(ordered, args.Limit, args.Offset, 2000);
            var computeVolumes = SafeComputeVolumes(doc);
            var ps = ctx.Services.Parameters;
            var list = page.Items.Select(x => Describe(doc, ps, x.s, x.status, args.Fields)).ToList();

            return new
            {
                kind = args.Kind,
                items = list,
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated,
                statusCounts = counts,
                volumesComputed = computeVolumes
            };
        }

        internal static string StatusOf(SpatialElement s, SpatialElementBoundaryOptions options)
        {
            if (s.Location == null) return "notPlaced";
            if (s.Area > 1e-9) return "placed";
            // Placed with zero area: no boundary at all = not enclosed; otherwise it shares a region (redundant).
            try
            {
                var segs = s.GetBoundarySegments(options);
                return segs == null || segs.Count == 0 ? "notEnclosed" : "redundant";
            }
            catch
            {
                return "notEnclosed";
            }
        }

        internal static ElementId PhaseIdOf(SpatialElement s)
        {
            try
            {
                var p = s.get_Parameter(BuiltInParameter.ROOM_PHASE);
                var id = p?.AsElementId();
                if (id != null && id != ElementId.InvalidElementId) return id;
            }
            catch
            {
                // fall through
            }
            return s.CreatedPhaseId;
        }

        internal static string NameOf(SpatialElement s)
        {
            try
            {
                var p = s.get_Parameter(BuiltInParameter.ROOM_NAME);
                if (p != null && p.HasValue) return p.AsString();
            }
            catch
            {
                // fall through
            }
            return QueryHelpers.SafeName(s);
        }

        private static object Describe(Document doc, ParameterService ps, SpatialElement s, string status, List<ParameterRef> fields)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = s.Id.GetValue(),
                ["uniqueId"] = s.UniqueId,
                ["number"] = s.Number,
                ["name"] = NameOf(s),
                ["status"] = status,
                ["level"] = s.Level?.Name,
                ["levelId"] = QueryHelpers.IdOrNull(s.LevelId),
                ["areaM2"] = QueryHelpers.M2(s.Area),
                ["perimeterMm"] = QueryHelpers.Mm(s.Perimeter),
            };
            if (s is Area area)
            {
                d["areaScheme"] = area.AreaScheme?.Name;
                d["isGrossInterior"] = area.IsGrossInterior;
            }
            else
            {
                var phaseId = PhaseIdOf(s);
                d["phase"] = phaseId == null ? null : QueryHelpers.SafeName(doc.GetElement(phaseId));
                double volume = 0;
                if (s is Room room) volume = room.Volume;
                else if (s is Space space) volume = space.Volume;
                d["volumeM3"] = QueryHelpers.M3(volume);
                if (s is Room r)
                {
                    d["upperLimit"] = r.UpperLimit?.Name;
                    d["unboundedHeightMm"] = QueryHelpers.Mm(r.UnboundedHeight);
                    d["department"] = SafeString(r, BuiltInParameter.ROOM_DEPARTMENT);
                }
            }
            if (s.Location is LocationPoint lp) d["point"] = QueryHelpers.Point(lp.Point);
            var f = QueryHelpers.Fields(ps, s, fields);
            if (f != null) d["fields"] = f;
            return d;
        }

        private static string SafeString(Element e, BuiltInParameter bip)
        {
            try { return e.get_Parameter(bip)?.AsString(); }
            catch { return null; }
        }

        private static double LevelElevation(Document doc, SpatialElement s)
        {
            try { return (doc.GetElement(s.LevelId) as Level)?.ProjectElevation ?? double.MaxValue; }
            catch { return double.MaxValue; }
        }

        private static bool? SafeComputeVolumes(Document doc)
        {
            try { return AreaVolumeSettings.GetAreaVolumeSettings(doc).ComputeVolumes; }
            catch { return null; }
        }
    }

    /// <summary>Orders "2" before "10" and "A2" before "A10".</summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        public int Compare(string a, string b)
        {
            a ??= string.Empty;
            b ??= string.Empty;
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = a.Substring(si, i - si).TrimStart('0');
                    var nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    var c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    var c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++;
                    j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
