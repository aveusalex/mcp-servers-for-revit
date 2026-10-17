using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Materials.Support
{
    /// <summary>Colors (#RRGGBB), fill patterns by name and material graphics read/write helpers.</summary>
    public static class MaterialGraphics
    {
        public static Color ParseColor(string text, string label)
        {
            var t = (text ?? string.Empty).Trim();
            if (t.StartsWith("#", StringComparison.Ordinal)) t = t.Substring(1);
            if (t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return new Color((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
            var parts = (text ?? string.Empty).Split(',', ';', ' ').Where(p => p.Length > 0).ToArray();
            if (parts.Length == 3 && parts.All(p => byte.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                return new Color(byte.Parse(parts[0], CultureInfo.InvariantCulture), byte.Parse(parts[1], CultureInfo.InvariantCulture),
                    byte.Parse(parts[2], CultureInfo.InvariantCulture));
            throw new ToolException("INVALID_ARGUMENTS", $"{label} '{text}' is not a color; use \"#RRGGBB\" or \"R,G,B\".");
        }

        public static string FormatColor(Color c)
        {
            if (c == null || !c.IsValid) return null;
            return "#" + c.Red.ToString("X2", CultureInfo.InvariantCulture) + c.Green.ToString("X2", CultureInfo.InvariantCulture) +
                   c.Blue.ToString("X2", CultureInfo.InvariantCulture);
        }

        public static bool SameColor(Color a, Color b) =>
            a != null && b != null && a.IsValid && b.IsValid && a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue;

        /// <summary>
        /// A fill pattern by name or id: "none" clears, "solid" is the solid fill; "model:Name" / "drafting:Name" pick
        /// the target when a name exists in both.
        /// </summary>
        public static ElementId FillPattern(Document doc, string text, string label)
        {
            var t = (text ?? string.Empty).Trim();
            if (t.Length == 0 || t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("nenhum", StringComparison.OrdinalIgnoreCase))
                return ElementId.InvalidElementId;
            var all = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().ToList();
            if (t.Equals("solid", StringComparison.OrdinalIgnoreCase) || t.Equals("sólido", StringComparison.OrdinalIgnoreCase))
            {
                var solid = all.FirstOrDefault(p => p.GetFillPattern().IsSolidFill && p.GetFillPattern().Target == FillPatternTarget.Drafting)
                            ?? all.FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
                return solid?.Id ?? throw new ToolException("NOT_FOUND", "The document has no solid fill pattern.");
            }
            if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                var byId = all.FirstOrDefault(p => p.Id.GetValue() == id);
                if (byId != null) return byId.Id;
            }
            FillPatternTarget? target = null;
            if (t.StartsWith("model:", StringComparison.OrdinalIgnoreCase)) { target = FillPatternTarget.Model; t = t.Substring(6).Trim(); }
            else if (t.StartsWith("drafting:", StringComparison.OrdinalIgnoreCase)) { target = FillPatternTarget.Drafting; t = t.Substring(9).Trim(); }
            var pool = target.HasValue ? all.Where(p => p.GetFillPattern().Target == target.Value).ToList() : all;
            return NameMatcher.Single(t, pool.Select(p => new NamedCandidate<ElementId>(p.Name, p.Id,
                (p.GetFillPattern().Target == FillPatternTarget.Model ? "model" : "drafting") + ", id " + p.Id.GetValue())), label);
        }

        public static string PatternName(Document doc, ElementId id) =>
            id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id)?.Name;

        public static object Describe(Document doc, Material m, int? useCount = null)
        {
            var asset = m.AppearanceAssetId == ElementId.InvalidElementId ? null : doc.GetElement(m.AppearanceAssetId);
            return new
            {
                id = m.Id.GetValue(),
                name = m.Name,
                materialClass = m.MaterialClass,
                materialCategory = m.MaterialCategory,
                color = FormatColor(m.Color),
                transparency = m.Transparency,
                shininess = m.Shininess,
                smoothness = m.Smoothness,
                useRenderAppearance = m.UseRenderAppearanceForShading,
                surfaceForeground = new { pattern = PatternName(doc, m.SurfaceForegroundPatternId), color = FormatColor(m.SurfaceForegroundPatternColor) },
                surfaceBackground = new { pattern = PatternName(doc, m.SurfaceBackgroundPatternId), color = FormatColor(m.SurfaceBackgroundPatternColor) },
                cutForeground = new { pattern = PatternName(doc, m.CutForegroundPatternId), color = FormatColor(m.CutForegroundPatternColor) },
                cutBackground = new { pattern = PatternName(doc, m.CutBackgroundPatternId), color = FormatColor(m.CutBackgroundPatternColor) },
                appearanceAsset = asset == null ? null : new { id = asset.Id.GetValue(), name = asset.Name },
                useCount
            };
        }

        /// <summary>Materials (other than <paramref name="except"/>) that share the appearance asset.</summary>
        public static List<Material> SharingAsset(Document doc, ElementId assetId, ElementId except) =>
            new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .Where(m => m.Id != except && m.AppearanceAssetId == assetId).ToList();

        /// <summary>Gives the material its own copy of the appearance asset. Returns the new asset id.</summary>
        public static ElementId DuplicateAsset(Document doc, Material m)
        {
            var asset = doc.GetElement(m.AppearanceAssetId) as AppearanceAssetElement;
            if (asset == null) return ElementId.InvalidElementId;
            var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(AppearanceAssetElement)).Select(e => e.Name),
                StringComparer.OrdinalIgnoreCase);
            var name = m.Name;
            for (int i = 2; names.Contains(name); i++) name = $"{m.Name} ({i})";
            var copy = asset.Duplicate(name);
            m.AppearanceAssetId = copy.Id;
            return copy.Id;
        }

        /// <summary>
        /// Sets the generic diffuse color of the render appearance: "ok", "texture" (set, but an image drives the
        /// diffuse so the color may not show), or "unsupported" (no editable generic diffuse; nothing changed).
        /// </summary>
        public static string SetAppearanceColor(Document doc, Material m, Color color)
        {
            if (m.AppearanceAssetId == ElementId.InvalidElementId) return "unsupported";
            using (var scope = new AppearanceAssetEditScope(doc))
            {
                var asset = scope.Start(m.AppearanceAssetId);
                var prop = asset.FindByName("generic_diffuse") as AssetPropertyDoubleArray4d;
                if (prop == null || prop.IsReadOnly)
                {
                    scope.Cancel();
                    return "unsupported";
                }
                var textured = prop.NumberOfConnectedProperties > 0;
                prop.SetValueAsColor(color);
                scope.Commit(true);
                return textured ? "texture" : "ok";
            }
        }
    }
}
