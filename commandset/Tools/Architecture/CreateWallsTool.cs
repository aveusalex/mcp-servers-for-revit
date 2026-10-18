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
        [Description("Let the walls join at their ends (corners). Default true. If joining a new wall to an existing one would invalidate the user's dimensions, the call is redone without joining to existing walls (the new walls' own corners stay joined) and a warning says so.")]
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
    public sealed class CreateWallsTool : McpTool<CreateWallsArgs>, IRetryAfterRollback
    {
        // Second attempt after Revit rejected the first because joining to existing walls broke dimensions.
        private bool _avoidJoinsToExisting;
        private CreateWallsArgs _args;

        public bool PrepareRetry(ToolContext ctx, SafeFailuresPreprocessor failures)
        {
            if (_avoidJoinsToExisting || !failures.OnlyDimensionErrors || _args?.Items == null || !_args.Items.Any(i => i.JoinEnds))
                return false;
            _avoidJoinsToExisting = true;
            ctx.Warnings.Add(new ToolWarning(
                "Joining the new walls to existing ones would have invalidated dimensions in the model, so the new walls " +
                "were created without joining to existing walls (their own corners are joined). Join them by hand where needed."));
            return true;
        }

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
            _args = args;
            int walls = 0;
            var existing = _avoidJoinsToExisting
                ? new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().ToList()
                : null;
            var existingIds = existing == null ? null : new HashSet<long>(existing.Select(w => w.Id.GetValue()));
            var outcome = ItemRunner.Run(doc, args.Items, args.ContinueOnError, "Wall item",
                (item, index) => CreateItem(ctx, item, index, n => walls += n));
            if (_avoidJoinsToExisting) KeepAwayFromExisting(doc, existing, existingIds);
            return new
            {
                created = outcome.Created,
                failed = outcome.Failed,
                createdItems = outcome.Created.Count,
                wallCount = walls,
                failedCount = outcome.Failed.Count
            };
        }

        /// <summary>Disallow the join at each end of a new wall that touches a wall that existed before this call.</summary>
        private static void KeepAwayFromExisting(Document doc, List<Wall> existing, HashSet<long> existingIds)
        {
            var created = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => !existingIds.Contains(w.Id.GetValue())).ToList();
            foreach (var wall in created)
            {
                if (!(wall.Location is LocationCurve lc)) continue;
                for (int end = 0; end < 2; end++)
                {
                    var p = lc.Curve.GetEndPoint(end);
                    bool touches = existing.Any(o => o.Location is LocationCurve olc &&
                        olc.Curve.Distance(new XYZ(p.X, p.Y, olc.Curve.GetEndPoint(0).Z)) <= (o.Width + wall.Width) / 2 + 1e-3);
                    if (touches && WallUtils.IsWallJoinAllowedAtEnd(wall, end)) WallUtils.DisallowWallJoinAtEnd(wall, end);
                }
            }
            doc.Regenerate();
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

            for (int w = 0; w < created.Count; w++)
            {
                var wall = created[w];
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

                if (keyRef != WallLocationLine.WallCenterline)
                {
                    // Put the body so the requested reference lies on the given line. Moving a joined wall's curve
                    // mid-transaction made Revit throw InternalException (live test), and switching the location
                    // line alone left the walls centred on the outline. So: recreate the wall directly on its
                    // centerline (the given line shifted toward the exterior by the reference's distance from the
                    // centre; finish interior = +W/2), with the same exterior side, then switch the location line,
                    // which keeps the body and moves the curve onto the reference.
                    double offset = ReferenceOffsetFromCenter(wall, keyRef); // + toward exterior
                    var exterior = wall.Orientation.Normalize();
                    var centre = (Line)line.CreateTransformed(Transform.CreateTranslation(exterior * -offset));
                    doc.Delete(wall.Id);

                    var rebuilt = Step("recreate the wall on its centerline", () =>
                        Wall.Create(doc, centre, type.Id, baseLevel.Id, heightFt, ArchGeometry.Ft(item.BaseOffset), false, item.Structural));
                    if (rebuilt == null)
                        throw new ToolException("CREATE_FAILED", "Revit did not create the wall (Wall.Create returned nothing).");
                    doc.Regenerate();
                    if (rebuilt.Orientation.DotProduct(exterior) < 0) Step("flip the wall", () => { rebuilt.Flip(); return true; });
                    Step("set the location line", () =>
                        rebuilt.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).Set((int)keyRef));
                    wall = rebuilt;
                    created[w] = rebuilt;
                }

                if (topLevel != null)
                {
                    var p = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                    p.Set(topLevel.Id);
                    ArchGeometry.SetDouble(wall, BuiltInParameter.WALL_TOP_OFFSET, ArchGeometry.Ft(item.TopOffset), "Top Offset");
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

        /// <summary>
        /// Signed distance (feet) from the wall's centerline to the given location line, positive toward the
        /// exterior side. Uses the type's compound structure; walls without one only know the finish faces.
        /// </summary>
        /// <summary>Runs one Revit API step and names it in the error if Revit throws.</summary>
        private static T Step<T>(string what, Func<T> action)
        {
            try { return action(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("REVIT_ERROR", $"Revit refused to {what} ({ex.GetType().Name}): {ex.Message}");
            }
        }

        private static double ReferenceOffsetFromCenter(Wall wall, WallLocationLine keyRef)
        {
            // Finish faces are exactly half the width from the centre.
            switch (keyRef)
            {
                case WallLocationLine.WallCenterline: return 0;
                case WallLocationLine.FinishFaceExterior: return wall.Width / 2;
                case WallLocationLine.FinishFaceInterior: return -wall.Width / 2;
            }

            // Core lines depend on the layers: sum them from the exterior side.
            var cs = wall.WallType.GetCompoundStructure();
            if (cs == null) return 0;
            try
            {
                var layers = cs.GetLayers();
                int first = cs.GetFirstCoreLayerIndex(), last = cs.GetLastCoreLayerIndex();
                double toCoreExterior = 0, coreThickness = 0;
                for (int i = 0; i < layers.Count; i++)
                {
                    if (i < first) toCoreExterior += layers[i].Width;
                    else if (i <= last) coreThickness += layers[i].Width;
                }
                double fromExterior;
                switch (keyRef)
                {
                    case WallLocationLine.CoreExterior: fromExterior = toCoreExterior; break;
                    case WallLocationLine.CoreInterior: fromExterior = toCoreExterior + coreThickness; break;
                    case WallLocationLine.CoreCenterline: fromExterior = toCoreExterior + coreThickness / 2; break;
                    default: return 0;
                }
                return wall.Width / 2 - fromExterior;
            }
            catch
            {
                return 0;
            }
        }

        private static object ReadBack(Document doc, Wall w)
        {
            if (!w.IsValidObject)
                throw new ToolException("CREATE_FAILED", "Revit removed a wall right after creating it.");
            var curve = ((LocationCurve)w.Location).Curve;
            var topId = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId();
            var top = topId != null && topId != ElementId.InvalidElementId ? doc.GetElement(topId) as Level : null;
            var keyRef = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? 0;
            // Revit reports the centerline as the location curve; also give the line the caller asked for.
            var toRef = w.Orientation.Normalize() * ReferenceOffsetFromCenter(w, (WallLocationLine)keyRef);
            return new
            {
                id = w.Id.GetValue(),
                start = ArchGeometry.ToWire2(curve.GetEndPoint(0)),
                end = ArchGeometry.ToWire2(curve.GetEndPoint(1)),
                referenceStart = ArchGeometry.ToWire2(curve.GetEndPoint(0) + toRef),
                referenceEnd = ArchGeometry.ToWire2(curve.GetEndPoint(1) + toRef),
                lengthMm = ArchGeometry.Mm(curve.Length),
                widthMm = ArchGeometry.Mm(w.Width),
                locationLine = ((WallLocationLine)keyRef).ToString(),
                baseOffsetMm = ArchGeometry.Mm(ArchGeometry.DoubleParam(w, BuiltInParameter.WALL_BASE_OFFSET)),
                topConstraint = top?.Name ?? "unconnected",
                topOffsetMm = top == null ? (double?)null : ArchGeometry.Mm(ArchGeometry.DoubleParam(w, BuiltInParameter.WALL_TOP_OFFSET)),
                heightMm = ArchGeometry.Mm(ArchGeometry.DoubleParam(w, BuiltInParameter.WALL_USER_HEIGHT_PARAM)),
                // A direction, not a point: unit vector, not converted to mm.
                exteriorDirection = new { x = Math.Round(w.Orientation.X, 6), y = Math.Round(w.Orientation.Y, 6) },
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
