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

    /// <summary>Lets execute_batch check every step's arguments before any step runs.</summary>
    public interface IMcpToolArgsCheck
    {
        /// <summary>Binds and validates the arguments without running anything. Throws <see cref="ToolException"/>.</summary>
        void CheckArgs(JObject args);
    }

    /// <summary>
    /// Marks a tool that manages its own transactions (execute_batch): the dispatcher runs it outside any
    /// transaction and leaves dryRun handling to it.
    /// </summary>
    public interface ISelfTransactedTool
    {
    }

    /// <summary>
    /// Base class for tools. A tool only describes what it does: the dispatcher owns transactions, warnings,
    /// dryRun, change tracking, batching and the result envelope.
    /// </summary>
    public abstract class McpTool<TArgs> : IMcpTool, IMcpToolArgsCheck where TArgs : class, new()
    {
        private McpToolAttribute _metadata;

        public McpToolAttribute Metadata =>
            _metadata ??= (McpToolAttribute)Attribute.GetCustomAttribute(GetType(), typeof(McpToolAttribute))
                ?? throw new InvalidOperationException($"{GetType().Name} has no [McpTool] attribute.");

        public Type ArgsType => typeof(TArgs);

        public object Invoke(ToolContext ctx, JObject args)
        {
            var bound = Bind(args);
            return Run(ctx, bound);
        }

        public void CheckArgs(JObject args)
        {
            Bind(args);
        }

        protected virtual void Validate(TArgs args)
        {
        }

        protected abstract object Run(ToolContext ctx, TArgs args);

        private TArgs Bind(JObject args)
        {
            TArgs bound;
            try
            {
                bound = ArgsBinder.Bind<TArgs>(args);
            }
            catch (ToolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ToolException(ToolErrorCodes.InvalidArguments, $"Arguments for '{Metadata.Name}' are invalid: {ex.Message}");
            }

            Validate(bound);
            return bound;
        }
    }
}
