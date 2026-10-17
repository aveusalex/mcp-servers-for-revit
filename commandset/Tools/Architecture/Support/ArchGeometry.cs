using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture.Support
{
    /// <summary>Unit and plan-geometry helpers for the architecture tools (mm on the wire, feet inside Revit).</summary>
    public static class ArchGeometry
    {
        public const double MmPerFoot = 304.8;

        public static double Ft(double mm) => mm / MmPerFoot;
        public static double Mm(double feet) => Math.Round(feet * MmPerFoot, 2);
        public static double M2(double sqFeet) => Math.Round(sqFeet * 0.09290304, 3);
        public static double M(double feet) => Math.Round(feet * 0.3048, 3);
        public static double Rad(double deg) => deg * Math.PI / 180.0;
        public static double Deg(double rad) => Math.Round(rad * 180.0 / Math.PI, 4);

        public static XYZ ToXyz(PointMm p, double zFeet = 0) => new XYZ(Ft(p.X), Ft(p.Y), zFeet);

        public static object ToWire(XYZ p) => new { x = Mm(p.X), y = Mm(p.Y), z = Mm(p.Z) };

        public static object ToWire2(XYZ p) => new { x = Mm(p.X), y = Mm(p.Y) };

        public static string Fmt(double mm) => mm.ToString("0.#", CultureInfo.InvariantCulture) + " mm";

        /// <summary>Corners of a rectangle, counter-clockwise, at Z = <paramref name="zFeet"/>.</summary>
        public static IList<XYZ> RectanglePoints(RectangleMm r, double zFeet = 0)
        {
            var o = ToXyz(r.Origin, zFeet);
            var a = Rad(r.Rotation);
            var ux = new XYZ(Math.Cos(a), Math.Sin(a), 0);
            var uy = new XYZ(-Math.Sin(a), Math.Cos(a), 0);
            var w = Ft(r.Width);
            var d = Ft(r.Depth);
            return new List<XYZ> { o, o + ux * w, o + ux * w + uy * d, o + uy * d };
        }

        public static IList<XYZ> ToXyz(IEnumerable<PointMm> points, double zFeet = 0) =>
            points.Select(p => ToXyz(p, zFeet)).ToList();

        /// <summary>Drops a repeated closing point and consecutive duplicates.</summary>
        public static IList<XYZ> CleanPolygon(IList<XYZ> pts, double tolerance)
        {
            var list = new List<XYZ>();
            foreach (var p in pts)
            {
                if (list.Count == 0 || list[list.Count - 1].DistanceTo(p) > tolerance) list.Add(p);
            }
            if (list.Count > 1 && list[0].DistanceTo(list[list.Count - 1]) <= tolerance) list.RemoveAt(list.Count - 1);
            return list;
        }

        /// <summary>Shoelace area in square feet: positive when counter-clockwise seen from above.</summary>
        public static double SignedArea(IList<XYZ> pts)
        {
            double a = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                var q = pts[(i + 1) % pts.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
            return a / 2.0;
        }

        /// <summary>Tessellated points of a loop (each curve's points without its last, so the polygon is closed implicitly).</summary>
        public static IList<XYZ> LoopPoints(CurveLoop loop)
        {
            var pts = new List<XYZ>();
            foreach (var c in loop)
            {
                var t = c.Tessellate();
                for (int i = 0; i < t.Count - 1; i++) pts.Add(t[i]);
            }
            return pts;
        }

        public static double SignedArea(CurveLoop loop) => SignedArea(LoopPoints(loop));

        /// <summary>
        /// A closed loop of straight lines through the points. Fails with INVALID_GEOMETRY (and the offending
        /// segment in mm) for segments shorter than Revit accepts or points that do not form a simple loop.
        /// </summary>
        public static CurveLoop PolygonLoop(Document doc, IList<XYZ> rawPoints, string what)
        {
            var tol = doc.Application.ShortCurveTolerance;
            var pts = CleanPolygon(rawPoints, tol);
            if (pts.Count < 3)
                throw new ToolException("INVALID_GEOMETRY", $"The {what} needs at least 3 distinct points (got {pts.Count}).");
            var curves = new List<Curve>();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                if (a.DistanceTo(b) <= tol)
                    throw new ToolException("INVALID_GEOMETRY",
                        $"Segment {i} of the {what} is {Fmt(Mm(a.DistanceTo(b)))} long; Revit needs more than {Fmt(Mm(tol))}.");
                curves.Add(Line.CreateBound(a, b));
            }
            if (Math.Abs(SignedArea(pts)) < tol * tol)
                throw new ToolException("INVALID_GEOMETRY", $"The {what} has no area (points are collinear).");
            if (SelfIntersects(pts))
                throw new ToolException("INVALID_GEOMETRY", $"The {what} crosses itself.",
                    "List the points in order around the outline (clockwise or counter-clockwise).");
            try
            {
                return CurveLoop.Create(curves);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("INVALID_GEOMETRY", $"The {what} is not a valid closed loop: {ex.Message}");
            }
        }

        /// <summary>Proper crossing between two non-adjacent edges of a polygon.</summary>
        public static bool SelfIntersects(IList<XYZ> pts)
        {
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                var a1 = pts[i];
                var a2 = pts[(i + 1) % n];
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i || (j + 1) % n == i || (i + 1) % n == j) continue;
                    var b1 = pts[j];
                    var b2 = pts[(j + 1) % n];
                    if (SegmentsCross(a1, a2, b1, b2)) return true;
                }
            }
            return false;
        }

        private static bool SegmentsCross(XYZ p1, XYZ p2, XYZ q1, XYZ q2)
        {
            double d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2), d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
            return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) &&
                   ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
        }

        private static double Cross(XYZ a, XYZ b, XYZ c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        /// <summary>Reverses the loop when its orientation differs from the requested one.</summary>
        public static CurveLoop Oriented(CurveLoop loop, bool counterClockwise)
        {
            bool ccw = SignedArea(loop) > 0;
            if (ccw == counterClockwise) return loop;
            var copy = CurveLoop.CreateViaCopy(loop);
            copy.Flip();
            return copy;
        }

        /// <summary>
        /// Outer loop counter-clockwise, holes clockwise, each offset by <paramref name="offsetFeet"/> (positive grows the
        /// region). The direction of the offset is checked by area, not assumed from the API convention.
        /// </summary>
        public static IList<CurveLoop> OffsetRegion(IList<CurveLoop> loops, double offsetFeet)
        {
            var result = new List<CurveLoop>();
            for (int i = 0; i < loops.Count; i++)
            {
                var loop = Oriented(loops[i], i == 0);
                if (Math.Abs(offsetFeet) < 1e-9)
                {
                    result.Add(loop);
                    continue;
                }
                // Region area grows when the outer loop grows and the holes shrink.
                double before = Math.Abs(SignedArea(loop));
                CurveLoop moved;
                try
                {
                    moved = CurveLoop.CreateViaOffset(loop, offsetFeet, XYZ.BasisZ);
                    double after = Math.Abs(SignedArea(moved));
                    bool grew = after > before;
                    bool wantGrow = i == 0 ? offsetFeet > 0 : offsetFeet < 0;
                    if (grew != wantGrow) moved = CurveLoop.CreateViaOffset(loop, -offsetFeet, XYZ.BasisZ);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    throw new ToolException("INVALID_GEOMETRY",
                        $"The outline cannot be offset by {Fmt(Mm(offsetFeet))}: {ex.Message}",
                        "Use a smaller boundaryOffset.");
                }
                result.Add(Oriented(moved, i == 0));
            }
            return result;
        }

        /// <summary>Moves every curve of the loops to elevation <paramref name="zFeet"/>.</summary>
        public static IList<CurveLoop> AtElevation(IList<CurveLoop> loops, double zFeet)
        {
            var result = new List<CurveLoop>();
            foreach (var loop in loops)
            {
                var curves = new List<Curve>();
                foreach (var c in loop)
                {
                    var dz = zFeet - c.GetEndPoint(0).Z;
                    curves.Add(Math.Abs(dz) < 1e-9 ? c : c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, dz))));
                }
                result.Add(CurveLoop.Create(curves));
            }
            return result;
        }

        /// <summary>Area of a region (outer minus holes) in square feet.</summary>
        public static double RegionArea(IList<CurveLoop> loops)
        {
            if (loops.Count == 0) return 0;
            double a = Math.Abs(SignedArea(loops[0]));
            for (int i = 1; i < loops.Count; i++) a -= Math.Abs(SignedArea(loops[i]));
            return a;
        }


        public static XYZ Flatten(XYZ p, double z = 0) => new XYZ(p.X, p.Y, z);

        /// <summary>Whether a point lies inside a polygon (plan, even-odd).</summary>
        public static bool Contains(IList<XYZ> polygon, XYZ p)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) &&
                    p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        public static double DoubleParam(Element e, BuiltInParameter bip)
        {
            var p = e?.get_Parameter(bip);
            return p != null && p.HasValue && p.StorageType == StorageType.Double ? p.AsDouble() : double.NaN;
        }

        /// <summary>Sets a length/double built-in parameter and reads it back; throws PARAMETER_NOT_APPLIED.</summary>
        public static void SetDouble(Element e, BuiltInParameter bip, double valueFeet, string label)
        {
            var p = e.get_Parameter(bip);
            if (p == null)
                throw new ToolException("PARAMETER_NOT_FOUND", $"The element {e.Id.GetValue()} has no '{label}' parameter.");
            if (p.IsReadOnly)
                throw new ToolException("PARAMETER_READ_ONLY", $"'{label}' is read-only on element {e.Id.GetValue()}.");
            p.Set(valueFeet);
            if (Math.Abs(p.AsDouble() - valueFeet) > 1e-6)
                throw new ToolException("PARAMETER_NOT_APPLIED",
                    $"Revit kept '{label}' at {Fmt(Mm(p.AsDouble()))} instead of {Fmt(Mm(valueFeet))}.");
        }

    }
}
