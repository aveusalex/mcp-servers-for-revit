using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Query.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class ListViewsArgs
    {
        [JsonProperty("viewTypes")]
        [Description("Only these Revit view types, e.g. FloorPlan, CeilingPlan, Elevation, Section, ThreeD, DraftingView, Legend, Schedule, DrawingSheet, AreaPlan, Detail, EngineeringPlan.")]
        public List<string> ViewTypes { get; set; }

        [JsonProperty("isTemplate")]
        [Description("false = only views (default), true = only view templates, any = both.")]
        [EnumValues("false", "true", "any")]
        [DefaultValue("false")]
        public string IsTemplate { get; set; } = "false";

        [JsonProperty("nameContains")]
        [Description("Only names containing this text (case and accents ignored).")]
        public string NameContains { get; set; }

        [JsonProperty("onSheet")]
        [Description("true = only views placed on a sheet, false = only views not placed, any (default).")]
        [EnumValues("true", "false", "any")]
        [DefaultValue("any")]
        public string OnSheet { get; set; } = "any";

        [JsonProperty("level")]
        [Description("Only plan views of this level (name or id).")]
        public string Level { get; set; }

        [JsonProperty("includeViewFamilyTypes")]
        [Description("Also return the view family types (used to create views), default false.")]
        [DefaultValue(false)]
        public bool IncludeViewFamilyTypes { get; set; }

        [JsonProperty("limit")]
        [Description("Page size, 1-2000 (default 200).")]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Views to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>CON-06: views, schedules, legends, sheets and templates.</summary>
    [McpTool("list_views", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "List views, schedules, legends, sheets and view templates with filters (viewTypes, isTemplate, " +
                      "nameContains, onSheet, level). Each item: id, name, viewType, isTemplate, level, scale, detail " +
                      "level, discipline, phase, view template applied, and the sheet(s) where it is placed. Internal " +
                      "views (project/system browser, revision schedules of title blocks) are excluded. Optionally " +
                      "lists the view family types. Ordered by type then name, paged with total. Read-only.")]
    public sealed class ListViewsTool : McpTool<ListViewsArgs>
    {
        protected override void Validate(ListViewsArgs args)
        {
            QueryHelpers.CheckPaging(args.Limit, args.Offset, 2000);
            args.IsTemplate = (args.IsTemplate ?? "false").Trim().ToLowerInvariant();
            args.OnSheet = (args.OnSheet ?? "any").Trim().ToLowerInvariant();
            if (args.ViewTypes != null)
            {
                foreach (var t in args.ViewTypes)
                {
                    if (!Enum.TryParse<ViewType>(t?.Trim(), true, out _))
                        throw new ToolException("INVALID_ARGUMENTS", $"Unknown view type '{t}'.",
                            "Use Revit's ViewType names: FloorPlan, CeilingPlan, Elevation, Section, ThreeD, DraftingView, Legend, Schedule, DrawingSheet, AreaPlan, Detail, EngineeringPlan, Rendering, Walkthrough.");
                }
            }
        }

        protected override object Run(ToolContext ctx, ListViewsArgs args)
        {
            var doc = ctx.Doc;
            var placements = SheetPlacements(doc);
            var types = args.ViewTypes?.Select(t => (ViewType)Enum.Parse(typeof(ViewType), t.Trim(), true)).ToList();

            IEnumerable<View> views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(IsListable);
            if (args.IsTemplate == "false") views = views.Where(v => !v.IsTemplate);
            else if (args.IsTemplate == "true") views = views.Where(v => v.IsTemplate);
            if (types != null && types.Count > 0) views = views.Where(v => types.Contains(v.ViewType));
            if (!string.IsNullOrWhiteSpace(args.NameContains)) views = views.Where(v => QueryHelpers.Contains(v.Name, args.NameContains));
            if (args.OnSheet == "true") views = views.Where(v => placements.ContainsKey(v.Id.GetValue()));
            else if (args.OnSheet == "false") views = views.Where(v => !placements.ContainsKey(v.Id.GetValue()) && !(v is ViewSheet));
            if (!string.IsNullOrWhiteSpace(args.Level))
            {
                var level = ctx.Services.Lookup.Level(args.Level);
                views = views.Where(v => v.GenLevel != null && v.GenLevel.Id == level.Id);
            }

            var ordered = views.OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var page = Paging.Page(ordered, args.Limit, args.Offset, 2000);
            var items = page.Items.Select(v => Describe(doc, v, placements)).ToList();

            object familyTypes = null;
            if (args.IncludeViewFamilyTypes)
            {
                familyTypes = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .OrderBy(t => t.ViewFamily.ToString()).ThenBy(t => t.Name)
                    .Select(t => new
                    {
                        id = t.Id.GetValue(),
                        name = t.Name,
                        viewFamily = t.ViewFamily.ToString(),
                        defaultTemplateId = QueryHelpers.IdOrNull(t.DefaultTemplateId)
                    }).ToList();
            }

            return new
            {
                items,
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated,
                viewFamilyTypes = familyTypes
            };
        }

        internal static bool IsListable(View v)
        {
            switch (v.ViewType)
            {
                case ViewType.ProjectBrowser:
                case ViewType.SystemBrowser:
                case ViewType.Internal:
                case ViewType.Undefined:
                case ViewType.Report:
                case ViewType.CostReport:
                case ViewType.LoadsReport:
                case ViewType.PresureLossReport:
                case ViewType.PanelSchedule:
                case ViewType.ColumnSchedule:
                    return false;
            }
            if (v is ViewSchedule vs)
            {
                try
                {
                    if (vs.IsTitleblockRevisionSchedule || vs.IsInternalKeynoteSchedule) return false;
                }
                catch
                {
                    // older API surface: keep
                }
            }
            return true;
        }

        /// <summary>viewId → sheets ("number - name") where it is placed (viewports and schedule instances).</summary>
        internal static Dictionary<long, List<object>> SheetPlacements(Document doc)
        {
            var map = new Dictionary<long, List<object>>();
            void Add(long viewId, ViewSheet sheet)
            {
                if (sheet == null) return;
                if (!map.TryGetValue(viewId, out var l)) map[viewId] = l = new List<object>();
                l.Add(new { sheetId = sheet.Id.GetValue(), sheetNumber = sheet.SheetNumber, sheetName = sheet.Name });
            }
            foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)))
                Add(vp.ViewId.GetValue(), doc.GetElement(vp.SheetId) as ViewSheet);
            foreach (ScheduleSheetInstance si in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)))
            {
                if (si.IsTitleblockRevisionSchedule) continue;
                Add(si.ScheduleId.GetValue(), doc.GetElement(si.OwnerViewId) as ViewSheet);
            }
            return map;
        }

        private static object Describe(Document doc, View v, Dictionary<long, List<object>> placements)
        {
            placements.TryGetValue(v.Id.GetValue(), out var sheets);
            int? scale = null;
            try { if (!(v is ViewSheet) && !(v is ViewSchedule)) scale = v.Scale; }
            catch { /* some views have no scale */ }
            string detail = null, discipline = null;
            try { detail = v.DetailLevel.ToString(); } catch { /* n/a */ }
            try { discipline = v.HasViewDiscipline() ? v.Discipline.ToString() : null; } catch { /* n/a */ }
            object phase = null;
            try
            {
                var pid = v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                phase = QueryHelpers.Ref(doc, pid);
            }
            catch { /* n/a */ }

            return new
            {
                id = v.Id.GetValue(),
                uniqueId = v.UniqueId,
                name = v.Name,
                viewType = v.ViewType.ToString(),
                isTemplate = v.IsTemplate,
                level = v.GenLevel?.Name,
                scale,
                detailLevel = detail,
                discipline,
                phase,
                viewTemplate = QueryHelpers.Ref(doc, v.ViewTemplateId),
                sheetNumber = (v as ViewSheet)?.SheetNumber,
                placedOn = sheets
            };
        }
    }
}
