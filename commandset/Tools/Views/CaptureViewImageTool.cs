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
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Views
{
    public sealed class CaptureViewImageArgs
    {
        [JsonProperty("view")]
        [Description("View or sheet (name or id). Omit for the active view (only when the target document is the active one).")]
        public string View { get; set; }

        [JsonProperty("widthPx")]
        [Description("Image width in pixels, 128-4096 (default 1200).")]
        [Range(128, 4096)]
        [DefaultValue(1200)]
        public int WidthPx { get; set; } = 1200;

        [JsonProperty("format")]
        [Description("png (default) or jpg.")]
        [EnumValues("png", "jpg")]
        [DefaultValue("png")]
        public string Format { get; set; } = "png";

        [JsonProperty("returnInline")]
        [Description("Return the image itself to the client (default true; images over 4 MB are only saved). false: only save the file and return its path.")]
        [DefaultValue(true)]
        public bool ReturnInline { get; set; } = true;
    }

    /// <summary>VIS-23: render a view/sheet to PNG/JPG in the allowed output folder and return it as an MCP image.</summary>
    [McpTool("capture_view_image", Kind = ToolKind.Read, Risk = Risk.Low, Toolset = "views", TimeoutSeconds = 120,
        Description = "Render a view or sheet to an image (png default, or jpg) at widthPx pixels, saved under the " +
                      "allowed output folder (Documents\\RevitMCP\\captures) with a unique name, and return it inline " +
                      "as an image so the model can be looked at; also returns the file path. Without view, captures " +
                      "the active view of the target document when it is the active one. Does not change the model.")]
    public sealed class CaptureViewImageTool : McpTool<CaptureViewImageArgs>
    {
        public const long MaxInlineBytes = 4 * 1024 * 1024;

        protected override object Run(ToolContext ctx, CaptureViewImageArgs args)
        {
            var doc = ctx.Doc;
            View view;
            if (!string.IsNullOrWhiteSpace(args.View)) view = ctx.Services.Lookup.View(args.View);
            else
            {
                var uidoc = ctx.UiApp?.ActiveUIDocument;
                if (uidoc == null || !(ReferenceEquals(uidoc.Document, doc) || uidoc.Document.Equals(doc)))
                    throw new ToolException("INVALID_ARGUMENTS", "view is required when the target document is not the active one.");
                view = uidoc.ActiveView;
            }
            if (view == null || view.IsTemplate)
                throw new ToolException("VIEW_NOT_EXPORTABLE", "That view cannot be exported (template or missing).");
            if (!view.CanBePrinted)
                throw new ToolException("VIEW_NOT_EXPORTABLE", $"'{view.Name}' ({view.ViewType}) cannot be exported as an image.");

            var jpg = string.Equals(args.Format, "jpg", StringComparison.OrdinalIgnoreCase);
            var folder = PathPolicy.Resolve(null, "captures", forWrite: true);
            Directory.CreateDirectory(folder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var baseName = "capture-" + view.Id.GetValue() + "-" + stamp;
            var basePath = Path.Combine(folder, baseName);

            var options = new ImageExportOptions
            {
                FilePath = basePath,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = args.WidthPx,
                FitDirection = FitDirectionType.Horizontal,
                ImageResolution = ImageResolution.DPI_150,
                HLRandWFViewsFileType = jpg ? ImageFileType.JPEGLossless : ImageFileType.PNG,
                ShadowViewsFileType = jpg ? ImageFileType.JPEGLossless : ImageFileType.PNG,
                ExportRange = ExportRange.SetOfViews
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });

            try
            {
                doc.ExportImage(options);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("EXPORT_FAILED", $"Revit could not export '{view.Name}': {ex.Message}");
            }

            // Revit appends " - <view type> - <view name>" to the file name; the unique base name finds exactly ours.
            var file = new DirectoryInfo(folder).GetFiles(baseName + "*")
                .Where(f => f.Extension.Equals(jpg ? ".jpg" : ".png", StringComparison.OrdinalIgnoreCase) ||
                            (jpg && f.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file == null || file.Length == 0)
                throw new ToolException("EXPORT_FAILED", $"Revit reported success but no image was written for '{view.Name}'.");

            var bytes = File.ReadAllBytes(file.FullName);
            var (w, h) = jpg ? (null, null) : PngSize(bytes);
            var mime = jpg ? "image/jpeg" : "image/png";
            var inline = args.ReturnInline && bytes.LongLength <= MaxInlineBytes;
            if (args.ReturnInline && !inline)
                ctx.Warnings.Add(new ToolWarning($"The image is {bytes.LongLength / 1024} KB, over the 4 MB inline limit; only the file was saved. Use a smaller widthPx."));

            return new
            {
                view = new { viewId = view.Id.GetValue(), name = view.Name, viewType = view.ViewType.ToString() },
                path = file.FullName,
                widthPx = w,
                heightPx = h,
                bytes = bytes.LongLength,
                image = inline ? new { base64 = Convert.ToBase64String(bytes), mimeType = mime } : null
            };
        }

        /// <summary>Width/height from the PNG IHDR chunk (bytes 16-23, big-endian).</summary>
        private static (int?, int?) PngSize(byte[] b)
        {
            if (b.Length < 24 || b[0] != 0x89 || b[1] != 0x50) return (null, null);
            int Read(int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
            return (Read(16), Read(20));
        }
    }
}
