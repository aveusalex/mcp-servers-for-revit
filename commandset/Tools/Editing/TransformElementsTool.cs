using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Tools.Query;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class MirrorPlaneArg
    {
        [JsonProperty("origin", Required = Required.Always)]
        [Description("A point on the mirror plane, in mm.")]
        public PointMm Origin { get; set; }

        [JsonProperty("normal", Required = Required.Always)]
        [Description("Normal of the mirror plane (e.g. {dx:1,dy:0,dz:0} mirrors across a plane parallel to YZ).")]
        public DirectionArg Normal { get; set; }
    }

    public sealed class TransformElementsArgs
    {
        [JsonProperty("elementIds", Required = Required.Always)]
        [Required]
        [MinLength(1)]
        [MaxLength(2000)]
        [Description("Elements to transform.")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("operation", Required = Required.Always)]
        [Required]
        [Description("move (by vector), copy (copies moved by vector), rotate (angle about an axis), mirror (across a plane or a line element).")]
        [EnumValues("move", "copy", "rotate", "mirror")]
        public string Operation { get; set; }

        [JsonProperty("vector")]
        [Description("Displacement for move / copy, in mm.")]
        public PointMm Vector { get; set; }

        [JsonProperty("angle")]
        [Description("Rotation angle for rotate, counter-clockwise seen from the axis direction.")]
        [Unit("deg")]
        public double? Angle { get; set; }

        [JsonProperty("axisOrigin")]
        [Description("A point on the rotation axis, in mm. Default: centre of the elements' combined bounding box.")]
        public PointMm AxisOrigin { get; set; }

        [JsonProperty("axisDirection")]
        [Description("Direction of the rotation axis. Default vertical (0,0,1).")]
        public DirectionArg AxisDirection { get; set; }

        [JsonProperty("mirrorPlane")]
        [Description("Mirror plane for mirror: origin + normal.")]
        public MirrorPlaneArg MirrorPlane { get; set; }

        [JsonProperty("mirrorLineElementId")]
        [Description("Alternative to mirrorPlane: a grid, reference plane or line element; mirrors across the vertical plane through it.")]
        public long? MirrorLineElementId { get; set; }

        [JsonProperty("copy")]
        [Description("rotate / mirror: transform copies and keep the originals. Default false.")]
        [DefaultValue(false)]
        public bool Copy { get; set; }
    }

    /// <summary>EDT-01: move, copy, rotate or mirror elements.</summary>
    [McpTool("transform_elements", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "editing",
        Description = "Move, copy, rotate or mirror elements in one transaction. Distances and points in mm, angles in " +
                      "degrees. rotate defaults to a vertical axis through the centre of the elements; mirror takes a " +
                      "plane (origin + normal) or a grid / reference plane / line element. copy:true keeps the originals " +
                      "for rotate/mirror. Pinned elements are refused (unpin with set_elements_pinned). Returns the ids " +
                      "of new copies and the read-back locations. Undo: Ctrl+Z.")]
    public sealed class TransformElementsTool : McpTool<TransformElementsArgs>
    {
        protected override void Validate(TransformElementsArgs a)
        {
            switch (a.Operation)
            {
                case "move":
                case "copy":
                    if (a.Vector == null) throw new ToolException("INVALID_ARGUMENTS", $"{a.Operation} needs vector (mm).");
                    break;
                case "rotate":
                    if (!a.Angle.HasValue) throw new ToolException("INVALID_ARGUMENTS", "rotate needs angle (degrees).");
                    break;
                case "mirror":
                    if ((a.MirrorPlane == null) == (a.MirrorLineElementId == null))
                        throw new ToolException("INVALID_ARGUMENTS", "mirror needs exactly one of mirrorPlane or mirrorLineElementId.");
                    break;
            }
        }

        protected override object Run(ToolContext ctx, TransformElementsArgs a)
        {
            var doc = ctx.Doc;
            var elements = EditGeometry.Elements(doc, a.ElementIds);
            var ids = elements.Select(e => e.Id).ToList();

            if (a.Operation != "copy" && !(a.Copy && (a.Operation == "rotate" || a.Operation == "mirror")))
            {
                var pinned = elements.Where(e => e.Pinned).Select(e => e.Id.GetValue()).ToList();
                if (pinned.Count > 0)
                    throw new ToolException("ELEMENT_PINNED", $"{pinned.Count} element(s) are pinned: {string.Join(", ", pinned.Take(20))}.",
                        "Unpin them with set_elements_pinned first (or copy instead).", "set_elements_pinned");
            }

            ICollection<ElementId> created = new List<ElementId>();
            string summary;
            try
            {
                switch (a.Operation)
                {
                    case "move":
                    {
                        var v = EditGeometry.ToXyz(a.Vector);
                        ElementTransformUtils.MoveElements(doc, ids, v);
                        summary = $"Moved {ids.Count} element(s).";
                        break;
                    }
                    case "copy":
                    {
                        var v = EditGeometry.ToXyz(a.Vector);
                        created = ElementTransformUtils.CopyElements(doc, ids, v);
                        summary = $"Copied {ids.Count} element(s) into {created.Count} new element(s).";
                        break;
                    }
                    case "rotate":
                    {
                        var origin = a.AxisOrigin != null ? EditGeometry.ToXyz(a.AxisOrigin) : EditGeometry.Center(elements)
                                     ?? throw new ToolException("NO_LOCATION", "The elements have no location; pass axisOrigin.");
                        var dir = a.AxisDirection != null ? EditGeometry.ToDirection(a.AxisDirection, "axisDirection") : XYZ.BasisZ;
                        var axis = Line.CreateUnbound(origin, dir);
                        var targets = ids;
                        if (a.Copy)
                        {
                            created = ElementTransformUtils.CopyElements(doc, ids, XYZ.Zero);
                            targets = created.ToList();
                        }
                        ElementTransformUtils.RotateElements(doc, targets, axis, a.Angle.Value * Math.PI / 180.0);
                        summary = $"Rotated {(a.Copy ? "copies of " : string.Empty)}{ids.Count} element(s) by {a.Angle.Value}°.";
                        break;
                    }
                    default:
                    {
                        var plane = MirrorPlane(ctx, a);
                        if (!ElementTransformUtils.CanMirrorElements(doc, ids))
                            throw new ToolException("CANNOT_MIRROR", "Revit can't mirror this set of elements (some element types don't support mirroring).");
                        created = ElementTransformUtils.MirrorElements(doc, ids, plane, a.Copy);
                        summary = a.Copy ? $"Mirrored copies: {created.Count} new element(s)." : $"Mirrored {ids.Count} element(s).";
                        break;
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("TRANSFORM_FAILED", $"Revit refused the {a.Operation}: {ex.Message}");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                throw new ToolException("TRANSFORM_FAILED", $"Revit refused the {a.Operation}: {ex.Message}");
            }

            var report = (created.Count > 0 ? created.Select(id => doc.GetElement(id)) : elements)
                .Where(e => e != null && e.IsValidObject).Take(500)
                .Select(e => new { id = e.Id.GetValue(), location = EditGeometry.Location(e) }).ToList();
            return new
            {
                operation = a.Operation,
                elementIds = ids.Select(i => i.GetValue()),
                newIds = created.Select(i => i.GetValue()),
                locations = report,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + summary
            };
        }

        private static Plane MirrorPlane(ToolContext ctx, TransformElementsArgs a)
        {
            if (a.MirrorPlane != null)
                return Plane.CreateByNormalAndOrigin(EditGeometry.ToDirection(a.MirrorPlane.Normal, "mirrorPlane.normal"),
                    EditGeometry.ToXyz(a.MirrorPlane.Origin));

            var line = ctx.Doc.GetElement(a.MirrorLineElementId.Value.ToRevitElementId());
            if (line == null) throw new ToolException("ELEMENT_NOT_FOUND", $"Mirror line element {a.MirrorLineElementId} not found.");
            switch (line)
            {
                case ReferencePlane rp:
                    return Plane.CreateByNormalAndOrigin(rp.Normal, rp.BubbleEnd);
                case Grid g when g.Curve is Line:
                    return VerticalPlaneThrough(g.Curve);
                case CurveElement ce when ce.GeometryCurve is Line:
                    return VerticalPlaneThrough(ce.GeometryCurve);
                default:
                    if (line.Location is LocationCurve lc && lc.Curve is Line) return VerticalPlaneThrough(lc.Curve);
                    throw new ToolException("INVALID_MIRROR_LINE", $"Element {a.MirrorLineElementId} is not a straight grid, reference plane or line.",
                        "Pass mirrorPlane {origin, normal} instead.");
            }
        }

        private static Plane VerticalPlaneThrough(Curve curve)
        {
            var p0 = curve.GetEndPoint(0);
            var dir = (curve.GetEndPoint(1) - p0).Normalize();
            var normal = dir.CrossProduct(XYZ.BasisZ);
            if (normal.GetLength() < 1e-9)
                throw new ToolException("INVALID_MIRROR_LINE", "The mirror line is vertical; pass mirrorPlane instead.");
            return Plane.CreateByNormalAndOrigin(normal.Normalize(), p0);
        }
    }
}
