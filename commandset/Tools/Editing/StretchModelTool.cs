using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class StretchModelArgs
    {
        [JsonProperty("axis")]
        [EnumValues("x", "y")]
        [DefaultValue("y")]
        [Description("Project axis the band is measured along: y (default, plan north-south) or x.")]
        public string Axis { get; set; } = "y";

        [JsonProperty("from", Required = Required.Always)]
        [Required]
        [Unit("mm")]
        [Description("Start of the band to remove, in mm along the axis (project internal origin).")]
        public double From { get; set; }

        [JsonProperty("to", Required = Required.Always)]
        [Required]
        [Unit("mm")]
        [Description("End of the band to remove (greater than from). The model shrinks by to − from.")]
        public double To { get; set; }

        [JsonProperty("movingSide")]
        [EnumValues("low", "high")]
        [DefaultValue("low")]
        [Description("Which side moves to close the gap: low (default: everything below from moves up by to − from) or high (everything above to moves down).")]
        public string MovingSide { get; set; } = "low";

        [JsonProperty("includeAnnotations")]
        [DefaultValue(true)]
        [Description("Also move/stretch view-specific elements of plan views (texts, tags, detail lines, filled regions). Dimensions follow their references. Default true.")]
        public bool IncludeAnnotations { get; set; } = true;

        [JsonProperty("excludeCategories")]
        [Description("Categories left alone (pt-BR or English). Default: Topography. Levels, grids, views, links and section marks are never touched.")]
        public List<string> ExcludeCategories { get; set; }
    }

    /// <summary>EDT-12: Revit-style stretch along one axis to remove a band of the model (shorten a building).</summary>
    [McpTool("stretch_model", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "editing", Batchable = false, TimeoutSeconds = 300,
        Description = "Shorten the model by removing a band along the project X or Y axis, like Revit's Stretch: " +
                      "everything entirely on the moving side moves by the band width; walls crossing the band are " +
                      "shortened (their doors/windows on the moving side move with the cut), floors, ceilings, roofs " +
                      "and filled regions crossing it have their sketches stretched, rooms and point-based elements on " +
                      "the moving side move, and plan annotations follow (includeAnnotations). Choose a band that " +
                      "crosses no wall running across it, no stair and no room you want to keep at full size: elements " +
                      "lying inside the band or that can't be stretched (arcs walls, railings, groups crossing it) are " +
                      "reported and left alone. Topography, levels, grids and views are not touched. Preview with " +
                      "dryRun first. One undo step.")]
    public sealed class StretchModelTool : McpTool<StretchModelArgs>, ISelfTransactedTool
    {
        private const double Eps = 1e-6;

        protected override void Validate(StretchModelArgs a)
        {
            if (a.To - a.From < 1)
                throw new ToolException("INVALID_ARGUMENTS", "to must be greater than from (at least 1 mm).");
            if (a.To - a.From > 50000)
                throw new ToolException("INVALID_ARGUMENTS", "The band is wider than 50 m; that is not a stretch.");
        }

        protected override object Run(ToolContext ctx, StretchModelArgs a)
        {
            var doc = ctx.Doc;
            bool alongY = a.Axis != "x";
            bool lowMoves = a.MovingSide != "high";
            double from = a.From / 304.8, to = a.To / 304.8, delta = to - from;
            var axis = alongY ? XYZ.BasisY : XYZ.BasisX;
            var shift = axis * (lowMoves ? delta : -delta);
            double C(XYZ p) => alongY ? p.Y : p.X;
            // New coordinate of a point: the moving side shifts, points inside the band collapse onto its kept edge.
            double Map(double c) => lowMoves
                ? (c <= from + Eps ? c + delta : Math.Max(c, to))
                : (c >= to - Eps ? c - delta : Math.Min(c, from));
            XYZ MapPoint(XYZ p) => alongY ? new XYZ(p.X, Map(p.Y), p.Z) : new XYZ(Map(p.X), p.Y, p.Z);
            bool OnMovingSide(double c) => lowMoves ? c <= from + Eps : c >= to - Eps;

            var excluded = new HashSet<long>((a.ExcludeCategories ?? new List<string> { "Topography" })
                .Select(n => ctx.Services.Categories.Resolve(n).Id.GetValue()));

            // Curves that belong to sketches are edited through their owner, never moved on their own.
            var sketchOwned = new HashSet<long>();
            foreach (Sketch s in new FilteredElementCollector(doc).OfClass(typeof(Sketch)))
            {
                try { foreach (var id in s.GetAllElements()) sketchOwned.Add(id.GetValue()); }
                catch { /* older sketches */ }
            }

            var move = new List<Element>();       // entirely on the moving side
            var cross = new List<Element>();      // crossing the band
            var skipped = new List<object>();
            int inBand = 0;

            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (!Eligible(e, excluded, sketchOwned, a.IncludeAnnotations, doc)) continue;
                var view = e.OwnerViewId != ElementId.InvalidElementId ? doc.GetElement(e.OwnerViewId) as View : null;
                BoundingBoxXYZ bb;
                try { bb = view != null ? e.get_BoundingBox(view) : e.get_BoundingBox(null); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { continue; }
                if (bb == null) continue;
                double lo = Math.Min(C(bb.Min), C(bb.Max)), hi = Math.Max(C(bb.Min), C(bb.Max));
                bool entirelyMoving = lowMoves ? hi <= from + Eps : lo >= to - Eps;
                bool entirelyKept = lowMoves ? lo >= to - Eps : hi <= from + Eps;
                bool insideBand = lo >= from - Eps && hi <= to + Eps;
                if (entirelyMoving) move.Add(e);
                else if (entirelyKept) continue;
                else if (insideBand) { inBand++; skipped.Add(Skip(e, "lies inside the band (left where it is)")); }
                else cross.Add(e);
            }

            // Hosted instances move with their host; groups' members move with the group.
            var moveIds = new HashSet<long>(move.Select(e => e.Id.GetValue()));
            var moveSet = move.Where(e => !(e is FamilyInstance fi && fi.Host != null && moveIds.Contains(fi.Host.Id.GetValue()))
                                          && !(e.GroupId != ElementId.InvalidElementId && moveIds.Contains(e.GroupId.GetValue())))
                              .ToList();
            var pinned = moveSet.Where(e => e.Pinned).ToList();
            foreach (var p in pinned) skipped.Add(Skip(p, "pinned (unpin it to stretch it)"));
            moveSet = moveSet.Where(e => !e.Pinned).ToList();

            var stretchedWalls = new List<object>();
            var stretchedSketches = new List<object>();
            var movedCrossing = new List<long>();

            // Sketch-based elements crossing the band are rebuilt with the stretched outline (editing a sketch in
            // place drags joined lines and walls locked to it, and Revit then asks the user what to do).
            var rebuild = cross.Where(e => SketchOf(doc, e) != null).ToList();
            foreach (var e in rebuild) cross.Remove(e);

            // Line-based targets from the original geometry too: when the moving side moves, Revit already drags
            // the ends of walls joined to it, and mapping the dragged end again would shorten twice.
            var lineTargets = new Dictionary<long, (Line original, Line target)>();
            foreach (var e in cross)
            {
                if (e.Location is LocationCurve olc && olc.Curve is Line ol)
                {
                    var p0 = MapPoint(ol.GetEndPoint(0));
                    var p1 = MapPoint(ol.GetEndPoint(1));
                    // Copy: a LocationCurve's curve is live and would change with the wall.
                    var original = Line.CreateBound(ol.GetEndPoint(0), ol.GetEndPoint(1));
                    if (p0.DistanceTo(p1) >= doc.Application.ShortCurveTolerance) lineTargets[e.Id.GetValue()] = (original, Line.CreateBound(p0, p1));
                }
            }

            var resolved = new List<string>();
            var group = new TransactionGroup(doc, "MCP: stretch_model" + (ctx.DryRun ? " (dry run)" : string.Empty));
            group.Start();
            try
            {
                // 2-4 in one transaction, so Revit only checks the final state (walls shortened and moved together,
                // the way its own Stretch does).
                using (var tx = new Transaction(doc, "MCP: stretch"))
                {
                    var failures = new StretchFailures(ctx.Warnings, resolved);
                    var options = tx.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(failures);
                    options.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(options);
                    tx.Start();

                    // 1) Rebuild the crossing sketch-based elements first, so nothing is locked to the walls that move.
                    foreach (var e in rebuild)
                    {
                        using (var sub = new SubTransaction(doc))
                        {
                            sub.Start();
                            long oldId = e.Id.GetValue();
                            string cat = e.Category?.Name, name = e.Name;
                            try
                            {
                                // What the original hosts may be deleted with it: re-place what Revit actually deletes.
                                // Face-based instances survive as orphans in place (and still move with the stretch).
                                // (A curtain roof's own panels and mullions go with it.)
                                var hosted = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                                    .Where(fi => fi.Host != null && fi.Host.Id == e.Id && !(fi is Panel) && !(fi is Mullion)).ToList();
                                var rehost = hosted.Select(h => SketchRebuild.Capture(h, MapPoint)).ToList();
                                SketchRebuild.LastGridReport = null;
                                var created = SketchRebuild.Rebuild(doc, e, SketchOf(doc, e), MapPoint);
                                doc.Delete(e.Id);
                                var replaced = rehost.Where(r => doc.GetElement(r.OldId.ToRevitElementId()) == null)
                                    .Select(r => SketchRebuild.Replace(doc, r, created)).ToList();
                                sub.Commit();
                                doc.Regenerate();
                                bool hasGeometry = created.get_BoundingBox(null) != null;
                                if (created is FootPrintRoof && SketchRebuild.LastGridReport != null) ctx.Warnings.Add(new ToolWarning($"Roof {created.Id.GetValue()}: {SketchRebuild.LastGridReport}"));
                                if (!hasGeometry) ctx.Warnings.Add(new ToolWarning($"Rebuilt {cat} {created.Id.GetValue()} ({name}) has no visible geometry; check it in 3D."));
                                stretchedSketches.Add(new { oldId, newId = created.Id.GetValue(), category = cat, name, hasGeometry,
                                    rehosted = replaced.Select(r => new { oldId = r.oldId, newId = r.newId, family = r.family }).ToList() });
                            }
                            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ToolException)
                            {
                                if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                                skipped.Add(Skip(e, "could not be rebuilt stretched: " + ex.Message + (ex is ToolException ? string.Empty : " (" + ex.GetType().Name + " in " + ex.TargetSite?.Name + ")")));
                            }
                        }
                    }

                    // 2) Point-based elements crossing the band (doors in a crossing wall, rooms, furniture on a
                    //    crossing floor) and line-based families that are objects rather than runs (blinds, shelves):
                    //    move the ones whose point (or middle) is on the moving side; they keep their size.
                    cross.RemoveAll(e => !e.IsValidObject);
                    foreach (var e in cross.ToList())
                    {
                        if (e is Wall) continue;
                        XYZ at = (e.Location as LocationPoint)?.Point;
                        if (at == null && e is FamilyInstance fi && fi.StructuralType == Autodesk.Revit.DB.Structure.StructuralType.NonStructural
                            && fi.Location is LocationCurve flc)
                            at = flc.Curve.Evaluate(0.5, true);
                        if (at == null) continue;
                        cross.Remove(e);
                        if (e.Pinned) { skipped.Add(Skip(e, "pinned")); continue; }
                        if (OnMovingSide(C(at))) { ElementTransformUtils.MoveElement(doc, e.Id, shift); movedCrossing.Add(e.Id.GetValue()); }
                    }

                    // 3) Everything on the moving side, in one move.
                    moveSet = moveSet.Where(e => e.IsValidObject).ToList();
                    if (moveSet.Count > 0) ElementTransformUtils.MoveElements(doc, moveSet.Select(e => e.Id).ToList(), shift);

                    // 4) Walls and other line-based elements crossing the band: new end points.
                    foreach (var e in cross.ToList())
                    {
                        if (!(e.Location is LocationCurve lc)) continue;
                        cross.Remove(e);
                        if (e.Pinned) { skipped.Add(Skip(e, "pinned")); continue; }
                        if (!(lc.Curve is Line)) { skipped.Add(Skip(e, "curved: not stretched")); continue; }
                        if (!lineTargets.TryGetValue(e.Id.GetValue(), out var lt)) { skipped.Add(Skip(e, "would collapse to nothing")); continue; }
                        if (lt.target.GetEndPoint(0).IsAlmostEqualTo(lt.original.GetEndPoint(0)) && lt.target.GetEndPoint(1).IsAlmostEqualTo(lt.original.GetEndPoint(1))) continue;
                        var now = (Line)lc.Curve;
                        if (!(now.GetEndPoint(0).IsAlmostEqualTo(lt.target.GetEndPoint(0)) && now.GetEndPoint(1).IsAlmostEqualTo(lt.target.GetEndPoint(1))))
                            lc.Curve = lt.target;
                        stretchedWalls.Add(new { id = e.Id.GetValue(), category = e.Category?.Name, name = e.Name,
                            lengthMm = Math.Round(lt.original.Length * 304.8), newLengthMm = Math.Round(lt.target.Length * 304.8) });
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        throw new ToolException(ToolErrorCodes.TransactionRolledBack,
                            "Revit rejected the stretch and rolled it back: " +
                            (failures.Errors.Count > 0 ? string.Join(" | ", failures.Errors.Distinct()) : "status " + status) +
                            (skipped.Count > 0 ? " Also not stretched: " + string.Join("; ", skipped.Select(o => JsonConvert.SerializeObject(o)).OrderBy(x => x.Contains("rebuilt") ? 0 : 1).Take(8)) : string.Empty),
                            "Nothing was changed. Choose a band that crosses no wall running across it, or fix the reported elements first.");
                }

                foreach (var e in cross) skipped.Add(Skip(e, "crosses the band and can't be stretched (move or edit it by hand)"));
                if (ctx.DryRun) group.RollBack();
                else group.Assimilate();
            }
            finally
            {
                if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                group.Dispose();
            }
            foreach (var r in resolved.Distinct()) ctx.Warnings.Add(new ToolWarning("Resolved as Revit would: " + r));

            return new
            {
                axis = alongY ? "y" : "x",
                bandMm = new { from = a.From, to = a.To, width = Math.Round(a.To - a.From, 1) },
                movingSide = lowMoves ? "low" : "high",
                movedCount = moveSet.Count + movedCrossing.Count,
                stretchedWalls,
                stretchedSketches,
                skipped,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"Removed a {Math.Round(a.To - a.From)} mm band along {(alongY ? "Y" : "X")}: {moveSet.Count + movedCrossing.Count} element(s) moved, " +
                                $"{stretchedWalls.Count} line-based element(s) and {stretchedSketches.Count} sketch(es) stretched, {skipped.Count} skipped."
            };
        }

        private static object Skip(Element e, string reason) =>
            new { id = e.Id.GetValue(), category = e.Category?.Name, name = e.Name, reason };

        private static bool Eligible(Element e, HashSet<long> excluded, HashSet<long> sketchOwned, bool annotations, Document doc)
        {
            if (e.Category == null || e is View || e is Sketch || e is SketchPlane || e is Level || e is Grid ||
                e is RevitLinkInstance || e is ImportInstance || e is Dimension || e is ElementType) return false;
            // Curtain sub-elements follow their wall or roof.
            if (e is CurtainGridLine || e is Mullion || e is Panel) return false;
            if (excluded.Contains(e.Category.Id.GetValue())) return false;
            if (sketchOwned.Contains(e.Id.GetValue())) return false;
            if (e.ViewSpecific)
            {
                if (!annotations) return false;
                // Section/elevation annotations stay; view templates own nothing to stretch.
                if (!(doc.GetElement(e.OwnerViewId) is ViewPlan plan) || plan.IsTemplate) return false;
            }
            var cat = (BuiltInCategory)(int)e.Category.Id.GetValue();
            switch (cat)
            {
                case BuiltInCategory.OST_Viewers:
                case BuiltInCategory.OST_Elev:
                case BuiltInCategory.OST_Sections:
                case BuiltInCategory.OST_Cameras:
                case BuiltInCategory.OST_VolumeOfInterest:
                case BuiltInCategory.OST_SketchLines:
                case BuiltInCategory.OST_ProjectBasePoint:
                case BuiltInCategory.OST_SharedBasePoint:
                case BuiltInCategory.OST_IOS_GeoSite:
                    return false;
            }
            return e.Category.CategoryType == CategoryType.Model || e.ViewSpecific;
        }

        private static Sketch SketchOf(Document doc, Element e)
        {
            if (!(e is Floor || e is Ceiling || e is RoofBase || e is FilledRegion || e is Opening)) return null;
            try
            {
                return e.GetDependentElements(new ElementClassFilter(typeof(Sketch)))
                    .Select(doc.GetElement).OfType<Sketch>().FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>
        /// Warnings are kept as warnings. Two errors a manual stretch also produces are resolved the way the user
        /// would in Revit's dialog: dimensions that lost a reference are deleted, and joins or sketch locks that can't hold are
        /// undone. Any other error rolls the stretch back.
        /// </summary>
        private sealed class StretchFailures : IFailuresPreprocessor
        {
            private readonly IList<ToolWarning> _warnings;
            private readonly List<string> _resolved;
            public List<string> Errors { get; } = new List<string>();

            public StretchFailures(IList<ToolWarning> warnings, List<string> resolved)
            {
                _warnings = warnings;
                _resolved = resolved;
            }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var doc = accessor.GetDocument();
                bool resolvedAny = false;
                foreach (var f in accessor.GetFailureMessages())
                {
                    var text = f.GetDescriptionText();
                    if (f.GetSeverity() == FailureSeverity.Warning)
                    {
                        _warnings.Add(new ToolWarning(text, f.GetFailingElementIds().Select(i => i.GetValue()).ToList()));
                        accessor.DeleteWarning(f);
                        continue;
                    }
                    var failing = f.GetFailingElementIds().Select(doc.GetElement).Where(x => x != null).ToList();
                    bool onlyDimensions = failing.Count > 0 && failing.All(x => x is Dimension);
                    var undo = f.HasResolutionOfType(FailureResolutionType.DetachElements) ? FailureResolutionType.DetachElements
                        : f.HasResolutionOfType(FailureResolutionType.UnlockConstraints) ? FailureResolutionType.UnlockConstraints
                        : (FailureResolutionType?)null;
                    if (undo.HasValue)
                    {
                        f.SetCurrentResolutionType(undo.Value);
                        accessor.ResolveFailure(f);
                        _resolved.Add(text + (undo == FailureResolutionType.DetachElements ? " (elements unjoined)" : " (constraints removed)"));
                        resolvedAny = true;
                    }
                    else if (onlyDimensions && f.HasResolutionOfType(FailureResolutionType.DeleteElements))
                    {
                        f.SetCurrentResolutionType(FailureResolutionType.DeleteElements);
                        accessor.ResolveFailure(f);
                        _resolved.Add(text + $" ({failing.Count} dimension(s) deleted)");
                        resolvedAny = true;
                    }
                    else Errors.Add(text + (failing.Count > 0 ? " [" + string.Join(", ", failing.Take(6).Select(x => x.Id.GetValue() + " " + (x.Category?.Name ?? x.GetType().Name) + " '" + x.Name + "'")) + "]" : string.Empty));
                }
                if (Errors.Count > 0) return FailureProcessingResult.ProceedWithRollBack;
                return resolvedAny ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
            }
        }
    }
}
