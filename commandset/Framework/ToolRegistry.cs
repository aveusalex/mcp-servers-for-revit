using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>A discovered <c>[McpTool]</c> class.</summary>
    public sealed class ToolDescriptor
    {
        internal ToolDescriptor(Type toolType)
        {
            ToolType = toolType;
            Metadata = ManifestBuilder.ReadMetadata(toolType);
            ArgsType = ManifestBuilder.FindArgsType(toolType);
            NoDocumentRequired = toolType.GetCustomAttribute<NoDocumentRequiredAttribute>() != null;
            SelfTransacted = typeof(ISelfTransactedTool).IsAssignableFrom(toolType);
        }

        public string Name => Metadata.Name;
        public McpToolAttribute Metadata { get; }
        public Type ToolType { get; }
        public Type ArgsType { get; }
        public bool NoDocumentRequired { get; }

        /// <summary>The tool opens its own transactions (execute_batch).</summary>
        public bool SelfTransacted { get; }

        /// <summary>Tools are stateless; a fresh instance per call keeps it that way.</summary>
        public IMcpTool CreateInstance() => (IMcpTool)Activator.CreateInstance(ToolType);
    }

    /// <summary>
    /// Finds the <c>[McpTool]</c> classes of this assembly and exposes them to the plugin. The plugin calls the
    /// static methods by reflection (no compile-time type is shared):
    /// <list type="bullet">
    /// <item><see cref="CreateCommands"/> on Revit's thread, to register one RevitMCPSDK command per tool;</item>
    /// <item><see cref="GetManifestJson"/> from any thread, to send the manifest to the broker on register.</item>
    /// </list>
    /// </summary>
    public static class ToolRegistry
    {
        private static readonly Lazy<IReadOnlyDictionary<string, ToolDescriptor>> Own =
            new Lazy<IReadOnlyDictionary<string, ToolDescriptor>>(
                () => Discover(typeof(ToolRegistry).Assembly).ToDictionary(d => d.Name, StringComparer.Ordinal),
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Lazy<JObject> Manifest = new Lazy<JObject>(
            () => ManifestBuilder.Build(Own.Value.Values.Select(d => d.ToolType)),
            System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Lazy<IList<string>> Problems = new Lazy<IList<string>>(
            () => ManifestBuilder.Validate(ManifestBuilder.FindToolTypes(LoadableTypes(typeof(ToolRegistry).Assembly))),
            System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// The tools of <paramref name="assembly"/>, sorted by name. A duplicate name keeps the first class found;
        /// <see cref="ContractProblems"/> reports it.
        /// </summary>
        public static IReadOnlyList<ToolDescriptor> Discover(Assembly assembly)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<ToolDescriptor>();
            foreach (var type in ManifestBuilder.FindToolTypes(LoadableTypes(assembly)).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (!typeof(IMcpTool).IsAssignableFrom(type)) continue;
                var descriptor = new ToolDescriptor(type);
                if (string.IsNullOrEmpty(descriptor.Name) || !seen.Add(descriptor.Name)) continue;
                result.Add(descriptor);
            }
            return result.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        }

        public static IEnumerable<ToolDescriptor> All => Own.Value.Values.OrderBy(d => d.Name, StringComparer.Ordinal);

        public static bool TryGet(string name, out ToolDescriptor descriptor)
        {
            descriptor = null;
            return name != null && Own.Value.TryGetValue(name, out descriptor);
        }

        /// <summary>
        /// One RevitMCPSDK command per tool, with <c>CommandName</c> = tool name. Must run on Revit's thread in an
        /// API context (it creates the dispatcher's ExternalEvent). Calling it again reuses the same dispatcher.
        /// </summary>
        public static IList<IRevitCommand> CreateCommands(UIApplication uiApp)
        {
            ToolDispatcher.EnsureCreated(uiApp);
            return All.Select(d => (IRevitCommand)new ToolCommandAdapter(d.Name)).ToList();
        }

        /// <summary>The manifest (Contract D) as compact JSON. Safe from any thread.</summary>
        public static string GetManifestJson()
        {
            return Manifest.Value.ToString(Formatting.None);
        }

        public static string CapabilityHash => (string)Manifest.Value["capabilityHash"];

        /// <summary>Contract problems found in this assembly's tools (empty when the contract tests pass).</summary>
        public static IList<string> ContractProblems => Problems.Value;

        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null);
            }
        }
    }
}
