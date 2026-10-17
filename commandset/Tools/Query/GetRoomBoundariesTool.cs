using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Query.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class GetRoomBoundariesArgs
    {
        [JsonProperty("ids", Required = Required.Always)]
        [Description("Room, area or space ids (1-50).")]
        public List<long> Ids { get; set; }

        [JsonProperty("boundaryLocation")]
        [Description("Which wall face bounds a room/space: finish (default), center, coreCenter or coreBoundary. Areas ignore it.")]
        [EnumValues("finish", "center", "coreCenter", "coreBoundary")]
        [DefaultValue("finish")]
        public string BoundaryLocation { get; set; } = "finish";

        [JsonProperty("includeBoundaryElements")]
        [Description("Return the bounding element of each segment (id, category, link) (default true).")]
        [DefaultValue(true)]
        public bool IncludeBoundaryElements { get; set; } = true;
    }

    /// <summary>CON-13: boundary loops of rooms/areas/spaces.</summary>
    [McpTool("get_room_boundaries", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "Boundary loops of rooms, areas or MEP spaces: for each loop its segments (curve type, start/end " +
                      "and midpoint in mm, length mm) and the bounding element (wall, room separation line, column...; " +
                      "linkInstanceId + linkedElementId when the element is inside a Revit link). The first loop is " +
                      "the outer one, the others are holes. boundaryLocation picks the wall face (finish, center, core). " +
                      "Unplaced or unenclosed rooms return no loops and their status. Read-only.")]
    public sealed class GetRoomBoundariesTool : McpTool<GetRoomBoundariesArgs>
    {
        protected override void Validate(GetRoomBoundariesArgs args)
        {
            if (args.Ids == null || args.Ids.Count == 0 || args.Ids.Count > 50)
                throw new ToolException("INVALID_ARGUMENTS", "ids must list 1 to 50 rooms, areas or spaces.");
        }

        protected override object Run(ToolContext ctx, GetRoomBoundariesArgs args)
        {
            var doc = ctx.Doc;
            var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = Location(args.BoundaryLocation) };
            var items = new List<object>();
            var notFound = new List<object>();
            foreach (var id in args.Ids.Distinct())
            {
                if (!(doc.GetElement(id.ToRevitElementId()) is SpatialElement s))
                {
                    notFound.Add(new { id, reason = "not a room, area or space in the target document" });
                    continue;
                }
                var status = ListSpatialElementsTool.StatusOf(s, options);
                var loops = new List<object>();
                if (status == "placed")
                {
                    IList<IList<BoundarySegment>> segs = null;
                    try { segs = s.GetBoundarySegments(options); }
                    catch { /* leave empty */ }
                    foreach (var loop in segs ?? new List<IList<BoundarySegment>>())
                    {
                        double length = 0;
                        var segments = new List<object>();
                        foreach (var seg in loop)
                        {
                            var c = seg.GetCurve();
                            if (c == null) continue;
                            length += c.Length;
                            segments.Add(Segment(doc, c, seg, args.IncludeBoundaryElements));
                        }
                        loops.Add(new { lengthMm = QueryHelpers.Mm(length), segments });
                    }
                }
                items.Add(new
                {
                    id,
                    number = s.Number,
                    name = ListSpatialElementsTool.NameOf(s),
                    category = s.Category?.Name,
                    status,
                    level = s.Level?.Name,
                    areaM2 = QueryHelpers.M2(s.Area),
                    loops
                });
            }
            return new { boundaryLocation = args.BoundaryLocation, items, notFound };
        }

        private static object Segment(Document doc, Curve c, BoundarySegment seg, bool withElement)
        {
            object element = null;
            if (withElement)
            {
                var hostId = seg.ElementId;
                var linkedId = seg.LinkElementId;
                if (hostId != null && hostId != ElementId.InvalidElementId)
                {
                    var e = doc.GetElement(hostId);
                    if (e is RevitLinkInstance link)
                    {
                        var linked = linkedId != null && linkedId != ElementId.InvalidElementId ? link.GetLinkDocument()?.GetElement(linkedId) : null;
                        element = new
                        {
                            linkInstanceId = hostId.GetValue(),
                            linkedElementId = QueryHelpers.IdOrNull(linkedId),
                            reference = linkedId != null && linkedId != ElementId.InvalidElementId ? $"{hostId.GetValue()}:{linkedId.GetValue()}" : null,
                            category = linked?.Category?.Name,
                            name = QueryHelpers.SafeName(linked)
                        };
                    }
                    else
                    {
                        element = new { id = hostId.GetValue(), category = e?.Category?.Name, name = QueryHelpers.SafeName(e) };
                    }
                }
            }
            return new
            {
                curveType = c is Line ? "line" : c is Arc ? "arc" : c.GetType().Name,
                start = QueryHelpers.Point(c.GetEndPoint(0)),
                end = QueryHelpers.Point(c.GetEndPoint(1)),
                mid = QueryHelpers.Point(c.Evaluate(0.5, true)),
                lengthMm = QueryHelpers.Mm(c.Length),
                element
            };
        }

        private static SpatialElementBoundaryLocation Location(string s)
        {
            switch ((s ?? "finish").Trim().ToLowerInvariant())
            {
                case "center": return SpatialElementBoundaryLocation.Center;
                case "corecenter": return SpatialElementBoundaryLocation.CoreCenter;
                case "coreboundary": return SpatialElementBoundaryLocation.CoreBoundary;
                default: return SpatialElementBoundaryLocation.Finish;
            }
        }
    }
}
