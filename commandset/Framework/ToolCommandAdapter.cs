using System;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// Exposes a framework tool as a RevitMCPSDK command, so the plugin's registry and executor handle it like any
    /// legacy command. <see cref="Execute"/> runs on the plugin's socket thread: it queues the call on the
    /// <see cref="ToolDispatcher"/> and returns the result envelope (always as the result, also when ok=false).
    /// </summary>
    public sealed class ToolCommandAdapter : IRevitCommand
    {
        public ToolCommandAdapter(string toolName)
        {
            CommandName = toolName;
        }

        public string CommandName { get; }

        public object Execute(JObject parameters, string requestId)
        {
            try
            {
                var dispatcher = ToolDispatcher.Instance;
                if (dispatcher == null)
                {
                    return EnvelopeJson.ToJson(ToolErrors.Fail(ToolErrorCodes.RevitBusy,
                        "The tool dispatcher isn't ready yet.", "Open or click into a project in Revit and retry."));
                }
                return dispatcher.Submit(CommandName, parameters, requestId);
            }
            catch (Exception ex)
            {
                return EnvelopeJson.ToJson(ToolErrors.Fail(ex));
            }
        }
    }
}
