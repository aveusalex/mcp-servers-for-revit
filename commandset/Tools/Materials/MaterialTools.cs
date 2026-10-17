using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Materials.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Materials
{
    // ---- MAT-07 list_materials ----------------------------------------------------------------------------------

    public sealed class ListMaterialsArgs
    {
        [JsonProperty("nameContains")]
        [Description("Only materials whose name contains this text (case and accents ignored).")]
        public string NameContains { get; set; }

        [JsonProperty("materialClass")]
        [Description("Only this material class (e.g. Concrete / Concreto, Paint / Pintura), case and accents ignored.")]
        public string MaterialClass { get; set; }

        [JsonProperty("includeUseCount")]
        [Description("Count the model elements that use each material (slower on big models). Default false.")]
        [DefaultValue(false)]
        public bool IncludeUseCount { get; set; }

        [JsonProperty("limit")]
        [Description("Page size (default 200).")]
        [Range(1, 5000)]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Materials to skip (default 0).")]
        [Range(0, int.MaxValue)]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    [McpTool("list_materials", Kind = ToolKind.Read, Toolset = "materials", Idempotent = true,
        Description = "List the project's materials with class, shading color (#RRGGBB), transparency, surface and cut " +
                      "patterns with their colors, render appearance asset and, optionally, how many elements use each. " +
                      "Filter by name or class; sorted by name; paged with an exact total. Read-only.")]
    public sealed class ListMaterialsTool : McpTool<ListMaterialsArgs>
    {
        protected override object Run(ToolContext ctx, ListMaterialsArgs a)
        {
            var doc = ctx.Doc;
            IEnumerable<Material> materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>();
            if (!string.IsNullOrWhiteSpace(a.NameContains))
            {
                var key = TextMatch.Fold(a.NameContains);
                materials = materials.Where(m => TextMatch.Fold(m.Name).Contains(key));
            }
            if (!string.IsNullOrWhiteSpace(a.MaterialClass))
            {
                var key = TextMatch.Fold(a.MaterialClass);
                materials = materials.Where(m => TextMatch.Fold(m.MaterialClass ?? string.Empty) == key);
            }
            var list = materials.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            Dictionary<long, int> uses = null;
            if (a.IncludeUseCount)
            {
                uses = new Dictionary<long, int>();
                foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements())
                {
                    if (e.Category == null || e.Category.CategoryType != CategoryType.Model) continue;
                    ICollection<ElementId> ids;
                    try { ids = e.GetMaterialIds(false); }
                    catch { continue; }
                    foreach (var id in ids)
                    {
                        var k = id.GetValue();
                        uses[k] = uses.TryGetValue(k, out var n) ? n + 1 : 1;
                    }
                }
            }

            var page = Paging.Page(list, a.Limit, a.Offset);
            return new
            {
                items = page.Items.Select(m => MaterialGraphics.Describe(doc, m,
                    uses == null ? (int?)null : uses.TryGetValue(m.Id.GetValue(), out var c) ? c : 0)),
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }
    }

    // ---- shared graphics arguments ------------------------------------------------------------------------------

    /// <summary>Graphics fields shared by create_material and set_material_graphics.</summary>
    public abstract class MaterialGraphicsArgs
    {
        [JsonProperty("materialClass")]
        [Description("Material class (free text, e.g. \"Concreto\", \"Pintura\").")]
        public string MaterialClass { get; set; }

        [JsonProperty("color")]
        [Description("Shading color \"#RRGGBB\" (or \"R,G,B\").")]
        public string Color { get; set; }

        [JsonProperty("transparency")]
        [Description("Shading transparency, 0 (opaque) to 100.")]
        [Range(0, 100)]
        public int? Transparency { get; set; }

        [JsonProperty("shininess")]
        [Description("Shininess, 0 to 128.")]
        [Range(0, 128)]
        public int? Shininess { get; set; }

        [JsonProperty("smoothness")]
        [Description("Smoothness, 0 to 100.")]
        [Range(0, 100)]
        public int? Smoothness { get; set; }

        [JsonProperty("useRenderAppearance")]
        [Description("Use the render appearance for shading.")]
        public bool? UseRenderAppearance { get; set; }

        [JsonProperty("surfaceForegroundPattern")]
        [Description("Surface foreground fill pattern: name or id, \"solid\", \"none\"; prefix \"model:\" / \"drafting:\" if the name exists in both.")]
        public string SurfaceForegroundPattern { get; set; }

        [JsonProperty("surfaceForegroundColor")]
        [Description("Surface foreground pattern color \"#RRGGBB\".")]
        public string SurfaceForegroundColor { get; set; }

        [JsonProperty("surfaceBackgroundPattern")]
        [Description("Surface background fill pattern (name, id, \"solid\" or \"none\").")]
        public string SurfaceBackgroundPattern { get; set; }

        [JsonProperty("surfaceBackgroundColor")]
        [Description("Surface background pattern color \"#RRGGBB\".")]
        public string SurfaceBackgroundColor { get; set; }

        [JsonProperty("cutForegroundPattern")]
        [Description("Cut foreground fill pattern (name, id, \"solid\" or \"none\").")]
        public string CutForegroundPattern { get; set; }

        [JsonProperty("cutForegroundColor")]
        [Description("Cut foreground pattern color \"#RRGGBB\".")]
        public string CutForegroundColor { get; set; }

        [JsonProperty("cutBackgroundPattern")]
        [Description("Cut background fill pattern (name, id, \"solid\" or \"none\").")]
        public string CutBackgroundPattern { get; set; }

        [JsonProperty("cutBackgroundColor")]
        [Description("Cut background pattern color \"#RRGGBB\".")]
        public string CutBackgroundColor { get; set; }

        [JsonProperty("appearanceColor")]
        [Description("Render appearance color \"#RRGGBB\" (generic diffuse). Edits the appearance asset; a shared asset is duplicated first.")]
        public string AppearanceColor { get; set; }

        internal bool Any() =>
            MaterialClass != null || Color != null || Transparency.HasValue || Shininess.HasValue || Smoothness.HasValue ||
            UseRenderAppearance.HasValue || SurfaceForegroundPattern != null || SurfaceForegroundColor != null ||
            SurfaceBackgroundPattern != null || SurfaceBackgroundColor != null || CutForegroundPattern != null ||
            CutForegroundColor != null || CutBackgroundPattern != null || CutBackgroundColor != null || AppearanceColor != null;

        /// <summary>Parses colors and patterns up front, then writes and reads back. Inside the transaction.</summary>
        internal List<string> ApplyTo(ToolContext ctx, Material m)
        {
            var doc = ctx.Doc;
            var applied = new List<string>();
            Color Col(string text, string label) => text == null ? null : MaterialGraphics.ParseColor(text, label);
            ElementId Pat(string text, string label) => text == null ? null : MaterialGraphics.FillPattern(doc, text, label);

            var color = Col(Color, "color");
            var sfc = Col(SurfaceForegroundColor, "surfaceForegroundColor");
            var sbc = Col(SurfaceBackgroundColor, "surfaceBackgroundColor");
            var cfc = Col(CutForegroundColor, "cutForegroundColor");
            var cbc = Col(CutBackgroundColor, "cutBackgroundColor");
            var appearance = Col(AppearanceColor, "appearanceColor");
            var sfp = Pat(SurfaceForegroundPattern, "surface foreground pattern");
            var sbp = Pat(SurfaceBackgroundPattern, "surface background pattern");
            var cfp = Pat(CutForegroundPattern, "cut foreground pattern");
            var cbp = Pat(CutBackgroundPattern, "cut background pattern");

            void Check(bool ok, string field)
            {
                if (!ok) throw new ToolException("SET_FAILED", $"Revit did not keep {field} on material '{m.Name}'.");
                applied.Add(field);
            }

            if (MaterialClass != null) { m.MaterialClass = MaterialClass; Check(m.MaterialClass == MaterialClass, "materialClass"); }
            if (color != null) { m.Color = color; Check(MaterialGraphics.SameColor(m.Color, color), "color"); }
            if (Transparency.HasValue) { m.Transparency = Transparency.Value; Check(m.Transparency == Transparency.Value, "transparency"); }
            if (Shininess.HasValue) { m.Shininess = Shininess.Value; Check(m.Shininess == Shininess.Value, "shininess"); }
            if (Smoothness.HasValue) { m.Smoothness = Smoothness.Value; Check(m.Smoothness == Smoothness.Value, "smoothness"); }
            if (UseRenderAppearance.HasValue) { m.UseRenderAppearanceForShading = UseRenderAppearance.Value; Check(m.UseRenderAppearanceForShading == UseRenderAppearance.Value, "useRenderAppearance"); }
            if (sfp != null) { m.SurfaceForegroundPatternId = sfp; Check(m.SurfaceForegroundPatternId == sfp, "surfaceForegroundPattern"); }
            if (sfc != null) { m.SurfaceForegroundPatternColor = sfc; Check(MaterialGraphics.SameColor(m.SurfaceForegroundPatternColor, sfc), "surfaceForegroundColor"); }
            if (sbp != null) { m.SurfaceBackgroundPatternId = sbp; Check(m.SurfaceBackgroundPatternId == sbp, "surfaceBackgroundPattern"); }
            if (sbc != null) { m.SurfaceBackgroundPatternColor = sbc; Check(MaterialGraphics.SameColor(m.SurfaceBackgroundPatternColor, sbc), "surfaceBackgroundColor"); }
            if (cfp != null) { m.CutForegroundPatternId = cfp; Check(m.CutForegroundPatternId == cfp, "cutForegroundPattern"); }
            if (cfc != null) { m.CutForegroundPatternColor = cfc; Check(MaterialGraphics.SameColor(m.CutForegroundPatternColor, cfc), "cutForegroundColor"); }
            if (cbp != null) { m.CutBackgroundPatternId = cbp; Check(m.CutBackgroundPatternId == cbp, "cutBackgroundPattern"); }
            if (cbc != null) { m.CutBackgroundPatternColor = cbc; Check(MaterialGraphics.SameColor(m.CutBackgroundPatternColor, cbc), "cutBackgroundColor"); }

            if (appearance != null)
            {
                if (m.AppearanceAssetId == ElementId.InvalidElementId)
                    ctx.Warnings.Add(new ToolWarning($"'{m.Name}' has no render appearance asset; appearanceColor was not applied."));
                else
                {
                    var sharing = MaterialGraphics.SharingAsset(doc, m.AppearanceAssetId, m.Id);
                    if (sharing.Count > 0)
                    {
                        MaterialGraphics.DuplicateAsset(doc, m);
                        ctx.Warnings.Add(new ToolWarning($"The appearance asset was shared with {sharing.Count} other material(s); '{m.Name}' got its own copy first."));
                    }
                    var status = MaterialGraphics.SetAppearanceColor(doc, m, appearance);
                    if (status == "unsupported")
                        ctx.Warnings.Add(new ToolWarning($"The appearance of '{m.Name}' has no editable generic color (not a Generic appearance); appearanceColor was not applied."));
                    else
                    {
                        applied.Add("appearanceColor");
                        if (status == "texture")
                            ctx.Warnings.Add(new ToolWarning($"The appearance color of '{m.Name}' is driven by an image; the color was set but may not show."));
                    }
                }
            }
            return applied;
        }
    }

    // ---- MAT-09 create_material ---------------------------------------------------------------------------------

    public sealed class CreateMaterialArgs : MaterialGraphicsArgs
    {
        [JsonProperty("name", Required = Required.Always)]
        [Required]
        [Description("Name of the new material (unique in the project).")]
        public string Name { get; set; }

        [JsonProperty("duplicateFrom")]
        [Description("Existing material (name or id) to copy; its appearance asset is duplicated too so later edits don't leak.")]
        public string DuplicateFrom { get; set; }

        [JsonProperty("ifExists")]
        [Description("When a material with this name exists: error (default) or reuse it (the graphics given are then applied to it).")]
        [EnumValues("error", "reuse")]
        [DefaultValue("error")]
        public string IfExists { get; set; } = "error";
    }

    [McpTool("create_material", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "materials",
        Description = "Create a material (new or duplicated from another, with its own copy of the appearance asset) and " +
                      "set class, shading color (#RRGGBB), transparency, shininess, smoothness, surface/cut patterns and " +
                      "colors, and optionally the render appearance color. ifExists=reuse returns the existing material of " +
                      "that name. Values are read back. Undo: Ctrl+Z.")]
    public sealed class CreateMaterialTool : McpTool<CreateMaterialArgs>
    {
        protected override object Run(ToolContext ctx, CreateMaterialArgs a)
        {
            var doc = ctx.Doc;
            var name = a.Name.Trim();
            var existing = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            Material material;
            string action;
            if (existing != null)
            {
                if (a.IfExists != "reuse")
                    throw new ToolException("NAME_COLLISION", $"Material '{existing.Name}' already exists (id {existing.Id.GetValue()}).",
                        "Use another name, ifExists:\"reuse\", or set_material_graphics to edit it.", "set_material_graphics");
                material = existing;
                action = "reused";
            }
            else
            {
                ctx.Services.Names.EnsureUniqueInClass(typeof(Material), name, "material name");
                if (!string.IsNullOrWhiteSpace(a.DuplicateFrom))
                {
                    var source = ctx.Services.Lookup.Material(a.DuplicateFrom);
                    material = source.Duplicate(name);
                    if (material.AppearanceAssetId != ElementId.InvalidElementId) MaterialGraphics.DuplicateAsset(doc, material);
                    action = "duplicated";
                }
                else
                {
                    material = doc.GetElement(Material.Create(doc, name)) as Material;
                    action = "created";
                }
                if (material == null || material.Name != name)
                    throw new ToolException("CREATE_FAILED", $"Revit did not create the material '{name}'.");
            }

            var applied = a.ApplyTo(ctx, material);
            return new
            {
                action,
                material = MaterialGraphics.Describe(doc, material),
                applied,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + $"Material '{material.Name}' {action}."
            };
        }
    }

    // ---- MAT-10 set_material_graphics ---------------------------------------------------------------------------

    public sealed class SetMaterialGraphicsArgs : MaterialGraphicsArgs
    {
        [JsonProperty("material", Required = Required.Always)]
        [Required]
        [Description("Material to edit: name or id.")]
        public string Material { get; set; }
    }

    [McpTool("set_material_graphics", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "materials",
        Description = "Edit a material's graphics: class, shading color (#RRGGBB), transparency, shininess, smoothness, " +
                      "use-render-appearance, surface and cut foreground/background patterns and colors, and the render " +
                      "appearance color (generic diffuse; a shared appearance asset is duplicated first so other materials " +
                      "don't change). Affects every element that uses the material. Values are read back and the before/" +
                      "after state returned. Preview with dryRun:true, then run (needs approval). Undo: Ctrl+Z.")]
    public sealed class SetMaterialGraphicsTool : McpTool<SetMaterialGraphicsArgs>
    {
        protected override void Validate(SetMaterialGraphicsArgs a)
        {
            if (!a.Any()) throw new ToolException("INVALID_ARGUMENTS", "Nothing to change.");
        }

        protected override object Run(ToolContext ctx, SetMaterialGraphicsArgs a)
        {
            var material = ctx.Services.Lookup.Material(a.Material);
            var before = MaterialGraphics.Describe(ctx.Doc, material);
            var applied = a.ApplyTo(ctx, material);
            return new
            {
                materialId = material.Id.GetValue(),
                applied,
                before,
                after = MaterialGraphics.Describe(ctx.Doc, material),
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + $"'{material.Name}': {string.Join(", ", applied)}."
            };
        }
    }
}
