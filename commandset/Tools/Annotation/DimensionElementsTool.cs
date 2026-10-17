using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Annotation.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Annotation
{
    public sealed class DimensionTarget
    {
        [JsonProperty("element", Required = Required.Always)]
        [Description("Wall, grid, reference plane or family instance id.")]
        public long Element { get; set; }

        [JsonProperty("side")]
        [Description("Which reference: walls exterior (default) | interior face; family instances centerLeftRight (default) | centerFrontBack | left | right | front | back. Ignored for grids and reference planes.")]
        [EnumValues("exterior", "interior", "centerLeftRight", "centerFrontBack", "left", "right", "front", "back")]
        public string Side { get; set; }
    }

    public sealed class DimensionLineMm
    {
        [JsonProperty("start", Required = Required.Always)]
        [Description("Start of the dimension line in the view plane (mm).")]
        public ViewPointMm Start { get; set; }

        [JsonProperty("end", Required = Required.Always)]
        [Description("End of the dimension line in the view plane (mm).")]
        public ViewPointMm End { get; set; }
    }

    public sealed class DimensionElementsArgs
    {
        [JsonProperty("view", Required = Required.Always)]
        [Description("Plan, section or elevation view (name or id). Not 3D views: Revit accepts the call there but draws nothing.")]
        public string View { get; set; }

        [JsonProperty("targets", Required = Required.Always)]
        [Description("2 to 50 parallel elements to dimension, in any order (a chain dimension with one segment per gap).")]
        public List<DimensionTarget> Targets { get; set; }

        [JsonProperty("line")]
        [Description("Dimension line in the view plane. Default: perpendicular to the targets, at offsetMm from the first target's midpoint.")]
        public DimensionLineMm Line { get; set; }

        [JsonProperty("offsetMm")]
        [Description("Default line only: distance along the targets' direction from the first target's midpoint (model mm, default 0).")]
        [Unit("mm")]
        [DefaultValue(0)]
        public double OffsetMm { get; set; }

        [JsonProperty("dimensionType")]
        [Description("Linear dimension type name or id. Default: the document's default.")]
        public string DimensionType { get; set; }
    }

    /// <summary>
    /// ANO-04: aligned chain dimensions between wall faces, grids, reference planes and family instance references.
    /// The legacy create_dimensions (point-based) is kept; this is the reference-based one.
    /// </summary>
    [McpTool("dimension_elements", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "annotation",
        Description = "Create an aligned chain dimension between parallel walls (exterior/interior face), grids, " +
                      "reference planes or family instance references (center, left/right, front/back) in a plan, " +
                      "section or elevation. The dimension line is computed perpendicular to the targets (or given in " +
                      "the view plane in mm). Returns the dimension id and each segment value in mm, read back. Not " +
                      "for 3D views. Undo: Ctrl+Z or delete the returned id.")]
    public sealed class DimensionElementsTool : McpTool<DimensionElementsArgs>
    {
        protected override void Validate(DimensionElementsArgs args)
        {
            if (args.Targets == null || args.Targets.Count < 2)
                throw new ToolException("INVALID_ARGUMENTS", "targets must list at least 2 elements.");
            if (args.Targets.Count > 50)
                throw new ToolException("INVALID_ARGUMENTS", "At most 50 targets per dimension.");
            if (args.Targets.Any(t => t == null))
                throw new ToolException("INVALID_ARGUMENTS", "targets must not contain null.");
            if (args.Targets.Select(t => t.Element + ":" + (t.Side ?? "")).Distinct().Count() != args.Targets.Count)
                throw new ToolException("INVALID_ARGUMENTS", "The same reference is listed twice.");
        }

        protected override object Run(ToolContext ctx, DimensionElementsArgs args)
        {
            var doc = ctx.Doc;
            var view = ViewPlane.RequireAnnotatable(ctx, args.View, false, "a dimension");
            if (view is View3D)
                throw new ToolException("VIEW_NOT_SUPPORTED", "Dimensions are not drawn in 3D views through the API.", "Use a plan, section or elevation.");

            var refs = new ReferenceArray();
            var axes = new List<(XYZ origin, XYZ direction)>();
            foreach (var t in args.Targets)
            {
                var e = ctx.Services.Lookup.Element(t.Element);
                var (reference, origin, direction) = ReferenceOf(e, t.Side, view);
                refs.Append(reference);
                axes.Add((origin, direction));
            }

            // Every target must be parallel to the first one (in the view plane).
            var dir = Flatten(axes[0].direction, view);
            for (var i = 1; i < axes.Count; i++)
            {
                var d = Flatten(axes[i].direction, view);
                if (Math.Abs(Math.Abs(dir.DotProduct(d)) - 1) > 1e-3)
                    throw new ToolException("NOT_PARALLEL", $"Target [{i}] ({args.Targets[i].Element}) is not parallel to target [0].",
                        "Dimension parallel walls/grids only, or split the call.");
            }

            Line line;
            if (args.Line != null)
            {
                var a = ViewPlane.ToModel(view, args.Line.Start.X, args.Line.Start.Y);
                var b = ViewPlane.ToModel(view, args.Line.End.X, args.Line.End.Y);
                if (a.DistanceTo(b) < 1e-3)
                    throw new ToolException("INVALID_ARGUMENTS", "The dimension line is too short.");
                line = Line.CreateBound(a, b);
            }
            else
            {
                var normal = view.ViewDirection.CrossProduct(dir).Normalize();
                var basePoint = axes[0].origin + dir * UnitPolicy.ToInternal(args.OffsetMm, UnitSpec.Length);
                var along = axes.Select(x => (x.origin - basePoint).DotProduct(normal)).ToList();
                var pad = UnitPolicy.ToInternal(500, UnitSpec.Length);
                var min = along.Min() - pad;
                var max = along.Max() + pad;
                var a = ProjectToPlane(basePoint + normal * min, view);
                var b = ProjectToPlane(basePoint + normal * max, view);
                line = Line.CreateBound(a, b);
            }

            DimensionType dimType = null;
            if (!string.IsNullOrWhiteSpace(args.DimensionType))
            {
                var types = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                    .Where(d => d.StyleType == DimensionStyleType.Linear || d.StyleType == DimensionStyleType.LinearFixed).ToList();
                dimType = ctx.Services.Lookup.ByNameOrId(args.DimensionType, types, "dimension type");
            }

            Dimension dim;
            try
            {
                dim = dimType == null
                    ? doc.Create.NewDimension(view, line, refs)
                    : doc.Create.NewDimension(view, line, refs, dimType);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("DIMENSION_FAILED", "Revit could not create the dimension: " + ex.Message,
                    "The references must be parallel and visible in the view; the line must lie in the view plane.");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                throw new ToolException("DIMENSION_FAILED", "Revit could not create the dimension: " + ex.Message);
            }
            if (dim == null) throw new ToolException("DIMENSION_FAILED", "Revit did not create the dimension.");

            doc.Regenerate();
            var segments = new List<double?>();
            if (dim.NumberOfSegments > 0)
            {
                foreach (DimensionSegment s in dim.Segments)
                    segments.Add(s.Value.HasValue ? Math.Round(UnitPolicy.FromInternal(s.Value.Value, UnitSpec.Length, out _), 1) : (double?)null);
            }
            else
            {
                segments.Add(dim.Value.HasValue ? Math.Round(UnitPolicy.FromInternal(dim.Value.Value, UnitSpec.Length, out _), 1) : (double?)null);
            }

            return new
            {
                view = ViewPlane.DescribeView(view),
                dimensionId = dim.Id.GetValue(),
                dimensionType = doc.GetElement(dim.GetTypeId())?.Name,
                segmentsMm = segments,
                line = new { start = ViewPlane.FromModel(view, line.GetEndPoint(0)), end = ViewPlane.FromModel(view, line.GetEndPoint(1)) }
            };
        }

        private static (Reference reference, XYZ origin, XYZ direction) ReferenceOf(Element e, string side, View view)
        {
            switch (e)
            {
                case Wall wall:
                {
                    if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Line wl))
                        throw new ToolException("UNSUPPORTED_TARGET", $"Wall {wall.Id.GetValue()} is not straight.");
                    var layer = string.Equals(side, "interior", StringComparison.OrdinalIgnoreCase) ? ShellLayerType.Interior : ShellLayerType.Exterior;
                    if (!string.IsNullOrEmpty(side) && !string.Equals(side, "interior", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(side, "exterior", StringComparison.OrdinalIgnoreCase))
                        throw new ToolException("INVALID_ARGUMENTS", $"side '{side}' does not apply to walls (exterior or interior).");
                    IList<Reference> faces;
                    try { faces = HostObjectUtils.GetSideFaces(wall, layer); }
                    catch (Autodesk.Revit.Exceptions.ArgumentException) { faces = null; }
                    var face = faces?.FirstOrDefault();
                    if (face == null)
                        throw new ToolException("UNSUPPORTED_TARGET", $"Wall {wall.Id.GetValue()} has no {layer.ToString().ToLowerInvariant()} face (curtain or stacked wall?).");
                    var offset = wall.Width / 2 * (layer == ShellLayerType.Exterior ? 1 : -1);
                    var origin = wl.Evaluate(0.5, true) + wall.Orientation * offset;
                    return (face, origin, wl.Direction);
                }
                case Grid grid:
                {
                    if (!(grid.Curve is Line gl))
                        throw new ToolException("UNSUPPORTED_TARGET", $"Grid '{grid.Name}' is not straight.");
                    return (new Reference(grid), gl.Evaluate(0.5, true), gl.Direction);
                }
                case ReferencePlane rp:
                {
                    var origin = (rp.BubbleEnd + rp.FreeEnd) / 2;
                    return (rp.GetReference(), origin, (rp.FreeEnd - rp.BubbleEnd).Normalize());
                }
                case FamilyInstance fi:
                {
                    var kind = FamilyReferenceType(side);
                    var reference = fi.GetReferences(kind).FirstOrDefault();
                    if (reference == null)
                        throw new ToolException("UNSUPPORTED_TARGET", $"Family instance {fi.Id.GetValue()} has no {side ?? "centerLeftRight"} reference.",
                            "Try another side (centerFrontBack, left, right, front, back).");
                    var origin = (fi.Location as LocationPoint)?.Point ?? Center(fi, view);
                    // Left/right references run along the facing direction; front/back along the hand direction.
                    var lr = kind == FamilyInstanceReferenceType.CenterLeftRight || kind == FamilyInstanceReferenceType.Left || kind == FamilyInstanceReferenceType.Right;
                    var direction = lr ? fi.FacingOrientation : fi.HandOrientation;
                    if (fi.Location is LocationCurve flc && flc.Curve is Line fl)
                    {
                        origin = fl.Evaluate(0.5, true);
                        direction = lr ? fl.Direction.CrossProduct(XYZ.BasisZ) : fl.Direction;
                    }
                    return (reference, origin, direction);
                }
                default:
                    throw new ToolException("UNSUPPORTED_TARGET",
                        $"Element {e.Id.GetValue()} ({e.Category?.Name}) is not a wall, grid, reference plane or family instance.");
            }
        }

        private static FamilyInstanceReferenceType FamilyReferenceType(string side)
        {
            switch ((side ?? "centerLeftRight").Trim().ToLowerInvariant())
            {
                case "centerleftright": return FamilyInstanceReferenceType.CenterLeftRight;
                case "centerfrontback": return FamilyInstanceReferenceType.CenterFrontBack;
                case "left": return FamilyInstanceReferenceType.Left;
                case "right": return FamilyInstanceReferenceType.Right;
                case "front": return FamilyInstanceReferenceType.Front;
                case "back": return FamilyInstanceReferenceType.Back;
                default: throw new ToolException("INVALID_ARGUMENTS", $"side '{side}' does not apply to family instances.");
            }
        }

        private static XYZ Center(Element e, View view)
        {
            var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
            if (bb == null) throw new ToolException("NO_LOCATION", $"Element {e.Id.GetValue()} has no location.");
            return (bb.Min + bb.Max) / 2;
        }

        /// <summary>The direction projected onto the view plane, normalized.</summary>
        private static XYZ Flatten(XYZ d, View view)
        {
            var n = view.ViewDirection;
            var p = d - n * d.DotProduct(n);
            if (p.GetLength() < 1e-9)
                throw new ToolException("NOT_PARALLEL", "A target runs along the view direction and cannot be dimensioned in this view.");
            return p.Normalize();
        }

        private static XYZ ProjectToPlane(XYZ p, View view)
        {
            var n = view.ViewDirection;
            var o = view.Origin;
            if (view is ViewPlan plan && plan.GenLevel != null)
                return new XYZ(p.X, p.Y, plan.GenLevel.Elevation);
            return p - n * (p - o).DotProduct(n);
        }
    }
}
