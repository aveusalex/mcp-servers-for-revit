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
using RevitMCPCommandSet.Tools.Query;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Parameters
{
    public sealed class ExportParametersTableArgs
    {
        [JsonProperty("categories")]
        [Description("Categories in any language (OST_Doors, Doors, Portas). Alternative to elementIds.")]
        public List<string> Categories { get; set; }

        [JsonProperty("elementIds")]
        [Description("Explicit elements (ids or UniqueIds as strings).")]
        public List<string> ElementIds { get; set; }

        [JsonProperty("view")]
        [Description("Only elements visible in this view (name or id).")]
        public string View { get; set; }

        [JsonProperty("where")]
        [Description("Extra parameter conditions, all must match (same operators as find_elements).")]
        public List<WhereCondition> Where { get; set; }

        [JsonProperty("parameters", Required = Required.Always)]
        [Required]
        [MinLength(1)]
        [MaxLength(100)]
        [Description("Columns: parameters as {guid} | {builtIn} | {name}, read on the instance first, then on its type.")]
        public List<ParameterRef> Parameters { get; set; }

        [JsonProperty("keyField")]
        [Description("First column, used by import_parameters_table to find the element again: uniqueId (default, stable) or elementId.")]
        [EnumValues("uniqueId", "elementId")]
        [DefaultValue("uniqueId")]
        public string KeyField { get; set; } = "uniqueId";

        [JsonProperty("outputPath")]
        [Description("CSV file to write: absolute, or relative to Documents\\RevitMCP\\exports. Must be inside the allowed folders. Default: parameters_<date-time>.csv.")]
        public string OutputPath { get; set; }

        [JsonProperty("decimalSeparator")]
        [Description("Decimal separator of numbers: \",\" (default, Excel pt-BR) or \".\". Fields are always separated by ';'.")]
        [EnumValues(",", ".")]
        [DefaultValue(",")]
        public string DecimalSeparator { get; set; } = ",";

        [JsonProperty("maxRows")]
        [Description("Refuse when more elements match (default 20000, max 100000).")]
        [Range(1, 100000)]
        [DefaultValue(20000)]
        public int MaxRows { get; set; } = 20000;
    }

    /// <summary>PAR-06: element × parameter grid as CSV (UTF-8 BOM, ';'), round-trippable with PAR-07.</summary>
    [McpTool("export_parameters_table", Kind = ToolKind.Read, Risk = Risk.Low, Toolset = "parameters", TimeoutSeconds = 120,
        Description = "Export an element × parameter table to a CSV file that Excel (pt-BR) opens directly: UTF-8 with " +
                      "BOM, ';' between fields, decimal comma by default. First column is the key (UniqueId by default) " +
                      "so import_parameters_table can bring edits back; headers carry the unit (\"Width [mm]\") and the " +
                      "builtIn/guid reference; columns starting with '_' are informative (category, family, type, " +
                      "read-only parameters). Numbers in wire units (mm, m2, m3, deg). Writes only inside the allowed " +
                      "folders (Documents\\RevitMCP by default). Does not change the model.")]
    public sealed class ExportParametersTableTool : McpTool<ExportParametersTableArgs>
    {
        protected override void Validate(ExportParametersTableArgs a)
        {
            if ((a.ElementIds?.Count ?? 0) == 0 && (a.Categories?.Count ?? 0) == 0)
                throw new ToolException("INVALID_ARGUMENTS", "Give categories or elementIds.");
            if (a.Parameters.Any(p => p == null || p.IsEmpty))
                throw new ToolException("INVALID_ARGUMENTS", "parameters[] items need a guid, builtIn or name.");
            ElementScope.ValidateWhere(a.Where);
        }

        protected override object Run(ToolContext ctx, ExportParametersTableArgs a)
        {
            var ps = ctx.Services.Parameters;
            var elements = ElementScope.Collect(ctx, new ElementScopeRequest
            {
                ElementIds = a.ElementIds, Categories = a.Categories, View = a.View, Where = a.Where
            });
            if (elements.Count == 0)
                throw new ToolException("NOTHING_TO_EXPORT", "No element matches the scope.", "Check categories / view / where with find_elements.", "find_elements");
            if (elements.Count > a.MaxRows)
                throw new ToolException("TOO_MANY_ELEMENTS", $"{elements.Count} elements match; maxRows is {a.MaxRows}.", "Narrow the scope or raise maxRows.");

            var path = a.OutputPath;
            if (string.IsNullOrWhiteSpace(path)) path = "parameters_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv";
            if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) path += ".csv";
            var full = PathPolicy.Resolve(path, "exports", forWrite: true);
            char dec = a.DecimalSeparator == "." ? '.' : ',';
            bool byUnique = !string.Equals(a.KeyField, "elementId", StringComparison.OrdinalIgnoreCase);

            // Column headers: the first element that has the parameter gives name and unit.
            var headers = new string[a.Parameters.Count];
            var missingEverywhere = new List<string>();
            for (int c = 0; c < a.Parameters.Count; c++)
            {
                var r = a.Parameters[c];
                Parameter sample = null;
                foreach (var e in elements)
                {
                    sample = ElementScope.FindScoped(ps, e, r, "auto");
                    if (sample != null) break;
                }
                string unit = null, name = r.Name;
                if (sample != null)
                {
                    name = sample.Definition?.Name ?? name;
                    ps.ReadWire(sample, out unit, out _);
                }
                else missingEverywhere.Add(r.ToString());
                headers[c] = CsvTable.Header(name ?? r.ToString(), unit, r.Guid == null ? r.BuiltIn : null, r.Guid);
            }

            var header = new List<string> { byUnique ? "UniqueId" : "ElementId", "_Category", "_Family", "_Type" };
            header.AddRange(headers);
            header.Add("_ReadOnly");

            var rows = new List<IList<string>>(elements.Count);
            foreach (var e in elements)
            {
                var type = ParameterService.TypeOf(e);
                var row = new List<string>
                {
                    byUnique ? e.UniqueId : e.Id.GetValue().ToString(CultureInfo.InvariantCulture),
                    e.Category?.Name,
                    type == null ? null : LookupService.SafeFamilyName(type),
                    e is ElementType ? e.Name : type?.Name
                };
                var readOnly = new List<string>();
                for (int c = 0; c < a.Parameters.Count; c++)
                {
                    var p = ElementScope.FindScoped(ps, e, a.Parameters[c], "auto");
                    if (p == null) { row.Add(null); continue; }
                    if (p.IsReadOnly) readOnly.Add(p.Definition?.Name);
                    row.Add(Cell(ps, p, dec));
                }
                row.Add(string.Join(", ", readOnly));
                rows.Add(row);
            }

            try
            {
                CsvTable.Write(full, header, rows);
            }
            catch (IOException ex)
            {
                throw new ToolException("FILE_WRITE_FAILED", $"Could not write '{full}': {ex.Message}", "Close the file if Excel has it open, or choose another outputPath.");
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ToolException("FILE_WRITE_FAILED", $"Could not write '{full}': {ex.Message}");
            }

            if (missingEverywhere.Count > 0)
                ctx.Warnings.Add(new ToolWarning("No exported element has: " + string.Join(", ", missingEverywhere) + " (empty column)."));

            return new
            {
                path = full,
                rows = rows.Count,
                columns = header,
                keyField = byUnique ? "uniqueId" : "elementId",
                decimalSeparator = dec.ToString(),
                delimiter = ";"
            };
        }

        internal static string Cell(ParameterService ps, Parameter p, char dec)
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.AsString();
                case StorageType.Integer:
                    return p.HasValue ? p.AsInteger().ToString(CultureInfo.InvariantCulture) : null;
                case StorageType.Double:
                    if (!p.HasValue) return null;
                    var v = ps.ReadWire(p, out _, out _);
                    return v is double d ? CsvTable.FormatNumber(d, dec) : Convert.ToString(v, CultureInfo.InvariantCulture);
                case StorageType.ElementId:
                    var w = ps.ReadWire(p, out _, out _);
                    return w == null ? null : Convert.ToString(w, CultureInfo.InvariantCulture);
                default:
                    return null;
            }
        }
    }
}
