using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Architecture.Support;

namespace RevitMCPCommandSet.Tools.Architecture
{
    public sealed class CreateRoomPerimeterProfileArgs
    {
        [JsonProperty("rooms")]
        [MaxLength(500)]
        [Description("Rooms by id, number or name. Give rooms or level.")]
        public List<string> Rooms { get; set; }

        [JsonProperty("level")]
        [Description("All placed rooms of this level (name or id). Give rooms or level.")]
        public string Level { get; set; }

        [JsonProperty("kind")]
        [Required]
        [EnumValues("baseboard", "ceiling_trim")]
        [Description("baseboard = rodapé at the floor; ceiling_trim = tabica/moldura at the ceiling line.")]
        public string Kind { get; set; }

        [JsonProperty("method")]
        [Required]
        [EnumValues("line_family", "wall_sweep")]
        [Description("line_family: line-based family instances on each boundary piece, interrupted at openings (exact per room). wall_sweep: a wall sweep on the room side of each bounding wall (runs the whole wall; cut by doors).")]
        public string Method { get; set; }

        [JsonProperty("type")]
        [Required]
        [Description("line_family: line-based family type (\"Family: Type\"), e.g. the office's rodapé family. wall_sweep: wall sweep type name.")]
        public string Type { get; set; }

        [JsonProperty("elevation")]
        [Unit("mm")]
        [Description("Height of the profile above the room's level in mm. Default: 0 for baseboard; the height of the ceiling over the room for ceiling_trim.")]
        public double? Elevation { get; set; }

        [JsonProperty("boundaryLocation")]
        [EnumValues("finish", "center", "coreBoundary", "coreCenter")]
        [DefaultValue("finish")]
        [Description("Which wall line the profile follows. Default finish (inner face of the walls).")]
        public string BoundaryLocation { get; set; }

        [JsonProperty("skipOpenings")]
        [DefaultValue(true)]
        [Description("Interrupt the profile where a door/opening crosses its height. Default true.")]
        public bool SkipOpenings { get; set; } = true;

        [JsonProperty("skipSeparationLines")]
        [DefaultValue(true)]
        [Description("No profile along room separation lines (there is no wall). Default true.")]
        public bool SkipSeparationLines { get; set; } = true;

        [JsonProperty("minSegment")]
        [Unit("mm")]
        [Range(0, 10000)]
        [DefaultValue(50)]
        [Description("Pieces shorter than this (mm) are skipped. Default 50.")]
        public double MinSegment { get; set; } = 50;

        [JsonProperty("reverseDirection")]
        [DefaultValue(false)]
        [Description("line_family: draw each piece the other way, which flips which side the profile sticks out. Default false (room on the left of each line).")]
        public bool ReverseDirection { get; set; }

        [JsonProperty("continueOnError")]
        [DefaultValue(false)]
        [Description("false (default): a failing room rolls back the call. true: failing rooms are listed in failed[].")]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>ARQ-05: baseboard (rodapé) or ceiling trim (tabica) along room perimeters, interrupted at openings.</summary>
    [McpTool("create_room_perimeter_profile", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Run a baseboard (rodapé) or ceiling trim (tabica) along the perimeter of rooms, skipping door " +
                      "openings and room separation lines. method line_family places a line-based family per boundary " +
                      "piece (exact per room); method wall_sweep adds a wall sweep on the room side of each wall (whole " +
                      "wall, cut by inserts). Ceiling trims default to the height of the ceiling over the room. Returns ids " +
                      "and total length (m) per room. One Ctrl+Z undoes the call.")]
    public sealed class CreateRoomPerimeterProfileTool : McpTool<CreateRoomPerimeterProfileArgs>
    {
        protected override void Validate(CreateRoomPerimeterProfileArgs args)
        {
            bool hasRooms = args.Rooms != null && args.Rooms.Count > 0;
            if (hasRooms == !string.IsNullOrWhiteSpace(args.Level))
                throw new ToolException("INVALID_ARGUMENTS", "Give rooms or level (exactly one).");
        }

