using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.QA.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA
{
    public sealed class CheckLevelsGridsArgs
    {
        [JsonProperty("tolerance")]
        [Description("Distance under which two levels or grids count as duplicated/overlapping (default 1 mm).")]
        [Unit("mm")]
        [DefaultValue(1.0)]
        public double Tolerance { get; set; } = 1.0;

        [JsonProperty("requireFloorPlan")]
        [Description("Report building-story levels with no floor plan (default true).")]
        [DefaultValue(true)]
        public bool RequireFloorPlan { get; set; } = true;

        [JsonProperty("requireCeilingPlan")]
        [Description("Report building-story levels with no ceiling plan (default false).")]
        [DefaultValue(false)]
        public bool RequireCeilingPlan { get; set; }

        [JsonProperty("offAxisAngle")]
        [Description("Report straight grids that are off the orthogonal axes by more than 0.001 deg but less than this (default 1 deg): usually drawing mistakes.")]
        [Unit("deg")]
        [DefaultValue(1.0)]
        public double OffAxisAngle { get; set; } = 1.0;
    }

    /// <summary>QA-20: levels and grids sanity.</summary>
    [McpTool("check_levels_grids", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "qa", Idempotent = true,
        TimeoutSeconds = 60,
        Description = "Check levels and grids: levels at the same elevation (within tolerance, mm), building-story levels " +
                      "without a floor plan (or ceiling plan), duplicated or overlapping grids (same line/arc within " +
                      "tolerance) and straight grids slightly off the orthogonal axes. Lists every level with its " +
                      "elevation in mm. Read-only.")]
    public sealed class CheckLevelsGridsTool : McpTool<CheckLevelsGridsArgs>
    {
        protected override object Run(ToolContext ctx, CheckLevelsGridsArgs a)
        {
            var doc = ctx.Doc;
            double tol = UnitPolicy.ToInternal(Math.Max(0, a.Tolerance), UnitSpec.Length);
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate && v.GenLevel != null).ToList();

            var duplicateElevations = new List<object>();
            for (int i = 0; i + 1 < levels.Count; i++)
            {
                var group = new List<Level> { levels[i] };
                while (i + 1 < levels.Count && Math.Abs(levels[i + 1].Elevation - group[0].Elevation) <= tol)
                    group.Add(levels[++i]);
                if (group.Count > 1)
                    duplicateElevations.Add(new
                    {
                        elevation = QaSupport.Mm(group[0].Elevation),
                        levels = group.Select(l => new { id = l.Id.GetValue(), name = l.Name }).ToList()
                    });
            }

            var withoutPlans = new List<object>();
            foreach (var l in levels)
            {
                bool story = true;
                try { story = l.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)?.AsInteger() != 0; }
                catch { /* keep true */ }
                if (!story) continue;
                var mine = plans.Where(p => p.GenLevel.Id == l.Id).ToList();
                var missing = new List<string>();
                if (a.RequireFloorPlan && !mine.Any(p => p.ViewType == ViewType.FloorPlan || p.ViewType == ViewType.EngineeringPlan)) missing.Add("floor plan");
                if (a.RequireCeilingPlan && !mine.Any(p => p.ViewType == ViewType.CeilingPlan)) missing.Add("ceiling plan");
                if (missing.Count > 0) withoutPlans.Add(new { id = l.Id.GetValue(), name = l.Name, missing });
            }

            var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
            var duplicateGrids = new List<object>();
            var offAxis = new List<object>();
            double offLimit = UnitPolicy.ToInternal(a.OffAxisAngle, UnitSpec.Angle);
            double minAngle = UnitPolicy.ToInternal(0.001, UnitSpec.Angle);
            for (int i = 0; i < grids.Count; i++)
            {
                var ci = SafeCurve(grids[i]);
                if (ci == null) continue;
                if (ci is Line li)
                {
                    var d = li.Direction;
                    double ang = Math.Atan2(d.Y, d.X);
                    double rem = Math.Abs(Math.IEEERemainder(ang, Math.PI / 2));
                    if (rem > minAngle && rem < offLimit)
                        offAxis.Add(new { id = grids[i].Id.GetValue(), name = grids[i].Name, offBy = Math.Round(UnitPolicy.FromInternal(rem, UnitSpec.Angle, out _), 4), unit = "deg" });
                }
                for (int j = i + 1; j < grids.Count; j++)
                {
                    var cj = SafeCurve(grids[j]);
                    if (cj == null) continue;
                    var why = Overlap(ci, cj, tol);
                    if (why != null)
                        duplicateGrids.Add(new
                        {
                            a = new { id = grids[i].Id.GetValue(), name = grids[i].Name },
                            b = new { id = grids[j].Id.GetValue(), name = grids[j].Name },
                            problem = why
                        });
                }
            }

            int issues = duplicateElevations.Count + withoutPlans.Count + duplicateGrids.Count + offAxis.Count;
            return new
            {
                ok = issues == 0,
                issueCount = issues,
                levels = levels.Select(l => new { id = l.Id.GetValue(), name = l.Name, elevation = QaSupport.Mm(l.Elevation) }).ToList(),
                elevationUnit = "mm",
                duplicateElevations,
                levelsWithoutPlans = withoutPlans,
                gridCount = grids.Count,
                duplicateOrOverlappingGrids = duplicateGrids,
                gridsOffAxis = offAxis
            };
        }

        private static Curve SafeCurve(Grid g)
        {
            try { return g.Curve; }
            catch { return null; }
        }

        /// <summary>"duplicate" (same geometry), "overlapping" (collinear and sharing a stretch) or null.</summary>
        private static string Overlap(Curve a, Curve b, double tol)
        {
            if (a is Line la && b is Line lb)
            {
                var a0 = Flat(la.GetEndPoint(0)); var a1 = Flat(la.GetEndPoint(1));
                var b0 = Flat(lb.GetEndPoint(0)); var b1 = Flat(lb.GetEndPoint(1));
                if ((a0.DistanceTo(b0) <= tol && a1.DistanceTo(b1) <= tol) || (a0.DistanceTo(b1) <= tol && a1.DistanceTo(b0) <= tol))
                    return "duplicate";
                var dir = (a1 - a0).Normalize();
                var dirB = (b1 - b0).Normalize();
                if (Math.Abs(Math.Abs(dir.DotProduct(dirB)) - 1) > 1e-6) return null;
                // distance of b's endpoints from a's infinite line
                double DistToLine(XYZ p) => (p - a0).CrossProduct(dir).GetLength();
                if (DistToLine(b0) > tol || DistToLine(b1) > tol) return null;
                double len = (a1 - a0).GetLength();
                double t0 = (b0 - a0).DotProduct(dir), t1 = (b1 - a0).DotProduct(dir);
                double lo = Math.Min(t0, t1), hi = Math.Max(t0, t1);
                return hi > tol && lo < len - tol ? "overlapping" : null;
            }
            if (a is Arc aa && b is Arc ab)
            {
                if (Flat(aa.Center).DistanceTo(Flat(ab.Center)) <= tol && Math.Abs(aa.Radius - ab.Radius) <= tol)
                    return "duplicate (same centre and radius)";
            }
            return null;
        }

        private static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);
    }
}
