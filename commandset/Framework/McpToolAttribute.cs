using System;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>How the dispatcher runs a tool.</summary>
    public enum ToolKind
    {
        /// <summary>Reads the model; runs without a transaction.</summary>
        Read,
        /// <summary>Changes the model; runs inside a SafeTransaction and supports dryRun.</summary>
        Write,
        /// <summary>Acts on Revit's UI (selection, active view); needs the target to be the active document.</summary>
        UI
    }

    /// <summary>Risk level. The broker asks for human approval from <see cref="Medium"/> up.</summary>
    public enum Risk
    {
        None,
        Low,
        Medium,
        High
    }

    /// <summary>
    /// Marks a class as an MCP tool. The plugin discovers these by reflection and registers them, and the
    /// manifest generator turns them into tools.manifest.json for the MCP server and the broker.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class McpToolAttribute : Attribute
    {
        public McpToolAttribute(string name)
        {
            Name = name;
        }

        /// <summary>snake_case tool name, unique across the command set.</summary>
        public string Name { get; }

        public ToolKind Kind { get; set; } = ToolKind.Read;

        /// <summary>Low for additive edits, Medium for bulk/type/delete, High for irreversible or file writes.</summary>
        public Risk Risk { get; set; } = Risk.None;

        /// <summary>Group used to expose a subset of tools (core, query, parameters, views, sheets, ...).</summary>
        public string Toolset { get; set; } = "core";

        /// <summary>Whether the tool can run as a step of execute_batch.</summary>
        public bool Batchable { get; set; } = true;

        public bool Idempotent { get; set; }

        /// <summary>After this many seconds the broker answers "running" and the client polls get_job_result.</summary>
        public int TimeoutSeconds { get; set; } = 50;

        /// <summary>What the tool does, when to use it, units and how to undo it.</summary>
        public string Description { get; set; }
    }
}
