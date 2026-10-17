using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture
{
    public sealed class WallItem
    {
        [JsonProperty("shape")]
        [Required]
        [EnumValues("segment", "polyline", "rectangle")]
        [Description("segment: one wall between 2 points. polyline: one wall per edge of the points (closed adds the last→first edge). rectangle: 4 walls.")]
        public string Shape { get; set; }

        [JsonProperty("points")]
        [MinLength(2)]
        [MaxLength(200)]
        [Description("Plan points in mm (segment: exactly 2; polyline: 2 or more).")]
        public List<PointMm> Points { get; set; }

        [JsonProperty("closed")]
        [DefaultValue(false)]
        [Description("polyline only: also create the wall from the last point back to the first. Default false.")]
        public bool Closed { get; set; }

        [JsonProperty("rectangle")]
        [Description("rectangle only: corner, width, depth and rotation.")]
        public RectangleMm Rectangle { get; set; }

        [JsonProperty("wallType")]
        [Required]
        [Description("Wall type as \"Family: Type\" (e.g. \"Parede básica: Alvenaria 140\"), type name or id. Required: never chosen for you.")]
        public string WallType { get; set; }

        [JsonProperty("baseLevel")]
        [Required]
        [Description("Base level by name or id.")]
        public string BaseLevel { get; set; }

        [JsonProperty("baseOffset")]
        [Unit("mm")]
        [DefaultValue(0)]
        [Description("Base offset from the base level, in mm. Default 0.")]
        public double BaseOffset { get; set; }

        [JsonProperty("topLevel")]
        [Description("Top constraint level by name or id. Give topLevel or height, not both.")]
        public string TopLevel { get; set; }

        [JsonProperty("topOffset")]
        [Unit("mm")]
        [DefaultValue(0)]
        [Description("Top offset from topLevel, in mm (negative = below it). Only with topLevel.")]
        public double TopOffset { get; set; }

        [JsonProperty("height")]
        [Unit("mm")]
        [Range(1, 1000000)]
        [Description("Unconnected height in mm (wall not attached to a top level). Give topLevel or height, not both.")]
        public double? Height { get; set; }

        [JsonProperty("locationLine")]
        [EnumValues("wallCenterline", "coreCenterline", "finishFaceExterior", "finishFaceInterior", "coreFaceExterior", "coreFaceInterior")]
        [DefaultValue("wallCenterline")]
        [Description("Which line of the wall lies on the given points. finishFaceInterior with a closed outline makes the outline the room's inner face. Default wallCenterline.")]
        public string LocationLine { get; set; }

        [JsonProperty("exteriorSide")]
        [EnumValues("outside", "inside", "asDrawn")]
        [DefaultValue("outside")]
        [Description("Closed shapes only: put each wall's exterior face outside the outline (default), inside it, or keep Revit's side from the drawing direction.")]
        public string ExteriorSide { get; set; }

        [JsonProperty("flip")]
        [DefaultValue(false)]
        [Description("Swap the exterior and interior faces of every wall of this item (applied after exteriorSide). Default false.")]
        public bool Flip { get; set; }

        [JsonProperty("structural")]
        [DefaultValue(false)]
        [Description("Create structural walls. Default false (architectural partitions).")]
        public bool Structural { get; set; }

        [JsonProperty("joinEnds")]
        [DefaultValue(true)]
        [Description("Let the walls join at their ends (corners). Default true.")]
        public bool JoinEnds { get; set; } = true;
    }

    public sealed class CreateWallsArgs
    {
        [JsonProperty("items")]
        [Required]
        [MinLength(1)]
        [MaxLength(100)]
        [Description("Walls to create: each item is a segment, a polyline or a rectangle.")]
        public List<WallItem> Items { get; set; }

        [JsonProperty("continueOnError")]
        [DefaultValue(false)]
        [Description("false (default): any failure rolls back the whole call. true: failed items are skipped and listed in failed[].")]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>ARQ-01: walls by segment, polyline or rectangle, with explicit type, top constraint and location line.</summary>
    [McpTool("create_walls", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Create walls by segment, polyline (open or closed) or rectangle, coordinates in mm. The wall type " +
                      "must be named (never defaulted); height is either a top level (+topOffset) or an unconnected " +
                      "height. locationLine says which face/centreline sits on the points (e.g. finishFaceInterior to " +
                      "draw the inner outline of a room); closed shapes put the exterior face outside. Returns ids, " +
                      "lengths and the read-back constraints. One Ctrl+Z undoes the call.")]
    public sealed class CreateWallsTool : McpTool<CreateWallsArgs>
    {
        protected override void Validate(CreateWallsArgs args)
        {
            for (int i = 0; i < args.Items.Count; i++)
            {
                var it = args.Items[i] ?? throw new ToolException("INVALID_ARGUMENTS", $"items[{i}] is null.");
                var shape = it.Shape.ToLowerInvariant();
                if (shape == "segment" && (it.Points == null || it.Points.Count != 2))
                    throw new ToolException("INVALID_ARGUMENTS", $"items[{i}]: a segment needs exactly 2 points.");
                if (shape == "polyline" && (it.Points == null || it.Points.Count < 2))
                    throw new ToolException("INVALID_ARGUMENTS", $"items[{i}]: a polyline needs at least 2 points.");
                if (shape == "polyline" && it.Closed && it.Points.Count < 3)
                    throw new ToolException("INVALID_ARGUMENTS", $"items[{i}]: a closed polyline needs at least 3 points.");
                if (shape == "rectangle" && it.Rectangle == null)
                    throw new ToolException("INVALID_ARGUMENTS", $"items[{i}]: shape rectangle needs rectangle {{origin, width, depth}}.");
                if (shape != "rectangle" && it.Rectangle != null)
                    throw new ToolException("INVALID_ARGUMENTS", $"items[{i}]: rectangle is only used with shape rectangle.");
                bool hasTop = !string.IsNullOrWhiteSpace(it.TopLevel);
                if (hasTop == it.Height.HasValue)
                    throw new ToolException("INVALID_ARGUMENTS", $"items[{i}]: give exactly one of topLevel or height.");
                if (!hasTop && Math.Abs(it.TopOffset) > 1e-9)
                    throw new ToolException("INVALID_ARGUMENTS", $"items[{i}]: topOffset only applies with topLevel.");
            }
        }

        protected override object Run(ToolContext ctx, CreateWallsArgs args)
        {
            var doc = ctx.Doc;
            int walls = 0;
            var outcome = ItemRunner.Run(doc, args.Items, args.ContinueOnError, "Wall item",
                (item, index) => CreateItem(ctx, item, index, n => walls += n));
            return new
            {
                created = outcome.Created,
                failed = outcome.Failed,
                createdItems = outcome.Created.Count,
                wallCount = walls,
                failedCount = outcome.Failed.Count
            };
        }

        private static object CreateItem(ToolContext ctx, WallItem item, int index, Action<int> count)
        {
            var doc = ctx.Doc;
            var type = TypeFinder.Resolve<WallType>(doc, item.WallType, "wall type");
            var baseLevel = ctx.Services.Lookup.Level(item.BaseLevel);
            var topLevel = string.IsNullOrWhiteSpace(item.TopLevel) ? null : ctx.Services.Lookup.Level(item.TopLevel);

            double baseZ = baseLevel.Elevation + ArchGeometry.Ft(item.BaseOffset);
            double heightFt;
            if (topLevel != null)
            {
                double topZ = topLevel.Elevation + ArchGeometry.Ft(item.TopOffset);
                heightFt = topZ - baseZ;
                if (heightFt <= doc.Application.ShortCurveTolerance)
                    throw new ToolException("INVALID_GEOMETRY",
                        $"The top ({topLevel.Name} {ArchGeometry.Fmt(item.TopOffset)}) is not above the base ({baseLevel.Name} {ArchGeometry.Fmt(item.BaseOffset)}): height would be {ArchGeometry.Fmt(ArchGeometry.Mm(heightFt))}.");
            }
            else heightFt = ArchGeometry.Ft(item.Height.Value);

            // Points of the walls, at the base level elevation.
            var shape = item.Shape.ToLowerInvariant();
            IList<XYZ> pts = shape == "rectangle"
                ? ArchGeometry.RectanglePoints(item.Rectangle, baseLevel.Elevation)
                : ArchGeometry.ToXyz(item.Points, baseLevel.Elevation);
            bool closed = shape == "rectangle" || (shape == "polyline" && item.Closed);
            var tol = doc.Application.ShortCurveTolerance;
            if (closed)
            {
                pts = ArchGeometry.CleanPolygon(pts, tol);
                if (pts.Count < 3) throw new ToolException("INVALID_GEOMETRY", "The closed outline needs at least 3 distinct points.");
                if (ArchGeometry.SelfIntersects(pts)) throw new ToolException("INVALID_GEOMETRY", "The closed outline crosses itself.");
            }

            var lines = new List<Line>();
            int edges = closed ? pts.Count : pts.Count - 1;
            for (int i = 0; i < edges; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                if (a.DistanceTo(b) <= tol)
                    throw new ToolException("INVALID_GEOMETRY",
                        $"Edge {i} is {ArchGeometry.Fmt(ArchGeometry.Mm(a.DistanceTo(b)))} long; Revit needs more than {ArchGeometry.Fmt(ArchGeometry.Mm(tol))}.");
                lines.Add(Line.CreateBound(a, b));
            }

            var keyRef = LocationLineOf(item.LocationLine);
            var exteriorSide = (item.ExteriorSide ?? "outside").ToLowerInvariant();
            var created = new List<Wall>();
            foreach (var line in lines)
            {
                var wall = Wall.Create(doc, line, type.Id, baseLevel.Id, heightFt, ArchGeometry.Ft(item.BaseOffset), false, item.Structural);
                if (wall == null)
                    throw new ToolException("CREATE_FAILED", "Revit did not create the wall (Wall.Create returned nothing).");
                created.Add(wall);
            }
            doc.Regenerate();

            foreach (var wall in created)
            {
                var line = (Line)((LocationCurve)wall.Location).Curve;
                bool flip = item.Flip;
                if (closed && exteriorSide != "asdrawn")
                {
                    var mid = line.Evaluate(0.5, true);
                    var probe = mid + wall.Orientation.Normalize() * Math.Min(0.05, line.Length / 10);
                    bool exteriorPointsOut = !ArchGeometry.Contains(pts, probe);
                    bool wantOut = exteriorSide == "outside";
                    if (exteriorPointsOut != wantOut) flip = !flip;
                }
                if (flip) wall.Flip();

                if (topLevel != null)
                {
                    var p = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                    p.Set(topLevel.Id);
                    ArchGeometry.SetDouble(wall, BuiltInParameter.WALL_TOP_OFFSET, ArchGeometry.Ft(item.TopOffset), "Top Offset");
                }

                if (keyRef != WallLocationLine.WallCenterline)
                {
                    // Changing the location line keeps the wall body where it is and moves the location curve to the
                    // new reference; putting the curve back on the requested line moves the body instead.
                    var keyParam = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                    keyParam.Set((int)keyRef);
                    doc.Regenerate();
                    ((LocationCurve)wall.Location).Curve = line;
                }
                if (!item.JoinEnds)
                {
                    WallUtils.DisallowWallJoinAtEnd(wall, 0);
                    WallUtils.DisallowWallJoinAtEnd(wall, 1);
                }
            }
            doc.Regenerate();
            var readBack = created.Select(w => ReadBack(doc, w)).ToList();
            count(created.Count);

            return new
            {
                index,
                wallType = LookupServiceFullName(type),
                kind = type.Kind.ToString(),
                baseLevel = baseLevel.Name,
                topLevel = topLevel?.Name,
                wallCount = created.Count,
                wallIds = created.Select(w => w.Id.GetValue()).ToList(),
                walls = readBack
            };
        }

        private static object ReadBack(Document doc, Wall w)
        {
            if (!w.IsValidObject)
                throw new ToolException("CREATE_FAILED", "Revit removed a wall right after creating it.");
            var curve = ((LocationCurve)w.Location).Curve;
            var topId = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId();
            var top = topId != null && topId != ElementId.InvalidElementId ? doc.GetElement(topId) as Level : null;
            var keyRef = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? 0;
            return new
            {
                id = w.Id.GetValue(),
                start = ArchGeometry.ToWire2(curve.GetEndPoint(0)),
                end = ArchGeometry.ToWire2(curve.GetEndPoint(1)),
                lengthMm = ArchGeometry.Mm(curve.Length),
                widthMm = ArchGeometry.Mm(w.Width),
                locationLine = ((WallLocationLine)keyRef).ToString(),
                baseOffsetMm = ArchGeometry.Mm(ArchGeometry.DoubleParam(w, BuiltInParameter.WALL_BASE_OFFSET)),
                topConstraint = top?.Name ?? "unconnected",
                topOffsetMm = top == null ? (double?)null : ArchGeometry.Mm(ArchGeometry.DoubleParam(w, BuiltInParameter.WALL_TOP_OFFSET)),
                heightMm = ArchGeometry.Mm(ArchGeometry.DoubleParam(w, BuiltInParameter.WALL_USER_HEIGHT_PARAM)),
                exteriorDirection = ArchGeometry.ToWire2(w.Orientation),
                structural = w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1
            };
        }

        private static string LookupServiceFullName(ElementType t) => Framework.Services.LookupService.FullName(t);

        internal static WallLocationLine LocationLineOf(string text)
        {
            switch ((text ?? "wallCenterline").ToLowerInvariant())
            {
                case "corecenterline": return WallLocationLine.CoreCenterline;
                case "finishfaceexterior": return WallLocationLine.FinishFaceExterior;
                case "finishfaceinterior": return WallLocationLine.FinishFaceInterior;
                case "corefaceexterior": return WallLocationLine.CoreExterior;
                case "corefaceinterior": return WallLocationLine.CoreInterior;
                default: return WallLocationLine.WallCenterline;
            }
        }
    }
}
