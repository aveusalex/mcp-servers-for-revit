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
    internal static class TemplateSupport
    {
        public static List<View> Templates(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();

        public static Dictionary<ElementId, int> Usage(Document doc)
        {
            var usage = new Dictionary<ElementId, int>();
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate))
            {
                var t = v.ViewTemplateId;
                if (t == ElementId.InvalidElementId) continue;
                usage[t] = usage.TryGetValue(t, out var n) ? n + 1 : 1;
            }
            return usage;
        }

        public static string ParameterLabel(Document doc, ElementId id)
        {
            var v = id.GetValue();
            if (v < 0)
            {
                try { return LabelUtils.GetLabelFor((BuiltInParameter)id.GetIntValue()); }
                catch { return ((BuiltInParameter)id.GetIntValue()).ToString(); }
            }
            return doc.GetElement(id)?.Name ?? v.ToString();
        }

        public static string BuiltInName(ElementId id)
        {
            var v = id.GetValue();
            if (v >= 0) return null;
            var bip = (BuiltInParameter)id.GetIntValue();
            return Enum.IsDefined(typeof(BuiltInParameter), bip) ? bip.ToString() : null;
        }

        public static IList<ElementId> Controlled(View template)
        {
            var non = new HashSet<ElementId>(template.GetNonControlledTemplateParameterIds());
            return template.GetTemplateParameterIds().Where(id => !non.Contains(id)).ToList();
        }
    }

    // ---- VIS-13 ------------------------------------------------------------------------------------------------------

    public sealed class ListViewTemplatesArgs
    {
        [JsonProperty("viewType")]
        [Description("Only templates for this view type, e.g. FloorPlan, CeilingPlan, Section, Elevation, ThreeD, DraftingView, Schedule.")]
        public string ViewType { get; set; }

        [JsonProperty("nameContains")]
        [Description("Only templates whose name contains this text (case and accents ignored).")]
        public string NameContains { get; set; }

        [JsonProperty("includeUsage")]
        [Description("Count the views that use each template (default true).")]
        [DefaultValue(true)]
        public bool IncludeUsage { get; set; } = true;

        [JsonProperty("includeControlled")]
        [Description("List the parameters each template controls (default false).")]
        [DefaultValue(false)]
        public bool IncludeControlled { get; set; }

        [JsonProperty("limit")]
        [Description("Page size, 1-500 (default 100).")]
        [DefaultValue(100)]
        public int Limit { get; set; } = 100;

        [JsonProperty("offset")]
        [Description("Templates to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    [McpTool("list_view_templates", Kind = ToolKind.Read, Toolset = "views", Idempotent = true,
        Description = "List view templates with id, name, view type, scale, detail level, how many views use them " +
                      "(includeUsage) and optionally the parameters each one controls (includeControlled). Filter by " +
                      "viewType (FloorPlan, Section, ThreeD...) and nameContains; paged with limit/offset. Read-only.")]
    public sealed class ListViewTemplatesTool : McpTool<ListViewTemplatesArgs>
    {
        protected override object Run(ToolContext ctx, ListViewTemplatesArgs args)
        {
            var doc = ctx.Doc;
            IEnumerable<View> templates = TemplateSupport.Templates(doc);
            if (!string.IsNullOrWhiteSpace(args.ViewType))
            {
                var known = Enum.GetNames(typeof(ViewType));
                if (!known.Any(n => string.Equals(n, args.ViewType.Trim(), StringComparison.OrdinalIgnoreCase)))
                    throw new ToolException("INVALID_ARGUMENTS", $"Unknown viewType '{args.ViewType}'.",
                        "Use a Revit ViewType name: " + string.Join(", ", known.Take(25)));
                templates = templates.Where(t => string.Equals(t.ViewType.ToString(), args.ViewType.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(args.NameContains))
            {
                var key = TextMatch.Fold(args.NameContains);
                templates = templates.Where(t => TextMatch.Fold(t.Name).Contains(key));
            }
            var usage = args.IncludeUsage ? TemplateSupport.Usage(doc) : null;
            var page = Paging.Page(templates.OrderBy(t => t.ViewType.ToString()).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                args.Limit, args.Offset, 500);
            var items = page.Items.Select(t => new
            {
                templateId = t.Id.GetValue(),
                name = t.Name,
                viewType = t.ViewType.ToString(),
                scale = ViewSupport.SafeScale(t),
                usedByViews = usage == null ? (int?)null : usage.TryGetValue(t.Id, out var n) ? n : 0,
                controlledCount = TemplateSupport.Controlled(t).Count,
                controlled = args.IncludeControlled
                    ? TemplateSupport.Controlled(t).Select(id => new { name = TemplateSupport.ParameterLabel(doc, id), builtIn = TemplateSupport.BuiltInName(id) }).ToList()
                    : null
            }).ToList();
            return new { templates = items, total = page.Total, limit = page.Limit, offset = page.Offset, truncated = page.Truncated };
        }
    }

    // ---- VIS-14 ------------------------------------------------------------------------------------------------------

    public sealed class ApplyViewTemplateArgs
    {
        [JsonProperty("views", Required = Required.Always)]
        [Description("Views (names or ids), up to 500.")]
        [MinLength(1)]
        [MaxLength(500)]
        public List<string> Views { get; set; }

        [JsonProperty("template", Required = Required.Always)]
        [Description("View template name or id, or \"none\" to remove the template from the views.")]
        public string Template { get; set; }

        [JsonProperty("mode")]
        [Description("assign (default): the view stays linked to the template. apply_once: copy the template's settings once, without linking.")]
        [EnumValues("assign", "apply_once")]
        [DefaultValue("assign")]
        public string Mode { get; set; } = "assign";

        [JsonProperty("atomic")]
        [Description("true (default): any incompatible view rolls the whole call back. false: incompatible views are reported in failed[].")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;
    }

    [McpTool("apply_view_template", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views",
        Description = "Assign a view template to views (mode assign: stays linked), apply its settings once without " +
                      "linking (mode apply_once), or remove the template (template \"none\"). Compatibility is checked " +
                      "per view (IsValidViewTemplate) and the result read back with the previous template, so the call " +
                      "can be reverted. Atomic by default. Undo: Ctrl+Z, or call again with the returned before values.")]
    public sealed class ApplyViewTemplateTool : McpTool<ApplyViewTemplateArgs>
    {
        protected override void Validate(ApplyViewTemplateArgs args)
        {
            args.Mode = (args.Mode ?? "assign").ToLowerInvariant();
            if (args.Mode == "apply_once" && ViewSupport.IsNone(args.Template))
                throw new ToolException("INVALID_ARGUMENTS", "apply_once needs a template (not \"none\").");
        }

        protected override object Run(ToolContext ctx, ApplyViewTemplateArgs args)
        {
            var doc = ctx.Doc;
            var template = ViewSupport.IsNone(args.Template) ? null : ctx.Services.Lookup.ViewTemplate(args.Template);
            var r = ItemLoop.Run(ctx, args.Views.Distinct().ToList(), !args.Atomic, (reference, index) =>
            {
                var view = ctx.Services.Lookup.View(reference);
                var before = view.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(view.ViewTemplateId)?.Name : null;
                if (template == null)
                {
                    view.ViewTemplateId = ElementId.InvalidElementId;
                    if (view.ViewTemplateId != ElementId.InvalidElementId)
                        throw new ToolException("TEMPLATE_NOT_APPLIED", $"Revit kept the template on '{view.Name}'.");
                }
                else if (args.Mode == "apply_once")
                {
                    if (!view.IsValidViewTemplate(template.Id))
                        throw new ToolException("TEMPLATE_INCOMPATIBLE", $"Template '{template.Name}' ({template.ViewType}) does not fit '{view.Name}' ({view.ViewType}).",
                            "Use list_view_templates with the view's type.", "list_view_templates");
                    view.ApplyViewTemplateParameters(template);
                }
                else ViewSupport.AssignTemplate(view, template);

                var after = view.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(view.ViewTemplateId)?.Name : null;
                return new { index, viewId = view.Id.GetValue(), name = view.Name, before, after, mode = args.Mode };
            }, v => v, "View");
            return new { template = template?.Name, views = r.Done, failed = r.Failed, dryRun = ctx.DryRun };
        }
    }

    // ---- VIS-15 ------------------------------------------------------------------------------------------------------

    public sealed class ManageViewTemplatesArgs
    {
        [JsonProperty("operation", Required = Required.Always)]
        [Description("create_from_view, duplicate, rename, delete or set_controlled.")]
        [EnumValues("create_from_view", "duplicate", "rename", "delete", "set_controlled")]
        public string Operation { get; set; }

        [JsonProperty("template")]
        [Description("Existing template (name or id), for duplicate, rename, delete and set_controlled.")]
        public string Template { get; set; }

        [JsonProperty("sourceView")]
        [Description("View (name or id) whose settings become a new template, for create_from_view.")]
        public string SourceView { get; set; }

        [JsonProperty("newName")]
        [Description("Name of the new template (create_from_view, duplicate) or the new name (rename). Must be free among templates.")]
        public string NewName { get; set; }

        [JsonProperty("controlled")]
        [Description("set_controlled: parameters the template must control (names as in the View Template dialog, or BuiltInParameter names).")]
        public List<string> Controlled { get; set; }

        [JsonProperty("uncontrolled")]
        [Description("set_controlled: parameters the template must stop controlling.")]
        public List<string> Uncontrolled { get; set; }

        [JsonProperty("maxUsedByViews")]
        [Description("delete: refuse when more than this many views use the template (default 0: only unused templates). Views that use it are detached first.")]
        [Range(0, 100000)]
        [DefaultValue(0)]
        public int MaxUsedByViews { get; set; }
    }

    [McpTool("manage_view_templates", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "views",
        Description = "Manage view templates: create_from_view (sourceView + newName), duplicate (template + newName), " +
                      "rename, delete (detaches the views that use it; refused above maxUsedByViews, default 0) and " +
                      "set_controlled (which parameters the template controls, by dialog name or BuiltInParameter). " +
                      "Results are read back (ids, names, controlled list). Needs approval. Undo: Ctrl+Z.")]
    public sealed class ManageViewTemplatesTool : McpTool<ManageViewTemplatesArgs>
    {
        protected override void Validate(ManageViewTemplatesArgs args)
        {
            args.Operation = args.Operation.ToLowerInvariant();
            switch (args.Operation)
            {
                case "create_from_view":
                    if (string.IsNullOrWhiteSpace(args.SourceView) || string.IsNullOrWhiteSpace(args.NewName))
                        throw new ToolException("INVALID_ARGUMENTS", "create_from_view needs sourceView and newName.");
                    break;
                case "duplicate":
                case "rename":
                    if (string.IsNullOrWhiteSpace(args.Template) || string.IsNullOrWhiteSpace(args.NewName))
                        throw new ToolException("INVALID_ARGUMENTS", $"{args.Operation} needs template and newName.");
                    break;
                case "delete":
                    if (string.IsNullOrWhiteSpace(args.Template)) throw new ToolException("INVALID_ARGUMENTS", "delete needs template.");
                    break;
                case "set_controlled":
                    if (string.IsNullOrWhiteSpace(args.Template)) throw new ToolException("INVALID_ARGUMENTS", "set_controlled needs template.");
                    if ((args.Controlled?.Count ?? 0) + (args.Uncontrolled?.Count ?? 0) == 0)
                        throw new ToolException("INVALID_ARGUMENTS", "set_controlled needs controlled and/or uncontrolled.");
                    break;
            }
        }

        protected override object Run(ToolContext ctx, ManageViewTemplatesArgs args)
        {
            var doc = ctx.Doc;
            switch (args.Operation)
            {
                case "create_from_view":
                {
                    var source = ctx.Services.Lookup.View(args.SourceView);
                    ctx.Services.Names.EnsureViewNameFree(args.NewName, source.ViewType, true);
                    var template = source.CreateViewTemplate()
                                   ?? throw new ToolException("TEMPLATE_NOT_CREATED", $"Revit could not create a template from '{source.Name}'.");
                    ctx.Services.Names.ApplyName(template, args.NewName, "template name");
                    return new { operation = args.Operation, template = Describe(doc, template), source = source.Name, dryRun = ctx.DryRun };
                }
                case "duplicate":
                {
                    var template = ctx.Services.Lookup.ViewTemplate(args.Template);
                    ctx.Services.Names.EnsureViewNameFree(args.NewName, template.ViewType, true);
                    if (!template.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
                        throw new ToolException("CANNOT_DUPLICATE", $"Template '{template.Name}' cannot be duplicated.");
                    var copy = doc.GetElement(template.Duplicate(ViewDuplicateOption.Duplicate)) as View
                               ?? throw new ToolException("TEMPLATE_NOT_CREATED", "Revit did not duplicate the template.");
                    ctx.Services.Names.ApplyName(copy, args.NewName, "template name");
                    return new { operation = args.Operation, template = Describe(doc, copy), source = template.Name, dryRun = ctx.DryRun };
                }
                case "rename":
                {
                    var template = ctx.Services.Lookup.ViewTemplate(args.Template);
                    var before = template.Name;
                    ctx.Services.Names.EnsureViewNameFree(args.NewName, template.ViewType, true, template.Id);
                    ctx.Services.Names.ApplyName(template, args.NewName, "template name");
                    return new { operation = args.Operation, before, template = Describe(doc, template), dryRun = ctx.DryRun };
                }
                case "delete":
                {
                    var template = ctx.Services.Lookup.ViewTemplate(args.Template);
                    var users = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .Where(v => !v.IsTemplate && v.ViewTemplateId == template.Id).ToList();
                    if (users.Count > args.MaxUsedByViews)
                        throw new ToolException("TEMPLATE_IN_USE", $"Template '{template.Name}' is used by {users.Count} view(s); maxUsedByViews is {args.MaxUsedByViews}.",
                            $"Pass maxUsedByViews >= {users.Count} to detach them and delete it anyway.");
                    foreach (var v in users) v.ViewTemplateId = ElementId.InvalidElementId;
                    var name = template.Name;
                    var id = template.Id;
                    doc.Delete(id);
                    if (doc.GetElement(id) != null)
                        throw new ToolException("TEMPLATE_NOT_DELETED", $"Revit kept template '{name}'.");
                    return new
                    {
                        operation = args.Operation,
                        deleted = new { templateId = id.GetValue(), name },
                        detachedViews = users.Select(v => new { viewId = v.Id.GetValue(), name = v.Name }).ToList(),
                        dryRun = ctx.DryRun
                    };
                }
                default:
                {
                    var template = ctx.Services.Lookup.ViewTemplate(args.Template);
                    var all = template.GetTemplateParameterIds().ToList();
                    var candidates = all.Select(id => new NamedCandidate<ElementId>(TemplateSupport.ParameterLabel(doc, id), id)).ToList();
                    ElementId Find(string text)
                    {
                        var byBip = all.FirstOrDefault(id => string.Equals(TemplateSupport.BuiltInName(id), text?.Trim(), StringComparison.OrdinalIgnoreCase));
                        return byBip ?? NameMatcher.Single(text, candidates, "template parameter");
                    }
                    var non = new HashSet<ElementId>(template.GetNonControlledTemplateParameterIds());
                    var beforeControlled = TemplateSupport.Controlled(template).Select(id => TemplateSupport.ParameterLabel(doc, id)).ToList();
                    var wantControlled = (args.Controlled ?? new List<string>()).Select(Find).ToList();
                    var wantFree = (args.Uncontrolled ?? new List<string>()).Select(Find).ToList();
                    var clash = wantControlled.Intersect(wantFree).ToList();
                    if (clash.Count > 0)
                        throw new ToolException("INVALID_ARGUMENTS", "Parameter(s) both controlled and uncontrolled: " +
                            string.Join(", ", clash.Select(id => TemplateSupport.ParameterLabel(doc, id))));
                    foreach (var id in wantControlled) non.Remove(id);
                    foreach (var id in wantFree) non.Add(id);
                    template.SetNonControlledTemplateParameterIds(non.ToList());

                    var nowNon = new HashSet<ElementId>(template.GetNonControlledTemplateParameterIds());
                    var notApplied = wantControlled.Where(nowNon.Contains).Concat(wantFree.Where(id => !nowNon.Contains(id))).ToList();
                    if (notApplied.Count > 0)
                        throw new ToolException("TEMPLATE_NOT_APPLIED", "Revit did not apply: " +
                            string.Join(", ", notApplied.Select(id => TemplateSupport.ParameterLabel(doc, id))));
                    return new
                    {
                        operation = args.Operation,
                        template = Describe(doc, template),
                        controlledBefore = beforeControlled,
                        controlledAfter = TemplateSupport.Controlled(template).Select(id => TemplateSupport.ParameterLabel(doc, id)).ToList(),
                        dryRun = ctx.DryRun
                    };
                }
            }
        }

        private static object Describe(Document doc, View t) => new
        {
            templateId = t.Id.GetValue(),
            name = t.Name,
            viewType = t.ViewType.ToString(),
            controlledCount = TemplateSupport.Controlled(t).Count
        };
    }
}
