using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Sheets.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Sheets
{
    public sealed class RenumberItem
    {
        [JsonProperty("sheet", Required = Required.Always)]
        [Description("Sheet (current number, name or id).")]
        public string Sheet { get; set; }

        [JsonProperty("newNumber")]
        [Description("New sheet number.")]
        public string NewNumber { get; set; }

        [JsonProperty("newName")]
        [Description("New sheet name.")]
        public string NewName { get; set; }
    }

    public sealed class FindReplaceSpec
    {
        [JsonProperty("find", Required = Required.Always)]
        [Description("Text to find in the sheet numbers (case-sensitive).")]
        public string Find { get; set; }

        [JsonProperty("replace", Required = Required.Always)]
        [Description("Replacement text (may be empty).")]
        public string Replace { get; set; }

        [JsonProperty("sheets")]
        [Description("Only these sheets (numbers, names or ids). Omit for every sheet whose number contains find.")]
        public List<string> Sheets { get; set; }
    }

    public sealed class SequenceSpec
    {
        [JsonProperty("sheets", Required = Required.Always)]
        [Description("Sheets in the order they get numbers (numbers, names or ids).")]
        [MinLength(1)]
        public List<string> Sheets { get; set; }

        [JsonProperty("prefix")]
        [Description("Text before the counter, e.g. \"A-\".")]
        public string Prefix { get; set; }

        [JsonProperty("start")]
        [Description("First counter value (default 1).")]
        [DefaultValue(1)]
        public int Start { get; set; } = 1;

        [JsonProperty("step")]
        [Description("Counter increment (default 1).")]
        [DefaultValue(1)]
        public int Step { get; set; } = 1;

        [JsonProperty("digits")]
        [Description("Zero-pad the counter to this many digits (default 2: 01, 02...).")]
        [Range(1, 6)]
        [DefaultValue(2)]
        public int Digits { get; set; } = 2;

        [JsonProperty("suffix")]
        [Description("Text after the counter.")]
        public string Suffix { get; set; }
    }

    public sealed class RenumberSheetsArgs
    {
        [JsonProperty("items")]
        [Description("Explicit changes: {sheet, newNumber?, newName?} per sheet, up to 1000.")]
        [MaxLength(1000)]
        public List<RenumberItem> Items { get; set; }

        [JsonProperty("findReplace")]
        [Description("Replace text in sheet numbers.")]
        public FindReplaceSpec FindReplace { get; set; }

        [JsonProperty("sequence")]
        [Description("Number the given sheets in order: prefix + zero-padded counter + suffix.")]
        public SequenceSpec Sequence { get; set; }
    }

    /// <summary>FOL-09: renumber/rename sheets in bulk, collision-checked, in two passes (swaps work).</summary>
    [McpTool("renumber_sheets", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "sheets",
        Description = "Renumber and/or rename sheets in bulk by explicit items, findReplace on numbers, or a sequence " +
                      "(prefix + zero-padded counter + suffix over an ordered list). The final numbering is computed " +
                      "first and checked for forbidden characters and collisions (with every other sheet, placeholders " +
                      "included); then numbers are applied in two passes via temporary numbers so swaps like A<->B " +
                      "work. Read back. Use dryRun to preview. Needs approval. Undo: Ctrl+Z.")]
    public sealed class RenumberSheetsTool : McpTool<RenumberSheetsArgs>
    {
        protected override void Validate(RenumberSheetsArgs args)
        {
            var modes = (args.Items != null && args.Items.Count > 0 ? 1 : 0) + (args.FindReplace != null ? 1 : 0) + (args.Sequence != null ? 1 : 0);
            if (modes != 1) throw new ToolException("INVALID_ARGUMENTS", "Pass exactly one of items, findReplace or sequence.");
            if (args.FindReplace != null && string.IsNullOrEmpty(args.FindReplace.Find))
                throw new ToolException("INVALID_ARGUMENTS", "findReplace.find must not be empty.");
            if (args.Items != null && args.Items.Any(i => i == null || (i.NewNumber == null && i.NewName == null)))
                throw new ToolException("INVALID_ARGUMENTS", "Every item needs newNumber and/or newName.");
            if (args.Sequence != null && args.Sequence.Step == 0)
                throw new ToolException("INVALID_ARGUMENTS", "sequence.step must not be 0.");
        }

        protected override object Run(ToolContext ctx, RenumberSheetsArgs args)
        {
            var doc = ctx.Doc;
            var plan = new List<(ViewSheet sheet, string number, string name)>();
            if (args.Items != null && args.Items.Count > 0)
            {
                foreach (var i in args.Items)
                {
                    var s = SheetSupport.Sheet(ctx, i.Sheet);
                    plan.Add((s, i.NewNumber ?? s.SheetNumber, i.NewName ?? s.Name));
                }
            }
            else if (args.FindReplace != null)
            {
                var f = args.FindReplace;
                var sheets = f.Sheets != null && f.Sheets.Count > 0
                    ? f.Sheets.Select(s => SheetSupport.Sheet(ctx, s)).ToList()
                    : SheetSupport.AllSheets(doc).Where(s => s.SheetNumber.Contains(f.Find)).ToList();
                foreach (var s in sheets.Where(s => s.SheetNumber.Contains(f.Find)))
                    plan.Add((s, s.SheetNumber.Replace(f.Find, f.Replace ?? string.Empty), s.Name));
                if (plan.Count == 0) throw new ToolException("NOT_FOUND", $"No sheet number contains '{f.Find}'.");
            }
            else
            {
                var q = args.Sequence;
                var counter = q.Start;
                foreach (var reference in q.Sheets)
                {
                    var s = SheetSupport.Sheet(ctx, reference);
                    plan.Add((s, (q.Prefix ?? string.Empty) + counter.ToString(new string('0', q.Digits)) + (q.Suffix ?? string.Empty), s.Name));
                    counter += q.Step;
                }
            }

            var repeated = plan.GroupBy(p => p.sheet.Id).Where(g => g.Count() > 1).Select(g => g.First().sheet.SheetNumber).ToList();
            if (repeated.Count > 0)
                throw new ToolException("INVALID_ARGUMENTS", "Sheet(s) listed more than once: " + string.Join(", ", repeated));
            foreach (var p in plan)
            {
                NameRules.Validate(p.number, "sheet number");
                NameRules.Validate(p.name, "sheet name");
            }

            // Final numbering of the whole document must be unique.
            var final = SheetSupport.AllSheets(doc).ToDictionary(s => s.Id, s => s.SheetNumber);
            foreach (var p in plan) final[p.sheet.Id] = p.number;
            var clashes = final.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                .Select(g => $"{g.Key} ({string.Join(", ", g.Select(kv => (doc.GetElement(kv.Key) as ViewSheet)?.Name))})").ToList();
            if (clashes.Count > 0)
                throw new ToolException("NAME_COLLISION", "The new numbering would repeat sheet number(s): " + string.Join("; ", clashes.Take(20)),
                    "Include the sheets that hold those numbers in the same call so they move too.");

            var changing = plan.Where(p => p.number != p.sheet.SheetNumber).ToList();
            var before = plan.ToDictionary(p => p.sheet.Id, p => (number: p.sheet.SheetNumber, name: p.sheet.Name));
            var tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            for (var i = 0; i < changing.Count; i++)
                changing[i].sheet.SheetNumber = $"TMP-{tag}-{i}";
            foreach (var p in changing) ctx.Services.Names.ApplySheetNumber(p.sheet, p.number);
            foreach (var p in plan.Where(p => p.name != p.sheet.Name)) ctx.Services.Names.ApplyName(p.sheet, p.name, "sheet name");

            var result = plan.Select(p => new
            {
                sheetId = p.sheet.Id.GetValue(),
                before = new { number = before[p.sheet.Id].number, name = before[p.sheet.Id].name },
                after = new { number = p.sheet.SheetNumber, name = p.sheet.Name },
                changed = before[p.sheet.Id].number != p.sheet.SheetNumber || before[p.sheet.Id].name != p.sheet.Name
            }).ToList();
            return new { sheets = result, changedCount = result.Count(r => r.changed), dryRun = ctx.DryRun };
        }
    }
}
