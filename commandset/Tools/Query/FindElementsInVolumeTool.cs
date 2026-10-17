using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Query.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class FindElementsInVolumeArgs
    {
        [JsonProperty("roomIds")]
        [Description("Rooms or MEP spaces whose volume is searched (1-50). Give roomIds or bbox.")]
        public List<long> RoomIds { get; set; }

        [JsonProperty("bbox")]
        [Description("Search box in mm (project internal coordinates), instead of rooms.")]
        public BoxMm Bbox { get; set; }

        [JsonProperty("categories")]
        [Description("Only these categories (any language: OST_Furniture / Furniture / Mobiliário). Default: all model categories.")]
        public List<string> Categories { get; set; }

        [JsonProperty("match")]
        [Description("inside (default): the element's location point / curve midpoint / box centre is inside; intersects: its geometry touches the volume.")]
        [EnumValues("inside", "intersects")]
        [DefaultValue("inside")]
        public string Match { get; set; } = "inside";

        [JsonProperty("limitPerVolume")]
        [Description("Most elements returned per room or box, 1-2000 (default 500); totals stay exact.")]
        [DefaultValue(500)]
        public int LimitPerVolume { get; set; } = 500;
    }

    /// <summary>CON-16: elements contained in rooms/spaces or in a 3D box.</summary>
    [McpTool("find_elements_in_volume", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "Find the model elements inside rooms / MEP spaces (their real 3D volume, limited by the room's " +
                      "height) or inside a 3D box in mm. match=inside tests the element's location point (curve " +
                      "midpoint or bounding-box centre when it has none); match=intersects tests the element's solid " +
                      "geometry against the room's closed shell (box: bounding boxes). Filter by categories (any " +
                      "language). Rooms, areas, spaces and annotations are never returned. To select the result in " +
                      "Revit pass the ids to select_elements. Read-only.")]
    public sealed class FindElementsInVolumeTool : McpTool<FindElementsInVolumeArgs>
    {
        private static readonly HashSet<long> Spatial = new HashSet<long>
        {
            (long)BuiltInCategory.OST_Rooms, (long)BuiltInCategory.OST_Areas, (long)BuiltInCategory.OST_MEPSpaces
        };

        protected override void Validate(FindElementsInVolumeArgs args)
        {
            var hasRooms = args.RoomIds != null && args.RoomIds.Count > 0;
            if (hasRooms == (args.Bbox != null))
                throw new ToolException("INVALID_ARGUMENTS", "Give either roomIds or bbox (exactly one).");
            if (hasRooms && args.RoomIds.Count > 50)
                throw new ToolException("INVALID_ARGUMENTS", "roomIds accepts at most 50 rooms per call.");
            if (args.Bbox != null && (args.Bbox.Min == null || args.Bbox.Max == null))
                throw new ToolException("INVALID_ARGUMENTS", "bbox needs min and max.");
            args.Match = (args.Match ?? "inside").Trim().ToLowerInvariant();
            if (args.LimitPerVolume < 1 || args.LimitPerVolume > 2000)
                throw new ToolException("INVALID_ARGUMENTS", "limitPerVolume must be between 1 and 2000.");
        }

        protected override object Run(ToolContext ctx, FindElementsInVolumeArgs args)
        {
            var doc = ctx.Doc;
            var categories = ctx.Services.Categories.ResolveAll(args.Categories);
            var volumes = new List<object>();
            var notFound = new List<object>();

            FilteredElementCollector Candidates(Outline outline, bool inside)
            {
                var c = new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent();
                if (categories.Count > 0) c = c.WherePasses(new ElementMulticategoryFilter(categories.Select(x => x.Id).ToList()));
                return inside ? c.WherePasses(new BoundingBoxIsInsideFilter(outline)) : c.WherePasses(new BoundingBoxIntersectsFilter(outline));
            }

            if (args.Bbox != null)
            {
                var a = ToXyz(args.Bbox.Min);
                var b = ToXyz(args.Bbox.Max);
                var outline = new Outline(new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
                    new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
                var found = Candidates(outline, args.Match == "inside").Where(IsModel).OrderBy(e => e.Id.GetValue()).ToList();
                volumes.Add(Result(doc, new { kind = "bbox" }, found, args.LimitPerVolume, "boundingBox"));
            }
            else
            {
                foreach (var id in args.RoomIds.Distinct())
                {
                    var s = doc.GetElement(id.ToRevitElementId()) as SpatialElement;
                    if (!(s is Room) && !(s is Space))
                    {
                        notFound.Add(new { id, reason = "not a room or MEP space" });
                        continue;
                    }
                    if (s.Location == null || s.Area <= 1e-9)
                    {
                        notFound.Add(new { id, reason = "room is not placed or not enclosed" });
                        continue;
                    }
                    var bb = s.get_BoundingBox(null);
                    if (bb == null)
                    {
                        notFound.Add(new { id, reason = "room has no geometry" });
                        continue;
                    }
                    var outline = new Outline(bb.Min, bb.Max);
                    var candidates = Candidates(outline, false).Where(IsModel).ToList();
                    List<Element> found;
                    string method;
                    if (args.Match == "intersects" && TryShell(s, out var solid))
                    {
                        method = "solidIntersection";
                        var ids = candidates.Select(e => e.Id).ToList();
                        found = ids.Count == 0
                            ? new List<Element>()
                            : new FilteredElementCollector(doc, ids).WherePasses(new ElementIntersectsSolidFilter(solid)).ToElements().ToList();
                        // Elements without solids (e.g. some MEP fixtures) are kept when their point is inside.
                        var seen = new HashSet<long>(found.Select(e => e.Id.GetValue()));
                        found.AddRange(candidates.Where(e => !seen.Contains(e.Id.GetValue()) && PointInside(s, e)));
                    }
                    else
                    {
                        method = args.Match == "intersects" ? "pointInside (room has no closed shell)" : "pointInside";
                        found = candidates.Where(e => PointInside(s, e)).ToList();
                    }
                    var room = new
                    {
                        kind = s is Room ? "room" : "space",
                        id,
                        number = s.Number,
                        name = ListSpatialElementsTool.NameOf(s),
                        level = s.Level?.Name
                    };
                    volumes.Add(Result(doc, room, found.OrderBy(e => e.Id.GetValue()).ToList(), args.LimitPerVolume, method));
                }
            }

            return new { match = args.Match, volumes, notFound };
        }

        private static object Result(Document doc, object volume, List<Element> found, int limit, string method) => new
        {
            volume,
            method,
            total = found.Count,
            truncated = found.Count > limit,
            elements = found.Take(limit).Select(e => QueryHelpers.Brief(doc, e)).ToList()
        };

        private static bool IsModel(Element e)
        {
            var cat = e.Category;
            if (cat == null || cat.CategoryType != CategoryType.Model) return false;
            if (Spatial.Contains(cat.Id.GetValue())) return false;
            // The internal origin and base/survey points are "model" elements at the origin, not contents of a room.
            if (e is BasePoint || e.GetType().Name == "InternalOrigin") return false;
            return !(e is RevitLinkInstance) && !(e is View);
        }

        private static bool PointInside(SpatialElement s, Element e)
        {
            var p = QueryHelpers.RepresentativePoint(e, out _);
            if (p == null) return false;
            // A point exactly on the room's floor may be reported outside: retry slightly above it.
            foreach (var dz in new[] { 0.0, 0.5 })
            {
                var q = new XYZ(p.X, p.Y, p.Z + dz);
                try
                {
                    if (s is Room r && r.IsPointInRoom(q)) return true;
                    if (s is Space sp && sp.IsPointInSpace(q)) return true;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        private static bool TryShell(SpatialElement s, out Solid solid)
        {
            solid = null;
            try
            {
                GeometryElement shell = s is Room r ? r.ClosedShell : (s as Space)?.ClosedShell;
                if (shell == null) return false;
                foreach (var g in shell)
                {
                    if (g is Solid so && so.Volume > 1e-9)
                    {
                        solid = so;
                        return true;
                    }
                }
            }
            catch
            {
                // no shell
            }
            return false;
        }

        private static XYZ ToXyz(PointMm p) =>
            new XYZ(QueryHelpers.FeetFromMm(p.X), QueryHelpers.FeetFromMm(p.Y), QueryHelpers.FeetFromMm(p.Z));
    }
}
