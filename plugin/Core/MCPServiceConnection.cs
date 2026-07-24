using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace revit_mcp_plugin.Core
{
    [Transaction(TransactionMode.Manual)]
    public class MCPServiceConnection : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Kill switch. The BrokerClient connects automatically on startup;
                // this button only cuts the connection to the broker (dropping every
                // document in this session at once) or restores it.
                BrokerClient client = BrokerClient.Instance;

                if (client.IsRunning)
                {
                    client.Stop();
                    TaskDialog.Show("revitMCP", "Disconnected from MCP broker (kill switch).");
                }
                else
                {
                    // Initialize() ran on startup and is idempotent; reconnecting is
                    // just restarting the connect/heartbeat loop.
                    client.Start();
                    TaskDialog.Show("revitMCP", "Reconnecting to MCP broker.");
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
