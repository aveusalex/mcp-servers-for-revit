using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Selection.Support;
using RevitMCPCommandSet.Tools.Sheets.Support;

namespace RevitMCPCommandSet.Tools.Views
{
    public sealed class OpenViewArgs
    {
        [JsonProperty("view", Required = Required.Always)]
        [Description("View, schedule or legend name or id, or a sheet number/name (e.g. \"A-101\").")]
        public string View { get; set; }

        [JsonProperty("viewType")]
        [Description("Revit view type to disambiguate a shared name (FloorPlan, CeilingPlan, Section, ThreeD, DrawingSheet...).")]
        public string ViewType { get; set; }

        [JsonProperty("zoom")]
        [EnumValues("keep", "fit")]
        [DefaultValue("keep")]
        [Description("keep (default) or fit: zoom the opened view to fit its contents.")]
        public string Zoom { get; set; } = "keep";

        [JsonProperty("closeOthers")]
        [DefaultValue(false)]
        [Description("Close every other open view of this document after opening it (tidy the tabs).")]
        public bool CloseOthers { get; set; }
    }

    /// <summary>VIS-24: open a view (or sheet by number) in the UI, optionally zoom to fit and close the other tabs.</summary>
    [McpTool("open_view", Kind = ToolKind.UI, Risk = Risk.None, Toolset = "views", Batchable = false,
        Description = "Open a view, schedule, legend or sheet (by name, id or sheet number) in Revit and make it active; " +
                      "optionally zoom to fit and close the other open views of the document. Active document only; " +
                      "does not change the model. Returns the active view and the open views. (activate_view does the " +
                      "same without sheet numbers/zoom and can close a single view.)")]
    public sealed class OpenViewTool : McpTool<OpenViewArgs>
    {
        protected override object Run(ToolContext ctx, OpenViewArgs a)
        {
            var uiDoc = SelectionSupport.UiDoc(ctx);
            View view;
            try
            {
                view = SelectionSupport.View(ctx, a.View, a.ViewType);
            }
            catch (ToolException ex) when ((ex.Code == "NOT_FOUND" || ex.Code == "VIEW_NOT_FOUND") && string.IsNullOrWhiteSpace(a.ViewType))
            {
                view = SheetSupport.Sheet(ctx, a.View);
            }
            if (view.IsTemplate)
                throw new ToolException("INVALID_ARGUMENTS", $"'{view.Name}' is a view template and can't be opened.", "Open a view that uses it instead.");

            bool pending = false;
            if (uiDoc.ActiveView?.Id != view.Id)
            {
                try { uiDoc.ActiveView = view; }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    uiDoc.RequestViewChange(view);
                    pending = true;
                }
            }
            if (!pending && uiDoc.ActiveView?.Id != view.Id)
                throw new ToolException("VIEW_NOT_ACTIVATED", $"Revit did not switch to '{view.Name}'.",
                    "Ask the user to finish any running command in Revit, then retry.");

            bool zoomed = false;
            var closed = new List<object>();
            if (!pending)
            {
                var open = uiDoc.GetOpenUIViews();
                if (a.Zoom == "fit")
                {
                    var ui = open.FirstOrDefault(v => v.ViewId == view.Id);
                    if (ui != null) { ui.ZoomToFit(); zoomed = true; }
                }
                if (a.CloseOthers)
                    foreach (var ui in open.Where(v => v.ViewId != view.Id).ToList())
                    {
                        var v = ctx.Doc.GetElement(ui.ViewId) as View;
                        try { ui.Close(); closed.Add(SelectionSupport.ViewRef(v)); }
                        catch (Autodesk.Revit.Exceptions.ApplicationException) { /* leave it open */ }
                    }
            }
            else if (a.Zoom == "fit" || a.CloseOthers)
                ctx.Warnings.Add(new ToolWarning("The view change was queued by Revit; zoom/closeOthers were skipped. Call again once it is active.", null, "info"));

            return new
            {
                view = SelectionSupport.ViewRef(view),
                activeView = SelectionSupport.ViewRef(uiDoc.ActiveView),
                pending,
                zoomedToFit = zoomed,
                closed,
                openViews = uiDoc.GetOpenUIViews().Select(v => SelectionSupport.ViewRef(ctx.Doc.GetElement(v.ViewId) as View)).ToList()
            };
        }
    }
}
