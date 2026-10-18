using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing.M1Support
{
    /// <summary>
    /// Reads and replaces the driving curve of linear elements (walls, beams/braces, line-based instances, model and
    /// detail lines). Walls are the delicate case: changing a joined wall's location curve mid-transaction made Revit
    /// throw InternalException in live tests, so the change runs in a sub-transaction with the end joins released,
    /// and, if Revit still refuses and the wall hosts nothing, the wall is recreated on the new line.
    /// </summary>
    public static class CurveEdit
    {
        /// <summary>
        /// A copy of the element's curve. Revit hands out live curve objects: after the element changes (or is
        /// recreated) the original becomes invalid and reading it throws InternalException, so never keep it.
        /// </summary>
        public static Curve Get(Element e)
        {
            Curve live;
            switch (e)
            {
                case CurveElement ce: live = ce.GeometryCurve; break;
                case Grid g: live = g.Curve; break;
                default: live = (e.Location as LocationCurve)?.Curve; break;
            }
            return live?.Clone();
        }

        public static Curve Require(Element e)
        {
            if (e is Grid)
                throw new ToolException("NOT_SUPPORTED", $"Grid {e.Id.GetValue()}: grid extents are per view and are not edited here.");
            var c = Get(e);
            if (c == null)
                throw new ToolException("NOT_LINEAR", $"Element {e.Id.GetValue()} ({e.Category?.Name ?? e.GetType().Name}) has no location curve.",
                    "Use transform_elements to move point-based elements.", "transform_elements");
            return c;
        }

        public static Line RequireLine(Element e, string operation)
        {
            if (Require(e) is Line l) return l;
            throw new ToolException("NOT_SUPPORTED", $"{operation} works on straight elements; {e.Id.GetValue()} is curved ({Get(e).GetType().Name}).");
        }

        public static object Describe(Curve c) => c == null ? null : new
        {
            start = EditGeometry.Point(c.GetEndPoint(0)),
            end = EditGeometry.Point(c.GetEndPoint(1)),
            lengthMm = EditGeometry.Mm(c.Length),
            kind = c is Line ? "line" : c is Arc ? "arc" : c.GetType().Name
        };

        /// <summary>
        /// Sets the curve; returns the element that now carries it (a new wall when it had to be recreated).
        /// </summary>
        public static Element Set(ToolContext ctx, Element e, Curve curve, out bool recreated)
        {
            recreated = false;
            var doc = ctx.Doc;
            if (curve.Length <= doc.Application.ShortCurveTolerance)
                throw new ToolException("INVALID_GEOMETRY", $"Element {e.Id.GetValue()}: the new curve is too short.");

            Exception error;
            if (e is Wall wall)
            {
                error = Isolated(doc, () =>
                {
                    var allowed = new[] { WallUtils.IsWallJoinAllowedAtEnd(wall, 0), WallUtils.IsWallJoinAllowedAtEnd(wall, 1) };
                    for (int end = 0; end < 2; end++) if (allowed[end]) WallUtils.DisallowWallJoinAtEnd(wall, end);
                    doc.Regenerate();
                    ((LocationCurve)wall.Location).Curve = curve;
                    doc.Regenerate();
                    for (int end = 0; end < 2; end++) if (allowed[end]) WallUtils.AllowWallJoinAtEnd(wall, end);
                    doc.Regenerate();
                });
                if (error == null) return wall;
                if (!(curve is Line line) || wall.FindInserts(true, false, true, true).Count > 0)
                    throw new ToolException("WALL_EDIT_FAILED",
                        $"Revit refused to change wall {wall.Id.GetValue()}'s line ({error.GetType().Name}: {error.Message}) and it can't be recreated safely (it hosts doors/windows/openings or is curved).",
                        "Unjoin it from its neighbours (join_cut_geometry unjoin) or edit it in Revit.");
                recreated = true;
                return RecreateWall(ctx, wall, line);
            }

            error = Isolated(doc, () =>
            {
                switch (e)
                {
                    case CurveElement ce: ce.SetGeometryCurve(curve, true); break;
                    default:
                        var lc = e.Location as LocationCurve
                                 ?? throw new ToolException("NOT_LINEAR", $"Element {e.Id.GetValue()} has no location curve.");
                        lc.Curve = curve;
                        break;
                }
                doc.Regenerate();
            });
            if (error is ToolException te) throw te;
            if (error != null)
                throw new ToolException("CURVE_EDIT_FAILED", $"Revit refused the new curve for element {e.Id.GetValue()}: {error.Message}");
            return e;
        }

        /// <summary>A new wall on <paramref name="locationLine"/> with the old wall's settings; the old one is deleted.</summary>
        private static Wall RecreateWall(ToolContext ctx, Wall old, Line locationLine)
        {
            var doc = ctx.Doc;
            var oldLine = (Line)((LocationCurve)old.Location).Curve;
            var keyRef = old.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? 0;

            // Distance from the location line to the centerline, measured along the exterior side.
            double towardExterior = 0;
            if (keyRef != 0)
            {
                using (var probe = new SubTransaction(doc))
                {
                    probe.Start();
                    old.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).Set(0);
                    doc.Regenerate();
                    var centre = ((LocationCurve)old.Location).Curve.GetEndPoint(0);
                    towardExterior = (centre - oldLine.GetEndPoint(0)).DotProduct(old.Orientation.Normalize());
                    probe.RollBack();
                }
            }

            // Exterior side relative to the drawing direction, kept for the new line.
            var oldLeft = XYZ.BasisZ.CrossProduct(oldLine.Direction).Normalize();
            double side = old.Orientation.DotProduct(oldLeft) >= 0 ? 1 : -1;
            var exterior = XYZ.BasisZ.CrossProduct(locationLine.Direction).Normalize() * side;
            var centreLine = (Line)locationLine.CreateTransformed(Transform.CreateTranslation(exterior * towardExterior));

            var levelId = old.LevelId;
            double height = old.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 10;
            double baseOffset = old.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
            bool structural = old.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1;
            var top = old.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId();
            double topOffset = old.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0;
            var typeId = old.WallType.Id;
            var oldId = old.Id.GetValue();

            doc.Delete(old.Id);
            var wall = Wall.Create(doc, centreLine, typeId, levelId, height, baseOffset, false, structural)
                       ?? throw new ToolException("CREATE_FAILED", "Revit did not recreate the wall.");
            doc.Regenerate();
            if (wall.Orientation.DotProduct(exterior) < 0) wall.Flip();
            if (top != null && top != ElementId.InvalidElementId)
            {
                wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(top);
                wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(topOffset);
            }
            if (keyRef != 0) wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set(keyRef);
            doc.Regenerate();
            ctx.Warnings.Add(new ToolWarning(
                $"Wall {oldId} was recreated as {wall.Id.GetValue()} because Revit refused to change its line in place; tags, dimensions and instance parameter values of the old wall are not carried over.",
                new List<long> { wall.Id.GetValue() }));
            return wall;
        }

        /// <summary>Intersection of the two unbounded lines in plan (z from <paramref name="a"/>), or null when parallel.</summary>
        public static XYZ PlanIntersection(Line a, Line b)
        {
            var p = a.GetEndPoint(0);
            var r = a.Direction;
            var q = b.GetEndPoint(0);
            var s = b.Direction;
            double denom = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(denom) < 1e-9) return null;
            double t = ((q.X - p.X) * s.Y - (q.Y - p.Y) * s.X) / denom;
            return new XYZ(p.X + r.X * t, p.Y + r.Y * t, p.Z + r.Z * t);
        }

        /// <summary>The straight line of a reference element (wall/beam/line location, grid, reference plane).</summary>
        public static Line ReferenceLine(Element e)
        {
            switch (e)
            {
                case ReferencePlane rp:
                    return Line.CreateBound(rp.BubbleEnd, rp.FreeEnd);
                case Grid g when g.Curve is Line gl:
                    return gl;
            }
            if (Get(e) is Line l) return l;
            throw new ToolException("INVALID_REFERENCE", $"Element {e.Id.GetValue()} is not a straight wall, beam, line, grid or reference plane.");
        }

        /// <summary>Replaces endpoint <paramref name="end"/> (0/1) of a line by <paramref name="p"/> (keeping that end's z).</summary>
        public static Line WithEnd(Line l, int end, XYZ p)
        {
            var keep = l.GetEndPoint(1 - end);
            var moved = new XYZ(p.X, p.Y, l.GetEndPoint(end).Z);
            return end == 0 ? Line.CreateBound(moved, keep) : Line.CreateBound(keep, moved);
        }

        /// <summary>Which end of the line is nearer to the point (in plan).</summary>
        public static int NearestEnd(Line l, XYZ p)
        {
            double d0 = Flat(l.GetEndPoint(0)).DistanceTo(Flat(p));
            double d1 = Flat(l.GetEndPoint(1)).DistanceTo(Flat(p));
            return d0 <= d1 ? 0 : 1;
        }

        private static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);

        public static Exception Isolated(Document doc, Action action)
        {
            using (var sub = new SubTransaction(doc))
            {
                sub.Start();
                try
                {
                    action();
                    sub.Commit();
                    return null;
                }
                catch (Exception ex) when (ex is ToolException || ex is Autodesk.Revit.Exceptions.ApplicationException)
                {
                    if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                    return ex;
                }
            }
        }

        /// <summary>Inserts (doors, windows, openings) hosted by a wall, with their parameter along the wall curve.</summary>
        public static List<(ElementId id, double param)> InsertsAlong(Wall wall, Curve curve)
        {
            var list = new List<(ElementId, double)>();
            foreach (var id in wall.FindInserts(true, false, true, true))
            {
                var e = wall.Document.GetElement(id);
                XYZ p = (e?.Location as LocationPoint)?.Point;
                if (p == null)
                {
                    var bb = e?.get_BoundingBox(null);
                    if (bb == null) continue;
                    p = (bb.Min + bb.Max) / 2;
                }
                var proj = curve.Project(new XYZ(p.X, p.Y, curve.GetEndPoint(0).Z));
                if (proj != null) list.Add((id, proj.Parameter));
            }
            return list;
        }
    }
}
