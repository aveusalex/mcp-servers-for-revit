using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>
    /// Where tools may read and write files. Default root: %USERPROFILE%\Documents\RevitMCP; more roots via the
    /// REVIT_MCP_ALLOWED_DIRS environment variable (';'-separated). Anything else fails with PATH_NOT_ALLOWED.
    /// </summary>
    public static class PathPolicy
    {
        public const string EnvVar = "REVIT_MCP_ALLOWED_DIRS";

        /// <summary>Default output folder for exports (created on demand).</summary>
        public static string DefaultRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitMCP");

        public static IReadOnlyList<string> AllowedRoots
        {
            get
            {
                var roots = new List<string> { DefaultRoot };
                var extra = Environment.GetEnvironmentVariable(EnvVar);
                if (!string.IsNullOrWhiteSpace(extra))
                {
                    roots.AddRange(extra.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).Where(s => s.Length > 0));
                }
                return roots.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        /// <summary>
        /// Resolves <paramref name="path"/> (absolute, or relative to <see cref="DefaultRoot"/>/<paramref name="subFolder"/>)
        /// and checks it is inside an allowed root. Returns the full path.
        /// </summary>
        public static string Resolve(string path, string subFolder = null, bool forWrite = false)
        {
            string baseDir = string.IsNullOrEmpty(subFolder) ? DefaultRoot : Path.Combine(DefaultRoot, subFolder);
            string full;
            try
            {
                full = string.IsNullOrWhiteSpace(path)
                    ? Normalize(baseDir)
                    : Normalize(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));
                // Callers often write paths relative to the root ("textures/x.png") even when the tool's default
                // folder is a subfolder: when reading, fall back to the root if that is where the file is.
                if (!forWrite && !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && subFolder != null
                    && !File.Exists(full) && !Directory.Exists(full))
                {
                    var fromRoot = Normalize(Path.Combine(DefaultRoot, path));
                    if (File.Exists(fromRoot) || Directory.Exists(fromRoot)) full = fromRoot;
                }
            }
            catch (Exception ex)
            {
                throw new ToolException("INVALID_PATH", $"'{path}' is not a valid path: {ex.Message}");
            }

            if (!IsAllowed(full))
            {
                throw new ToolException("PATH_NOT_ALLOWED",
                    $"'{full}' is outside the folders this integration may use.",
                    $"Use a path under {string.Join(" or ", AllowedRoots)}, or ask the user to add the folder to {EnvVar}.");
            }

            if (forWrite)
            {
                var dir = Path.HasExtension(full) ? Path.GetDirectoryName(full) : full;
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            return full;
        }

        public static bool IsAllowed(string fullPath)
        {
            var p = Normalize(fullPath);
            return AllowedRoots.Any(root =>
                p.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        private static string Normalize(string p) =>
            Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
