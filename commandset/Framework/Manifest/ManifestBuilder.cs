using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// Builds the tool manifest (Contract D) from the <c>[McpTool]</c> classes:
    /// <c>{ version: 1, capabilityHash, tools: [{ name, description, kind, risk, toolset, batchable, idempotent,
    /// timeoutSeconds, inputSchema }] }</c>, tools sorted by name (ordinal), kind/risk in lowercase.
    /// <para><c>capabilityHash</c> = lowercase hex SHA-256 of the UTF-8 bytes of the canonical JSON (object keys sorted
    /// ordinally at every level, no whitespace) of <c>[{ "inputSchema": ..., "name": ... }, ...]</c> sorted by name.</para>
    /// </summary>
    /// <remarks>
    /// No Revit dependency and no generic-type references to <c>McpTool&lt;T&gt;</c> (matched by name), so the build-time
    /// generator and the tests can link this file and produce exactly what the plugin sends at runtime.
    /// </remarks>
    public static class ManifestBuilder
    {
        public const int Version = 1;

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

        public static string DescriptionOf(Type toolType, McpToolAttribute metadata)
        {
            if (!string.IsNullOrWhiteSpace(metadata.Description)) return metadata.Description.Trim();
            return toolType.GetCustomAttribute<DescriptionAttribute>()?.Description?.Trim();
        }

        public static string Lower(ToolKind kind) => kind.ToString().ToLowerInvariant();
        public static string Lower(Risk risk) => risk.ToString().ToLowerInvariant();

        public static JObject BuildTool(Type toolType)
        {
            var metadata = ReadMetadata(toolType);
            var argsType = FindArgsType(toolType);
            return new JObject
            {
                ["name"] = metadata.Name,
                ["description"] = DescriptionOf(toolType, metadata) ?? string.Empty,
                ["kind"] = Lower(metadata.Kind),
                ["risk"] = Lower(metadata.Risk),
                ["toolset"] = metadata.Toolset ?? "core",
                ["batchable"] = metadata.Batchable,
                ["idempotent"] = metadata.Idempotent,
                ["timeoutSeconds"] = metadata.TimeoutSeconds,
                ["inputSchema"] = ToolSchemaGenerator.Generate(argsType)
            };
        }

        public static JObject Build(IEnumerable<Type> toolTypes)
        {
            var tools = toolTypes
                .Select(BuildTool)
                .OrderBy(t => (string)t["name"], StringComparer.Ordinal)
                .ToList();

            return new JObject
            {
                ["version"] = Version,
                ["capabilityHash"] = CapabilityHash(tools),
                ["tools"] = new JArray(tools)
            };
        }

        public static string CapabilityHash(IEnumerable<JObject> tools)
        {
            var identity = new JArray(tools
                .OrderBy(t => (string)t["name"], StringComparer.Ordinal)
                .Select(t => new JObject
                {
                    ["name"] = t["name"]?.DeepClone(),
                    ["inputSchema"] = t["inputSchema"]?.DeepClone()
                }));

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Canonicalize(identity)));
                var hex = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        /// <summary>Compact JSON with object keys sorted ordinally at every level.</summary>
        public static string Canonicalize(JToken token)
        {
            return Sort(token).ToString(Formatting.None);
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

                if (metadata != null && string.IsNullOrWhiteSpace(DescriptionOf(type, metadata)))
                {
                    problems.Add($"{type.FullName}: tool '{name}' has no description.");
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
                problems.AddRange(ToolSchemaGenerator.FindContractViolations(argsType, name));
            }
            return problems;
        }

        private static JToken Sort(JToken token)
        {
            switch (token)
            {
                case JObject obj:
                    var sorted = new JObject();
                    foreach (var property in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        sorted.Add(property.Name, Sort(property.Value));
                    }
                    return sorted;
                case JArray array:
                    return new JArray(array.Select(Sort));
                default:
                    return token?.DeepClone() ?? JValue.CreateNull();
            }
        }
    }
}
