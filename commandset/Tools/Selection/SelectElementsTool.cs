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
    public sealed class VisibleInViewSource
    {
        [JsonProperty("view")]
        [Description("View name or id (default: the active view).")]
        public string View { get; set; }

        [JsonProperty("categories")]
        [Description("Only these categories (any language). Default: every selectable element visible in the view.")]
        public List<string> Categories { get; set; }
    }

    public sealed class SelectElementsArgs
    {
        [JsonProperty("mode")]
        [Description("replace (default) the selection, add to it, remove from it, or clear it.")]
        [EnumValues("replace", "add", "remove", "clear")]
        [DefaultValue("replace")]
        public string Mode { get; set; } = "replace";

        [JsonProperty("elementIds")]
        [Description("Elements by id or UniqueId (strings), max 5000.")]
        public List<string> ElementIds { get; set; }

        [JsonProperty("savedSelection")]
        [Description("Use the elements of this saved selection (name or id).")]
        public string SavedSelection { get; set; }

        [JsonProperty("visibleInView")]
        [Description("Use the elements visible in a view, optionally by category.")]
        public VisibleInViewSource VisibleInView { get; set; }

        [JsonProperty("zoom")]
        [Description("Also zoom Revit to the resulting selection (default false).")]
        [DefaultValue(false)]
        public bool Zoom { get; set; }
    }

    /// <summary>SEL-01: set / add / remove / clear the UI selection.</summary>
    [McpTool("select_elements", Kind = ToolKind.UI, Risk = Risk.None, Toolset = "selection", Batchable = false,
        Description = "Change the selection in Revit's UI (active document only): replace, add, remove or clear, with " +
                      "elements given by id/UniqueId, by a saved selection, or as everything visible in a view " +
                      "(optionally by category). Sources can be combined. Optionally zooms to the result. Does not change " +
                      "the model; elements inside links can't be selected this way. Returns the final selection count.")]
    public sealed class SelectElementsTool : McpTool<SelectElementsArgs>
    {
        protected override void Validate(SelectElementsArgs args)
        {
            args.Mode = (args.Mode ?? "replace").Trim().ToLowerInvariant();
            if ((args.ElementIds?.Count ?? 0) > 5000)
                throw new ToolException("INVALID_ARGUMENTS", "elementIds accepts at most 5000 entries.");
            var hasSource = (args.ElementIds?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(args.SavedSelection) || args.VisibleInView != null;
            if (args.Mode != "clear" && !hasSource)
                throw new ToolException("INVALID_ARGUMENTS", "Give elementIds, savedSelection or visibleInView (or use mode=clear).");
        }

        protected override object Run(ToolContext ctx, SelectElementsArgs args)
        {
            var uiDoc = SelectionSupport.UiDoc(ctx);
            var doc = ctx.Doc;
            var notFound = new List<object>();
            var ids = new List<ElementId>();

            if (args.Mode != "clear")
            {
                ids.AddRange(SelectionSupport.ResolveIds(ctx, args.ElementIds, notFound));
                if (!string.IsNullOrWhiteSpace(args.SavedSelection))
                {
                    var saved = SelectionSupport.SavedSelection(ctx, args.SavedSelection);
                    ids.AddRange(saved.GetElementIds().Where(i => doc.GetElement(i) != null));
                }
                if (args.VisibleInView != null)
                {
                    var view = string.IsNullOrWhiteSpace(args.VisibleInView.View)
                        ? uiDoc.ActiveView
                        : ctx.Services.Lookup.View(args.VisibleInView.View);
                    var collector = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
                    var cats = ctx.Services.Categories.ResolveAll(args.VisibleInView.Categories);
                    if (cats.Count > 0) collector = collector.WherePasses(new ElementMulticategoryFilter(cats.Select(c => c.Id).ToList()));
                    ids.AddRange(collector.Where(e => e.Category != null && !(e is View)).Select(e => e.Id));
                }
                ids = ids.GroupBy(i => i.GetValue()).Select(g => g.First()).ToList();
            }

            var current = uiDoc.Selection.GetElementIds().ToList();
            List<ElementId> final;
            switch (args.Mode)
            {
                case "clear":
                    final = new List<ElementId>();
                    break;
                case "add":
                    final = current.Concat(ids).GroupBy(i => i.GetValue()).Select(g => g.First()).ToList();
                    break;
                case "remove":
                    var drop = new HashSet<long>(ids.Select(i => i.GetValue()));
                    final = current.Where(i => !drop.Contains(i.GetValue())).ToList();
                    break;
                default:
                    final = ids;
                    break;
            }

            if (args.Mode == "replace" && final.Count == 0)
                throw new ToolException("NOT_FOUND", "No element matched, so the selection was left unchanged.",
                    "Check the ids, saved selection name or view.", "find_elements");

            uiDoc.Selection.SetElementIds(final);
            if (args.Zoom && final.Count > 0)
            {
                try { uiDoc.ShowElements(final); }
                catch (System.Exception ex) { ctx.Warnings.Add(new ToolWarning("Could not zoom to the selection: " + ex.Message)); }
            }

            var readBack = uiDoc.Selection.GetElementIds();
            return new
            {
                mode = args.Mode,
                requested = ids.Count,
                selectedCount = readBack.Count,
                selectedIds = readBack.Take(200).Select(i => i.GetValue()).ToList(),
                selectedIdsTruncated = readBack.Count > 200,
                activeView = SelectionSupport.ViewRef(uiDoc.ActiveView),
                notFound
            };
        }
    }
}
