using System.IO;
using System.Reflection;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// On .NET Framework Revits (2022-2024) Revit.exe.config has no binding redirects for Roslyn's dependencies
    /// (System.Memory, System.Runtime.CompilerServices.Unsafe, System.Collections.Immutable, ...), so the first
    /// compile can fail with FileNotFoundException. This resolver answers those requests with the copy shipped next
    /// to the command set. Idea from upstream PR #46, rewritten: it matches the exact simple name (never a prefix
    /// such as "Microsoft.CodeAnalysis*") and only resolves files that exist in our own folder.
    /// On .NET 8+ Revits the add-in load context handles this, so the resolver is a no-op there.
    /// </summary>
    internal static class RoslynDependencyResolver
    {
#if NETFRAMEWORK
        private static int _registered;
#endif

        public static void EnsureRegistered()
        {
#if NETFRAMEWORK
            if (Interlocked.Exchange(ref _registered, 1) == 1) return;
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
#endif
        }

#if NETFRAMEWORK
        private static readonly string OwnDirectory = Path.GetDirectoryName(typeof(RoslynDependencyResolver).Assembly.Location);

        [ThreadStatic] private static bool _resolving;

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            if (_resolving) return null;

            AssemblyName requested;
            try
            {
                requested = new AssemblyName(args.Name);
            }
            catch (Exception)
            {
                return null;
            }

            string simpleName = requested.Name;
            if (string.IsNullOrEmpty(simpleName) || simpleName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                return null;

            string candidate = Path.Combine(OwnDirectory ?? string.Empty, simpleName + ".dll");
            if (string.IsNullOrEmpty(OwnDirectory) || !File.Exists(candidate))
                return null;

            _resolving = true;
            try
            {
                // Prefer an already-loaded copy with the same exact simple name and a version at least as new.
                foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = loaded.GetName();
                    if (string.Equals(name.Name, simpleName, StringComparison.OrdinalIgnoreCase)
                        && (requested.Version == null || name.Version >= requested.Version))
                    {
                        return loaded;
                    }
                }

                var fileName = AssemblyName.GetAssemblyName(candidate);
                if (!string.Equals(fileName.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return null;

                return Assembly.LoadFrom(candidate);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                _resolving = false;
            }
        }
#endif
    }
}
