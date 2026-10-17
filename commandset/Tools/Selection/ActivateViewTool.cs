using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Selection.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Selection
{
    public sealed class ActivateViewArgs
    {
        [JsonProperty("view", Required = Required.Always)]
        [Description("View, sheet or schedule name or id (list_views shows them).")]
        public string View { get; set; }

        [JsonProperty("viewType")]
        [Description("Revit view type to disambiguate a name shared by several views (FloorPlan, CeilingPlan, Section, ThreeD, DrawingSheet...).")]
        public string ViewType { get; set; }

        [JsonProperty("operation")]
        [Description("activate (default) opens the view and makes it active; close closes it if it is open (never the last open view).")]
        [EnumValues("activate", "close")]
        [DefaultValue("activate")]
        public string Operation { get; set; } = "activate";
    }

    /// <summary>SEL-04: open/activate or close a view in the UI.</summary>
    [McpTool("activate_view", Kind = ToolKind.UI, Risk = Risk.None, Toolset = "selection", Batchable = false,
        Description = "Open a view, sheet or schedule and make it the active view (by name or id; viewType " +
                      "disambiguates names shared by a plan and a ceiling plan), or close an open view. Active " +
                      "document only; view templates can't be opened and the last open view can't be closed. Does not " +
                      "change the model. Returns the active view after the call and the open views.")]
    public sealed class ActivateViewTool : McpTool<ActivateViewArgs>
    {
        protected override void Validate(ActivateViewArgs args)
        {
            args.Operation = (args.Operation ?? "activate").Trim().ToLowerInvariant();
        }

        protected override object Run(ToolContext ctx, ActivateViewArgs args)
        {
            var uiDoc = SelectionSupport.UiDoc(ctx);
            var view = SelectionSupport.View(ctx, args.View, args.ViewType);
            bool pending = false;

            if (args.Operation == "close")
            {
                var open = uiDoc.GetOpenUIViews();
                var uiView = open.FirstOrDefault(v => v.ViewId == view.Id);
                if (uiView == null)
                    throw new ToolException("VIEW_NOT_OPEN", $"The view '{view.Name}' is not open.");
                if (open.Count <= 1)
                    throw new ToolException("LAST_OPEN_VIEW", "Revit needs at least one open view; open another view first.", null, "activate_view");
                if (uiDoc.ActiveView.Id == view.Id)
                {
                    // Revit can't close the active view: switch to another open one first.
                    var other = open.First(v => v.ViewId != view.Id);
                    uiDoc.ActiveView = ctx.Doc.GetElement(other.ViewId) as View;
                }
                uiView.Close();
            }
            else
            {
                if (view.IsTemplate)
                    throw new ToolException("INVALID_ARGUMENTS", $"'{view.Name}' is a view template and can't be opened.");
                if (uiDoc.ActiveView?.Id != view.Id)
                {
                    try
                    {
                        uiDoc.ActiveView = view;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        // Some contexts only allow an asynchronous request.
                        uiDoc.RequestViewChange(view);
                        pending = true;
                    }
                }
            }

            var active = uiDoc.ActiveView;
            if (args.Operation == "activate" && !pending && active?.Id != view.Id)
                throw new ToolException("VIEW_NOT_ACTIVATED", $"Revit did not switch to '{view.Name}'.",
                    "Ask the user to finish any running command in Revit, then retry.");
            return new
            {
                operation = args.Operation,
                view = SelectionSupport.ViewRef(view),
                activeView = SelectionSupport.ViewRef(active),
                pending,
                openViews = uiDoc.GetOpenUIViews().Select(v => SelectionSupport.ViewRef(ctx.Doc.GetElement(v.ViewId) as View)).ToList()
            };
        }
    }
}
