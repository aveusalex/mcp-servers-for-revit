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
    public sealed class ExportIfcArgs
    {
        [JsonProperty("ifcVersion")]
        [Description("IFC2x3CV2 (default, Coordination View 2.0), IFC4RV (Reference View) or IFC4DTV (Design Transfer View).")]
        [EnumValues("IFC2x3CV2", "IFC4RV", "IFC4DTV")]
        [DefaultValue("IFC2x3CV2")]
        public string IfcVersion { get; set; } = "IFC2x3CV2";

        [JsonProperty("viewId")]
        [Description("Export only what is visible in this view (usually a 3D view). Omit to export the whole model.")]
        public long? ViewId { get; set; }

        [JsonProperty("exportBaseQuantities")]
        [Description("Include IFC base quantities (areas, volumes). Default false.")]
        [DefaultValue(false)]
        public bool ExportBaseQuantities { get; set; }

        [JsonProperty("spaceBoundaries")]
        [Description("Room/space boundaries: 0 none (default), 1 first level, 2 second level.")]
        [System.ComponentModel.DataAnnotations.Range(0, 2)]
        [DefaultValue(0)]
        public int SpaceBoundaries { get; set; }

        [JsonProperty("splitWallsAndColumnsByLevel")]
        [Description("Split walls and columns by building storey. Default false.")]
        [DefaultValue(false)]
        public bool SplitWallsAndColumnsByLevel { get; set; }

        [JsonProperty("fileName")]
        [Description("File name without extension (default \"{Project}\"). Placeholders {Project} {Date}.")]
        public string FileName { get; set; }

        [JsonProperty("folder")]
        [Description("Output folder: absolute inside an allowed root, or relative to Documents\\RevitMCP\\exports. Default: exports\\<document>\\<timestamp>.")]
        public string Folder { get; set; }

        [JsonProperty("overwrite")]
        [Description("Replace an existing file with the same name (default false: fail with FILE_EXISTS).")]
        [DefaultValue(false)]
        public bool Overwrite { get; set; }
    }

    /// <summary>
    /// EXP-04: IFC export. Revit's IFC exporter needs a modifiable document, so it runs inside a transaction that is
    /// always rolled back: the model is left exactly as it was.
    /// </summary>
    [McpTool("export_ifc", Kind = ToolKind.Read, Risk = Risk.Low, Toolset = "export", Batchable = false,
        TimeoutSeconds = 1800,
        Description = "Export the model (or only what a view shows, via viewId) to IFC 2x3 CV2, IFC4 RV or IFC4 DTV, with " +
                      "optional base quantities and space boundaries. Writes only inside the allowed folders (default " +
                      "Documents\\RevitMCP\\exports\\<document>\\<timestamp>) and returns the file really written. The " +
                      "exporter runs in a transaction that is always rolled back, so the model is not changed. Large " +
                      "models take minutes: the call answers 'running' with a jobId (poll get_job_result).")]
    public sealed class ExportIfcTool : McpTool<ExportIfcArgs>
    {
        protected override object Run(ToolContext ctx, ExportIfcArgs a)
        {
            var doc = ctx.Doc;
            if (doc.IsFamilyDocument)
                throw new ToolException("NOT_A_PROJECT", "IFC export needs a project document, not a family.");
            if (doc.IsReadOnly)
                throw new ToolException(ToolErrorCodes.DocumentReadOnly, $"'{doc.Title}' is read-only; Revit's IFC exporter needs a modifiable document.");
            if (doc.IsModifiable)
                throw new ToolException(ToolErrorCodes.DocumentBusy, $"'{doc.Title}' has an open transaction.", "Ask the user to finish the current command in Revit, then retry.");

            View filterView = null;
            if (a.ViewId.HasValue)
            {
                filterView = doc.GetElement(a.ViewId.Value.ToRevitElementId()) as View;
                if (filterView == null || filterView.IsTemplate)
                    throw new ToolException("VIEW_NOT_FOUND", $"{a.ViewId} is not a (non-template) view in the target document.");
            }

            var name = ExportSupport.ApplyPattern(string.IsNullOrWhiteSpace(a.FileName) ? "{Project}" : a.FileName, doc, null);
            if (name.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            var folder = ExportSupport.OutputFolder(doc, a.Folder);
            var target = Path.Combine(folder, name + ".ifc");
            if (File.Exists(target))
            {
                if (!a.Overwrite)
                    throw new ToolException("FILE_EXISTS", $"'{target}' already exists.", "Pass overwrite: true or another fileName.");
                File.Delete(target);
            }

            var options = new IFCExportOptions
            {
                FileVersion = ParseVersion(a.IfcVersion),
                ExportBaseQuantities = a.ExportBaseQuantities,
                SpaceBoundaryLevel = a.SpaceBoundaries,
                WallAndColumnSplitting = a.SplitWallsAndColumnsByLevel
            };
            if (filterView != null) options.FilterViewId = filterView.Id;

            var before = ExportSupport.Snapshot(folder);
            bool ok;
            var started = DateTime.UtcNow;
            using (var tx = new Transaction(doc, "MCP: export_ifc (rolled back)"))
            {
                tx.Start();
                try
                {
                    ok = doc.Export(folder, name + ".ifc", options);
                }
                finally
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                }
            }

            var written = ExportSupport.NewFiles(folder, before);
            if (!ok || written.Count == 0)
                throw new ToolException("EXPORT_FAILED", "Revit's IFC exporter did not write a file.",
                    "Check that the view (if any) shows model elements, and that the IFC exporter add-in is not disabled.");

            return new
            {
                ifcVersion = options.FileVersion.ToString(),
                filterView = filterView == null ? null : new { id = filterView.Id.GetValue(), name = filterView.Name },
                folder,
                files = written,
                seconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
                modelChanged = false
            };
        }

        private static IFCVersion ParseVersion(string v)
        {
            switch ((v ?? "IFC2x3CV2").Trim().ToUpperInvariant())
            {
                case "IFC4RV": return IFCVersion.IFC4RV;
                case "IFC4DTV": return IFCVersion.IFC4DTV;
                case "IFC2X3CV2": return IFCVersion.IFC2x3CV2;
                default: throw new ToolException("INVALID_ARGUMENTS", $"Unknown ifcVersion '{v}'.", "Use IFC2x3CV2, IFC4RV or IFC4DTV.");
            }
        }
    }
}
