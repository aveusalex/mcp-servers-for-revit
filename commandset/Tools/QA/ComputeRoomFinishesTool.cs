using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.QA.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA
{
    public sealed class ComputeRoomFinishesArgs
    {
        [JsonProperty("roomIds")]
        [Description("Rooms to compute. Omit to use every placed room (optionally of one level).")]
        public List<long> RoomIds { get; set; }

        [JsonProperty("level")]
        [Description("Only rooms of this level (name or id).")]
        public string Level { get; set; }

        [JsonProperty("limit")]
        [Description("Rooms per page, 1-500 (default 100).")]
        [DefaultValue(100)]
        public int Limit { get; set; } = 100;

        [JsonProperty("offset")]
        [Description("Rooms to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>
    /// QA-23: finishes per room from the room's boundary geometry. Each face of the room solid is matched to the
    /// element face that bounds it (SpatialElementGeometryCalculator) and that face's material is the finish.
    /// </summary>
    [McpTool("compute_room_finishes", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "qa", Idempotent = true,
        TimeoutSeconds = 300,
        Description = "Compute finishes per room: floor, wall and ceiling areas in m2 grouped by the material of the " +
                      "bounding element's face and its type (from Revit's room geometry, finish faces), plus room area " +
                      "(m2) and perimeter (mm) for skirting, and totals per material for the page. Openings are not " +
                      "deducted beyond what Revit's room geometry already excludes. Rooms that are not enclosed are listed " +
                      "with the error. Read-only.")]
    public sealed class ComputeRoomFinishesTool : McpTool<ComputeRoomFinishesArgs>
    {
        private sealed class Acc
        {
            public string surface;
            public string material;
            public long? materialId;
            public string elementType;
            public double area;
        }

        protected override void Validate(ComputeRoomFinishesArgs a)
        {
            if (a.Limit < 1 || a.Limit > 500) throw new ToolException("INVALID_ARGUMENTS", "limit must be between 1 and 500.");
        }

        protected override object Run(ToolContext ctx, ComputeRoomFinishesArgs a)
        {
            var doc = ctx.Doc;
            List<Room> rooms;
            if (a.RoomIds != null && a.RoomIds.Count > 0)
            {
                rooms = new List<Room>();
                foreach (var id in a.RoomIds.Distinct())
                {
                    if (!(doc.GetElement(id.ToRevitElementId()) is Room r))
                        throw new ToolException("ELEMENT_NOT_FOUND", $"{id} is not a room in the target document.");
                    rooms.Add(r);
                }
            }
            else
            {
                rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                    .Cast<Room>().Where(r => r.Location != null).ToList();
            }
            if (!string.IsNullOrWhiteSpace(a.Level))
            {
                var level = ctx.Services.Lookup.Level(a.Level);
                rooms = rooms.Where(r => r.LevelId == level.Id).ToList();
            }
            rooms = rooms.OrderBy(r => r.Level?.Elevation ?? 0).ThenBy(r => r.Number, StringComparer.OrdinalIgnoreCase).ToList();

            var page = Paging.Page(rooms, a.Limit, a.Offset, 500);
            var calc = new SpatialElementGeometryCalculator(doc,
                new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish });
            var totals = new Dictionary<string, Acc>();
            var results = new List<object>();
            var failed = new List<object>();

            foreach (var room in page.Items)
            {
                if (room.Location == null || room.Area <= 0)
                {
                    failed.Add(new { id = room.Id.GetValue(), number = room.Number, name = room.Name, reason = room.Location == null ? "not placed" : "not enclosed (area 0)" });
                    continue;
                }
                SpatialElementGeometryResults geo;
                try
                {
                    geo = calc.CalculateSpatialElementGeometry(room);
                }
                catch (Exception ex)
                {
                    failed.Add(new { id = room.Id.GetValue(), number = room.Number, name = room.Name, reason = ex.Message });
                    continue;
                }

                var acc = new Dictionary<string, Acc>();
                var solid = geo.GetGeometry();
                foreach (Face face in solid.Faces)
                {
                    var surface = SurfaceOf(face);
                    IList<SpatialElementBoundarySubface> subfaces;
                    try { subfaces = geo.GetBoundaryFaceInfo(face); }
                    catch { subfaces = null; }
                    if (subfaces == null || subfaces.Count == 0)
                    {
                        Add(acc, surface, null, "(no bounding element: room separation or open)", face.Area);
                        continue;
                    }
                    foreach (var sf in subfaces)
                    {
                        double area;
                        try { area = sf.GetSubface()?.Area ?? 0; }
                        catch { area = 0; }
                        if (area <= 0) continue;
                        Element host = null;
                        Face bounding = null;
                        try
                        {
                            host = doc.GetElement(sf.SpatialBoundaryElement.HostElementId);
                            bounding = sf.GetBoundingElementFace();
                        }
                        catch
                        {
                            // linked or deleted bounding element
                        }
                        Material mat = null;
                        try
                        {
                            if (bounding != null && bounding.MaterialElementId != ElementId.InvalidElementId)
                                mat = doc.GetElement(bounding.MaterialElementId) as Material;
                        }
                        catch
                        {
                            // no material on that face
                        }
                        var typeName = host == null ? "(linked or unknown element)" : TypeLabel(host);
                        Add(acc, surface, mat, typeName, area);
                    }
                }

                foreach (var x in acc.Values)
                {
                    var key = x.surface + "|" + x.material + "|" + x.elementType;
                    if (!totals.TryGetValue(key, out var t)) totals[key] = t = new Acc { surface = x.surface, material = x.material, materialId = x.materialId, elementType = x.elementType };
                    t.area += x.area;
                }

                double perimeter = 0;
                try { perimeter = room.Perimeter; }
                catch { /* no perimeter */ }
                results.Add(new
                {
                    id = room.Id.GetValue(),
                    number = room.Number,
                    name = room.Name,
                    level = room.Level?.Name,
                    area = M2(room.Area),
                    perimeter = QaSupport.Mm(perimeter),
                    floor = Rows(acc, "floor"),
                    walls = Rows(acc, "wall"),
                    ceiling = Rows(acc, "ceiling")
                });
            }

            return new
            {
                units = new { area = "m2", perimeter = "mm" },
                roomCount = page.Total,
                rooms = results,
                failed,
                totalsByMaterial = totals.Values.OrderBy(t => t.surface).ThenByDescending(t => t.area)
                    .Select(t => new { t.surface, t.material, t.materialId, t.elementType, area = M2(t.area) }).ToList(),
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }

        private static void Add(Dictionary<string, Acc> acc, string surface, Material mat, string typeName, double area)
        {
            var matName = mat?.Name ?? "(no material)";
            var key = surface + "|" + matName + "|" + typeName;
            if (!acc.TryGetValue(key, out var x))
                acc[key] = x = new Acc { surface = surface, material = matName, materialId = mat?.Id.GetValue(), elementType = typeName };
            x.area += area;
        }

        private static List<object> Rows(Dictionary<string, Acc> acc, string surface) =>
            acc.Values.Where(x => x.surface == surface).OrderByDescending(x => x.area)
                .Select(x => (object)new { x.material, x.materialId, x.elementType, area = M2(x.area) }).ToList();

        private static string SurfaceOf(Face face)
        {
            XYZ n = null;
            try
            {
                var bb = face.GetBoundingBox();
                n = face.ComputeNormal((bb.Min + bb.Max) / 2);
            }
            catch
            {
                // fall through
            }
            if (n == null) return "wall";
            if (n.Z < -0.7) return "floor";
            if (n.Z > 0.7) return "ceiling";
            return "wall";
        }

        private static string TypeLabel(Element e)
        {
            var t = ParameterService.TypeOf(e);
            var cat = e.Category?.Name;
            if (t == null) return cat ?? QaSupport.SafeName(e);
            var fam = LookupService.SafeFamilyName(t);
            return string.IsNullOrEmpty(fam) ? $"{cat}: {t.Name}" : $"{fam}: {t.Name}";
        }

        private static double M2(double sqft) => Math.Round(UnitPolicy.FromInternal(sqft, UnitSpec.Area, out _), 3);
    }
}
