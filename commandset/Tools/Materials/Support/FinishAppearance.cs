using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;
using Color = Autodesk.Revit.DB.Color;

namespace RevitMCPCommandSet.Tools.Materials.Support
{
    /// <summary>Size and average color of a raster image (read with WPF; nothing is written).</summary>
    public sealed class ImageInfo
    {
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        public Color Average { get; set; }

        public static ImageInfo TryRead(string path, out string error)
        {
            error = null;
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapSource frame = decoder.Frames[0];
                    var info = new ImageInfo { PixelWidth = frame.PixelWidth, PixelHeight = frame.PixelHeight };
                    double scale = Math.Min(1.0, 64.0 / Math.Max(frame.PixelWidth, frame.PixelHeight));
                    BitmapSource small = scale < 1.0 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
                    var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                    int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
                    var pixels = new byte[stride * h];
                    bgra.CopyPixels(pixels, stride, 0);
                    long r = 0, g = 0, b = 0, n = 0;
                    for (int i = 0; i + 3 < pixels.Length; i += 4)
                    {
                        if (pixels[i + 3] < 16) continue; // transparent
                        b += pixels[i];
                        g += pixels[i + 1];
                        r += pixels[i + 2];
                        n++;
                    }
                    if (n > 0) info.Average = new Color((byte)(r / n), (byte)(g / n), (byte)(b / n));
                    return info;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }
    }

    /// <summary>
    /// Render appearance of finish materials: a Generic appearance asset whose diffuse is either a color or a
    /// bitmap (UnifiedBitmap) with a real-world size. New assets are duplicated from a Generic asset already in the
    /// document, or created from Revit's appearance library when the document has none.
    /// </summary>
    public static class FinishAppearance
    {
        private const string UnifiedBitmapSchema = "UnifiedBitmapSchema";

        public static bool IsGeneric(Asset asset) => asset?.FindByName(Generic.GenericDiffuse) is AssetPropertyDoubleArray4d;

