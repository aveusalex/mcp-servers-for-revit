using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Sheets.Support;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Sheets
{
    // ---- FOL-01 ------------------------------------------------------------------------------------------------------

    public sealed class ListSheetsArgs
    {
        [JsonProperty("numberContains")]
        [Description("Only sheets whose number contains this text (e.g. \"A-1\").")]
        public string NumberContains { get; set; }

        [JsonProperty("nameContains")]
        [Description("Only sheets whose name contains this text (case and accents ignored).")]
        public string NameContains { get; set; }

        [JsonProperty("includePlaceholders")]
        [Description("Include placeholder sheets (default true).")]
        [DefaultValue(true)]
        public bool IncludePlaceholders { get; set; } = true;

        [JsonProperty("includeViewports")]
        [Description("List each sheet's viewports and schedules (default false: counts only).")]
        [DefaultValue(false)]
        public bool IncludeViewports { get; set; }

        [JsonProperty("includeRevisions")]
        [Description("List each sheet's revisions (default false).")]
        [DefaultValue(false)]
        public bool IncludeRevisions { get; set; }

        [JsonProperty("limit")]
        [Description("Page size, 1-1000 (default 200).")]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Sheets to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    [McpTool("list_sheets", Kind = ToolKind.Read, Toolset = "sheets", Idempotent = true,
        Description = "List sheets in natural number order (A-2 before A-10): id, number, name, placeholder flag, title " +
                      "block, current revision, viewport and schedule counts; optionally the viewports/schedules " +
                      "(includeViewports) and revisions (includeRevisions). Filter by numberContains / nameContains; " +
                      "paged with limit/offset. Read-only.")]
    public sealed class ListSheetsTool : McpTool<ListSheetsArgs>
    {
        protected override object Run(ToolContext ctx, ListSheetsArgs args)
        {
            var doc = ctx.Doc;
            IEnumerable<ViewSheet> sheets = SheetSupport.AllSheets(doc);
            if (!args.IncludePlaceholders) sheets = sheets.Where(s => !s.IsPlaceholder);
            if (!string.IsNullOrWhiteSpace(args.NumberContains))
                sheets = sheets.Where(s => (s.SheetNumber ?? string.Empty).IndexOf(args.NumberContains.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(args.NameContains))
            {
                var key = TextMatch.Fold(args.NameContains);
                sheets = sheets.Where(s => TextMatch.Fold(s.Name).Contains(key));
            }
            var page = Paging.Page(sheets.OrderBy(s => s.SheetNumber, new SheetSupport.NaturalComparer()).ToList(), args.Limit, args.Offset, 1000);

            var titleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().ToLookup(f => f.OwnerViewId);
            var schedules = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                .Where(s => !s.IsTitleblockRevisionSchedule).ToLookup(s => s.OwnerViewId);

            var items = page.Items.Select(s =>
            {
                var viewportIds = s.GetAllViewports();
                var tb = titleBlocks[s.Id].FirstOrDefault();
                return new
                {
                    sheetId = s.Id.GetValue(),
                    number = s.SheetNumber,
                    name = s.Name,
                    placeholder = s.IsPlaceholder,
                    titleBlock = tb == null ? null : tb.Symbol.FamilyName + ": " + tb.Symbol.Name,
                    currentRevision = s.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString(),
                    viewportCount = viewportIds.Count,
                    scheduleCount = schedules[s.Id].Count(),
                    viewports = args.IncludeViewports
                        ? viewportIds.Select(id => doc.GetElement(id) as Viewport).Where(v => v != null).Select(v =>
                        {
                            var view = doc.GetElement(v.ViewId) as View;
                            return new { viewportId = v.Id.GetValue(), viewId = v.ViewId.GetValue(), view = view?.Name, viewType = view?.ViewType.ToString() };
                        }).ToList()
                        : null,
                    schedules = args.IncludeViewports
                        ? schedules[s.Id].Select(i => new { instanceId = i.Id.GetValue(), scheduleId = i.ScheduleId.GetValue(), schedule = doc.GetElement(i.ScheduleId)?.Name }).ToList()
                        : null,
                    revisions = args.IncludeRevisions
                        ? s.GetAllRevisionIds().Select(id => doc.GetElement(id) as Revision).Where(r => r != null).Select(r => new
                        {
                            revisionId = r.Id.GetValue(),
                            sequence = r.SequenceNumber,
                            numberOnSheet = s.GetRevisionNumberOnSheet(r.Id),
                            description = r.Description,
                            date = r.RevisionDate
                        }).ToList()
                        : null
                };
            }).ToList();
            return new { sheets = items, total = page.Total, limit = page.Limit, offset = page.Offset, truncated = page.Truncated };
        }
    }

    // ---- FOL-02 ------------------------------------------------------------------------------------------------------

    public sealed class GetSheetLayoutArgs
    {
        [JsonProperty("sheet", Required = Required.Always)]
        [Description("Sheet number, name or id.")]
        public string Sheet { get; set; }

        [JsonProperty("includeParameters")]
        [Description("Include the sheet's and title block's parameters (compact; default false).")]
        [DefaultValue(false)]
        public bool IncludeParameters { get; set; }
    }

    [McpTool("get_sheet_layout", Kind = ToolKind.Read, Toolset = "sheets", Idempotent = true,
        Description = "Layout of one sheet in paper mm (sheet coordinates, the same ones place_on_sheet and create_sheets " +
                      "use): title block with its extents and paper size, each viewport with view, scale, box centre, " +
                      "box outline, label outline, rotation, viewport type, detail number and title on sheet, and each " +
                      "schedule instance with its insertion point and extents. Optionally sheet and title block " +
                      "parameters. Read-only.")]
    public sealed class GetSheetLayoutTool : McpTool<GetSheetLayoutArgs>
    {
        protected override object Run(ToolContext ctx, GetSheetLayoutArgs args)
        {
            var doc = ctx.Doc;
            var sheet = SheetSupport.Sheet(ctx, args.Sheet);
            var titleBlocks = SheetSupport.TitleBlocks(doc, sheet.Id).Select(tb => new
            {
                instanceId = tb.Id.GetValue(),
                type = tb.Symbol.FamilyName + ": " + tb.Symbol.Name,
                extents = SheetSupport.Rect(tb.get_BoundingBox(sheet)),
                paperWidth = Length(tb, BuiltInParameter.SHEET_WIDTH),
                paperHeight = Length(tb, BuiltInParameter.SHEET_HEIGHT),
                parameters = args.IncludeParameters ? Params(ctx, tb) : null
            }).ToList();

            var viewports = sheet.GetAllViewports().Select(id => doc.GetElement(id) as Viewport).Where(v => v != null).Select(vp =>
            {
                var view = doc.GetElement(vp.ViewId) as View;
                Outline label = null;
                try { label = vp.GetLabelOutline(); } catch { /* no label */ }
                return new
                {
                    viewportId = vp.Id.GetValue(),
                    viewId = vp.ViewId.GetValue(),
                    view = view?.Name,
                    viewType = view?.ViewType.ToString(),
                    scale = view == null ? null : ViewSupport.SafeScale(view),
                    center = Mm.Point2(vp.GetBoxCenter()),
                    box = SheetSupport.Rect(vp.GetBoxOutline()),
                    label = SheetSupport.Rect(label),
                    rotation = vp.Rotation.ToString(),
                    viewportType = doc.GetElement(vp.GetTypeId())?.Name,
                    detailNumber = vp.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.AsString(),
                    titleOnSheet = view?.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString()
                };
            }).ToList();

            var schedules = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                .Where(s => !s.IsTitleblockRevisionSchedule).Select(s => new
                {
                    instanceId = s.Id.GetValue(),
                    scheduleId = s.ScheduleId.GetValue(),
                    schedule = doc.GetElement(s.ScheduleId)?.Name,
                    point = Mm.Point2(s.Point),
                    extents = SheetSupport.Rect(s.get_BoundingBox(sheet)),
                    rotation = s.Rotation.ToString()
                }).ToList();

            return new
            {
                sheet = new { sheetId = sheet.Id.GetValue(), number = sheet.SheetNumber, name = sheet.Name, placeholder = sheet.IsPlaceholder },
                units = "paper mm, sheet coordinates",
                titleBlocks,
                viewports,
                schedules,
                parameters = args.IncludeParameters ? Params(ctx, sheet) : null
            };
        }

        private static double? Length(Element e, BuiltInParameter bip)
        {
            var p = e.get_Parameter(bip);
            return p != null && p.HasValue && p.StorageType == StorageType.Double ? Mm.FromFt(p.AsDouble()) : (double?)null;
        }

        private static object Params(ToolContext ctx, Element e) =>
            e.Parameters.Cast<Parameter>().Where(p => p.HasValue)
                .OrderBy(p => p.Definition?.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => ctx.Services.Parameters.Describe(p, "instance", true)).ToList();
    }
}