        protected override object Run(ToolContext ctx, CreateRoomPerimeterProfileArgs args)
        {
            var doc = ctx.Doc;
            var options = PerimeterArgs.Options(ctx, args.Kind, args.Method, args.Type, args.Elevation, args.BoundaryLocation,
                args.SkipOpenings, args.SkipSeparationLines, args.MinSegment, args.ReverseDirection);
            var rooms = PerimeterArgs.Rooms(ctx, args.Rooms, args.Level);
            var sweeps = new PerimeterProfiles.SweepRegistry();
            var ids = new List<long>();
            var outcome = ItemRunner.Run(doc, rooms, args.ContinueOnError, "Room",
                (room, index) => PerimeterProfiles.ForRoom(ctx, room, options, sweeps, ids));
            return new
            {
                rooms = outcome.Created,
                failed = outcome.Failed,
                roomCount = outcome.Created.Count,
                failedCount = outcome.Failed.Count,
                elementCount = ids.Count
            };
        }
    }

    /// <summary>Argument resolution shared by the perimeter tool and the room envelope.</summary>
    public static class PerimeterArgs
    {
        public static PerimeterOptions Options(ToolContext ctx, string kind, string method, string type, double? elevationMm,
            string boundaryLocation, bool skipOpenings, bool skipSeparationLines, double minSegmentMm, bool reverse)
        {
            var o = new PerimeterOptions
            {
                Kind = kind.ToLowerInvariant(),
                Method = method.ToLowerInvariant(),
                ElevationFt = elevationMm.HasValue ? ArchGeometry.Ft(elevationMm.Value) : (double?)null,
                BoundaryLocation = RoomSupport.ParseLocation(boundaryLocation),
                SkipOpenings = skipOpenings,
                SkipSeparationLines = skipSeparationLines,
                MinSegmentFt = ArchGeometry.Ft(minSegmentMm),
                ReverseDirection = reverse
            };
            if (o.Method == "wall_sweep")
            {
                o.SweepType = TypeFinder.ResolveInCategory(ctx.Doc, type, BuiltInCategory.OST_Cornices, "wall sweep type");
            }
            else
            {
                var symbol = TypeFinder.Symbol(ctx.Doc, type, null, out var candidates);
                if (symbol == null)
                    throw new ToolException("AMBIGUOUS_NAME", $"'{type}' matches several family types: " +
                        string.Join("; ", candidates.Select(c => c.ToString())) + ".", "Use \"Family: Type\" or the type id.");
                if (symbol.Family.FamilyPlacementType != FamilyPlacementType.CurveBased)
                    throw new ToolException("TYPE_NOT_SUITABLE",
                        $"'{Framework.Services.LookupService.FullName(symbol)}' is not a line-based family ({symbol.Family.FamilyPlacementType}).",
                        "Use a line-based generic model family for rodapé/tabica, or method wall_sweep with a wall sweep type.");
                o.LineSymbol = symbol;
            }
            return o;
        }

        public static IList<Room> Rooms(ToolContext ctx, IList<string> refs, string levelText)
        {
            if (refs != null && refs.Count > 0)
            {
                var list = new List<Room>();
                foreach (var r in refs)
                {
                    var room = RoomSupport.Resolve(ctx.Doc, r);
                    if (list.All(x => x.Id != room.Id)) list.Add(room);
                }
                return list;
            }
            var level = ctx.Services.Lookup.Level(levelText);
            var rooms = RoomSupport.PlacedRoomsOn(ctx.Doc, level);
            if (rooms.Count == 0)
                throw new ToolException("ROOM_NOT_FOUND", $"Level '{level.Name}' has no placed rooms.", "Create them first (create_rooms).", "create_rooms");
            return rooms;
        }
    }
}
