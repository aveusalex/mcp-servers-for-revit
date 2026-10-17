using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Export.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Export
{
    public sealed class ExportCadArgs
    {
        [JsonProperty("format")]
        [Description("dwg (default) or dxf.")]
        [EnumValues("dwg", "dxf")]
        [DefaultValue("dwg")]
        public string Format { get; set; } = "dwg";

        [JsonProperty("viewIds")]
        [Description("Views or sheets to export, by id.")]
        public List<long> ViewIds { get; set; }

        [JsonProperty("sheetNumberFilter")]
        [Description("Also export sheets whose number matches this pattern (* and ? wildcards, e.g. \"ARQ-*\").")]
        public string SheetNumberFilter { get; set; }

        [JsonProperty("setupName")]
        [Description("Saved DWG/DXF export setup to use (Export > Options > Export Setups). Default: Revit's default options.")]
        public string SetupName { get; set; }

        [JsonProperty("fileNamePattern")]
        [Description("File name per view (default \"{SheetNumber} - {SheetName}\" for sheets, \"{ViewName}\" for views). Placeholders: {SheetNumber} {SheetName} {ViewName} {ViewType} {Id} {Project} {Date}.")]
        public string FileNamePattern { get; set; }

        [JsonProperty("mergedViews")]
        [Description("DWG sheets: true (default) puts the views in the sheet file; false writes each view as an external reference file. Ignored for DXF.")]
        [DefaultValue(true)]
        public bool MergedViews { get; set; } = true;

        [JsonProperty("folder")]
        [Description("Output folder: absolute inside an allowed root, or relative to Documents\\RevitMCP\\exports. Default: exports\\<document>\\<timestamp>.")]
        public string Folder { get; set; }

        [JsonProperty("listOnly")]
        [Description("Only list the views and file names that would be exported. Default false.")]
        [DefaultValue(false)]
        public bool ListOnly { get; set; }
    }

    /// <summary>EXP-02: views/sheets to DWG or DXF.</summary>
    [McpTool("export_cad", Kind = ToolKind.Read, Risk = Risk.Low, Toolset = "export", Batchable = false,
        TimeoutSeconds = 900,
        Description = "Export views or sheets to DWG or DXF (one file per view/sheet, named by fileNamePattern), " +
                      "optionally with a saved export setup (setupName). Writes only inside the allowed folders (default " +
                      "Documents\\RevitMCP\\exports\\<document>\\<timestamp>) and returns every file really written " +
                      "(including xref and .pcp files). The model is not changed. Long exports answer 'running' with a jobId.")]
    public sealed class ExportCadTool : McpTool<ExportCadArgs>
    {
        protected override void Validate(ExportCadArgs a)
        {
            if ((a.ViewIds?.Count ?? 0) == 0 && string.IsNullOrWhiteSpace(a.SheetNumberFilter))
                throw new ToolException("INVALID_ARGUMENTS", "Give viewIds and/or sheetNumberFilter.");
        }

        protected override object Run(ToolContext ctx, ExportCadArgs a)
        {
            var doc = ctx.Doc;
            bool dxf = string.Equals(a.Format, "dxf", StringComparison.OrdinalIgnoreCase);
            var views = new List<View>();
            var skipped = new List<object>();
            foreach (var id in a.ViewIds ?? new List<long>())
            {
                var v = doc.GetElement(id.ToRevitElementId()) as View;
                if (v == null) throw new ToolException("VIEW_NOT_FOUND", $"{id} is not a view in the target document.");
                if (v.IsTemplate || v is ViewSchedule || (v is ViewSheet s0 && s0.IsPlaceholder))
                {
                    skipped.Add(new { id, name = v.Name, reason = v.IsTemplate ? "view template" : v is ViewSchedule ? "schedules can't be exported to CAD" : "placeholder sheet" });
                    continue;
                }
                if (!views.Any(x => x.Id == v.Id)) views.Add(v);
            }
            if (!string.IsNullOrWhiteSpace(a.SheetNumberFilter))
            {
                var rx = ExportPdfTool.WildcardRegex(a.SheetNumberFilter);
                foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                             .Where(s => !s.IsPlaceholder && rx.IsMatch(s.SheetNumber ?? string.Empty))
                             .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase))
                    if (!views.Any(x => x.Id == s.Id)) views.Add(s);
            }
            if (views.Count == 0)
                throw new ToolException("NOTHING_TO_EXPORT", "No exportable view or sheet matched.");

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var jobs = new List<(string name, View view)>();
            foreach (var v in views)
            {
                var pattern = !string.IsNullOrWhiteSpace(a.FileNamePattern) ? a.FileNamePattern
                    : v is ViewSheet ? "{SheetNumber} - {SheetName}" : "{ViewName}";
                var name = ExportSupport.ApplyPattern(pattern, doc, v);
                var unique = name;
                for (int n = 2; !used.Add(unique); n++) unique = $"{name} ({n})";
                jobs.Add((unique, v));
            }

            var ext = dxf ? ".dxf" : ".dwg";
            if (a.ListOnly)
                return new
                {
                    listOnly = true,
                    format = dxf ? "dxf" : "dwg",
                    files = jobs.Select(j => new { fileName = j.name + ext, viewId = j.view.Id.GetValue(), view = j.view.Name }).ToList(),
                    skipped
                };

            var folder = ExportSupport.OutputFolder(doc, a.Folder);
            var before = ExportSupport.Snapshot(folder);
            var failed = new List<object>();
            foreach (var job in jobs)
            {
                try
                {
                    var ids = new List<ElementId> { job.view.Id };
                    bool ok;
                    if (dxf)
                    {
                        var o = LoadDxf(doc, a.SetupName); // DXF has no merged-views option
                        ok = doc.Export(folder, job.name, ids, o);
                    }
                    else
                    {
                        var o = LoadDwg(doc, a.SetupName);
                        o.MergedViews = a.MergedViews;
                        ok = doc.Export(folder, job.name, ids, o);
                    }
                    if (!ok) failed.Add(new { fileName = job.name + ext, reason = "Revit's exporter returned false" });
                }
                catch (ToolException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed.Add(new { fileName = job.name + ext, reason = ex.Message });
                }
            }

            var written = ExportSupport.NewFiles(folder, before);
            if (written.Count == 0)
                throw new ToolException("EXPORT_FAILED", "Revit did not write any CAD file.",
                    failed.Count > 0 ? "First failure: " + JsonConvert.SerializeObject(failed[0]) : null);
            return new
            {
                format = dxf ? "dxf" : "dwg",
                folder,
                files = written,
                fileCount = written.Count,
                totalBytes = written.Sum(f => f.bytes),
                skipped,
                failed
            };
        }

        private static DWGExportOptions LoadDwg(Document doc, string setup)
        {
            if (string.IsNullOrWhiteSpace(setup)) return new DWGExportOptions();
            var o = DWGExportOptions.GetPredefinedOptions(doc, setup.Trim());
            if (o == null) throw SetupNotFound(doc, setup);
            return o;
        }

        private static DXFExportOptions LoadDxf(Document doc, string setup)
        {
            if (string.IsNullOrWhiteSpace(setup)) return new DXFExportOptions();
            var o = DXFExportOptions.GetPredefinedOptions(doc, setup.Trim());
            if (o == null) throw SetupNotFound(doc, setup);
            return o;
        }

        private static ToolException SetupNotFound(Document doc, string setup)
        {
            IList<string> names;
            try { names = BaseExportOptions.GetPredefinedSetupNames(doc); }
            catch { names = new List<string>(); }
            return new ToolException("EXPORT_SETUP_NOT_FOUND", $"No export setup named '{setup}'.",
                names.Count > 0 ? "Available: " + string.Join(", ", names.Select(n => $"'{n}'")) : "Omit setupName to use the default options.");
        }
    }
}
