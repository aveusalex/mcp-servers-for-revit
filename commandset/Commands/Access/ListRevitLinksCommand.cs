using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Access;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Access
{
    /// <summary>
    /// Enumerates the host document's Revit link tree. This command is strictly
    /// read-only: linked documents are never made broker targets and no Revit
    /// transaction is opened.
    /// </summary>
    public class ListRevitLinksCommand : ExternalEventCommandBase
    {
        private ListRevitLinksEventHandler _handler => (ListRevitLinksEventHandler)Handler;

        public override string CommandName => "list_revit_links";

        public ListRevitLinksCommand(UIApplication uiApp)
            : base(new ListRevitLinksEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                _handler.Prepare();
                if (!RaiseAndWaitForCompletion(120000))
                    throw new TimeoutException("Listing Revit links timed out");
                return _handler.ResultInfo;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to list Revit links: {ex.Message}");
            }
        }
    }
}
