using System.Collections.Generic;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>Element ids changed by one call, collected from DocumentChanged.</summary>
    public sealed class ChangeSet
    {
        public List<long> Created { get; } = new List<long>();
        public List<long> Modified { get; } = new List<long>();
        public List<long> Deleted { get; } = new List<long>();

        /// <summary>True when a list hit the cap and was cut.</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>A warning Revit raised (and the preprocessor swallowed) or one the tool reports itself.</summary>
    public sealed class ToolWarning
    {
        public ToolWarning(string message, IList<long> elementIds = null, string severity = "warning")
        {
            Message = message;
            ElementIds = elementIds ?? new List<long>();
            Severity = severity;
        }

        public string Message { get; }
        public IList<long> ElementIds { get; }
        public string Severity { get; }
    }

    public sealed class ToolError
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public string Hint { get; set; }
        public string RecommendedTool { get; set; }
    }

    /// <summary>The envelope every tool call returns to the broker as its result.</summary>
    public sealed class ToolResult
    {
        public bool Ok { get; set; }
        public object Data { get; set; }
        public ChangeSet Changes { get; set; }
        public List<ToolWarning> Warnings { get; set; } = new List<ToolWarning>();
        public bool DryRun { get; set; }
        public ToolError Error { get; set; }
    }
}
