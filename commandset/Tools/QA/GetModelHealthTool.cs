using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.QA.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA
{
    public sealed class GetModelHealthArgs
    {
        [JsonProperty("deep")]
        [Description("Also count purgeable families/types (slower on big models). Default false.")]
        [DefaultValue(false)]
        public bool Deep { get; set; }

        [JsonProperty("topN")]
        [Description("How many top warning types to show, 0-20 (default 5).")]
        [System.ComponentModel.DataAnnotations.Range(0, 20)]
        [DefaultValue(5)]
        public int TopN { get; set; } = 5;
    }

    /// <summary>
    /// QA-03: model check-up with a 0-100 score and an A-F grade, composed from cheap metrics. Each metric is measured
    /// in its own try/catch so one failure never hides the others. Penalty weights are built in (documented in the
    /// result) until the office defines its own.
    /// </summary>
    [McpTool("get_model_health", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "qa", Idempotent = true,
        TimeoutSeconds = 180,
        Description = "Model check-up: score 0-100 and grade A-F from warnings, imported (not linked) CAD, in-place " +
                      "families, model groups, views not on sheets, detail lines, unplaced/unenclosed rooms, file size and " +
                      "(deep: true) purgeable families/types. Returns every metric, the penalty it cost and prioritized " +
                      "findings with the fix and the tool to use next (get_model_warnings, purge_unused, audit_sheets_views, " +
                      "audit_families...). Read-only.")]
    public sealed class GetModelHealthTool : McpTool<GetModelHealthArgs>
    {
        private sealed class Metric
        {
            public string name { get; set; }
            public object value { get; set; }
            public double penalty { get; set; }
            public double maxPenalty { get; set; }
            public string error { get; set; }
            public string finding { get; set; }
            public string fix { get; set; }
            public string[] nextTools { get; set; }
        }

        protected override object Run(ToolContext ctx, GetModelHealthArgs a)
        {
            var doc = ctx.Doc;
            var metrics = new List<Metric>();

            void Measure(string name, double maxPenalty, Func<Metric, double> measure)
            {
                var m = new Metric { name = name, maxPenalty = maxPenalty };
                try
                {
                    var p = measure(m);
                    m.penalty = Math.Round(Math.Max(0, Math.Min(maxPenalty, p)), 1);
                }
                catch (Exception ex)
                {
                    m.error = ex.Message;
                    m.penalty = 0;
                }
                metrics.Add(m);
            }

            List<ModelWarning> warnings = null;
            Measure("warnings", 25, m =>
            {
                warnings = QaSupport.ReadWarnings(doc);
                var top = warnings.GroupBy(w => w.DefinitionId)
                    .Select(g => new { definitionId = g.Key.ToString(), description = g.First().Text, count = g.Count() })
                    .OrderByDescending(g => g.count).Take(a.TopN).ToList();
                m.value = new { count = warnings.Count, top };
                m.finding = $"{warnings.Count} warnings";
                m.fix = "Review the largest warning groups first (duplicates and overlaps are usually quick to fix).";
                m.nextTools = new[] { "get_model_warnings", "warnings_delta" };
                return warnings.Count * 0.05;
            });

            Measure("importedCad", 15, m =>
            {
                var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
                int imported = imports.Count(i => !i.IsLinked);
                m.value = new { imported, linked = imports.Count - imported };
                m.finding = $"{imported} imported CAD instance(s) (imports bloat the model; link instead)";
                m.fix = "Delete imported CAD or replace it with linked CAD.";
                m.nextTools = new[] { "list_links" };
                return imported * 3;
            });

            Measure("inPlaceFamilies", 10, m =>
            {
                int n = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Count(f => f.IsInPlace);
                m.value = n;
                m.finding = $"{n} in-place famil{(n == 1 ? "y" : "ies")}";
                m.fix = "Replace in-place families with loadable families or system elements where possible.";
                m.nextTools = new[] { "audit_families" };
                return n;
            });

            Measure("modelGroups", 5, m =>
            {
                var groups = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>()
                    .Where(g => g.Category != null && g.Category.Id.GetValue() == (long)BuiltInCategory.OST_IOSModelGroups).ToList();
                var types = groups.Select(g => g.GroupType.Id.GetValue()).Distinct().Count();
                m.value = new { instances = groups.Count, types };
                m.finding = $"{groups.Count} model group instance(s) of {types} type(s)";
                m.fix = "Many or nested model groups slow editing; ungroup the ones that aren't repeated.";
                return groups.Count * 0.05;
            });

            Measure("viewsNotOnSheets", 10, m =>
            {
                var placed = OnSheets(doc);
                var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && IsPlaceable(v)).ToList();
                int notPlaced = views.Count(v => !placed.Contains(v.Id.GetValue()));
                m.value = new { views = views.Count, notOnSheets = notPlaced };
                m.finding = $"{notPlaced} of {views.Count} views are not on any sheet";
                m.fix = "Delete working views that are no longer needed (keep them in a 'work' browser group otherwise).";
                m.nextTools = new[] { "audit_sheets_views" };
                return notPlaced * 0.05;
            });

            Measure("detailLines", 5, m =>
            {
                int n = new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).Cast<CurveElement>()
                    .Count(c => c.CurveElementType == CurveElementType.DetailCurve);
                m.value = n;
                m.finding = $"{n} detail lines";
                m.fix = "Large numbers of detail lines usually mean drafting over the model; prefer components/detail items.";
                return n / 1000.0;
            });

            Measure("rooms", 5, m =>
            {
                var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                    .Cast<Room>().ToList();
                int unplaced = rooms.Count(r => r.Location == null);
                int unenclosed = rooms.Count(r => r.Location != null && r.Area <= 0);
                m.value = new { total = rooms.Count, unplaced, notEnclosed = unenclosed };
                m.finding = $"{unplaced} unplaced and {unenclosed} not-enclosed room(s)";
                m.fix = "Delete unplaced rooms and close the boundaries of not-enclosed ones.";
                m.nextTools = new[] { "get_model_warnings" };
                return (unplaced + unenclosed) * 0.5;
            });

            Measure("fileSize", 10, m =>
            {
                if (string.IsNullOrEmpty(doc.PathName) || !File.Exists(doc.PathName))
                {
                    m.value = null;
                    m.finding = "file size unknown (not saved locally or cloud model)";
                    return 0;
                }
                double mb = new FileInfo(doc.PathName).Length / 1048576.0;
                m.value = new { megabytes = Math.Round(mb, 1) };
                m.finding = $"{Math.Round(mb)} MB on disk";
                m.fix = "Purge unused content and save with Compact; check imported CAD and heavy families.";
                m.nextTools = new[] { "purge_unused", "save_document" };
                return mb > 300 ? (mb - 300) / 50 : 0;
            });

            if (a.Deep)
            {
                Measure("purgeable", 10, m =>
                {
                    var ids = UnusedFinder.Find(doc, out var source);
                    var byKind = ids.Select(id => doc.GetElement(id)).Where(e => e != null)
                        .GroupBy(UnusedFinder.Classify).ToDictionary(g => g.Key, g => g.Count());
                    m.value = new { count = ids.Count, byKind, source };
                    m.finding = $"{ids.Count} purgeable element(s)";
                    m.fix = "Run purge_unused (preview first) to remove unused families, types and materials.";
                    m.nextTools = new[] { "purge_unused" };
                    return ids.Count * 0.02;
                });
            }

            double score = Math.Round(Math.Max(0, 100 - metrics.Sum(x => x.penalty)));
            string grade = score >= 90 ? "A" : score >= 80 ? "B" : score >= 70 ? "C" : score >= 60 ? "D" : "F";
            var findings = metrics.Where(x => x.penalty > 0)
                .OrderByDescending(x => x.penalty)
                .Select(x => new
                {
                    metric = x.name,
                    severity = x.penalty >= 10 ? "high" : x.penalty >= 4 ? "medium" : "low",
                    penalty = x.penalty,
                    finding = x.finding,
                    fix = x.fix,
                    nextTools = x.nextTools
                }).ToList();

            return new
            {
                document = doc.Title,
                score,
                grade,
                findings,
                metrics = metrics.Select(x => new { x.name, x.value, x.penalty, x.maxPenalty, x.error }).ToList(),
                suggestedNextSteps = findings.SelectMany(f => f.nextTools ?? new string[0]).Distinct().ToList(),
                scoring = "Built-in weights: score = 100 - sum of penalties (warnings 0.05 each up to 25; imported CAD 3 each up to 15; " +
                          "in-place families 1 each up to 10; views not on sheets 0.05 each up to 10; model groups 0.05 per instance up to 5; " +
                          "detail lines 1 per 1000 up to 5; unplaced/unenclosed rooms 0.5 each up to 5; file size above 300 MB 1 per 50 MB up to 10; " +
                          "deep: purgeable 0.02 each up to 10). A >= 90, B >= 80, C >= 70, D >= 60, else F." +
                          (a.Deep ? string.Empty : " Purgeable content not measured (deep: false).")
            };
        }

        internal static HashSet<long> OnSheets(Document doc)
        {
            var placed = new HashSet<long>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                placed.Add(vp.ViewId.GetValue());
            foreach (var ss in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                placed.Add(ss.ScheduleId.GetValue());
            return placed;
        }

        /// <summary>Views that are meant to go on sheets (plans, sections, elevations, 3D, drafting, legends).</summary>
        internal static bool IsPlaceable(View v)
        {
            switch (v.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                case ViewType.Elevation:
                case ViewType.Section:
                case ViewType.Detail:
                case ViewType.ThreeD:
                case ViewType.DraftingView:
                case ViewType.Legend:
                    return true;
                default:
                    return false;
            }
        }
    }
}
