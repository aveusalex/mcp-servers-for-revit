using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Query.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class GetRoomOpeningsArgs
    {
        [JsonProperty("roomIds")]
        [Description("Rooms by id. Combine with roomNumbers / level, or omit all three for every placed room.")]
        public List<long> RoomIds { get; set; }

        [JsonProperty("roomNumbers")]
        [Description("Rooms by number (exact, case ignored).")]
        public List<string> RoomNumbers { get; set; }

        [JsonProperty("level")]
        [Description("Only rooms on this level (name or id).")]
        public string Level { get; set; }

        [JsonProperty("categories")]
        [Description("doors, windows or both (default both).")]
        [EnumValues("doors", "windows")]
        public List<string> Categories { get; set; }

        [JsonProperty("phase")]
        [Description("Phase used for From/To Room and to decide which openings exist (name or id). Default: each room's own phase.")]
        public string Phase { get; set; }

        [JsonProperty("parameters")]
        [Description("Extra parameters to read on each opening (instance, then type), each {guid} | {builtIn} | {name}.")]
        public List<ParameterRef> Parameters { get; set; }

        [JsonProperty("includeOrientation")]
        [Description("Return facing/hand orientation vectors and flips (default true).")]
        [DefaultValue(true)]
        public bool IncludeOrientation { get; set; } = true;

        [JsonProperty("limit")]
        [Description("Rooms per page, 1-500 (default 50).")]
        [DefaultValue(50)]
        public int Limit { get; set; } = 50;

        [JsonProperty("offset")]
        [Description("Rooms to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>CON-14: doors and windows of each room, per phase, using From/To Room.</summary>
    [McpTool("get_room_openings", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "Doors and windows of each room, resolved per phase with Revit's From Room / To Room (not by " +
                      "geometry): for each opening its id, category, family/type, mark, width/height in mm, host wall, " +
                      "side (from/to), the room on the other side (null = exterior), phase status (New, Existing, " +
                      "Demolished, Temporary) and optional orientation and parameters. By default each room is " +
                      "evaluated in its own phase; pass phase to force one. Openings hosted in linked walls are not " +
                      "seen. Paged by room. Read-only.")]
    public sealed class GetRoomOpeningsTool : McpTool<GetRoomOpeningsArgs>
    {
        protected override void Validate(GetRoomOpeningsArgs args)
        {
            QueryHelpers.CheckPaging(args.Limit, args.Offset, 500);
        }

        protected override object Run(ToolContext ctx, GetRoomOpeningsArgs args)
        {
            var doc = ctx.Doc;
            IEnumerable<Room> rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().OfType<Room>().Where(r => r.Location != null && r.Area > 1e-9);

            var notFound = new List<object>();
            if ((args.RoomIds?.Count ?? 0) > 0 || (args.RoomNumbers?.Count ?? 0) > 0)
            {
                var ids = new HashSet<long>(args.RoomIds ?? new List<long>());
                var numbers = new HashSet<string>((args.RoomNumbers ?? new List<string>()).Select(n => TextMatch.Key(n)));
                var all = rooms.ToList();
                rooms = all.Where(r => ids.Contains(r.Id.GetValue()) || numbers.Contains(TextMatch.Key(r.Number))).ToList();
                foreach (var id in ids.Where(i => all.All(r => r.Id.GetValue() != i)))
                    notFound.Add(new { roomId = id, reason = "not a placed room" });
                foreach (var n in args.RoomNumbers ?? new List<string>())
                    if (all.All(r => TextMatch.Key(r.Number) != TextMatch.Key(n)))
                        notFound.Add(new { roomNumber = n, reason = "no placed room with this number" });
            }
            if (!string.IsNullOrWhiteSpace(args.Level))
            {
                var level = ctx.Services.Lookup.Level(args.Level);
                rooms = rooms.Where(r => r.LevelId == level.Id);
            }

            var ordered = rooms.OrderBy(r => r.Level?.ProjectElevation ?? 0).ThenBy(r => r.Number ?? "", new NaturalComparer()).ToList();
            var page = Paging.Page(ordered, args.Limit, args.Offset, 500);

            Phase forced = string.IsNullOrWhiteSpace(args.Phase) ? null : ctx.Services.Lookup.Phase(args.Phase);
            var cats = new List<BuiltInCategory>();
            var wanted = args.Categories == null || args.Categories.Count == 0 ? new List<string> { "doors", "windows" } : args.Categories;
            if (wanted.Contains("doors")) cats.Add(BuiltInCategory.OST_Doors);
            if (wanted.Contains("windows")) cats.Add(BuiltInCategory.OST_Windows);
            var openings = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats))
                .WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();

            // One pass per phase in play: room id -> (opening, side, other room).
            var byPhase = new Dictionary<long, Dictionary<long, List<(FamilyInstance fi, string side, Room other, string status)>>>();
            Dictionary<long, List<(FamilyInstance, string, Room, string)>> MapFor(Phase phase)
            {
                if (byPhase.TryGetValue(phase.Id.GetValue(), out var m)) return m;
                m = new Dictionary<long, List<(FamilyInstance, string, Room, string)>>();
                foreach (var fi in openings)
                {
                    var status = QueryHelpers.PhaseStatus(fi, phase.Id);
                    if (status == null || status == "None" || status == "Past" || status == "Future") continue;
                    Room from = null, to = null;
                    try { from = fi.get_FromRoom(phase); } catch { /* none */ }
                    try { to = fi.get_ToRoom(phase); } catch { /* none */ }
                    if (from != null) Add(m, from.Id.GetValue(), (fi, "from", to, status));
                    if (to != null && (from == null || to.Id != from.Id)) Add(m, to.Id.GetValue(), (fi, "to", from, status));
                }
                byPhase[phase.Id.GetValue()] = m;
                return m;
            }

            var ps = ctx.Services.Parameters;
            var items = new List<object>();
            foreach (var room in page.Items)
            {
                var phase = forced ?? doc.GetElement(ListSpatialElementsTool.PhaseIdOf(room)) as Phase ?? QueryHelpers.LastPhase(doc);
                var map = MapFor(phase);
                map.TryGetValue(room.Id.GetValue(), out var list);
                list ??= new List<(FamilyInstance, string, Room, string)>();
                items.Add(new
                {
                    roomId = room.Id.GetValue(),
                    number = room.Number,
                    name = ListSpatialElementsTool.NameOf(room),
                    level = room.Level?.Name,
                    phase = phase.Name,
                    phaseId = phase.Id.GetValue(),
                    openingCount = list.Count,
                    openings = list.OrderBy(o => o.Item1.Category?.Name).ThenBy(o => o.Item1.Id.GetValue())
                        .Select(o => Describe(doc, ps, o.Item1, o.Item2, o.Item3, o.Item4, args)).ToList()
                });
            }

            return new
            {
                items,
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated,
                notFound
            };
        }

        private static void Add(Dictionary<long, List<(FamilyInstance, string, Room, string)>> m, long key,
            (FamilyInstance, string, Room, string) v)
        {
            if (!m.TryGetValue(key, out var l)) m[key] = l = new List<(FamilyInstance, string, Room, string)>();
            l.Add(v);
        }

        private static object Describe(Document doc, ParameterService ps, FamilyInstance fi, string side, Room other,
            string status, GetRoomOpeningsArgs args)
        {
            var isDoor = fi.Category?.Id.GetValue() == (long)BuiltInCategory.OST_Doors;
            var d = new Dictionary<string, object>
            {
                ["id"] = fi.Id.GetValue(),
                ["category"] = isDoor ? "door" : "window",
                ["familyName"] = fi.Symbol?.FamilyName,
                ["typeName"] = fi.Symbol?.Name,
                ["mark"] = ReadString(fi, BuiltInParameter.ALL_MODEL_MARK),
                ["widthMm"] = ReadLength(fi, isDoor ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM),
                ["heightMm"] = ReadLength(fi, isDoor ? BuiltInParameter.DOOR_HEIGHT : BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM),
                ["sillHeightMm"] = isDoor ? null : ReadLength(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM),
                ["hostId"] = fi.Host?.Id.GetValue(),
                ["side"] = side,
                ["otherRoom"] = other == null ? null : new { id = other.Id.GetValue(), number = other.Number, name = ListSpatialElementsTool.NameOf(other) },
                ["isExterior"] = other == null,
                ["phaseStatus"] = status
            };
            if (args.IncludeOrientation)
            {
                d["facingOrientation"] = QueryHelpers.Vector(fi.FacingOrientation);
                d["handOrientation"] = QueryHelpers.Vector(fi.HandOrientation);
                d["facingFlipped"] = fi.FacingFlipped;
                d["handFlipped"] = fi.HandFlipped;
            }
            var f = QueryHelpers.Fields(ps, fi, args.Parameters);
            if (f != null) d["parameters"] = f;
            return d;
        }

        private static string ReadString(Element e, BuiltInParameter bip)
        {
            try { return e.get_Parameter(bip)?.AsString(); }
            catch { return null; }
        }

        private static object ReadLength(FamilyInstance fi, params BuiltInParameter[] bips)
        {
            foreach (var bip in bips)
            {
                foreach (Element owner in new Element[] { fi, fi.Symbol })
                {
                    if (owner == null) continue;
                    try
                    {
                        var p = owner.get_Parameter(bip);
                        if (p != null && p.HasValue && p.StorageType == StorageType.Double) return QueryHelpers.Mm(p.AsDouble());
                    }
                    catch
                    {
                        // try the next one
                    }
                }
            }
            return null;
        }
    }
}
