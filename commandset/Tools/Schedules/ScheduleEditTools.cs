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
    // ---- modify_schedule_fields (TAB-06) ------------------------------------------------------------------------

    public sealed class ScheduleFieldOperation
    {
        [JsonProperty("op", Required = Required.Always)]
        [Description("add (a schedulable field), remove, move (to index) or update (heading, hidden, width, alignment, totals).")]
        [EnumValues("add", "remove", "move", "update")]
        public string Op { get; set; }

        [JsonProperty("field", Required = Required.Always)]
        [Description("add: {builtIn}/{guid}/{name} of a schedulable field (see get_schedulable_fields). Others: {name: column heading or field name} | {builtIn} | {guid} of a field in the schedule.")]
        public ParameterRef Field { get; set; }

        [JsonProperty("fieldType")]
        [Description("add only: disambiguates fields with the same parameter (Instance, ElementType/type, Room, Material...).")]
        public string FieldType { get; set; }

        [JsonProperty("index")]
        [Description("add/move: 0-based position among all fields (hidden included). add without index appends.")]
        public int? Index { get; set; }

        [JsonProperty("heading")]
        [Description("New column heading.")]
        public string Heading { get; set; }

        [JsonProperty("hidden")]
        [Description("Hide or show the column.")]
        public bool? Hidden { get; set; }

        [JsonProperty("widthMm")]
        [Description("Column width on the sheet (paper size).")]
        [Unit("mm")]
        public double? WidthMm { get; set; }

        [JsonProperty("align")]
        [Description("Horizontal alignment.")]
        [EnumValues("left", "center", "right")]
        public string Align { get; set; }

        [JsonProperty("total")]
        [Description("Show totals for the column (numeric fields only).")]
        public bool? Total { get; set; }

        internal ScheduleFieldSpec Format() => new ScheduleFieldSpec
        {
            Parameter = Field, FieldType = FieldType, Heading = Heading, Hidden = Hidden, WidthMm = WidthMm, Align = Align, Total = Total
        };
    }

    public sealed class ModifyScheduleFieldsArgs
    {
        [JsonProperty("schedule", Required = Required.Always)]
        [Description("Schedule name or id.")]
        public string Schedule { get; set; }

        [JsonProperty("operations", Required = Required.Always)]
        [Description("Operations applied in order, all or nothing (up to 100).")]
        public List<ScheduleFieldOperation> Operations { get; set; }
    }

    /// <summary>TAB-06. Calculated fields cannot be created through the Revit API (no SetFormula), so they are not offered.</summary>
    [McpTool("modify_schedule_fields", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "schedules",
        Description = "Edit the columns of a schedule with ordered operations: add a schedulable field (builtIn/guid/" +
                      "name, optional index), remove, move to an index, or update heading, hidden, width (mm), " +
                      "alignment and totals. All operations succeed or none is applied; unknown fields fail with " +
                      "suggestions instead of being ignored. Calculated fields cannot be created via the API. Returns the " +
                      "fields read back. Undo: Ctrl+Z.")]
    public sealed class ModifyScheduleFieldsTool : McpTool<ModifyScheduleFieldsArgs>
    {
        protected override void Validate(ModifyScheduleFieldsArgs args)
        {
            if (args.Operations == null || args.Operations.Count == 0)
                throw new ToolException("INVALID_ARGUMENTS", "operations must list at least one operation.");
            if (args.Operations.Count > 100)
                throw new ToolException("INVALID_ARGUMENTS", "At most 100 operations per call.");
            foreach (var o in args.Operations)
            {
                if (o == null || o.Field == null || o.Field.IsEmpty)
                    throw new ToolException("INVALID_ARGUMENTS", "Every operation needs a field.");
                o.Op = (o.Op ?? "").Trim().ToLowerInvariant();
                if (o.Op == "move" && !o.Index.HasValue)
                    throw new ToolException("INVALID_ARGUMENTS", $"move of '{o.Field}' needs index.");
                if (o.Op == "update" && o.Heading == null && !o.Hidden.HasValue && !o.WidthMm.HasValue &&
                    string.IsNullOrWhiteSpace(o.Align) && !o.Total.HasValue)
                    throw new ToolException("INVALID_ARGUMENTS", $"update of '{o.Field}' changes nothing.");
                ScheduleSupport.ParseFieldType(o.FieldType);
            }
        }

        protected override object Run(ToolContext ctx, ModifyScheduleFieldsArgs args)
        {
            var doc = ctx.Doc;
            var schedule = ScheduleSupport.Find(ctx, args.Schedule);
            var def = schedule.Definition;
            var applied = new List<object>();

            for (var i = 0; i < args.Operations.Count; i++)
            {
                var o = args.Operations[i];
                try
                {
                    applied.Add(Apply(doc, def, o));
                }
                catch (ToolException ex)
                {
                    throw new ToolException(ex.Code, $"Operation [{i}] {o.Op} '{o.Field}': {ex.Message} Nothing was changed.", ex.Hint, ex.RecommendedTool);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    throw new ToolException("REVIT_REJECTED", $"Operation [{i}] {o.Op} '{o.Field}': {ex.Message} Nothing was changed.");
                }
            }

            doc.Regenerate();
            var fields = Enumerable.Range(0, def.GetFieldCount())
                .Select(k => ScheduleSupport.DescribeField(doc, def, def.GetField(k), ctx.Services.Units)).ToList();
            return new { scheduleId = schedule.Id.GetValue(), name = schedule.Name, applied, fields };
        }

        private static object Apply(Document doc, ScheduleDefinition def, ScheduleFieldOperation o)
        {
            switch (o.Op)
            {
                case "add":
                {
                    var sf = ScheduleSupport.ResolveSchedulable(doc, def.GetSchedulableFields(), o.Field, ScheduleSupport.ParseFieldType(o.FieldType));
                    var count = def.GetFieldCount();
                    if (o.Index.HasValue && (o.Index.Value < 0 || o.Index.Value > count))
                        throw new ToolException("INVALID_ARGUMENTS", $"index must be between 0 and {count}.");
                    var field = o.Index.HasValue ? def.InsertField(sf, o.Index.Value) : def.AddField(sf);
                    ScheduleSupport.ApplyFieldFormat(field, o.Format());
                    return new { op = "add", heading = field.ColumnHeading, index = field.FieldIndex };
                }
                case "remove":
                {
                    var field = ScheduleSupport.ResolveExisting(doc, def, o.Field);
                    var heading = field.ColumnHeading;
                    def.RemoveField(field.FieldId);
                    return new { op = "remove", heading };
                }
                case "move":
                {
                    var field = ScheduleSupport.ResolveExisting(doc, def, o.Field);
                    var order = def.GetFieldOrder().ToList();
                    var index = o.Index.Value;
                    if (index < 0 || index >= order.Count)
                        throw new ToolException("INVALID_ARGUMENTS", $"index must be between 0 and {order.Count - 1}.");
                    order.RemoveAll(id => id == field.FieldId);
                    order.Insert(index, field.FieldId);
                    def.SetFieldOrder(order);
                    return new { op = "move", heading = field.ColumnHeading, index = def.GetField(field.FieldId).FieldIndex };
                }
                case "update":
                {
                    var field = ScheduleSupport.ResolveExisting(doc, def, o.Field);
                    ScheduleSupport.ApplyFieldFormat(field, o.Format());
                    return new { op = "update", heading = field.ColumnHeading, hidden = field.IsHidden };
                }
                default:
                    throw new ToolException("INVALID_ARGUMENTS", $"Unknown op '{o.Op}'.");
            }
        }
    }

    // ---- set_schedule_filters_sorting (TAB-07) ------------------------------------------------------------------

    public sealed class ScheduleAppearance
    {
        [JsonProperty("showTitle")]
        [Description("Show the title row.")]
        public bool? ShowTitle { get; set; }

        [JsonProperty("showHeaders")]
        [Description("Show the column headings.")]
        public bool? ShowHeaders { get; set; }

        [JsonProperty("gridLines")]
        [Description("Show grid lines in the body.")]
        public bool? GridLines { get; set; }
    }

    public sealed class SetScheduleFiltersSortingArgs
    {
        [JsonProperty("schedule", Required = Required.Always)]
        [Description("Schedule name or id.")]
        public string Schedule { get; set; }

        [JsonProperty("filters")]
        [Description("Filters (AND, at most 8 in total) over fields of the schedule (hidden ones included). Omit to leave filters as they are; [] with mode replace clears them.")]
        public List<ScheduleFilterSpec> Filters { get; set; }

        [JsonProperty("sort")]
        [Description("Sorting/grouping levels. Omit to leave sorting as it is; [] with mode replace clears it.")]
        public List<ScheduleSortSpec> Sort { get; set; }

        [JsonProperty("mode")]
        [Description("replace (default): the given lists replace the current ones. append: added after the current ones.")]
        [EnumValues("replace", "append")]
        [DefaultValue("replace")]
        public string Mode { get; set; } = "replace";

        [JsonProperty("itemized")]
        [Description("Itemize every instance (true) or group identical rows (false).")]
        public bool? Itemized { get; set; }

        [JsonProperty("grandTotal")]
        [Description("Show the grand total row.")]
        public bool? GrandTotal { get; set; }

        [JsonProperty("appearance")]
        [Description("Title, headers and grid-line visibility.")]
        public ScheduleAppearance Appearance { get; set; }

        [JsonProperty("newName")]
        [Description("Rename the schedule (must be unique among schedules).")]
        public string NewName { get; set; }
    }

    /// <summary>TAB-07.</summary>
    [McpTool("set_schedule_filters_sorting", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "schedules",
        Description = "Set a schedule's filters (eq, neq, gt/gte/lt/lte with numbers in mm/m2/m3/deg, contains, " +
                      "begins_with, ends_with and negations, has_value/has_no_value; at most 8), sorting/grouping " +
                      "(asc/desc, header, footer, blank line), itemization, grand total, title/header/grid-line " +
                      "visibility and name. mode replace (default) or append. Filters and sorting only use fields " +
                      "already in the schedule (add hidden ones with modify_schedule_fields). Returns the definition " +
                      "read back. Undo: Ctrl+Z.")]
    public sealed class SetScheduleFiltersSortingTool : McpTool<SetScheduleFiltersSortingArgs>
    {
        protected override void Validate(SetScheduleFiltersSortingArgs args)
        {
            args.Mode = (args.Mode ?? "replace").Trim().ToLowerInvariant();
            ScheduleSupport.ValidateFilterSpecs(args.Filters);
            if (args.Filters == null && args.Sort == null && !args.Itemized.HasValue && !args.GrandTotal.HasValue &&
                args.Appearance == null && string.IsNullOrWhiteSpace(args.NewName))
                throw new ToolException("INVALID_ARGUMENTS", "Nothing to change: give filters, sort, itemized, grandTotal, appearance or newName.");
            if (!string.IsNullOrWhiteSpace(args.NewName)) NameRules.Validate(args.NewName, "schedule name");
        }

        protected override object Run(ToolContext ctx, SetScheduleFiltersSortingArgs args)
        {
            var doc = ctx.Doc;
            var schedule = ScheduleSupport.Find(ctx, args.Schedule);
            var def = schedule.Definition;

            if (args.Filters != null)
            {
                if (args.Mode == "replace") def.ClearFilters();
                if (def.GetFilterCount() + args.Filters.Count > ScheduleSupport.MaxFilters)
                    throw new ToolException("TOO_MANY_FILTERS",
                        $"The schedule already has {def.GetFilterCount()} filter(s); with {args.Filters.Count} more it would exceed {ScheduleSupport.MaxFilters}.",
                        "Use mode replace, or fewer filters.");
                foreach (var f in args.Filters) ScheduleSupport.AddFilter(ctx, def, f);
            }

            if (args.Sort != null)
            {
                if (args.Mode == "replace") def.ClearSortGroupFields();
                foreach (var s in args.Sort) def.AddSortGroupField(ScheduleSupport.BuildSort(doc, def, s));
            }

            if (args.Itemized.HasValue) def.IsItemized = args.Itemized.Value;
            if (args.GrandTotal.HasValue) def.ShowGrandTotal = args.GrandTotal.Value;
            if (args.Appearance != null)
            {
                if (args.Appearance.ShowTitle.HasValue) def.ShowTitle = args.Appearance.ShowTitle.Value;
                if (args.Appearance.ShowHeaders.HasValue) def.ShowHeaders = args.Appearance.ShowHeaders.Value;
                if (args.Appearance.GridLines.HasValue) def.ShowGridLines = args.Appearance.GridLines.Value;
            }

            if (!string.IsNullOrWhiteSpace(args.NewName) && !string.Equals(args.NewName, schedule.Name, StringComparison.Ordinal))
            {
                ctx.Services.Names.EnsureViewNameFree(args.NewName, ViewType.Schedule, false, schedule.Id);
                ctx.Services.Names.ApplyName(schedule, args.NewName, "schedule name");
            }

            doc.Regenerate();
            return GetScheduleDefinitionTool.Describe(ctx, schedule);
        }
    }
}
