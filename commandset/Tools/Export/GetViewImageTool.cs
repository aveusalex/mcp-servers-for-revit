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
using RevitMCPCommandSet.Tools.Export.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Export
{
    public sealed class GetViewImageArgs
    {
        [JsonProperty("viewId")]
        [Required]
        [Description("Id of the view or sheet to render (any document view; it does not need to be open).")]
        public long ViewId { get; set; }

        [JsonProperty("pixels")]
        [Description("Image size in pixels along fitDirection, 64-4096 (default 1600). Big images cost many tokens.")]
        [System.ComponentModel.DataAnnotations.Range(64, 4096)]
        [DefaultValue(1600)]
        public int Pixels { get; set; } = 1600;

        [JsonProperty("fitDirection")]
        [Description("Which side 'pixels' applies to: horizontal (default) or vertical.")]
        [EnumValues("horizontal", "vertical")]
        [DefaultValue("horizontal")]
        public string FitDirection { get; set; } = "horizontal";

        [JsonProperty("format")]
        [Description("png (default) or jpg.")]
        [EnumValues("png", "jpg")]
        [DefaultValue("png")]
        public string Format { get; set; } = "png";

        [JsonProperty("returnImage")]
        [Description("Return the image so the assistant can see it (default true). false only saves the file (needs saveTo).")]
        [DefaultValue(true)]
        public bool ReturnImage { get; set; } = true;

        [JsonProperty("saveTo")]
        [Description("Optional folder to keep the image in (absolute inside an allowed root, or relative to Documents\\RevitMCP\\exports).")]
        public string SaveTo { get; set; }
    }

    /// <summary>EXP-03 (minimal; capture_view_image covers the active-view capture): one view by id → PNG/JPG.</summary>
    [McpTool("get_view_image", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "export", Batchable = false,
        TimeoutSeconds = 120,
        Description = "Render one view or sheet, by id, to a PNG (or JPG) and return it as an image the assistant can " +
                      "look at (data.image), sized by pixels/fitDirection. Optionally keep the file in an allowed folder " +
                      "(saveTo). Schedules can't be rendered. Read-only: the model is not changed.")]
    public sealed class GetViewImageTool : McpTool<GetViewImageArgs>
    {
        protected override void Validate(GetViewImageArgs a)
        {
            if (a.Pixels < 64 || a.Pixels > 4096)
                throw new ToolException("INVALID_ARGUMENTS", "pixels must be between 64 and 4096.");
            if (!a.ReturnImage && string.IsNullOrWhiteSpace(a.SaveTo))
                throw new ToolException("INVALID_ARGUMENTS", "With returnImage false, give saveTo (otherwise nothing is produced).");
        }

        protected override object Run(ToolContext ctx, GetViewImageArgs a)
        {
            var doc = ctx.Doc;
            var view = doc.GetElement(a.ViewId.ToRevitElementId()) as View;
            if (view == null) throw new ToolException("VIEW_NOT_FOUND", $"{a.ViewId} is not a view in the target document.");
            if (view.IsTemplate || view is ViewSchedule || view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.SystemBrowser)
                throw new ToolException("VIEW_NOT_EXPORTABLE", $"'{view.Name}' ({view.ViewType}) can't be rendered as an image.",
                    "Pick a plan, section, elevation, 3D view, drafting view or sheet.");

            bool jpg = string.Equals(a.Format, "jpg", StringComparison.OrdinalIgnoreCase);
            bool keep = !string.IsNullOrWhiteSpace(a.SaveTo);
            // saveTo may name a file ("planta.png", live test) or a folder.
            string targetFile = keep && Path.HasExtension(a.SaveTo)
                ? PathPolicy.Resolve(a.SaveTo, "exports", forWrite: true)
                : null;
            string folder = targetFile != null
                ? PathPolicy.Resolve(Guid.NewGuid().ToString("N"), Path.Combine("tmp", "images"))
                : keep
                    ? ExportSupport.OutputFolder(doc, a.SaveTo)
                    : PathPolicy.Resolve(Guid.NewGuid().ToString("N"), Path.Combine("tmp", "images"));
            Directory.CreateDirectory(folder);

            var baseName = ExportSupport.SafeFileName($"{view.Name}-{a.ViewId}", "view");
            var before = ExportSupport.Snapshot(folder);
            var o = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(folder, baseName),
                HLRandWFViewsFileType = jpg ? ImageFileType.JPEGMedium : ImageFileType.PNG,
                ShadowViewsFileType = jpg ? ImageFileType.JPEGMedium : ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                FitDirection = string.Equals(a.FitDirection, "vertical", StringComparison.OrdinalIgnoreCase)
                    ? FitDirectionType.Vertical
                    : FitDirectionType.Horizontal,
                PixelSize = a.Pixels
            };
            o.SetViewsAndSheets(new List<ElementId> { view.Id });

            string file;
            try
            {
                doc.ExportImage(o);
                // Revit appends the view type/name to FilePath, so look for what it really wrote.
                file = ExportSupport.NewFiles(folder, before)
                    .Where(f => f.fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                f.fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                f.fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.bytes).Select(f => f.path).FirstOrDefault();
                if (file == null)
                    throw new ToolException("EXPORT_FAILED", $"Revit did not write an image for '{view.Name}'.",
                        "The view may be empty or not printable.");
                if (targetFile != null)
                {
                    File.Copy(file, targetFile, true);
                    try { File.Delete(file); } catch { /* temp file; harmless if it stays */ }
                    file = targetFile;
                }

                object image = null;
                if (a.ReturnImage)
                {
                    var bytes = File.ReadAllBytes(file);
                    var mime = file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                    image = new { base64 = Convert.ToBase64String(bytes), mimeType = mime, bytes = bytes.Length };
                }

                return new
                {
                    view = new { id = view.Id.GetValue(), name = view.Name, viewType = view.ViewType.ToString() },
                    pixels = a.Pixels,
                    fitDirection = o.FitDirection.ToString().ToLowerInvariant(),
                    savedTo = keep ? file : null,
                    image
                };
            }
            finally
            {
                if (!keep || targetFile != null)
                {
                    try { Directory.Delete(folder, true); }
                    catch { /* temp folder; harmless if left behind */ }
                }
            }
        }
    }
}
