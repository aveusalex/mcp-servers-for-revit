using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Millwork.Support
{
    /// <summary>What is stored (Extensible Storage) on every part of a module.</summary>
    public sealed class PartRecord
    {
        [JsonProperty("v")] public int Version { get; set; } = 1;
        [JsonProperty("moduleId")] public string ModuleId { get; set; }
        [JsonProperty("part")] public MillworkPart Part { get; set; }
        [JsonProperty("materialId")] public long? MaterialId { get; set; }
        [JsonProperty("design")] public ResolvedDesign Design { get; set; }
    }

    /// <summary>A module found in the model: its parts (DirectShapes) and the group holding them.</summary>
    public sealed class ModuleInstance
    {
        public string ModuleId { get; set; }
        public Group Group { get; set; }
        public List<(DirectShape Shape, PartRecord Record)> Parts { get; } = new List<(DirectShape, PartRecord)>();
        public ResolvedDesign Design => Parts.Count > 0 ? Parts[0].Record.Design : null;
        public string Key => Group != null ? "group:" + Group.Id.GetValue() : "module:" + ModuleId;
    }

    /// <summary>Revit side of the millwork tools: building parts as DirectShapes, storage, lookup, placement.</summary>
    public static class MillworkRevit
    {
        public const string AppId = "RevitMCP";
        public const string DataPrefix = "millwork:";
        private static readonly Guid SchemaGuid = new Guid("5d3c9a2e-7b41-4f0e-9c6a-2e8b1d4f7a63");
        private const string Field = "Data";
        private const double MmPerFt = 304.8;

        // ---- storage ------------------------------------------------------------------------------------------------

        private static Schema GetSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;
            var sb = new SchemaBuilder(SchemaGuid);
            sb.SetSchemaName("RevitMCPMillworkPart");
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);
            sb.SetDocumentation("Millwork module part created by the Revit MCP (create_millwork_module).");
            sb.AddSimpleField(Field, typeof(string));
            return sb.Finish();
        }

        public static void Write(Element e, PartRecord record)
        {
            var schema = GetSchema();
            var entity = new Entity(schema);
            entity.Set(Field, JsonConvert.SerializeObject(record));
            e.SetEntity(entity);
        }

        public static PartRecord Read(Element e)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null || e == null) return null;
            Entity entity;
            try { entity = e.GetEntity(schema); }
            catch { return null; }
            if (entity == null || !entity.IsValid()) return null;
            var json = entity.Get<string>(Field);
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonConvert.DeserializeObject<PartRecord>(json); }
            catch { return null; }
        }

        // ---- building -----------------------------------------------------------------------------------------------

        public sealed class Placement
        {
            public XYZ Origin { get; set; }      // feet, world
            public double Angle { get; set; }    // radians, CCW about Z
            public Level Level { get; set; }
        }

        public static Placement PlacementOf(ResolvedDesign r, Level level) => new Placement
        {
            Origin = new XYZ(r.X / MmPerFt, r.Y / MmPerFt, level.Elevation + r.Elevation / MmPerFt),
            Angle = r.Rotation * Math.PI / 180.0,
            Level = level
        };

        public static XYZ ToWorld(Placement p, double xMm, double yMm, double zMm)
        {
            double x = xMm / MmPerFt, y = yMm / MmPerFt;
            double c = Math.Cos(p.Angle), s = Math.Sin(p.Angle);
            return new XYZ(p.Origin.X + x * c - y * s, p.Origin.Y + x * s + y * c, p.Origin.Z + zMm / MmPerFt);
        }

        /// <summary>Creates one DirectShape (Casework) per part, stores the record on each and groups them.</summary>
        public static (Group group, List<(DirectShape shape, MillworkPart part, Material material)> parts) BuildModule(
            Document doc, string moduleId, ResolvedDesign r, List<MillworkPart> parts, Placement p,
            IDictionary<string, Material> materials, string groupName)
        {
            var created = new List<(DirectShape, MillworkPart, Material)>();
            var category = new ElementId(BuiltInCategory.OST_Casework);
            if (!DirectShape.IsValidCategoryId(category, doc))
                throw new ToolException("CATEGORY_NOT_SUPPORTED", "This document does not accept Casework DirectShapes.");
            foreach (var part in parts)
            {
                materials.TryGetValue(part.MaterialKey, out var material);
                var b = part.Box;
                var corners = new List<XYZ>
                {
                    ToWorld(p, b[0], b[1], b[2]), ToWorld(p, b[3], b[1], b[2]),
                    ToWorld(p, b[3], b[4], b[2]), ToWorld(p, b[0], b[4], b[2])
                };
                var loop = new CurveLoop();
                for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(corners[i], corners[(i + 1) % 4]));
                Solid solid;
                try
                {
                    var options = new SolidOptions(material?.Id ?? ElementId.InvalidElementId, ElementId.InvalidElementId);
                    solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, (b[5] - b[2]) / MmPerFt, options);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    throw new ToolException("INVALID_GEOMETRY", $"Revit could not build the {part.Label}: {ex.Message}");
                }
                var ds = DirectShape.CreateElement(doc, category);
                ds.ApplicationId = AppId;
                ds.ApplicationDataId = DataPrefix + moduleId + ":" + part.Role;
                var shape = new List<GeometryObject> { solid };
                if (!ds.IsValidShape(shape))
                    throw new ToolException("INVALID_GEOMETRY", $"The {part.Label} is not a valid shape for Revit.");
                ds.SetShape(shape);
                string label = string.IsNullOrWhiteSpace(r.Mark) ? part.Label : r.Mark + " - " + part.Label;
                try { ds.SetName(label); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { /* name is cosmetic */ }
                var comments = ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (comments != null && !comments.IsReadOnly) comments.Set(label);
                Write(ds, new PartRecord { ModuleId = moduleId, Part = part, MaterialId = material?.Id.GetValue(), Design = r });
                created.Add((ds, part, material));
            }

            doc.Regenerate();
            var ids = created.Select(c => c.Item1.Id).ToList();
            if (ids.Any(id => doc.GetElement(id) == null))
                throw new ToolException("CREATE_FAILED", "Revit removed part of the module right after creating it.");
            Group group;
            try
            {
                group = doc.Create.NewGroup(ids);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("GROUP_FAILED", "Revit could not group the module's parts: " + ex.Message);
            }
            var name = FreeGroupTypeName(doc, groupName);
            try { group.GroupType.Name = name; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { /* keep Revit's default name */ }
            return (group, created);
        }

        private static string FreeGroupTypeName(Document doc, string desired)
        {
            var used = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Select(e => e.Name),
                StringComparer.OrdinalIgnoreCase);
            var clean = new string((desired ?? "Marcenaria").Where(c => "{}[]|;<>?`~\\:".IndexOf(c) < 0).ToArray()).Trim();
            if (clean.Length == 0) clean = "Marcenaria";
            var name = clean;
            for (int n = 2; used.Contains(name); n++) name = $"{clean} ({n})";
            return name;
        }

        public static string GroupNameFor(ResolvedDesign r)
        {
            string kind = r.Kind == "base" ? "inferior" : r.Kind == "wall" ? "aéreo" : "torre";
            string dims = $"{MillworkLayout.F(r.Width)}x{MillworkLayout.F(r.Height)}x{MillworkLayout.F(r.Depth)}";
            return string.IsNullOrWhiteSpace(r.Mark) ? $"Marcenaria {kind} {dims}" : $"{r.Mark} - Marcenaria {kind} {dims}";
        }

        public static Dictionary<string, Material> ResolveMaterials(ToolContext ctx, ResolvedDesign r)
        {
            var map = new Dictionary<string, Material>();
            Material carcass = string.IsNullOrWhiteSpace(r.CarcassMaterial) ? null : ctx.Services.Lookup.Material(r.CarcassMaterial);
            Material front = string.IsNullOrWhiteSpace(r.FrontMaterial) ? carcass : ctx.Services.Lookup.Material(r.FrontMaterial);
            Material back = string.IsNullOrWhiteSpace(r.BackMaterial) ? carcass : ctx.Services.Lookup.Material(r.BackMaterial);
            if (carcass != null) map["carcass"] = carcass;
            if (front != null) map["front"] = front;
            if (back != null) map["back"] = back;
            return map;
        }

        // ---- lookup -------------------------------------------------------------------------------------------------

        /// <summary>All millwork parts of the document grouped into module instances (one per group, or per moduleId when ungrouped).</summary>
        public static List<ModuleInstance> AllModules(Document doc)
        {
            var result = new Dictionary<string, ModuleInstance>();
            var shapes = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>()
                .Where(ds => ds.ApplicationId == AppId && (ds.ApplicationDataId ?? "").StartsWith(DataPrefix, StringComparison.Ordinal));
            foreach (var ds in shapes)
            {
                var record = Read(ds);
                if (record?.Part == null || record.Design == null) continue;
                var group = ds.GroupId != ElementId.InvalidElementId ? doc.GetElement(ds.GroupId) as Group : null;
                // Nested groups: climb to the outermost group.
                while (group != null && group.GroupId != ElementId.InvalidElementId && doc.GetElement(group.GroupId) is Group outer)
                    group = outer;
                var key = group != null ? "group:" + group.Id.GetValue() : "module:" + record.ModuleId;
                if (!result.TryGetValue(key, out var m))
                {
                    m = new ModuleInstance { ModuleId = record.ModuleId, Group = group };
                    result[key] = m;
                }
                m.Parts.Add((ds, record));
            }
            return result.Values.ToList();
        }

        /// <summary>A module by group id, part id, mark or module id.</summary>
        public static ModuleInstance Find(ToolContext ctx, string reference, List<ModuleInstance> all = null)
        {
            all = all ?? AllModules(ctx.Doc);
            if (all.Count == 0)
                throw new ToolException("NOT_FOUND", "The document has no millwork modules created by create_millwork_module.", null, "create_millwork_module");
            var text = reference?.Trim() ?? "";
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                var hit = all.FirstOrDefault(m => m.Group?.Id.GetValue() == id || m.Parts.Any(p => p.Shape.Id.GetValue() == id));
                if (hit != null) return hit;
            }
            var matches = all.Where(m => string.Equals(m.Design.Mark, text, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(m.ModuleId, text, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(m.Group?.GroupType?.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1)
                throw new ToolException("AMBIGUOUS_NAME",
                    $"'{reference}' matches {matches.Count} modules (copies of the same group?): group ids {string.Join(", ", matches.Select(m => m.Group?.Id.GetValue().ToString() ?? m.ModuleId))}.",
                    "Pass the group id of the one you mean.");
            var known = all.Select(m => m.Design.Mark ?? m.Group?.GroupType?.Name ?? m.ModuleId).Distinct().Take(30);
            throw new ToolException("NOT_FOUND", $"No millwork module '{reference}'. Modules: {string.Join(", ", known)}.",
                "Pass a group id, a part id or the module mark.", "millwork_cutting_list");
        }

        // ---- placement recovery ------------------------------------------------------------------------------------

        /// <summary>
        /// Where the module is now (the user may have moved/rotated its group): fits origin and rotation from the
        /// current centroids of two far-apart parts against their local centroids. Returns null when it can't.
        /// </summary>
        public static Placement CurrentPlacement(ModuleInstance m, Level level, out double residualMm)
        {
            residualMm = 0;
            var pts = new List<(XYZ world, XYZ local)>();
            foreach (var (shape, record) in m.Parts)
            {
                var c = Centroid(shape);
                if (c == null) continue;
                var b = record.Part.Box;
                pts.Add((c, new XYZ((b[0] + b[3]) / 2 / MmPerFt, (b[1] + b[4]) / 2 / MmPerFt, (b[2] + b[5]) / 2 / MmPerFt)));
            }
            if (pts.Count < 2) return null;
            double best = -1; int bi = 0, bj = 1;
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    var d = new XYZ(pts[i].local.X - pts[j].local.X, pts[i].local.Y - pts[j].local.Y, 0).GetLength();
                    if (d > best) { best = d; bi = i; bj = j; }
                }
            if (best < 1e-3) return null;
            var dl = pts[bj].local - pts[bi].local;
            var dw = pts[bj].world - pts[bi].world;
            double angle = Math.Atan2(dw.Y, dw.X) - Math.Atan2(dl.Y, dl.X);
            double cs = Math.Cos(angle), sn = Math.Sin(angle);
            var li = pts[bi].local;
            var origin = new XYZ(pts[bi].world.X - (li.X * cs - li.Y * sn), pts[bi].world.Y - (li.X * sn + li.Y * cs), pts[bi].world.Z - li.Z);
            var placement = new Placement { Origin = origin, Angle = angle, Level = level };
            foreach (var (world, local) in pts)
            {
                var expected = ToWorld(placement, local.X * MmPerFt, local.Y * MmPerFt, local.Z * MmPerFt);
                residualMm = Math.Max(residualMm, expected.DistanceTo(world) * MmPerFt);
            }
            return placement;
        }

        private static XYZ Centroid(Element e)
        {
            var geo = e.get_Geometry(new Options());
            if (geo == null) return null;
            foreach (var obj in geo)
            {
                if (obj is Solid s && s.Volume > 1e-9) return s.ComputeCentroid();
                if (obj is GeometryInstance gi)
                    foreach (var inner in gi.GetInstanceGeometry())
                        if (inner is Solid si && si.Volume > 1e-9) return si.ComputeCentroid();
            }
            return null;
        }

        public static double NormalizeDeg(double rad)
        {
            double deg = rad * 180.0 / Math.PI;
            deg %= 360.0;
            if (deg < 0) deg += 360.0;
            if (deg > 359.9995) deg = 0;
            return Math.Round(deg, 3);
        }

        // ---- reporting ----------------------------------------------------------------------------------------------

        public static object PartRow(DirectShape ds, MillworkPart part, string materialName)
        {
            var d = part.PanelDims();
            return new
            {
                id = ds?.Id.GetValue(),
                role = part.Role,
                label = part.Label,
                piece = part.Piece,
                lengthMm = d[0],
                widthMm = d[1],
                thicknessMm = d[2],
                material = materialName,
                note = part.Note
            };
        }

        /// <summary>Aggregates identical pieces: piece name + dimensions + material.</summary>
        public static List<CutRow> CuttingList(IEnumerable<(MillworkPart part, string material, string module)> parts)
        {
            return parts.GroupBy(p =>
                {
                    var d = p.part.PanelDims();
                    return (p.module, p.part.Piece, d[0], d[1], d[2], p.material ?? "");
                })
                .Select(g => new CutRow
                {
                    Module = g.Key.module,
                    Piece = g.Key.Piece,
                    Qty = g.Count(),
                    LengthMm = g.Key.Item3,
                    WidthMm = g.Key.Item4,
                    ThicknessMm = g.Key.Item5,
                    Material = g.Key.Item6.Length == 0 ? null : g.Key.Item6,
                    AreaM2 = Math.Round(g.Count() * g.Key.Item3 * g.Key.Item4 / 1e6, 3)
                })
                .OrderBy(r => r.Module).ThenBy(r => r.Material).ThenByDescending(r => r.ThicknessMm).ThenBy(r => r.Piece)
                .ThenByDescending(r => r.LengthMm).ToList();
        }

        public static string MaterialName(Document doc, long? id) =>
            id.HasValue ? (doc.GetElement(id.Value.ToRevitElementId()) as Material)?.Name : null;
    }

    public sealed class CutRow
    {
        [JsonProperty("module")] public string Module { get; set; }
        [JsonProperty("piece")] public string Piece { get; set; }
        [JsonProperty("qty")] public int Qty { get; set; }
        [JsonProperty("lengthMm")] public double LengthMm { get; set; }
        [JsonProperty("widthMm")] public double WidthMm { get; set; }
        [JsonProperty("thicknessMm")] public double ThicknessMm { get; set; }
        [JsonProperty("material")] public string Material { get; set; }
        [JsonProperty("areaM2")] public double AreaM2 { get; set; }
    }
}
