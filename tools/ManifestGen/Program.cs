using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RevitMCP.Manifest
{
    /// <summary>
    /// ManifestGen --assembly path\RevitMCPCommandSet.dll [--assembly ...] [--ref dirOrDll ...] [--no-auto-refs]
    ///             [--out server\src\framework\tools.manifest.json] [--check] [--strict]
    ///
    /// --check   compares with the existing --out file and exits 1 if it is stale (nothing is written).
    /// --strict  turns generator warnings (missing descriptions, measures without [Unit], ...) into failures.
    /// Without --out the manifest goes to stdout.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] argv)
        {
            try
            {
                return Run(argv);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ManifestGen: " + ex.Message);
                return 2;
            }
        }

        private static int Run(string[] argv)
        {
            var assemblies = new List<string>();
            var refs = new List<string>();
            string output = null;
            bool check = false, strict = false, autoRefs = true;

            for (var i = 0; i < argv.Length; i++)
            {
                switch (argv[i])
                {
                    case "--assembly": assemblies.Add(Path.GetFullPath(argv[++i])); break;
                    case "--ref": refs.Add(Path.GetFullPath(argv[++i])); break;
                    case "--out": output = Path.GetFullPath(argv[++i]); break;
                    case "--check": check = true; break;
                    case "--strict": strict = true; break;
                    case "--no-auto-refs": autoRefs = false; break;
                    case "-h":
                    case "--help":
                        Console.WriteLine("ManifestGen --assembly <dll> [--ref <dir|dll>] [--no-auto-refs] [--out <file>] [--check] [--strict]");
                        return 0;
                    default: throw new ArgumentException($"Unknown argument '{argv[i]}'.");
                }
            }

            if (assemblies.Count == 0) throw new ArgumentException("--assembly is required.");
            foreach (var a in assemblies)
            {
                if (!File.Exists(a)) throw new FileNotFoundException($"Assembly not found: {a}");
            }
            if (check && output == null) throw new ArgumentException("--check needs --out.");

            var paths = ResolverPaths(assemblies, refs, autoRefs);
            var warnings = new List<string>();
            string json;

            using (var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Private.CoreLib"))
            {
                var types = new List<Type>();
                foreach (var path in assemblies)
                {
                    var asm = mlc.LoadFromAssemblyPath(path);
                    types.AddRange(LoadableTypes(asm, warnings));
                }

                var manifest = ToolManifestBuilder.Build(types, w => warnings.Add(w));
                json = ToolManifestBuilder.Serialize(manifest);
                Console.Error.WriteLine($"ManifestGen: {((Newtonsoft.Json.Linq.JArray)manifest["tools"]).Count} tool(s), capabilityHash {manifest["capabilityHash"]}");
            }

            foreach (var w in warnings) Console.Error.WriteLine("warning: " + w);
            if (strict && warnings.Count > 0)
            {
                Console.Error.WriteLine($"ManifestGen: {warnings.Count} warning(s) with --strict.");
                return 1;
            }

            if (check)
            {
                var current = File.Exists(output) ? File.ReadAllText(output).Replace("\r\n", "\n") : null;
                if (current != json)
                {
                    Console.Error.WriteLine($"ManifestGen: {output} is stale. Run `npm run manifest` in server/ and commit the result.");
                    return 1;
                }
                Console.Error.WriteLine("ManifestGen: manifest is up to date.");
                return 0;
            }

            if (output == null)
            {
                Console.Out.Write(json);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.WriteAllText(output, json);
                Console.Error.WriteLine("ManifestGen: wrote " + output);
            }
            return 0;
        }

        private static IEnumerable<Type> LoadableTypes(Assembly asm, List<string> warnings)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (var le in ex.LoaderExceptions.Where(e => e != null).Select(e => e.Message).Distinct().Take(5))
                    warnings.Add($"{asm.GetName().Name}: some types could not be loaded ({le}).");
                return ex.Types.Where(t => t != null);
            }
        }

        /// <summary>
        /// Everything the metadata resolver may need: the target assemblies' folders (Newtonsoft, RevitMCPSDK when
        /// copied), explicit --ref entries, the Revit API reference packages in the NuGet cache (they are
        /// compile-only, so not in bin/), and the running .NET shared frameworks (core + WindowsDesktop for WPF).
        /// </summary>
        private static List<string> ResolverPaths(List<string> assemblies, List<string> refs, bool autoRefs)
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Add(string dll, bool overwrite)
            {
                var key = Path.GetFileName(dll);
                if (overwrite || !byName.ContainsKey(key)) byName[key] = dll;
            }

            void AddDir(string dir, bool recursive, bool overwrite)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var dll in Directory.EnumerateFiles(dir, "*.dll", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                    Add(dll, overwrite);
            }

            // Lowest priority first; later sources overwrite.
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            AddDir(runtimeDir, false, true);
            var desktop = WindowsDesktopDir(runtimeDir);
            if (desktop != null) AddDir(desktop, false, true);

            if (autoRefs)
            {
                foreach (var dir in RevitReferencePackageDirs()) AddDir(dir, true, true);
            }

            foreach (var r in refs)
            {
                if (File.Exists(r)) Add(r, true);
                else AddDir(r, true, true);
            }

            foreach (var a in assemblies)
            {
                AddDir(Path.GetDirectoryName(a), false, false);
                Add(a, true);
            }

            // Last resort for Newtonsoft (library outputs do not copy package references): our own copy.
            Add(Path.Combine(AppContext.BaseDirectory, "Newtonsoft.Json.dll"), false);

            return byName.Values.ToList();
        }

        private static string WindowsDesktopDir(string coreRuntimeDir)
        {
            // ...\dotnet\shared\Microsoft.NETCore.App\8.0.x\ -> ...\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.x\
            var core = new DirectoryInfo(coreRuntimeDir.TrimEnd(Path.DirectorySeparatorChar));
            var shared = core.Parent?.Parent;
            var desktopRoot = shared == null ? null : Path.Combine(shared.FullName, "Microsoft.WindowsDesktop.App");
            if (desktopRoot == null || !Directory.Exists(desktopRoot)) return null;
            var exact = Path.Combine(desktopRoot, core.Name);
            if (Directory.Exists(exact)) return exact;
            return Directory.GetDirectories(desktopRoot).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
        }

        private static IEnumerable<string> RevitReferencePackageDirs()
        {
            var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            string[] packages =
            {
                "nice3point.revit.api.revitapi", "nice3point.revit.api.revitapiui", "revitmcpsdk",
                "nice3point.revit.toolkit", "nice3point.revit.extensions"
            };
            foreach (var p in packages)
            {
                var dir = Path.Combine(root, p);
                if (!Directory.Exists(dir)) continue;
                var latest = Directory.GetDirectories(dir)
                    .OrderByDescending(d => ParseVersion(Path.GetFileName(d)))
                    .FirstOrDefault();
                if (latest != null) yield return latest;
            }
        }

        private static Version ParseVersion(string s)
        {
            var core = s.Split('-')[0];
            return Version.TryParse(core, out var v) ? v : new Version(0, 0);
        }
    }
}
