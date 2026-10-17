using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Query;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing.Support
{
    /// <summary>A direction (dimensionless; only its orientation matters).</summary>
    public sealed class DirectionArg
    {
        [JsonProperty("dx", Required = Required.Always)]
        [Description("X component of the direction (any scale).")]
        public double Dx { get; set; }

        [JsonProperty("dy", Required = Required.Always)]
        [Description("Y component of the direction (any scale).")]
        public double Dy { get; set; }

        [JsonProperty("dz", Required = Required.Always)]
        [Description("Z component of the direction (any scale).")]
        public double Dz { get; set; }
    }

    /// <summary>Wire (mm) ↔ internal (feet) points and the anchor point of an element.</summary>
    public static class EditGeometry
    {
        public static double Ft(double mm) => UnitPolicy.ToInternal(mm, UnitSpec.Length);
        public static double Mm(double ft) => Math.Round(UnitPolicy.FromInternal(ft, UnitSpec.Length, out _), 3);

        public static XYZ ToXyz(PointMm p) => new XYZ(Ft(p.X), Ft(p.Y), Ft(p.Z));

        public static XYZ ToDirection(DirectionArg d, string label)
        {
            var v = new XYZ(d.Dx, d.Dy, d.Dz);
            if (v.GetLength() < 1e-9)
                throw new ToolException("INVALID_ARGUMENTS", $"{label} must not be a zero vector.");
            return v.Normalize();
        }

        public static object Point(XYZ p) => p == null ? null : new { x = Mm(p.X), y = Mm(p.Y), z = Mm(p.Z) };

        /// <summary>Location point, middle of the location curve, or centre of the bounding box.</summary>
        public static XYZ Anchor(Element e)
        {
            switch (e.Location)
            {
                case LocationPoint lp: return lp.Point;
                case LocationCurve lc when lc.Curve != null: return lc.Curve.Evaluate(0.5, true);
            }
            var bb = e.get_BoundingBox(null);
            return bb == null ? null : (bb.Min + bb.Max) / 2;
        }

        /// <summary>Where an element is, in mm: point (+ rotation) or curve ends.</summary>
        public static object Location(Element e)
        {
            switch (e.Location)
            {
                case LocationPoint lp:
                    double? rotation = null;
                    try { rotation = Math.Round(lp.Rotation * 180.0 / Math.PI, 6); } catch { /* not rotatable */ }
                    return new { point = Point(lp.Point), rotationDeg = rotation };
                case LocationCurve lc when lc.Curve != null:
                    return new { start = Point(lc.Curve.GetEndPoint(0)), end = Point(lc.Curve.GetEndPoint(1)) };
            }
            var bb = e.get_BoundingBox(null);
            return bb == null ? null : new { bboxCenter = Point((bb.Min + bb.Max) / 2) };
        }

        /// <summary>Centre of the combined bounding box of the elements (default rotation centre).</summary>
        public static XYZ Center(IEnumerable<Element> elements)
        {
            XYZ min = null, max = null;
            foreach (var e in elements)
            {
                var bb = e.get_BoundingBox(null);
                if (bb == null)
                {
                    var a = Anchor(e);
                    if (a == null) continue;
                    bb = new BoundingBoxXYZ { Min = a, Max = a };
                }
                min = min == null ? bb.Min : new XYZ(Math.Min(min.X, bb.Min.X), Math.Min(min.Y, bb.Min.Y), Math.Min(min.Z, bb.Min.Z));
                max = max == null ? bb.Max : new XYZ(Math.Max(max.X, bb.Max.X), Math.Max(max.Y, bb.Max.Y), Math.Max(max.Z, bb.Max.Z));
            }
            return min == null ? null : (min + max) / 2;
        }

        /// <summary>Elements by id; fails with ELEMENT_NOT_FOUND listing the missing ids.</summary>
        public static List<Element> Elements(Document doc, IEnumerable<long> ids)
        {
            var list = new List<Element>();
            var missing = new List<long>();
            foreach (var id in ids.Distinct())
            {
                var e = doc.GetElement(id.ToRevitElementId());
                if (e == null) missing.Add(id);
                else list.Add(e);
            }
            if (missing.Count > 0)
                throw new ToolException("ELEMENT_NOT_FOUND", "Element(s) not found: " + string.Join(", ", missing.Take(20)) + ".",
                    "Check the ids with find_elements against the same document.", "find_elements");
            return list;
        }
    }
}
