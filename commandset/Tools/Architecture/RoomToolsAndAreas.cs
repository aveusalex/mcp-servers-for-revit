using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture
{
    // ---------------------------------------------------------------------------------------------------------------
    // ARQ-08 create_rooms
    // ---------------------------------------------------------------------------------------------------------------

    public sealed class RoomPointItem
    {
        [JsonProperty("point")]
        [Required]
        [Description("A plan point in mm inside the area the room should fill.")]
        public PointMm Point { get; set; }

        [JsonProperty("name")]
        [Description("Room name (e.g. \"Sala\").")]
        public string Name { get; set; }

        [JsonProperty("number")]
        [Description("Room number (e.g. \"101\"). Omit to let Revit number it.")]
        public string Number { get; set; }
    }

    public sealed class CreateRoomsArgs
    {
        [JsonProperty("mode")]
        [Required]
        [EnumValues("points", "all_circuits")]
        [Description("points: one room per item point. all_circuits: a room in every closed circuit of the level (walls + separation lines).")]
        public string Mode { get; set; }

        [JsonProperty("level")]
        [Required]
        [Description("Level by name or id.")]
        public string Level { get; set; }

        [JsonProperty("phase")]
        [Description("Phase by name or id. Default: the project's last phase (where new construction usually goes).")]
        public string Phase { get; set; }

        [JsonProperty("items")]
        [MaxLength(500)]
        [Description("points mode: the rooms to create.")]
        public List<RoomPointItem> Items { get; set; }

        [JsonProperty("name")]
        [Description("all_circuits mode: name given to every new room (optional).")]
        public string Name { get; set; }

        [JsonProperty("numberPrefix")]
        [Description("all_circuits mode: prefix for the numbers (e.g. \"1\" gives 101, 102... with startNumber 1 and digits 2).")]
        public string NumberPrefix { get; set; }

        [JsonProperty("startNumber")]
        [Range(0, 100000)]
        [Description("all_circuits mode: first sequential number. Omit to let Revit number the rooms.")]
        public int? StartNumber { get; set; }

        [JsonProperty("digits")]
        [Range(1, 6)]
        [DefaultValue(2)]
        [Description("all_circuits mode: minimum digits of the sequential part (zero padded). Default 2.")]
        public int Digits { get; set; } = 2;

        [JsonProperty("skipExisting")]
        [DefaultValue(true)]
        [Description("Skip circuits/points that already have a room in that phase. Default true.")]
        public bool SkipExisting { get; set; } = true;
    }

    /// <summary>ARQ-08: rooms by point or in every closed circuit of a level.</summary>
    [McpTool("create_rooms", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Create rooms on a level: at given plan points (mm) with name/number, or automatically in every " +
                      "closed circuit (walls + room separation lines) with optional name and sequential numbers. Rooms " +
                      "that already exist are skipped by default. Each room is read back with its area (m²) and whether it " +
                      "is enclosed; dryRun lists the circuits it would fill. One Ctrl+Z undoes the call.")]
    public sealed class CreateRoomsTool : McpTool<CreateRoomsArgs>
    {
        protected override void Validate(CreateRoomsArgs args)
        {
            bool points = args.Mode.Equals("points", StringComparison.OrdinalIgnoreCase);
            if (points && (args.Items == null || args.Items.Count == 0))
                throw new ToolException("INVALID_ARGUMENTS", "points mode needs items[] with a point each.");
            if (!points && args.Items != null && args.Items.Count > 0)
                throw new ToolException("INVALID_ARGUMENTS", "items[] is only used in points mode.");
        }

        protected override object Run(ToolContext ctx, CreateRoomsArgs args)
        {
            var doc = ctx.Doc;
            var level = ctx.Services.Lookup.Level(args.Level);
            var phase = string.IsNullOrWhiteSpace(args.Phase)
                ? doc.Phases.Cast<Phase>().LastOrDefault() ?? throw new ToolException("PHASE_NOT_FOUND", "The document has no phases.")
                : ctx.Services.Lookup.Phase(args.Phase);
            var created = new List<Room>();
            var skipped = new List<object>();

            if (args.Mode.Equals("points", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < args.Items.Count; i++)
                {
                    var it = args.Items[i];
                    var probe = new XYZ(ArchGeometry.Ft(it.Point.X), ArchGeometry.Ft(it.Point.Y), level.Elevation + ArchGeometry.Ft(500));
                    var existing = doc.GetRoomAtPoint(probe, phase);
                    if (existing != null && args.SkipExisting)
                    {
                        skipped.Add(new { index = i, reason = "ROOM_EXISTS", room = RoomSupport.Summary(existing) });
                        continue;
                    }
                    var room = doc.Create.NewRoom(level, new UV(ArchGeometry.Ft(it.Point.X), ArchGeometry.Ft(it.Point.Y)))
                               ?? throw new ToolException("CREATE_FAILED", $"items[{i}]: Revit did not create the room.");
                    var roomPhase = room.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId();
                    if (roomPhase != null && roomPhase != ElementId.InvalidElementId && roomPhase != phase.Id)
                        throw new ToolException("PHASE_MISMATCH",
                            $"Revit placed the room in phase '{(doc.GetElement(roomPhase) as Phase)?.Name}', not '{phase.Name}'.",
                            "Use mode all_circuits with that phase, or omit phase.");
                    Apply(ctx, room, it.Name, it.Number);
                    created.Add(room);
                }
            }
            else
            {
                var topology = doc.get_PlanTopology(level, phase);
                int seq = args.StartNumber ?? 0;
                var circuits = topology.Circuits.Cast<PlanCircuit>().ToList();
                foreach (var circuit in circuits)
                {
                    if (circuit.IsRoomLocated && args.SkipExisting)
                    {
                        skipped.Add(new { reason = "ROOM_EXISTS", circuitAreaM2 = ArchGeometry.M2(circuit.Area) });
                        continue;
                    }
                    var room = doc.Create.NewRoom(phase);
                    room = doc.Create.NewRoom(room, circuit) ?? throw new ToolException("CREATE_FAILED", "Revit did not place a room in a circuit.");
                    string number = args.StartNumber.HasValue
                        ? (args.NumberPrefix ?? "") + (seq++).ToString(CultureInfo.InvariantCulture).PadLeft(args.Digits, '0')
                        : null;
                    Apply(ctx, room, args.Name, number);
                    created.Add(room);
                }
            }

            doc.Regenerate();
            var result = new List<object>();
            foreach (var room in created)
            {
                if (!room.IsValidObject)
                    throw new ToolException("CREATE_FAILED", "Revit removed a room right after creating it (redundant or outside any circuit).");
                bool enclosed = room.Area > 0;
                if (!enclosed)
                    ctx.Warnings.Add(new ToolWarning($"Room {room.Number} is not enclosed (area 0). Close it with walls or create_room_separation_lines.",
                        new List<long> { room.Id.GetValue() }));
                var loc = (room.Location as LocationPoint)?.Point;
                result.Add(new
                {
                    id = room.Id.GetValue(),
                    number = room.Number,
                    name = RoomSupport.RoomName(room),
                    level = level.Name,
                    phase = (doc.GetElement(room.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() ?? ElementId.InvalidElementId) as Phase)?.Name,
                    areaM2 = ArchGeometry.M2(room.Area),
                    enclosed,
                    location = loc == null ? null : ArchGeometry.ToWire2(loc)
                });
            }
            return new { created = result, skipped, createdCount = result.Count, skippedCount = skipped.Count, level = level.Name, phase = phase.Name };
        }

        private static void Apply(ToolContext ctx, Room room, string name, string number)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var p = room.get_Parameter(BuiltInParameter.ROOM_NAME);
                p.Set(name);
                if (p.AsString() != name) throw new ToolException("NAME_NOT_APPLIED", $"Revit kept the room name '{p.AsString()}'.");
            }
            if (!string.IsNullOrWhiteSpace(number))
            {
                room.Number = number;
                if (room.Number != number) throw new ToolException("NAME_NOT_APPLIED", $"Revit kept the room number '{room.Number}'.");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // ARQ-09 create_room_separation_lines
    // ---------------------------------------------------------------------------------------------------------------

    public sealed class SegmentMm
    {
        [JsonProperty("start")]
        [Required]
        [Description("Start point in mm.")]
        public PointMm Start { get; set; }

        [JsonProperty("end")]
        [Required]
        [Description("End point in mm.")]
        public PointMm End { get; set; }
    }

    public sealed class CreateRoomSeparationLinesArgs
    {
        [JsonProperty("level")]
        [Required]
        [Description("Level by name or id.")]
        public string Level { get; set; }

        [JsonProperty("view")]
        [Description("Floor plan of that level to draw in, by name or id. Default: the level's floor plan when there is exactly one.")]
        public string View { get; set; }

        [JsonProperty("points")]
        [MinLength(2)]
        [MaxLength(500)]
        [Description("Polyline of plan points in mm (consecutive points are joined). Give points or segments.")]
        public List<PointMm> Points { get; set; }

        [JsonProperty("closed")]
        [DefaultValue(false)]
        [Description("With points: also join the last point to the first. Default false.")]
        public bool Closed { get; set; }

        [JsonProperty("segments")]
        [MaxLength(500)]
        [Description("Independent segments (alternative to points).")]
        public List<SegmentMm> Segments { get; set; }
    }

    /// <summary>ARQ-09: room separation lines on a level.</summary>
    [McpTool("create_room_separation_lines", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Create room separation lines on a level (to split open areas or close rooms without walls), from " +
                      "a polyline of points or a list of segments, in mm. Drawn in a floor plan of that level (named, or " +
                      "the only one). Returns the line ids. One Ctrl+Z undoes the call.")]
    public sealed class CreateRoomSeparationLinesTool : McpTool<CreateRoomSeparationLinesArgs>
    {
        protected override void Validate(CreateRoomSeparationLinesArgs args)
        {
            bool hasPoints = args.Points != null && args.Points.Count > 0;
            bool hasSegments = args.Segments != null && args.Segments.Count > 0;
            if (hasPoints == hasSegments)
                throw new ToolException("INVALID_ARGUMENTS", "Give points or segments (exactly one).");
        }

        protected override object Run(ToolContext ctx, CreateRoomSeparationLinesArgs args)
        {
            var doc = ctx.Doc;
            var level = ctx.Services.Lookup.Level(args.Level);
            var view = PlanViews.ForLevel(ctx, level, args.View, ViewType.FloorPlan);
            var z = level.Elevation;
            var tol = doc.Application.ShortCurveTolerance;
            var curves = new CurveArray();
            var pairs = new List<(XYZ, XYZ)>();
            if (args.Points != null && args.Points.Count > 0)
            {
                var pts = ArchGeometry.ToXyz(args.Points, z);
                int n = args.Closed ? pts.Count : pts.Count - 1;
                for (int i = 0; i < n; i++) pairs.Add((pts[i], pts[(i + 1) % pts.Count]));
            }
            else
            {
                foreach (var s in args.Segments) pairs.Add((ArchGeometry.ToXyz(s.Start, z), ArchGeometry.ToXyz(s.End, z)));
            }
            for (int i = 0; i < pairs.Count; i++)
            {
                var (a, b) = pairs[i];
                if (a.DistanceTo(b) <= tol)
                    throw new ToolException("INVALID_GEOMETRY", $"Segment {i} is {ArchGeometry.Fmt(ArchGeometry.Mm(a.DistanceTo(b)))} long; too short for Revit.");
                curves.Append(Line.CreateBound(a, b));
            }
            var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
            var lines = doc.Create.NewRoomBoundaryLines(plane, curves, view);
            if (lines == null || lines.Size == 0)
                throw new ToolException("CREATE_FAILED", "Revit did not create the separation lines.");
            doc.Regenerate();
            var ids = new List<long>();
            double total = 0;
            foreach (ModelCurve mc in lines)
            {
                if (!mc.IsValidObject) continue;
                ids.Add(mc.Id.GetValue());
                total += mc.GeometryCurve.Length;
            }
            return new { ids, count = ids.Count, totalLengthM = ArchGeometry.M(total), level = level.Name, view = view.Name };
        }
    }

    /// <summary>Plan views of a level, chosen by name or because there is only one.</summary>
    public static class PlanViews
    {
        public static ViewPlan ForLevel(ToolContext ctx, Level level, string viewText, ViewType viewType)
        {
            var doc = ctx.Doc;
            if (!string.IsNullOrWhiteSpace(viewText))
            {
                var v = ctx.Services.Lookup.View(viewText) as ViewPlan;
                if (v == null || v.GenLevel == null || v.GenLevel.Id != level.Id)
                    throw new ToolException("VIEW_NOT_SUITABLE", $"'{viewText}' is not a plan view of level '{level.Name}'.");
                return v;
            }
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == viewType && v.GenLevel != null && v.GenLevel.Id == level.Id).ToList();
            if (plans.Count == 1) return plans[0];
            if (plans.Count == 0)
                throw new ToolException("VIEW_NOT_FOUND", $"Level '{level.Name}' has no {viewType} view to draw in.",
                    "Create one (create_or_update_levels with createPlanView) or pass view.", "create_or_update_levels");
            // Several plans: the lines are model elements, so any of them works; pick deterministically by id and say so.
            var chosen = plans.OrderBy(v => v.Id.GetValue()).First();
            ctx.Warnings.Add(new ToolWarning($"Level '{level.Name}' has {plans.Count} plan views; drew in '{chosen.Name}' (the lines are visible in all of them)."));
            return chosen;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // ARQ-10 create_area
    // ---------------------------------------------------------------------------------------------------------------

    public sealed class AreaItem
    {
        [JsonProperty("point")]
        [Description("Plan point in mm inside the area. Optional when boundary is given (its centre is used).")]
        public PointMm Point { get; set; }

        [JsonProperty("boundary")]
        [MinLength(3)]
        [MaxLength(500)]
        [Description("Closed outline in mm: area boundary lines are drawn along it before placing the area.")]
        public List<PointMm> Boundary { get; set; }

        [JsonProperty("name")]
        [Description("Area name.")]
        public string Name { get; set; }

        [JsonProperty("number")]
        [Description("Area number.")]
        public string Number { get; set; }
    }

    public sealed class CreateAreaArgs
    {
        [JsonProperty("areaScheme")]
        [Description("Area scheme by name or id (e.g. \"Área bruta\" / \"Gross Building\"). Required when the document has more than one.")]
        public string AreaScheme { get; set; }

        [JsonProperty("level")]
        [Required]
        [Description("Level by name or id.")]
        public string Level { get; set; }

        [JsonProperty("areaPlan")]
        [Description("Area plan view by name or id. Default: the area plan of this scheme and level.")]
        public string AreaPlan { get; set; }

        [JsonProperty("createAreaPlanIfMissing")]
        [DefaultValue(true)]
        [Description("Create the area plan for this scheme and level when there is none. Default true.")]
        public bool CreateAreaPlanIfMissing { get; set; } = true;

        [JsonProperty("items")]
        [Required]
        [MinLength(1)]
        [MaxLength(200)]
        [Description("Areas to create.")]
        public List<AreaItem> Items { get; set; }
    }

    /// <summary>ARQ-10: areas in an area plan (creating the plan and the boundary lines when asked).</summary>
    [McpTool("create_area", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Create areas (for area schedules / quadro de áreas) in the area plan of a scheme and level, " +
                      "creating that area plan if missing. Each item is a point inside an existing boundary, or a closed " +
                      "outline in mm that is first drawn as area boundary lines. Read-back: area in m², name, number, " +
                      "and a warning when it is not enclosed. One Ctrl+Z undoes the call.")]
    public sealed class CreateAreaTool : McpTool<CreateAreaArgs>
    {
        protected override void Validate(CreateAreaArgs args)
        {
            for (int i = 0; i < args.Items.Count; i++)
            {
                var it = args.Items[i] ?? throw new ToolException("INVALID_ARGUMENTS", $"items[{i}] is null.");
                if (it.Point == null && (it.Boundary == null || it.Boundary.Count < 3))
                    throw new ToolException("INVALID_ARGUMENTS", $"items[{i}]: give point, boundary, or both.");
            }
        }

        protected override object Run(ToolContext ctx, CreateAreaArgs args)
        {
            var doc = ctx.Doc;
            var level = ctx.Services.Lookup.Level(args.Level);
            var schemes = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().ToList();
            AreaScheme scheme;
            if (!string.IsNullOrWhiteSpace(args.AreaScheme)) scheme = ctx.Services.Lookup.ByNameOrId(args.AreaScheme, schemes, "area scheme");
            else if (schemes.Count == 1) scheme = schemes[0];
            else
                throw new ToolException("AREA_SCHEME_REQUIRED", "The document has several area schemes; name one.",
                    "Available: " + string.Join("; ", schemes.Select(s => s.Name)) + ".");

            ViewPlan plan;
            bool planCreated = false;
            if (!string.IsNullOrWhiteSpace(args.AreaPlan))
            {
                plan = ctx.Services.Lookup.View(args.AreaPlan) as ViewPlan;
                if (plan == null || plan.ViewType != ViewType.AreaPlan || plan.AreaScheme?.Id != scheme.Id || plan.GenLevel?.Id != level.Id)
                    throw new ToolException("VIEW_NOT_SUITABLE", $"'{args.AreaPlan}' is not an area plan of scheme '{scheme.Name}' on level '{level.Name}'.");
            }
            else
            {
                var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Where(v => !v.IsTemplate && v.ViewType == ViewType.AreaPlan && v.GenLevel?.Id == level.Id && v.AreaScheme?.Id == scheme.Id)
                    .OrderBy(v => v.Id.GetValue()).ToList();
                if (plans.Count > 0) plan = plans[0];
                else if (args.CreateAreaPlanIfMissing)
                {
                    plan = ViewPlan.CreateAreaPlan(doc, scheme.Id, level.Id)
                           ?? throw new ToolException("CREATE_FAILED", "Revit did not create the area plan.");
                    planCreated = true;
                }
                else
                    throw new ToolException("VIEW_NOT_FOUND", $"No area plan of scheme '{scheme.Name}' on level '{level.Name}'.",
                        "Pass createAreaPlanIfMissing:true.");
            }

            var z = level.Elevation;
            SketchPlane sketch = null;
            var created = new List<object>();
            for (int i = 0; i < args.Items.Count; i++)
            {
                var it = args.Items[i];
                var lineIds = new List<long>();
                XYZ inside = it.Point != null ? ArchGeometry.ToXyz(it.Point, z) : null;
                if (it.Boundary != null && it.Boundary.Count >= 3)
                {
                    var loop = ArchGeometry.PolygonLoop(doc, ArchGeometry.ToXyz(it.Boundary, z), $"items[{i}].boundary");
                    sketch ??= SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
                    foreach (var c in loop)
                    {
                        var line = doc.Create.NewAreaBoundaryLine(sketch, c, plan)
                                   ?? throw new ToolException("CREATE_FAILED", $"items[{i}]: Revit did not create an area boundary line.");
                        lineIds.Add(line.Id.GetValue());
                    }
                    if (inside == null) inside = InteriorPoint(ArchGeometry.LoopPoints(loop));
                }
                doc.Regenerate();
                var area = doc.Create.NewArea(plan, new UV(inside.X, inside.Y))
                           ?? throw new ToolException("CREATE_FAILED", $"items[{i}]: Revit did not create the area.");
                if (!string.IsNullOrWhiteSpace(it.Name))
                {
                    area.Name = it.Name;
                }
                if (!string.IsNullOrWhiteSpace(it.Number)) area.Number = it.Number;
                doc.Regenerate();
                if (!area.IsValidObject) throw new ToolException("CREATE_FAILED", $"items[{i}]: Revit removed the area right after creating it.");
                var name = area.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                if (!string.IsNullOrWhiteSpace(it.Name) && name != it.Name)
                    throw new ToolException("NAME_NOT_APPLIED", $"items[{i}]: Revit kept the area name '{name}'.");
                if (area.Area <= 0)
                    ctx.Warnings.Add(new ToolWarning($"Area {area.Number} is not enclosed (0 m²). Draw area boundary lines around it (boundary).",
                        new List<long> { area.Id.GetValue() }));
                created.Add(new
                {
                    index = i,
                    id = area.Id.GetValue(),
                    number = area.Number,
                    name,
                    areaM2 = ArchGeometry.M2(area.Area),
                    enclosed = area.Area > 0,
                    boundaryLineIds = lineIds
                });
            }

            return new
            {
                created,
                createdCount = created.Count,
                areaScheme = scheme.Name,
                level = level.Name,
                areaPlan = new { id = plan.Id.GetValue(), name = plan.Name, created = planCreated }
            };
        }

        /// <summary>A point inside a simple polygon: the centroid if inside, else the middle of a horizontal chord.</summary>
        private static XYZ InteriorPoint(IList<XYZ> pts)
        {
            var c = new XYZ(pts.Average(p => p.X), pts.Average(p => p.Y), pts[0].Z);
            if (ArchGeometry.Contains(pts, c)) return c;
            var y = c.Y;
            var xs = new List<double>();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                if ((a.Y > y) != (b.Y > y)) xs.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            xs.Sort();
            return xs.Count >= 2 ? new XYZ((xs[0] + xs[1]) / 2, y, c.Z) : c;
        }
    }
}
