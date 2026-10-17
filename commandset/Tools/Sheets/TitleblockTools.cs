using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Sheets.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Sheets
{
    // ---- FOL-10 ------------------------------------------------------------------------------------------------------

    public sealed class GetTitleblockParametersArgs
    {
        [JsonProperty("sheets")]
        [Description("Sheets (numbers, names or ids). Omit for every non-placeholder sheet (paged).")]
        [MaxLength(1000)]
        public List<string> Sheets { get; set; }

        [JsonProperty("target")]
        [Description("Where to read: both (sheet + title block instance, default), sheet, titleblock (instance), titleblock_type, or all.")]
        [EnumValues("both", "sheet", "titleblock", "titleblock_type", "all")]
        [DefaultValue("both")]
        public string Target { get; set; } = "both";

        [JsonProperty("parameters")]
        [Description("Only these parameters, each {guid} | {builtIn} | {name}. Omit for all.")]
        public List<ParameterRef> Parameters { get; set; }

        [JsonProperty("includeReadOnly")]
        [Description("Include read-only parameters (default false).")]
        [DefaultValue(false)]
        public bool IncludeReadOnly { get; set; }

        [JsonProperty("limit")]
        [Description("Sheets per page, 1-200 (default 50).")]
        [DefaultValue(50)]
        public int Limit { get; set; } = 50;

        [JsonProperty("offset")]
        [Description("Sheets to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    [McpTool("get_titleblock_parameters", Kind = ToolKind.Read, Toolset = "sheets", Idempotent = true,
        Description = "Read the parameters that fill a title block: the sheet's own parameters (number, name, drawn by, " +
                      "dates...) and the title block family instance's (and optionally type's), with values in wire " +
                      "units and the 'on' owner (sheet / titleblock / titleblock_type) to use in set_titleblock_parameters. " +
                      "Filter by parameters[]; paged by sheet. Read-only.")]
    public sealed class GetTitleblockParametersTool : McpTool<GetTitleblockParametersArgs>
    {
        protected override object Run(ToolContext ctx, GetTitleblockParametersArgs args)
        {
            var doc = ctx.Doc;
            var target = (args.Target ?? "both").ToLowerInvariant();
            var sheets = args.Sheets != null && args.Sheets.Count > 0
                ? args.Sheets.Select(s => SheetSupport.Sheet(ctx, s)).GroupBy(s => s.Id).Select(g => g.First()).ToList()
                : SheetSupport.AllSheets(doc).Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber, new SheetSupport.NaturalComparer()).ToList();
            var page = Paging.Page(sheets, args.Limit, args.Offset, 200);
            var ps = ctx.Services.Parameters;

            var result = page.Items.Select(sheet =>
            {
                var tbs = SheetSupport.TitleBlocks(doc, sheet.Id);
                var tb = tbs.FirstOrDefault();
                var owners = new List<(Element e, string on)>();
                if (target == "both" || target == "sheet" || target == "all") owners.Add((sheet, "sheet"));
                if (tb != null && (target == "both" || target == "titleblock" || target == "all")) owners.Add((tb, "titleblock"));
                if (tb != null && (target == "titleblock_type" || target == "all")) owners.Add((tb.Symbol, "titleblock_type"));
                var parameters = new List<object>();
                var missing = new List<string>();
                foreach (var (owner, on) in owners)
                {
                    IEnumerable<Parameter> found;
                    if (args.Parameters != null && args.Parameters.Count > 0)
                    {
                        var list = new List<Parameter>();
                        foreach (var r in args.Parameters)
                        {
                            try
                            {
                                var p = ps.Find(owner, r);
                                if (p != null) list.Add(p);
                            }
                            catch (ToolException) { /* reported as missing below */ }
                        }
                        found = list;
                    }
                    else found = owner.Parameters.Cast<Parameter>().OrderBy(p => p.Definition?.Name, StringComparer.CurrentCultureIgnoreCase);
                    foreach (var p in found)
                    {
                        if (!args.IncludeReadOnly && p.IsReadOnly) continue;
                        parameters.Add(ps.Describe(p, on, true));
                    }
                }
                if (args.Parameters != null)
                {
                    foreach (var r in args.Parameters)
                    {
                        var any = owners.Any(o => { try { return ps.Find(o.e, r) != null; } catch (ToolException) { return false; } });
                        if (!any) missing.Add(r.ToString());
                    }
                }
                return new
                {
                    sheetId = sheet.Id.GetValue(),
                    number = sheet.SheetNumber,
                    name = sheet.Name,
                    titleBlock = tb == null ? null : new { instanceId = tb.Id.GetValue(), type = tb.Symbol.FamilyName + ": " + tb.Symbol.Name },
                    titleBlockCount = tbs.Count,
                    parameters,
                    notFound = missing.Count > 0 ? missing : null
                };
            }).ToList();
            return new { sheets = result, total = page.Total, limit = page.Limit, offset = page.Offset, truncated = page.Truncated };
        }
    }

    // ---- FOL-11 ------------------------------------------------------------------------------------------------------

    public sealed class TitleblockWriteItem
    {
        [JsonProperty("sheet", Required = Required.Always)]
        [Description("Sheet number, name or id.")]
        public string Sheet { get; set; }

        [JsonProperty("parameters", Required = Required.Always)]
        [Description("Values by parameter name, e.g. {\"Desenhado por\": \"AE\", \"Data de emissão da folha\": \"2026-10-17\"}. Same value rules as set_parameters.")]
        public Dictionary<string, JToken> Parameters { get; set; }

        [JsonProperty("target")]
        [Description("auto (default: the sheet if it has the parameter, else the title block instance), sheet or titleblock.")]
        [EnumValues("auto", "sheet", "titleblock")]
        [DefaultValue("auto")]
        public string Target { get; set; } = "auto";
    }

    public sealed class SetTitleblockParametersArgs
    {
        [JsonProperty("items", Required = Required.Always)]
        [Description("One entry per sheet, up to 500 sheets.")]
        [MinLength(1)]
        [MaxLength(500)]
        public List<TitleblockWriteItem> Items { get; set; }

        [JsonProperty("atomic")]
        [Description("true (default): any invalid or refused value rolls the whole call back. false: valid writes are kept and the rest reported.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;
    }

    [McpTool("set_titleblock_parameters", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "sheets",
        Description = "Fill title block data in batch: per sheet, parameter values by name written to the sheet or to " +
                      "its title block instance (auto picks the owner that has the parameter). Uses the set_parameters " +
                      "engine: wire units, read-back with 'mismatch', atomic by default, revert[] returned. Title block " +
                      "TYPE parameters (shared by every sheet of that type) are not written here: use " +
                      "set_type_parameters. Undo: Ctrl+Z or send revert[] to set_parameters.")]
    public sealed class SetTitleblockParametersTool : McpTool<SetTitleblockParametersArgs>
    {
        protected override void Validate(SetTitleblockParametersArgs args)
        {
            foreach (var i in args.Items)
            {
                if (i == null || i.Parameters == null || i.Parameters.Count == 0)
                    throw new ToolException("INVALID_ARGUMENTS", "Every item needs sheet and at least one parameter.");
                i.Target = (i.Target ?? "auto").ToLowerInvariant();
            }
            var total = args.Items.Sum(i => i.Parameters.Count);
            if (total > 2000) throw new ToolException("TOO_MANY_ITEMS", $"{total} values requested; the limit is 2000 per call.");
        }

        protected override object Run(ToolContext ctx, SetTitleblockParametersArgs args)
        {
            var doc = ctx.Doc;
            var ps = ctx.Services.Parameters;
            var requests = new List<ParameterWriteRequest>();
            foreach (var item in args.Items)
            {
                var sheet = SheetSupport.Sheet(ctx, item.Sheet);
                var tbs = SheetSupport.TitleBlocks(doc, sheet.Id);
                foreach (var kv in item.Parameters)
                {
                    var r = ParameterRef.ByName(kv.Key);
                    Element owner;
                    if (item.Target == "sheet") owner = sheet;
                    else if (item.Target == "titleblock") owner = SingleTitleBlock(sheet, tbs);
                    else
                    {
                        Parameter onSheet = null;
                        try { onSheet = ps.Find(sheet, r); } catch (ToolException) { /* try the title block */ }
                        owner = onSheet != null || tbs.Count == 0 ? (Element)sheet : SingleTitleBlock(sheet, tbs);
                    }
                    requests.Add(new ParameterWriteRequest
                    {
                        Element = owner.Id.GetValue().ToString(CultureInfo.InvariantCulture),
                        Parameter = r,
                        Value = kv.Value
                    });
                }
            }

            var plan = ps.Plan(requests, new ParameterPlanOptions { Target = ParameterTarget.Instance, MaxItems = 2000 });
            if (args.Atomic && plan.HasErrors)
                throw new ToolException("PARAMETER_WRITE_REFUSED",
                    $"{plan.Failed.Count} of {plan.Items.Count} value(s) are invalid; nothing was written. {plan.FailureSummary()}",
                    "Check names with get_titleblock_parameters, or pass atomic:false.", "get_titleblock_parameters");
            var result = ps.Apply(plan);
            if (args.Atomic && result.Failed > 0)
                throw new ToolException("PARAMETER_WRITE_FAILED",
                    $"{result.Failed} write(s) did not stick, so the whole call was rolled back. " +
                    string.Join(" ", result.Items.Where(i => i.Status != "ok").Take(10).Select(i => $"[{i.Index}] {i.Parameter?.Name}: {i.Status} {i.Message}")),
                    "Check formulas and read-only parameters, or pass atomic:false.");
            return new { applied = result.Applied, failed = result.Failed, items = result.Items, revert = result.Revert, dryRun = ctx.DryRun };
        }

        private static Element SingleTitleBlock(ViewSheet sheet, List<FamilyInstance> tbs)
        {
            if (tbs.Count == 0)
                throw new ToolException("TITLE_BLOCK_NOT_FOUND", $"Sheet {sheet.SheetNumber} has no title block.");
            if (tbs.Count > 1)
                throw new ToolException("AMBIGUOUS_TITLE_BLOCK", $"Sheet {sheet.SheetNumber} has {tbs.Count} title blocks.",
                    "Write to one of them with set_parameters and its instance id (see get_titleblock_parameters).", "set_parameters");
            return tbs[0];
        }
    }

    // ---- FOL-12 ------------------------------------------------------------------------------------------------------

    public sealed class ListTitleblocksArgs
    {
        [JsonProperty("nameContains")]
        [Description("Only title blocks whose \"Family: Type\" contains this text (case and accents ignored).")]
        public string NameContains { get; set; }

        [JsonProperty("includeUnused")]
        [Description("Include types not used by any sheet (default true).")]
        [DefaultValue(true)]
        public bool IncludeUnused { get; set; } = true;

        [JsonProperty("limit")]
        [Description("Page size, 1-500 (default 100).")]
        [DefaultValue(100)]
        public int Limit { get; set; } = 100;

        [JsonProperty("offset")]
        [Description("Types to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    [McpTool("list_titleblocks", Kind = ToolKind.Read, Toolset = "sheets", Idempotent = true,
        Description = "List title block types (\"Family: Type\" as create_sheets expects) with id, whether it is the " +
                      "document default, paper size in mm when the family exposes it, and how many sheets use it " +
                      "(with up to 20 sheet numbers). Filter by nameContains, skip unused types with " +
                      "includeUnused:false; paged. Read-only.")]
    public sealed class ListTitleblocksTool : McpTool<ListTitleblocksArgs>
    {
        protected override object Run(ToolContext ctx, ListTitleblocksArgs args)
        {
            var doc = ctx.Doc;
            var instances = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
            var bySymbol = instances.ToLookup(i => i.Symbol.Id);
            ElementId defaultId = ElementId.InvalidElementId;
            try { defaultId = doc.GetDefaultFamilyTypeId(new ElementId(BuiltInCategory.OST_TitleBlocks)); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { /* none */ }

            IEnumerable<FamilySymbol> types = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>();
            if (!args.IncludeUnused) types = types.Where(t => bySymbol[t.Id].Any());
            if (!string.IsNullOrWhiteSpace(args.NameContains))
            {
                var key = TextMatch.Fold(args.NameContains);
                types = types.Where(t => TextMatch.Fold(t.FamilyName + ": " + t.Name).Contains(key));
            }
            var page = Paging.Page(types.OrderBy(t => t.FamilyName).ThenBy(t => t.Name).ToList(), args.Limit, args.Offset, 500);
            var items = page.Items.Select(t =>
            {
                var sheets = bySymbol[t.Id].Select(i => doc.GetElement(i.OwnerViewId) as ViewSheet).Where(s => s != null)
                    .Select(s => s.SheetNumber).OrderBy(n => n, new SheetSupport.NaturalComparer()).ToList();
                return new
                {
                    typeId = t.Id.GetValue(),
                    name = t.FamilyName + ": " + t.Name,
                    family = t.FamilyName,
                    type = t.Name,
                    isDefault = t.Id == defaultId,
                    paperWidth = Length(t, BuiltInParameter.SHEET_WIDTH),
                    paperHeight = Length(t, BuiltInParameter.SHEET_HEIGHT),
                    sheetCount = sheets.Count,
                    sheets = sheets.Take(20).ToList()
                };
            }).ToList();
            return new { titleBlocks = items, total = page.Total, limit = page.Limit, offset = page.Offset, truncated = page.Truncated };
        }

        private static double? Length(Element e, BuiltInParameter bip)
        {
            var p = e.get_Parameter(bip);
            return p != null && p.HasValue && p.StorageType == StorageType.Double ? Math.Round(p.AsDouble() * 304.8, 1) : (double?)null;
        }
    }
}
