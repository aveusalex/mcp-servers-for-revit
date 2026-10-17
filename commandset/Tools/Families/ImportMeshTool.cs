using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Tools.Families.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Families
{
    public sealed class ImportMeshArgs
    {
        [JsonProperty("path", Required = Required.Always)]
        [Required]
        [Description("Mesh file (.obj or .stl, ASCII or binary): absolute, or relative to Documents\\RevitMCP\\models; must be in the allowed folders.")]
        public string Path { get; set; }

        [JsonProperty("name")]
        [Description("Type name of the imported object (it becomes a reusable DirectShape type). Default: the file name.")]
        public string Name { get; set; }

        [JsonProperty("category")]
        [DefaultValue("Generic Models")]
        [Description("Category (pt-BR or English), e.g. \"Mobiliário\" / \"Furniture\", \"Equipamento especializado\". Default Generic Models.")]
        public string Category { get; set; } = "Generic Models";

        [JsonProperty("units")]
        [EnumValues("mm", "cm", "m", "in", "ft")]
        [DefaultValue("mm")]
        [Description("Units the file was modelled in (OBJ/STL carry none). Default mm. SketchUp exports are often in, Blender in m.")]
        public string Units { get; set; } = "mm";

        [JsonProperty("upAxis")]
        [EnumValues("z", "y")]
        [DefaultValue("z")]
        [Description("Which file axis points up. y for most Blender/glTF-style OBJ exports, z for CAD/SketchUp. Default z.")]
        public string UpAxis { get; set; } = "z";

        [JsonProperty("anchor")]
        [EnumValues("bottomCenter", "origin")]
        [DefaultValue("bottomCenter")]
        [Description("Which point of the mesh goes to position: bottomCenter (default: centre of the bounding box at its lowest point, good for furniture) or origin (the file's 0,0,0).")]
        public string Anchor { get; set; } = "bottomCenter";

        [JsonProperty("position")]
        [Description("Where the anchor goes (mm). z is measured from the level when level is given. Default 0,0,0.")]
        public Point3Mm Position { get; set; }

        [JsonProperty("level")]
        [Description("Level (name or id) the position's z is measured from. Default: z is absolute.")]
        public string Level { get; set; }

        [JsonProperty("rotationDeg")]
        [Unit("deg")]
        [Description("Rotation about the vertical axis through the anchor, in degrees (counter-clockwise). Default 0.")]
        public double RotationDeg { get; set; }

        [JsonProperty("material")]
        [Description("Material (name or id) for every face. OBJ faces whose usemtl matches a project material by name keep that material instead. Default: none (category material).")]
        public string Material { get; set; }

        [JsonProperty("maxTriangles")]
        [Range(1, 500000)]
        [DefaultValue(100000)]
        [Description("Refuse meshes with more triangles than this (heavy meshes slow the whole model). Default 100000.")]
        public int MaxTriangles { get; set; } = 100000;

        [JsonProperty("reuseType")]
        [DefaultValue(true)]
        [Description("If a type with this name was already imported in this category, place another instance of it without reading the file again. Default true.")]
        public bool ReuseType { get; set; } = true;
    }

    /// <summary>EXP-15: bring an OBJ/STL mesh in as a reusable DirectShape type and place an instance.</summary>
    [McpTool("import_mesh", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "families", TimeoutSeconds = 300,
        Description = "Import a 3D mesh (.obj or .stl, e.g. furniture or a decorative object from SketchUp/Blender/3D " +
                      "Warehouse exports) as a reusable DirectShape type in a category (default Generic Models) and place " +
                      "an instance: units, up axis, anchor (bottomCenter or file origin), position in mm, level, rotation " +
                      "and material (OBJ usemtl names are matched to project materials). Degenerate triangles are skipped " +
                      "and reported. Revit builds a solid when the mesh is closed, else a mesh (visible, but no cut/volume). " +
                      "Importing again with the same name places another instance of the same type. It is not an editable " +
                      "family: for parametric objects use create_family_from_template. Undo: Ctrl+Z.")]
    public sealed class ImportMeshTool : McpTool<ImportMeshArgs>
    {
        private static readonly string[] Extensions = { ".obj", ".stl" };

        protected override object Run(ToolContext ctx, ImportMeshArgs a)
        {
            var doc = ctx.Doc;
            var full = PathPolicy.Resolve(a.Path, "models");
            if (!Extensions.Contains(System.IO.Path.GetExtension(full).ToLowerInvariant()))
                throw new ToolException("INVALID_PATH", $"'{full}' is not an .obj or .stl file.");
            if (!File.Exists(full)) throw new ToolException("FILE_NOT_FOUND", $"'{full}' does not exist.");

            var category = ctx.Services.Categories.Resolve(a.Category);
            if (!DirectShape.IsValidCategoryId(category.Id, doc))
                throw new ToolException("CATEGORY_NOT_SUPPORTED", $"'{category.Name}' can't hold imported shapes.",
                    "Use a model category such as Generic Models, Furniture, Casework or Specialty Equipment.");

            var name = string.IsNullOrWhiteSpace(a.Name) ? System.IO.Path.GetFileNameWithoutExtension(full) : a.Name.Trim();
            var defaultMaterial = string.IsNullOrWhiteSpace(a.Material) ? null : ctx.Services.Lookup.Material(a.Material);

            var type = a.ReuseType
                ? new FilteredElementCollector(doc).OfClass(typeof(DirectShapeType)).Cast<DirectShapeType>()
                    .FirstOrDefault(t => t.Name == name && t.Category?.Id == category.Id)
                : null;
            bool reused = type != null;
            string definitionId = "RevitMCP:mesh:" + name;

            object meshInfo = null;
            double[] sizeMm = null;
            if (!reused)
            {
                var mesh = MeshFile.Read(full, a.MaxTriangles);
                if (mesh.Triangles.Count == 0) throw new ToolException("MESH_EMPTY", $"'{full}' has no faces.");
                var built = Build(doc, mesh, a, defaultMaterial);
                type = DirectShapeType.Create(doc, UniqueTypeName(doc, name), category.Id);
                type.SetShape(built.Geometry);
                sizeMm = built.SizeMm;
                meshInfo = new
                {
                    format = mesh.Format,
                    vertices = mesh.Vertices.Count,
                    triangles = mesh.Triangles.Count,
                    skippedTriangles = built.Skipped,
                    result = built.Outcome,
                    materialsMatched = built.Matched,
                    materialsNotFound = built.Unmatched
                };
                if (built.Skipped > 0)
                    ctx.Warnings.Add(new ToolWarning($"{built.Skipped} degenerate triangle(s) (edges shorter than Revit's tolerance) were skipped."));
                var largest = sizeMm.Max();
                if (largest > 50000 || largest < 20)
                    ctx.Warnings.Add(new ToolWarning($"The object is {largest:0.#} mm across: check units (the file was read as {a.Units})."));
                if (built.Outcome != "solid")
                    ctx.Warnings.Add(new ToolWarning("The mesh is not closed, so Revit kept it as a mesh: it shows in views but has no volume and is not cut in sections."));
            }
            definitionId = "RevitMCP:mesh:" + type.Id.GetValue();
            var library = DirectShapeLibrary.GetDirectShapeLibrary(doc);
            if (!library.Contains(definitionId)) library.AddDefinitionType(definitionId, type.Id);

            double z0 = 0;
            Level level = null;
            if (!string.IsNullOrWhiteSpace(a.Level))
            {
                level = ctx.Services.Lookup.Level(a.Level);
                z0 = level.Elevation;
            }
            var origin = new XYZ((a.Position?.X ?? 0) / 304.8, (a.Position?.Y ?? 0) / 304.8, z0 + (a.Position?.Z ?? 0) / 304.8);
            var transform = Transform.CreateTranslation(origin)
                .Multiply(Transform.CreateRotation(XYZ.BasisZ, a.RotationDeg * Math.PI / 180.0));
            var instance = DirectShape.CreateElementInstance(doc, type.Id, category.Id, definitionId, transform);
            instance.SetTypeId(type.Id);
            instance.ApplicationId = "RevitMCP";
            instance.ApplicationDataId = "mesh:" + System.IO.Path.GetFileName(full);
            doc.Regenerate();

            return new
            {
                instanceId = instance.Id.GetValue(),
                typeId = type.Id.GetValue(),
                typeName = type.Name,
                typeReused = reused,
                category = category.Name,
                path = full,
                level = level?.Name,
                position = new { x = a.Position?.X ?? 0, y = a.Position?.Y ?? 0, z = a.Position?.Z ?? 0 },
                rotationDeg = a.RotationDeg,
                sizeMm = sizeMm == null ? null : new { x = sizeMm[0], y = sizeMm[1], z = sizeMm[2] },
                mesh = meshInfo,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                (reused ? $"Placed another '{type.Name}'." : $"Imported '{System.IO.Path.GetFileName(full)}' as '{type.Name}' and placed it.")
            };
        }

        private sealed class Built
        {
            public IList<GeometryObject> Geometry;
            public int Skipped;
            public string Outcome;
            public double[] SizeMm;
            public List<string> Matched = new List<string>();
            public List<string> Unmatched = new List<string>();
        }

        private static Built Build(Document doc, MeshData mesh, ImportMeshArgs a, Material defaultMaterial)
        {
            double scale = UnitScaleToFeet(a.Units);
            bool yUp = a.UpAxis == "y";
            var pts = mesh.Vertices.Select(v => yUp
                ? new XYZ(v[0] * scale, -v[2] * scale, v[1] * scale)
                : new XYZ(v[0] * scale, v[1] * scale, v[2] * scale)).ToList();

            double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            double minZ = pts.Min(p => p.Z), maxZ = pts.Max(p => p.Z);
            var shift = a.Anchor == "origin" ? XYZ.Zero : new XYZ(-(minX + maxX) / 2, -(minY + maxY) / 2, -minZ);
            pts = pts.Select(p => p + shift).ToList();

            // OBJ usemtl → project material of the same name.
            var built = new Built();
            var byName = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            var fallback = defaultMaterial?.Id ?? ElementId.InvalidElementId;
            var cache = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
            ElementId MaterialFor(string mtl)
            {
                if (string.IsNullOrEmpty(mtl)) return fallback;
                if (cache.TryGetValue(mtl, out var id)) return id;
                if (byName.TryGetValue(mtl, out id)) built.Matched.Add(mtl);
                else { id = fallback; built.Unmatched.Add(mtl); }
                return cache[mtl] = id;
            }

            double tol = doc.Application.ShortCurveTolerance;
            var builder = new TessellatedShapeBuilder();
            builder.OpenConnectedFaceSet(false);
            int added = 0;
            for (int t = 0; t < mesh.Triangles.Count; t++)
            {
                var tri = mesh.Triangles[t];
                XYZ p0 = pts[tri[0]], p1 = pts[tri[1]], p2 = pts[tri[2]];
                if (p0.DistanceTo(p1) < tol || p1.DistanceTo(p2) < tol || p2.DistanceTo(p0) < tol ||
                    (p1 - p0).CrossProduct(p2 - p0).GetLength() < tol * tol)
                {
                    built.Skipped++;
                    continue;
                }
                builder.AddFace(new TessellatedFace(new List<XYZ> { p0, p1, p2 }, MaterialFor(mesh.TriangleMaterials[t])));
                added++;
            }
            builder.CloseConnectedFaceSet();
            if (added == 0)
                throw new ToolException("MESH_DEGENERATE", "Every triangle is smaller than Revit's tolerance (≈0.8 mm).",
                    "Check units: a model in m read as mm is 1000× too small.");

            builder.Target = TessellatedShapeBuilderTarget.AnyGeometry;
            builder.Fallback = TessellatedShapeBuilderFallback.Mesh;
            builder.Build();
            var result = builder.GetBuildResult();
            if (result.Outcome == TessellatedShapeBuilderOutcome.Nothing)
                throw new ToolException("MESH_BUILD_FAILED", "Revit could not build any geometry from the mesh.");
            built.Geometry = result.GetGeometricalObjects();
            built.Outcome = built.Geometry.Any(g => g is Solid s && s.Volume > 0) ? "solid" : "mesh";
            built.SizeMm = new[] { Mm(maxX - minX), Mm(maxY - minY), Mm(maxZ - minZ) };
            return built;
        }

        private static double Mm(double ft) => Math.Round(ft * 304.8, 1);

        private static double UnitScaleToFeet(string units)
        {
            switch (units)
            {
                case "cm": return 10 / 304.8;
                case "m": return 1000 / 304.8;
                case "in": return 1.0 / 12;
                case "ft": return 1;
                default: return 1 / 304.8;
            }
        }

        private static string UniqueTypeName(Document doc, string name)
        {
            var taken = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(DirectShapeType)).Select(e => e.Name));
            if (!taken.Contains(name)) return name;
            for (int i = 2; ; i++)
                if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
        }
    }
}
