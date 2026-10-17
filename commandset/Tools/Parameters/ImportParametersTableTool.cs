using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Parameters.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Parameters
{
    public sealed class ImportParametersTableArgs
    {
        [JsonProperty("path", Required = Required.Always)]
        [Required]
        [Description("CSV file to read: absolute, or relative to Documents\\RevitMCP\\exports. Must be inside the allowed folders.")]
        public string Path { get; set; }

        [JsonProperty("keyColumn")]
        [Description("Header of the column that identifies the element (UniqueId or ElementId values). Default: the column named UniqueId or ElementId, else the first one.")]
        public string KeyColumn { get; set; }

        [JsonProperty("columns")]
        [Description("Only import these parameter columns (header names without the [unit] / {ref} suffix). Default: every column not starting with '_'.")]
        public List<string> Columns { get; set; }

        [JsonProperty("decimalSeparator")]
        [Description("Decimal separator used in the file: \",\" (default, as exported) or \".\".")]
        [EnumValues(",", ".")]
        [DefaultValue(",")]
        public string DecimalSeparator { get; set; } = ",";

        [JsonProperty("emptyCellsClear")]
        [Description("true: an empty cell clears the parameter. false (default): empty cells are ignored.")]
        [DefaultValue(false)]
        public bool EmptyCellsClear { get; set; }

        [JsonProperty("target")]
        [Description("Where to resolve each column: auto (default: instance, then type), instance or type.")]
        [EnumValues("auto", "instance", "type")]
        [DefaultValue("auto")]
        public string Target { get; set; } = "auto";

        [JsonProperty("maxWrites")]
        [Description("Refuse when more cells would be written (default 5000, max 20000).")]
        [Range(1, 20000)]
        [DefaultValue(5000)]
        public int MaxWrites { get; set; } = 5000;

        [JsonProperty("sampleSize")]
        [Description("How many changed cells to list (default 50).")]
        [Range(0, 1000)]
        [DefaultValue(50)]
        public int SampleSize { get; set; } = 50;

        [JsonProperty("atomic")]
        [Description("true (default): any problem cell (invalid value, read-only, missing element or parameter) or failed write rolls everything back. false: write the valid cells and report the rest.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;

        [JsonProperty("allowCritical")]
        [Description("Allow critical parameters (type, level, phase, workset, host; repeated Mark). Default false.")]
        [DefaultValue(false)]
        public bool AllowCritical { get; set; }
    }

    /// <summary>PAR-07: CSV (from export_parameters_table or Excel) back into parameters, with a per-cell diff.</summary>
    [McpTool("import_parameters_table", Kind = ToolKind.Write, Risk = Risk.Medium, DryRunDefault = true, Toolset = "parameters", TimeoutSeconds = 120,
        Description = "Import a CSV table (as written by export_parameters_table and edited in Excel) into parameters. " +
                      "Rows are found by the key column (UniqueId or ElementId); headers name the parameter (\"Width [mm]\", " +
                      "optional {builtIn:X}/{guid:G}); columns starting with '_' are ignored. ALWAYS run it first with " +
                      "dryRun:true (no approval needed): you get a per-cell diff (changed, unchanged, invalid, read-only, " +
                      "missing element/parameter) and nothing is kept. Then run it for real (needs approval): one " +
                      "transaction, read-back, atomic by default. Only reads inside the allowed folders. Undo: Ctrl+Z or " +
                      "send revert[] to set_parameters / set_type_parameters.")]
    public sealed class ImportParametersTableTool : McpTool<ImportParametersTableArgs>
    {
        private sealed class Column
        {
            public int Index;
            public string Header;
            public string Name;
            public string Unit;
            public ParameterRef Ref;
        }

        protected override object Run(ToolContext ctx, ImportParametersTableArgs a)
        {
            var full = PathPolicy.Resolve(a.Path, "exports");
            if (!File.Exists(full))
                throw new ToolException("FILE_NOT_FOUND", $"'{full}' does not exist.", "Export first with export_parameters_table, or check the path.", "export_parameters_table");

            List<List<string>> rows;
            try { rows = CsvTable.Read(full); }
            catch (IOException ex) { throw new ToolException("FILE_READ_FAILED", $"Could not read '{full}': {ex.Message}", "Close the file in Excel and retry."); }
            if (rows.Count < 2) throw new ToolException("INVALID_FILE", "The file has no data rows.");

            var header = rows[0];
            int keyIndex = FindKeyColumn(header, a.KeyColumn);
            var columns = Columns(header, keyIndex, a.Columns);
            if (columns.Count == 0)
                throw new ToolException("INVALID_FILE", "No parameter column to import.", "Columns starting with '_' are ignored; check the columns argument.");

            var ps = ctx.Services.Parameters;
            var target = a.Target == "instance" ? ParameterTarget.Instance : a.Target == "type" ? ParameterTarget.Type : ParameterTarget.Auto;
            char dec = a.DecimalSeparator == "." ? '.' : ',';

            var counts = new Dictionary<string, int>
            {
                ["changed"] = 0, ["unchanged"] = 0, ["blank"] = 0, ["invalid"] = 0, ["readOnly"] = 0,
                ["elementMissing"] = 0, ["parameterMissing"] = 0
            };
            var problems = new List<object>();
            var requests = new List<ParameterWriteRequest>();
            var requestRows = new List<int>();

            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                var key = keyIndex < row.Count ? row[keyIndex]?.Trim() : null;
                if (string.IsNullOrEmpty(key)) continue;
                Element element;
                try { element = ctx.Services.Lookup.Element(key); }
                catch (ToolException ex)
                {
                    counts["elementMissing"]++;
                    problems.Add(new { row = r + 1, key, status = "element_missing", message = ex.Message });
                    continue;
                }

                foreach (var col in columns)
                {
                    var cell = col.Index < row.Count ? row[col.Index] : string.Empty;
                    ResolvedParameter resolved;
                    try { resolved = ps.Resolve(element, col.Ref, target); }
                    catch (ToolException ex)
                    {
                        if (string.IsNullOrEmpty(cell)) { counts["blank"]++; continue; }
                        counts["parameterMissing"]++;
                        problems.Add(new { row = r + 1, key, column = col.Header, status = "parameter_missing", message = ex.Message });
                        continue;
                    }

                    var p = resolved.Parameter;
                    var current = ExportParametersTableTool.Cell(ps, p, dec) ?? string.Empty;
                    if (string.IsNullOrEmpty(cell))
                    {
                        if (!a.EmptyCellsClear || current.Length == 0) { counts["blank"]++; continue; }
                    }
                    else if (Same(p, current, cell, dec))
                    {
                        counts["unchanged"]++;
                        continue;
                    }
                    if (p.IsReadOnly)
                    {
                        counts["readOnly"]++;
                        problems.Add(new { row = r + 1, key, column = col.Header, status = "read_only", value = cell });
                        continue;
                    }

                    if (!TryValue(p, cell, dec, col.Unit, out var value, out var unit))
                    {
                        counts["invalid"]++;
                        problems.Add(new { row = r + 1, key, column = col.Header, status = "invalid", value = cell,
                            message = $"'{cell}' is not a valid {p.StorageType} value (decimal separator '{dec}')." });
                        continue;
                    }
                    counts["changed"]++;
                    requests.Add(new ParameterWriteRequest
                    {
                        Element = (resolved.On == "type" ? resolved.Owner : element).Id.GetValue().ToString(CultureInfo.InvariantCulture),
                        Parameter = col.Ref,
                        Value = value,
                        Unit = unit
                    });
                    requestRows.Add(r + 1);
                }
            }

            int problemCount = problems.Count;
            if (requests.Count > a.MaxWrites)
                throw new ToolException("TOO_MANY_WRITES", $"{requests.Count} cells would be written; maxWrites is {a.MaxWrites}.", "Split the file or raise maxWrites.");
            if (a.Atomic && problemCount > 0 && !ctx.DryRun)
                throw new ToolException("IMPORT_REFUSED",
                    $"{problemCount} cell(s) can't be imported; nothing was written. First: " +
                    string.Join(" | ", problems.Take(5).Select(p => JsonConvert.SerializeObject(p))),
                    "Run with dryRun:true to see every problem, fix the file, or pass atomic:false.");

            ParameterWriteResult result = null;
            ParameterPlan plan = null;
            if (requests.Count > 0)
            {
                plan = ps.Plan(requests, new ParameterPlanOptions { Target = target, AllowCritical = a.AllowCritical, MaxItems = Math.Max(a.MaxWrites, requests.Count) });
                if (a.Atomic && plan.HasErrors && !ctx.DryRun)
                    throw new ToolException("PARAMETER_WRITE_REFUSED",
                        $"{plan.Failed.Count} of {plan.Items.Count} write(s) are invalid; nothing was written. {plan.FailureSummary()}",
                        "Fix the file or pass atomic:false.");
                result = ps.Apply(plan);
                if (a.Atomic && result.Failed > 0 && !ctx.DryRun)
                    throw new ToolException("PARAMETER_WRITE_FAILED",
                        $"{result.Failed} write(s) did not stick, so everything was rolled back. " +
                        string.Join(" ", result.Items.Where(i => i.Status != "ok" && i.Status != "duplicate").Take(10)
                            .Select(i => $"[row {requestRows[i.Index]}, element {i.ElementId} '{i.Parameter?.Name}'] {i.Status}: {i.Message}")),
                        "Check formulas and value ranges, or pass atomic:false.");
            }

            var failedWrites = result?.Items.Where(i => i.Status != "ok" && i.Status != "duplicate")
                .Select(i => new { row = requestRows[i.Index], elementId = i.ElementId, parameter = i.Parameter?.Name, status = i.Status, message = i.Message })
                .ToList();
            bool wouldFail = a.Atomic && (problemCount > 0 || (result?.Failed ?? 0) > 0);
            return new
            {
                path = full,
                rows = rows.Count - 1,
                columns = columns.Select(c => c.Header),
                cells = counts,
                applied = result?.Applied ?? 0,
                failed = result?.Failed ?? 0,
                wouldFailWhenApplied = ctx.DryRun ? wouldFail : (bool?)null,
                sample = result?.Items.Where(i => i.Status == "ok").Take(a.SampleSize)
                    .Select(i => new { row = requestRows[i.Index], elementId = i.ElementId, parameter = i.Parameter?.Name, before = i.Old, after = i.New, unit = i.Unit }),
                problems = problems.Take(200),
                failedWrites = failedWrites?.Take(200),
                revert = result?.Revert,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                (result?.ChangeSummary ?? "Nothing to write.") + (problemCount > 0 ? $" {problemCount} problem cell(s)." : string.Empty)
            };
        }

        private static int FindKeyColumn(List<string> header, string keyColumn)
        {
            if (!string.IsNullOrWhiteSpace(keyColumn))
            {
                var i = header.FindIndex(h => string.Equals(h?.Trim(), keyColumn.Trim(), StringComparison.OrdinalIgnoreCase));
                if (i < 0) throw new ToolException("INVALID_ARGUMENTS", $"No column named '{keyColumn}'. Columns: {string.Join(", ", header)}.");
                return i;
            }
            var k = header.FindIndex(h => string.Equals(h?.Trim(), "UniqueId", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(h?.Trim(), "ElementId", StringComparison.OrdinalIgnoreCase));
            return k < 0 ? 0 : k;
        }

        private static List<Column> Columns(List<string> header, int keyIndex, List<string> only)
        {
            var list = new List<Column>();
            for (int i = 0; i < header.Count; i++)
            {
                if (i == keyIndex) continue;
                var h = header[i]?.Trim();
                if (string.IsNullOrEmpty(h) || h.StartsWith("_", StringComparison.Ordinal)) continue;
                CsvTable.ParseHeader(h, out var name, out var unit, out var builtIn, out var guid);
                if (only != null && only.Count > 0 &&
                    !only.Any(o => string.Equals(o?.Trim(), name, StringComparison.OrdinalIgnoreCase) || string.Equals(o?.Trim(), h, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var r = guid != null ? new ParameterRef { Guid = guid } : builtIn != null ? new ParameterRef { BuiltIn = builtIn } : ParameterRef.ByName(name);
                list.Add(new Column { Index = i, Header = h, Name = name, Unit = unit, Ref = r });
            }
            return list;
        }

        private static bool Same(Parameter p, string current, string cell, char dec)
        {
            if (p.StorageType == StorageType.Double &&
                CsvTable.TryParseNumber(current, dec, out var a) && CsvTable.TryParseNumber(cell, dec, out var b))
                return Math.Abs(a - b) <= 1e-6 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
            if (p.StorageType == StorageType.String) return string.Equals(current, cell, StringComparison.Ordinal);
            return string.Equals(current.Trim(), cell.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryValue(Parameter p, string cell, char dec, string headerUnit, out object value, out string unit)
        {
            unit = null;
            value = null;
            if (string.IsNullOrEmpty(cell)) return true; // clear
            switch (p.StorageType)
            {
                case StorageType.String:
                    value = cell;
                    return true;
                case StorageType.Double:
                    if (!CsvTable.TryParseNumber(cell, dec, out var d)) return false;
                    value = d;
                    unit = string.IsNullOrWhiteSpace(headerUnit) ? null : headerUnit;
                    return true;
                case StorageType.Integer:
                    var t = cell.Trim();
                    if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) { value = i; return true; }
                    value = t; // yes/no words are handled by the parameter engine
                    return true;
                case StorageType.ElementId:
                    var s = cell.Trim();
                    value = long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? (object)id : s;
                    return true;
                default:
                    return false;
            }
        }
    }
}
