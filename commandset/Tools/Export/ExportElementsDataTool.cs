using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Export.Support;
using RevitMCPCommandSet.Tools.Query;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Export
{
    public sealed class ExportElementsDataArgs
    {
        [JsonProperty("categories")]
        [Required]
        [MinLength(1)]
        [Description("Categories to export, in any language: OST_Doors, Doors or Portas.")]
        public List<string> Categories { get; set; }

        [JsonProperty("parameters")]
        [Description("Parameters to export, each {guid} | {builtIn} | {name} (or a plain name). Omit to export every instance parameter that has a value (max 200 columns).")]
        public List<ParameterRef> Parameters { get; set; }

        [JsonProperty("includeTypeParameters")]
        [Description("Also look the requested parameters up on the element's type when the instance doesn't have them (default true). With no parameters list, adds the type's parameters as 'type:<name>' columns.")]
        [DefaultValue(true)]
        public bool IncludeTypeParameters { get; set; } = true;

        [JsonProperty("where")]
        [Description("Optional parameter conditions (same as find_elements.where), all must match.")]
        public List<WhereCondition> Where { get; set; }

        [JsonProperty("view")]
        [Description("Only elements visible in this view (name or id).")]
        public string View { get; set; }

        [JsonProperty("format")]
        [Description("csv (default; UTF-8 with BOM, opens in Excel) or json. Use the server's xlsx conversion for Excel files.")]
        [EnumValues("csv", "json")]
        [DefaultValue("csv")]
        public string Format { get; set; } = "csv";

        [JsonProperty("delimiter")]
        [Description("CSV delimiter: ';' (default, Excel pt-BR), ',' or 'tab'.")]
        [EnumValues(";", ",", "tab")]
        [DefaultValue(";")]
        public string Delimiter { get; set; } = ";";

        [JsonProperty("decimalSeparator")]
        [Description("Decimal separator for numbers in CSV: ',' (default, pt-BR) or '.'.")]
        [EnumValues(",", ".")]
        [DefaultValue(",")]
        public string DecimalSeparator { get; set; } = ",";

        [JsonProperty("output")]
        [Description("file (default): write the file and return its path plus the first rows; inline: return all rows (max 500).")]
        [EnumValues("file", "inline")]
        [DefaultValue("file")]
        public string Output { get; set; } = "file";

        [JsonProperty("fileName")]
        [Description("File name without extension (default \"<categories> - <date>\").")]
        public string FileName { get; set; }

        [JsonProperty("folder")]
        [Description("Output folder: absolute inside an allowed root, or relative to Documents\\RevitMCP\\exports. Default: exports\\<document>\\<timestamp>.")]
        public string Folder { get; set; }

        [JsonProperty("maxElements")]
        [Description("Stop after this many elements, 1-100000 (default 20000); the result says truncated.")]
        [System.ComponentModel.DataAnnotations.Range(1, 100000)]
        [DefaultValue(20000)]
        public int MaxElements { get; set; } = 20000;
    }

    /// <summary>EXP-07: elements × parameters to CSV/JSON (the base of "edit in Excel and re-import").</summary>
    [McpTool("export_elements_data", Kind = ToolKind.Read, Risk = Risk.Low, Toolset = "export", TimeoutSeconds = 300,
        Description = "Export the elements of some categories with chosen parameters (or all instance parameters with " +
                      "a value) to CSV (UTF-8 BOM, ';' and decimal comma by default) or JSON. Each row has id and " +
                      "uniqueId (keys for re-import with set_parameters), category, family, type and level; values are " +
                      "in wire units (mm, m2, m3, deg) and headers carry the unit. Optional where[] and view filters. " +
                      "Writes only inside the allowed folders; returns the path, row/column counts and a preview.")]
    public sealed class ExportElementsDataTool : McpTool<ExportElementsDataArgs>
    {
        private const int MaxInline = 500;
        private const int MaxAutoColumns = 200;

        protected override void Validate(ExportElementsDataArgs a)
        {
            foreach (var w in a.Where ?? new List<WhereCondition>())
            {
                if (w?.Parameter == null || w.Parameter.IsEmpty)
                    throw new ToolException("INVALID_ARGUMENTS", "Every where[] condition needs a parameter.");
                w.Op = WhereEvaluator.NormalizeOperator(w.Op);
                WhereEvaluator.ValidateCondition(w.Op, w.Value);
            }
        }

        protected override object Run(ToolContext ctx, ExportElementsDataArgs a)
        {
            var doc = ctx.Doc;
            var ps = ctx.Services.Parameters;
            var cats = ctx.Services.Categories.ResolveAll(a.Categories);
            var collector = string.IsNullOrWhiteSpace(a.View)
                ? new FilteredElementCollector(doc)
                : new FilteredElementCollector(doc, ctx.Services.Lookup.View(a.View).Id);
            var elements = collector.WhereElementIsNotElementType()
                .WherePasses(new ElementMulticategoryFilter(cats.Select(c => c.Id).ToList()))
                .ToElements().OrderBy(e => e.Id.GetValue()).ToList();

            var where = a.Where ?? new List<WhereCondition>();
            if (where.Count > 0)
                elements = elements.Where(e => where.All(w =>
                    WhereEvaluator.Matches(ps.Snapshot(FindAuto(ps, e, w.Parameter, (w.Scope ?? "auto").ToLowerInvariant())), w.Op, w.Value))).ToList();

            int total = elements.Count;
            bool truncated = total > a.MaxElements;
            if (truncated) elements = elements.Take(a.MaxElements).ToList();

            // Columns: requested parameters, or the union of the instance (and type) parameters that have a value.
            var columns = new List<Column>();
            if (a.Parameters != null && a.Parameters.Count > 0)
            {
                foreach (var r in a.Parameters.Where(r => r != null && !r.IsEmpty))
                    columns.Add(new Column { Header = r.ToString(), Ref = r, Scope = a.IncludeTypeParameters ? "auto" : "instance" });
            }
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in elements)
                {
                    AddAuto(e, "instance", columns, seen);
                    if (a.IncludeTypeParameters)
                    {
                        var t = ParameterService.TypeOf(e);
                        if (t != null) AddAuto(t, "type", columns, seen);
                    }
                    if (columns.Count >= MaxAutoColumns) break;
                }
            }

            var rows = new List<Dictionary<string, object>>();
            var units = new Dictionary<string, string>();
            foreach (var e in elements)
            {
                var type = ParameterService.TypeOf(e);
                var row = new Dictionary<string, object>
                {
                    ["id"] = e.Id.GetValue(),
                    ["uniqueId"] = e.UniqueId,
                    ["category"] = e.Category?.Name,
                    ["family"] = type == null ? null : LookupService.SafeFamilyName(type),
                    ["type"] = type?.Name,
                    ["level"] = e.LevelId != null && e.LevelId != ElementId.InvalidElementId ? doc.GetElement(e.LevelId)?.Name : null
                };
                foreach (var c in columns)
                {
                    Parameter p;
                    try
                    {
                        p = c.Scope == "type" ? (type == null ? null : ps.Find(type, c.Ref))
                            : FindAuto(ps, e, c.Ref, c.Scope);
                    }
                    catch (ToolException)
                    {
                        p = null; // ambiguous on this element: leave empty
                    }
                    if (p == null)
                    {
                        row[c.Header] = null;
                        continue;
                    }
                    var v = ps.ReadWire(p, out var unit, out _);
                    if (unit != null && !units.ContainsKey(c.Header)) units[c.Header] = unit;
                    row[c.Header] = v;
                }
                rows.Add(row);
            }

            var headers = new List<string> { "id", "uniqueId", "category", "family", "type", "level" };
            headers.AddRange(columns.Select(c => c.Header));
            Func<string, string> label = h => units.TryGetValue(h, out var u) ? $"{h} ({u})" : h;

            if (string.Equals(a.Output, "inline", StringComparison.OrdinalIgnoreCase))
            {
                if (rows.Count > MaxInline)
                    throw new ToolException("TOO_MANY_ROWS", $"{rows.Count} rows is too many to return inline (max {MaxInline}).",
                        "Use output \"file\", or narrow the categories / where[].");
                return new { total, truncated, columns = headers.Select(label).ToList(), units, rows };
            }

            bool json = string.Equals(a.Format, "json", StringComparison.OrdinalIgnoreCase);
            var folder = ExportSupport.OutputFolder(doc, a.Folder);
            var baseName = string.IsNullOrWhiteSpace(a.FileName)
                ? ExportSupport.SafeFileName(string.Join("+", cats.Select(c => c.Name)) + " - " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture))
                : ExportSupport.SafeFileName(a.FileName);
            var path = Path.Combine(folder, baseName + (json ? ".json" : ".csv"));

            if (json)
            {
                var payload = new JObject
                {
                    ["document"] = doc.Title,
                    ["exportedAt"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                    ["units"] = JObject.FromObject(units),
                    ["rows"] = JArray.FromObject(rows)
                };
                File.WriteAllText(path, payload.ToString(Formatting.Indented), new UTF8Encoding(false));
            }
            else
            {
                var delim = a.Delimiter == "tab" ? "\t" : (a.Delimiter ?? ";");
                var dec = a.DecimalSeparator ?? ",";
                if (dec == delim)
                    throw new ToolException("INVALID_ARGUMENTS", "The decimal separator can't be the same as the delimiter.");
                var sb = new StringBuilder();
                sb.AppendLine(string.Join(delim, headers.Select(h => Csv(label(h), delim))));
                foreach (var r in rows)
                    sb.AppendLine(string.Join(delim, headers.Select(h => Csv(Format(r.TryGetValue(h, out var v) ? v : null, dec), delim))));
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }

            return new
            {
                file = ExportSupport.Describe(path),
                format = json ? "json" : "csv",
                rowCount = rows.Count,
                total,
                truncated,
                columns = headers.Select(label).ToList(),
                preview = rows.Take(5).ToList()
            };
        }

        private sealed class Column
        {
            public string Header;
            public ParameterRef Ref;
            public string Scope;
        }

        private static void AddAuto(Element e, string scope, List<Column> columns, HashSet<string> seen)
        {
            foreach (Parameter p in e.Parameters)
            {
                if (columns.Count >= MaxAutoColumns) return;
                var name = p.Definition?.Name;
                if (string.IsNullOrEmpty(name) || !p.HasValue) continue;
                var header = scope == "type" ? "type:" + name : name;
                if (!seen.Add(header)) continue;
                var id = ParameterService.Identify(p);
                var r = id.Guid != null ? new ParameterRef { Guid = id.Guid }
                    : id.BuiltIn != null ? new ParameterRef { BuiltIn = id.BuiltIn }
                    : ParameterRef.ByName(name);
                columns.Add(new Column { Header = header, Ref = r, Scope = scope });
            }
        }

        private static Parameter FindAuto(ParameterService ps, Element e, ParameterRef r, string scope)
        {
            if (scope != "type")
            {
                var p = ps.Find(e, r);
                if (p != null || scope == "instance") return p;
            }
            var t = ParameterService.TypeOf(e);
            return t == null ? null : ps.Find(t, r);
        }

        private static string Format(object v, string dec)
        {
            switch (v)
            {
                case null: return string.Empty;
                case double d: return d.ToString("0.######", CultureInfo.InvariantCulture).Replace(".", dec);
                case float f: return ((double)f).ToString("0.######", CultureInfo.InvariantCulture).Replace(".", dec);
                case IFormattable fm: return fm.ToString(null, CultureInfo.InvariantCulture);
                default: return v.ToString();
            }
        }

        private static string Csv(string s, string delim)
        {
            if (s == null) return string.Empty;
            if (s.Contains(delim) || s.Contains("\"") || s.Contains("\n") || s.Contains("\r"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
