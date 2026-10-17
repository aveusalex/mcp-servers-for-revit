using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Views
{
    public sealed class ViewRangePlaneSpec
    {
        [JsonProperty("level")]
        [Description("Level the plane is measured from: a level name or id, \"current\" (the view's level, default), \"above\" (level above) or \"unlimited\" (top/depth only).")]
        public string Level { get; set; }

        [JsonProperty("offset")]
        [Description("Offset from that level, in mm (default: keep the current offset).")]
        [Unit("mm")]
        public double? Offset { get; set; }
    }

    public sealed class SetViewRangeArgs
    {
        [JsonProperty("views", Required = Required.Always)]
        [Description("Plan views (names or ids), up to 200.")]
        [MinLength(1)]
        [MaxLength(200)]
        public List<string> Views { get; set; }

        [JsonProperty("top")]
        [Description("Top clip plane.")]
        public ViewRangePlaneSpec Top { get; set; }

        [JsonProperty("cut")]
        [Description("Cut plane (e.g. offset 1200 from the current level).")]
        public ViewRangePlaneSpec Cut { get; set; }

        [JsonProperty("bottom")]
        [Description("Bottom clip plane.")]
        public ViewRangePlaneSpec Bottom { get; set; }

        [JsonProperty("viewDepth")]
        [Description("View depth plane (must be at or below bottom).")]
        public ViewRangePlaneSpec ViewDepth { get; set; }

        [JsonProperty("atomic")]
        [Description("true (default): any view that fails rolls the whole call back. false: failures are reported per view.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;
    }

    /// <summary>VIS-11: plan view range in batch, validated top ≥ cut ≥ bottom ≥ depth, with old and new values.</summary>
    [McpTool("set_view_range", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views",
        Description = "Set the view range of plan views: top, cut, bottom and viewDepth, each {level, offset mm} " +
                      "where level is a level name/id, \"current\", \"above\" or \"unlimited\". Unspecified parts are " +
                      "kept. Elevations are checked top >= cut >= bottom >= depth before writing; plans whose range is " +
                      "controlled by a view template are refused with the template name. Returns old and new ranges. " +
                      "Undo: Ctrl+Z.")]
    public sealed class SetViewRangeTool : McpTool<SetViewRangeArgs>
    {
        private static readonly PlanViewPlane[] Planes =
            { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane };

        protected override void Validate(SetViewRangeArgs args)
        {
            if (args.Top == null && args.Cut == null && args.Bottom == null && args.ViewDepth == null)
                throw new ToolException("INVALID_ARGUMENTS", "Pass at least one of top, cut, bottom, viewDepth.");
        }

        protected override object Run(ToolContext ctx, SetViewRangeArgs args)
        {
            var r = ItemLoop.Run(ctx, args.Views.Distinct().ToList(), !args.Atomic, (reference, index) =>
            {
                var plan = ctx.Services.Lookup.View(reference) as ViewPlan
                           ?? throw new ToolException("NOT_A_PLAN", $"'{reference}' is not a plan view.");
                var range = plan.GetViewRange();
                var before = Describe(ctx.Doc, plan, range);

                var specs = new[] { args.Top, args.Cut, args.Bottom, args.ViewDepth };
                for (var i = 0; i < Planes.Length; i++)
                {
                    var spec = specs[i];
                    if (spec == null) continue;
                    if (!string.IsNullOrWhiteSpace(spec.Level)) range.SetLevelId(Planes[i], LevelId(ctx, plan, Planes[i], spec.Level));
                    if (spec.Offset.HasValue) range.SetOffset(Planes[i], Mm.ToFt(spec.Offset.Value));
                }

                var z = Planes.Select(p => Elevation(ctx.Doc, plan, range, p)).ToArray();
                for (var i = 0; i < 3; i++)
                {
                    if (z[i].HasValue && z[i + 1].HasValue && z[i].Value + 1e-6 < z[i + 1].Value)
                        throw new ToolException("INVALID_VIEW_RANGE",
                            $"{Planes[i]} ({Mm.FromFt(z[i].Value)} mm) would be below {Planes[i + 1]} ({Mm.FromFt(z[i + 1].Value)} mm) in '{plan.Name}'.",
                            "Keep top >= cut >= bottom >= view depth (elevations in mm from the project internal origin).");
                }

                try
                {
                    plan.SetViewRange(range);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    var template = plan.ViewTemplateId != ElementId.InvalidElementId ? ctx.Doc.GetElement(plan.ViewTemplateId)?.Name : null;
                    throw new ToolException("VIEW_RANGE_NOT_APPLIED", $"Revit refused the view range of '{plan.Name}': {ex.Message}",
                        template != null ? $"View template '{template}' may control the view range; change the template instead." : null);
                }

                var after = Describe(ctx.Doc, plan, plan.GetViewRange());
                return new { index, viewId = plan.Id.GetValue(), name = plan.Name, before, after };
            }, v => v, "View");
            return new { views = r.Done, failed = r.Failed, dryRun = ctx.DryRun };
        }

        private static ElementId LevelId(ToolContext ctx, ViewPlan plan, PlanViewPlane plane, string text)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "current":
                case "associated":
                    return PlanViewRange.Current;
                case "above":
                    return PlanViewRange.LevelAbove;
                case "unlimited":
                    if (plane != PlanViewPlane.TopClipPlane && plane != PlanViewPlane.ViewDepthPlane)
                        throw new ToolException("INVALID_ARGUMENTS", "Only top and viewDepth can be unlimited.");
                    return PlanViewRange.Unlimited;
                default:
                    return ctx.Services.Lookup.Level(text).Id;
            }
        }

        /// <summary>Absolute elevation (ft) of a plane, or null when unlimited / unresolvable.</summary>
        private static double? Elevation(Document doc, ViewPlan plan, PlanViewRange range, PlanViewPlane plane)
        {
            var id = range.GetLevelId(plane);
            if (id == PlanViewRange.Unlimited || id == ElementId.InvalidElementId) return null;
            Level level;
            if (id == PlanViewRange.Current) level = plan.GenLevel;
            else if (id == PlanViewRange.LevelAbove)
            {
                var baseZ = plan.GenLevel?.Elevation ?? 0;
                level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .Where(l => l.Elevation > baseZ + 1e-6).OrderBy(l => l.Elevation).FirstOrDefault();
            }
            else level = doc.GetElement(id) as Level;
            if (level == null) return null;
            return level.Elevation + range.GetOffset(plane);
        }

        private static object Describe(Document doc, ViewPlan plan, PlanViewRange range)
        {
            var result = new Dictionary<string, object>();
            string[] keys = { "top", "cut", "bottom", "viewDepth" };
            for (var i = 0; i < Planes.Length; i++)
            {
                var id = range.GetLevelId(Planes[i]);
                string level = id == PlanViewRange.Unlimited ? "unlimited"
                    : id == PlanViewRange.Current ? "current"
                    : id == PlanViewRange.LevelAbove ? "above"
                    : doc.GetElement(id)?.Name;
                var z = Elevation(doc, plan, range, Planes[i]);
                result[keys[i]] = new
                {
                    level,
                    offset = Mm.FromFt(range.GetOffset(Planes[i])),
                    elevation = z.HasValue ? Mm.FromFt(z.Value) : (double?)null
                };
            }
            return result;
        }
    }
}
