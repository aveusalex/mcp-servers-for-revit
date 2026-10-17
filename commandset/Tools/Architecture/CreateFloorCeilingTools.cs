using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture
{
    public sealed class FloorItem
    {
        [JsonProperty("boundary")]
        [Required]
        [Description("Outline: points (+holes), rectangle, or a room (inner finish face by default).")]
        public SurfaceBoundary Boundary { get; set; }

        [JsonProperty("floorType")]
        [Required]
        [Description("Floor type as \"Family: Type\", type name or id. Required: never chosen for you.")]
        public string FloorType { get; set; }

        [JsonProperty("level")]
        [Description("Level by name or id. Optional with boundary.room (defaults to the room's level); required otherwise.")]
        public string Level { get; set; }

        [JsonProperty("heightOffset")]
        [Unit("mm")]
        [DefaultValue(0)]
        [Description("Height offset of the top of the floor from the level, in mm (Revit's 'Height Offset From Level'). Default 0.")]
        public double HeightOffset { get; set; }

        [JsonProperty("structural")]
        [DefaultValue(false)]
        [Description("Mark the floor as structural. Default false.")]
        public bool Structural { get; set; }
    }

    public sealed class CreateFloorArgs
    {
        [JsonProperty("items")]
        [Required]
        [MinLength(1)]
        [MaxLength(200)]
        [Description("Floors to create.")]
        public List<FloorItem> Items { get; set; }

        [JsonProperty("continueOnError")]
        [DefaultValue(false)]
        [Description("false (default): any failure rolls back the whole call. true: failed items are skipped and listed in failed[].")]
        public bool ContinueOnError { get; set; }
    }

    public sealed class CeilingItem
    {
        [JsonProperty("boundary")]
        [Required]
        [Description("Outline: points (+holes), rectangle, or a room (inner finish face by default).")]
        public SurfaceBoundary Boundary { get; set; }

        [JsonProperty("ceilingType")]
        [Required]
        [Description("Ceiling type as \"Family: Type\", type name or id. Required: never chosen for you.")]
        public string CeilingType { get; set; }

        [JsonProperty("level")]
        [Description("Level the height is measured from, by name or id. Optional with boundary.room (defaults to the room's level).")]
        public string Level { get; set; }

        [JsonProperty("heightAboveLevel")]
        [Required]
        [Unit("mm")]
        [Description("Height of the ceiling above the level in mm (Revit's 'Height Offset From Level'), e.g. 2600.")]
        public double HeightAboveLevel { get; set; }
    }

    public sealed class CreateCeilingArgs
    {
        [JsonProperty("items")]
        [Required]
        [MinLength(1)]
        [MaxLength(200)]
        [Description("Ceilings to create.")]
        public List<CeilingItem> Items { get; set; }

        [JsonProperty("continueOnError")]
        [DefaultValue(false)]
        [Description("false (default): any failure rolls back the whole call. true: failed items are skipped and listed in failed[].")]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>Shared creation code for floors and ceilings (also used by the room envelope).</summary>
    public static class SurfaceCreation
    {
        public static Level LevelFor(ToolContext ctx, string levelText, ResolvedBoundary boundary, string label)
        {
            if (!string.IsNullOrWhiteSpace(levelText)) return ctx.Services.Lookup.Level(levelText);
            if (boundary.Room?.Level != null) return boundary.Room.Level;
            throw new ToolException("INVALID_ARGUMENTS", $"{label}: level is required when the outline is not a room.");
        }

        public static Floor CreateFloor(Document doc, IList<CurveLoop> loops, FloorType type, Level level, double offsetFt, bool structural)
        {
            var atLevel = ArchGeometry.AtElevation(loops, level.Elevation);
            Floor floor;
            try
            {
                floor = Floor.Create(doc, atLevel, type.Id, level.Id, structural, null, 0.0);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("INVALID_GEOMETRY", "Revit refused the floor outline: " + ex.Message);
            }
            if (floor == null)
                throw new ToolException("CREATE_FAILED", "Revit did not create the floor (Floor.Create returned nothing).");
            ArchGeometry.SetDouble(floor, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, offsetFt, "Height Offset From Level");
            doc.Regenerate();
            if (!floor.IsValidObject)
                throw new ToolException("CREATE_FAILED", "Revit removed the floor right after creating it.");
            return floor;
        }

        public static Ceiling CreateCeiling(Document doc, IList<CurveLoop> loops, CeilingType type, Level level, double heightFt)
        {
            var atLevel = ArchGeometry.AtElevation(loops, level.Elevation);
            Ceiling ceiling;
            try
            {
                ceiling = Ceiling.Create(doc, atLevel, type.Id, level.Id);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("INVALID_GEOMETRY", "Revit refused the ceiling outline: " + ex.Message);
            }
            if (ceiling == null)
                throw new ToolException("CREATE_FAILED", "Revit did not create the ceiling (Ceiling.Create returned nothing).");
            ArchGeometry.SetDouble(ceiling, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM, heightFt, "Height Offset From Level");
            doc.Regenerate();
            if (!ceiling.IsValidObject)
                throw new ToolException("CREATE_FAILED", "Revit removed the ceiling right after creating it.");
            return ceiling;
        }

        public static object FloorReadBack(Floor f, ResolvedBoundary b, IList<CurveLoop> loops)
        {
            var area = ArchGeometry.DoubleParam(f, BuiltInParameter.HOST_AREA_COMPUTED);
            return new
            {
                id = f.Id.GetValue(),
                floorType = LookupService.FullName(f.FloorType),
                level = (f.Document.GetElement(f.LevelId) as Level)?.Name,
                heightOffsetMm = ArchGeometry.Mm(ArchGeometry.DoubleParam(f, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)),
                thicknessMm = ArchGeometry.Mm(ArchGeometry.DoubleParam(f, BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM)),
                areaM2 = double.IsNaN(area) ? ArchGeometry.M2(ArchGeometry.RegionArea(loops)) : ArchGeometry.M2(area),
                loops = loops.Count,
                source = b.Source,
                room = b.Room == null ? null : RoomSupport.Summary(b.Room)
            };
        }

        public static object CeilingReadBack(Ceiling c, ResolvedBoundary b, IList<CurveLoop> loops)
        {
            var area = ArchGeometry.DoubleParam(c, BuiltInParameter.HOST_AREA_COMPUTED);
            var type = c.Document.GetElement(c.GetTypeId()) as ElementType;
            return new
            {
                id = c.Id.GetValue(),
                ceilingType = type == null ? null : LookupService.FullName(type),
                level = (c.Document.GetElement(c.LevelId) as Level)?.Name,
                heightAboveLevelMm = ArchGeometry.Mm(ArchGeometry.DoubleParam(c, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)),
                areaM2 = double.IsNaN(area) ? ArchGeometry.M2(ArchGeometry.RegionArea(loops)) : ArchGeometry.M2(area),
                loops = loops.Count,
                source = b.Source,
                room = b.Room == null ? null : RoomSupport.Summary(b.Room)
            };
        }
    }

    /// <summary>ARQ-02: floors by polygon or room boundary, read back (the upstream helper returned null on R25/R26).</summary>
    [McpTool("create_floor", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Create floors (slabs or finish floors) from a polygon in mm (with holes), a rectangle, or a room's " +
                      "boundary (inner finish face by default, optional boundaryOffset). The floor type must be named. " +
                      "heightOffset (mm) is written to 'Height Offset From Level', not baked into the outline. Every " +
                      "floor is read back (id, area m², thickness, offset). One Ctrl+Z undoes the call.")]
    public sealed class CreateFloorTool : McpTool<CreateFloorArgs>
    {
        protected override void Validate(CreateFloorArgs args)
        {
            for (int i = 0; i < args.Items.Count; i++)
                BoundaryResolver.Validate(args.Items[i]?.Boundary, $"items[{i}]");
        }

        protected override object Run(ToolContext ctx, CreateFloorArgs args)
        {
            var doc = ctx.Doc;
            var outcome = ItemRunner.Run(doc, args.Items, args.ContinueOnError, "Floor", (item, index) =>
            {
                var type = TypeFinder.Resolve<FloorType>(doc, item.FloorType, "floor type", t => !t.IsFoundationSlab);
                var boundary = BoundaryResolver.Resolve(doc, item.Boundary, 0);
                var level = SurfaceCreation.LevelFor(ctx, item.Level, boundary, $"items[{index}]");
                var floor = SurfaceCreation.CreateFloor(doc, boundary.Loops, type, level, ArchGeometry.Ft(item.HeightOffset), item.Structural);
                return SurfaceCreation.FloorReadBack(floor, boundary, boundary.Loops);
            });
            return new { created = outcome.Created, failed = outcome.Failed, createdCount = outcome.Created.Count, failedCount = outcome.Failed.Count };
        }
    }

    /// <summary>ARQ-03: ceilings by polygon or room boundary with the height in the right parameter.</summary>
    [McpTool("create_ceiling", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Create ceilings (forros) from a polygon in mm (with holes), a rectangle, or a room's boundary " +
                      "(inner finish face by default, optional boundaryOffset, e.g. -10 for a shadow gap). The ceiling " +
                      "type must be named; heightAboveLevel (mm) goes to 'Height Offset From Level'. Needs Revit 2022+. " +
                      "Every ceiling is read back (id, area m², height). One Ctrl+Z undoes the call.")]
    public sealed class CreateCeilingTool : McpTool<CreateCeilingArgs>
    {
        protected override void Validate(CreateCeilingArgs args)
        {
            for (int i = 0; i < args.Items.Count; i++)
                BoundaryResolver.Validate(args.Items[i]?.Boundary, $"items[{i}]");
        }

        protected override object Run(ToolContext ctx, CreateCeilingArgs args)
        {
            var doc = ctx.Doc;
            var outcome = ItemRunner.Run(doc, args.Items, args.ContinueOnError, "Ceiling", (item, index) =>
            {
                var type = TypeFinder.Resolve<CeilingType>(doc, item.CeilingType, "ceiling type");
                var boundary = BoundaryResolver.Resolve(doc, item.Boundary, 0);
                var level = SurfaceCreation.LevelFor(ctx, item.Level, boundary, $"items[{index}]");
                var ceiling = SurfaceCreation.CreateCeiling(doc, boundary.Loops, type, level, ArchGeometry.Ft(item.HeightAboveLevel));
                return SurfaceCreation.CeilingReadBack(ceiling, boundary, boundary.Loops);
            });
            return new { created = outcome.Created, failed = outcome.Failed, createdCount = outcome.Created.Count, failedCount = outcome.Failed.Count };
        }
    }
}
