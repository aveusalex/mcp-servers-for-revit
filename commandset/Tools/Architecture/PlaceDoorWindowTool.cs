using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture
{
    public sealed class DoorWindowItem
    {
        [JsonProperty("wallId")]
        [Required]
        [Description("Id of the host wall.")]
        public long WallId { get; set; }

        [JsonProperty("familyType")]
        [Required]
        [Description("Door or window type as \"Family: Type\", type name or id. An ambiguous name returns placed:false with candidates.")]
        public string FamilyType { get; set; }

        [JsonProperty("alongWall")]
        [Unit("mm")]
        [Description("Distance in mm from the wall's start point to the centre of the opening. Give alongWall or point.")]
        public double? AlongWall { get; set; }

        [JsonProperty("point")]
        [Description("Plan point in mm near the wall; it is projected onto the wall's location line. Give alongWall or point.")]
        public PointMm Point { get; set; }

        [JsonProperty("sillHeight")]
        [Unit("mm")]
        [Description("Sill height in mm above the level (windows; doors usually 0). Omit to keep the family's default.")]
        public double? SillHeight { get; set; }

        [JsonProperty("level")]
        [Description("Level by name or id. Default: the wall's base level.")]
        public string Level { get; set; }

        [JsonProperty("flipFacing")]
        [DefaultValue(false)]
        [Description("Flip to open to the other side of the wall. Default false.")]
        public bool FlipFacing { get; set; }

        [JsonProperty("flipHand")]
        [DefaultValue(false)]
        [Description("Swap the hinge side. Default false.")]
        public bool FlipHand { get; set; }

        [JsonProperty("allowOverlap")]
        [DefaultValue(false)]
        [Description("Skip the overlap check against other doors/windows in the wall. Default false.")]
        public bool AllowOverlap { get; set; }

        [JsonProperty("parameters")]
        [Description("Instance parameters to set after placing, by name (e.g. {\"Marca\": \"P01\"}).")]
        public Dictionary<string, JToken> Parameters { get; set; }
    }

    public sealed class PlaceDoorWindowArgs
    {
        [JsonProperty("items")]
        [Required]
        [MinLength(1)]
        [MaxLength(200)]
        [Description("Doors/windows to place.")]
        public List<DoorWindowItem> Items { get; set; }

        [JsonProperty("continueOnError")]
        [DefaultValue(false)]
        [Description("false (default): any failure rolls back the whole call. true: failed items are skipped and listed in failed[].")]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>ARQ-07: doors and windows in walls, with a fit check explained in millimetres.</summary>
    [McpTool("place_door_window", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Place doors or windows in a wall, at a distance along the wall (mm from its start to the opening " +
                      "centre) or at a point projected onto it, with optional sill height (mm). Before placing, checks that " +
                      "the opening fits in the wall's clear length (corners deducted) and does not overlap other " +
                      "openings, and refuses with the numbers in mm. Curtain walls are refused. Read-back: host, sill, " +
                      "head, position. One Ctrl+Z undoes the call.")]
    public sealed class PlaceDoorWindowTool : McpTool<PlaceDoorWindowArgs>
    {
        private static readonly List<BuiltInCategory> Categories = new List<BuiltInCategory> { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows };

        protected override void Validate(PlaceDoorWindowArgs args)
        {
            for (int i = 0; i < args.Items.Count; i++)
            {
                var it = args.Items[i] ?? throw new ToolException("INVALID_ARGUMENTS", $"items[{i}] is null.");
                if (it.AlongWall.HasValue == (it.Point != null))
                    throw new ToolException("INVALID_ARGUMENTS", $"items[{i}]: give exactly one of alongWall or point.");
            }
        }

        protected override object Run(ToolContext ctx, PlaceDoorWindowArgs args)
        {
            var doc = ctx.Doc;
            var notPlaced = new List<object>();
            var outcome = ItemRunner.Run(doc, args.Items, args.ContinueOnError, "Opening", (item, index) =>
            {
                var symbol = TypeFinder.Symbol(doc, item.FamilyType, Categories, out var candidates);
                if (symbol == null)
                {
                    var skipped = new { index, placed = false, reason = "AMBIGUOUS_NAME", message = $"'{item.FamilyType}' matches several door/window types; pick one.", candidates };
                    notPlaced.Add(skipped);
                    return skipped;
                }
                return PlaceOne(ctx, symbol, item, index);
            });
            var placed = outcome.Created.Where(c => !notPlaced.Contains(c)).ToList();
            return new
            {
                created = placed,
                notPlaced,
                failed = outcome.Failed,
                createdCount = placed.Count,
                notPlacedCount = notPlaced.Count,
                failedCount = outcome.Failed.Count
            };
        }

        private static object PlaceOne(ToolContext ctx, FamilySymbol symbol, DoorWindowItem item, int index)
        {
            var doc = ctx.Doc;
            var wall = doc.GetElement(item.WallId.ToRevitElementId()) as Wall
                       ?? throw new ToolException("ELEMENT_NOT_FOUND", $"{item.WallId} is not a wall in the target document.");
            if (wall.WallType.Kind == WallKind.Curtain)
                throw new ToolException("WALL_NOT_SUPPORTED", $"Wall {item.WallId} is a curtain wall; doors/windows go into curtain panels, not this tool.");
            if (wall.WallType.Kind == WallKind.Stacked)
                ctx.Warnings.Add(new ToolWarning($"Wall {item.WallId} is a stacked wall; check the opening in each sub-wall.", new List<long> { item.WallId }));
            var curve = (wall.Location as LocationCurve)?.Curve
                        ?? throw new ToolException("WALL_NOT_SUPPORTED", $"Wall {item.WallId} has no location line.");

            var length = curve.Length;
            double along;
            double offAxisMm = 0;
            if (item.AlongWall.HasValue)
            {
                along = ArchGeometry.Ft(item.AlongWall.Value);
            }
            else
            {
                var probe = new XYZ(ArchGeometry.Ft(item.Point.X), ArchGeometry.Ft(item.Point.Y), curve.GetEndPoint(0).Z);
                var proj = curve.Project(probe);
                along = curve.ComputeNormalizedParameter(proj.Parameter) * length;
                offAxisMm = ArchGeometry.Mm(proj.Distance);
                if (proj.Distance > wall.Width / 2 + ArchGeometry.Ft(1))
                    ctx.Warnings.Add(new ToolWarning($"Item {index}: the point is {ArchGeometry.Fmt(offAxisMm)} from the wall's location line; it was projected onto the wall.",
                        new List<long> { item.WallId }));
            }

            var width = OpeningWidth(symbol);
            var check = FitCheck(wall, curve, along, width, item.AllowOverlap);
            if (check != null) throw check;

            Level level = string.IsNullOrWhiteSpace(item.Level) ? doc.GetElement(wall.LevelId) as Level : ctx.Services.Lookup.Level(item.Level);
            if (level == null) throw new ToolException("INVALID_ARGUMENTS", "The wall has no base level; pass level.");

            TypeFinder.Activate(doc, symbol);
            var at = curve.Evaluate(along / length, true);
            at = new XYZ(at.X, at.Y, level.Elevation);
            var instance = doc.Create.NewFamilyInstance(at, symbol, wall, level, StructuralType.NonStructural)
                           ?? throw new ToolException("CREATE_FAILED", "Revit did not place the opening.");
            doc.Regenerate();

            if (item.SillHeight.HasValue)
                ArchGeometry.SetDouble(instance, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM, ArchGeometry.Ft(item.SillHeight.Value), "Sill Height");
            if (item.FlipFacing)
            {
                if (!instance.CanFlipFacing) throw new ToolException("FLIP_NOT_SUPPORTED", "This family cannot flip its facing.");
                instance.flipFacing();
            }
            if (item.FlipHand)
            {
                if (!instance.CanFlipHand) throw new ToolException("FLIP_NOT_SUPPORTED", "This family cannot flip its hand.");
                instance.flipHand();
            }
            var parameters = ParamWriter.Apply(ctx, instance, item.Parameters);
            doc.Regenerate();
            if (!instance.IsValidObject || instance.Host == null || instance.Host.Id != wall.Id)
                throw new ToolException("CREATE_FAILED", "Revit did not keep the opening in the wall.");

            var sill = ArchGeometry.DoubleParam(instance, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            var head = ArchGeometry.DoubleParam(instance, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM);
            var wallHeight = ArchGeometry.DoubleParam(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (!double.IsNaN(head) && !double.IsNaN(wallHeight) && head > wallHeight + 1e-6)
                ctx.Warnings.Add(new ToolWarning($"Item {index}: the head ({ArchGeometry.Fmt(ArchGeometry.Mm(head))}) is above the wall height ({ArchGeometry.Fmt(ArchGeometry.Mm(wallHeight))}).",
                    new List<long> { instance.Id.GetValue() }));

            var lp = (instance.Location as LocationPoint)?.Point;
            return new
            {
                index,
                placed = true,
                id = instance.Id.GetValue(),
                familyType = LookupService.FullName(instance.Symbol),
                category = instance.Category?.Name,
                wallId = wall.Id.GetValue(),
                level = level.Name,
                alongWallMm = ArchGeometry.Mm(along),
                widthMm = width.HasValue ? ArchGeometry.Mm(width.Value) : (double?)null,
                wallLengthMm = ArchGeometry.Mm(length),
                sillHeightMm = double.IsNaN(sill) ? (double?)null : ArchGeometry.Mm(sill),
                headHeightMm = double.IsNaN(head) ? (double?)null : ArchGeometry.Mm(head),
                location = lp == null ? null : ArchGeometry.ToWire(lp),
                facingFlipped = instance.FacingFlipped,
                handFlipped = instance.HandFlipped,
                fitChecked = width.HasValue,
                parameters
            };
        }

        /// <summary>Width of the opening from the type (or the family's width parameter), in feet; null when unknown.</summary>
        internal static double? OpeningWidth(Element typeOrInstance)
        {
            foreach (var bip in new[] { BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM })
            {
                var v = ArchGeometry.DoubleParam(typeOrInstance, bip);
                if (!double.IsNaN(v) && v > 0) return v;
            }
            if (typeOrInstance is FamilyInstance fi) return OpeningWidth(fi.Symbol);
            return null;
        }

        /// <summary>Null when it fits; otherwise the refusal with the numbers in mm.</summary>
        private static ToolException FitCheck(Wall wall, Curve curve, double along, double? width, bool allowOverlap)
        {
            var length = curve.Length;
            if (along < 0 || along > length)
                return new ToolException("OPENING_DOES_NOT_FIT",
                    $"The opening centre at {ArchGeometry.Fmt(ArchGeometry.Mm(along))} is outside the wall, which runs from 0 to {ArchGeometry.Fmt(ArchGeometry.Mm(length))}.",
                    "alongWall is measured from the wall's start point (see the wall's location line).");
            if (!width.HasValue) return null;

            double half = width.Value / 2;
            // Joined walls at each end take part of the length (a corner): deduct half of their thickness.
            double startClear = JoinedHalfWidth(wall, 0);
            double endClear = length - JoinedHalfWidth(wall, 1);
            double from = along - half, to = along + half;
            if (from < startClear - 1e-6 || to > endClear + 1e-6)
                return new ToolException("OPENING_DOES_NOT_FIT",
                    $"An opening {ArchGeometry.Fmt(ArchGeometry.Mm(width.Value))} wide centred at {ArchGeometry.Fmt(ArchGeometry.Mm(along))} would run from " +
                    $"{ArchGeometry.Fmt(ArchGeometry.Mm(from))} to {ArchGeometry.Fmt(ArchGeometry.Mm(to))}, but the wall's clear length is " +
                    $"{ArchGeometry.Fmt(ArchGeometry.Mm(startClear))} to {ArchGeometry.Fmt(ArchGeometry.Mm(endClear))} (of {ArchGeometry.Fmt(ArchGeometry.Mm(length))}).",
                    $"Use alongWall between {ArchGeometry.Fmt(ArchGeometry.Mm(startClear + half))} and {ArchGeometry.Fmt(ArchGeometry.Mm(endClear - half))}, or a narrower type.");

            if (allowOverlap) return null;
            foreach (var id in wall.FindInserts(true, false, false, false))
            {
                if (!(wall.Document.GetElement(id) is FamilyInstance other) || !(other.Location is LocationPoint olp)) continue;
                var ow = OpeningWidth(other);
                if (!ow.HasValue) continue;
                var proj = curve.Project(new XYZ(olp.Point.X, olp.Point.Y, curve.GetEndPoint(0).Z));
                var oAlong = curve.ComputeNormalizedParameter(proj.Parameter) * length;
                double oFrom = oAlong - ow.Value / 2, oTo = oAlong + ow.Value / 2;
                if (from < oTo - 1e-6 && to > oFrom + 1e-6)
                    return new ToolException("OPENING_OVERLAPS",
                        $"The new opening ({ArchGeometry.Fmt(ArchGeometry.Mm(from))}–{ArchGeometry.Fmt(ArchGeometry.Mm(to))} along the wall) overlaps " +
                        $"{LookupService.FullName(other.Symbol)} (id {other.Id.GetValue()}) at {ArchGeometry.Fmt(ArchGeometry.Mm(oFrom))}–{ArchGeometry.Fmt(ArchGeometry.Mm(oTo))}.",
                        "Move it along the wall, or pass allowOverlap:true if the openings are at different heights.");
            }
            return null;
        }

        private static double JoinedHalfWidth(Wall wall, int end)
        {
            try
            {
                var lc = (LocationCurve)wall.Location;
                double w = 0;
                foreach (Element e in lc.get_ElementsAtJoin(end))
                {
                    if (e is Wall other && other.Id != wall.Id) w = Math.Max(w, other.Width / 2);
                }
                return w;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return 0;
            }
        }
    }
}
