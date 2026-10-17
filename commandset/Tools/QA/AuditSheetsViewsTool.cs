using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.QA.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA
{
    public sealed class AuditSheetsViewsArgs
    {
        [JsonProperty("sections")]
        [Description("What to audit (default all): unplacedViews, unplacedSchedules, emptySchedules, emptySheets, sheetsWithoutTitleBlock, sheetParameters.")]
        [EnumValues("unplacedViews", "unplacedSchedules", "emptySchedules", "emptySheets", "sheetsWithoutTitleBlock", "sheetParameters")]
        public List<string> Sections { get; set; }

        [JsonProperty("requiredSheetParameters")]
        [Description("Sheet parameters that must have a value (e.g. \"Desenhado por\", {builtIn: SHEET_DRAWN_BY}). Default: drawn by, checked by, issue date.")]
        public List<ParameterRef> RequiredSheetParameters { get; set; }

        [JsonProperty("excludeViewNames")]
        [Description("Regex: views whose name matches are ignored (working views), e.g. \"^(WORK|TRAB|x_)\".")]
        public string ExcludeViewNames { get; set; }

        [JsonProperty("limit")]
        [Description("Items listed per section, 1-2000 (default 100); counts are always complete.")]
        [System.ComponentModel.DataAnnotations.Range(1, 2000)]
        [DefaultValue(100)]
        public int Limit { get; set; } = 100;
    }

    /// <summary>QA-13 (audit part; cleanup is left to delete tools with approval).</summary>
    [McpTool("audit_sheets_views", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "qa", Idempotent = true,
        TimeoutSeconds = 120,
        Description = "Audit sheets and views: views and schedules not placed on any sheet (templates, dependent " +
                      "working views and excludeViewNames ignored), schedules with no rows, sheets with no views, sheets " +
                      "without a title block, and sheets missing required parameters (drawn by, checked by, issue date " +
                      "by default). Counts per section plus ids/names. Read-only: nothing is deleted.")]
    public sealed class AuditSheetsViewsTool : McpTool<AuditSheetsViewsArgs>
    {
        private static readonly string[] All = { "unplacedViews", "unplacedSchedules", "emptySchedules", "emptySheets", "sheetsWithoutTitleBlock", "sheetParameters" };

        protected override void Validate(AuditSheetsViewsArgs a)
        {
            if (!string.IsNullOrEmpty(a.ExcludeViewNames))
            {
                try { _ = new Regex(a.ExcludeViewNames); }
                catch (ArgumentException ex) { throw new ToolException("INVALID_ARGUMENTS", $"excludeViewNames is not a valid regex: {ex.Message}"); }
            }
        }

        protected override object Run(ToolContext ctx, AuditSheetsViewsArgs a)
        {
            var doc = ctx.Doc;
            var sections = new HashSet<string>(a.Sections == null || a.Sections.Count == 0 ? All : a.Sections, StringComparer.OrdinalIgnoreCase);
            var exclude = string.IsNullOrEmpty(a.ExcludeViewNames) ? null : new Regex(a.ExcludeViewNames, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var placed = GetModelHealthTool.OnSheets(doc);
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            var sheets = views.OfType<ViewSheet>().Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
            var result = new Dictionary<string, object>();

            object Section<T>(IList<T> items, Func<T, object> describe) => new
            {
                count = items.Count,
                items = items.Take(a.Limit).Select(describe).ToList(),
                truncated = items.Count > a.Limit
            };
            object V(View v) => new { id = v.Id.GetValue(), name = v.Name, viewType = v.ViewType.ToString() };
            object S(ViewSheet s) => new { id = s.Id.GetValue(), number = s.SheetNumber, name = s.Name };
            bool Excluded(View v) => exclude != null && exclude.IsMatch(v.Name ?? string.Empty);

            if (sections.Contains("unplacedViews"))
            {
                var list = views.Where(v => GetModelHealthTool.IsPlaceable(v) && !placed.Contains(v.Id.GetValue()) && !Excluded(v))
                    .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var dependent = list.Where(v => v.GetPrimaryViewId() != ElementId.InvalidElementId).ToList();
                result["unplacedViews"] = new
                {
                    count = list.Count,
                    dependentViews = dependent.Count,
                    byType = list.GroupBy(v => v.ViewType.ToString()).ToDictionary(g => g.Key, g => g.Count()),
                    items = list.Take(a.Limit).Select(V).ToList(),
                    truncated = list.Count > a.Limit
                };
            }

            var schedules = views.OfType<ViewSchedule>()
                .Where(s => !s.IsTitleblockRevisionSchedule && !IsInternal(s) && !Excluded(s)).ToList();
            if (sections.Contains("unplacedSchedules"))
                result["unplacedSchedules"] = Section(schedules.Where(s => !placed.Contains(s.Id.GetValue())).ToList(), V);

            if (sections.Contains("emptySchedules"))
            {
                var empty = new List<ViewSchedule>();
                foreach (var s in schedules)
                {
                    try
                    {
                        if (s.Definition.IsKeySchedule) continue;
                        if (!new FilteredElementCollector(doc, s.Id).WhereElementIsNotElementType().Any()) empty.Add(s);
                    }
                    catch
                    {
                        // schedules of non-element data (e.g. sheet lists of nothing) are skipped
                    }
                }
                result["emptySchedules"] = Section(empty, V);
            }

            if (sections.Contains("emptySheets"))
            {
                var withSchedules = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance))
                    .Cast<ScheduleSheetInstance>().Where(i => !i.IsTitleblockRevisionSchedule).Select(i => i.OwnerViewId.GetValue()));
                var empty = sheets.Where(s => s.GetAllPlacedViews().Count == 0 && !withSchedules.Contains(s.Id.GetValue())).ToList();
                result["emptySheets"] = Section(empty, S);
            }

            if (sections.Contains("sheetsWithoutTitleBlock"))
            {
                var withTb = new HashSet<long>(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType().Select(e => e.OwnerViewId.GetValue()));
                result["sheetsWithoutTitleBlock"] = Section(sheets.Where(s => !withTb.Contains(s.Id.GetValue())).ToList(), S);
            }

            if (sections.Contains("sheetParameters"))
            {
                var required = a.RequiredSheetParameters != null && a.RequiredSheetParameters.Count > 0
                    ? a.RequiredSheetParameters
                    : new List<ParameterRef>
                    {
                        new ParameterRef { BuiltIn = "SHEET_DRAWN_BY" },
                        new ParameterRef { BuiltIn = "SHEET_CHECKED_BY" },
                        new ParameterRef { BuiltIn = "SHEET_ISSUE_DATE" }
                    };
                var ps = ctx.Services.Parameters;
                var missing = new List<object>();
                int missingCount = 0;
                foreach (var s in sheets)
                {
                    var names = new List<string>();
                    foreach (var r in required)
                    {
                        Parameter p;
                        try { p = ps.Find(s, r); }
                        catch (ToolException) { p = null; }
                        var text = p == null ? null : p.StorageType == StorageType.String ? p.AsString() : ParameterService.SafeValueString(p);
                        if (string.IsNullOrWhiteSpace(text)) names.Add(r.ToString());
                    }
                    if (names.Count == 0) continue;
                    missingCount++;
                    if (missing.Count < a.Limit) missing.Add(new { id = s.Id.GetValue(), number = s.SheetNumber, name = s.Name, missing = names });
                }
                result["sheetParameters"] = new
                {
                    required = required.Select(r => r.ToString()).ToList(),
                    sheetsMissing = missingCount,
                    items = missing,
                    truncated = missingCount > missing.Count
                };
            }

            return new
            {
                sheetCount = sheets.Count,
                viewCount = views.Count(v => GetModelHealthTool.IsPlaceable(v)),
                scheduleCount = schedules.Count,
                sections = result,
                note = "Nothing was deleted. Legends and dependent views may be intentionally off-sheet."
            };
        }

        private static bool IsInternal(ViewSchedule s)
        {
            try
            {
                return s.IsInternalKeynoteSchedule || s.IsTitleblockRevisionSchedule;
            }
            catch
            {
                return false;
            }
        }
    }
}
