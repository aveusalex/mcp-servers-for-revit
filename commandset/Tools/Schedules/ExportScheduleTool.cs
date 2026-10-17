using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Schedules.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Schedules
{
    public sealed class ExportScheduleArgs
    {
        [JsonProperty("schedules", Required = Required.Always)]
        [Description("Schedules to export, by name or id (up to 50).")]
        public List<string> Schedules { get; set; }

        [JsonProperty("folder")]
        [Description("Output folder: absolute inside an allowed folder, or relative to Documents\\RevitMCP. Default Documents\\RevitMCP\\schedules.")]
        public string Folder { get; set; }

        [JsonProperty("format")]
        [Description("csv (default, delimiter applies) or txt (tab-separated).")]
        [EnumValues("csv", "txt")]
        [DefaultValue("csv")]
        public string Format { get; set; } = "csv";

        [JsonProperty("delimiter")]
        [Description("CSV delimiter (default \";\", which Excel in pt-BR opens directly; numbers keep the project's decimal comma).")]
        [DefaultValue(";")]
        public string Delimiter { get; set; } = ";";

        [JsonProperty("includeHeaders")]
        [Description("Write the column headings as the first line (default true).")]
        [DefaultValue(true)]
        public bool IncludeHeaders { get; set; } = true;

        [JsonProperty("includeTitle")]
        [Description("Write the schedule title as the very first line (default false).")]
        [DefaultValue(false)]
        public bool IncludeTitle { get; set; }

        [JsonProperty("skipBlankRows")]
        [Description("Leave out blank separator rows (default true).")]
        [DefaultValue(true)]
        public bool SkipBlankRows { get; set; } = true;

        [JsonProperty("overwrite")]
        [Description("Overwrite existing files (default true). false fails if a file exists.")]
        [DefaultValue(true)]
        public bool Overwrite { get; set; } = true;
    }

    /// <summary>
    /// EXP-06 (TAB-08). Writes the rendered cells itself (UTF-8 with BOM) instead of ViewSchedule.Export, whose
    /// encoding depends on the machine. Read kind: the model is not changed; the file goes only to allowed folders.
    /// </summary>
    [McpTool("export_schedule", Kind = ToolKind.Read, Risk = Risk.Low, Toolset = "schedules",
        Description = "Export schedules to CSV (UTF-8 with BOM, delimiter \";\" by default for Excel pt-BR) or " +
                      "tab-separated TXT, with the visible columns and cell text exactly as Revit shows it. Files go " +
                      "to Documents\\RevitMCP\\schedules by default (or another allowed folder; others fail with " +
                      "PATH_NOT_ALLOWED), named after the schedule. Returns each path and row count. The model is not " +
                      "changed.")]
    public sealed class ExportScheduleTool : McpTool<ExportScheduleArgs>
    {
        protected override void Validate(ExportScheduleArgs args)
        {
            if (args.Schedules == null || args.Schedules.Count == 0 || args.Schedules.Any(string.IsNullOrWhiteSpace))
                throw new ToolException("INVALID_ARGUMENTS", "schedules must list at least one schedule name or id.");
            if (args.Schedules.Count > 50)
                throw new ToolException("INVALID_ARGUMENTS", "At most 50 schedules per call.");
            args.Format = (args.Format ?? "csv").Trim().ToLowerInvariant();
            if (args.Format == "csv" && string.IsNullOrEmpty(args.Delimiter))
                throw new ToolException("INVALID_ARGUMENTS", "delimiter cannot be empty.");
            if (args.Delimiter != null && (args.Delimiter.Length > 3 || args.Delimiter.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0))
                throw new ToolException("INVALID_ARGUMENTS", "delimiter must be 1-3 characters and cannot be a quote or line break.");
        }

        protected override object Run(ToolContext ctx, ExportScheduleArgs args)
        {
            var schedules = args.Schedules.Select(s => ScheduleSupport.Find(ctx, s)).ToList();
            var folder = PathPolicy.Resolve(args.Folder, string.IsNullOrWhiteSpace(args.Folder) ? "schedules" : null, forWrite: true);
            if (File.Exists(folder))
                throw new ToolException("INVALID_PATH", $"'{folder}' is a file; folder must be a directory.");
            Directory.CreateDirectory(folder);

            var delimiter = args.Format == "txt" ? "\t" : args.Delimiter;
            var extension = args.Format == "txt" ? ".txt" : ".csv";
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<object>();

            foreach (var schedule in schedules)
            {
                var baseName = SafeFileName(schedule.Name);
                var name = baseName;
                for (var n = 2; !used.Add(name); n++) name = baseName + " (" + n + ")";
                var path = Path.Combine(folder, name + extension);
                if (!PathPolicy.IsAllowed(path))
                    throw new ToolException("PATH_NOT_ALLOWED", $"'{path}' is outside the allowed folders.");
                if (!args.Overwrite && File.Exists(path))
                    throw new ToolException("FILE_EXISTS", $"'{path}' already exists.", "Pass overwrite:true or another folder.");

                var table = ScheduleTable.Read(schedule);
                var lines = new List<string>();
                if (args.IncludeTitle && !string.IsNullOrWhiteSpace(table.Title)) lines.Add(Line(new[] { table.Title }, delimiter));
                if (args.IncludeHeaders) lines.Add(Line(table.Columns, delimiter));
                var rows = table.Rows.Where(r => !args.SkipBlankRows || r.Kind != "blank").ToList();
                lines.AddRange(rows.Select(r => Line(r.Cells, delimiter)));

                try
                {
                    File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(true));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new ToolException("FILE_WRITE_FAILED", $"Could not write '{path}': {ex.Message}",
                        "Close the file if it is open in Excel, or choose another folder.");
                }

                files.Add(new
                {
                    scheduleId = schedule.Id.GetValue(),
                    schedule = schedule.Name,
                    path,
                    rows = rows.Count,
                    columns = table.Columns.Count
                });
            }

            return new { folder, files, count = files.Count };
        }

        private static string Line(IEnumerable<string> cells, string delimiter) =>
            string.Join(delimiter, cells.Select(c => Quote(c ?? string.Empty, delimiter)));

        private static string Quote(string cell, string delimiter)
        {
            if (cell.Contains("\"") || cell.Contains(delimiter) || cell.Contains("\n") || cell.Contains("\r"))
                return "\"" + cell.Replace("\"", "\"\"") + "\"";
            return cell;
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string((name ?? "schedule").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return cleaned.Length == 0 ? "schedule" : cleaned;
        }
    }
}
