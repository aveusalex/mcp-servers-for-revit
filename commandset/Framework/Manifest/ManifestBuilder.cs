using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RevitMCP.Manifest;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// Discovery helpers and contract checks for <c>[McpTool]</c> classes. The manifest itself (Contract D) is built by
    /// <see cref="ToolManifestBuilder"/> (tools/ManifestGen/Core, linked into this assembly), so the manifest and the
    /// capabilityHash the plugin sends at runtime are byte-identical to the committed tools.manifest.json.
    /// </summary>
    /// <remarks>
    /// No Revit dependency and no generic-type references to <c>McpTool&lt;T&gt;</c> (matched by name), so the framework
    /// tests can link this file.
    /// </remarks>
    public static class ManifestBuilder
    {
        private static readonly Regex SnakeCase = new Regex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$", RegexOptions.CultureInvariant);

        public static McpToolAttribute ReadMetadata(Type toolType) =>
            (McpToolAttribute)Attribute.GetCustomAttribute(toolType, typeof(McpToolAttribute), false);

        /// <summary>The <c>TArgs</c> of <c>McpTool&lt;TArgs&gt;</c>, or null when the type isn't a tool.</summary>
        public static Type FindArgsType(Type toolType)
        {
            for (var t = toolType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.IsGenericType && t.Name == "McpTool`1" && t.Namespace == typeof(McpToolAttribute).Namespace)
                    return t.GetGenericArguments()[0];
            }
            return null;
        }

        /// <summary>Concrete classes carrying <c>[McpTool]</c> that derive from <c>McpTool&lt;TArgs&gt;</c>.</summary>
        public static IEnumerable<Type> FindToolTypes(IEnumerable<Type> types) =>
            types.Where(t => t != null && t.IsClass && !t.IsAbstract && ReadMetadata(t) != null && FindArgsType(t) != null);

        public static string Lower(ToolKind kind) => kind.ToString().ToLowerInvariant();
        public static string Lower(Risk risk) => risk.ToString().ToLowerInvariant();

        /// <summary>The manifest (Contract D) of <paramref name="toolTypes"/>.</summary>
        public static JObject Build(IEnumerable<Type> toolTypes)
        {
            return ToolManifestBuilder.Build(toolTypes);
        }

        /// <summary>Contract problems across a set of tools: names, descriptions, duplicates and argument POCOs.</summary>
        public static IList<string> Validate(IEnumerable<Type> toolTypes)
        {
            var problems = new List<string>();
            var names = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var type in toolTypes)
            {
                var metadata = ReadMetadata(type);
                var name = metadata?.Name;
                if (string.IsNullOrEmpty(name) || !SnakeCase.IsMatch(name))
                {
                    problems.Add($"{type.FullName}: tool name '{name}' is not snake_case.");
                }
                else if (names.TryGetValue(name, out var other))
                {
                    problems.Add($"{type.FullName}: tool name '{name}' is already used by {other.FullName}.");
                }
                else
                {
                    names[name] = type;
                }

                if (metadata != null && (metadata.Description ?? string.Empty).Trim().Length < 20)
                {
                    problems.Add($"{type.FullName}: tool '{name}' needs a Description (what, when, units, how to undo).");
                }
                if (metadata != null && metadata.Kind == ToolKind.Write && metadata.Risk == Risk.None)
                {
                    problems.Add($"{type.FullName}: write tool '{name}' must set Risk explicitly.");
                }
                if (metadata != null && metadata.TimeoutSeconds <= 0)
                {
                    problems.Add($"{type.FullName}: TimeoutSeconds must be positive.");
                }

                var argsType = FindArgsType(type);
                if (argsType == null)
                {
                    problems.Add($"{type.FullName}: [McpTool] class does not derive from McpTool<TArgs>.");
                    continue;
                }
                problems.AddRange(ArgsContract.FindContractViolations(argsType, name));
            }
            return problems;
        }
    }
}
