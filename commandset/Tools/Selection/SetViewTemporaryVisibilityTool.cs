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
    public sealed class SetViewTemporaryVisibilityArgs
    {
        [JsonProperty("operation", Required = Required.Always)]
        [Description("isolate_temp / hide_temp (temporary hide/isolate, not saved with the view), reset_temp (end temporary hide/isolate), hide / unhide (permanent, in this view only), show_attached_groups / hide_attached_groups (attached detail groups of model groups).")]
        [EnumValues("isolate_temp", "hide_temp", "reset_temp", "hide", "unhide", "show_attached_groups", "hide_attached_groups")]
        public string Operation { get; set; }

        [JsonProperty("elementIds")]
        [Description("Element ids (strings: id or UniqueId). For *_attached_groups: the model group ids.")]
        public List<string> ElementIds { get; set; }

        [JsonProperty("categories")]
        [Description("Categories (any language) instead of / besides elements: isolate_temp/hide_temp/hide/unhide act on the whole category.")]
        public List<string> Categories { get; set; }

        [JsonProperty("view")]
        [Description("View name or id. Default: the active view (target must be the active document).")]
        public string View { get; set; }
    }

    /// <summary>SEL-06: temporary / permanent hide and isolate in a view.</summary>
    [McpTool("set_view_temporary_visibility", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "selection",
        Description = "Control what a view shows, by elements or categories: isolate_temp / hide_temp (Temporary " +
                      "Hide/Isolate, cleared with reset_temp or by closing the view: handy to show the user what a " +
                      "plan will touch), hide / unhide (permanent, only in that view; undo with unhide or Ctrl+Z), " +
                      "show_attached_groups / hide_attached_groups for model groups. Category changes are refused when a " +
                      "view template controls visibility. Temporary operations are skipped in dryRun (only validated). " +
                      "Default view: the active one.")]
    public sealed class SetViewTemporaryVisibilityTool : McpTool<SetViewTemporaryVisibilityArgs>
    {
        protected override void Validate(SetViewTemporaryVisibilityArgs args)
        {
            args.Operation = (args.Operation ?? "").Trim().ToLowerInvariant();
            var hasIds = (args.ElementIds?.Count ?? 0) > 0;
            var hasCats = (args.Categories?.Count ?? 0) > 0;
            if (args.Operation != "reset_temp" && !hasIds && !hasCats)
                throw new ToolException("INVALID_ARGUMENTS", $"{args.Operation} needs elementIds or categories.");
            if (args.Operation.EndsWith("_attached_groups") && !hasIds)
                throw new ToolException("INVALID_ARGUMENTS", "show/hide_attached_groups needs the model group ids in elementIds.");
            if ((args.ElementIds?.Count ?? 0) > 5000)
                throw new ToolException("INVALID_ARGUMENTS", "elementIds accepts at most 5000 entries.");
        }

        protected override object Run(ToolContext ctx, SetViewTemporaryVisibilityArgs args)
        {
            var doc = ctx.Doc;
            var view = string.IsNullOrWhiteSpace(args.View)
                ? SelectionSupport.ActiveViewOrNull(ctx) ?? throw new ToolException("INVALID_ARGUMENTS",
                    "The target document is not the active one, so give the view explicitly.", null, "list_views")
                : ctx.Services.Lookup.View(args.View);
            if (view.IsTemplate)
                throw new ToolException("INVALID_ARGUMENTS", "View templates have no visibility of their own to change.");

            var notFound = new List<object>();
            var ids = SelectionSupport.ResolveIds(ctx, args.ElementIds, notFound);
            var cats = ctx.Services.Categories.ResolveAll(args.Categories);
            var catIds = cats.Select(c => c.Id).ToList();
            var temporary = args.Operation.EndsWith("_temp");

            if ((args.Operation == "hide" || args.Operation == "unhide") && catIds.Count > 0 && TemplateControlsVisibility(doc, view))
                throw new ToolException("VIEW_CONTROLLED_BY_TEMPLATE",
                    $"The view template of '{view.Name}' controls category visibility, so categories can't be hidden here.",
                    "Hide elements instead, or change the view template.");

            var skipped = new List<long>();
            if (args.Operation == "hide" || args.Operation == "hide_temp" || args.Operation == "isolate_temp")
            {
                var ok = new List<ElementId>();
                foreach (var id in ids)
                {
                    var e = doc.GetElement(id);
                    if (args.Operation == "isolate_temp" || (e != null && e.CanBeHidden(view))) ok.Add(id);
                    else skipped.Add(id.GetValue());
                }
                ids = ok;
            }

            object applied;
            if (temporary && ctx.DryRun)
            {
                applied = new { skipped = true, reason = "Temporary view modes can't be rolled back, so dryRun only validated the call." };
            }
            else
            {
                switch (args.Operation)
                {
                    case "isolate_temp":
                        if (ids.Count > 0) view.IsolateElementsTemporary(ids);
                        if (catIds.Count > 0) view.IsolateCategoriesTemporary(catIds);
                        break;
                    case "hide_temp":
                        if (ids.Count > 0) view.HideElementsTemporary(ids);
                        if (catIds.Count > 0) view.HideCategoriesTemporary(catIds);
                        break;
                    case "reset_temp":
                        view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                        break;
                    case "hide":
                        if (ids.Count > 0) view.HideElements(ids);
                        foreach (var c in catIds) SetCategoryHidden(view, c, true, skipped);
                        break;
                    case "unhide":
                        if (ids.Count > 0) view.UnhideElements(ids);
                        foreach (var c in catIds) SetCategoryHidden(view, c, false, skipped);
                        break;
                    case "show_attached_groups":
                    case "hide_attached_groups":
                        foreach (var id in ids)
                        {
                            if (!(doc.GetElement(id) is Group g) ||
                                g.Category?.Id.GetValue() != (long)BuiltInCategory.OST_IOSModelGroups)
                            {
                                skipped.Add(id.GetValue());
                                continue;
                            }
                            try
                            {
                                if (args.Operation == "show_attached_groups") g.ShowAllAttachedDetailGroups(view);
                                else g.HideAllAttachedDetailGroups(view);
                            }
                            catch (Autodesk.Revit.Exceptions.ApplicationException)
                            {
                                skipped.Add(id.GetValue());
                            }
                        }
                        break;
                }
                applied = true;
            }

            if (skipped.Count > 0)
                ctx.Warnings.Add(new ToolWarning($"{skipped.Count} element(s)/categories could not be changed in this view (not hideable, not a model group, or locked).", skipped.Take(50).ToList()));

            bool? tempActive = null;
            try { tempActive = view.IsTemporaryHideIsolateActive(); } catch { /* n/a */ }
            return new
            {
                operation = args.Operation,
                view = SelectionSupport.ViewRef(view),
                applied,
                elementCount = ids.Count,
                categories = cats.Select(c => c.BuiltInName ?? c.Name).ToList(),
                temporaryHideIsolateActive = tempActive,
                skipped,
                notFound
            };
        }

        private static bool TemplateControlsVisibility(Document doc, View view)
        {
            if (view.ViewTemplateId == null || view.ViewTemplateId == ElementId.InvalidElementId) return false;
            if (!(doc.GetElement(view.ViewTemplateId) is View template)) return false;
            try
            {
                var nonControlled = template.GetNonControlledTemplateParameterIds();
                var vgModel = new ElementId(BuiltInParameter.VIS_GRAPHICS_MODEL);
                return !nonControlled.Contains(vgModel);
            }
            catch
            {
                return true;
            }
        }

        private static void SetCategoryHidden(View view, ElementId categoryId, bool hidden, List<long> skipped)
        {
            try
            {
                if (view.CanCategoryBeHidden(categoryId)) view.SetCategoryHidden(categoryId, hidden);
                else skipped.Add(categoryId.GetValue());
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                skipped.Add(categoryId.GetValue());
            }
        }
    }
}
