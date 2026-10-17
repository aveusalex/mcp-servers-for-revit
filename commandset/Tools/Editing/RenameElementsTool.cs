using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Tools.Parameters.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    /// <summary>Text operation arguments shared by rename_elements and rename_types.</summary>
    public sealed class RenameElementsArgs
    {
        [JsonProperty("elementIds")]
        [MaxLength(2000)]
        [Description("Elements to rename (views, sheets, levels, grids, rooms, types, families, materials...). Alternative to categories.")]
        public List<string> ElementIds { get; set; }

        [JsonProperty("categories")]
        [Description("Categories in any language (OST_Views, OST_Sheets, Levels, Ambientes...): every element (or type, with elementKind) of them.")]
        public List<string> Categories { get; set; }

        [JsonProperty("elementKind")]
        [Description("With categories: instances (default) or types.")]
        [EnumValues("instances", "types")]
        [DefaultValue("instances")]
        public string ElementKind { get; set; } = "instances";

        [JsonProperty("nameContains")]
        [Description("Only elements whose current name contains this text (case and accents ignored).")]
        public string NameContains { get; set; }

        [JsonProperty("operation", Required = Required.Always)]
        [Required]
        [Description("set (value; \"{n}\" becomes a counter), prefix / suffix (value), findReplace (find → replace), regexReplace (.NET regex, replace may use $1).")]
        [EnumValues("set", "prefix", "suffix", "findReplace", "regexReplace")]
        public string Operation { get; set; }

        [JsonProperty("value")]
        [Description("New name (set) or the text to add (prefix / suffix). \"{n}\" is replaced by a counter.")]
        public string Value { get; set; }

        [JsonProperty("find")]
        [Description("Text or regular expression to look for (findReplace / regexReplace).")]
        public string Find { get; set; }

        [JsonProperty("replace")]
        [Description("Replacement (default empty). \"{n}\" is replaced by a counter.")]
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

        [JsonProperty("sheetField")]
        [Description("For sheets: rename the name (default) or the number.")]
        [EnumValues("name", "number")]
        [DefaultValue("name")]
        public string SheetField { get; set; } = "name";

        [JsonProperty("atomic")]
        [Description("true (default): any invalid name or collision renames nothing. false: rename the valid ones and report the rest.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;
    }

    /// <summary>EDT-07: batch rename with collision checks and swap-safe two passes.</summary>
    [McpTool("rename_elements", Kind = ToolKind.Write, Risk = Risk.Medium, DryRunDefault = true, Toolset = "editing",
        Description = "Rename many elements at once: views, sheets (name or number), levels, grids, rooms (Name " +
                      "parameter), types, families, materials. Operations: set (with {n} counter), prefix, suffix, " +
                      "findReplace, regexReplace. Before writing it checks characters Revit forbids and collisions in " +
                      "the scope Revit uses (inside the batch and against the document); swapped names go through a " +
                      "temporary pass. Every name is read back. ALWAYS preview with dryRun:true first (no approval; " +
                      "returns the before/after table), then run (needs approval). Undo: Ctrl+Z.")]
    public sealed class RenameElementsTool : McpTool<RenameElementsArgs>
    {
        public const int MaxItems = 2000;

        protected override void Validate(RenameElementsArgs a)
        {
            if ((a.ElementIds?.Count ?? 0) == 0 && (a.Categories?.Count ?? 0) == 0)
                throw new ToolException("INVALID_ARGUMENTS", "Give elementIds or categories.");
            if (a.Operation == "set" && string.IsNullOrEmpty(a.Value))
                throw new ToolException("INVALID_ARGUMENTS", "operation 'set' needs value.");
            TextOperation.Create(a.Operation, a.Value, a.Find, a.Replace, a.Separator, a.IgnoreCase);
        }

        protected override object Run(ToolContext ctx, RenameElementsArgs a)
        {
            var elements = ElementScope.Collect(ctx, new ElementScopeRequest
            {
                ElementIds = a.ElementIds,
                Categories = a.Categories,
                Types = a.ElementKind == "types"
            });
            return Rename(ctx, elements, a, countInstances: true);
        }

        /// <summary>Shared by rename_types.</summary>
        internal static object Rename(ToolContext ctx, List<Element> elements, RenameElementsArgs a, bool countInstances)
        {
            if (!string.IsNullOrWhiteSpace(a.NameContains))
            {
                var key = TextMatch.Fold(a.NameContains);
                elements = elements.Where(e => TextMatch.Fold(RenameEngine.SafeName(e) ?? string.Empty).Contains(key)).ToList();
            }
            if (elements.Count == 0)
                throw new ToolException("NOTHING_TO_RENAME", "No element matches.", "Check the ids or categories with find_elements.", "find_elements");
            if (elements.Count > MaxItems)
                throw new ToolException("TOO_MANY_ELEMENTS", $"{elements.Count} elements match; the limit is {MaxItems} per call.", "Narrow the scope.");

            var op = TextOperation.Create(a.Operation, a.Value, a.Find, a.Replace, a.Separator, a.IgnoreCase);
            var engine = new RenameEngine(ctx.Doc, ctx.Services.Names);
            var items = engine.Plan(elements, op, a.Start, a.SheetField, countInstances);

            var bad = items.Where(i => i.Status == "invalid_name" || i.Status == "collision" || i.Status == "not_renamable").ToList();
            if (bad.Count > 0 && a.Atomic)
                throw new ToolException(bad.Any(b => b.Status == "collision") ? "NAME_COLLISION" : "INVALID_NAME",
                    $"{bad.Count} name(s) can't be applied; nothing was renamed. " +
                    string.Join(" ", bad.Take(10).Select(b => $"[{b.Element.Id.GetValue()}] '{b.Before}' → '{b.After}': {b.Message}")),
                    "Change the operation, or pass atomic:false to rename only the valid ones.");

            engine.Apply(items);
            var renamed = items.Count(i => i.Status == "ok");
            return new
            {
                renamed,
                unchanged = items.Count(i => i.Status == "unchanged" || i.Status == "no_match"),
                failed = bad.Count,
                items = items.Where(i => i.Status != "no_match").Take(500).Select(i => i.ToResult()),
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + $"{renamed} element(s) renamed; {bad.Count} refused."
            };
        }
    }
}
