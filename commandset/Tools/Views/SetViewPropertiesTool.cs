using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Views
{
    public sealed class SetViewPropertiesArgs
    {
        [JsonProperty("views", Required = Required.Always)]
        [Description("Views (names or ids), up to 200.")]
        [MinLength(1)]
        [MaxLength(200)]
        public List<string> Views { get; set; }

        [JsonProperty("scale")]
        [Description("Scale denominator (100 = 1:100).")]
        [Range(1, 24000)]
        public int? Scale { get; set; }

        [JsonProperty("detailLevel")]
        [Description("coarse, medium or fine.")]
        [EnumValues("coarse", "medium", "fine")]
        public string DetailLevel { get; set; }

        [JsonProperty("displayStyle")]
        [Description("Visual style: wireframe, hidden_line, shaded, shaded_with_edges, consistent_colors, realistic.")]
        [EnumValues("wireframe", "hidden_line", "shaded", "shaded_with_edges", "consistent_colors", "realistic")]
        public string DisplayStyle { get; set; }

        [JsonProperty("discipline")]
        [Description("View discipline: architectural, structural, mechanical, electrical, plumbing or coordination.")]
        [EnumValues("architectural", "structural", "mechanical", "electrical", "plumbing", "coordination")]
        public string Discipline { get; set; }

        [JsonProperty("phase")]
        [Description("Phase name or id shown by the view.")]
        public string Phase { get; set; }

        [JsonProperty("phaseFilter")]
        [Description("Phase filter name or id (e.g. \"Show All\" / \"Mostrar tudo\").")]
        public string PhaseFilter { get; set; }

        [JsonProperty("newName")]
        [Description("Rename the view (only with a single view). Must be free among views of its type.")]
        public string NewName { get; set; }

        [JsonProperty("atomic")]
        [Description("true (default): any property that doesn't stick rolls the whole call back. false: the rest is kept and failures are reported per view.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;
    }

    /// <summary>VIS-09: scale, detail level, visual style, discipline, phase, phase filter and name, with before/after.</summary>
    [McpTool("set_view_properties", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views",
        Description = "Change view properties on up to 200 views: scale (100 = 1:100), detailLevel, displayStyle, " +
                      "discipline, phase, phaseFilter, and newName for a single view. Every value is read back and " +
                      "returned as before/after; properties locked by a view template are reported as blocked with " +
                      "the template name. Atomic by default. Undo: Ctrl+Z, or call again with the 'before' values.")]
    public sealed class SetViewPropertiesTool : McpTool<SetViewPropertiesArgs>
    {
        protected override void Validate(SetViewPropertiesArgs args)
        {
            if (args.Scale == null && args.DetailLevel == null && args.DisplayStyle == null && args.Discipline == null &&
                args.Phase == null && args.PhaseFilter == null && args.NewName == null)
                throw new ToolException("INVALID_ARGUMENTS", "Nothing to change: pass at least one property.");
            if (args.NewName != null && args.Views.Count != 1)
                throw new ToolException("INVALID_ARGUMENTS", "newName works only with a single view.");
        }

        protected override object Run(ToolContext ctx, SetViewPropertiesArgs args)
        {
            var doc = ctx.Doc;
            var phase = args.Phase != null ? ctx.Services.Lookup.Phase(args.Phase) : null;
            PhaseFilter phaseFilter = null;
            if (args.PhaseFilter != null)
            {
                var filters = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>().ToList();
                phaseFilter = ctx.Services.Lookup.ByNameOrId(args.PhaseFilter, filters, "phase filter");
            }

            var results = new List<object>();
            var failures = new List<string>();
            foreach (var reference in args.Views.Distinct())
            {
                var view = ctx.Services.Lookup.View(reference);
                var template = view.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(view.ViewTemplateId) as View : null;
                var changes = new List<object>();

                void Do(string property, Func<object> get, Action set, Func<bool> ok)
                {
                    var before = Safe(get);
                    string status = "ok", message = null;
                    try
                    {
                        set();
                        if (!ok()) { status = "blocked"; message = BlockedMessage(template); }
                    }
                    catch (ToolException ex) { status = "failed"; message = ex.Message; }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        status = template != null ? "blocked" : "failed";
                        message = ex.Message + (template != null ? " " + BlockedMessage(template) : string.Empty);
                    }
                    changes.Add(new { property, before, after = Safe(get), status, message });
                    if (status != "ok") failures.Add($"'{view.Name}'.{property}: {status}{(message == null ? string.Empty : " - " + message)}");
                }

                if (args.Scale.HasValue)
                    Do("scale", () => view.Scale, () => view.Scale = args.Scale.Value, () => view.Scale == args.Scale.Value);
                if (args.DetailLevel != null)
                {
                    var level = ViewSupport.ParseDetailLevel(args.DetailLevel);
                    Do("detailLevel", () => ViewSupport.DetailLevelName(view.DetailLevel), () => view.DetailLevel = level, () => view.DetailLevel == level);
                }
                if (args.DisplayStyle != null)
                {
                    var style = ParseStyle(args.DisplayStyle);
                    Do("displayStyle", () => view.DisplayStyle.ToString(), () => view.DisplayStyle = style, () => view.DisplayStyle == style);
                }
                if (args.Discipline != null)
                {
                    var discipline = ParseDiscipline(args.Discipline);
                    Do("discipline", () => view.Discipline.ToString(), () => view.Discipline = discipline, () => view.Discipline == discipline);
                }
                if (phase != null)
                    Do("phase", () => doc.GetElement(view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId)?.Name,
                        () => SetId(view, BuiltInParameter.VIEW_PHASE, phase.Id),
                        () => view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() == phase.Id);
                if (phaseFilter != null)
                    Do("phaseFilter", () => doc.GetElement(view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId() ?? ElementId.InvalidElementId)?.Name,
                        () => SetId(view, BuiltInParameter.VIEW_PHASE_FILTER, phaseFilter.Id),
                        () => view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId() == phaseFilter.Id);
                if (args.NewName != null)
                    Do("name", () => view.Name, () =>
                    {
                        ctx.Services.Names.EnsureViewNameFree(args.NewName, view.ViewType, view.IsTemplate, view.Id);
                        ctx.Services.Names.ApplyName(view, args.NewName, "view name");
                    }, () => view.Name == args.NewName);

                results.Add(new { viewId = view.Id.GetValue(), name = view.Name, template = template?.Name, changes });
            }

            if (args.Atomic && failures.Count > 0)
                throw new ToolException("VIEW_PROPERTY_NOT_APPLIED",
                    $"{failures.Count} change(s) did not stick, so nothing was changed: {string.Join("; ", failures.Take(10))}",
                    "Properties controlled by a view template must be changed on the template (or remove the template); pass atomic:false to keep the rest.");

            return new { views = results, failedCount = failures.Count, dryRun = ctx.DryRun };
        }

        private static void SetId(View view, BuiltInParameter bip, ElementId id)
        {
            var p = view.get_Parameter(bip)
                    ?? throw new ToolException("NOT_SUPPORTED", $"'{view.Name}' ({view.ViewType}) has no {bip} parameter.");
            if (p.IsReadOnly) return; // reported as blocked by the read-back
            p.Set(id);
        }

        private static string BlockedMessage(View template) =>
            template != null ? $"Probably controlled by view template '{template.Name}'." : "Revit kept the old value.";

        private static object Safe(Func<object> get)
        {
            try { return get(); }
            catch { return null; }
        }

        private static DisplayStyle ParseStyle(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "wireframe": return DisplayStyle.Wireframe;
                case "hidden_line": return DisplayStyle.HLR;
                case "shaded": return DisplayStyle.Shading;
                case "shaded_with_edges": return DisplayStyle.ShadingWithEdges;
                case "consistent_colors": return DisplayStyle.FlatColors;
                case "realistic": return DisplayStyle.Realistic;
                default: throw new ToolException("INVALID_ARGUMENTS", $"Unknown displayStyle '{s}'.");
            }
        }

        private static ViewDiscipline ParseDiscipline(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "architectural": return ViewDiscipline.Architectural;
                case "structural": return ViewDiscipline.Structural;
                case "mechanical": return ViewDiscipline.Mechanical;
                case "electrical": return ViewDiscipline.Electrical;
                case "plumbing": return ViewDiscipline.Plumbing;
                case "coordination": return ViewDiscipline.Coordination;
                default: throw new ToolException("INVALID_ARGUMENTS", $"Unknown discipline '{s}'.");
            }
        }
    }
}
