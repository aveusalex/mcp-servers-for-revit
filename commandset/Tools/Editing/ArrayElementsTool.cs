using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Editing.M1Support;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Tools.Query;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class ArrayElementsArgs
    {
        [JsonProperty("elementIds", Required = Required.Always)]
        [Required]
        [MinLength(1)]
        [MaxLength(500)]
        [Description("Elements to array (copied together as one set).")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("type", Required = Required.Always)]
        [Required]
        [EnumValues("linear", "radial")]
        [Description("linear (copies along a vector) or radial (copies around an axis).")]
        public string Type { get; set; }

        [JsonProperty("count", Required = Required.Always)]
        [Required]
        [System.ComponentModel.DataAnnotations.Range(2, 200)]
        [Description("Number of members INCLUDING the originals, like Revit's Array (2-200). count 5 = originals + 4 copies.")]
        public int Count { get; set; }

        [JsonProperty("measure")]
        [EnumValues("spacing", "lastCopy")]
        [DefaultValue("spacing")]
        [Description("spacing (default): vector/angle is the step between neighbours. lastCopy: vector/angle reaches the last member and the copies are spread evenly (a 360° radial array with lastCopy spreads count members around the full circle).")]
        public string Measure { get; set; } = "spacing";

        [JsonProperty("vector")]
        [Description("linear: displacement in mm (step or total, see measure), e.g. {x:1200,y:0,z:0}.")]
        public PointMm Vector { get; set; }

        [JsonProperty("center")]
        [Description("radial: a point on the rotation axis, in mm.")]
        public PointMm Center { get; set; }

        [JsonProperty("angle")]
        [Unit("deg")]
        [Description("radial: angle in degrees (step or total, see measure); positive = counter-clockwise seen from the axis direction.")]
        public double? Angle { get; set; }

        [JsonProperty("axisDirection")]
        [Description("radial: direction of the rotation axis. Default vertical (0,0,1).")]
        public DirectionArg AxisDirection { get; set; }

        [JsonProperty("rotateCopies")]
        [DefaultValue(true)]
        [Description("radial: rotate each copy with the array (default true). false keeps the copies' orientation and only moves them around the axis.")]
        public bool RotateCopies { get; set; } = true;

        [JsonProperty("associative")]
        [DefaultValue(false)]
        [Description("false (default): independent copies. true: a Revit array element (members grouped and editable as an array in Revit); needs view.")]
        public bool Associative { get; set; }

        [JsonProperty("view")]
        [Description("associative only: the view the array is created in (name or id); for view-specific elements, their own view.")]
        public string View { get; set; }
    }

    /// <summary>EDT-02: linear or radial array (independent copies by default).</summary>
    [McpTool("array_elements", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "editing", TimeoutSeconds = 120,
        Description = "Array elements linearly (along a vector in mm) or radially (around an axis, angle in degrees). " +
                      "count includes the originals (2-200); measure says whether vector/angle is the step or reaches " +
                      "the last member. By default makes independent copies; associative:true creates a Revit array " +
                      "element (needs view). Pinned or grouped elements are refused. Returns the new ids per copy. " +
                      "Undo: Ctrl+Z.")]
    public sealed class ArrayElementsTool : McpTool<ArrayElementsArgs>
    {
        protected override void Validate(ArrayElementsArgs a)
        {
            if (a.Type == "linear")
            {
                if (a.Vector == null) throw new ToolException("INVALID_ARGUMENTS", "A linear array needs vector (mm).");
                if (Math.Abs(a.Vector.X) + Math.Abs(a.Vector.Y) + Math.Abs(a.Vector.Z) < 1e-6)
                    throw new ToolException("INVALID_ARGUMENTS", "vector must not be zero.");
            }
            else
            {
                if (a.Center == null) throw new ToolException("INVALID_ARGUMENTS", "A radial array needs center (mm).");
                if (!a.Angle.HasValue || Math.Abs(a.Angle.Value) < 1e-9)
                    throw new ToolException("INVALID_ARGUMENTS", "A radial array needs a non-zero angle (degrees).");
                if (Math.Abs(a.Angle.Value) > 360 + 1e-9)
                    throw new ToolException("INVALID_ARGUMENTS", "angle must be between -360 and 360 degrees.");
            }
            if (a.Associative && string.IsNullOrWhiteSpace(a.View))
                throw new ToolException("INVALID_ARGUMENTS", "associative:true needs view (name or id): Revit creates arrays in a view.");
        }

        protected override object Run(ToolContext ctx, ArrayElementsArgs a)
        {
            var doc = ctx.Doc;
            var elements = EditGeometry.Elements(doc, a.ElementIds);
            M1Elements.RefuseGroupMembers(elements, "arrayed");
            var ids = elements.Select(e => e.Id).ToList();
            bool last = a.Measure == "lastCopy";
            int steps = a.Count - 1;

            try
            {
                if (a.Associative) return Associative(ctx, a, ids, last);
                return a.Type == "linear" ? Linear(ctx, a, ids, last, steps) : Radial(ctx, a, elements, ids, last, steps);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("ARRAY_FAILED", $"Revit refused the array: {ex.Message}");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                throw new ToolException("ARRAY_FAILED", $"Revit refused the array: {ex.Message}");
            }
        }

        private static object Linear(ToolContext ctx, ArrayElementsArgs a, List<ElementId> ids, bool last, int steps)
        {
            var total = EditGeometry.ToXyz(a.Vector);
            var step = last ? total / steps : total;
            var copies = new List<object>();
            var all = new List<long>();
            for (int i = 1; i <= steps; i++)
            {
                var created = ElementTransformUtils.CopyElements(ctx.Doc, ids, step * i);
                var list = M1Elements.Ids(created);
                all.AddRange(list);
                copies.Add(new { index = i, offset = EditGeometry.Point(step * i), ids = list });
            }
            return Result(ctx, a, all, copies, null,
                $"Linear array: {steps} cop{(steps == 1 ? "y" : "ies")} of {ids.Count} element(s), step {Fmt(step)}.");
        }

        private static object Radial(ToolContext ctx, ArrayElementsArgs a, List<Element> elements, List<ElementId> ids, bool last, int steps)
        {
            var doc = ctx.Doc;
            var center = EditGeometry.ToXyz(a.Center);
            var dir = a.AxisDirection != null ? EditGeometry.ToDirection(a.AxisDirection, "axisDirection") : XYZ.BasisZ;
            var axis = Line.CreateUnbound(center, dir);
            double stepDeg = StepAngle(a.Angle.Value, a.Count, last);
            var anchor = EditGeometry.Center(elements) ?? center;
            var copies = new List<object>();
            var all = new List<long>();
            for (int i = 1; i <= steps; i++)
            {
                double rad = stepDeg * i * Math.PI / 180.0;
                ICollection<ElementId> created;
                if (a.RotateCopies)
                {
                    created = ElementTransformUtils.CopyElements(doc, ids, XYZ.Zero);
                    ElementTransformUtils.RotateElements(doc, created, axis, rad);
                }
                else
                {
                    var moved = Transform.CreateRotationAtPoint(dir, rad, center).OfPoint(anchor);
                    created = ElementTransformUtils.CopyElements(doc, ids, moved - anchor);
                }
                var list = M1Elements.Ids(created);
                all.AddRange(list);
                copies.Add(new { index = i, angleDeg = Math.Round(stepDeg * i, 6), ids = list });
            }
            return Result(ctx, a, all, copies, null,
                $"Radial array: {steps} cop{(steps == 1 ? "y" : "ies")} of {ids.Count} element(s), {Math.Round(stepDeg, 4)}° apart.");
        }

        /// <summary>Step angle: the angle itself (spacing) or spread to the last member (a full circle spreads over count).</summary>
        private static double StepAngle(double angle, int count, bool last)
        {
            if (!last) return angle;
            return Math.Abs(Math.Abs(angle) - 360) < 1e-9 ? angle / count : angle / (count - 1);
        }

        private static object Associative(ToolContext ctx, ArrayElementsArgs a, List<ElementId> ids, bool last)
        {
            var doc = ctx.Doc;
            var view = ctx.Services.Lookup.View(a.View);
            var notArrayable = ids.Where(id => !LinearArray.IsElementArrayable(doc, id)).Select(id => id.GetValue()).ToList();
            if (notArrayable.Count > 0)
                throw new ToolException("NOT_ARRAYABLE", $"Revit can't put these elements in an array: {string.Join(", ", notArrayable.Take(20))}.",
                    "Use associative:false for independent copies.");
            var anchor = last ? ArrayAnchorMember.Last : ArrayAnchorMember.Second;
            BaseArray array;
            string summary;
            if (a.Type == "linear")
            {
                var v = EditGeometry.ToXyz(a.Vector);
                array = LinearArray.Create(doc, view, ids, a.Count, v, anchor);
                summary = $"Associative linear array of {a.Count} members.";
            }
            else
            {
                var dir = a.AxisDirection != null ? EditGeometry.ToDirection(a.AxisDirection, "axisDirection") : XYZ.BasisZ;
                var center = EditGeometry.ToXyz(a.Center);
                var axis = Line.CreateBound(center, center + dir);
                double rad = a.Angle.Value * Math.PI / 180.0;
                if (!RadialArray.IsRotationAngleValid(rad))
                    throw new ToolException("INVALID_ARGUMENTS", $"Revit does not accept a radial array angle of {a.Angle.Value}°.");
                array = RadialArray.Create(doc, view, ids, a.Count, axis, rad, anchor);
                summary = $"Associative radial array of {a.Count} members.";
            }
            if (array == null) throw new ToolException("ARRAY_FAILED", "Revit did not create the array.");
            var copied = M1Elements.Ids(array.GetCopiedMemberIds());
            return Result(ctx, a, copied, null, array.Id.GetValue(), summary);
        }

        private static object Result(ToolContext ctx, ArrayElementsArgs a, List<long> all, List<object> copies, long? arrayId, string summary) => new
        {
            type = a.Type,
            count = a.Count,
            associative = a.Associative,
            arrayId,
            newIds = all,
            newCount = all.Count,
            copies = copies?.Take(200).ToList(),
            changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + summary
        };

        private static string Fmt(XYZ v) =>
            $"({EditGeometry.Mm(v.X)}, {EditGeometry.Mm(v.Y)}, {EditGeometry.Mm(v.Z)}) mm";
    }
}
