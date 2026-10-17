using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Export.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Export
{
    public sealed class ExportPdfArgs
    {
        [JsonProperty("sheetIds")]
        [Description("Sheet ids to export.")]
        public List<long> SheetIds { get; set; }

        [JsonProperty("viewIds")]
        [Description("Other views (not sheets) to export, by id.")]
        public List<long> ViewIds { get; set; }

        [JsonProperty("sheetNumberFilter")]
        [Description("Sheets whose number matches this pattern: * and ? wildcards (e.g. \"ARQ-1*\"), case-insensitive.")]
        public string SheetNumberFilter { get; set; }

        [JsonProperty("viewSheetSetName")]
        [Description("Name of a saved view/sheet set (Print Setup > View/Sheet Set) to export.")]
        public string ViewSheetSetName { get; set; }

        [JsonProperty("combine")]
        [Description("true: one combined PDF; false (default): one PDF per sheet/view.")]
        [DefaultValue(false)]
        public bool Combine { get; set; }

        [JsonProperty("fileNamePattern")]
        [Description("Per-sheet file name pattern (default \"{SheetNumber} - {SheetName}\"; for views \"{ViewName}\"). Placeholders: {SheetNumber} {SheetName} {ViewName} {ViewType} {Id} {Project} {Date}.")]
        public string FileNamePattern { get; set; }

        [JsonProperty("fileName")]
        [Description("Name of the combined PDF (without extension; default \"{Project} - {Date}\"). Same placeholders as fileNamePattern.")]
        public string FileName { get; set; }

        [JsonProperty("folder")]
        [Description("Output folder: absolute inside an allowed root, or relative to Documents\\RevitMCP\\exports. Default: exports\\<document>\\<timestamp>.")]
        public string Folder { get; set; }

        [JsonProperty("overwrite")]
        [Description("Replace PDFs that already exist (default false: those sheets are skipped).")]
        [DefaultValue(false)]
        public bool Overwrite { get; set; }

        [JsonProperty("paperFormat")]
        [Description("Paper: \"default\" (use each sheet's size, default) or A0, A1, A2, A3, A4.")]
        [EnumValues("default", "A0", "A1", "A2", "A3", "A4")]
        [DefaultValue("default")]
        public string PaperFormat { get; set; } = "default";

        [JsonProperty("colorMode")]
        [Description("color (default), grayscale or blackLine.")]
        [EnumValues("color", "grayscale", "blackLine")]
        [DefaultValue("color")]
        public string ColorMode { get; set; } = "color";

        [JsonProperty("rasterQuality")]
        [Description("Raster quality: low, medium, high (default) or presentation.")]
        [EnumValues("low", "medium", "high", "presentation")]
        [DefaultValue("high")]
        public string RasterQuality { get; set; } = "high";

        [JsonProperty("hideCropBoundaries")]
        [Description("Hide crop boundaries (default true).")]
        [DefaultValue(true)]
        public bool HideCropBoundaries { get; set; } = true;

        [JsonProperty("hideScopeBoxes")]
        [Description("Hide scope boxes (default true).")]
        [DefaultValue(true)]
        public bool HideScopeBoxes { get; set; } = true;

        [JsonProperty("hideReferencePlanes")]
        [Description("Hide reference planes (default true).")]
        [DefaultValue(true)]
        public bool HideReferencePlanes { get; set; } = true;

        [JsonProperty("hideUnreferencedViewTags")]
        [Description("Hide unreferenced view tags (default true).")]
        [DefaultValue(true)]
        public bool HideUnreferencedViewTags { get; set; } = true;

        [JsonProperty("listOnly")]
        [Description("Only list what would be exported (sheets, file names, skips) without writing anything. Default false.")]
        [DefaultValue(false)]
        public bool ListOnly { get; set; }
    }

    /// <summary>EXP-01: sheets/views to PDF with Revit's native PDF exporter (2022+).</summary>
    [McpTool("export_pdf", Kind = ToolKind.Read, Risk = Risk.Low, Toolset = "export", Batchable = false,
        TimeoutSeconds = 900,
        Description = "Export sheets (by ids, sheet number wildcard or a saved view/sheet set) and/or views to PDF with " +
                      "Revit's PDF exporter, one file per sheet (fileNamePattern) or one combined file. Writes only inside " +
                      "the allowed folders (default Documents\\RevitMCP\\exports\\<document>\\<timestamp>) and returns the " +
                      "files really written (path, bytes) plus skipped sheets with the reason. The model is not changed. " +
                      "Use listOnly to preview. Long exports answer 'running' with a jobId (poll get_job_result).")]
    public sealed class ExportPdfTool : McpTool<ExportPdfArgs>
    {
        protected override void Validate(ExportPdfArgs a)
        {
            if ((a.SheetIds?.Count ?? 0) == 0 && (a.ViewIds?.Count ?? 0) == 0 &&
                string.IsNullOrWhiteSpace(a.SheetNumberFilter) && string.IsNullOrWhiteSpace(a.ViewSheetSetName))
                throw new ToolException("INVALID_ARGUMENTS",
                    "Say what to export: sheetIds, viewIds, sheetNumberFilter or viewSheetSetName.",
                    "To export every sheet, use sheetNumberFilter \"*\".");
        }

        protected override object Run(ToolContext ctx, ExportPdfArgs a)
        {
            var doc = ctx.Doc;
            var skipped = new List<object>();
            var views = new List<View>();

            void Add(View v)
            {
                if (v == null || views.Any(x => x.Id == v.Id)) return;
                if (v.IsTemplate)
                {
                    skipped.Add(new { id = v.Id.GetValue(), name = v.Name, reason = "view template" });
                    return;
                }
                if (v is ViewSheet s && s.IsPlaceholder)
                {
                    skipped.Add(new { id = v.Id.GetValue(), name = s.SheetNumber + " - " + s.Name, reason = "placeholder sheet (nothing to print)" });
                    return;
                }
                if (!v.CanBePrinted)
                {
                    skipped.Add(new { id = v.Id.GetValue(), name = v.Name, reason = $"{v.ViewType} views can't be printed" });
                    return;
                }
                views.Add(v);
            }

            foreach (var id in a.SheetIds ?? new List<long>())
            {
                var v = doc.GetElement(id.ToRevitElementId()) as ViewSheet;
                if (v == null) throw new ToolException("SHEET_NOT_FOUND", $"{id} is not a sheet in the target document.",
                    "Use sheetNumberFilter, or find_elements with categories [\"OST_Sheets\"].", "find_elements");
                Add(v);
            }
            foreach (var id in a.ViewIds ?? new List<long>())
            {
                var v = doc.GetElement(id.ToRevitElementId()) as View;
                if (v == null) throw new ToolException("VIEW_NOT_FOUND", $"{id} is not a view in the target document.");
                Add(v);
            }
            if (!string.IsNullOrWhiteSpace(a.SheetNumberFilter))
            {
                var rx = WildcardRegex(a.SheetNumberFilter);
                var matched = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => rx.IsMatch(s.SheetNumber ?? string.Empty))
                    .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
                if (matched.Count == 0)
                    throw new ToolException("SHEET_NOT_FOUND", $"No sheet number matches '{a.SheetNumberFilter}'.");
                matched.ForEach(Add);
            }
            if (!string.IsNullOrWhiteSpace(a.ViewSheetSetName))
            {
                var sets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>().ToList();
                var set = sets.FirstOrDefault(s => TextMatch.Key(s.Name) == TextMatch.Key(a.ViewSheetSetName));
                if (set == null)
                    throw new ToolException("SHEET_SET_NOT_FOUND", $"No view/sheet set named '{a.ViewSheetSetName}'.",
                        sets.Count > 0 ? "Available: " + string.Join(", ", sets.Select(s => $"'{s.Name}'").Take(30)) : "The document has no saved view/sheet sets.");
                foreach (View v in set.Views) Add(v);
            }

            if (views.Count == 0)
                throw new ToolException("NOTHING_TO_EXPORT", "None of the requested sheets/views can be exported.", "See the skipped reasons in the message data.");

            var folder = ExportSupport.OutputFolder(doc, a.Folder);
            var jobs = new List<(string name, List<View> views)>();
            if (a.Combine)
            {
                jobs.Add((ExportSupport.ApplyPattern(string.IsNullOrWhiteSpace(a.FileName) ? "{Project} - {Date}" : a.FileName, doc, null), views));
            }
            else
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in views)
                {
                    var pattern = !string.IsNullOrWhiteSpace(a.FileNamePattern) ? a.FileNamePattern
                        : v is ViewSheet ? "{SheetNumber} - {SheetName}" : "{ViewName}";
                    var name = ExportSupport.ApplyPattern(pattern, doc, v);
                    var unique = name;
                    for (int n = 2; !used.Add(unique); n++) unique = $"{name} ({n})";
                    jobs.Add((unique, new List<View> { v }));
                }
            }

            var planned = new List<object>();
            var toRun = new List<(string name, List<View> views)>();
            foreach (var job in jobs)
            {
                var target = Path.Combine(folder, job.name + ".pdf");
                if (File.Exists(target) && !a.Overwrite)
                {
                    skipped.Add(new { name = job.name, reason = "file exists (pass overwrite: true to replace it)", path = target });
                    continue;
                }
                planned.Add(new { fileName = job.name + ".pdf", views = job.views.Select(Label).ToList() });
                toRun.Add(job);
            }

            if (a.ListOnly)
                return new { listOnly = true, folder, files = planned, skipped, viewCount = views.Count };

            var written = new List<ExportedFile>();
            var failed = new List<object>();
            var before = ExportSupport.Snapshot(folder);
            foreach (var job in toRun)
            {
                var target = Path.Combine(folder, job.name + ".pdf");
                try
                {
                    if (File.Exists(target)) File.Delete(target); // overwrite: true (checked above)
                    var options = BuildOptions(a);
                    options.FileName = job.name;
                    options.Combine = true;
                    var ok = doc.Export(folder, job.views.Select(v => v.Id).ToList(), options);
                    if (!ok) failed.Add(new { fileName = job.name + ".pdf", reason = "Revit's PDF exporter returned false" });
                }
                catch (Exception ex)
                {
                    failed.Add(new { fileName = job.name + ".pdf", reason = ex.Message });
                }
            }
            written.AddRange(ExportSupport.NewFiles(folder, before));
            if (written.Count == 0)
                throw new ToolException("EXPORT_FAILED", "Revit did not write any PDF.",
                    failed.Count > 0 ? "First failure: " + JsonConvert.SerializeObject(failed[0]) : "Check that the sheets have printable content.");

            return new
            {
                folder,
                files = written,
                fileCount = written.Count,
                totalBytes = written.Sum(f => f.bytes),
                exportedViews = views.Count,
                skipped,
                failed
            };
        }

        private static object Label(View v) =>
            v is ViewSheet s
                ? (object)new { id = v.Id.GetValue(), sheetNumber = s.SheetNumber, name = s.Name }
                : new { id = v.Id.GetValue(), name = v.Name, viewType = v.ViewType.ToString() };

        private static PDFExportOptions BuildOptions(ExportPdfArgs a)
        {
            var o = new PDFExportOptions
            {
                HideCropBoundaries = a.HideCropBoundaries,
                HideScopeBoxes = a.HideScopeBoxes,
                HideReferencePlane = a.HideReferencePlanes,
                HideUnreferencedViewTags = a.HideUnreferencedViewTags,
                ZoomType = ZoomType.Zoom,
                ZoomPercentage = 100
            };
            switch ((a.ColorMode ?? "color").ToLowerInvariant())
            {
                case "grayscale": o.ColorDepth = ColorDepthType.GrayScale; break;
                case "blackline": o.ColorDepth = ColorDepthType.BlackLine; break;
                default: o.ColorDepth = ColorDepthType.Color; break;
            }
            switch ((a.RasterQuality ?? "high").ToLowerInvariant())
            {
                case "low": o.RasterQuality = RasterQualityType.Low; break;
                case "medium": o.RasterQuality = RasterQualityType.Medium; break;
                case "presentation": o.RasterQuality = RasterQualityType.Presentation; break;
                default: o.RasterQuality = RasterQualityType.High; break;
            }
            var paper = (a.PaperFormat ?? "default").Trim();
            if (!paper.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse("ISO_" + paper.ToUpperInvariant(), out ExportPaperFormat pf)) o.PaperFormat = pf;
                else throw new ToolException("INVALID_ARGUMENTS", $"Unknown paperFormat '{paper}'.");
            }
            else
            {
                o.PaperFormat = ExportPaperFormat.Default;
            }
            return o;
        }

        internal static Regex WildcardRegex(string pattern) =>
            new Regex("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
