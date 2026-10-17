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
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class AlignDistributeElementsArgs
    {
        [JsonProperty("elementIds", Required = Required.Always)]
        [Required]
        [MinLength(2)]
        [MaxLength(1000)]
        [Description("Elements to align or distribute (at least 2).")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("mode", Required = Required.Always)]
        [Required]
        [EnumValues("align", "distribute")]
        [Description("align: line up one edge/centre of every element. distribute: space them evenly between the two outermost ones (which stay put).")]
        public string Mode { get; set; }

        [JsonProperty("axis", Required = Required.Always)]
        [Required]
        [EnumValues("x", "y", "z")]
        [Description("Axis along which elements move (project internal X/Y/Z). E.g. align left edges = axis x, edge min.")]
        public string Axis { get; set; }

        [JsonProperty("edge")]
        [EnumValues("min", "center", "max")]
        [DefaultValue("center")]
        [Description("Which part of each element's bounding box is aligned / evenly spaced: min (left/bottom), center (default) or max (right/top).")]
        public string Edge { get; set; } = "center";

        [JsonProperty("reference")]
        [EnumValues("first", "last", "min", "max", "element", "value")]
        [DefaultValue("first")]
        [Description("align: where the line is. first/last = that element of elementIds (default first); min/max = the lowest/highest edge among them; element = referenceElementId; value = coordinate.")]
        public string Reference { get; set; } = "first";

        [JsonProperty("referenceElementId")]
        [Description("align with reference element: the element whose edge is the line (may be outside elementIds; it does not move).")]
        public long? ReferenceElementId { get; set; }

        [JsonProperty("coordinate")]
        [Unit("mm")]
        [Description("align with reference value: the coordinate on the axis, in mm.")]
        public double? Coordinate { get; set; }

        [JsonProperty("equalGaps")]
        [DefaultValue(false)]
        [Description("distribute: make the clear gaps between bounding boxes equal instead of spacing the chosen edge evenly. Default false.")]
        public bool EqualGaps { get; set; }

        [JsonProperty("spacing")]
        [Unit("mm")]
        [Description("distribute: fixed step in mm from the first (lowest) element instead of spreading between the outermost ones (edge step, or clear gap with equalGaps).")]
        public double? Spacing { get; set; }
    }

    /// <summary>EDT-11: align or distribute elements along an axis using their bounding boxes.</summary>
    [McpTool("align_distribute_elements", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "editing",
        Description = "Align elements (line up their min/center/max bounding-box edge on an axis, at the first/last/" +
                      "min/max element, another element or a coordinate in mm) or distribute them evenly between the two " +
                      "outermost ones (or at a fixed spacing in mm, or with equal clear gaps). Uses world-axis bounding " +
                      "boxes (rotated elements use their box). Pinned/grouped elements are refused; hosted elements whose " +
                      "host also moves are skipped. Returns per-element movement read back. Undo: Ctrl+Z.")]
    public sealed class AlignDistributeElementsTool : McpTool<AlignDistributeElementsArgs>
    {
        protected override void Validate(AlignDistributeElementsArgs a)
        {
            if (a.Mode == "align")
            {
                if (a.Reference == "element" && !a.ReferenceElementId.HasValue)
                    throw new ToolException("INVALID_ARGUMENTS", "reference element needs referenceElementId.");
                if (a.Reference == "value" && !a.Coordinate.HasValue)
                    throw new ToolException("INVALID_ARGUMENTS", "reference value needs coordinate (mm).");
            }
            else if (a.ElementIds.Distinct().Count() < 3 && !a.Spacing.HasValue)
                throw new ToolException("INVALID_ARGUMENTS", "distribute needs at least 3 elements (or spacing).");
            if (a.Spacing.HasValue && a.Spacing.Value < 0 && !a.EqualGaps)
                throw new ToolException("INVALID_ARGUMENTS", "spacing must be positive.");
        }

        private sealed class Item
        {
            public Element Element;
            public BoundingBoxXYZ Box;
            public double Delta;
            public string Status = "moved";
        }

        protected override object Run(ToolContext ctx, AlignDistributeElementsArgs a)
        {
            var doc = ctx.Doc;
            var elements = EditGeometry.Elements(doc, a.ElementIds);
            M1Elements.RefusePinned(elements, "moved");
            M1Elements.RefuseGroupMembers(elements, "moved");
            var items = elements.Select(e => new Item { Element = e, Box = M1Elements.Box(e) }).ToList();
            var noBox = items.Where(i => i.Box == null).Select(i => i.Element.Id.GetValue()).ToList();
            if (noBox.Count > 0)
                throw new ToolException("NO_GEOMETRY", $"These elements have no bounding box to align: {string.Join(", ", noBox.Take(20))}.");
            int axis = a.Axis == "x" ? 0 : a.Axis == "y" ? 1 : 2;

            if (a.Mode == "align") PlanAlign(ctx, a, items, axis);
            else PlanDistribute(a, items, axis);

            // A hosted element moves with its host; moving it again would double the move.
            var moving = new HashSet<long>(items.Where(i => Math.Abs(i.Delta) > 1e-9).Select(i => i.Element.Id.GetValue()));
            foreach (var i in items)
            {
                if (Math.Abs(i.Delta) <= 1e-9) { i.Status = "unchanged"; continue; }
                if (i.Element is FamilyInstance fi && fi.Host != null && moving.Contains(fi.Host.Id.GetValue()))
                    i.Status = "moves_with_host";
            }

            var results = new List<object>();
            int moved = 0;
            foreach (var i in items)
            {
                double before = Coord(i.Box, axis, a.Edge);
                if (i.Status == "moved")
                {
                    var v = Axis(axis) * i.Delta;
                    var error = ItemIsolated(doc, () => ElementTransformUtils.MoveElement(doc, i.Element.Id, v));
                    if (error != null)
                    {
                        i.Status = "failed";
                        results.Add(new { id = i.Element.Id.GetValue(), status = i.Status, message = error.Message });
                        continue;
                    }
                    moved++;
                }
                results.Add(new { id = i.Element.Id.GetValue(), status = i.Status, plannedMoveMm = EditGeometry.Mm(i.Delta), before = EditGeometry.Mm(before) });
            }
            doc.Regenerate();

            // Read back: the actual coordinate of the edge after the move.
            var readBack = new List<object>();
            var off = new List<long>();
            foreach (var i in items)
            {
                var box = M1Elements.Box(i.Element);
                if (box == null) continue;
                double expected = Coord(i.Box, axis, a.Edge) + (i.Status == "moved" ? i.Delta : 0);
                double actual = Coord(box, axis, a.Edge);
                if (i.Status == "moved" && Math.Abs(actual - expected) > EditGeometry.Ft(1)) off.Add(i.Element.Id.GetValue());
                readBack.Add(new { id = i.Element.Id.GetValue(), edgeMm = EditGeometry.Mm(actual) });
            }
            if (off.Count > 0)
                ctx.Warnings.Add(new ToolWarning($"{off.Count} element(s) did not end where planned (hosted or constrained elements move only along their host).", off));

            return new
            {
                mode = a.Mode,
                axis = a.Axis,
                edge = a.Edge,
                movedCount = moved,
                items = results,
                readBack,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"{(a.Mode == "align" ? "Aligned" : "Distributed")} {moved} of {items.Count} element(s) along {a.Axis.ToUpperInvariant()}."
            };
        }

        private static void PlanAlign(ToolContext ctx, AlignDistributeElementsArgs a, List<Item> items, int axis)
        {
            double target;
            switch (a.Reference)
            {
                case "last": target = Coord(items[items.Count - 1].Box, axis, a.Edge); break;
                case "min": target = items.Min(i => Coord(i.Box, axis, a.Edge)); break;
                case "max": target = items.Max(i => Coord(i.Box, axis, a.Edge)); break;
                case "value": target = EditGeometry.Ft(a.Coordinate.Value); break;
                case "element":
                {
                    var refEl = ctx.Doc.GetElement(a.ReferenceElementId.Value.ToRevitElementId())
                                ?? throw new ToolException("ELEMENT_NOT_FOUND", $"Reference element {a.ReferenceElementId} not found.");
                    var box = M1Elements.Box(refEl) ?? throw new ToolException("NO_GEOMETRY", $"Reference element {a.ReferenceElementId} has no bounding box.");
                    target = Coord(box, axis, a.Edge);
                    break;
                }
                default: target = Coord(items[0].Box, axis, a.Edge); break;
            }
            foreach (var i in items) i.Delta = target - Coord(i.Box, axis, a.Edge);
        }

        private static void PlanDistribute(AlignDistributeElementsArgs a, List<Item> items, int axis)
        {
            var sorted = items.OrderBy(i => Coord(i.Box, axis, "center")).ToList();
            int n = sorted.Count;
            if (a.EqualGaps)
            {
                var sizes = sorted.Select(i => Coord(i.Box, axis, "max") - Coord(i.Box, axis, "min")).ToList();
                double start = Coord(sorted[0].Box, axis, "min");
                double gap = a.Spacing.HasValue
                    ? EditGeometry.Ft(a.Spacing.Value)
                    : ((Coord(sorted[n - 1].Box, axis, "max") - start) - sizes.Sum()) / (n - 1);
                double cursor = start;
                for (int k = 0; k < n; k++)
                {
                    sorted[k].Delta = cursor - Coord(sorted[k].Box, axis, "min");
                    cursor += sizes[k] + gap;
                }
                return;
            }
            double first = Coord(sorted[0].Box, axis, a.Edge);
            double step = a.Spacing.HasValue
                ? EditGeometry.Ft(a.Spacing.Value)
                : (Coord(sorted[n - 1].Box, axis, a.Edge) - first) / (n - 1);
            for (int k = 0; k < n; k++)
                sorted[k].Delta = first + step * k - Coord(sorted[k].Box, axis, a.Edge);
        }

        private static double Coord(BoundingBoxXYZ b, int axis, string edge)
        {
            double min = b.Min[axis], max = b.Max[axis];
            return edge == "min" ? min : edge == "max" ? max : (min + max) / 2;
        }

        private static XYZ Axis(int axis) => axis == 0 ? XYZ.BasisX : axis == 1 ? XYZ.BasisY : XYZ.BasisZ;

        private static Exception ItemIsolated(Document doc, Action action)
        {
            using (var sub = new SubTransaction(doc))
            {
                sub.Start();
                try
                {
                    action();
                    doc.Regenerate();
                    sub.Commit();
                    return null;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                    return ex;
                }
            }
        }
    }
}
