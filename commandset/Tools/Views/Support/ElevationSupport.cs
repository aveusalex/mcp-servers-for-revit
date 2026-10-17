using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;

namespace RevitMCPCommandSet.Tools.Views.Support
{
    /// <summary>Elevation markers and their views, by the compass direction the camera looks to.</summary>
    internal static class ElevationSupport
    {
        public static readonly string[] Directions = { "north", "east", "south", "west" };

        public static string Letter(string direction) => direction.Substring(0, 1).ToUpperInvariant();

        /// <summary>Direction the camera of a section/elevation looks to (model axes: +Y north, +X east).</summary>
        public static string LooksTo(View view)
        {
            var look = view.ViewDirection.Negate();
            if (Math.Abs(look.X) >= Math.Abs(look.Y)) return look.X >= 0 ? "east" : "west";
            return look.Y >= 0 ? "north" : "south";
        }

        /// <summary>
        /// Creates a marker at <paramref name="point"/> (ft) on <paramref name="plan"/> and one elevation per requested
        /// direction. Revit's marker indices are not documented as a compass mapping, so every slot is created, each
        /// view's real direction is read, and the slots that were not requested are deleted again.
        /// </summary>
        public static Dictionary<string, ViewSection> Create(Document doc, ViewFamilyType type, ViewPlan plan, XYZ point,
            int scale, IList<string> directions)
        {
            var marker = ElevationMarker.CreateElevationMarker(doc, type.Id, point, scale);
            if (marker == null)
                throw new ToolException("ELEVATION_NOT_CREATED", "Revit did not create the elevation marker.");
            var made = new List<ViewSection>();
            for (var i = 0; i < marker.MaximumViewCount; i++)
            {
                if (!marker.IsAvailableIndex(i)) continue;
                var v = marker.CreateElevation(doc, plan.Id, i);
                if (v != null) made.Add(v);
            }
            doc.Regenerate();

            var result = new Dictionary<string, ViewSection>(StringComparer.OrdinalIgnoreCase);
            var discard = new List<ElementId>();
            foreach (var v in made)
            {
                var d = LooksTo(v);
                if (directions.Contains(d, StringComparer.OrdinalIgnoreCase) && !result.ContainsKey(d)) result[d] = v;
                else discard.Add(v.Id);
            }
            var missing = directions.Where(d => !result.ContainsKey(d)).ToList();
            if (missing.Count > 0)
                throw new ToolException("ELEVATION_NOT_CREATED",
                    $"The elevation marker produced no view looking {string.Join("/", missing)}.");
            if (discard.Count > 0) doc.Delete(discard);
            return result;
        }

        /// <summary>
        /// Crops a section/elevation to the projection of <paramref name="corners"/> (+ padding, ft) and sets the far
        /// clip just beyond the farthest corner. Read back by the caller through <see cref="ViewSupport.DescribeCrop"/>.
        /// </summary>
        public static void CropTo(View view, IList<XYZ> corners, double paddingFt)
        {
            ViewSupport.CropToPoints(view, corners, paddingFt);
            var look = view.ViewDirection.Negate();
            var far = corners.Max(p => (p - view.Origin).DotProduct(look)) + paddingFt;
            if (far <= 0) return;
            var mode = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
            if (mode != null && !mode.IsReadOnly && mode.AsInteger() == 0) mode.Set(1);
            var offset = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
            if (offset != null && !offset.IsReadOnly) offset.Set(far);
        }
    }
}
