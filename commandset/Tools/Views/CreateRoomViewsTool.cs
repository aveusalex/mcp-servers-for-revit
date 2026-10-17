using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Views
{
    public sealed class CreateRoomViewsArgs
    {
        [JsonProperty("rooms")]
        [Description("Room ids (strings). Alternatively use level or all.")]
        public List<string> Rooms { get; set; }

        [JsonProperty("level")]
        [Description("Every placed room of this level (name or id).")]
        public string Level { get; set; }

        [JsonProperty("all")]
        [Description("Every placed room of the document (default false).")]
        [DefaultValue(false)]
        public bool All { get; set; }

        [JsonProperty("kinds")]
        [Description("What to create per room: plan (floor plan cropped to the room) and/or elevations (interior elevations). Default both.")]
        [EnumValues("plan", "elevations")]
        public List<string> Kinds { get; set; }

        [JsonProperty("directions")]
        [Description("Interior elevations by the direction the camera looks to (north = +Y, east = +X). Default all four.")]
        [EnumValues("north", "south", "east", "west")]
        public List<string> Directions { get; set; }

        [JsonProperty("padding")]
        [Description("Margin around the room for crops, in mm (default 300).")]
        [Unit("mm")]
        [DefaultValue(300.0)]
        public double Padding { get; set; } = 300;

        [JsonProperty("planScale")]
        [Description("Scale denominator of the room plans (default 50 = 1:50).")]
        [Range(1, 24000)]
        [DefaultValue(50)]
        public int PlanScale { get; set; } = 50;

        [JsonProperty("elevationScale")]
        [Description("Scale denominator of the elevations (default 50 = 1:50).")]
        [Range(1, 24000)]
        [DefaultValue(50)]
        public int ElevationScale { get; set; } = 50;

        [JsonProperty("planTemplate")]
        [Description("View template name or id for the room plans.")]
        public string PlanTemplate { get; set; }

        [JsonProperty("elevationTemplate")]
        [Description("View template name or id for the elevations.")]
        public string ElevationTemplate { get; set; }

        [JsonProperty("planViewFamilyType")]
        [Description("Floor plan view type name or id. Omit for the default floor plan type.")]
        public string PlanViewFamilyType { get; set; }

        [JsonProperty("elevationViewFamilyType")]
        [Description("Elevation view type name or id (e.g. an interior elevation type). Omit for the default.")]
        public string ElevationViewFamilyType { get; set; }

        [JsonProperty("planNamePattern")]
        [Description("Plan name pattern; tokens {number}, {name}, {level}. Default \"{number} - {name}\". Collisions get \" (2)\".")]
        [DefaultValue("{number} - {name}")]
        public string PlanNamePattern { get; set; } = "{number} - {name}";

        [JsonProperty("elevationNamePattern")]
        [Description("Elevation name pattern; tokens {number}, {name}, {level}, {dir} (N/S/E/W), {direction}. Default \"{number} - {name} - {dir}\".")]
        [DefaultValue("{number} - {name} - {dir}")]
        public string ElevationNamePattern { get; set; } = "{number} - {name} - {dir}";

        [JsonProperty("hostPlan")]
        [Description("Plan (name or id) that hosts the elevation markers when no room plan is created. Default: a floor plan of the room's level.")]
        public string HostPlan { get; set; }

        [JsonProperty("continueOnError")]
        [Description("false (default): any failure rolls back everything. true: failed rooms are listed in failed[] and the rest kept.")]
        [DefaultValue(false)]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>VIS-06: per room, a cropped plan and interior elevations (room documentation).</summary>
    [McpTool("create_room_views", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "views", TimeoutSeconds = 120,
        Description = "Room documentation views in bulk: for each room (ids, a level, or all) create a floor plan cropped " +
                      "to the room (+ padding mm) and/or interior elevations from a marker at the room's location " +
                      "point, each cropped to the room and far-clipped beyond its walls. Directions are the way the " +
                      "camera looks (north = +Y). Name patterns with {number}, {name}, {level}, {dir}; scales (50 = " +
                      "1:50) and templates per kind. Up to 100 rooms per call; needs approval. Undo: Ctrl+Z or delete " +
                      "the returned view ids.")]
    public sealed class CreateRoomViewsTool : McpTool<CreateRoomViewsArgs>
    {
        public const int MaxRooms = 100;

        protected override void Validate(CreateRoomViewsArgs args)
        {
            var sources = (args.Rooms != null && args.Rooms.Count > 0 ? 1 : 0) + (!string.IsNullOrWhiteSpace(args.Level) ? 1 : 0) + (args.All ? 1 : 0);
            if (sources != 1)
                throw new ToolException("INVALID_ARGUMENTS", "Pass exactly one of rooms, level or all:true.");
            if (args.Kinds != null && args.Kinds.Count == 0)
                throw new ToolException("INVALID_ARGUMENTS", "kinds must list plan and/or elevations (or be omitted).");
            if (args.Directions != null && args.Directions.Count == 0)
                throw new ToolException("INVALID_ARGUMENTS", "directions must list at least one direction (or be omitted).");
            if (args.Padding < 0) throw new ToolException("INVALID_ARGUMENTS", "padding must be 0 or more.");
            if (args.Rooms != null && args.Rooms.Count > MaxRooms)
                throw new ToolException("TOO_MANY_ITEMS", $"{args.Rooms.Count} rooms requested; the limit is {MaxRooms} per call.");
        }

        protected override object Run(ToolContext ctx, CreateRoomViewsArgs args)
        {
            var doc = ctx.Doc;
            var kinds = (args.Kinds ?? new List<string> { "plan", "elevations" }).Select(k => k.ToLowerInvariant()).ToList();
            var wantPlan = kinds.Contains("plan");
            var wantElev = kinds.Contains("elevations");
            var directions = (args.Directions ?? ElevationSupport.Directions.ToList()).Select(d => d.ToLowerInvariant()).Distinct().ToList();

            var rooms = CollectRooms(ctx, args);
            if (rooms.Count > MaxRooms)
                throw new ToolException("TOO_MANY_ITEMS", $"{rooms.Count} rooms match; the limit is {MaxRooms} per call.",
                    "Pass a list of room ids, or one level at a time.");

            // Resolve types and templates once, before creating anything.
            var planType = wantPlan ? ViewSupport.FamilyType(ctx, ViewFamily.FloorPlan, args.PlanViewFamilyType) : null;
            var elevType = wantElev ? ViewSupport.FamilyType(ctx, ViewFamily.Elevation, args.ElevationViewFamilyType) : null;
            if (wantPlan && !string.IsNullOrWhiteSpace(args.PlanTemplate)) ctx.Services.Lookup.ViewTemplate(args.PlanTemplate);
            if (wantElev && !string.IsNullOrWhiteSpace(args.ElevationTemplate)) ctx.Services.Lookup.ViewTemplate(args.ElevationTemplate);
            ViewPlan hostPlan = null;
            if (wantElev && !wantPlan && !string.IsNullOrWhiteSpace(args.HostPlan))
            {
                hostPlan = ctx.Services.Lookup.View(args.HostPlan) as ViewPlan
                    ?? throw new ToolException("NOT_A_PLAN", $"hostPlan '{args.HostPlan}' is not a plan view.");
            }

            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pad = Mm.ToFt(args.Padding);
            var r = ItemLoop.Run(ctx, rooms, args.ContinueOnError, (room, index) =>
            {
                if (room.Area <= 0 || !(room.Location is LocationPoint lp))
                    throw new ToolException("ROOM_NOT_PLACED", $"Room {room.Number} is not placed or not enclosed (area 0).");
                var bb = room.get_BoundingBox(null)
                         ?? throw new ToolException("ROOM_NOT_PLACED", $"Room {room.Number} has no extents.");
                var corners = ViewSupport.Corners(bb).ToList();
                var level = doc.GetElement(room.LevelId) as Level;
                var tokens = new Dictionary<string, string>
                {
                    ["number"] = room.Number,
                    ["name"] = RoomName(room),
                    ["level"] = level?.Name ?? string.Empty
                };

                object planResult = null;
                ViewPlan plan = null;
                if (wantPlan)
                {
                    if (level == null) throw new ToolException("ROOM_NOT_PLACED", $"Room {room.Number} has no level.");
                    plan = ViewPlan.Create(doc, planType.Id, level.Id);
                    ViewSupport.CropToPoints(plan, corners, pad);
                    var name = ViewSupport.FreeViewName(doc, ViewSupport.Expand(args.PlanNamePattern, tokens), plan.ViewType, reserved);
                    ViewSupport.ApplyCommon(ctx, plan, null, args.PlanScale, null, args.PlanTemplate, null);
                    ctx.Services.Names.ApplyName(plan, name, "view name");
                    planResult = new { view = ViewSupport.Describe(plan), crop = ViewSupport.DescribeCrop(plan) };
                }

                var elevations = new List<object>();
                if (wantElev)
                {
                    var host = plan ?? hostPlan ?? DefaultHost(doc, room.LevelId, room.Number);
                    var point = new XYZ(lp.Point.X, lp.Point.Y, level?.Elevation ?? lp.Point.Z);
                    var views = ElevationSupport.Create(doc, elevType, host, point, args.ElevationScale, directions);
                    foreach (var d in directions)
                    {
                        var v = views[d];
                        ElevationSupport.CropTo(v, corners, pad);
                        var t = new Dictionary<string, string>(tokens) { ["dir"] = ElevationSupport.Letter(d), ["direction"] = d };
                        var name = ViewSupport.FreeViewName(doc, ViewSupport.Expand(args.ElevationNamePattern, t), v.ViewType, reserved);
                        ViewSupport.ApplyCommon(ctx, v, null, args.ElevationScale, null, args.ElevationTemplate, null);
                        ctx.Services.Names.ApplyName(v, name, "view name");
                        elevations.Add(new { direction = d, looksTo = ElevationSupport.LooksTo(v), hostPlan = host.Name, view = ViewSupport.Describe(v), crop = ViewSupport.DescribeCrop(v) });
                    }
                }

                return new
                {
                    index,
                    roomId = room.Id.GetValue(),
                    number = room.Number,
                    name = RoomName(room),
                    level = level?.Name,
                    plan = planResult,
                    elevations
                };
            }, room => $"{room.Number} {RoomName(room)}", "Room");

            return new
            {
                rooms = r.Done,
                failed = r.Failed,
                roomCount = r.Done.Count,
                failedCount = r.Failed.Count,
                dryRun = ctx.DryRun
            };
        }

        private static List<Room> CollectRooms(ToolContext ctx, CreateRoomViewsArgs args)
        {
            if (args.Rooms != null && args.Rooms.Count > 0)
            {
                return args.Rooms.Distinct().Select(id =>
                {
                    var e = ctx.Services.Lookup.Element(id);
                    return e as Room ?? throw new ToolException("NOT_A_ROOM", $"Element {id} is not a room ({e.Category?.Name}).");
                }).ToList();
            }
            var all = new FilteredElementCollector(ctx.Doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Where(r => r.Area > 0 && r.Location != null);
            if (!string.IsNullOrWhiteSpace(args.Level))
            {
                var level = ctx.Services.Lookup.Level(args.Level);
                all = all.Where(r => r.LevelId == level.Id);
            }
            var list = all.OrderBy(r => r.Number, StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0)
                throw new ToolException("NOT_FOUND", "No placed rooms match.", "Check the level, or place rooms first.");
            return list;
        }

        private static ViewPlan DefaultHost(Document doc, ElementId levelId, string roomNumber)
        {
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null && v.GenLevel.Id == levelId)
                .OrderBy(v => v.GetPrimaryViewId() == ElementId.InvalidElementId ? 0 : 1).ThenBy(v => v.Id.GetValue())
                .ToList();
            if (plans.Count == 0)
                throw new ToolException("HOST_PLAN_NOT_FOUND", $"No floor plan exists on the level of room {roomNumber} to host the elevation marker.",
                    "Include kind plan, or pass hostPlan.");
            return plans[0];
        }

        private static string RoomName(Room room) =>
            room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name;
    }
}