        private static string UniqueAssetName(Document doc, string wanted)
        {
            var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(AppearanceAssetElement)).Select(e => e.Name),
                StringComparer.OrdinalIgnoreCase);
            var name = wanted;
            for (int i = 2; names.Contains(name); i++) name = $"{wanted} ({i})";
            return name;
        }

        /// <summary>
        /// Makes sure <paramref name="m"/> has its own Generic appearance asset. Returns how ("kept", "duplicated",
        /// "createdFromDocument", "createdFromLibrary") or null when no Generic asset could be found.
        /// </summary>
        public static string EnsureOwnGenericAsset(Document doc, Material m, IList<ToolWarning> warnings)
        {
            if (m.AppearanceAssetId != ElementId.InvalidElementId &&
                doc.GetElement(m.AppearanceAssetId) is AppearanceAssetElement current && IsGeneric(SafeAsset(current)))
            {
                if (MaterialGraphics.SharingAsset(doc, m.AppearanceAssetId, m.Id).Count == 0) return "kept";
                MaterialGraphics.DuplicateAsset(doc, m);
                return "duplicated";
            }

            // A Generic asset already in the document, preferring a plain one (no image on the diffuse) named "Generic".
            var candidates = new FilteredElementCollector(doc).OfClass(typeof(AppearanceAssetElement)).Cast<AppearanceAssetElement>()
                .Select(e => new { e, a = SafeAsset(e) })
                .Where(x => IsGeneric(x.a))
                .OrderBy(x => ((AssetPropertyDoubleArray4d)x.a.FindByName(Generic.GenericDiffuse)).NumberOfConnectedProperties > 0 ? 1 : 0)
                .ThenBy(x => x.e.Name.IndexOf("generic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             x.e.Name.IndexOf("genéric", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .ToList();
            if (candidates.Count > 0)
            {
                var copy = candidates[0].e.Duplicate(UniqueAssetName(doc, m.Name));
                m.AppearanceAssetId = copy.Id;
                return "createdFromDocument";
            }

            try
            {
                var library = doc.Application.GetAssets(AssetType.Appearance);
                var generic = library.Where(IsGeneric)
                    .OrderBy(a => ((AssetPropertyDoubleArray4d)a.FindByName(Generic.GenericDiffuse)).NumberOfConnectedProperties > 0 ? 1 : 0)
                    .FirstOrDefault();
                if (generic != null)
                {
                    var created = AppearanceAssetElement.Create(doc, UniqueAssetName(doc, m.Name), generic);
                    m.AppearanceAssetId = created.Id;
                    return "createdFromLibrary";
                }
            }
            catch (Exception ex)
            {
                warnings.Add(new ToolWarning("Revit's appearance library could not be read: " + ex.Message));
            }
            return null;
        }

        private static Asset SafeAsset(AppearanceAssetElement e)
        {
            try { return e.GetRenderingAsset(); }
            catch { return null; }
        }

        /// <summary>Sets a plain diffuse color (removing an image on the diffuse). Returns false if not editable.</summary>
        public static bool SetColor(Document doc, Material m, Color color)
        {
            using (var scope = new AppearanceAssetEditScope(doc))
            {
                var asset = scope.Start(m.AppearanceAssetId);
                if (!(asset.FindByName(Generic.GenericDiffuse) is AssetPropertyDoubleArray4d diffuse) || diffuse.IsReadOnly)
                {
                    scope.Cancel();
                    return false;
                }
                if (diffuse.NumberOfConnectedProperties > 0) diffuse.RemoveConnectedAsset();
                diffuse.SetValueAsColor(color);
                scope.Commit(true);
                return true;
            }
        }

        /// <summary>
        /// Puts <paramref name="imagePath"/> on the diffuse as a UnifiedBitmap with the given real-world size (mm)
        /// and rotation. Returns the properties that were set and adds a warning for each one Revit refused.
        /// </summary>
        public static List<string> SetTexture(Document doc, Material m, string imagePath, double widthMm, double heightMm,
            double rotationDeg, Color tint, IList<ToolWarning> warnings)
        {
            var set = new List<string>();
            using (var scope = new AppearanceAssetEditScope(doc))
            {
                var asset = scope.Start(m.AppearanceAssetId);
                if (!(asset.FindByName(Generic.GenericDiffuse) is AssetPropertyDoubleArray4d diffuse) || diffuse.IsReadOnly)
                {
                    scope.Cancel();
                    throw new ToolException("APPEARANCE_NOT_EDITABLE", $"The appearance of '{m.Name}' has no editable Generic diffuse.");
                }
                if (tint != null) diffuse.SetValueAsColor(tint);

                Asset bitmap = diffuse.NumberOfConnectedProperties > 0 ? diffuse.GetSingleConnectedAsset() : null;
                if (bitmap == null || !(bitmap.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) is AssetPropertyString))
                {
                    if (diffuse.NumberOfConnectedProperties > 0) diffuse.RemoveConnectedAsset();
                    diffuse.AddConnectedAsset(UnifiedBitmapSchema);
                    bitmap = diffuse.GetSingleConnectedAsset();
                }
                var path = bitmap?.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) as AssetPropertyString;
                if (path == null || !path.IsValidValue(imagePath))
                {
                    scope.Cancel();
                    throw new ToolException("TEXTURE_NOT_SET", $"Revit refused the image path '{imagePath}' for the texture.",
                        "Use a PNG/JPG/BMP/TIF image.");
                }
                path.Value = imagePath;
                set.Add("image");

                if (bitmap.FindByName(UnifiedBitmap.TextureScaleLock) is AssetPropertyBoolean scaleLock && !scaleLock.IsReadOnly)
                    scaleLock.Value = Math.Abs(widthMm - heightMm) < 0.01;
                if (SetDistance(bitmap, UnifiedBitmap.TextureRealWorldScaleX, widthMm)) set.Add("widthMm");
                else warnings.Add(new ToolWarning("The texture width (real-world scale X) could not be set on this Revit version; adjust it in the material browser."));
                if (SetDistance(bitmap, UnifiedBitmap.TextureRealWorldScaleY, heightMm)) set.Add("heightMm");
                else warnings.Add(new ToolWarning("The texture height (real-world scale Y) could not be set on this Revit version; adjust it in the material browser."));
                if (Math.Abs(rotationDeg) > 1e-9)
                {
                    if (bitmap.FindByName(UnifiedBitmap.TextureWAngle) is AssetPropertyDouble angle && !angle.IsReadOnly && angle.IsValidValue(rotationDeg))
                    {
                        angle.Value = rotationDeg;
                        set.Add("rotationDeg");
                    }
                    else warnings.Add(new ToolWarning("The texture rotation could not be set; rotate it in the material browser."));
                }
                scope.Commit(true);
            }
            return set;
        }

        private static bool SetDistance(Asset bitmap, string name, double mm)
        {
            if (!(bitmap.FindByName(name) is AssetPropertyDistance d) || d.IsReadOnly) return false;
            double value;
            try { value = UnitUtils.Convert(mm, UnitTypeId.Millimeters, d.GetUnitTypeId()); }
            catch { value = mm / 25.4; } // appearance distances default to inches
            if (!d.IsValidValue(value)) return false;
            d.Value = value;
            return true;
        }

        /// <summary>What the material's appearance diffuse holds now (read back after the edit).</summary>
        public static object Describe(Document doc, Material m)
        {
            if (m.AppearanceAssetId == ElementId.InvalidElementId) return null;
            var element = doc.GetElement(m.AppearanceAssetId) as AppearanceAssetElement;
            var asset = element == null ? null : SafeAsset(element);
            if (!(asset?.FindByName(Generic.GenericDiffuse) is AssetPropertyDoubleArray4d diffuse))
                return new { assetId = m.AppearanceAssetId.GetValue(), assetName = element?.Name, generic = false };
            var bitmap = diffuse.NumberOfConnectedProperties > 0 ? diffuse.GetSingleConnectedAsset() : null;
            double? Mm(string name)
            {
                if (!(bitmap?.FindByName(name) is AssetPropertyDistance d)) return null;
                try { return Math.Round(UnitUtils.Convert(d.Value, d.GetUnitTypeId(), UnitTypeId.Millimeters), 1); }
                catch { return null; }
            }
            return new
            {
                assetId = element.Id.GetValue(),
                assetName = element.Name,
                generic = true,
                diffuseColor = MaterialGraphics.FormatColor(diffuse.GetValueAsColor()),
                texture = bitmap == null ? null : new
                {
                    image = (bitmap.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) as AssetPropertyString)?.Value,
                    widthMm = Mm(UnifiedBitmap.TextureRealWorldScaleX),
                    heightMm = Mm(UnifiedBitmap.TextureRealWorldScaleY),
                    rotationDeg = (bitmap.FindByName(UnifiedBitmap.TextureWAngle) as AssetPropertyDouble)?.Value
                }
            };
        }

        /// <summary>
        /// A model fill pattern drawing the tile grid (one line per joint, spacing = tile + joint). Reuses an
        /// existing model pattern of the same name when its grid matches; otherwise creates "name (2)".
        /// </summary>
        public static FillPatternElement TilePattern(Document doc, double tileWmm, double tileHmm, double jointMm, out bool created)
        {
            created = false;
            double sx = (tileWmm + jointMm) / 304.8, sy = (tileHmm + jointMm) / 304.8;
            var baseName = $"Revestimento {Fmt(tileWmm)}x{Fmt(tileHmm)}" + (jointMm > 0 ? $" junta {Fmt(jointMm)}" : string.Empty);
            // Crosshatch: grid 1 at 0° spaced along Y (horizontal joints every tile height), grid 2 at 90° (vertical joints).
            var wanted = new FillPattern(baseName, FillPatternTarget.Model, FillPatternHostOrientation.ToHost, 0.0, sy, sx);
            var models = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .Where(p => p.GetFillPattern().Target == FillPatternTarget.Model).ToList();
            var name = baseName;
            for (int i = 2; ; i++)
            {
                var same = models.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (same == null) break;
                if (SameGrids(same.GetFillPattern(), wanted)) return same;
                name = $"{baseName} ({i})";
            }
            if (name != baseName) wanted = new FillPattern(name, FillPatternTarget.Model, FillPatternHostOrientation.ToHost, 0.0, sy, sx);
            created = true;
            return FillPatternElement.Create(doc, wanted);
        }

        private static bool SameGrids(FillPattern a, FillPattern b)
        {
            var ga = a.GetFillGrids();
            var gb = b.GetFillGrids();
            if (ga.Count != gb.Count) return false;
            for (int i = 0; i < ga.Count; i++)
            {
                if (Math.Abs(ga[i].Angle - gb[i].Angle) > 1e-6 || Math.Abs(ga[i].Offset - gb[i].Offset) > 1e-6 ||
                    Math.Abs(ga[i].Shift - gb[i].Shift) > 1e-6) return false;
            }
            return true;
        }

        private static string Fmt(double mm) => Math.Round(mm, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }
}
