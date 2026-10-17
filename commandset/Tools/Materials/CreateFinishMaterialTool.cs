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
using RevitMCPCommandSet.Tools.Materials.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Materials
{
    public sealed class CreateFinishMaterialArgs
    {
        [JsonProperty("name", Required = Required.Always)]
        [Required]
        [Description("Material name (the office's naming rule, e.g. \"MDF Freijó Puro\", \"Porcelanato Cinza 60x60\", \"Pintura Branco Neve\").")]
        public string Name { get; set; }

        [JsonProperty("kind", Required = Required.Always)]
        [Required]
        [EnumValues("mdf", "tile", "paint")]
        [Description("mdf: wood/MDF board from a texture image. tile: revestimento (ceramic/porcelain) with a model surface pattern of the tile grid. paint: solid color (tinta).")]
        public string Kind { get; set; }

        [JsonProperty("image")]
        [Description("Texture image (PNG/JPG/BMP/TIF): absolute, or relative to Documents\\RevitMCP\\textures; must be in the allowed folders. Needed for mdf; optional for tile; ignored for paint.")]
        public string Image { get; set; }

        [JsonProperty("textureWidthMm")]
        [Unit("mm")]
        [Range(1, 100000)]
        [Description("Real-world width the image covers. Default: tile = tile width + joint; mdf = 1000.")]
        public double? TextureWidthMm { get; set; }

        [JsonProperty("textureHeightMm")]
        [Unit("mm")]
        [Range(1, 100000)]
        [Description("Real-world height the image covers. Default: tile = tile height + joint; otherwise keeps the image's aspect ratio.")]
        public double? TextureHeightMm { get; set; }

        [JsonProperty("textureRotationDeg")]
        [Unit("deg")]
        [Range(-360, 360)]
        [DefaultValue(0)]
        [Description("Rotation of the texture (e.g. 90 to turn the wood grain). Default 0.")]
        public double TextureRotationDeg { get; set; }

        [JsonProperty("tileWidthMm")]
        [Unit("mm")]
        [Range(1, 10000)]
        [Description("tile: tile width (e.g. 600).")]
        public double? TileWidthMm { get; set; }

        [JsonProperty("tileHeightMm")]
        [Unit("mm")]
        [Range(1, 10000)]
        [Description("tile: tile height. Default = tileWidthMm (square tile).")]
        public double? TileHeightMm { get; set; }

        [JsonProperty("jointMm")]
        [Unit("mm")]
        [Range(0, 100)]
        [DefaultValue(2)]
        [Description("tile: grout joint width (rejunte). Default 2. The surface pattern repeats every tile + joint.")]
        public double JointMm { get; set; } = 2;

        [JsonProperty("color")]
        [Description("Shading color \"#RRGGBB\" (color in shaded views; for paint also the render color). Default: the image's average color; required for paint.")]
        public string Color { get; set; }

        [JsonProperty("materialClass")]
        [Description("Material class. Default by kind: mdf \"Madeira\", tile \"Cerâmica\", paint \"Pintura\".")]
        public string MaterialClass { get; set; }

        [JsonProperty("surfacePattern")]
        [Description("Surface foreground pattern (name/id, \"none\"). Default: tile = generated tile-grid model pattern; mdf/paint = none.")]
        public string SurfacePattern { get; set; }

        [JsonProperty("surfacePatternColor")]
        [Description("Surface pattern color \"#RRGGBB\". Default: grey #808080 for the tile grid.")]
        public string SurfacePatternColor { get; set; }

        [JsonProperty("cutPattern")]
        [Description("Cut foreground pattern (name/id, \"solid\", \"none\"). Default: unchanged.")]
        public string CutPattern { get; set; }

        [JsonProperty("ifExists")]
        [EnumValues("error", "update")]
        [DefaultValue("error")]
        [Description("When a material with this name exists: error (default) or update it in place (affects every element using it).")]
        public string IfExists { get; set; } = "error";
    }

    /// <summary>MAT-15: finish material (MDF by image, tile with joint grid, paint) in one call.</summary>
    [McpTool("create_finish_material", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "materials", TimeoutSeconds = 120,
        Description = "Create (or update with ifExists:update) a finish material in one step: kind mdf (texture image as " +
                      "the render appearance diffuse with its real-world size in mm and rotation), tile (revestimento: " +
                      "optional image plus a model surface pattern of the tile grid, spacing = tile + grout joint) or " +
                      "paint (solid color). Sets the shading color (default: the image's average color), class and " +
                      "patterns. The image must be in the allowed folders (Documents\\RevitMCP\\textures by default); its " +
                      "absolute path is stored, so other machines need the same path. Values are read back. Undo: Ctrl+Z.")]
    public sealed class CreateFinishMaterialTool : McpTool<CreateFinishMaterialArgs>
    {
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

        protected override void Validate(CreateFinishMaterialArgs a)
        {
            a.Kind = a.Kind.Trim().ToLowerInvariant();
            if (a.Kind == "paint" && string.IsNullOrWhiteSpace(a.Color))
                throw new ToolException("INVALID_ARGUMENTS", "paint needs color (\"#RRGGBB\").");
            if (a.Kind == "mdf" && string.IsNullOrWhiteSpace(a.Image))
                throw new ToolException("INVALID_ARGUMENTS", "mdf needs image (the board's texture).",
                    "For a plain colored board use kind paint, or create_material.");
            if (a.Kind == "tile" && !a.TileWidthMm.HasValue)
                throw new ToolException("INVALID_ARGUMENTS", "tile needs tileWidthMm (and tileHeightMm if not square).");
        }

        protected override object Run(ToolContext ctx, CreateFinishMaterialArgs a)
        {
            var doc = ctx.Doc;
            var name = a.Name.Trim();
            ctx.Services.Names.Validate(name, "material name");

            // ---- inputs first: nothing is changed if any of them is wrong ----
            string imagePath = null;
            ImageInfo image = null;
            if (a.Kind != "paint" && !string.IsNullOrWhiteSpace(a.Image))
            {
                imagePath = PathPolicy.Resolve(a.Image, "textures");
                if (!ImageExtensions.Contains(Path.GetExtension(imagePath).ToLowerInvariant()))
                    throw new ToolException("INVALID_PATH", $"'{imagePath}' is not a PNG/JPG/BMP/TIF image.");
                if (!File.Exists(imagePath))
                    throw new ToolException("FILE_NOT_FOUND", $"'{imagePath}' does not exist.");
                image = ImageInfo.TryRead(imagePath, out var err);
                if (image == null)
                    ctx.Warnings.Add(new ToolWarning($"The image could not be read to get its size/average color ({err}); defaults were used."));
            }
            else if (a.Kind == "paint" && !string.IsNullOrWhiteSpace(a.Image))
                ctx.Warnings.Add(new ToolWarning("image is ignored for paint."));

            var color = !string.IsNullOrWhiteSpace(a.Color) ? MaterialGraphics.ParseColor(a.Color, "color") : image?.Average;
            var patternColor = !string.IsNullOrWhiteSpace(a.SurfacePatternColor)
                ? MaterialGraphics.ParseColor(a.SurfacePatternColor, "surfacePatternColor") : null;
            var cutPattern = a.CutPattern == null ? null : MaterialGraphics.FillPattern(doc, a.CutPattern, "cut pattern");
            var surfacePattern = a.SurfacePattern == null ? null : MaterialGraphics.FillPattern(doc, a.SurfacePattern, "surface pattern");

            double tileW = a.TileWidthMm ?? 0, tileH = a.TileHeightMm ?? a.TileWidthMm ?? 0;
            double texW = 0, texH = 0;
            if (imagePath != null)
            {
                texW = a.TextureWidthMm ?? (a.Kind == "tile" ? tileW + a.JointMm : 1000.0);
                double aspect = image != null && image.PixelWidth > 0 ? (double)image.PixelHeight / image.PixelWidth : 1.0;
                texH = a.TextureHeightMm ?? (a.Kind == "tile" && !a.TextureWidthMm.HasValue ? tileH + a.JointMm : Math.Round(texW * aspect, 1));
                if (a.Kind == "mdf" && !a.TextureWidthMm.HasValue)
                    ctx.Warnings.Add(new ToolWarning($"textureWidthMm not given: the image was mapped to {texW} x {texH} mm; pass the real size of the scanned board for a correct grain scale."));
            }

            // ---- the material ----
            var existing = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            Material material;
            string action;
            if (existing != null)
            {
                if (!string.Equals(a.IfExists, "update", StringComparison.OrdinalIgnoreCase))
                    throw new ToolException("NAME_COLLISION", $"Material '{existing.Name}' already exists (id {existing.Id.GetValue()}).",
                        "Use another name or ifExists:\"update\" to change it (affects every element that uses it).");
                material = existing;
                action = "updated";
            }
            else
            {
                material = doc.GetElement(Material.Create(doc, name)) as Material
                           ?? throw new ToolException("CREATE_FAILED", $"Revit did not create the material '{name}'.");
                action = "created";
            }

            var applied = new List<string>();
            material.MaterialClass = a.MaterialClass ?? (a.Kind == "mdf" ? "Madeira" : a.Kind == "tile" ? "Cerâmica" : "Pintura");
            applied.Add("materialClass");
            if (color != null)
            {
                material.Color = color;
                applied.Add("color");
            }
            else ctx.Warnings.Add(new ToolWarning("No shading color: pass color (the image's average could not be computed)."));
            material.UseRenderAppearanceForShading = false;

            // Patterns
            object tilePattern = null;
            if (surfacePattern != null)
            {
                material.SurfaceForegroundPatternId = surfacePattern;
                applied.Add("surfacePattern");
            }
            else if (a.Kind == "tile")
            {
                var p = FinishAppearance.TilePattern(doc, tileW, tileH, a.JointMm, out bool patternCreated);
                material.SurfaceForegroundPatternId = p.Id;
                tilePattern = new { id = p.Id.GetValue(), name = p.Name, created = patternCreated, spacingMm = new { x = tileW + a.JointMm, y = tileH + a.JointMm } };
                applied.Add("surfacePattern");
                if (patternColor == null) patternColor = new Color(128, 128, 128);
            }
            else if (action == "created")
            {
                material.SurfaceForegroundPatternId = ElementId.InvalidElementId;
            }
            if (patternColor != null)
            {
                material.SurfaceForegroundPatternColor = patternColor;
                applied.Add("surfacePatternColor");
            }
            if (cutPattern != null)
            {
                material.CutForegroundPatternId = cutPattern;
                applied.Add("cutPattern");
            }

            // Render appearance
            string assetSource = FinishAppearance.EnsureOwnGenericAsset(doc, material, ctx.Warnings);
            if (assetSource == null)
            {
                ctx.Warnings.Add(new ToolWarning("No Generic render appearance was found in the document or in Revit's library; " +
                                                 "only the shading color and patterns were set (the texture was not applied)."));
            }
            else if (a.Kind == "paint" || imagePath == null)
            {
                if (color != null && FinishAppearance.SetColor(doc, material, color)) applied.Add("appearanceColor");
            }
            else
            {
                var set = FinishAppearance.SetTexture(doc, material, imagePath, texW, texH, a.TextureRotationDeg, color, ctx.Warnings);
                applied.AddRange(set.Select(s => "texture." + s));
            }

            doc.Regenerate();
            var appearance = FinishAppearance.Describe(doc, material);
            return new
            {
                action,
                kind = a.Kind,
                material = MaterialGraphics.Describe(doc, material),
                appearance,
                appearanceSource = assetSource,
                tilePattern,
                image = imagePath == null ? null : new { path = imagePath, pixelWidth = image?.PixelWidth, pixelHeight = image?.PixelHeight, textureWidthMm = texW, textureHeightMm = texH },
                applied,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + $"Finish material '{material.Name}' ({a.Kind}) {action}."
            };
        }
    }
}
