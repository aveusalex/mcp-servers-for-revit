using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Sheets
{
    public sealed class SheetViewPlacement
    {
        [JsonProperty("view", Required = Required.Always)]
        [Description("View or schedule to place, by name or id.")]
        public string View { get; set; }

        [JsonProperty("x", Required = Required.Always)]
        [Description("Centre of the viewport on the sheet, from the sheet origin (bottom-left of most title blocks).")]
        [Unit("mm")]
        public double X { get; set; }

        [JsonProperty("y", Required = Required.Always)]
        [Description("Centre of the viewport on the sheet, from the sheet origin.")]
        [Unit("mm")]
        public double Y { get; set; }
    }

    public sealed class SheetItem
    {
        [JsonProperty("number", Required = Required.Always)]
        [Description("Sheet number, unique in the document (e.g. \"A-101\").")]
        public string Number { get; set; }

        [JsonProperty("name", Required = Required.Always)]
        [Description("Sheet name (e.g. \"Planta Baixa - Térreo\").")]
        public string Name { get; set; }

        [JsonProperty("titleBlock")]
        [Description("Title block type as \"Family: Type\", type name or id. Omit for the document's default title block; \"none\" for no title block.")]
        public string TitleBlock { get; set; }

        [JsonProperty("placeholder")]
        [Description("Create a placeholder sheet (listed in sheet lists, not printed; no title block or views). Default false.")]
        [DefaultValue(false)]
        public bool Placeholder { get; set; }

        [JsonProperty("parameters")]
        [Description("Extra sheet parameters by name, e.g. {\"Desenhado por\": \"AE\"}; same value rules as set_parameters.")]
        public Dictionary<string, JToken> Parameters { get; set; }

        [JsonProperty("views")]
        [Description("Views or schedules to place on the new sheet.")]
        public List<SheetViewPlacement> Views { get; set; }
    }

    public sealed class CreateSheetsArgs
    {
        [JsonProperty("items", Required = Required.Always)]
        [Description("Sheets to create, up to 200 per call.")]
        public List<SheetItem> Items { get; set; }

        [JsonProperty("continueOnError")]
        [Description("false (default): any failure rolls back every sheet of the call. true: failed sheets are skipped and listed in failed[].")]
        [DefaultValue(false)]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>FOL-03: batch sheet creation.</summary>
    [McpTool("create_sheets", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "sheets",
        Description = "Create sheets in batch (real or placeholder): number, name, title block (\"Family: Type\", " +
                      "default title block if omitted), extra parameters and optional views/schedules placed at x/y " +
                      "in mm. Numbers are checked for forbidden characters and collisions (also within the request) " +
                      "before anything is created; names are read back. One transaction (one Ctrl+Z); all or nothing " +
                      "unless continueOnError. Undo: Ctrl+Z, or delete the returned sheet ids.")]
    public sealed class CreateSheetsTool : McpTool<CreateSheetsArgs>
    {
        public const int MaxItems = 200;

        protected override void Validate(CreateSheetsArgs args)
        {
            if (args.Items == null || args.Items.Count == 0)
                throw new ToolException("INVALID_ARGUMENTS", "items must list at least one sheet.");
            if (args.Items.Count > MaxItems)
                throw new ToolException("TOO_MANY_ITEMS", $"{args.Items.Count} sheets requested; the limit is {MaxItems} per call.");
            if (args.Items.Any(i => i == null))
                throw new ToolException("INVALID_ARGUMENTS", "items must not contain null.");
            var dup = NameRules.DuplicatesIn(args.Items.Select(i => i.Number));
            if (dup.Count > 0)
                throw new ToolException("NAME_COLLISION", "Sheet number(s) repeated in the request: " + string.Join(", ", dup) + ".");
            foreach (var i in args.Items)
            {
                if (i.Placeholder && ((i.Views?.Count ?? 0) > 0 || (!string.IsNullOrWhiteSpace(i.TitleBlock) && !IsNone(i.TitleBlock))))
                    throw new ToolException("INVALID_ARGUMENTS", $"Placeholder sheet '{i.Number}' cannot have a title block or views.");
            }
        }

        protected override object Run(ToolContext ctx, CreateSheetsArgs args)
        {
            var doc = ctx.Doc;
            var services = ctx.Services;
            var created = new List<object>();
            var failed = new List<object>();
            var defaultTitleBlock = DefaultTitleBlock(doc);

            for (var index = 0; index < args.Items.Count; index++)
            {
                var item = args.Items[index];
                if (!args.ContinueOnError)
                {
                    try
                    {
                        created.Add(CreateOne(doc, services, item, index, defaultTitleBlock));
                    }
                    catch (ToolException ex)
                    {
                        throw new ToolException(ex.Code, $"Sheet [{index}] '{item.Number}': {ex.Message} No sheet was created.",
                            ex.Hint ?? "Fix this item, or pass continueOnError:true to create the others.", ex.RecommendedTool);
                    }
                    continue;
                }

                // continueOnError: each sheet in its own sub-transaction so a failure leaves nothing behind.
                using (var sub = new SubTransaction(doc))
                {
                    sub.Start();
                    try
                    {
                        created.Add(CreateOne(doc, services, item, index, defaultTitleBlock));
                        sub.Commit();
                    }
                    catch (Exception ex) when (ex is ToolException || ex is Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        sub.RollBack();
                        failed.Add(new
                        {
                            index,
                            number = item.Number,
                            code = (ex as ToolException)?.Code ?? "REVIT_ERROR",
                            message = ex.Message,
                            hint = (ex as ToolException)?.Hint
                        });
                    }
                }
            }

            return new
            {
                created,
                failed,
                createdCount = created.Count,
                failedCount = failed.Count,
                dryRun = ctx.DryRun
            };
        }

        private static object CreateOne(Document doc, ToolServices services, SheetItem item, int index, ElementId defaultTitleBlock)
        {
            services.Names.EnsureSheetNumberFree(item.Number);
            services.Names.Validate(item.Name, "sheet name");

            ElementId titleBlockId = ElementId.InvalidElementId;
            if (!item.Placeholder)
            {
                if (string.IsNullOrWhiteSpace(item.TitleBlock))
                {
                    titleBlockId = defaultTitleBlock;
                    if (titleBlockId == ElementId.InvalidElementId && new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                            .OfCategory(BuiltInCategory.OST_TitleBlocks).GetElementCount() > 0)
                        throw new ToolException("TITLE_BLOCK_REQUIRED",
                            "The document has several title blocks and no default one.",
                            "Pass titleBlock as \"Family: Type\" (or \"none\").");
                }
                else if (!IsNone(item.TitleBlock))
                    titleBlockId = services.Lookup.FamilySymbol(item.TitleBlock, BuiltInCategory.OST_TitleBlocks).Id;
            }

            // Resolve views before creating anything.
            var placements = new List<(View view, SheetViewPlacement p)>();
            foreach (var p in item.Views ?? new List<SheetViewPlacement>())
            {
                var view = services.Lookup.View(p.View);
                if (view is ViewSheet)
                    throw new ToolException("VIEW_NOT_PLACEABLE", $"'{view.Name}' is a sheet and cannot be placed on a sheet.");
                placements.Add((view, p));
            }

            var sheet = item.Placeholder ? ViewSheet.CreatePlaceholder(doc) : ViewSheet.Create(doc, titleBlockId);
            services.Names.ApplySheetNumber(sheet, item.Number);
            services.Names.ApplyName(sheet, item.Name, "sheet name");

            object parameterResult = null;
            if (item.Parameters != null && item.Parameters.Count > 0)
            {
                var requests = item.Parameters.Select(kv => new ParameterWriteRequest
                {
                    Element = sheet.Id.GetValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Parameter = ParameterRef.ByName(kv.Key),
                    Value = kv.Value
                }).ToList();
                var plan = services.Parameters.Plan(requests, new ParameterPlanOptions { Target = ParameterTarget.Instance });
                if (plan.HasErrors)
                    throw new ToolException("PARAMETER_WRITE_REFUSED", "Sheet parameters are invalid: " + plan.FailureSummary());
                var written = services.Parameters.Apply(plan);
                if (written.Failed > 0)
                    throw new ToolException("PARAMETER_WRITE_FAILED", "Sheet parameters did not stick: " +
                        string.Join(" ", written.Items.Where(i => i.Status != "ok").Select(i => $"'{i.Parameter?.Name}': {i.Status}")));
                parameterResult = written.Items.Select(i => new { parameter = i.Parameter, value = i.New, unit = i.Unit }).ToList();
            }

            var viewports = new List<object>();
            foreach (var (view, p) in placements)
            {
                var point = new XYZ(UnitPolicy.ToInternal(p.X, UnitSpec.Length), UnitPolicy.ToInternal(p.Y, UnitSpec.Length), 0);
                if (view is ViewSchedule schedule)
                {
                    var instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, point);
                    viewports.Add(new { view = view.Name, viewId = view.Id.GetValue(), scheduleInstanceId = instance.Id.GetValue() });
                    continue;
                }
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    throw new ToolException("VIEW_NOT_PLACEABLE",
                        $"View '{view.Name}' cannot be placed on a sheet (already on another sheet, a template, or an unsupported view type).",
                        "Duplicate the view first, or remove it from its current sheet.");
                var viewport = Viewport.Create(doc, sheet.Id, view.Id, point);
                viewports.Add(new { view = view.Name, viewId = view.Id.GetValue(), viewportId = viewport.Id.GetValue() });
            }

            var tb = titleBlockId != ElementId.InvalidElementId ? doc.GetElement(titleBlockId) as ElementType : null;
            return new
            {
                index,
                sheetId = sheet.Id.GetValue(),
                uniqueId = sheet.UniqueId,
                number = sheet.SheetNumber,
                name = sheet.Name,
                placeholder = sheet.IsPlaceholder,
                titleBlock = tb == null ? null : LookupService.FullName(tb),
                parameters = parameterResult,
                viewports
            };
        }

        private static ElementId DefaultTitleBlock(Document doc)
        {
            try
            {
                var id = doc.GetDefaultFamilyTypeId(new ElementId(BuiltInCategory.OST_TitleBlocks));
                if (id != null && id != ElementId.InvalidElementId && doc.GetElement(id) is FamilySymbol) return id;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // fall through
            }
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks).ToElementIds().ToList();
            return symbols.Count == 1 ? symbols[0] : ElementId.InvalidElementId;
        }

        private static bool IsNone(string text) =>
            string.Equals(text?.Trim(), "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text?.Trim(), "nenhum", StringComparison.OrdinalIgnoreCase);
    }
}
