using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture
{
    public sealed class EnvelopeFloor
    {
        [JsonProperty("floorType")]
        [Required]
        [Description("Finish floor type (\"Family: Type\", name or id), e.g. \"Piso porcelanato 10 mm\".")]
        public string FloorType { get; set; }

        [JsonProperty("heightOffset")]
        [Unit("mm")]
        [DefaultValue(0)]
        [Description("Top of the finish above the room's level in mm. Default 0 (level = finished floor).")]
        public double HeightOffset { get; set; }

        [JsonProperty("boundaryOffset")]
        [Unit("mm")]
        [DefaultValue(0)]
        [Description("Grow (+) or shrink (-) the room outline in mm. Default 0.")]
        public double BoundaryOffset { get; set; }
    }

    public sealed class EnvelopeCeiling
    {
        [JsonProperty("ceilingType")]
        [Required]
        [Description("Ceiling type (\"Family: Type\", name or id), e.g. \"Forro de gesso 12,5 mm\".")]
        public string CeilingType { get; set; }

        [JsonProperty("heightAboveLevel")]
        [Required]
        [Unit("mm")]
        [Description("Ceiling height above the room's level in mm, e.g. 2600.")]
        public double HeightAboveLevel { get; set; }

        [JsonProperty("boundaryOffset")]
        [Unit("mm")]
        [DefaultValue(0)]
        [Description("Grow (+) or shrink (-) the room outline in mm, e.g. -10 for a shadow gap. Default 0.")]
        public double BoundaryOffset { get; set; }
    }

    public sealed class EnvelopeProfile
    {
        [JsonProperty("method")]
        [Required]
        [EnumValues("line_family", "wall_sweep")]
        [Description("line_family (exact per room, cut at openings) or wall_sweep (whole wall, cut by inserts).")]
        public string Method { get; set; }

        [JsonProperty("type")]
        [Required]
        [Description("Line-based family type, or wall sweep type for wall_sweep.")]
        public string Type { get; set; }

        [JsonProperty("elevation")]
        [Unit("mm")]
        [Description("Height above the room's level in mm. Default: 0 for the baseboard, the ceiling height for the trim.")]
        public double? Elevation { get; set; }

        [JsonProperty("reverseDirection")]
        [DefaultValue(false)]
        [Description("line_family: flip which side the profile sticks out. Default false.")]
        public bool ReverseDirection { get; set; }
    }

    public sealed class CreateRoomEnvelopeArgs
    {
        [JsonProperty("rooms")]
        [MaxLength(300)]
        [Description("Rooms by id, number or name. Give rooms or level.")]
        public List<string> Rooms { get; set; }

        [JsonProperty("level")]
        [Description("All placed rooms of this level (name or id). Give rooms or level.")]
        public string Level { get; set; }

        [JsonProperty("floorFinish")]
        [Description("Finish floor (piso de acabamento) over the room's inner outline.")]
        public EnvelopeFloor FloorFinish { get; set; }

        [JsonProperty("ceiling")]
        [Description("Ceiling (forro) over the room's inner outline.")]
        public EnvelopeCeiling Ceiling { get; set; }

        [JsonProperty("baseboard")]
        [Description("Baseboard (rodapé) along the perimeter, interrupted at doors.")]
        public EnvelopeProfile Baseboard { get; set; }

        [JsonProperty("ceilingTrim")]
        [Description("Ceiling trim (tabica) along the perimeter at the ceiling line.")]
        public EnvelopeProfile CeilingTrim { get; set; }

        [JsonProperty("existing")]
        [EnumValues("skip", "create")]
        [DefaultValue("skip")]
        [Description("skip (default): rooms that already have a floor / ceiling over them do not get another. create: add anyway.")]
        public string Existing { get; set; }
    }

    /// <summary>ARQ-04: close a room (finish floor, ceiling, baseboard, ceiling trim) in one call, reporting each part.</summary>
    [McpTool("create_room_envelope", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "architecture", TimeoutSeconds = 120,
        Description = "Close rooms in one go: finish floor and ceiling over each room's inner outline, baseboard (rodapé) " +
                      "and ceiling trim (tabica) along its perimeter, each with an explicitly named type. Parts already " +
                      "present (a floor/ceiling over the room) are skipped by default. A failing part does not stop the " +
                      "others: every room reports created/skipped/failed per part with ids. Run with dryRun first to show " +
                      "the plan. One Ctrl+Z undoes the whole call.")]
    public sealed class CreateRoomEnvelopeTool : McpTool<CreateRoomEnvelopeArgs>
    {
        protected override void Validate(CreateRoomEnvelopeArgs a)
        {
            bool hasRooms = a.Rooms != null && a.Rooms.Count > 0;
            if (hasRooms == !string.IsNullOrWhiteSpace(a.Level))
                throw new ToolException("INVALID_ARGUMENTS", "Give rooms or level (exactly one).");
            if (a.FloorFinish == null && a.Ceiling == null && a.Baseboard == null && a.CeilingTrim == null)
                throw new ToolException("INVALID_ARGUMENTS", "Ask for at least one part: floorFinish, ceiling, baseboard or ceilingTrim.");
        }

        protected override object Run(ToolContext ctx, CreateRoomEnvelopeArgs a)
        {
            var doc = ctx.Doc;
            bool skipExisting = !string.Equals(a.Existing, "create", StringComparison.OrdinalIgnoreCase);

            // Resolve every type before touching the model, so a typo fails the call instead of every room.
            var floorType = a.FloorFinish == null ? null : TypeFinder.Resolve<FloorType>(doc, a.FloorFinish.FloorType, "floor type", t => !t.IsFoundationSlab);
            var ceilingType = a.Ceiling == null ? null : TypeFinder.Resolve<CeilingType>(doc, a.Ceiling.CeilingType, "ceiling type");
            var baseboard = a.Baseboard == null ? null : PerimeterArgs.Options(ctx, "baseboard", a.Baseboard.Method, a.Baseboard.Type,
                a.Baseboard.Elevation, "finish", true, true, 50, a.Baseboard.ReverseDirection);
            var trim = a.CeilingTrim == null ? null : PerimeterArgs.Options(ctx, "ceiling_trim", a.CeilingTrim.Method, a.CeilingTrim.Type,
                a.CeilingTrim.Elevation ?? (a.Ceiling != null ? a.Ceiling.HeightAboveLevel : (double?)null), "finish", false, true, 50, a.CeilingTrim.ReverseDirection);
            var rooms = PerimeterArgs.Rooms(ctx, a.Rooms, a.Level);

            var sweeps = new PerimeterProfiles.SweepRegistry();
            var allIds = new List<long>();
            var report = new List<object>();
            int created = 0, skipped = 0, failed = 0;

            foreach (var room in rooms)
            {
                var parts = new List<object>();
                void Part(string name, Func<object> run, Func<string> existingReason)
                {
                    var reason = skipExisting ? existingReason?.Invoke() : null;
                    if (reason != null)
                    {
                        parts.Add(new { part = name, status = "skipped", reason });
                        skipped++;
                        return;
                    }
                    object result = null;
                    var error = ItemRunner.TryIsolated(doc, () => result = run());
                    if (error == null)
                    {
                        parts.Add(new { part = name, status = "created", result });
                        created++;
                    }
                    else
                    {
                        parts.Add(new { part = name, status = "failed", code = (error as ToolException)?.Code ?? "REVIT_ERROR", message = error.Message, hint = (error as ToolException)?.Hint });
                        failed++;
                    }
                }

                if (floorType != null)
                    Part("floorFinish", () =>
                    {
                        var b = new SurfaceBoundary { Room = room.Id.GetValue().ToString(), BoundaryOffset = a.FloorFinish.BoundaryOffset };
                        var rb = BoundaryResolver.Resolve(doc, b, 0);
                        var f = SurfaceCreation.CreateFloor(doc, rb.Loops, floorType, room.Level, ArchGeometry.Ft(a.FloorFinish.HeightOffset), false);
                        allIds.Add(f.Id.GetValue());
                        return SurfaceCreation.FloorReadBack(f, rb, rb.Loops);
                    }, () => FloorOver(doc, room));

                if (ceilingType != null)
                    Part("ceiling", () =>
                    {
                        var b = new SurfaceBoundary { Room = room.Id.GetValue().ToString(), BoundaryOffset = a.Ceiling.BoundaryOffset };
                        var rb = BoundaryResolver.Resolve(doc, b, 0);
                        var c = SurfaceCreation.CreateCeiling(doc, rb.Loops, ceilingType, room.Level, ArchGeometry.Ft(a.Ceiling.HeightAboveLevel));
                        allIds.Add(c.Id.GetValue());
                        return SurfaceCreation.CeilingReadBack(c, rb, rb.Loops);
                    }, () =>
                    {
                        var h = PerimeterProfiles.CeilingHeightOver(doc, room);
                        return h.HasValue ? $"a ceiling already covers the room at {ArchGeometry.Fmt(ArchGeometry.Mm(h.Value))}" : null;
                    });

                if (baseboard != null)
                    Part("baseboard", () => PerimeterProfiles.ForRoom(ctx, room, baseboard, sweeps, allIds), null);
                if (trim != null)
                    Part("ceilingTrim", () => PerimeterProfiles.ForRoom(ctx, room, trim, sweeps, allIds), null);

                report.Add(new { room = RoomSupport.Summary(room), parts });
            }

            return new
            {
                rooms = report,
                roomCount = rooms.Count,
                partsCreated = created,
                partsSkipped = skipped,
                partsFailed = failed,
                elementIds = allIds,
                note = "Slabs (laje inferior/superior) are not part of this tool; use create_floor with the structure level."
            };
        }

        /// <summary>A reason to skip the finish floor when a floor on the room's level already covers its location point.</summary>
        private static string FloorOver(Document doc, Room room)
        {
            var at = (room.Location as LocationPoint)?.Point;
            if (at == null) return null;
            foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => f.LevelId == room.LevelId))
            {
                var bb = f.get_BoundingBox(null);
                if (bb == null || at.X < bb.Min.X || at.X > bb.Max.X || at.Y < bb.Min.Y || at.Y > bb.Max.Y) continue;
                // Only floors whose top is near the room's level (finish floors), not the slab of another storey.
                var offset = ArchGeometry.DoubleParam(f, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (!double.IsNaN(offset) && Math.Abs(offset) > ArchGeometry.Ft(300)) continue;
                if (!IsInsideFloor(f, at)) continue;
                return $"floor {f.Id.GetValue()} ({Framework.Services.LookupService.FullName(f.FloorType)}) already covers the room";
            }
            return null;
        }

        private static bool IsInsideFloor(Floor f, XYZ at)
        {
            try
            {
                var top = HostObjectUtils.GetTopFaces(f).Select(r => f.GetGeometryObjectFromReference(r) as Face).FirstOrDefault(x => x != null);
                if (top == null) return true;
                var proj = top.Project(new XYZ(at.X, at.Y, top.Evaluate(new UV(0, 0)).Z));
                return proj != null;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return true;
            }
        }
    }
}
