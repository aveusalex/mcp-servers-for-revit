using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture.Support
{
    /// <summary>One piece of a room boundary, with the element that bounds it (wall, separation line, column...).</summary>
    public sealed class BoundaryPiece
    {
        public Curve Curve { get; set; }
        public Element Bounding { get; set; }
        public int LoopIndex { get; set; }
    }

    /// <summary>A room boundary as closed loops: [0] is the outer loop (counter-clockwise), the rest are islands.</summary>
    public sealed class RoomOutline
    {
        public IList<CurveLoop> Loops { get; set; } = new List<CurveLoop>();
        public IList<BoundaryPiece> Pieces { get; set; } = new List<BoundaryPiece>();
    }

    /// <summary>Finds rooms by id / number / name and reads their boundaries.</summary>
    public static class RoomSupport
    {
        /// <summary>A placed room by id, number or name. Unplaced or unenclosed rooms fail with ROOM_NOT_PLACED.</summary>
        public static Room Resolve(Document doc, string reference, Level level = null)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new ToolException("INVALID_ARGUMENTS", "A room reference (id, number or name) is required.");
            var text = reference.Trim();
            Room room = null;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
                room = doc.GetElement(id.ToRevitElementId()) as Room;

            if (room == null)
            {
                var all = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                    .OfType<Room>().Where(r => level == null || r.LevelId == level.Id).ToList();
                var byNumber = all.Where(r => string.Equals((r.Number ?? "").Trim(), text, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byNumber.Count == 1) room = byNumber[0];
                else if (byNumber.Count > 1)
                    throw new ToolException("AMBIGUOUS_NAME",
                        $"Room number '{text}' is used by {byNumber.Count} rooms: {string.Join("; ", byNumber.Take(20).Select(Describe))}.",
                        "Pass the room id, or a level to narrow the search.");
                else
                    room = NameMatcher.Single(text, all.Select(r => new NamedCandidate<Room>(RoomName(r), r, Describe(r))), "room");
            }

            if (room == null)
                throw new ToolException("ROOM_NOT_FOUND", $"No room '{reference}' exists in the target document.");
            if (room.Location == null || room.Area <= 0)
                throw new ToolException("ROOM_NOT_PLACED",
                    $"Room {Describe(room)} is not placed or not enclosed (area 0), so it has no boundary.",
                    "Close the room with walls or room separation lines first (create_room_separation_lines).");
            return room;
        }

        public static IList<Room> PlacedRoomsOn(Document doc, Level level) =>
            new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Where(r => r.LevelId == level.Id && r.Location != null && r.Area > 0)
                .OrderBy(r => r.Number, StringComparer.OrdinalIgnoreCase).ToList();

        public static string RoomName(Room r) =>
            r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? r.Name;

        public static string Describe(Room r) => $"{r.Number} {RoomName(r)} (id {r.Id.GetValue()})".Trim();

        public static object Summary(Room r) => new
        {
            id = r.Id.GetValue(),
            number = r.Number,
            name = RoomName(r),
            level = r.Level?.Name,
            areaM2 = ArchGeometry.M2(r.Area)
        };

        public static SpatialElementBoundaryLocation ParseLocation(string text)
        {
            switch ((text ?? "finish").Trim().ToLowerInvariant())
            {
                case "center": return SpatialElementBoundaryLocation.Center;
                case "coreboundary": return SpatialElementBoundaryLocation.CoreBoundary;
                case "corecenter": return SpatialElementBoundaryLocation.CoreCenter;
                default: return SpatialElementBoundaryLocation.Finish;
            }
        }

        /// <summary>
        /// Boundary loops of a room at the chosen wall line, flattened to <paramref name="zFeet"/>. The outer loop is
        /// the one with the largest area and is returned counter-clockwise; islands (columns, shafts) clockwise.
        /// </summary>
        public static RoomOutline Outline(Document doc, Room room, SpatialElementBoundaryLocation location, double zFeet)
        {
            var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = location };
            var segments = room.GetBoundarySegments(options);
            if (segments == null || segments.Count == 0)
                throw new ToolException("ROOM_NOT_PLACED", $"Room {Describe(room)} has no boundary.",
                    "Check that the room is enclosed (it may be 'Not Enclosed' or redundant).");

            var raw = new List<(CurveLoop loop, List<BoundaryPiece> pieces)>();
            int index = 0;
            foreach (var loopSegments in segments)
            {
                var curves = new List<Curve>();
                var pieces = new List<BoundaryPiece>();
                foreach (var s in loopSegments)
                {
                    var c = s.GetCurve();
                    if (c == null) continue;
                    var dz = zFeet - c.GetEndPoint(0).Z;
                    if (Math.Abs(dz) > 1e-9) c = c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, dz)));
                    curves.Add(c);
                    Element bounding = null;
                    try
                    {
                        bounding = doc.GetElement(s.ElementId);
                        if (bounding is RevitLinkInstance) bounding = null;
                    }
                    catch
                    {
                        // linked or missing element: treat as unknown
                    }
                    pieces.Add(new BoundaryPiece { Curve = c, Bounding = bounding, LoopIndex = index });
                }
                if (curves.Count == 0) continue;
                CurveLoop loop;
                try
                {
                    loop = CurveLoop.Create(curves);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    throw new ToolException("BOUNDARY_INVALID",
                        $"The boundary of room {Describe(room)} does not form a closed loop: {ex.Message}",
                        "Draw the outline with points instead, or fix the gaps between the bounding walls.");
                }
                raw.Add((loop, pieces));
                index++;
            }
            if (raw.Count == 0)
                throw new ToolException("ROOM_NOT_PLACED", $"Room {Describe(room)} has no boundary.");

            var ordered = raw.OrderByDescending(r => Math.Abs(ArchGeometry.SignedArea(r.loop))).ToList();
            var outline = new RoomOutline();
            for (int i = 0; i < ordered.Count; i++)
            {
                bool outer = i == 0;
                var oriented = ArchGeometry.Oriented(ordered[i].loop, outer);
                outline.Loops.Add(oriented);
                bool flipped = !ReferenceEquals(oriented, ordered[i].loop);
                var pieces = ordered[i].pieces;
                if (flipped)
                {
                    pieces = pieces.AsEnumerable().Reverse()
                        .Select(p => new BoundaryPiece { Curve = p.Curve.CreateReversed(), Bounding = p.Bounding, LoopIndex = i }).ToList();
                }
                foreach (var p in pieces)
                {
                    p.LoopIndex = i;
                    outline.Pieces.Add(p);
                }
            }
            return outline;
        }
    }
}
