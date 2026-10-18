using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Tools.Editing.M1Support;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class EditElementCurveArgs
    {
        [JsonProperty("operation", Required = Required.Always)]
        [Required]
        [EnumValues("setEndpoints", "extendBy", "extendTo", "trimToCorner", "split", "offset")]
        [Description("setEndpoints (new start/end), extendBy (lengthen/shorten one end by distance), extendTo (extend/trim to a reference line), trimToCorner (two elements meet at their intersection), split (into two at a point), offset (move/copy sideways by distance).")]
        public string Operation { get; set; }

        [JsonProperty("elementIds", Required = Required.Always)]
        [Required]
        [MinLength(1)]
        [MaxLength(200)]
        [Description("Walls, beams/braces, line-based instances or model/detail lines. setEndpoints/extendBy/split: one; trimToCorner: two; extendTo/offset: one or more.")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("start")]
        [Description("setEndpoints: new start point in mm (z defaults to the current one). Omit to keep it.")]
        public Point3Mm Start { get; set; }

        [JsonProperty("end")]
        [Description("setEndpoints: new end point in mm (z defaults to the current one). Omit to keep it.")]
        public Point3Mm End { get; set; }

        [JsonProperty("whichEnd")]
        [EnumValues("auto", "start", "end")]
        [DefaultValue("auto")]
        [Description("extendBy / extendTo: which end changes. auto (default) = end for extendBy, the end nearer the reference for extendTo.")]
        public string WhichEnd { get; set; } = "auto";

        [JsonProperty("distance")]
        [Unit("mm")]
        [Description("extendBy: mm to add (negative shortens). offset: sideways distance in mm, positive = to the left of start→end (seen from above).")]
        public double? Distance { get; set; }

        [JsonProperty("referenceId")]
        [Description("extendTo: element whose line is the target (wall/beam/line location line, grid or reference plane; unbounded).")]
        public long? ReferenceId { get; set; }

        [JsonProperty("splitPoint")]
        [Description("split: point in mm, projected onto the element's curve.")]
        public Point3Mm SplitPoint { get; set; }

        [JsonProperty("ratio")]
        [System.ComponentModel.DataAnnotations.Range(0.0, 1.0)]
        [Description("split: alternative to splitPoint, fraction of the length from the start (0-1 exclusive), e.g. 0.5 = middle.")]
        public double? Ratio { get; set; }

        [JsonProperty("copy")]
        [DefaultValue(false)]
        [Description("offset: make offset copies and keep the originals. Default false (move).")]
        public bool Copy { get; set; }
    }

    /// <summary>EDT-13: edit the driving curve of linear elements.</summary>
    [McpTool("edit_element_curve", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "editing",
        Description = "Edit the line of walls, beams/braces, line-based families and model/detail lines: set endpoints, " +
                      "extend/shorten by a distance, extend/trim to a reference (wall/line/grid/reference plane), trim two " +
                      "elements to their corner, split one into two (walls keep their doors on the right part), or offset " +
                      "sideways (move or copy). All in mm. Wall joins are released during the change; if Revit still " +
                      "refuses, a wall without inserts is recreated (new id, reported). Returns before/after read back. " +
                      "Undo: Ctrl+Z.")]
    public sealed class EditElementCurveTool : McpTool<EditElementCurveArgs>, IRetryAfterRollback
    {
        // Dimensions that only fail because the edit moves the faces they measure: removed on the second attempt, as
        // Revit's own dialog offers. The person asked for the new geometry; the dimensions are listed in a warning.
        private List<long> _dimensionsToRemove;

        public bool PrepareRetry(ToolContext ctx, SafeFailuresPreprocessor failures)
        {
            if (_dimensionsToRemove != null || !failures.OnlyDimensionErrors || failures.FailingDimensionIds.Count == 0) return false;
            _dimensionsToRemove = failures.FailingDimensionIds.ToList();
            ctx.Warnings.Add(new ToolWarning(
                $"{_dimensionsToRemove.Count} dimension(s) measured the faces this edit moves and could no longer be kept " +
                "(Revit refuses them), so they were removed; redraw them if needed.", _dimensionsToRemove));
            return true;
        }

        protected override void Validate(EditElementCurveArgs a)
        {
            int n = a.ElementIds.Distinct().Count();
            switch (a.Operation)
            {
                case "setEndpoints":
                    One(n, a.Operation);
                    if (a.Start == null && a.End == null) throw new ToolException("INVALID_ARGUMENTS", "setEndpoints needs start and/or end.");
                    break;
                case "extendBy":
                    One(n, a.Operation);
                    if (!a.Distance.HasValue || Math.Abs(a.Distance.Value) < 1e-9) throw new ToolException("INVALID_ARGUMENTS", "extendBy needs a non-zero distance (mm).");
                    break;
                case "extendTo":
                    if (!a.ReferenceId.HasValue) throw new ToolException("INVALID_ARGUMENTS", "extendTo needs referenceId.");
                    break;
                case "trimToCorner":
                    if (n != 2) throw new ToolException("INVALID_ARGUMENTS", "trimToCorner needs exactly two elements.");
                    break;
                case "split":
                    One(n, a.Operation);
                    if ((a.SplitPoint == null) == (a.Ratio == null)) throw new ToolException("INVALID_ARGUMENTS", "split needs exactly one of splitPoint or ratio.");
                    if (a.Ratio.HasValue && (a.Ratio <= 0 || a.Ratio >= 1)) throw new ToolException("INVALID_ARGUMENTS", "ratio must be between 0 and 1 (exclusive).");
                    break;
                case "offset":
                    if (!a.Distance.HasValue || Math.Abs(a.Distance.Value) < 1e-9) throw new ToolException("INVALID_ARGUMENTS", "offset needs a non-zero distance (mm).");
                    break;
            }
        }

        private static void One(int n, string op)
        {
            if (n != 1) throw new ToolException("INVALID_ARGUMENTS", $"{op} works on exactly one element.");
        }

        protected override object Run(ToolContext ctx, EditElementCurveArgs a)
        {
            var doc = ctx.Doc;
            if (_dimensionsToRemove != null)
                doc.Delete(_dimensionsToRemove.Select(id => id.ToRevitElementId()).Where(id => doc.GetElement(id) != null).ToList());
            var elements = EditGeometry.Elements(doc, a.ElementIds);
            M1Elements.RefusePinned(elements, "edited");
            M1Elements.RefuseGroupMembers(elements, "edited");
            var results = new List<object>();
            try
            {
                switch (a.Operation)
                {
                    case "setEndpoints": results.Add(SetEndpoints(ctx, elements[0], a)); break;
                    case "extendBy": results.Add(ExtendBy(ctx, elements[0], a)); break;
                    case "extendTo":
                    {
                        var reference = doc.GetElement(a.ReferenceId.Value.ToRevitElementId())
                                        ?? throw new ToolException("ELEMENT_NOT_FOUND", $"Reference {a.ReferenceId} not found.");
                        var refLine = CurveEdit.ReferenceLine(reference);
                        foreach (var e in elements) results.Add(ExtendTo(ctx, e, refLine, a.WhichEnd));
                        break;
                    }
                    case "trimToCorner": results.AddRange(TrimToCorner(ctx, elements[0], elements[1])); break;
                    case "split": results.Add(Split(ctx, elements[0], a)); break;
                    default: foreach (var e in elements) results.Add(Offset(ctx, e, a)); break;
                }
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("CURVE_EDIT_FAILED", $"Revit refused the {a.Operation}: {ex.Message}");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                throw new ToolException("CURVE_EDIT_FAILED", $"Revit refused the {a.Operation}: {ex.Message}");
            }

            return new
            {
                operation = a.Operation,
                elements = results,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + $"{a.Operation} applied to {results.Count} element(s)."
            };
        }

        private static object SetEndpoints(ToolContext ctx, Element e, EditElementCurveArgs a)
        {
            var line = CurveEdit.RequireLine(e, "setEndpoints");
            var p0 = Pt(a.Start, line.GetEndPoint(0));
            var p1 = Pt(a.End, line.GetEndPoint(1));
            return Apply(ctx, e, line, Line.CreateBound(p0, p1));
        }

        private static object ExtendBy(ToolContext ctx, Element e, EditElementCurveArgs a)
        {
            var line = CurveEdit.RequireLine(e, "extendBy");
            double d = EditGeometry.Ft(a.Distance.Value);
            if (-d >= line.Length) throw new ToolException("INVALID_GEOMETRY", $"Shortening by {a.Distance} mm would remove the whole element ({EditGeometry.Mm(line.Length)} mm long).");
            var p0 = line.GetEndPoint(0);
            var p1 = line.GetEndPoint(1);
            var dir = line.Direction;
            var next = a.WhichEnd == "start" ? Line.CreateBound(p0 - dir * d, p1) : Line.CreateBound(p0, p1 + dir * d);
            return Apply(ctx, e, line, next);
        }

        private static object ExtendTo(ToolContext ctx, Element e, Line target, string whichEnd)
        {
            var line = CurveEdit.RequireLine(e, "extendTo");
            var hit = CurveEdit.PlanIntersection(line, target);
            if (hit == null)
                return new { id = e.Id.GetValue(), status = "not_applicable", message = "The element is parallel to the reference." };
            int end = whichEnd == "start" ? 0 : whichEnd == "end" ? 1 : CurveEdit.NearestEnd(line, hit);
            return Apply(ctx, e, line, CurveEdit.WithEnd(line, end, hit));
        }

        private static IEnumerable<object> TrimToCorner(ToolContext ctx, Element a, Element b)
        {
            var la = CurveEdit.RequireLine(a, "trimToCorner");
            var lb = CurveEdit.RequireLine(b, "trimToCorner");
            var hit = CurveEdit.PlanIntersection(la, lb)
                      ?? throw new ToolException("INVALID_GEOMETRY", "The two elements are parallel; they have no corner.");
            return new[]
            {
                Apply(ctx, a, la, CurveEdit.WithEnd(la, CurveEdit.NearestEnd(la, hit), hit)),
                Apply(ctx, b, lb, CurveEdit.WithEnd(lb, CurveEdit.NearestEnd(lb, hit), hit))
            };
        }

        private static object Split(ToolContext ctx, Element e, EditElementCurveArgs a)
        {
            var doc = ctx.Doc;
            var curve = CurveEdit.Require(e);
            double t0 = curve.GetEndParameter(0), t1 = curve.GetEndParameter(1);
            double ts;
            if (a.Ratio.HasValue) ts = curve.ComputeRawParameter(a.Ratio.Value);
            else
            {
                var p = new XYZ(EditGeometry.Ft(a.SplitPoint.X), EditGeometry.Ft(a.SplitPoint.Y),
                    a.SplitPoint.Z.HasValue ? EditGeometry.Ft(a.SplitPoint.Z.Value) : curve.GetEndPoint(0).Z);
                var proj = curve.Project(p) ?? throw new ToolException("INVALID_GEOMETRY", "The split point can't be projected onto the element.");
                ts = proj.Parameter;
            }
            var tol = doc.Application.ShortCurveTolerance;
            Curve first, second;
            if (curve is Line)
            {
                var ps = curve.Evaluate(ts, false);
                if (ps.DistanceTo(curve.GetEndPoint(0)) <= tol || ps.DistanceTo(curve.GetEndPoint(1)) <= tol)
                    throw new ToolException("INVALID_GEOMETRY", "The split point is at (or too close to) an end of the element.");
                first = Line.CreateBound(curve.GetEndPoint(0), ps);
                second = Line.CreateBound(ps, curve.GetEndPoint(1));
            }
            else
            {
                if (ts <= t0 || ts >= t1)
                    throw new ToolException("INVALID_GEOMETRY", "The split point is at (or past) an end of the element.");
                first = curve.Clone();
                second = curve.Clone();
                first.MakeBound(t0, ts);
                second.MakeBound(ts, t1);
            }
            if (first.Length <= tol || second.Length <= tol)
                throw new ToolException("INVALID_GEOMETRY", "The split point is at (or too close to) an end of the element.");

            var before = CurveEdit.Describe(curve);
            var copies = ElementTransformUtils.CopyElement(doc, e.Id, XYZ.Zero);
            var twin = copies.Select(id => doc.GetElement(id)).FirstOrDefault(x => x != null && x.GetType() == e.GetType() && x.Category?.Id == e.Category?.Id)
                       ?? throw new ToolException("SPLIT_FAILED", "Revit did not copy the element to make the second part.");
            int movedInserts = 0;

            if (e is Wall wall && twin is Wall twinWall)
            {
                // Each part keeps the doors/windows on its side: the original loses those past the split, the copy
                // (which received copies of every insert) loses those before it.
                var removeFromOriginal = CurveEdit.InsertsAlong(wall, curve).Where(x => x.param > ts).Select(x => x.id).ToList();
                var removeFromCopy = CurveEdit.InsertsAlong(twinWall, curve).Where(x => x.param <= ts).Select(x => x.id).ToList();
                movedInserts = removeFromOriginal.Count;
                var extra = copies.Where(id => id != twin.Id && doc.GetElement(id) != null && !(doc.GetElement(id) is FamilyInstance fi && fi.Host?.Id == twin.Id) &&
                                               !(doc.GetElement(id) is Opening op && op.Host?.Id == twin.Id)).ToList();
                var drop = removeFromOriginal.Concat(removeFromCopy).Concat(extra).Where(id => doc.GetElement(id) != null).ToList();
                if (drop.Count > 0) doc.Delete(drop);
                doc.Regenerate();
                if (movedInserts > 0)
                    ctx.Warnings.Add(new ToolWarning($"{movedInserts} insert(s) past the split now live on the new wall as copies (new ids; tags on the old ones are lost).",
                        new List<long> { twin.Id.GetValue() }));
            }
            else
            {
                var extra = copies.Where(id => id != twin.Id && doc.GetElement(id) != null).ToList();
                if (extra.Count > 0) doc.Delete(extra);
            }

            var firstHolder = CurveEdit.Set(ctx, e, first, out bool r1);
            var secondHolder = CurveEdit.Set(ctx, twin, second, out bool r2);
            return new
            {
                id = firstHolder.Id.GetValue(),
                newId = secondHolder.Id.GetValue(),
                status = "split",
                recreated = r1 || r2,
                before,
                first = CurveEdit.Describe(CurveEdit.Get(firstHolder)),
                second = CurveEdit.Describe(CurveEdit.Get(secondHolder))
            };
        }

        private static object Offset(ToolContext ctx, Element e, EditElementCurveArgs a)
        {
            var doc = ctx.Doc;
            var line = CurveEdit.RequireLine(e, "offset");
            var left = XYZ.BasisZ.CrossProduct(line.Direction);
            if (left.GetLength() < 1e-9)
                return new { id = e.Id.GetValue(), status = "not_applicable", message = "The element is vertical; offset is measured in plan." };
            var v = left.Normalize() * EditGeometry.Ft(a.Distance.Value);
            var before = CurveEdit.Describe(line);
            if (a.Copy)
            {
                var created = ElementTransformUtils.CopyElement(doc, e.Id, v);
                var twin = created.Select(id => doc.GetElement(id)).FirstOrDefault(x => x != null && x.GetType() == e.GetType());
                return new
                {
                    id = e.Id.GetValue(),
                    newId = twin?.Id.GetValue(),
                    newIds = M1Elements.Ids(created),
                    status = "copied",
                    before,
                    after = CurveEdit.Describe(twin == null ? null : CurveEdit.Get(twin))
                };
            }
            var error = CurveEdit.Isolated(doc, () => { ElementTransformUtils.MoveElement(doc, e.Id, v); doc.Regenerate(); });
            if (error != null)
                return new { id = e.Id.GetValue(), status = "failed", message = error.Message };
            return new { id = e.Id.GetValue(), status = "moved", before, after = CurveEdit.Describe(CurveEdit.Get(e)) };
        }

        private static object Apply(ToolContext ctx, Element e, Curve before, Curve next)
        {
            var id = e.Id.GetValue();
            var holder = CurveEdit.Set(ctx, e, next, out bool recreated);
            return new
            {
                id,
                newId = recreated ? holder.Id.GetValue() : (long?)null,
                status = recreated ? "recreated" : "changed",
                before = CurveEdit.Describe(before),
                after = CurveEdit.Describe(CurveEdit.Get(holder))
            };
        }

        private static XYZ Pt(Point3Mm p, XYZ current) => p == null
            ? current
            : new XYZ(EditGeometry.Ft(p.X), EditGeometry.Ft(p.Y), p.Z.HasValue ? EditGeometry.Ft(p.Z.Value) : current.Z);
    }
}
