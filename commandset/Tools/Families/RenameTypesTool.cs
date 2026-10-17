using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Editing;
using RevitMCPCommandSet.Tools.Parameters.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Families
{
    public sealed class RenameTypesArgs
    {
        [JsonProperty("typeIds")]
        [MaxLength(2000)]
        [Description("Types to rename (ids as strings; an instance id means its type). Alternative to categories.")]
        public List<string> TypeIds { get; set; }

        [JsonProperty("categories")]
        [Description("Every type of these categories (any language), narrowed by familyName / nameContains.")]
        public List<string> Categories { get; set; }

        [JsonProperty("familyName")]
        [Description("Only types of this family (case and accents ignored).")]
        public string FamilyName { get; set; }

        [JsonProperty("nameContains")]
        [Description("Only types (or families) whose current name contains this text.")]
        public string NameContains { get; set; }

        [JsonProperty("renameFamilies")]
        [Description("Rename the loadable families of the selected types instead of the types. Default false.")]
        [DefaultValue(false)]
        public bool RenameFamilies { get; set; }

        [JsonProperty("operation", Required = Required.Always)]
        [Required]
        [Description("set (value; \"{n}\" becomes a counter), prefix / suffix (value), findReplace (find → replace), regexReplace (.NET regex).")]
        [EnumValues("set", "prefix", "suffix", "findReplace", "regexReplace")]
        public string Operation { get; set; }

        [JsonProperty("value")]
        [Description("New name (set) or text to add (prefix / suffix).")]
        public string Value { get; set; }

        [JsonProperty("find")]
        [Description("Text or regular expression to look for.")]
        public string Find { get; set; }

        [JsonProperty("replace")]
        [Description("Replacement (default empty).")]
        public string Replace { get; set; }

        [JsonProperty("separator")]
        [Description("Text between the name and the prefix/suffix (default none).")]
        public string Separator { get; set; }

        [JsonProperty("ignoreCase")]
        [Description("findReplace / regexReplace ignore case. Default false.")]
        [DefaultValue(false)]
        public bool IgnoreCase { get; set; }

        [JsonProperty("start")]
        [Description("First value of the {n} counter (default 1).")]
        [DefaultValue(1)]
        public int Start { get; set; } = 1;

        [JsonProperty("atomic")]
        [Description("true (default): any invalid name or collision renames nothing.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;
    }

    /// <summary>FAM-04: rename types (or their families) in batch, on the shared RenameEngine.</summary>
    [McpTool("rename_types", Kind = ToolKind.Write, Risk = Risk.Medium, DryRunDefault = true, Toolset = "families",
        Description = "Rename types in batch (or, with renameFamilies, their loadable families): set with {n} counter, " +
                      "prefix, suffix, findReplace or regexReplace, over explicit type ids or every type of categories " +
                      "filtered by family and name. Checks forbidden characters and collisions within each family " +
                      "before writing, handles swapped names, reads names back and reports instancesAffected. System " +
                      "families (walls, floors) can't be renamed, only their types. Preview with dryRun:true (no " +
                      "approval), then run (needs approval). Undo: Ctrl+Z.")]
    public sealed class RenameTypesTool : McpTool<RenameTypesArgs>
    {
        protected override void Validate(RenameTypesArgs a)
        {
            if ((a.TypeIds?.Count ?? 0) == 0 && (a.Categories?.Count ?? 0) == 0)
                throw new ToolException("INVALID_ARGUMENTS", "Give typeIds or categories.");
            if (a.Operation == "set" && string.IsNullOrEmpty(a.Value))
                throw new ToolException("INVALID_ARGUMENTS", "operation 'set' needs value.");
            TextOperation.Create(a.Operation, a.Value, a.Find, a.Replace, a.Separator, a.IgnoreCase);
        }

        protected override object Run(ToolContext ctx, RenameTypesArgs a)
        {
            List<ElementType> types;
            if (a.TypeIds != null && a.TypeIds.Count > 0)
            {
                types = a.TypeIds.Select(id =>
                {
                    var e = ctx.Services.Lookup.Element(id);
                    return e as ElementType ?? ParameterService.TypeOf(e) ?? throw new ToolException("TYPE_NOT_FOUND", $"Element {id} has no type.");
                }).ToList();
            }
            else
            {
                var cats = ctx.Services.Categories.ResolveAll(a.Categories).Select(c => c.Id).ToList();
                types = new FilteredElementCollector(ctx.Doc).WhereElementIsElementType()
                    .WherePasses(new ElementMulticategoryFilter(cats)).Cast<ElementType>().ToList();
            }
            if (!string.IsNullOrWhiteSpace(a.FamilyName))
            {
                var key = TextMatch.Key(a.FamilyName);
                types = types.Where(t => TextMatch.Key(LookupService.SafeFamilyName(t) ?? string.Empty) == key).ToList();
            }

            List<Element> targets;
            if (a.RenameFamilies)
            {
                var systemTypes = types.Where(t => !(t is FamilySymbol)).ToList();
                if (systemTypes.Count > 0)
                    ctx.Warnings.Add(new ToolWarning($"{systemTypes.Count} type(s) belong to system families, which can't be renamed; skipped.",
                        systemTypes.Take(50).Select(t => t.Id.GetValue()).ToList()));
                targets = types.OfType<FamilySymbol>().Select(s => s.Family).Where(f => f != null)
                    .GroupBy(f => f.Id.GetValue()).Select(g => (Element)g.First()).ToList();
            }
            else targets = types.GroupBy(t => t.Id.GetValue()).Select(g => (Element)g.First()).ToList();

            return RenameElementsTool.Rename(ctx, targets.OrderBy(e => e.Id.GetValue()).ToList(), new RenameElementsArgs
            {
                NameContains = a.NameContains,
                Operation = a.Operation,
                Value = a.Value,
                Find = a.Find,
                Replace = a.Replace,
                Separator = a.Separator,
                IgnoreCase = a.IgnoreCase,
                Start = a.Start,
                Atomic = a.Atomic,
                SheetField = "name"
            }, countInstances: true);
        }
    }
}
