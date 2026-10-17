using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Graphics.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Graphics
{
    // ---- set_graphic_overrides (MAT-04) -------------------------------------------------------------------------

    public sealed class SetGraphicOverridesArgs
    {
        [JsonProperty("view", Required = Required.Always)]
        [Description("View (name or id) where the overrides apply.")]
        public string View { get; set; }

        [JsonProperty("elementIds")]
        [Description("Elements to override (ids, up to 5000). Give elementIds and/or categories.")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("categories")]
        [Description("Categories to override in the view, in any language (OST_Walls, Walls, Paredes).")]
        public List<string> Categories { get; set; }

        [JsonProperty("overrides")]
        [Description("Overrides to set. Only what is given changes (see merge).")]
        public OverrideSpec Overrides { get; set; }

        [JsonProperty("merge")]
        [Description("true (default): keep the current overrides and change only what is given. false: replace them entirely.")]
        [DefaultValue(true)]
        public bool Merge { get; set; } = true;

        [JsonProperty("visible")]
        [Description("Hide (false) or unhide (true) the elements/categories in the view.")]
        public bool? Visible { get; set; }
    }

    /// <summary>MAT-04.</summary>
    [McpTool("set_graphic_overrides", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "graphics",
        Description = "Override graphics of elements and/or categories in one view: projection/cut lines (colour " +
                      "#RRGGBB, weight 1-16, pattern), surface/cut foreground and background patterns (colour, pattern " +
                      "or \"solid\", visible), transparency 0-100, halftone, and hide/unhide. Merges with the existing " +
                      "overrides by default (nothing given is reset). Category overrides fail with " +
                      "VIEW_CONTROLLED_BY_TEMPLATE when the view template controls them. Returns the overrides read " +
                      "back. Undo: Ctrl+Z or clear_graphic_overrides.")]
    public sealed class SetGraphicOverridesTool : McpTool<SetGraphicOverridesArgs>
    {
        protected override void Validate(SetGraphicOverridesArgs args)
        {
            if ((args.ElementIds?.Count ?? 0) == 0 && (args.Categories?.Count ?? 0) == 0)
                throw new ToolException("INVALID_ARGUMENTS", "Give elementIds and/or categories.");
            if ((args.ElementIds?.Count ?? 0) > 5000)
                throw new ToolException("TOO_MANY_ITEMS", "At most 5000 elements per call.");
            if ((args.Overrides == null || args.Overrides.IsEmpty) && !args.Visible.HasValue && args.Merge)
                throw new ToolException("INVALID_ARGUMENTS", "Nothing to change: give overrides and/or visible.",
                    "To remove overrides use clear_graphic_overrides.", "clear_graphic_overrides");
        }

        protected override object Run(ToolContext ctx, SetGraphicOverridesArgs args)
        {
            var doc = ctx.Doc;
            var view = ctx.Services.Lookup.View(args.View);
            GraphicsSupport.EnsureOverridesAllowed(view);

            var elements = (args.ElementIds ?? new List<long>()).Distinct().Select(id => ctx.Services.Lookup.Element(id)).ToList();
            var categories = ctx.Services.Categories.ResolveAll(args.Categories);
            if (categories.Count > 0)
            {
                var model = categories.Any(c => c.Category == null || c.Category.CategoryType != CategoryType.Annotation);
                GraphicsSupport.EnsureNotControlled(doc, view,
                    model ? BuiltInParameter.VIS_GRAPHICS_MODEL : BuiltInParameter.VIS_GRAPHICS_ANNOTATION, "category graphics");
                foreach (var c in categories)
                {
                    if (c.Category == null)
                        throw new ToolException("CATEGORY_NOT_FOUND", $"Category '{c.Name}' does not exist in this document.");
                    if (!view.IsCategoryOverridable(c.Id))
                        throw new ToolException("CATEGORY_NOT_OVERRIDABLE", $"Category '{c.Name}' cannot be overridden in '{view.Name}'.");
                }
            }

            foreach (var e in elements)
            {
                if (args.Overrides != null || !args.Merge)
                {
                    var ogs = args.Merge ? view.GetElementOverrides(e.Id) : new OverrideGraphicSettings();
                    view.SetElementOverrides(e.Id, GraphicsSupport.Apply(ctx, ogs, args.Overrides));
                }
            }
            if (args.Visible.HasValue && elements.Count > 0)
            {
                var ids = elements.Where(e => args.Visible.Value ? e.IsHidden(view) : !e.IsHidden(view) && e.CanBeHidden(view))
                    .Select(e => e.Id).ToList();
                if (ids.Count > 0)
                {
                    if (args.Visible.Value) view.UnhideElements(ids);
                    else view.HideElements(ids);
                }
            }

            var categoryResults = new List<object>();
            foreach (var c in categories)
            {
                if (args.Overrides != null || !args.Merge)
                {
                    var ogs = args.Merge ? view.GetCategoryOverrides(c.Id) : new OverrideGraphicSettings();
                    view.SetCategoryOverrides(c.Id, GraphicsSupport.Apply(ctx, ogs, args.Overrides));
                }
                if (args.Visible.HasValue)
                {
                    if (!view.CanCategoryBeHidden(c.Id))
                        throw new ToolException("CATEGORY_NOT_HIDEABLE", $"Category '{c.Name}' cannot be hidden in '{view.Name}'.");
                    view.SetCategoryHidden(c.Id, !args.Visible.Value);
                }
                categoryResults.Add(new
                {
                    category = c.Name,
                    categoryBuiltIn = c.BuiltInName,
                    hidden = view.GetCategoryHidden(c.Id),
                    overrides = GraphicsSupport.Describe(doc, view.GetCategoryOverrides(c.Id))
                });
            }

            var sample = elements.Take(50).Select(e => new
            {
                elementId = e.Id.GetValue(),
                hidden = e.IsHidden(view),
                overrides = GraphicsSupport.Describe(doc, view.GetElementOverrides(e.Id))
            }).ToList();

            return new
            {
                view = view.Name,
                viewId = view.Id.GetValue(),
                elementCount = elements.Count,
                elements = sample,
                elementsTruncated = elements.Count > sample.Count,
                categories = categoryResults
            };
        }
    }

    // ---- clear_graphic_overrides (MAT-05) -----------------------------------------------------------------------

    public sealed class ClearGraphicOverridesArgs
    {
        [JsonProperty("view", Required = Required.Always)]
        [Description("View (name or id).")]
        public string View { get; set; }

        [JsonProperty("elementIds")]
        [Description("Elements whose overrides are cleared.")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("categories")]
        [Description("Categories whose overrides are cleared, in any language.")]
        public List<string> Categories { get; set; }

        [JsonProperty("all")]
        [Description("Clear every element override in the view (and every category override when allCategories). Default false.")]
        [DefaultValue(false)]
        public bool All { get; set; }

        [JsonProperty("allCategories")]
        [Description("With all: also clear every category override of the view (default false).")]
        [DefaultValue(false)]
        public bool AllCategories { get; set; }

        [JsonProperty("unhide")]
        [Description("Also unhide the given elements/categories (default false).")]
        [DefaultValue(false)]
        public bool Unhide { get; set; }
    }

    /// <summary>MAT-05. Filter overrides are not touched (use set_view_filter).</summary>
    [McpTool("clear_graphic_overrides", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "graphics",
        Description = "Clear graphic overrides in a view: of given elements, of given categories (any language), or " +
                      "of every element in the view (all) and optionally every category (allCategories); optionally " +
                      "unhide them too. View filter overrides are not touched (set_view_filter). Returns how many " +
                      "elements/categories were reset. Undo: Ctrl+Z.")]
    public sealed class ClearGraphicOverridesTool : McpTool<ClearGraphicOverridesArgs>
    {
        protected override void Validate(ClearGraphicOverridesArgs args)
        {
            if (!args.All && (args.ElementIds?.Count ?? 0) == 0 && (args.Categories?.Count ?? 0) == 0)
                throw new ToolException("INVALID_ARGUMENTS", "Give elementIds, categories or all:true.");
            if (args.AllCategories && !args.All)
                throw new ToolException("INVALID_ARGUMENTS", "allCategories needs all:true.");
        }

        protected override object Run(ToolContext ctx, ClearGraphicOverridesArgs args)
        {
            var doc = ctx.Doc;
            var view = ctx.Services.Lookup.View(args.View);
            GraphicsSupport.EnsureOverridesAllowed(view);
            var empty = new OverrideGraphicSettings();

            var elementIds = new List<ElementId>();
            if (args.All)
            {
                foreach (var id in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds())
                    if (GraphicsSupport.Describe(doc, view.GetElementOverrides(id)).Count > 0) elementIds.Add(id);
            }
            foreach (var id in args.ElementIds ?? new List<long>())
                elementIds.Add(ctx.Services.Lookup.Element(id).Id);
            elementIds = elementIds.Distinct().ToList();
            foreach (var id in elementIds) view.SetElementOverrides(id, empty);

            if (args.Unhide && (args.ElementIds?.Count ?? 0) > 0)
            {
                var hidden = args.ElementIds.Select(i => doc.GetElement(i.ToRevitElementId())).Where(e => e != null && e.IsHidden(view)).Select(e => e.Id).ToList();
                if (hidden.Count > 0) view.UnhideElements(hidden);
            }

            var categoryIds = new List<(ElementId id, string name)>();
            foreach (var c in ctx.Services.Categories.ResolveAll(args.Categories)) categoryIds.Add((c.Id, c.Name));
            if (args.AllCategories)
            {
                foreach (Category c in doc.Settings.Categories)
                {
                    if (!view.IsCategoryOverridable(c.Id)) continue;
                    if (GraphicsSupport.Describe(doc, view.GetCategoryOverrides(c.Id)).Count > 0) categoryIds.Add((c.Id, c.Name));
                }
            }
            if (categoryIds.Count > 0)
            {
                GraphicsSupport.EnsureNotControlled(doc, view, BuiltInParameter.VIS_GRAPHICS_MODEL, "category graphics");
                foreach (var (id, name) in categoryIds.Distinct())
                {
                    if (!view.IsCategoryOverridable(id))
                        throw new ToolException("CATEGORY_NOT_OVERRIDABLE", $"Category '{name}' cannot be overridden in '{view.Name}'.");
                    view.SetCategoryOverrides(id, empty);
                    if (args.Unhide && view.CanCategoryBeHidden(id) && view.GetCategoryHidden(id)) view.SetCategoryHidden(id, false);
                }
            }

            return new
            {
                view = view.Name,
                viewId = view.Id.GetValue(),
                elementsCleared = elementIds.Count,
                categoriesCleared = categoryIds.Select(c => c.name).Distinct().ToList()
            };
        }
    }
}
