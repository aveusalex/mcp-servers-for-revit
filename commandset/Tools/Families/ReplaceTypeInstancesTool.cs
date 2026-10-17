using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Families.Support;
using RevitMCPCommandSet.Tools.Parameters.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Families
{
    public sealed class ReplaceTypeInstancesArgs
    {
        [JsonProperty("fromType", Required = Required.Always)]
        [Required]
        [Description("Type to replace: id, \"Family: Type\" or type name.")]
        public string FromType { get; set; }

        [JsonProperty("toType", Required = Required.Always)]
        [Required]
        [Description("Replacement type: id, \"Family: Type\" or type name (same category).")]
        public string ToType { get; set; }

        [JsonProperty("category")]
        [Description("Category (any language) to disambiguate type names.")]
        public string Category { get; set; }

        [JsonProperty("scope")]
        [Description("project (default), view (instances visible in view) or selection (selected instances only).")]
        [EnumValues("project", "view", "selection")]
        [DefaultValue("project")]
        public string Scope { get; set; } = "project";

        [JsonProperty("view")]
        [Description("View name or id for scope=view.")]
        public string View { get; set; }

        [JsonProperty("maxInstances")]
        [Description("Safety limit (default 1000, max 5000).")]
        [Range(1, 5000)]
        [DefaultValue(1000)]
        public int MaxInstances { get; set; } = 1000;
    }

    /// <summary>FAM-02: every instance of type A becomes type B (project, view or selection).</summary>
    [McpTool("replace_type_instances", Kind = ToolKind.Write, Risk = Risk.Medium, DryRunDefault = true, Toolset = "families",
        Description = "Replace every instance of one type with another type of the same category, in the whole project, " +
                      "in a view or in the selection. Types by id, \"Family: Type\" or name. Instances inside groups are " +
                      "skipped and reported (Revit can't change them without editing the group). Preview with " +
                      "dryRun:true (no approval; returns the count and ids), then run (needs approval). Undo: Ctrl+Z, " +
                      "or run again with fromType/toType swapped on the returned ids via change_element_type.")]
    public sealed class ReplaceTypeInstancesTool : McpTool<ReplaceTypeInstancesArgs>
    {
        protected override void Validate(ReplaceTypeInstancesArgs a)
        {
            a.Scope = (a.Scope ?? "project").Trim().ToLowerInvariant();
            if (a.Scope == "view" && string.IsNullOrWhiteSpace(a.View))
                throw new ToolException("INVALID_ARGUMENTS", "scope=view needs view.");
        }

        protected override object Run(ToolContext ctx, ReplaceTypeInstancesArgs a)
        {
            var doc = ctx.Doc;
            var from = TypeTools.ResolveType(ctx, a.FromType, a.Category);
            var to = TypeTools.ResolveType(ctx, a.ToType, a.Category ?? CategoryService.BuiltInNameOf(from.Category));
            if (from.Id == to.Id) throw new ToolException("INVALID_ARGUMENTS", "fromType and toType are the same type.");

            IEnumerable<Element> instances;
            switch (a.Scope)
            {
                case "view":
                    var view = ctx.Services.Lookup.View(a.View);
                    var inView = new HashSet<long>(new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds().Select(i => i.GetValue()));
                    instances = TypeTools.Instances(doc, from).Where(e => inView.Contains(e.Id.GetValue()));
                    break;
                case "selection":
                    instances = ElementScope.SelectedElements(ctx).Where(e => e.GetTypeId() == from.Id);
                    break;
                default:
                    instances = TypeTools.Instances(doc, from);
                    break;
            }
            var list = instances.OrderBy(e => e.Id.GetValue()).ToList();
            if (list.Count == 0)
                throw new ToolException("NOTHING_TO_REPLACE", $"No instance of '{LookupService.FullName(from)}' in scope {a.Scope}.");
            if (list.Count > a.MaxInstances)
                throw new ToolException("TOO_MANY_ELEMENTS", $"{list.Count} instances; maxInstances is {a.MaxInstances}.", "Narrow the scope or raise maxInstances.");
            if (!list[0].GetValidTypes().Contains(to.Id))
                throw new ToolException("TYPE_NOT_VALID", $"'{LookupService.FullName(to)}' is not a valid replacement for '{LookupService.FullName(from)}' (different category or kind).");

            var changed = new List<long>();
            var skipped = new List<object>();
            foreach (var e in list)
            {
                if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId)
                {
                    skipped.Add(new { elementId = e.Id.GetValue(), reason = "in_group", groupId = e.GroupId.GetValue() });
                    continue;
                }
                try
                {
                    e.ChangeTypeId(to.Id);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
                {
                    skipped.Add(new { elementId = e.Id.GetValue(), reason = "set_failed", message = ex.Message });
                    continue;
                }
                if (doc.GetElement(e.Id)?.GetTypeId() == to.Id) changed.Add(e.Id.GetValue());
                else skipped.Add(new { elementId = e.Id.GetValue(), reason = "mismatch" });
            }
            if (changed.Count == 0)
                throw new ToolException("TYPE_CHANGE_FAILED", $"None of the {list.Count} instance(s) could be changed.",
                    "Instances in groups must be changed by editing the group in Revit.");
            if (skipped.Count > 0)
                ctx.Warnings.Add(new ToolWarning($"{skipped.Count} instance(s) were not changed (see skipped)."));

            return new
            {
                fromType = TypeTools.Describe(from),
                toType = TypeTools.Describe(to),
                matched = list.Count,
                changed = changed.Count,
                changedIds = changed,
                skipped,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"{changed.Count} instance(s) of '{LookupService.FullName(from)}' now '{LookupService.FullName(to)}'."
            };
        }
    }
}
