using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Annotation.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Annotation
{
    public sealed class ImportImageArgs
    {
        [JsonProperty("path", Required = Required.Always)]
        [Required]
        [Description("Image file (PNG, JPG, BMP, TIF or PDF): absolute, or relative to Documents\\RevitMCP\\images; must be in the allowed folders.")]
        public string Path { get; set; }

        [JsonProperty("view", Required = Required.Always)]
        [Required]
        [Description("Sheet, drafting view, legend, plan, section, elevation or detail view: name or id (not 3D or schedules).")]
        public string View { get; set; }

        [JsonProperty("position")]
        [Description("Where the anchor goes, in the view plane (mm). Sheets: paper mm from the sheet origin. Plans: model X/Y. Default: the view's origin (sheets: 0,0).")]
        public ViewPointMm Position { get; set; }

        [JsonProperty("anchor")]
        [EnumValues("center", "topLeft", "topRight", "bottomLeft", "bottomRight")]
        [DefaultValue("center")]
        [Description("Which point of the image sits on position. Default center.")]
        public string Anchor { get; set; } = "center";

        [JsonProperty("widthMm")]
        [Unit("mm")]
        [Range(1, 1000000)]
        [Description("Image width, proportions kept. On sheets it is paper size; on other views it is model size (like a detail item). Default: Revit's size from the image resolution.")]
        public double? WidthMm { get; set; }

        [JsonProperty("link")]
        [DefaultValue(false)]
        [Description("false (default): the image is embedded in the model. true: linked to the file (breaks if the file moves).")]
        public bool Link { get; set; }

        [JsonProperty("pdfPage")]
        [Range(1, 10000)]
        [DefaultValue(1)]
        [Description("PDF only: page to import (1-based). Default 1.")]
        public int PdfPage { get; set; } = 1;

        [JsonProperty("pdfDpi")]
        [Range(72, 600)]
        [DefaultValue(150)]
        [Description("PDF only: rasterization resolution in dpi. Default 150.")]
        public int PdfDpi { get; set; } = 150;

        [JsonProperty("background")]
        [DefaultValue(false)]
        [Description("Draw the image behind model elements (background) instead of in front. Default false.")]
        public bool Background { get; set; }

        [JsonProperty("reuseType")]
        [DefaultValue(true)]
        [Description("Reuse an image type already loaded from the same file (and page) instead of importing it again. Default true.")]
        public bool ReuseType { get; set; } = true;
    }

    /// <summary>EXP-14: place a raster image (or a PDF page) on a sheet or 2D view.</summary>
    [McpTool("import_image", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "annotation", TimeoutSeconds = 120,
        Description = "Place an image (PNG/JPG/BMP/TIF, or one page of a PDF) on a sheet or a 2D/plan/section view at a " +
                      "position (mm in the view plane; sheets in paper mm) with an anchor and an optional width in mm " +
                      "(proportions kept). The file must be in the allowed folders (Documents\\RevitMCP\\images by default); " +
                      "it is embedded in the model unless link:true. An image type already loaded from the same file is " +
                      "reused. Not for 3D views or schedules. Returns the image instance and type ids and the placed size. " +
                      "Undo: Ctrl+Z or delete the instance.")]
    public sealed class ImportImageTool : McpTool<ImportImageArgs>
    {
        private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".pdf" };

        protected override object Run(ToolContext ctx, ImportImageArgs a)
        {
            var doc = ctx.Doc;
            var full = PathPolicy.Resolve(a.Path, "images");
            var ext = System.IO.Path.GetExtension(full).ToLowerInvariant();
            if (!Extensions.Contains(ext))
                throw new ToolException("INVALID_PATH", $"'{full}' is not a PNG/JPG/BMP/TIF/PDF file.");
            if (!File.Exists(full))
                throw new ToolException("FILE_NOT_FOUND", $"'{full}' does not exist.");
            bool pdf = ext == ".pdf";

            var view = ctx.Services.Lookup.View(a.View);
            if (!ImageInstance.IsValidView(view))
                throw new ToolException("VIEW_NOT_SUPPORTED", $"'{view.Name}' ({view.ViewType}) can't hold images.",
                    "Use a sheet, drafting view, legend, plan, section, elevation or detail view.");

            // Image type: reuse one loaded from the same file (and page), or import.
            ImageType type = null;
            bool reused = false;
            if (a.ReuseType)
            {
                type = new FilteredElementCollector(doc).OfClass(typeof(ImageType)).Cast<ImageType>()
                    .FirstOrDefault(t => SamePath(t, full) && (!pdf || SafePage(t) == a.PdfPage) &&
                                         SafeSource(t) == (a.Link ? ImageTypeSource.Link : ImageTypeSource.Import));
                reused = type != null;
            }
            if (type == null)
            {
                var options = new ImageTypeOptions(full, false, a.Link ? ImageTypeSource.Link : ImageTypeSource.Import);
                if (pdf)
                {
                    options.PageNumber = a.PdfPage;
                    options.Resolution = a.PdfDpi;
                }
                if (!options.IsValid(doc))
                    throw new ToolException("IMAGE_NOT_VALID", $"Revit can't import '{full}'" + (pdf ? $" (page {a.PdfPage})" : string.Empty) + ".",
                        pdf ? "Check that the PDF has that page and is not password-protected." : "Check that the file is a valid image.");
                try { type = ImageType.Create(doc, options); }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    throw new ToolException("IMAGE_IMPORT_FAILED", $"Revit could not import '{full}': {ex.Message}");
                }
            }

            var anchor = Placement(a.Anchor);
            var location = a.Position != null
                ? ViewPlane.ToModel(view, a.Position.X, a.Position.Y)
                : view is ViewSheet ? XYZ.Zero : view.Origin;
            ImageInstance instance;
            try
            {
                instance = ImageInstance.Create(doc, view, type.Id, new ImagePlacementOptions(location, anchor));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("IMAGE_PLACE_FAILED", $"Revit could not place the image in '{view.Name}': {ex.Message}");
            }

            instance.LockProportions = true;
            if (a.WidthMm.HasValue)
            {
                instance.Width = a.WidthMm.Value / 304.8;
                instance.SetLocation(location, anchor); // resizing keeps the center: put the anchor back
            }
            instance.DrawLayer = a.Background ? DrawLayer.Background : DrawLayer.Foreground;
            doc.Regenerate();

            double Mm(double ft) => Math.Round(ft * 304.8, 1);
            return new
            {
                imageInstanceId = instance.Id.GetValue(),
                imageTypeId = type.Id.GetValue(),
                imageTypeReused = reused,
                imageTypeName = type.Name,
                path = full,
                embedded = !a.Link,
                view = new { id = view.Id.GetValue(), name = view.Name, viewType = view.ViewType.ToString() },
                widthMm = Mm(instance.Width),
                heightMm = Mm(instance.Height),
                center = ViewPlane.FromModel(view, instance.GetLocation(BoxPlacement.Center)),
                sizeIs = view is ViewSheet ? "paper" : "model",
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"Image '{System.IO.Path.GetFileName(full)}' placed in '{view.Name}' ({Mm(instance.Width)} x {Mm(instance.Height)} mm)."
            };
        }

        private static BoxPlacement Placement(string text)
        {
            switch ((text ?? "center").Trim().ToLowerInvariant())
            {
                case "topleft": return BoxPlacement.TopLeft;
                case "topright": return BoxPlacement.TopRight;
                case "bottomleft": return BoxPlacement.BottomLeft;
                case "bottomright": return BoxPlacement.BottomRight;
                default: return BoxPlacement.Center;
            }
        }

        private static bool SamePath(ImageType t, string full)
        {
            try { return string.Equals(System.IO.Path.GetFullPath(t.Path), full, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static int SafePage(ImageType t)
        {
            try { return t.PageNumber; }
            catch { return 1; }
        }

        private static ImageTypeSource? SafeSource(ImageType t)
        {
            try { return t.Source; }
            catch { return null; }
        }
    }
}
