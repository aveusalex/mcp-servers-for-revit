using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Tools.Families.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Families
{
    public sealed class ChangeElementTypeArgs
    {
        [JsonProperty("elementIds", Required = Required.Always)]
        [Required]
        [MinLength(1)]
        [MaxLength(1000)]
        [Description("Elements whose type changes (instances, tags, views...).")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("targetType")]
        [Description("New type: id, \"Family: Type\" or type name (searched in the elements' category).")]
        public string TargetType { get; set; }

        [JsonProperty("matchFromElementId")]
        [Description("Alternative to targetType: use the type of this element (Match Type).")]
        public long? MatchFromElementId { get; set; }

        [JsonProperty("atomic")]
        [Description("true (default): if any element can't take the type, nothing changes. false: change the others and report the failures.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;
    }

    /// <summary>FAM-01: change the type of specific elements (incl. Match Type).</summary>
    [McpTool("change_element_type", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "families",
        Description = "Change the type of specific elements (instances, tags, section views...) to a type given by id, " +
                      "\"Family: Type\" or name, or to the type of another element (matchFromElementId = Match Type). " +
                      "Each element is checked against the types Revit allows for it (same category/class); the old " +
                      "and new type are read back per element. For every instance of a type use " +
                      "replace_type_instances. Undo: Ctrl+Z, or call again with the returned old types.")]
    public sealed class ChangeElementTypeTool : McpTool<ChangeElementTypeArgs>
    {
        protected override void Validate(ChangeElementTypeArgs a)
        {
            if (string.IsNullOrWhiteSpace(a.TargetType) == (a.MatchFromElementId == null))
                throw new ToolException("INVALID_ARGUMENTS", "Give exactly one of targetType or matchFromElementId.");
        }

        protected override object Run(ToolContext ctx, ChangeElementTypeArgs a)
        {
            var doc = ctx.Doc;
            var elements = EditGeometry.Elements(doc, a.ElementIds);
            ElementType target;
            if (a.MatchFromElementId.HasValue)
                target = TypeTools.TypeOfElement(ctx, a.MatchFromElementId.Value);
            else
            {
                var cats = elements.Select(e => CategoryService.BuiltInNameOf(e.Category)).Distinct().ToList();
                target = TypeTools.ResolveType(ctx, a.TargetType, cats.Count == 1 ? cats[0] : null);
            }

            var items = new List<object>();
            var failures = new List<string>();
            int changed = 0;
            foreach (var e in elements)
            {
                var oldType = ParameterService.TypeOf(e);
                if (oldType != null && oldType.Id == target.Id)
                {
                    items.Add(new { elementId = e.Id.GetValue(), status = "unchanged", type = TypeTools.Describe(target) });
                    continue;
                }
                if (!e.GetValidTypes().Contains(target.Id))
                {
                    failures.Add($"{e.Id.GetValue()}: '{LookupService.FullName(target)}' is not a valid type for this element");
                    items.Add(new { elementId = e.Id.GetValue(), status = "type_not_valid", oldType = TypeTools.Describe(oldType) });
                    continue;
                }
                try
                {
                    e.ChangeTypeId(target.Id);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
                {
                    failures.Add($"{e.Id.GetValue()}: {ex.Message}");
                    items.Add(new { elementId = e.Id.GetValue(), status = "set_failed", message = ex.Message });
                    continue;
                }
                var now = doc.GetElement(e.Id);
                if (now == null || now.GetTypeId() != target.Id)
                {
                    failures.Add($"{e.Id.GetValue()}: Revit kept another type");
                    items.Add(new { elementId = e.Id.GetValue(), status = "mismatch" });
                    continue;
                }
                changed++;
                items.Add(new { elementId = e.Id.GetValue(), status = "ok", oldType = TypeTools.Describe(oldType), newType = TypeTools.Describe(target) });
            }

            if (failures.Count > 0 && (a.Atomic || changed == 0))
                throw new ToolException("TYPE_CHANGE_FAILED",
                    $"{failures.Count} element(s) can't take '{LookupService.FullName(target)}'; nothing was changed. " + string.Join("; ", failures.Take(10)),
                    a.Atomic ? "Remove those elements or pass atomic:false." : "Pick a type of the same category (get_available_family_types).");

            return new
            {
                targetType = TypeTools.Describe(target),
                changed,
                failed = failures.Count,
                items,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + $"{changed} element(s) changed to '{LookupService.FullName(target)}'."
            };
        }
    }
}
