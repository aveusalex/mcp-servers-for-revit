using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Schedules.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Schedules
{
    // ---- list_schedules (TAB-01) --------------------------------------------------------------------------------

    public sealed class ListSchedulesArgs
    {
        [JsonProperty("kind")]
        [Description("Only schedules of this kind.")]
        [EnumValues("regular", "material_takeoff", "key", "sheet_list", "view_list", "revision", "note_block", "keynote_legend")]
        public string Kind { get; set; }

        [JsonProperty("category")]
        [Description("Only schedules of this category, in any language (OST_Doors, Doors, Portas).")]
        public string Category { get; set; }

        [JsonProperty("nameContains")]
        [Description("Only schedules whose name contains this text (case and accents ignored).")]
        public string NameContains { get; set; }

        [JsonProperty("includePlacement")]
        [Description("List the sheets each schedule is placed on (default true).")]
        [DefaultValue(true)]
        public bool IncludePlacement { get; set; } = true;

        [JsonProperty("limit")]
        [Description("Page size, 1-1000 (default 200).")]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Schedules to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>TAB-01.</summary>
    [McpTool("list_schedules", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "schedules", Idempotent = true,
        Description = "List the schedules of the document (regular, material takeoff, key, sheet/view lists, note " +
                      "blocks) with id, name, kind, category, field count and the sheets they are placed on. Internal " +
                      "title-block revision schedules and schedule templates are excluded. Filter by kind, category " +
                      "(any language) or name; paged with limit/offset. Read-only.")]
    public sealed class ListSchedulesTool : McpTool<ListSchedulesArgs>
    {
        protected override object Run(ToolContext ctx, ListSchedulesArgs args)
        {
            var doc = ctx.Doc;
            var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !ScheduleSupport.IsInternal(s));

            if (!string.IsNullOrWhiteSpace(args.Kind))
                schedules = schedules.Where(s => ScheduleSupport.Kind(s) == args.Kind.Trim().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(args.Category))
            {
                var cat = ctx.Services.Categories.Resolve(args.Category);
                schedules = schedules.Where(s => s.Definition.CategoryId == cat.Id);
            }
            if (!string.IsNullOrWhiteSpace(args.NameContains))
            {
                var key = TextMatch.Key(args.NameContains);
                schedules = schedules.Where(s => TextMatch.Key(s.Name).Contains(key));
            }

            var placements = new Dictionary<ElementId, List<object>>();
            if (args.IncludePlacement)
            {
                foreach (ScheduleSheetInstance inst in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)))
                {
                    if (inst.IsTitleblockRevisionSchedule) continue;
                    var sheet = doc.GetElement(inst.OwnerViewId) as ViewSheet;
                    if (sheet == null) continue;
                    if (!placements.TryGetValue(inst.ScheduleId, out var list))
                        placements[inst.ScheduleId] = list = new List<object>();
                    list.Add(new { sheetId = sheet.Id.GetValue(), number = sheet.SheetNumber, name = sheet.Name, instanceId = inst.Id.GetValue() });
                }
            }

            var page = Paging.Page(schedules.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                args.Limit, args.Offset, 1000);
            var items = page.Items.Select(s => new
            {
                id = s.Id.GetValue(),
                name = s.Name,
                kind = ScheduleSupport.Kind(s),
                category = ScheduleSupport.CategoryName(doc, s.Definition.CategoryId),
                categoryBuiltIn = ScheduleSupport.CategoryBuiltIn(doc, s.Definition.CategoryId),
                fieldCount = s.Definition.GetFieldCount(),
                itemized = s.Definition.IsItemized,
                viewTemplateId = s.ViewTemplateId == ElementId.InvalidElementId ? (long?)null : s.ViewTemplateId.GetValue(),
                sheets = args.IncludePlacement
                    ? (placements.TryGetValue(s.Id, out var p) ? p : new List<object>())
                    : null
            }).ToList();

            return new { items, total = page.Total, limit = page.Limit, offset = page.Offset, truncated = page.Truncated };
        }
    }

    // ---- get_schedule_data (TAB-02) -----------------------------------------------------------------------------

    public sealed class GetScheduleDataArgs
    {
        [JsonProperty("schedule", Required = Required.Always)]
        [Description("Schedule name or id (see list_schedules).")]
        public string Schedule { get; set; }

        [JsonProperty("includeHeadings")]
        [Description("Also return the heading rows (column headings and heading groups) as Revit draws them. Default false: columns[] already names each column.")]
        [DefaultValue(false)]
        public bool IncludeHeadings { get; set; }

        [JsonProperty("skipBlankRows")]
        [Description("Leave out blank separator rows (default true).")]
        [DefaultValue(true)]
        public bool SkipBlankRows { get; set; } = true;

        [JsonProperty("limit")]
        [Description("Rows per page, 1-2000 (default 200).")]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Rows to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>TAB-02.</summary>
    [McpTool("get_schedule_data", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "schedules", Idempotent = true,
        Description = "Read what a schedule shows: the visible columns and the rows as Revit renders them (cell text " +
                      "with the project's formatting and units, calculated values included; hidden fields are left " +
                      "out). Group headers/footers and the grand total come as ordinary rows, in table order. Paged " +
                      "with limit/offset over the body rows; use export_schedule for a CSV file. Read-only.")]
    public sealed class GetScheduleDataTool : McpTool<GetScheduleDataArgs>
    {
        protected override object Run(ToolContext ctx, GetScheduleDataArgs args)
        {
            var schedule = ScheduleSupport.Find(ctx, args.Schedule);
            var table = ScheduleTable.Read(schedule);
            IEnumerable<ScheduleRow> rows = table.Rows;
            if (args.SkipBlankRows) rows = rows.Where(r => r.Kind != "blank");
            var page = Paging.Page(rows.ToList(), args.Limit, args.Offset, 2000);
            return new
            {
                scheduleId = schedule.Id.GetValue(),
                name = schedule.Name,
                kind = ScheduleSupport.Kind(schedule),
                title = table.Title,
                columns = table.Columns,
                headings = args.IncludeHeadings ? table.HeadingRows.Select(r => r.Cells).ToList() : null,
                rows = page.Items.Select(r => r.Cells).ToList(),
                rowIndexes = page.Items.Select(r => r.Index).ToList(),
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }
    }

    // ---- get_schedulable_fields (TAB-04) ------------------------------------------------------------------------

    public sealed class GetSchedulableFieldsArgs
    {
        [JsonProperty("schedule")]
        [Description("Existing schedule (name or id). Give this, or category (+ kind) for a schedule not created yet.")]
        public string Schedule { get; set; }

        [JsonProperty("category")]
        [Description("Category of a schedule not created yet, in any language (OST_Rooms, Rooms, Ambientes). Not needed for sheet_list/view_list.")]
        public string Category { get; set; }

        [JsonProperty("kind")]
        [Description("Kind of the schedule to probe when using category (default regular).")]
        [EnumValues("regular", "material_takeoff", "key", "sheet_list", "view_list")]
        [DefaultValue("regular")]
        public string Kind { get; set; } = "regular";

        [JsonProperty("fieldTypes")]
        [Description("Only these field types, e.g. [\"Instance\", \"ElementType\", \"Room\", \"Material\"].")]
        public List<string> FieldTypes { get; set; }

        [JsonProperty("nameContains")]
        [Description("Only fields whose name contains this text (case and accents ignored).")]
        public string NameContains { get; set; }

        [JsonProperty("limit")]
        [Description("Page size, 1-2000 (default 500).")]
        [DefaultValue(500)]
        public int Limit { get; set; } = 500;

        [JsonProperty("offset")]
        [Description("Fields to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>
    /// TAB-04. For a category without a schedule it creates a temporary schedule inside its own transaction and always
    /// rolls it back (a SubTransaction when the document already has one open), so the model is never changed.
    /// </summary>
    [McpTool("get_schedulable_fields", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "schedules", Idempotent = true,
        Description = "List the fields that can be added to a schedule: name (Revit's language), fieldType (Instance, " +
                      "ElementType, Count, Room, Material...), parameterId, builtIn and guid, so create_schedule and " +
                      "modify_schedule_fields can reference them language-neutrally. Works for an existing schedule " +
                      "or for a category + kind not scheduled yet (probed with a temporary schedule that is always " +
                      "rolled back; nothing is changed). Read-only.")]
    public sealed class GetSchedulableFieldsTool : McpTool<GetSchedulableFieldsArgs>
    {
        protected override void Validate(GetSchedulableFieldsArgs args)
        {
            var hasSchedule = !string.IsNullOrWhiteSpace(args.Schedule);
            var kind = (args.Kind ?? "regular").Trim().ToLowerInvariant();
            args.Kind = kind;
            if (hasSchedule && !string.IsNullOrWhiteSpace(args.Category))
                throw new ToolException("INVALID_ARGUMENTS", "Give schedule or category, not both.");
            if (!hasSchedule && string.IsNullOrWhiteSpace(args.Category) && kind != "sheet_list" && kind != "view_list")
                throw new ToolException("INVALID_ARGUMENTS", "Give schedule (existing) or category (+ kind).");
        }

        protected override object Run(ToolContext ctx, GetSchedulableFieldsArgs args)
        {
            var doc = ctx.Doc;
            List<object> described;
            string source;
            if (!string.IsNullOrWhiteSpace(args.Schedule))
            {
                var schedule = ScheduleSupport.Find(ctx, args.Schedule);
                described = Describe(doc, schedule.Definition.GetSchedulableFields(), args);
                source = "schedule " + schedule.Name;
            }
            else
            {
                ElementId categoryId = ElementId.InvalidElementId;
                if (args.Kind != "sheet_list" && args.Kind != "view_list")
                    categoryId = ctx.Services.Categories.Resolve(args.Category).Id;
                described = Probe(doc, categoryId, args);
                source = "temporary schedule (rolled back)";
            }

            var page = Paging.Page(described, args.Limit, args.Offset, 2000);
            return new { source, items = page.Items, total = page.Total, limit = page.Limit, offset = page.Offset, truncated = page.Truncated };
        }

        private static List<object> Probe(Document doc, ElementId categoryId, GetSchedulableFieldsArgs args)
        {
            if (doc.IsReadOnly)
                throw new ToolException(ToolErrorCodes.DocumentReadOnly,
                    "The document is read-only, so a temporary schedule cannot be probed.",
                    "Pass an existing schedule instead.", "list_schedules");

            if (doc.IsModifiable)
            {
                using (var sub = new SubTransaction(doc))
                {
                    sub.Start();
                    try { return Describe(doc, CreateTemporary(doc, categoryId, args.Kind).Definition.GetSchedulableFields(), args); }
                    finally { if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack(); }
                }
            }

            using (var tx = new Transaction(doc, "MCP: probe schedulable fields (rolled back)"))
            {
                tx.Start();
                try { return Describe(doc, CreateTemporary(doc, categoryId, args.Kind).Definition.GetSchedulableFields(), args); }
                finally { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); }
            }
        }

        internal static ViewSchedule CreateTemporary(Document doc, ElementId categoryId, string kind)
        {
            try
            {
                switch (kind)
                {
                    case "material_takeoff": return ViewSchedule.CreateMaterialTakeoff(doc, categoryId);
                    case "key": return ViewSchedule.CreateKeySchedule(doc, categoryId);
                    case "sheet_list": return ViewSchedule.CreateSheetList(doc);
                    case "view_list": return ViewSchedule.CreateViewList(doc);
                    default: return ViewSchedule.CreateSchedule(doc, categoryId);
                }
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("CATEGORY_NOT_SCHEDULABLE", $"This category cannot have a {kind} schedule: {ex.Message}");
            }
        }

        private static List<object> Describe(Document doc, IList<SchedulableField> fields, GetSchedulableFieldsArgs args)
        {
            IEnumerable<SchedulableField> q = fields;
            if (args.FieldTypes != null && args.FieldTypes.Count > 0)
            {
                var types = new HashSet<ScheduleFieldType>(args.FieldTypes.Select(t => ScheduleSupport.ParseFieldType(t).Value));
                q = q.Where(f => types.Contains(f.FieldType));
            }
            if (!string.IsNullOrWhiteSpace(args.NameContains))
            {
                var key = TextMatch.Key(args.NameContains);
                q = q.Where(f =>
                {
                    try { return TextMatch.Key(f.GetName(doc)).Contains(key); }
                    catch { return false; }
                });
            }
            return q.Select(f => ScheduleSupport.DescribeSchedulable(doc, f)).ToList();
        }
    }

    // ---- get_schedule_definition (TAB-05) -----------------------------------------------------------------------

    public sealed class GetScheduleDefinitionArgs
    {
        [JsonProperty("schedule", Required = Required.Always)]
        [Description("Schedule name or id (see list_schedules).")]
        public string Schedule { get; set; }
    }

    /// <summary>TAB-05. Formulas of calculated fields are not exposed by the Revit API (only isCalculated).</summary>
    [McpTool("get_schedule_definition", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "schedules", Idempotent = true,
        Description = "Full definition of a schedule: kind, category, fields in order (name, heading, fieldType, " +
                      "builtIn/guid, hidden, calculated/combined, width in mm, alignment, totals), filters (op and " +
                      "value in wire units), sorting/grouping (order, header, footer, blank line), itemization, grand " +
                      "totals and title/headers/grid-line flags. Formulas of calculated fields are not readable through " +
                      "the Revit API. Read-only.")]
    public sealed class GetScheduleDefinitionTool : McpTool<GetScheduleDefinitionArgs>
    {
        protected override object Run(ToolContext ctx, GetScheduleDefinitionArgs args)
        {
            var schedule = ScheduleSupport.Find(ctx, args.Schedule);
            return Describe(ctx, schedule);
        }

        internal static object Describe(ToolContext ctx, ViewSchedule schedule)
        {
            var doc = ctx.Doc;
            var def = schedule.Definition;
            var fields = Enumerable.Range(0, def.GetFieldCount())
                .Select(i => ScheduleSupport.DescribeField(doc, def, def.GetField(i), ctx.Services.Units)).ToList();
            var filters = def.GetFilters().Select(f => ScheduleSupport.DescribeFilter(ctx, def, f)).ToList();
            var sorting = def.GetSortGroupFields().Select(s => ScheduleSupport.DescribeSort(def, s)).ToList();
            return new
            {
                scheduleId = schedule.Id.GetValue(),
                name = schedule.Name,
                kind = ScheduleSupport.Kind(schedule),
                category = ScheduleSupport.CategoryName(doc, def.CategoryId),
                categoryBuiltIn = ScheduleSupport.CategoryBuiltIn(doc, def.CategoryId),
                fields,
                filters,
                sorting,
                itemized = def.IsItemized,
                grandTotal = def.ShowGrandTotal,
                grandTotalTitle = def.ShowGrandTotal ? def.GrandTotalTitle : null,
                showTitle = def.ShowTitle,
                showHeaders = def.ShowHeaders,
                showGridLines = def.ShowGridLines,
                includeLinkedFiles = SafeBool(() => def.IncludeLinkedFiles),
                viewTemplateId = schedule.ViewTemplateId == ElementId.InvalidElementId ? (long?)null : schedule.ViewTemplateId.GetValue()
            };
        }

        private static bool? SafeBool(Func<bool> f)
        {
            try { return f(); }
            catch { return null; }
        }
    }
}
