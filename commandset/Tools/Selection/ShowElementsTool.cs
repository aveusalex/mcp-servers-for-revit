using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Selection.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Selection
{
    public sealed class ShowElementsArgs
    {
        [JsonProperty("elementIds", Required = Required.Always)]
        [Description("Elements to show, by id or UniqueId (strings), 1-1000.")]
        public List<string> ElementIds { get; set; }

        [JsonProperty("view")]
        [Description("View to show them in (name or id). Default: Revit picks a suitable view (may open one).")]
        public string View { get; set; }

        [JsonProperty("select")]
        [Description("Also select the elements (default true).")]
        [DefaultValue(true)]
        public bool Select { get; set; } = true;

        [JsonProperty("marginPercent")]
        [Description("With view: extra margin around the elements when zooming, 0-500 % of their extent (default 20).")]
        [DefaultValue(20)]
        public int MarginPercent { get; set; } = 20;
    }

    /// <summary>SEL-05: bring elements on screen for the human.</summary>
    [McpTool("show_elements", Kind = ToolKind.UI, Risk = Risk.None, Toolset = "selection", Batchable = false,
        Description = "Show elements to the user in Revit: optionally activates the given view and zooms to the " +
                      "elements there (with a margin), otherwise lets Revit pick a view that shows them (it may open " +
                      "one); selects them by default. Active document only; does not change the model. Elements not " +
                      "visible in the chosen view are reported.")]
    public sealed class ShowElementsTool : McpTool<ShowElementsArgs>
    {
        protected override void Validate(ShowElementsArgs args)
        {
            if (args.ElementIds == null || args.ElementIds.Count == 0 || args.ElementIds.Count > 1000)
                throw new ToolException("INVALID_ARGUMENTS", "elementIds must list 1 to 1000 elements.");
            if (args.MarginPercent < 0 || args.MarginPercent > 500)
                throw new ToolException("INVALID_ARGUMENTS", "marginPercent must be between 0 and 500.");
        }

        protected override object Run(ToolContext ctx, ShowElementsArgs args)
        {
            var uiDoc = SelectionSupport.UiDoc(ctx);
            var doc = ctx.Doc;
            var notFound = new List<object>();
            var ids = SelectionSupport.ResolveIds(ctx, args.ElementIds, notFound);
            if (ids.Count == 0)
                throw new ToolException("ELEMENT_NOT_FOUND", "None of the elements exists in the target document.", null, "find_elements");

            var notVisible = new List<long>();
            string how;
            if (!string.IsNullOrWhiteSpace(args.View))
            {
                var view = ctx.Services.Lookup.View(args.View);
                if (uiDoc.ActiveView?.Id != view.Id) uiDoc.ActiveView = view;
                var visible = new HashSet<long>(new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
                    .ToElementIds().Select(i => i.GetValue()));
                XYZ min = null, max = null;
                foreach (var id in ids)
                {
                    if (!visible.Contains(id.GetValue()))
                    {
                        notVisible.Add(id.GetValue());
                        continue;
                    }
                    var e = doc.GetElement(id);
                    var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
                    if (bb == null) continue;
                    min = min == null ? bb.Min : new XYZ(Math.Min(min.X, bb.Min.X), Math.Min(min.Y, bb.Min.Y), Math.Min(min.Z, bb.Min.Z));
                    max = max == null ? bb.Max : new XYZ(Math.Max(max.X, bb.Max.X), Math.Max(max.Y, bb.Max.Y), Math.Max(max.Z, bb.Max.Z));
                }
                var uiView = uiDoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                if (min != null && uiView != null)
                {
                    var diag = max - min;
                    var pad = Math.Max(diag.GetLength() * args.MarginPercent / 100.0, 1.0);
                    var padV = new XYZ(pad, pad, pad);
                    uiView.ZoomAndCenterRectangle(min - padV, max + padV);
                    how = "zoomInView";
                }
                else
                {
                    how = "viewActivatedOnly";
                }
            }
            else
            {
                uiDoc.ShowElements(ids);
                how = "revitShowElements";
            }

            if (args.Select) uiDoc.Selection.SetElementIds(ids);
            if (notVisible.Count > 0)
                ctx.Warnings.Add(new ToolWarning($"{notVisible.Count} element(s) are not visible in the chosen view.", notVisible.Take(50).ToList()));

            return new
            {
                shown = ids.Count - notVisible.Count,
                method = how,
                selected = args.Select ? uiDoc.Selection.GetElementIds().Count : (int?)null,
                activeView = SelectionSupport.ViewRef(uiDoc.ActiveView),
                notVisible,
                notFound
            };
        }
    }
}
