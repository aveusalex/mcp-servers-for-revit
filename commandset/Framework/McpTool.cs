using System;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>Non-generic view of a tool, used by the registry and the dispatcher.</summary>
    public interface IMcpTool
    {
        McpToolAttribute Metadata { get; }
        Type ArgsType { get; }

        /// <summary>Binds and validates the arguments, then runs the tool. Called on Revit's thread.</summary>
        object Invoke(ToolContext ctx, JObject args);
    }

    /// <summary>
    /// Base class for tools. A tool only describes what it does: the dispatcher owns transactions, warnings,
    /// dryRun, change tracking, batching and the result envelope.
    /// </summary>
    public abstract class McpTool<TArgs> : IMcpTool where TArgs : class, new()
    {
        private McpToolAttribute _metadata;

        public McpToolAttribute Metadata =>
            _metadata ??= (McpToolAttribute)Attribute.GetCustomAttribute(GetType(), typeof(McpToolAttribute))
                ?? throw new InvalidOperationException($"{GetType().Name} has no [McpTool] attribute.");

        public Type ArgsType => typeof(TArgs);

        public object Invoke(ToolContext ctx, JObject args)
        {
            TArgs bound;
            try
            {
                bound = (args ?? new JObject()).ToObject<TArgs>() ?? new TArgs();
            }
            catch (Exception ex)
            {
                throw new ToolException("INVALID_ARGUMENTS", $"Arguments for '{Metadata.Name}' are invalid: {ex.Message}");
            }

            Validate(bound);
            return Run(ctx, bound);
        }

        protected virtual void Validate(TArgs args)
        {
        }

        protected abstract object Run(ToolContext ctx, TArgs args);
    }
}
