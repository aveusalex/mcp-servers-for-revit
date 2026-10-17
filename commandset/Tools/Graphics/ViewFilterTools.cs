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
    // ---- set_view_filter (MAT-02) -------------------------------------------------------------------------------

    public sealed class SetViewFilterArgs
    {
        [JsonProperty("view")]
        [Description("View (name or id). Give view or template.")]
        public string View { get; set; }

        [JsonProperty("template")]
        [Description("View template (name or id), to change every view that uses it. Give view or template.")]
        public string Template { get; set; }

        [JsonProperty("filter", Required = Required.Always)]
        [Description("Filter (rule-based or selection) by name or id.")]
        public string Filter { get; set; }

        [JsonProperty("operation")]
        [Description("add (default; updates if already applied), update (must be applied) or remove (from the view; the filter definition is kept).")]
        [EnumValues("add", "update", "remove")]
        [DefaultValue("add")]
        public string Operation { get; set; } = "add";

        [JsonProperty("visible")]
        [Description("Show (true) or hide (false) the elements that pass the filter.")]
        public bool? Visible { get; set; }

        [JsonProperty("enabled")]
        [Description("Enable or disable the filter in the view without removing it.")]
        public bool? Enabled { get; set; }

        [JsonProperty("overrides")]
        [Description("Graphic overrides for the filtered elements.")]
        public OverrideSpec Overrides { get; set; }

        [JsonProperty("merge")]
        [Description("true (default): keep the filter's current overrides and change only what is given. false: replace them.")]
        [DefaultValue(true)]
        public bool Merge { get; set; } = true;
    }

    /// <summary>MAT-02.</summary>
    [McpTool("set_view_filter", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "graphics",
        Description = "Apply, update or remove a view filter on a view or a view template, with visibility, " +
                      "enabled state and graphic overrides (line colour/weight/pattern, surface/cut patterns, " +
                      "transparency, halftone). Overrides are merged with the current ones by default. A view whose " +
                      "template controls filters fails with VIEW_CONTROLLED_BY_TEMPLATE: target the template. Returns " +
                      "the overrides read back. Undo: Ctrl+Z.")]
    public sealed class SetViewFilterTool : McpTool<SetViewFilterArgs>
    {
        protected override void Validate(SetViewFilterArgs args)
        {
            args.Operation = (args.Operation ?? "add").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(args.View) == string.IsNullOrWhiteSpace(args.Template))
                throw new ToolException("INVALID_ARGUMENTS", "Give view or template (exactly one).");
            if (args.Operation == "remove" && (args.Overrides != null || args.Visible.HasValue || args.Enabled.HasValue))
                throw new ToolException("INVALID_ARGUMENTS", "remove takes no overrides, visible or enabled.");
        }

        protected override object Run(ToolContext ctx, SetViewFilterArgs args)
        {
            var view = GraphicsSupport.ViewOrTemplate(ctx, args.View, args.Template);
            var filter = FindFilter(ctx, args.Filter);
            return Apply(ctx, view, filter, args.Operation, args.Visible, args.Enabled, args.Overrides, args.Merge);
        }

        internal static FilterElement FindFilter(ToolContext ctx, string nameOrId)
        {
            var all = new FilteredElementCollector(ctx.Doc).OfClass(typeof(FilterElement)).Cast<FilterElement>().ToList();
            return ctx.Services.Lookup.ByNameOrId(nameOrId, all, "view filter",
                f => f is ParameterFilterElement ? "rule-based" : "selection");
        }

        internal static object Apply(ToolContext ctx, View view, FilterElement filter, string operation, bool? visible, bool? enabled,
            OverrideSpec overrides, bool merge)
        {
            var doc = ctx.Doc;
            GraphicsSupport.EnsureNotControlled(doc, view, BuiltInParameter.VIS_GRAPHICS_FILTERS, "filters");
            var applied = view.IsFilterApplied(filter.Id);
            bool wasApplied = applied;

            switch (operation)
            {
                case "remove":
                    if (!applied)
                        throw new ToolException("FILTER_NOT_APPLIED", $"Filter '{filter.Name}' is not applied to '{view.Name}'.");
                    view.RemoveFilter(filter.Id);
                    return new { view = view.Name, viewId = view.Id.GetValue(), isTemplate = view.IsTemplate, filter = filter.Name, filterId = filter.Id.GetValue(), removed = true };
                case "update":
                    if (!applied)
                        throw new ToolException("FILTER_NOT_APPLIED", $"Filter '{filter.Name}' is not applied to '{view.Name}'.",
                            "Use operation add.");
                    break;
                default:
                    if (!applied)
                    {
                        try
                        {
                            view.AddFilter(filter.Id);
                        }
                        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                        {
                            throw new ToolException("FILTER_NOT_APPLICABLE", $"Filter '{filter.Name}' cannot be applied to '{view.Name}': {ex.Message}");
                        }
                        catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                        {
                            throw new ToolException("FILTER_NOT_APPLICABLE", $"Filter '{filter.Name}' cannot be applied to '{view.Name}': {ex.Message}");
                        }
                    }
                    break;
            }

            if (overrides != null && !overrides.IsEmpty)
            {
                var ogs = merge ? view.GetFilterOverrides(filter.Id) : new OverrideGraphicSettings();
                view.SetFilterOverrides(filter.Id, GraphicsSupport.Apply(ctx, ogs, overrides));
            }
            else if (overrides != null && !merge)
            {
                view.SetFilterOverrides(filter.Id, new OverrideGraphicSettings());
            }
            if (visible.HasValue) view.SetFilterVisibility(filter.Id, visible.Value);
            if (enabled.HasValue) view.SetIsFilterEnabled(filter.Id, enabled.Value);

            return new
            {
                view = view.Name,
                viewId = view.Id.GetValue(),
                isTemplate = view.IsTemplate,
                filter = filter.Name,
                filterId = filter.Id.GetValue(),
                added = !wasApplied,
                visible = view.GetFilterVisibility(filter.Id),
                enabled = view.GetIsFilterEnabled(filter.Id),
                overrides = GraphicsSupport.Describe(doc, view.GetFilterOverrides(filter.Id))
            };
        }
    }

    // ---- create_view_filter (MAT-01) ----------------------------------------------------------------------------

    public sealed class ViewFilterApplyTo
    {
        [JsonProperty("view")]
        [Description("View (name or id) to apply the filter to. Give view or template.")]
        public string View { get; set; }

        [JsonProperty("template")]
        [Description("View template (name or id) to apply the filter to.")]
        public string Template { get; set; }

        [JsonProperty("visible")]
        [Description("Show (true) or hide (false) the filtered elements.")]
        public bool? Visible { get; set; }

        [JsonProperty("overrides")]
        [Description("Graphic overrides for the filtered elements.")]
        public OverrideSpec Overrides { get; set; }
    }

    public sealed class CreateViewFilterArgs
    {
        [JsonProperty("name", Required = Required.Always)]
        [Description("Filter name, unique among filters.")]
        public string Name { get; set; }

        [JsonProperty("categories", Required = Required.Always)]
        [Description("Categories the filter applies to, in any language (OST_Walls, Walls, Paredes).")]
        public List<string> Categories { get; set; }

        [JsonProperty("rules")]
        [Description("Parameter rules (up to 20). Omit for a category-only filter.")]
        public List<FilterRuleSpec> Rules { get; set; }

        [JsonProperty("logic")]
        [Description("and (default): every rule must match. or: any rule.")]
        [EnumValues("and", "or")]
        [DefaultValue("and")]
        public string Logic { get; set; } = "and";

        [JsonProperty("ifExists")]
        [Description("When a filter with this name exists: error (default), reuse (leave it as is) or update (replace its categories and rules).")]
        [EnumValues("error", "reuse", "update")]
        [DefaultValue("error")]
        public string IfExists { get; set; } = "error";

        [JsonProperty("applyTo")]
        [Description("Optionally apply the filter right away to a view or template, with visibility and overrides.")]
        public ViewFilterApplyTo ApplyTo { get; set; }
    }

    /// <summary>MAT-01.</summary>
    [McpTool("create_view_filter", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "graphics",
        Description = "Create a rule-based view filter: categories in any language and parameter rules (builtIn/guid/" +
                      "name; eq, neq, gt/gte/lt/lte with numbers in mm/m2/m3/deg, contains, begins_with, ends_with and " +
                      "negations, has_value/has_no_value) combined with and/or. The parameter must be common to all " +
                      "the categories. ifExists error/reuse/update. Optionally applies it to a view or template with " +
                      "visibility and overrides. Undo: Ctrl+Z or delete the filter.")]
    public sealed class CreateViewFilterTool : McpTool<CreateViewFilterArgs>
    {
        protected override void Validate(CreateViewFilterArgs args)
        {
            NameRules.Validate(args.Name, "filter name");
            if (args.Categories == null || args.Categories.Count(c => !string.IsNullOrWhiteSpace(c)) == 0)
                throw new ToolException("INVALID_ARGUMENTS", "categories must list at least one category.");
            if (args.Rules != null && args.Rules.Count > 20)
                throw new ToolException("INVALID_ARGUMENTS", "At most 20 rules.");
            FilterRules.ValidateSpecs(args.Rules);
            args.Logic = (args.Logic ?? "and").Trim().ToLowerInvariant();
            args.IfExists = (args.IfExists ?? "error").Trim().ToLowerInvariant();
            if (args.ApplyTo != null && string.IsNullOrWhiteSpace(args.ApplyTo.View) == string.IsNullOrWhiteSpace(args.ApplyTo.Template))
                throw new ToolException("INVALID_ARGUMENTS", "applyTo needs view or template (exactly one).");
        }

        protected override object Run(ToolContext ctx, CreateViewFilterArgs args)
        {
            var doc = ctx.Doc;
            var categories = ctx.Services.Categories.ResolveAll(args.Categories);
            var filterable = new HashSet<ElementId>(ParameterFilterUtilities.GetAllFilterableCategories());
            var bad = categories.Where(c => !filterable.Contains(c.Id)).Select(c => c.Name).ToList();
            if (bad.Count > 0)
                throw new ToolException("CATEGORY_NOT_FILTERABLE", "These categories cannot be used in view filters: " + string.Join(", ", bad) + ".");
            var catIds = categories.Select(c => c.Id).Distinct().ToList();

            var existing = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>()
                .FirstOrDefault(f => string.Equals(f.Name, args.Name, StringComparison.OrdinalIgnoreCase));
            if (existing == null && new FilteredElementCollector(doc).OfClass(typeof(FilterElement))
                    .Any(f => string.Equals(f.Name, args.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException("NAME_COLLISION", $"A selection filter is already named '{args.Name}'.");

            var elementFilter = FilterRules.Build(ctx, catIds, args.Rules, args.Logic == "or");
            if (elementFilter != null && !ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement(doc, new HashSet<ElementId>(catIds), elementFilter))
                throw new ToolException("FILTER_NOT_ACCEPTABLE", "Revit does not accept these rules for a view filter on these categories.",
                    "Check that every parameter applies to all the categories.");

            ParameterFilterElement pfe;
            string status;
            if (existing != null)
            {
                switch (args.IfExists)
                {
                    case "reuse":
                        pfe = existing;
                        status = "reused";
                        break;
                    case "update":
                        pfe = existing;
                        pfe.SetCategories(catIds);
                        if (elementFilter != null) pfe.SetElementFilter(elementFilter);
                        else pfe.ClearRules();
                        status = "updated";
                        break;
                    default:
                        throw new ToolException("NAME_COLLISION", $"A view filter named '{existing.Name}' already exists.",
                            "Pass ifExists reuse or update, or another name.");
                }
            }
            else
            {
                pfe = elementFilter == null
                    ? ParameterFilterElement.Create(doc, args.Name, catIds)
                    : ParameterFilterElement.Create(doc, args.Name, catIds, elementFilter);
                status = "created";
            }

            object applied = null;
            if (args.ApplyTo != null)
            {
                var view = GraphicsSupport.ViewOrTemplate(ctx, args.ApplyTo.View, args.ApplyTo.Template);
                applied = SetViewFilterTool.Apply(ctx, view, pfe, "add", args.ApplyTo.Visible, null, args.ApplyTo.Overrides, true);
            }

            return new
            {
                status,
                filterId = pfe.Id.GetValue(),
                name = pfe.Name,
                categories = pfe.GetCategories().Select(id => SafeCategoryName(doc, id)).ToList(),
                ruleParameters = pfe.GetElementFilterParameters().Select(id => FilterRules.ParameterLabel(doc, id)).Distinct().ToList(),
                logic = args.Logic,
                applied
            };
        }

        private static string SafeCategoryName(Document doc, ElementId id)
        {
            try { return Category.GetCategory(doc, id)?.Name ?? id.GetValue().ToString(); }
            catch { return id.GetValue().ToString(); }
        }
    }
}
