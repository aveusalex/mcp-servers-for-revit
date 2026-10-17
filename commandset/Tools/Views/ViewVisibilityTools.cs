using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Views
{
    // ---- VIS-18 ------------------------------------------------------------------------------------------------------

    public sealed class GetViewVisibilityArgs
    {
        [JsonProperty("view", Required = Required.Always)]
        [Description("View or view template (name or id).")]
        public string View { get; set; }

        [JsonProperty("onlyChanged")]
        [Description("true (default): list only hidden or overridden categories. false: every category that can be hidden.")]
        [DefaultValue(true)]
        public bool OnlyChanged { get; set; } = true;

        [JsonProperty("includeSubcategories")]
        [Description("Also check subcategories (default false).")]
        [DefaultValue(false)]
        public bool IncludeSubcategories { get; set; }

        [JsonProperty("includeOverrides")]
        [Description("Include the override details of categories and filters (default true).")]
        [DefaultValue(true)]
        public bool IncludeOverrides { get; set; } = true;

        [JsonProperty("limit")]
        [Description("Categories page size, 1-1000 (default 200).")]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Categories to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    [McpTool("get_view_visibility", Kind = ToolKind.Read, Toolset = "views", Idempotent = true,
        Description = "Visibility/Graphics state of a view or template: hidden and overridden categories (BuiltInCategory " +
                      "+ local name, paged), applied filters with visibility, enabled flag and overrides, the view " +
                      "template and which V/G parts it controls, discipline, phase, detail level, temporary hide/isolate. " +
                      "Use before set_category_visibility / apply_view_filter. Read-only.")]
    public sealed class GetViewVisibilityTool : McpTool<GetViewVisibilityArgs>
    {
        protected override object Run(ToolContext ctx, GetViewVisibilityArgs args)
        {
            var doc = ctx.Doc;
            var view = ViewSupport.ViewOrTemplate(ctx, args.View);
            var rows = new List<object>();
            foreach (Category c in doc.Settings.Categories)
            {
                AddCategory(view, c, null, rows, args);
                if (!args.IncludeSubcategories) continue;
                foreach (Category sub in c.SubCategories) AddCategory(view, sub, c, rows, args);
            }
            var page = Paging.Page(rows, args.Limit, args.Offset, 1000);

            var filters = new List<object>();
            if (view.AreGraphicsOverridesAllowed())
            {
                foreach (var fid in view.GetFilters())
                {
                    filters.Add(new
                    {
                        filterId = fid.GetValue(),
                        name = doc.GetElement(fid)?.Name,
                        visible = view.GetFilterVisibility(fid),
                        enabled = view.GetIsFilterEnabled(fid),
                        overrides = args.IncludeOverrides ? GraphicsSupport.Describe(doc, view.GetFilterOverrides(fid)) : null
                    });
                }
            }

            var template = view.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(view.ViewTemplateId) as View : null;
            string detail = null;
            try { detail = ViewSupport.DetailLevelName(view.DetailLevel); } catch { /* none */ }
            return new
            {
                view = new { viewId = view.Id.GetValue(), name = view.Name, viewType = view.ViewType.ToString(), isTemplate = view.IsTemplate },
                template = template?.Name,
                templateControls = template == null ? null : new
                {
                    modelCategories = GraphicsSupport.ControllingTemplate(view, BuiltInParameter.VIS_GRAPHICS_MODEL) != null,
                    annotationCategories = GraphicsSupport.ControllingTemplate(view, BuiltInParameter.VIS_GRAPHICS_ANNOTATION) != null,
                    filters = GraphicsSupport.ControllingTemplate(view, BuiltInParameter.VIS_GRAPHICS_FILTERS) != null
                },
                discipline = SafeString(() => view.Discipline.ToString()),
                phase = doc.GetElement(view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId)?.Name,
                phaseFilter = doc.GetElement(view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId() ?? ElementId.InvalidElementId)?.Name,
                detailLevel = detail,
                temporaryHideIsolate = SafeBool(() => view.IsTemporaryHideIsolateActive()),
                categories = page.Items,
                categoriesTotal = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated,
                filters
            };
        }

        private static void AddCategory(View view, Category c, Category parent, List<object> rows, GetViewVisibilityArgs args)
        {
            bool canHide;
            try { canHide = view.CanCategoryBeHidden(c.Id); }
            catch { return; }
            if (!canHide) return;
            var hidden = view.GetCategoryHidden(c.Id);
            OverrideGraphicSettings o = null;
            try { o = view.GetCategoryOverrides(c.Id); } catch { /* not overridable */ }
            var overridden = o != null && GraphicsSupport.IsOverridden(o);
            if (args.OnlyChanged && !hidden && !overridden) return;
            rows.Add(new
            {
                name = parent == null ? c.Name : parent.Name + ": " + c.Name,
                builtIn = CategoryService.BuiltInNameOf(c),
                type = c.CategoryType.ToString(),
                hidden,
                overridden,
                overrides = args.IncludeOverrides && overridden ? GraphicsSupport.Describe(view.Document, o) : null
            });
        }

        private static string SafeString(Func<string> f) { try { return f(); } catch { return null; } }
        private static bool? SafeBool(Func<bool> f) { try { return f(); } catch { return null; } }
    }

    // ---- VIS-19 ------------------------------------------------------------------------------------------------------

    public sealed class SetCategoryVisibilityArgs
    {
        [JsonProperty("views", Required = Required.Always)]
        [Description("Views or view templates (names or ids), up to 200.")]
        [MinLength(1)]
        [MaxLength(200)]
        public List<string> Views { get; set; }

        [JsonProperty("categories", Required = Required.Always)]
        [Description("Categories in any language (OST_Furniture, Furniture, Mobiliário); a subcategory as \"Category: Subcategory\".")]
        [MinLength(1)]
        [MaxLength(200)]
        public List<string> Categories { get; set; }

        [JsonProperty("hidden")]
        [Description("true hides the categories, false shows them. Omit to leave visibility alone.")]
        public bool? Hidden { get; set; }

        [JsonProperty("overrides")]
        [Description("Graphic overrides to apply to the categories (only the given fields change).")]
        public GraphicOverrides Overrides { get; set; }

        [JsonProperty("clearOverrides")]
        [Description("Reset the categories' overrides before applying overrides (default false).")]
        [DefaultValue(false)]
        public bool ClearOverrides { get; set; }

        [JsonProperty("atomic")]
        [Description("true (default): any failure rolls the whole call back. false: failures are reported per view/category.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;
    }

    [McpTool("set_category_visibility", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views",
        Description = "Show/hide categories (and subcategories as \"Category: Sub\") and apply category graphic overrides " +
                      "(line colors/weight, surface/cut patterns and colors, transparency, halftone) in views or view " +
                      "templates. Categories by BuiltInCategory or name in any language. Views whose V/G is controlled " +
                      "by a template are refused with the template name (apply to the template instead). Read back " +
                      "per view. Undo: Ctrl+Z.")]
    public sealed class SetCategoryVisibilityTool : McpTool<SetCategoryVisibilityArgs>
    {
        protected override void Validate(SetCategoryVisibilityArgs args)
        {
            if (args.Hidden == null && args.Overrides == null && !args.ClearOverrides)
                throw new ToolException("INVALID_ARGUMENTS", "Pass hidden, overrides and/or clearOverrides.");
        }

        protected override object Run(ToolContext ctx, SetCategoryVisibilityArgs args)
        {
            var categories = args.Categories.Distinct().Select(n => (text: n, cat: ResolveCategory(ctx, n))).ToList();
            var r = ItemLoop.Run(ctx, args.Views.Distinct().ToList(), !args.Atomic, (reference, index) =>
            {
                var view = ViewSupport.ViewOrTemplate(ctx, reference);
                var rows = new List<object>();
                foreach (var (text, cat) in categories)
                {
                    var bip = cat.CategoryType == CategoryType.Annotation ? BuiltInParameter.VIS_GRAPHICS_ANNOTATION : BuiltInParameter.VIS_GRAPHICS_MODEL;
                    var controller = GraphicsSupport.ControllingTemplate(view, bip);
                    if (controller != null)
                        throw new ToolException("CONTROLLED_BY_TEMPLATE", $"Visibility of '{view.Name}' is controlled by template '{controller}'.",
                            $"Pass the template '{controller}' in views, or remove the template from the view.");
                    var beforeHidden = view.GetCategoryHidden(cat.Id);
                    if (args.Hidden.HasValue)
                    {
                        if (!view.CanCategoryBeHidden(cat.Id))
                            throw new ToolException("CATEGORY_NOT_HIDEABLE", $"'{cat.Name}' cannot be hidden in '{view.Name}'.");
                        view.SetCategoryHidden(cat.Id, args.Hidden.Value);
                        if (view.GetCategoryHidden(cat.Id) != args.Hidden.Value)
                            throw new ToolException("VISIBILITY_NOT_APPLIED", $"Revit kept '{cat.Name}' {(beforeHidden ? "hidden" : "visible")} in '{view.Name}'.");
                    }
                    object overrides = null;
                    if (args.ClearOverrides || args.Overrides != null)
                    {
                        if (!view.IsCategoryOverridable(cat.Id))
                            throw new ToolException("CATEGORY_NOT_OVERRIDABLE", $"'{cat.Name}' cannot be overridden in '{view.Name}'.");
                        var start = args.ClearOverrides ? new OverrideGraphicSettings() : view.GetCategoryOverrides(cat.Id);
                        view.SetCategoryOverrides(cat.Id, GraphicsSupport.Build(ctx, args.Overrides, start));
                        overrides = GraphicsSupport.Describe(ctx.Doc, view.GetCategoryOverrides(cat.Id));
                    }
                    rows.Add(new { category = cat.Name, builtIn = CategoryService.BuiltInNameOf(cat), hiddenBefore = beforeHidden, hidden = view.GetCategoryHidden(cat.Id), overrides });
                }
                return new { index, viewId = view.Id.GetValue(), name = view.Name, isTemplate = view.IsTemplate, categories = rows };
            }, v => v, "View");
            return new { views = r.Done, failed = r.Failed, dryRun = ctx.DryRun };
        }

        internal static Category ResolveCategory(ToolContext ctx, string text)
        {
            try
            {
                var resolved = ctx.Services.Categories.Resolve(text);
                return resolved.Category ?? throw new ToolException("CATEGORY_NOT_FOUND", $"Category '{text}' does not exist in this document.");
            }
            catch (ToolException) when (text.Contains(":"))
            {
                var i = text.IndexOf(':');
                var parent = ctx.Services.Categories.Resolve(text.Substring(0, i).Trim()).Category
                             ?? throw new ToolException("CATEGORY_NOT_FOUND", $"Category '{text.Substring(0, i)}' does not exist in this document.");
                var subs = parent.SubCategories.Cast<Category>().ToList();
                return NameMatcher.Single(text.Substring(i + 1).Trim(), subs.Select(s => new NamedCandidate<Category>(s.Name, s)), $"subcategory of {parent.Name}");
            }
        }
    }

    // ---- VIS-17 ------------------------------------------------------------------------------------------------------

    public sealed class ApplyViewFilterArgs
    {
        [JsonProperty("targets", Required = Required.Always)]
        [Description("Views or view templates (names or ids), up to 200.")]
        [MinLength(1)]
        [MaxLength(200)]
        public List<string> Targets { get; set; }

        [JsonProperty("filter", Required = Required.Always)]
        [Description("View filter (rule-based or selection filter) name or id.")]
        public string Filter { get; set; }

        [JsonProperty("action")]
        [Description("add (default; updates if already applied), update (must already be applied) or remove.")]
        [EnumValues("add", "update", "remove")]
        [DefaultValue("add")]
        public string Action { get; set; } = "add";

        [JsonProperty("visible")]
        [Description("Whether elements passing the filter are visible.")]
        public bool? Visible { get; set; }

        [JsonProperty("enabled")]
        [Description("Whether the filter is enabled in the view.")]
        public bool? Enabled { get; set; }

        [JsonProperty("overrides")]
        [Description("Graphic overrides for the filter (only the given fields change).")]
        public GraphicOverrides Overrides { get; set; }

        [JsonProperty("atomic")]
        [Description("true (default): any target that fails rolls the whole call back. false: failures are reported per target.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;
    }

    [McpTool("apply_view_filter", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views",
        Description = "Add, update or remove an existing view filter on views or view templates, with visibility, " +
                      "enabled flag and graphic overrides (line colors/weight, surface/cut patterns and colors, " +
                      "transparency, halftone; \"solid\" = solid fill). Views whose filters are controlled by a template " +
                      "are refused with the template name (target the template). State is read back. Undo: Ctrl+Z.")]
    public sealed class ApplyViewFilterTool : McpTool<ApplyViewFilterArgs>
    {
        protected override void Validate(ApplyViewFilterArgs args)
        {
            args.Action = (args.Action ?? "add").ToLowerInvariant();
            if (args.Action == "remove" && (args.Visible.HasValue || args.Enabled.HasValue || args.Overrides != null))
                throw new ToolException("INVALID_ARGUMENTS", "remove takes no visible/enabled/overrides.");
        }

        protected override object Run(ToolContext ctx, ApplyViewFilterArgs args)
        {
            var doc = ctx.Doc;
            var filters = new FilteredElementCollector(doc).OfClass(typeof(FilterElement)).Cast<FilterElement>().ToList();
            var filter = ctx.Services.Lookup.ByNameOrId(args.Filter, filters, "view filter");
            var r = ItemLoop.Run(ctx, args.Targets.Distinct().ToList(), !args.Atomic, (reference, index) =>
            {
                var view = ViewSupport.ViewOrTemplate(ctx, reference);
                var controller = GraphicsSupport.ControllingTemplate(view, BuiltInParameter.VIS_GRAPHICS_FILTERS);
                if (controller != null)
                    throw new ToolException("CONTROLLED_BY_TEMPLATE", $"Filters of '{view.Name}' are controlled by template '{controller}'.",
                        $"Target the template '{controller}' instead, or remove the template from the view.");
                if (!view.AreGraphicsOverridesAllowed())
                    throw new ToolException("FILTERS_NOT_SUPPORTED", $"'{view.Name}' ({view.ViewType}) does not take filters.");
                var applied = view.IsFilterApplied(filter.Id);
                if (args.Action == "remove")
                {
                    if (!applied) throw new ToolException("FILTER_NOT_APPLIED", $"Filter '{filter.Name}' is not applied to '{view.Name}'.");
                    view.RemoveFilter(filter.Id);
                    if (view.IsFilterApplied(filter.Id))
                        throw new ToolException("FILTER_NOT_REMOVED", $"Revit kept filter '{filter.Name}' on '{view.Name}'.");
                    return new { index, viewId = view.Id.GetValue(), name = view.Name, action = "removed" };
                }
                if (!applied)
                {
                    if (args.Action == "update")
                        throw new ToolException("FILTER_NOT_APPLIED", $"Filter '{filter.Name}' is not applied to '{view.Name}'.", "Use action add.");
                    try
                    {
                        view.AddFilter(filter.Id);
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                    {
                        throw new ToolException("FILTER_NOT_APPLICABLE", $"Filter '{filter.Name}' cannot be applied to '{view.Name}': {ex.Message}");
                    }
                }
                if (args.Visible.HasValue) view.SetFilterVisibility(filter.Id, args.Visible.Value);
                if (args.Enabled.HasValue) view.SetIsFilterEnabled(filter.Id, args.Enabled.Value);
                if (args.Overrides != null)
                    view.SetFilterOverrides(filter.Id, GraphicsSupport.Build(ctx, args.Overrides, view.GetFilterOverrides(filter.Id)));

                if (!view.IsFilterApplied(filter.Id) ||
                    (args.Visible.HasValue && view.GetFilterVisibility(filter.Id) != args.Visible.Value) ||
                    (args.Enabled.HasValue && view.GetIsFilterEnabled(filter.Id) != args.Enabled.Value))
                    throw new ToolException("FILTER_NOT_APPLIED", $"Revit did not apply filter '{filter.Name}' as requested on '{view.Name}'.");
                return new
                {
                    index,
                    viewId = view.Id.GetValue(),
                    name = view.Name,
                    action = applied ? "updated" : "added",
                    visible = view.GetFilterVisibility(filter.Id),
                    enabled = view.GetIsFilterEnabled(filter.Id),
                    overrides = GraphicsSupport.Describe(doc, view.GetFilterOverrides(filter.Id))
                };
            }, v => v, "Target");
            return new { filter = filter.Name, filterId = filter.Id.GetValue(), targets = r.Done, failed = r.Failed, dryRun = ctx.DryRun };
        }
    }
}
