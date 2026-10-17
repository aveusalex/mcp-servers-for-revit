using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Export.Support
{
    /// <summary>A file an export really produced (read from disk afterwards).</summary>
    public sealed class ExportedFile
    {
        public string path { get; set; }
        public string fileName { get; set; }
        public long bytes { get; set; }
    }

    /// <summary>Output folders, file names and "what did the exporter really write" for the export tools.</summary>
    public static class ExportSupport
    {
        /// <summary>
        /// The output folder: <paramref name="folder"/> (absolute, or relative to RevitMCP\exports) or, when omitted,
        /// RevitMCP\exports\&lt;document&gt;\&lt;timestamp&gt;. Always checked by <see cref="PathPolicy"/> and created.
        /// </summary>
        public static string OutputFolder(Document doc, string folder)
        {
            string full = string.IsNullOrWhiteSpace(folder)
                ? PathPolicy.Resolve(Path.Combine(SafeFileName(doc.Title, "document"),
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)), "exports")
                : PathPolicy.Resolve(folder, "exports");
            try
            {
                Directory.CreateDirectory(full);
            }
            catch (Exception ex)
            {
                throw new ToolException("OUTPUT_FOLDER_FAILED", $"Could not create the output folder '{full}': {ex.Message}");
            }
            return full;
        }

        private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

        /// <summary>A file name without characters Windows rejects ("/ \ : * ? \" &lt; &gt; |").</summary>
        public static string SafeFileName(string name, string fallback = "export")
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            var sb = new StringBuilder(name.Length);
            foreach (var c in name.Trim())
                sb.Append(Invalid.Contains(c) || char.IsControl(c) ? '_' : c);
            var s = sb.ToString().Trim(' ', '.');
            if (s.Length > 150) s = s.Substring(0, 150);
            return s.Length == 0 ? fallback : s;
        }

        /// <summary>Replaces {SheetNumber}, {SheetName}, {ViewName}, {ViewType}, {Id}, {Project}, {Date} in a pattern.</summary>
        public static string ApplyPattern(string pattern, Document doc, View view)
        {
            var sheet = view as ViewSheet;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SheetNumber"] = sheet?.SheetNumber ?? string.Empty,
                ["SheetName"] = sheet?.Name ?? string.Empty,
                ["ViewName"] = view?.Name ?? string.Empty,
                ["ViewType"] = view?.ViewType.ToString() ?? string.Empty,
                ["Id"] = view?.Id.GetValue().ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["Project"] = doc.Title ?? string.Empty,
                ["Date"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };
            var sb = new StringBuilder();
            int i = 0;
            while (i < pattern.Length)
            {
                if (pattern[i] == '{')
                {
                    int end = pattern.IndexOf('}', i + 1);
                    if (end > i)
                    {
                        var key = pattern.Substring(i + 1, end - i - 1);
                        if (map.TryGetValue(key, out var v))
                        {
                            sb.Append(v);
                            i = end + 1;
                            continue;
                        }
                        throw new ToolException("INVALID_ARGUMENTS", $"Unknown placeholder '{{{key}}}' in the file name pattern.",
                            "Use {SheetNumber}, {SheetName}, {ViewName}, {ViewType}, {Id}, {Project} or {Date}.");
                    }
                }
                sb.Append(pattern[i]);
                i++;
            }
            return SafeFileName(sb.ToString());
        }

        /// <summary>Files under <paramref name="folder"/> with their last write time, to diff before/after an export.</summary>
        public static Dictionary<string, DateTime> Snapshot(string folder)
        {
            var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(folder)) return result;
            foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { result[f] = File.GetLastWriteTimeUtc(f); }
                catch { /* locked or vanished */ }
            }
            return result;
        }

        /// <summary>Files created or rewritten since <paramref name="before"/>.</summary>
        public static List<ExportedFile> NewFiles(string folder, Dictionary<string, DateTime> before)
        {
            var after = Snapshot(folder);
            return after
                .Where(kv => !before.TryGetValue(kv.Key, out var t) || kv.Value > t)
                .Select(kv => Describe(kv.Key))
                .Where(f => f != null)
                .OrderBy(f => f.path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static ExportedFile Describe(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                return new ExportedFile { path = info.FullName, fileName = info.Name, bytes = info.Length };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Resolves views/sheets by id or name. Throws VIEW_NOT_FOUND for unknown ones; templates are rejected.
        /// </summary>
        public static List<View> ResolveViews(ToolContext ctx, IEnumerable<string> refs)
        {
            var result = new List<View>();
            foreach (var r in refs ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(r)) continue;
                var v = ctx.Services.Lookup.View(r);
                if (!result.Any(x => x.Id == v.Id)) result.Add(v);
            }
            return result;
        }
    }
}
