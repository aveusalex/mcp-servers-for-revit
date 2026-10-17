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
    public sealed class CreateScheduleArgs
    {
        [JsonProperty("name", Required = Required.Always)]
        [Description("Schedule name, unique among schedules (e.g. \"Tabela de Portas\").")]
        public string Name { get; set; }

        [JsonProperty("kind")]
        [Description("regular (default), material_takeoff, key, sheet_list or view_list.")]
        [EnumValues("regular", "material_takeoff", "key", "sheet_list", "view_list")]
        [DefaultValue("regular")]
        public string Kind { get; set; } = "regular";

        [JsonProperty("category")]
        [Description("Category in any language (OST_Doors, Doors, Portas). Required except for sheet_list/view_list. \"OST_MultiCategory\" for a multi-category schedule.")]
        public string Category { get; set; }

        [JsonProperty("fields")]
        [Description("Fields in column order. Reference them language-neutrally with builtIn/guid when possible (see get_schedulable_fields).")]
        public List<ScheduleFieldSpec> Fields { get; set; }

        [JsonProperty("filters")]
        [Description("Filters (AND, at most 8) over fields listed in fields[] (hidden ones included).")]
        public List<ScheduleFilterSpec> Filters { get; set; }

        [JsonProperty("sort")]
        [Description("Sorting/grouping levels, in order, over fields listed in fields[].")]
        public List<ScheduleSortSpec> Sort { get; set; }

        [JsonProperty("itemized")]
        [Description("Itemize every instance (default true). false groups identical rows (needs sort).")]
        [DefaultValue(true)]
        public bool Itemized { get; set; } = true;

        [JsonProperty("grandTotal")]
        [Description("Show a grand total row (default false).")]
        [DefaultValue(false)]
        public bool GrandTotal { get; set; }

        [JsonProperty("skipUnknownFields")]
        [Description("false (default): an unknown or ambiguous field fails the call and nothing is created. true: such fields are skipped and listed in skippedFields.")]
        [DefaultValue(false)]
        public bool SkipUnknownFields { get; set; }
    }

    /// <summary>TAB-03 (presets will come from knowledge/ later).</summary>
    [McpTool("create_schedule", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "schedules",
        Description = "Create a schedule (regular, material takeoff, key, sheet list or view list) for a category in " +
                      "any language, with fields in order (heading, hidden, width mm, alignment, totals), filters (eq, " +
                      "neq, gt/gte/lt/lte in mm/m2/m3/deg, contains, begins_with, ends_with, has_value...), " +
                      "sorting/grouping (header, footer, blank line), itemization and grand total. Fields are resolved " +
                      "before anything is created; returns the definition read back. Undo: Ctrl+Z or delete the " +
                      "returned schedule id.")]
    public sealed class CreateScheduleTool : McpTool<CreateScheduleArgs>
    {
        protected override void Validate(CreateScheduleArgs args)
        {
            args.Kind = (args.Kind ?? "regular").Trim().ToLowerInvariant();
            NameRules.Validate(args.Name, "schedule name");
            if (args.Kind != "sheet_list" && args.Kind != "view_list" && string.IsNullOrWhiteSpace(args.Category))
                throw new ToolException("INVALID_ARGUMENTS", $"category is required for a {args.Kind} schedule.");
            if (args.Fields != null && args.Fields.Any(f => f == null || f.Parameter == null || f.Parameter.IsEmpty))
                throw new ToolException("INVALID_ARGUMENTS", "Every field needs a parameter.");
            if (args.Fields != null && args.Fields.Count > 100)
                throw new ToolException("INVALID_ARGUMENTS", "At most 100 fields per schedule.");
            ScheduleSupport.ValidateFilterSpecs(args.Filters);
            if ((args.Fields?.Count ?? 0) == 0 && ((args.Filters?.Count ?? 0) > 0 || (args.Sort?.Count ?? 0) > 0))
                throw new ToolException("INVALID_ARGUMENTS", "filters and sort refer to fields, so fields[] cannot be empty.");
            foreach (var f in args.Fields ?? new List<ScheduleFieldSpec>()) ScheduleSupport.ParseFieldType(f.FieldType);
        }

        protected override object Run(ToolContext ctx, CreateScheduleArgs args)
        {
            var doc = ctx.Doc;
            ctx.Services.Names.EnsureViewNameFree(args.Name, ViewType.Schedule);

            var categoryId = ElementId.InvalidElementId;
            if (args.Kind == "regular" && IsMultiCategory(args.Category))
            {
                // Multi-category schedule: created without a category.
            }
            else if (args.Kind != "sheet_list" && args.Kind != "view_list")
            {
                var cat = ctx.Services.Categories.Resolve(args.Category);
                categoryId = cat.Id;
                if (args.Kind == "regular" && !ViewSchedule.IsValidCategoryForSchedule(categoryId))
                    throw new ToolException("CATEGORY_NOT_SCHEDULABLE", $"'{cat.Name}' cannot be scheduled.");
                if (args.Kind == "material_takeoff" && !ViewSchedule.IsValidCategoryForMaterialTakeoff(categoryId))
                    throw new ToolException("CATEGORY_NOT_SCHEDULABLE", $"'{cat.Name}' cannot have a material takeoff.");
                if (args.Kind == "key" && !ViewSchedule.IsValidCategoryForKeySchedule(categoryId))
                    throw new ToolException("CATEGORY_NOT_SCHEDULABLE", $"'{cat.Name}' cannot have a key schedule.");
            }

            var schedule = GetSchedulableFieldsTool.CreateTemporary(doc, categoryId, args.Kind);
            ctx.Services.Names.ApplyName(schedule, args.Name, "schedule name");
            var def = schedule.Definition;

            // Key schedules and lists come with default fields; replace them when the caller lists fields.
            var skipped = new List<object>();
            var fields = args.Fields ?? new List<ScheduleFieldSpec>();
            if (fields.Count > 0)
            {
                var available = def.GetSchedulableFields();
                var resolved = new List<(ScheduleFieldSpec spec, SchedulableField field)>();
                foreach (var spec in fields)
                {
                    try
                    {
                        resolved.Add((spec, ScheduleSupport.ResolveSchedulable(doc, available, spec.Parameter, ScheduleSupport.ParseFieldType(spec.FieldType))));
                    }
                    catch (ToolException ex) when (args.SkipUnknownFields && (ex.Code == "FIELD_NOT_SCHEDULABLE" || ex.Code == "AMBIGUOUS_FIELD"))
                    {
                        skipped.Add(new { parameter = spec.Parameter.ToString(), code = ex.Code, message = ex.Message });
                    }
                }

                if (!def.IsKeySchedule)
                {
                    // Keep the key name field of key schedules; clear the defaults of the other kinds.
                    def.ClearFields();
                }

                foreach (var (spec, sf) in resolved)
                {
                    if (def.IsKeySchedule && Enumerable.Range(0, def.GetFieldCount()).Any(i =>
                            def.GetField(i).ParameterId == sf.ParameterId && def.GetField(i).FieldType == sf.FieldType))
                    {
                        ScheduleSupport.ApplyFieldFormat(Enumerable.Range(0, def.GetFieldCount()).Select(def.GetField)
                            .First(f => f.ParameterId == sf.ParameterId && f.FieldType == sf.FieldType), spec);
                        continue;
                    }
                    ScheduleField added;
                    try
                    {
                        added = def.AddField(sf);
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                    {
                        throw new ToolException("FIELD_REJECTED", $"Revit rejected field '{spec.Parameter}': {ex.Message}");
                    }
                    ScheduleSupport.ApplyFieldFormat(added, spec);
                }
            }

            foreach (var f in args.Filters ?? new List<ScheduleFilterSpec>())
                ScheduleSupport.AddFilter(ctx, def, f);

            foreach (var s in args.Sort ?? new List<ScheduleSortSpec>())
                def.AddSortGroupField(ScheduleSupport.BuildSort(doc, def, s));

            def.IsItemized = args.Itemized;
            if (args.GrandTotal) def.ShowGrandTotal = true;

            doc.Regenerate();
            return new
            {
                created = true,
                schedule = GetScheduleDefinitionTool.Describe(ctx, schedule),
                skippedFields = skipped
            };
        }

        private static bool IsMultiCategory(string text)
        {
            var t = (text ?? "").Trim().Replace("_", "").Replace("-", "").Replace(" ", "").ToLowerInvariant();
            return t == "ostmulticategory" || t == "multicategory" || t == "multicategoria" || t == "multiplecategories";
        }
    }
}
