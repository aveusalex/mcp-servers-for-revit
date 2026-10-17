using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture.Support
{
    /// <summary>Options of a perimeter profile (baseboard / rodapé or ceiling trim / tabica) around a room.</summary>
    public sealed class PerimeterOptions
    {
        public string Kind { get; set; } = "baseboard";
        public string Method { get; set; } = "line_family";
        public FamilySymbol LineSymbol { get; set; }
        public ElementType SweepType { get; set; }

        /// <summary>Height above the room's level in feet; null = 0 for baseboards, the ceiling height for ceiling trims.</summary>
        public double? ElevationFt { get; set; }

        public SpatialElementBoundaryLocation BoundaryLocation { get; set; } = SpatialElementBoundaryLocation.Finish;
        public bool SkipOpenings { get; set; } = true;
        public bool SkipSeparationLines { get; set; } = true;
        public double MinSegmentFt { get; set; } = 50 / 304.8;
        public bool ReverseDirection { get; set; }
    }

    /// <summary>
    /// Runs a profile along a room's boundary: either line-based family instances on each boundary piece minus the
    /// door openings (exact per room), or wall sweeps on the room side of each bounding wall (follows the whole wall
    /// and is cut by inserts natively).
    /// </summary>
    public static class PerimeterProfiles
    {
        /// <summary>Wall sweeps already created in this call (wall, side, height) so rooms sharing a wall do not duplicate.</summary>
        public sealed class SweepRegistry
        {
            public HashSet<string> Keys { get; } = new HashSet<string>();
        }

        public static object ForRoom(ToolContext ctx, Room room, PerimeterOptions o, SweepRegistry sweeps, List<long> createdIds)
        {
            var doc = ctx.Doc;
            var level = room.Level ?? throw new ToolException("ROOM_NOT_PLACED", $"Room {RoomSupport.Describe(room)} has no level.");
            double elevation = o.ElevationFt ?? (o.Kind == "ceiling_trim" ? CeilingHeightOver(doc, room)
                ?? throw new ToolException("CEILING_NOT_FOUND",
                    $"Room {RoomSupport.Describe(room)} has no ceiling over it, so the ceiling trim height is unknown.",
                    "Create the ceiling first (create_ceiling) or pass elevation.", "create_ceiling") : 0.0);

            var outline = RoomSupport.Outline(doc, room, o.BoundaryLocation, level.Elevation);
            var skipped = new List<object>();
            var ids = new List<long>();
            double total = 0;
            int openings = 0;

            if (o.Method == "wall_sweep")
            {
                foreach (var piece in outline.Pieces)
                {
                    if (!(piece.Bounding is Wall wall))
                    {
                        skipped.Add(new { reason = piece.Bounding == null ? "no bounding element" : "not a wall (" + piece.Bounding.Category?.Name + ")", lengthMm = ArchGeometry.Mm(piece.Curve.Length) });
                        continue;
                    }
                    if (wall.WallType.Kind != WallKind.Basic)
                    {
                        skipped.Add(new { reason = "curtain/stacked wall " + wall.Id.GetValue(), lengthMm = ArchGeometry.Mm(piece.Curve.Length) });
                        continue;
                    }
                    var dir = (piece.Curve.GetEndPoint(1) - piece.Curve.GetEndPoint(0)).Normalize();
                    var inward = XYZ.BasisZ.CrossProduct(dir); // the room is on the left of each piece
                    var side = inward.DotProduct(wall.Orientation) > 0 ? WallSide.Exterior : WallSide.Interior;
                    var baseLevel = doc.GetElement(wall.LevelId) as Level;
                    var baseOffset = ArchGeometry.DoubleParam(wall, BuiltInParameter.WALL_BASE_OFFSET);
                    var wallBase = (baseLevel?.Elevation ?? 0) + (double.IsNaN(baseOffset) ? 0 : baseOffset);
                    var distance = level.Elevation + elevation - wallBase;
                    var key = $"{wall.Id.GetValue()}|{side}|{Math.Round(distance * 304.8)}";
                    if (!sweeps.Keys.Add(key))
                    {
                        skipped.Add(new { reason = $"wall {wall.Id.GetValue()} already got this sweep in this call", lengthMm = ArchGeometry.Mm(piece.Curve.Length) });
                        continue;
                    }
                    if (distance < 0)
                    {
                        skipped.Add(new { reason = $"profile height is below the base of wall {wall.Id.GetValue()}", lengthMm = ArchGeometry.Mm(piece.Curve.Length) });
                        continue;
                    }
                    var info = new WallSweepInfo(WallSweepType.Sweep, false)
                    {
                        WallSide = side,
                        Distance = distance,
                        DistanceMeasuredFrom = DistanceMeasuredFrom.Base,
                        WallOffset = 0,
                        IsCutByInserts = o.SkipOpenings
                    };
                    var sweep = WallSweep.Create(wall, o.SweepType.Id, info)
                                ?? throw new ToolException("CREATE_FAILED", $"Revit did not create the sweep on wall {wall.Id.GetValue()}.");
                    ids.Add(sweep.Id.GetValue());
                    var wallLength = ((LocationCurve)wall.Location).Curve.Length;
                    total += wallLength;
                    if (wallLength > piece.Curve.Length + ArchGeometry.Ft(50))
                        ctx.Warnings.Add(new ToolWarning(
                            $"Room {room.Number}: the sweep on wall {wall.Id.GetValue()} runs the whole wall ({ArchGeometry.Fmt(ArchGeometry.Mm(wallLength))}), " +
                            $"beyond this room's {ArchGeometry.Fmt(ArchGeometry.Mm(piece.Curve.Length))}. Use method line_family for an exact per-room profile.",
                            new List<long> { sweep.Id.GetValue() }));
                }
            }
            else
            {
                TypeFinder.Activate(doc, o.LineSymbol);
                foreach (var piece in outline.Pieces)
                {
                    var c = piece.Curve;
                    bool isSeparation = piece.Bounding is ModelCurve;
                    if (isSeparation && o.SkipSeparationLines)
                    {
                        skipped.Add(new { reason = "room separation line", lengthMm = ArchGeometry.Mm(c.Length) });
                        continue;
                    }
                    if (!(c is Line line))
                    {
                        skipped.Add(new { reason = "curved boundary (line-based families need straight pieces)", lengthMm = ArchGeometry.Mm(c.Length) });
                        continue;
                    }
                    var cuts = new List<(double from, double to)>();
                    if (o.SkipOpenings && piece.Bounding is Wall wall) cuts = OpeningIntervals(doc, wall, line, level, elevation, ref openings);
                    foreach (var (a, b) in Subtract(0, line.Length, cuts))
                    {
                        if (b - a < o.MinSegmentFt)
                        {
                            if (b - a > 1e-6) skipped.Add(new { reason = "shorter than minSegment", lengthMm = ArchGeometry.Mm(b - a) });
                            continue;
                        }
                        var p0 = line.GetEndPoint(0) + line.Direction * a;
                        var p1 = line.GetEndPoint(0) + line.Direction * b;
                        var seg = o.ReverseDirection ? Line.CreateBound(p1, p0) : Line.CreateBound(p0, p1);
                        var fi = ctx.Doc.Create.NewFamilyInstance(seg, o.LineSymbol, level, StructuralType.NonStructural)
                                 ?? throw new ToolException("CREATE_FAILED", "Revit did not place the profile family.");
                        if (Math.Abs(elevation) > 1e-9) FamilyPlacement.SetElevation(fi, elevation);
                        ids.Add(fi.Id.GetValue());
                        total += seg.Length;
                    }
                }
            }

            doc.Regenerate();
            foreach (var id in ids)
            {
                if (doc.GetElement(id.ToRevitElementId()) == null)
                    throw new ToolException("CREATE_FAILED", $"Revit removed profile element {id} right after creating it.");
            }
            createdIds.AddRange(ids);
            return new
            {
                room = RoomSupport.Summary(room),
                kind = o.Kind,
                method = o.Method,
                elevationMm = ArchGeometry.Mm(elevation),
                ids,
                count = ids.Count,
                totalLengthM = ArchGeometry.M(total),
                openingsSkipped = openings,
                skippedPieces = skipped
            };
        }

        /// <summary>Intervals (along the piece, feet) of the wall's inserts whose height range covers the profile.</summary>
        private static List<(double, double)> OpeningIntervals(Document doc, Wall wall, Line piece, Level roomLevel, double elevation, ref int count)
        {
            var list = new List<(double, double)>();
            foreach (var id in wall.FindInserts(true, false, false, false))
            {
                if (!(doc.GetElement(id) is FamilyInstance fi) || !(fi.Location is LocationPoint lp)) continue;
                var width = PlaceDoorWindowWidth(fi);
                if (width == null) continue;
                var insertLevel = doc.GetElement(fi.LevelId) as Level ?? roomLevel;
                var sill = ArchGeometry.DoubleParam(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                var head = ArchGeometry.DoubleParam(fi, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM);
                double bottom = insertLevel.Elevation + (double.IsNaN(sill) ? 0 : sill) - roomLevel.Elevation;
                double top = double.IsNaN(head) ? double.MaxValue : insertLevel.Elevation + head - roomLevel.Elevation;
                // Profiles sit on a small band above `elevation`; an opening interrupts them when it spans that height.
                if (elevation < bottom - ArchGeometry.Ft(5) || elevation > top) continue;
                var pt = new XYZ(lp.Point.X, lp.Point.Y, piece.GetEndPoint(0).Z);
                var t = (pt - piece.GetEndPoint(0)).DotProduct(piece.Direction);
                var off = (pt - (piece.GetEndPoint(0) + piece.Direction * t)).GetLength();
                if (off > wall.Width + ArchGeometry.Ft(10)) continue; // the insert is in another part of the wall
                if (t + width.Value / 2 < 0 || t - width.Value / 2 > piece.Length) continue;
                list.Add((t - width.Value / 2, t + width.Value / 2));
                count++;
            }
            return list;
        }

        private static double? PlaceDoorWindowWidth(FamilyInstance fi)
        {
            foreach (var bip in new[] { BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM })
            {
                var v = ArchGeometry.DoubleParam(fi, bip);
                if (double.IsNaN(v) || v <= 0) v = ArchGeometry.DoubleParam(fi.Symbol, bip);
                if (!double.IsNaN(v) && v > 0) return v;
            }
            return null;
        }

        /// <summary>[from, to] minus the cut intervals.</summary>
        public static IList<(double, double)> Subtract(double from, double to, IList<(double from, double to)> cuts)
        {
            var result = new List<(double, double)>();
            double cursor = from;
            foreach (var (a, b) in cuts.OrderBy(c => c.from))
            {
                if (b <= cursor) continue;
                if (a > cursor) result.Add((cursor, Math.Min(a, to)));
                cursor = Math.Max(cursor, b);
                if (cursor >= to) break;
            }
            if (cursor < to) result.Add((cursor, to));
            return result.Where(r => r.Item2 > r.Item1).ToList();
        }

        /// <summary>Height (feet above the room's level) of the lowest ceiling over the room's location point.</summary>
        public static double? CeilingHeightOver(Document doc, Room room)
        {
            var at = (room.Location as LocationPoint)?.Point;
            if (at == null || room.Level == null) return null;
            double? best = null;
            foreach (var c in new FilteredElementCollector(doc).OfClass(typeof(Ceiling)).Cast<Ceiling>())
            {
                var bb = c.get_BoundingBox(null);
                if (bb == null || at.X < bb.Min.X || at.X > bb.Max.X || at.Y < bb.Min.Y || at.Y > bb.Max.Y) continue;
                var lvl = doc.GetElement(c.LevelId) as Level;
                var h = ArchGeometry.DoubleParam(c, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                if (lvl == null || double.IsNaN(h)) continue;
                var rel = lvl.Elevation + h - room.Level.Elevation;
                var upper = room.UpperLimit != null ? room.UpperLimit.Elevation + room.LimitOffset - room.Level.Elevation : rel;
                if (rel <= 0 || rel > upper + 1e-6) continue;
                if (best == null || rel < best) best = rel;
            }
            return best;
        }
    }
}
