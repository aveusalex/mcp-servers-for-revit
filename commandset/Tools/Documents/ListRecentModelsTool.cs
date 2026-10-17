using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;

namespace RevitMCPCommandSet.Tools.Documents
{
    public sealed class ListRecentModelsArgs
    {
        [JsonProperty("revitVersion")]
        [Description("Revit version whose recent list to read, e.g. \"2025\". Default: the running Revit.")]
        public string RevitVersion { get; set; }

        [JsonProperty("limit")]
        [Description("Maximum entries, 1-100 (default 20).")]
        [System.ComponentModel.DataAnnotations.Range(1, 100)]
        [DefaultValue(20)]
        public int Limit { get; set; } = 20;
    }

    /// <summary>
    /// INF-15: Revit's most-recently-used file list, read from Revit.ini ([Recent File List]). Works with no document
    /// open. Revit.ini is a fixed per-user location of Revit itself (not a path chosen by the caller), so it is read
    /// directly; nothing is written.
    /// </summary>
    [McpTool("list_recent_models", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "documents", Idempotent = true,
        TimeoutSeconds = 20,
        Description = "List the models Revit opened recently (its MRU list in Revit.ini), most recent first, with path, " +
                      "whether the file still exists, last modified time and size. Works with no document open. Use it " +
                      "to find a model to open. Read-only.")]
    [NoDocumentRequired]
    public sealed class ListRecentModelsTool : McpTool<ListRecentModelsArgs>
    {
        private static readonly Regex Entry = new Regex(@"^\s*File(\d+)\s*=\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        protected override object Run(ToolContext ctx, ListRecentModelsArgs a)
        {
            var version = string.IsNullOrWhiteSpace(a.RevitVersion) ? ctx.UiApp?.Application?.VersionNumber : a.RevitVersion.Trim();
            if (string.IsNullOrWhiteSpace(version) || !Regex.IsMatch(version, @"^20\d\d$"))
                throw new ToolException("INVALID_ARGUMENTS", $"revitVersion must look like \"2025\" (got '{version}').");

            var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Autodesk Revit " + version, "Revit.ini");
            if (!File.Exists(ini))
                throw new ToolException("RECENT_LIST_NOT_FOUND", $"Revit {version} has no Revit.ini for this user ({ini}).",
                    "Check revitVersion, or open a model in that Revit once.");

            string[] lines;
            try
            {
                lines = File.ReadAllLines(ini); // honours the UTF-16 BOM Revit writes
            }
            catch (Exception ex)
            {
                throw new ToolException("RECENT_LIST_UNREADABLE", $"Could not read {ini}: {ex.Message}");
            }

            var entries = new List<(int order, string path)>();
            bool inSection = false;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inSection = line.Equals("[Recent File List]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                var m = Entry.Match(line);
                if (m.Success) entries.Add((int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value.Trim('"')));
            }

            var items = entries.OrderBy(e => e.order).Select(e => e.path)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(a.Limit)
                .Select(p =>
                {
                    FileInfo fi = null;
                    try { fi = new FileInfo(p); }
                    catch { /* malformed entry */ }
                    bool exists = fi != null && fi.Exists;
                    return new
                    {
                        path = p,
                        fileName = fi?.Name,
                        exists,
                        lastModified = exists ? fi.LastWriteTime.ToString("s", CultureInfo.InvariantCulture) : null,
                        bytes = exists ? fi.Length : (long?)null,
                        kind = Kind(p)
                    };
                }).ToList();

            return new { revitVersion = version, source = ini, count = items.Count, items };
        }

        private static string Kind(string p)
        {
            var ext = Path.GetExtension(p ?? string.Empty).ToLowerInvariant();
            switch (ext)
            {
                case ".rvt": return "project";
                case ".rfa": return "family";
                case ".rte": return "projectTemplate";
                case ".rft": return "familyTemplate";
                default: return ext.TrimStart('.');
            }
        }
    }
}
