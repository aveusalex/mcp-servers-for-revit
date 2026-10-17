using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Query.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class ListLevelsArgs
    {
        [JsonProperty("includeElementCounts")]
        [Description("Also count the model elements associated with each level (slower on big models; default false).")]
        [DefaultValue(false)]
        public bool IncludeElementCounts { get; set; }
    }

    /// <summary>CON-05: levels ordered by elevation.</summary>
    [McpTool("list_levels", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "List the levels ordered by elevation: id, name, elevation in mm relative to the internal origin " +
                      "(elevationMm), the elevation Revit displays (displayedElevationMm, relative to the Project Base " +
                      "Point or Survey Point according to the level type's Elevation Base), building story flag, and the " +
                      "floor/ceiling plan views associated with it. Optionally counts the elements on each level. Read-only.")]
    public sealed class ListLevelsTool : McpTool<ListLevelsArgs>
    {
        protected override object Run(ToolContext ctx, ListLevelsArgs args)
        {
            var doc = ctx.Doc;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation).ThenBy(l => l.Name).ToList();
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null)
                .GroupBy(v => v.GenLevel.Id.GetValue())
                .ToDictionary(g => g.Key, g => g.ToList());

            var items = new List<object>();
            Level previous = null;
            foreach (var l in levels)
            {
                plans.TryGetValue(l.Id.GetValue(), out var views);
                views ??= new List<ViewPlan>();
                double? displayed = null;
                try
                {
                    var p = l.get_Parameter(BuiltInParameter.LEVEL_ELEV);
                    if (p != null) displayed = QueryHelpers.Mm(p.AsDouble());
                }
                catch
                {
                    // keep null
                }
                bool? story = null;
                try { story = l.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)?.AsInteger() == 1; }
                catch { /* keep null */ }

                items.Add(new
                {
                    id = l.Id.GetValue(),
                    uniqueId = l.UniqueId,
                    name = l.Name,
                    elevationMm = QueryHelpers.Mm(l.ProjectElevation),
                    displayedElevationMm = displayed,
                    distanceFromPreviousMm = previous == null ? (double?)null : QueryHelpers.Mm(l.ProjectElevation - previous.ProjectElevation),
                    isBuildingStory = story,
                    hasPlanView = views.Any(v => v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.EngineeringPlan),
                    planViews = views.Select(v => new { id = v.Id.GetValue(), name = v.Name, viewType = v.ViewType.ToString() }).ToList(),
                    elementCount = args.IncludeElementCounts
                        ? new FilteredElementCollector(doc).WherePasses(new ElementLevelFilter(l.Id)).WhereElementIsNotElementType().GetElementCount()
                        : (int?)null
                });
                previous = l;
            }
            return new { items, total = items.Count };
        }
    }
}
