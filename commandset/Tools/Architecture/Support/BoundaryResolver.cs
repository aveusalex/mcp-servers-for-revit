using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitMCPCommandSet.Framework;

namespace RevitMCPCommandSet.Tools.Architecture.Support
{
    /// <summary>A resolved outline: loops ready for Floor.Create / Ceiling.Create, plus where they came from.</summary>
    public sealed class ResolvedBoundary
    {
        public IList<CurveLoop> Loops { get; set; }
        public Room Room { get; set; }
        public string Source { get; set; }
    }

    /// <summary>Turns a <see cref="SurfaceBoundary"/> into curve loops (shared by floors, ceilings, countertops, envelopes).</summary>
    public static class BoundaryResolver
    {
        public static void Validate(SurfaceBoundary b, string label)
        {
            if (b == null) throw new ToolException("INVALID_ARGUMENTS", $"{label}: boundary is required.");
            int given = (b.Points != null && b.Points.Count > 0 ? 1 : 0) + (b.Rectangle != null ? 1 : 0) +
                        (string.IsNullOrWhiteSpace(b.Room) ? 0 : 1);
            if (given != 1)
                throw new ToolException("INVALID_ARGUMENTS", $"{label}: give exactly one of boundary.points, boundary.rectangle or boundary.room.");
            if (!string.IsNullOrWhiteSpace(b.Room) && b.Holes != null && b.Holes.Count > 0)
                throw new ToolException("INVALID_ARGUMENTS", $"{label}: holes only apply to points/rectangle; a room boundary already includes its islands.");
        }

        /// <summary>Loops at elevation <paramref name="zFeet"/>. <paramref name="level"/> filters room lookup by name/number.</summary>
        public static ResolvedBoundary Resolve(Document doc, SurfaceBoundary b, double zFeet, Level level = null)
        {
            IList<CurveLoop> loops;
            Room room = null;
            string source;
            if (!string.IsNullOrWhiteSpace(b.Room))
            {
                room = RoomSupport.Resolve(doc, b.Room, level);
                loops = RoomSupport.Outline(doc, room, RoomSupport.ParseLocation(b.BoundaryLocation), zFeet).Loops;
                source = "room:" + (b.BoundaryLocation ?? "finish");
            }
            else
            {
                var outer = b.Rectangle != null
                    ? ArchGeometry.RectanglePoints(b.Rectangle, zFeet)
                    : ArchGeometry.ToXyz(b.Points, zFeet);
                loops = new List<CurveLoop> { ArchGeometry.PolygonLoop(doc, outer, "outline") };
                int i = 0;
                foreach (var hole in b.Holes ?? new List<PolygonMm>())
                {
                    if (hole?.Points == null) continue;
                    var holeLoop = ArchGeometry.PolygonLoop(doc, ArchGeometry.ToXyz(hole.Points, zFeet), $"hole {i}");
                    var outerPts = ArchGeometry.LoopPoints(loops[0]);
                    if (ArchGeometry.LoopPoints(holeLoop).Any(p => !ArchGeometry.Contains(outerPts, p)))
                        throw new ToolException("INVALID_GEOMETRY", $"Hole {i} is not fully inside the outline.");
                    loops.Add(holeLoop);
                    i++;
                }
                source = b.Rectangle != null ? "rectangle" : "points";
            }
            loops = ArchGeometry.OffsetRegion(loops, ArchGeometry.Ft(b.BoundaryOffset));
            return new ResolvedBoundary { Loops = loops, Room = room, Source = source };
        }
    }
}
