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
    public sealed class GetElementRoomsArgs
    {
        [JsonProperty("ids", Required = Required.Always)]
        [Description("Element ids (doors, windows, furniture, equipment, any placed element), 1-500.")]
        public List<long> Ids { get; set; }

        [JsonProperty("phase")]
        [Description("Phase to evaluate rooms in (name or id). Default: the project's last phase.")]
        public string Phase { get; set; }

        [JsonProperty("includeSpaces")]
        [Description("Also return the MEP space containing the element (default false).")]
        [DefaultValue(false)]
        public bool IncludeSpaces { get; set; }
    }

    /// <summary>CON-15: room / from-room / to-room of elements, per phase.</summary>
    [McpTool("get_element_rooms", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "Which room each element is in, for a phase (default: the last phase). Family instances use " +
                      "Revit's Room, From Room and To Room (doors/windows: the two sides; null = exterior). Other " +
                      "elements fall back to the room at their location point, curve midpoint or bounding-box centre " +
                      "(method reported, with a warning). Optionally returns the MEP space too. Read-only.")]
    public sealed class GetElementRoomsTool : McpTool<GetElementRoomsArgs>
    {
        protected override void Validate(GetElementRoomsArgs args)
        {
            if (args.Ids == null || args.Ids.Count == 0 || args.Ids.Count > 500)
                throw new ToolException("INVALID_ARGUMENTS", "ids must list 1 to 500 elements.");
        }

        protected override object Run(ToolContext ctx, GetElementRoomsArgs args)
        {
            var doc = ctx.Doc;
            var phase = QueryHelpers.PhaseOrLast(ctx, args.Phase);
            var items = new List<object>();
            var notFound = new List<long>();
            var fallback = new List<long>();

            foreach (var id in args.Ids.Distinct())
            {
                var e = doc.GetElement(id.ToRevitElementId());
                if (e == null)
                {
                    notFound.Add(id);
                    continue;
                }

                object room = null, from = null, to = null, space = null;
                string method;
                if (e is FamilyInstance fi)
                {
                    method = "familyInstance";
                    room = RoomRef(SafeGet(() => fi.get_Room(phase)));
                    from = RoomRef(SafeGet(() => fi.get_FromRoom(phase)));
                    to = RoomRef(SafeGet(() => fi.get_ToRoom(phase)));
                    if (args.IncludeSpaces) space = SpaceRef(SafeGet(() => fi.get_Space(phase)));
                    if (room == null && from == null && to == null)
                    {
                        var p = QueryHelpers.RepresentativePoint(e, out _);
                        if (p != null)
                        {
                            room = RoomRef(SafeGet(() => doc.GetRoomAtPoint(p, phase)));
                            if (room != null) method = "familyInstance+point";
                        }
                    }
                }
                else
                {
                    var p = QueryHelpers.RepresentativePoint(e, out var how);
                    method = how == null ? "none" : "point:" + how;
                    if (p != null)
                    {
                        room = RoomRef(SafeGet(() => doc.GetRoomAtPoint(p, phase)));
                        if (args.IncludeSpaces) space = SpaceRef(SafeGet(() => doc.GetSpaceAtPoint(p, phase)));
                    }
                    fallback.Add(id);
                }

                items.Add(new
                {
                    id,
                    name = QueryHelpers.SafeName(e),
                    category = e.Category?.Name,
                    method,
                    room,
                    fromRoom = from,
                    toRoom = to,
                    space
                });
            }

            if (fallback.Count > 0)
                ctx.Warnings.Add(new ToolWarning(
                    $"{fallback.Count} element(s) are not family instances: their room was found by point, which may miss elements on a boundary or above the room's height.",
                    fallback.Take(50).ToList()));

            return new { phase = phase.Name, phaseId = phase.Id.GetValue(), items, notFound };
        }

        private static T SafeGet<T>(System.Func<T> f) where T : class
        {
            try { return f(); }
            catch { return null; }
        }

        private static object RoomRef(Room r) =>
            r == null ? null : new { id = r.Id.GetValue(), number = r.Number, name = ListSpatialElementsTool.NameOf(r), level = r.Level?.Name };

        private static object SpaceRef(Space s) =>
            s == null ? null : new { id = s.Id.GetValue(), number = s.Number, name = ListSpatialElementsTool.NameOf(s) };
    }
}
