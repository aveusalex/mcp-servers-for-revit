using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Schedules.Support
{
    /// <summary>A field of a schedule to add (create_schedule, modify_schedule_fields).</summary>
    public sealed class ScheduleFieldSpec
    {
        [JsonProperty("parameter", Required = Required.Always)]
        [Description("Field to add: {builtIn} (e.g. ROOM_AREA, language-neutral), {guid} (shared parameter) or {name} as listed by get_schedulable_fields (\"Count\", \"Área\", ...).")]
        public ParameterRef Parameter { get; set; }

        [JsonProperty("fieldType")]
        [Description("Disambiguates fields with the same parameter: Instance, ElementType (alias: type), Count, Room, FromRoom, ToRoom, Material, MaterialQuantity, ProjectInfo... (see get_schedulable_fields).")]
        public string FieldType { get; set; }

        [JsonProperty("heading")]
        [Description("Column heading (default: the parameter name).")]
        public string Heading { get; set; }

        [JsonProperty("hidden")]
        [Description("Hide the column (it still works for filters and sorting). Default false.")]
        public bool? Hidden { get; set; }

        [JsonProperty("widthMm")]
        [Description("Column width on the sheet (paper size).")]
        [Unit("mm")]
        public double? WidthMm { get; set; }

        [JsonProperty("align")]
        [Description("Horizontal alignment of the column.")]
        [EnumValues("left", "center", "right")]
        public string Align { get; set; }

        [JsonProperty("total")]
        [Description("Show totals for this column (only numeric fields that can be totalled). Default false.")]
        public bool? Total { get; set; }
    }

    /// <summary>A filter of a schedule.</summary>
    public sealed class ScheduleFilterSpec
    {
        [JsonProperty("field", Required = Required.Always)]
        [Description("Field already in the schedule (it may be hidden): its heading or name, or {builtIn}/{guid}/{name}.")]
        public ParameterRef Field { get; set; }

        [JsonProperty("op", Required = Required.Always)]
        [Description("Operator. Numbers are compared in wire units (mm, m2, m3, deg).")]
        [EnumValues("eq", "neq", "gt", "gte", "lt", "lte", "contains", "not_contains", "begins_with", "not_begins_with",
            "ends_with", "not_ends_with", "has_value", "has_no_value")]
        public string Op { get; set; }

        [JsonProperty("value")]
        [Description("Value to compare with (not used by has_value/has_no_value). Number in wire units, text, true/false, or a level/phase name for element fields.")]
        public JToken Value { get; set; }
    }

    /// <summary>A sorting/grouping level of a schedule.</summary>
    public sealed class ScheduleSortSpec
    {
        [JsonProperty("field", Required = Required.Always)]
        [Description("Field already in the schedule: its heading or name, or {builtIn}/{guid}/{name}.")]
        public ParameterRef Field { get; set; }

        [JsonProperty("order")]
        [Description("asc (default) or desc.")]
        [EnumValues("asc", "desc")]
        [DefaultValue("asc")]
        public string Order { get; set; } = "asc";

        [JsonProperty("header")]
        [Description("Show a group header row. Default false.")]
        [DefaultValue(false)]
        public bool Header { get; set; }

        [JsonProperty("footer")]
        [Description("Show a group footer (title, count and totals). Default false.")]
        [DefaultValue(false)]
        public bool Footer { get; set; }

        [JsonProperty("blankLine")]
        [Description("Blank line after each group. Default false.")]
        [DefaultValue(false)]
        public bool BlankLine { get; set; }
    }

    /// <summary>Shared schedule helpers: lookup, kinds, field description and resolution, filters and sorting.</summary>
    public static class ScheduleSupport
    {
        public const int MaxFilters = 8;

        /// <summary>A schedule (not a template, not an internal revision/keynote table) by name or id.</summary>
        public static ViewSchedule Find(ToolContext ctx, string nameOrId)
        {
            var all = new FilteredElementCollector(ctx.Doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !IsInternal(s)).ToList();
            return ctx.Services.Lookup.ByNameOrId(nameOrId, all, "schedule", Kind);
        }

        public static bool IsInternal(ViewSchedule s)
        {
            try
            {
                return s.IsTitleblockRevisionSchedule || s.IsInternalKeynoteSchedule;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>regular | material_takeoff | key | sheet_list | view_list | revision | note_block | keynote_legend.</summary>
        public static string Kind(ViewSchedule s)
        {
            var def = s.Definition;
            if (def.IsKeySchedule) return "key";
            if (def.IsMaterialTakeoff) return "material_takeoff";
            var cat = def.CategoryId.GetValue();
            if (cat == (long)BuiltInCategory.OST_Sheets) return "sheet_list";
            if (cat == (long)BuiltInCategory.OST_Views) return "view_list";
            if (cat == (long)BuiltInCategory.OST_Revisions) return "revision";
            if (cat == (long)BuiltInCategory.OST_KeynoteTags) return "keynote_legend";
            if (def.FamilyId != null && def.FamilyId != ElementId.InvalidElementId) return "note_block";
            return "regular";
        }

        public static string CategoryName(Document doc, ElementId categoryId)
        {
            if (categoryId == null || categoryId == ElementId.InvalidElementId) return null;
            try
            {
                var c = Category.GetCategory(doc, categoryId);
                return c?.Name;
            }
            catch
            {
                return null;
            }
        }

        public static string CategoryBuiltIn(Document doc, ElementId categoryId)
        {
            if (categoryId == null || categoryId == ElementId.InvalidElementId) return null;
            try
            {
                return CategoryService.BuiltInNameOf(Category.GetCategory(doc, categoryId));
            }
            catch
            {
                return null;
            }
        }

        // ---- fields ---------------------------------------------------------------------------------------------

        public static string BuiltInOf(ElementId parameterId)
        {
            if (parameterId == null) return null;
            var v = parameterId.GetValue();
            if (v >= 0 || v < int.MinValue) return null;
            var bip = (BuiltInParameter)(int)v;
            return Enum.IsDefined(typeof(BuiltInParameter), bip) && bip != BuiltInParameter.INVALID ? bip.ToString() : null;
        }

        public static string GuidOf(Document doc, ElementId parameterId)
        {
            if (parameterId == null || parameterId.GetValue() <= 0) return null;
            return (doc.GetElement(parameterId) as SharedParameterElement)?.GuidValue.ToString();
        }

        public static object DescribeSchedulable(Document doc, SchedulableField f)
        {
            return new
            {
                name = SafeName(() => f.GetName(doc)),
                fieldType = f.FieldType.ToString(),
                parameterId = f.ParameterId.GetValue(),
                builtIn = BuiltInOf(f.ParameterId),
                guid = GuidOf(doc, f.ParameterId)
            };
        }

        public static object DescribeField(Document doc, ScheduleDefinition def, ScheduleField f, UnitsService units)
        {
            string spec = null;
            try { spec = f.GetSpecTypeId()?.TypeId; } catch { /* calculated / count */ }
            double? width = null;
            try { width = Math.Round(UnitPolicy.FromInternal(f.SheetColumnWidth, UnitSpec.Length, out _), 2); } catch { }
            return new
            {
                index = f.FieldIndex,
                fieldId = f.FieldId.IntegerValue,
                name = SafeName(f.GetName),
                heading = f.ColumnHeading,
                fieldType = f.FieldType.ToString(),
                parameterId = f.ParameterId.GetValue(),
                builtIn = BuiltInOf(f.ParameterId),
                guid = GuidOf(doc, f.ParameterId),
                hidden = f.IsHidden,
                isCalculated = f.IsCalculatedField,
                isCombined = f.IsCombinedParameterField,
                spec,
                widthMm = width,
                align = f.HorizontalAlignment.ToString().ToLowerInvariant(),
                display = f.DisplayType.ToString()
            };
        }

        private static string SafeName(Func<string> f)
        {
            try { return f(); }
            catch { return null; }
        }

        public static ScheduleFieldType? ParseFieldType(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim();
            if (string.Equals(t, "type", StringComparison.OrdinalIgnoreCase)) return ScheduleFieldType.ElementType;
            if (Enum.TryParse(t.Replace("_", ""), true, out ScheduleFieldType ft) && Enum.IsDefined(typeof(ScheduleFieldType), ft))
                return ft;
            throw new ToolException("INVALID_ARGUMENTS", $"Unknown fieldType '{text}'.",
                "Use one of: " + string.Join(", ", Enum.GetNames(typeof(ScheduleFieldType))) + " (or 'type').");
        }

        /// <summary>Finds the schedulable field for a reference: builtIn → guid → name (localized), then fieldType.</summary>
        public static SchedulableField ResolveSchedulable(Document doc, IList<SchedulableField> available, ParameterRef r,
            ScheduleFieldType? fieldType)
        {
            if (r == null || r.IsEmpty)
                throw new ToolException("INVALID_ARGUMENTS", "Every field needs a parameter ({builtIn}, {guid} or {name}).");

            IEnumerable<SchedulableField> pool = available;
            if (fieldType.HasValue) pool = pool.Where(f => f.FieldType == fieldType.Value);
            var list = pool.ToList();

            List<SchedulableField> hits;
            if (!string.IsNullOrWhiteSpace(r.Guid))
            {
                var g = r.ParsedGuid().Value;
                var spe = SharedParameterElement.Lookup(doc, g);
                hits = spe == null ? new List<SchedulableField>() : list.Where(f => f.ParameterId == spe.Id).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(r.BuiltIn))
            {
                if (!Enum.TryParse(r.BuiltIn.Trim(), true, out BuiltInParameter bip))
                    throw new ToolException("INVALID_ARGUMENTS", $"'{r.BuiltIn}' is not a BuiltInParameter name.");
                var id = new ElementId(bip);
                hits = list.Where(f => f.ParameterId == id).ToList();
            }
            else
            {
                var named = list.Select(f => new NamedCandidate<SchedulableField>(SafeName(() => f.GetName(doc)) ?? "", f)).ToList();
                hits = NameMatcher.FindBest(r.Name, named).Select(c => c.Item).ToList();
            }

            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1)
            {
                // Prefer the element's own parameter over related ones (room, material, ...) when not told otherwise.
                if (!fieldType.HasValue)
                {
                    var instance = hits.Where(h => h.FieldType == ScheduleFieldType.Instance).ToList();
                    if (instance.Count == 1) return instance[0];
                }
                throw new ToolException("AMBIGUOUS_FIELD",
                    $"Field '{r}' matches {hits.Count} schedulable fields: " +
                    string.Join("; ", hits.Take(10).Select(h => $"{SafeName(() => h.GetName(doc))} ({h.FieldType})")) + ".",
                    "Pass fieldType (e.g. Instance, ElementType, Room) or the builtIn/guid of the parameter.",
                    "get_schedulable_fields");
            }

            var suggestions = TextMatch.Suggest(r.ToString(), available.Select(f => SafeName(() => f.GetName(doc)) ?? ""));
            throw new ToolException("FIELD_NOT_SCHEDULABLE",
                $"'{r}' is not a schedulable field of this schedule{(fieldType.HasValue ? $" with fieldType {fieldType}" : "")}." +
                (suggestions.Count > 0 ? " Did you mean: " + string.Join(", ", suggestions) + "?" : ""),
                "List the available fields with get_schedulable_fields.", "get_schedulable_fields");
        }

        /// <summary>A field already in the schedule by heading, name, builtIn or guid (hidden ones included).</summary>
        public static ScheduleField ResolveExisting(Document doc, ScheduleDefinition def, ParameterRef r)
        {
            if (r == null || r.IsEmpty)
                throw new ToolException("INVALID_ARGUMENTS", "A field reference is required.");
            var fields = Enumerable.Range(0, def.GetFieldCount()).Select(def.GetField).ToList();

            List<ScheduleField> hits;
            if (!string.IsNullOrWhiteSpace(r.Guid))
            {
                var spe = SharedParameterElement.Lookup(doc, r.ParsedGuid().Value);
                hits = spe == null ? new List<ScheduleField>() : fields.Where(f => f.ParameterId == spe.Id).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(r.BuiltIn))
            {
                if (!Enum.TryParse(r.BuiltIn.Trim(), true, out BuiltInParameter bip))
                    throw new ToolException("INVALID_ARGUMENTS", $"'{r.BuiltIn}' is not a BuiltInParameter name.");
                var id = new ElementId(bip);
                hits = fields.Where(f => f.ParameterId == id).ToList();
            }
            else
            {
                var byHeading = NameMatcher.FindBest(r.Name,
                    fields.Select(f => new NamedCandidate<ScheduleField>(f.ColumnHeading ?? "", f)).ToList());
                hits = byHeading.Count > 0
                    ? byHeading.Select(c => c.Item).ToList()
                    : NameMatcher.FindBest(r.Name, fields.Select(f => new NamedCandidate<ScheduleField>(SafeName(f.GetName) ?? "", f)).ToList())
                        .Select(c => c.Item).ToList();
            }

            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1)
                throw new ToolException("AMBIGUOUS_FIELD",
                    $"'{r}' matches {hits.Count} fields of the schedule: " +
                    string.Join("; ", hits.Select(h => $"[{h.FieldIndex}] {h.ColumnHeading}")) + ".",
                    "Use the exact column heading, or the builtIn/guid.", "get_schedule_definition");
            throw new ToolException("FIELD_NOT_IN_SCHEDULE",
                $"'{r}' is not a field of this schedule. Fields: " + string.Join(", ", fields.Select(f => f.ColumnHeading)) + ".",
                "Add the field first (modify_schedule_fields), hidden if it should not show.", "modify_schedule_fields");
        }

        public static void ApplyFieldFormat(ScheduleField field, ScheduleFieldSpec spec)
        {
            if (spec.Heading != null) field.ColumnHeading = spec.Heading;
            if (spec.Hidden.HasValue) field.IsHidden = spec.Hidden.Value;
            if (spec.WidthMm.HasValue)
            {
                if (spec.WidthMm.Value <= 0 || spec.WidthMm.Value > 1000)
                    throw new ToolException("INVALID_ARGUMENTS", $"widthMm must be between 0 and 1000 (got {spec.WidthMm}).");
                field.SheetColumnWidth = UnitPolicy.ToInternal(spec.WidthMm.Value, UnitSpec.Length);
            }
            if (!string.IsNullOrWhiteSpace(spec.Align))
            {
                switch (spec.Align.Trim().ToLowerInvariant())
                {
                    case "left": field.HorizontalAlignment = ScheduleHorizontalAlignment.Left; break;
                    case "center": field.HorizontalAlignment = ScheduleHorizontalAlignment.Center; break;
                    case "right": field.HorizontalAlignment = ScheduleHorizontalAlignment.Right; break;
                    default: throw new ToolException("INVALID_ARGUMENTS", $"align must be left, center or right (got '{spec.Align}').");
                }
            }
            if (spec.Total.HasValue)
            {
                if (spec.Total.Value)
                {
                    if (!field.CanTotal())
                        throw new ToolException("FIELD_CANNOT_TOTAL", $"Field '{field.ColumnHeading}' cannot be totalled.");
                    field.DisplayType = ScheduleFieldDisplayType.Totals;
                }
                else
                {
                    field.DisplayType = ScheduleFieldDisplayType.Standard;
                }
            }
        }

        // ---- filters --------------------------------------------------------------------------------------------

        public static ScheduleFilterType FilterType(string op)
        {
            switch ((op ?? "").Trim().ToLowerInvariant())
            {
                case "eq": return ScheduleFilterType.Equal;
                case "neq": return ScheduleFilterType.NotEqual;
                case "gt": return ScheduleFilterType.GreaterThan;
                case "gte": return ScheduleFilterType.GreaterThanOrEqual;
                case "lt": return ScheduleFilterType.LessThan;
                case "lte": return ScheduleFilterType.LessThanOrEqual;
                case "contains": return ScheduleFilterType.Contains;
                case "not_contains": return ScheduleFilterType.NotContains;
                case "begins_with": return ScheduleFilterType.BeginsWith;
                case "not_begins_with": return ScheduleFilterType.NotBeginsWith;
                case "ends_with": return ScheduleFilterType.EndsWith;
                case "not_ends_with": return ScheduleFilterType.NotEndsWith;
                case "has_value": return ScheduleFilterType.HasValue;
                case "has_no_value": return ScheduleFilterType.HasNoValue;
                default: throw new ToolException("INVALID_ARGUMENTS", $"Unknown filter op '{op}'.");
            }
        }

        public static string OpName(ScheduleFilterType t)
        {
            switch (t)
            {
                case ScheduleFilterType.Equal: return "eq";
                case ScheduleFilterType.NotEqual: return "neq";
                case ScheduleFilterType.GreaterThan: return "gt";
                case ScheduleFilterType.GreaterThanOrEqual: return "gte";
                case ScheduleFilterType.LessThan: return "lt";
                case ScheduleFilterType.LessThanOrEqual: return "lte";
                case ScheduleFilterType.Contains: return "contains";
                case ScheduleFilterType.NotContains: return "not_contains";
                case ScheduleFilterType.BeginsWith: return "begins_with";
                case ScheduleFilterType.NotBeginsWith: return "not_begins_with";
                case ScheduleFilterType.EndsWith: return "ends_with";
                case ScheduleFilterType.NotEndsWith: return "not_ends_with";
                case ScheduleFilterType.HasValue: return "has_value";
                case ScheduleFilterType.HasNoValue: return "has_no_value";
                default: return t.ToString();
            }
        }

        /// <summary>Builds and adds a filter, converting the value to the field's storage and internal units.</summary>
        public static ScheduleFilter AddFilter(ToolContext ctx, ScheduleDefinition def, ScheduleFilterSpec spec)
        {
            var field = ResolveExisting(ctx.Doc, def, spec.Field);
            var type = FilterType(spec.Op);
            if (!def.CanFilter())
                throw new ToolException("SCHEDULE_CANNOT_FILTER", "This schedule type does not support filters.");
            if (def.GetFilterCount() >= MaxFilters)
                throw new ToolException("TOO_MANY_FILTERS", $"A schedule holds at most {MaxFilters} filters.");

            var candidates = BuildFilters(ctx, field, type, spec.Value);
            Exception last = null;
            foreach (var f in candidates)
            {
                try
                {
                    def.AddFilter(f);
                    return f;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    last = ex;
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                {
                    last = ex;
                }
            }
            throw new ToolException("FILTER_REJECTED",
                $"Revit rejected the filter on '{field.ColumnHeading}' {spec.Op} {spec.Value}: {last?.Message}",
                "Check the operator suits the field type (text ops only on text fields; numbers in mm/m2/m3/deg).");
        }

        private static IEnumerable<ScheduleFilter> BuildFilters(ToolContext ctx, ScheduleField field, ScheduleFilterType type, JToken value)
        {
            var fid = field.FieldId;
            if (type == ScheduleFilterType.HasValue || type == ScheduleFilterType.HasNoValue)
            {
                yield return new ScheduleFilter(fid, type);
                yield break;
            }
            if (value == null || value.Type == JTokenType.Null)
                throw new ToolException("INVALID_ARGUMENTS", $"Filter on '{field.ColumnHeading}' needs a value.");

            ForgeTypeId spec = null;
            try { spec = field.GetSpecTypeId(); } catch { }
            var kind = ctx.Services.Units.Classify(spec);
            var specId = spec?.TypeId ?? "";

            if (value.Type == JTokenType.Boolean)
            {
                yield return new ScheduleFilter(fid, type, value.Value<bool>() ? 1 : 0);
                yield break;
            }

            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                var number = value.Value<double>();
                if (specId.StartsWith("autodesk.spec:spec.int64", StringComparison.Ordinal) ||
                    specId.StartsWith("autodesk.spec.aec:integer", StringComparison.Ordinal) ||
                    specId.StartsWith("autodesk.spec:spec.bool", StringComparison.Ordinal))
                {
                    yield return new ScheduleFilter(fid, type, (int)Math.Round(number));
                    yield break;
                }
                if (kind == UnitSpec.OtherMeasurable && specId.Length > 0 && !IsTextSpec(specId))
                    throw new ToolException("UNIT_UNSUPPORTED",
                        $"Field '{field.ColumnHeading}' uses {specId}, which has no wire-unit conversion; it cannot be filtered by number here.");
                if (IsTextSpec(specId))
                {
                    yield return new ScheduleFilter(fid, type, number.ToString(CultureInfo.InvariantCulture));
                    yield break;
                }
                var raw = kind == UnitSpec.OtherMeasurable ? number : UnitPolicy.ToInternal(number, kind);
                yield return new ScheduleFilter(fid, type, raw);
                yield return new ScheduleFilter(fid, type, (int)Math.Round(number));
                yield break;
            }

            var text = value.Type == JTokenType.String ? (string)value : value.ToString(Formatting.None);
            yield return new ScheduleFilter(fid, type, text);

            // Element-valued fields (Level, Phase, Material, ...) need the element id.
            var target = FindElementByName(ctx.Doc, text);
            if (target != null) yield return new ScheduleFilter(fid, type, target.Id);
        }

        private static bool IsTextSpec(string specId) =>
            specId.StartsWith("autodesk.spec:spec.string", StringComparison.Ordinal) ||
            specId.StartsWith("autodesk.spec.string", StringComparison.Ordinal);

        private static Element FindElementByName(Document doc, string name)
        {
            foreach (var t in new[] { typeof(Level), typeof(Phase), typeof(Material) })
            {
                var hit = new FilteredElementCollector(doc).OfClass(t).FirstOrDefault(e =>
                    string.Equals(TextMatch.Key(e.Name), TextMatch.Key(name), StringComparison.Ordinal));
                if (hit != null) return hit;
            }
            return null;
        }

        public static object DescribeFilter(ToolContext ctx, ScheduleDefinition def, ScheduleFilter f)
        {
            ScheduleField field = null;
            try { field = def.GetField(f.FieldId); } catch { }
            object value = null;
            string unit = null;
            if (f.IsStringValue) value = f.GetStringValue();
            else if (f.IsIntegerValue) value = f.GetIntegerValue();
            else if (f.IsElementIdValue)
            {
                var id = f.GetElementIdValue();
                value = new { id = id.GetValue(), name = ctx.Doc.GetElement(id)?.Name };
            }
            else if (f.IsDoubleValue)
            {
                ForgeTypeId spec = null;
                try { spec = field?.GetSpecTypeId(); } catch { }
                if (ctx.Services.Units.TryFromInternal(f.GetDoubleValue(), spec, out var v, out var u))
                {
                    value = Math.Round(v, 6);
                    unit = u;
                }
                else
                {
                    value = f.GetDoubleValue();
                    unit = u;
                }
            }
            return new
            {
                field = field?.ColumnHeading,
                fieldName = field == null ? null : SafeName(field.GetName),
                op = OpName(f.FilterType),
                value,
                unit
            };
        }

        // ---- sorting --------------------------------------------------------------------------------------------

        public static ScheduleSortGroupField BuildSort(Document doc, ScheduleDefinition def, ScheduleSortSpec spec)
        {
            var field = ResolveExisting(doc, def, spec.Field);
            if (!def.CanSortByField(field.FieldId))
                throw new ToolException("FIELD_CANNOT_SORT", $"The schedule cannot be sorted by '{field.ColumnHeading}'.");
            var order = string.Equals(spec.Order?.Trim(), "desc", StringComparison.OrdinalIgnoreCase)
                ? ScheduleSortOrder.Descending
                : ScheduleSortOrder.Ascending;
            return new ScheduleSortGroupField(field.FieldId, order)
            {
                ShowHeader = spec.Header,
                ShowFooter = spec.Footer,
                ShowBlankLine = spec.BlankLine
            };
        }

        public static object DescribeSort(ScheduleDefinition def, ScheduleSortGroupField s)
        {
            ScheduleField field = null;
            try { field = def.GetField(s.FieldId); } catch { }
            return new
            {
                field = field?.ColumnHeading,
                order = s.SortOrder == ScheduleSortOrder.Descending ? "desc" : "asc",
                header = s.ShowHeader,
                footer = s.ShowFooter,
                blankLine = s.ShowBlankLine
            };
        }

        public static void ValidateFilterSpecs(IList<ScheduleFilterSpec> filters)
        {
            if (filters == null) return;
            if (filters.Count > MaxFilters)
                throw new ToolException("TOO_MANY_FILTERS", $"A schedule holds at most {MaxFilters} filters (got {filters.Count}).");
            foreach (var f in filters)
            {
                if (f == null || f.Field == null || f.Field.IsEmpty)
                    throw new ToolException("INVALID_ARGUMENTS", "Every filter needs a field.");
                var t = FilterType(f.Op);
                if (t != ScheduleFilterType.HasValue && t != ScheduleFilterType.HasNoValue &&
                    (f.Value == null || f.Value.Type == JTokenType.Null))
                    throw new ToolException("INVALID_ARGUMENTS", $"Filter on '{f.Field}' with op {f.Op} needs a value.");
            }
        }
    }
}
