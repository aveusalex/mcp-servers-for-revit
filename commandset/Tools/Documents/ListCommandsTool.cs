using System;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;

namespace RevitMCPCommandSet.Tools.Documents
{
    public sealed class ListCommandsArgs
    {
        [JsonProperty("contains")]
        [Description("Only tools whose name or description contains this text (case and accents ignored).")]
        public string Contains { get; set; }

        [JsonProperty("kind")]
        [Description("Only tools of this kind: read, write or ui.")]
        [EnumValues("read", "write", "ui")]
        public string Kind { get; set; }

        [JsonProperty("toolset")]
        [Description("Only tools of this toolset (core, query, parameters, sheets, export, qa, documents, ...).")]
        public string Toolset { get; set; }

        [JsonProperty("includeDescriptions")]
        [Description("Include each tool's full description (default false: first sentence only).")]
        [DefaultValue(false)]
        public bool IncludeDescriptions { get; set; }
    }

    /// <summary>INF-09: what the installed add-in can do, from its own registry (the real version, not the server's).</summary>
    [McpTool("list_commands", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "core", Idempotent = true,
        TimeoutSeconds = 20,
        Description = "List the tools the installed Revit add-in provides, with kind (read/write/ui), risk, toolset, " +
                      "whether it changes the model, whether it needs the user's approval (risk medium or high), " +
                      "batchable, idempotent and timeout. Filter by text, kind or toolset. Works with no document open.")]
    [NoDocumentRequired]
    public sealed class ListCommandsTool : McpTool<ListCommandsArgs>
    {
        protected override object Run(ToolContext ctx, ListCommandsArgs a)
        {
            var key = string.IsNullOrWhiteSpace(a.Contains) ? null : TextMatch.Fold(a.Contains);
            var tools = ToolRegistry.All
                .Where(d => a.Kind == null || ManifestBuilder.Lower(d.Metadata.Kind) == a.Kind.ToLowerInvariant())
                .Where(d => string.IsNullOrWhiteSpace(a.Toolset) || string.Equals(d.Metadata.Toolset, a.Toolset.Trim(), StringComparison.OrdinalIgnoreCase))
                .Where(d => key == null || TextMatch.Fold(d.Name).Contains(key) || TextMatch.Fold(d.Metadata.Description ?? string.Empty).Contains(key))
                .Select(d => new
                {
                    name = d.Name,
                    kind = ManifestBuilder.Lower(d.Metadata.Kind),
                    risk = ManifestBuilder.Lower(d.Metadata.Risk),
                    toolset = d.Metadata.Toolset,
                    mutating = d.Metadata.Kind == ToolKind.Write,
                    requiresApproval = d.Metadata.Risk >= Risk.Medium,
                    needsDocument = !d.NoDocumentRequired,
                    batchable = d.Metadata.Batchable,
                    idempotent = d.Metadata.Idempotent,
                    timeoutSeconds = d.Metadata.TimeoutSeconds,
                    description = a.IncludeDescriptions ? d.Metadata.Description : FirstSentence(d.Metadata.Description)
                })
                .ToList();

            return new
            {
                capabilityHash = ToolRegistry.CapabilityHash,
                total = ToolRegistry.All.Count(),
                count = tools.Count,
                toolsets = ToolRegistry.All.Select(d => d.Metadata.Toolset).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList(),
                tools,
                note = "Tools served only by the MCP server (e.g. get_office_knowledge, plans) and legacy commands are not listed here."
            };
        }

        private static string FirstSentence(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var i = s.IndexOf(". ", StringComparison.Ordinal);
            return i > 0 ? s.Substring(0, i + 1) : s;
        }
    }
}
