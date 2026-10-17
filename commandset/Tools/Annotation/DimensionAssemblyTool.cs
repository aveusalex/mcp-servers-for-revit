using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Annotation.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Annotation
{
    public sealed class DimensionAssemblyArgs
    {
        [JsonProperty("assemblyId")]
        [Description("Assembly instance id. Default: assemblyName, or the only assembly in the model.")]
        public long? AssemblyId { get; set; }

        [JsonProperty("assemblyName")]
        [Description("Assembly type name (exact or partial) when assemblyId is not given.")]
        public string AssemblyName { get; set; }

        [JsonProperty("views")]
        [Description("Views to dimension (names or ids). Default: every elevation/section/plan view of the assembly (3D views and schedules are skipped and listed).")]
        public List<string> Views { get; set; }

        [JsonProperty("detail")]
        [EnumValues("overall", "pieces", "both")]
        [DefaultValue("overall")]
        [Description("overall (default): one horizontal and one vertical dimension between the extreme faces. pieces: a chain through every piece face (each panel thickness and gap). both: pieces chain next to the object and the overall one outside it.")]
        public string Detail { get; set; } = "overall";

        [JsonProperty("horizontalSide")]
        [EnumValues("bottom", "top")]
        [DefaultValue("bottom")]
        [Description("Where the horizontal dimensions go. Default bottom.")]
        public string HorizontalSide { get; set; } = "bottom";

        [JsonProperty("verticalSide")]
        [EnumValues("left", "right")]
        [DefaultValue("left")]
        [Description("Where the vertical dimensions go. Default left.")]
        public string VerticalSide { get; set; } = "left";

        [JsonProperty("offsetPaperMm")]
        [Unit("mm")]
        [Range(1, 100)]
        [DefaultValue(8)]
        [Description("Distance from the object to the first dimension line, in paper mm (scaled by each view's scale). Default 8.")]
        public double OffsetPaperMm { get; set; } = 8;

        [JsonProperty("stepPaperMm")]
        [Unit("mm")]
        [Range(1, 100)]
        [DefaultValue(7)]
        [Description("Distance between stacked dimension lines (detail both), in paper mm. Default 7.")]
        public double StepPaperMm { get; set; } = 7;

        [JsonProperty("minSegmentMm")]
        [Unit("mm")]
        [Range(0, 1000)]
        [DefaultValue(5)]
        [Description("pieces chain: faces closer than this to the previous one are left out (e.g. 1.5 mm door overlay gaps), so texts don't pile up. The extreme faces are always kept. Default 5; 0 keeps every face.")]
        public double MinSegmentMm { get; set; } = 5;

        [JsonProperty("dimensionType")]
        [Description("Linear dimension type (name or id). Default: the document's default.")]
        public string DimensionType { get; set; }
    }

    /// <summary>ANO-14: dimension the views of an assembly (millwork detailing).</summary>
    [McpTool("dimension_assembly", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "annotation", TimeoutSeconds = 120,
        Description = "Dimension the views of an assembly (conjunto, e.g. a millwork module from create_millwork_module + " +
                      "manage_assembly + create_assembly_views): in each elevation/section/plan of the assembly, a " +
                      "horizontal and a vertical dimension along the view's own axes, either overall (extreme faces), " +
                      "per piece (a chain through every face: panel thicknesses, gaps, shelves) or both stacked. " +
                      "Offsets are in paper mm, so it reads the same at any scale. Views it can't use are listed with " +
                      "the reason. Returns each dimension's id and segment values in mm. Undo: Ctrl+Z.")]
    public sealed class DimensionAssemblyTool : McpTool<DimensionAssemblyArgs>
    {
        private const double MergeTol = 0.5 / 304.8;

        protected override object Run(ToolContext ctx, DimensionAssemblyArgs a)
        {
            var doc = ctx.Doc;
            var asm = AssemblyDetailing.Resolve(doc, a.AssemblyId, a.AssemblyName);
            var skipped = new List<SkippedView>();
            var views = AssemblyDetailing.Views(ctx, asm, a.Views, false, skipped);
            var members = AssemblyDetailing.Members(doc, asm);

            DimensionType dimType = null;
            if (!string.IsNullOrWhiteSpace(a.DimensionType))
            {
                var types = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                    .Where(d => d.StyleType == DimensionStyleType.Linear).ToList();
                dimType = ctx.Services.Lookup.ByNameOrId(a.DimensionType, types, "dimension type");
            }

            var perView = new List<object>();
            int total = 0;
            foreach (var view in views)
            {
                var (minR, maxR, minU, maxU) = AssemblyDetailing.Extents(view, members);
                var faces = Faces(view, members);
                double off = AssemblyDetailing.PaperToModel(view, a.OffsetPaperMm);
                double step = AssemblyDetailing.PaperToModel(view, a.StepPaperMm);
                var dims = new List<object>();
                var notes = new List<string>();

                foreach (bool horizontal in new[] { true, false })
                {
                    var axis = horizontal ? view.RightDirection : view.UpDirection;
                    var stations = Stations(faces, axis, a.MinSegmentMm / 304.8);
                    if (stations.Count < 2)
                    {
                        notes.Add($"{(horizontal ? "horizontal" : "vertical")}: fewer than 2 faces seen edge-on in this view");
                        continue;
                    }
                    var levels = new List<List<(double c, Reference r)>>();
                    if (a.Detail != "overall" && stations.Count > 2) levels.Add(stations);
                    if (a.Detail != "pieces" || stations.Count == 2) levels.Add(new List<(double, Reference)> { stations.First(), stations.Last() });

                    for (int i = 0; i < levels.Count; i++)
                    {
                        double distance = off + i * step;
                        Line line;
                        if (horizontal)
                        {
                            double u = a.HorizontalSide == "top" ? maxU + distance : minU - distance;
                            line = Line.CreateBound(AssemblyDetailing.InView(view, minR - off, u), AssemblyDetailing.InView(view, maxR + off, u));
                        }
                        else
                        {
                            double r = a.VerticalSide == "right" ? maxR + distance : minR - distance;
                            line = Line.CreateBound(AssemblyDetailing.InView(view, r, minU - off), AssemblyDetailing.InView(view, r, maxU + off));
                        }
                        var refs = new ReferenceArray();
                        foreach (var s in levels[i]) refs.Append(s.r);
                        try
                        {
                            var dim = dimType == null ? doc.Create.NewDimension(view, line, refs) : doc.Create.NewDimension(view, line, refs, dimType);
                            if (dim == null) { notes.Add("Revit did not create a dimension"); continue; }
                            total++;
                            var outward = horizontal
                                ? (a.HorizontalSide == "top" ? view.UpDirection : view.UpDirection.Negate())
                                : (a.VerticalSide == "right" ? view.RightDirection : view.RightDirection.Negate());
                            int moved = MoveCrowdedTexts(view, dim, outward);
                            if (moved > 0) notes.Add($"{(horizontal ? "horizontal" : "vertical")}: {moved} short segment text(s) moved off the line so they don't overlap");
                            dims.Add(new
                            {
                                id = dim.Id.GetValue(),
                                direction = horizontal ? "horizontal" : "vertical",
                                level = levels[i].Count == 2 && i == levels.Count - 1 ? "overall" : "pieces",
                                segmentsMm = Segments(dim)
                            });
                        }
                        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ArgumentException || ex is Autodesk.Revit.Exceptions.InvalidOperationException)
                        {
                            notes.Add($"{(horizontal ? "horizontal" : "vertical")}: Revit refused the dimension ({ex.Message})");
                        }
                    }
                }
                perView.Add(new { view = new { id = view.Id.GetValue(), name = view.Name, viewType = view.ViewType.ToString() }, dimensions = dims, notes });
            }

            if (total == 0)
                throw new ToolException("DIMENSION_FAILED", $"No dimension could be placed in the views of '{asm.AssemblyTypeName}'.",
                    "Check that the members have planar faces seen edge-on in those views (elevations/sections of the assembly).");

            return new
            {
                assembly = new { id = asm.Id.GetValue(), name = asm.AssemblyTypeName },
                detail = a.Detail,
                views = perView,
                skippedViews = skipped,
                dimensionCount = total,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"{total} dimension(s) in {perView.Count} view(s) of '{asm.AssemblyTypeName}'."
            };
        }

        private static List<(XYZ normal, XYZ origin, Reference reference)> Faces(View view, IEnumerable<Element> members)
        {
            var opts = new Options { ComputeReferences = true, View = view, IncludeNonVisibleObjects = false };
            var faces = new List<(XYZ, XYZ, Reference)>();
            foreach (var e in members)
            {
                GeometryElement geo;
                try { geo = e.get_Geometry(opts); }
                catch { continue; }
                if (geo != null) Collect(geo, faces);
            }
            return faces;
        }

        private static void Collect(GeometryElement geo, List<(XYZ, XYZ, Reference)> faces)
        {
            foreach (var obj in geo)
            {
                if (obj is Solid solid && solid.Faces.Size > 0)
                {
                    foreach (Face f in solid.Faces)
                        if (f is PlanarFace pf && pf.Reference != null)
                            faces.Add((pf.FaceNormal, pf.Origin, pf.Reference));
                }
                else if (obj is GeometryInstance gi)
                {
                    Collect(gi.GetInstanceGeometry(), faces);
                }
            }
        }

        /// <summary>Distinct positions along <paramref name="axis"/> of the faces perpendicular to it, sorted, one reference each.</summary>
        private static List<(double c, Reference r)> Stations(List<(XYZ normal, XYZ origin, Reference reference)> faces, XYZ axis, double minGap)
        {
            var sorted = faces.Where(f => Math.Abs(Math.Abs(f.normal.DotProduct(axis)) - 1) <= 1e-4)
                .Select(f => (c: f.origin.DotProduct(axis), r: f.reference)).OrderBy(x => x.c).ToList();
            var merged = new List<(double c, Reference r)>();
            if (sorted.Count == 0) return merged;
            double gap = Math.Max(MergeTol, minGap);
            foreach (var s in sorted)
                if (merged.Count == 0 || s.c - merged[merged.Count - 1].c > gap) merged.Add(s);
            // Keep the true extreme: if the last face was dropped as too close, it replaces the previous station.
            var last = sorted[sorted.Count - 1];
            if (last.c - merged[merged.Count - 1].c > MergeTol)
            {
                if (merged.Count > 1) merged[merged.Count - 1] = last;
                else merged.Add(last);
            }
            return merged;
        }

        /// <summary>
        /// Texts of segments shorter than about two characters on paper collide with their neighbours. As in hand
        /// drafting: a short first/last segment gets its text outside the chain, past its end; a short middle one is
        /// moved off the line, away from the object (alternating distance for consecutive ones). Returns how many moved.
        /// </summary>
        private static int MoveCrowdedTexts(View view, Dimension dim, XYZ outward)
        {
            if (dim.NumberOfSegments < 2 || !(dim.Curve is Line line)) return 0;
            double shortLen = AssemblyDetailing.PaperToModel(view, 4);
            double shift = AssemblyDetailing.PaperToModel(view, 3.5);
            double clearance = AssemblyDetailing.PaperToModel(view, 3);
            var along = line.Direction; // references were added in ascending order along it
            int moved = 0, run = 0, count = dim.NumberOfSegments, index = -1;
            foreach (DimensionSegment seg in dim.Segments)
            {
                index++;
                double value = seg.Value ?? 0;
                if (value >= shortLen) { run = 0; continue; }
                try
                {
                    if (!seg.IsTextPositionAdjustable()) continue;
                    if (index == 0) seg.TextPosition = seg.Origin - along * (value / 2 + clearance);
                    else if (index == count - 1) seg.TextPosition = seg.Origin + along * (value / 2 + clearance);
                    else
                    {
                        seg.TextPosition = seg.TextPosition + outward * shift * (1 + run % 2);
                        run++;
                    }
                    moved++;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // cosmetic: leave the text where Revit put it
                }
            }
            return moved;
        }

        private static List<double> Segments(Dimension dim)
        {
            if (dim.NumberOfSegments == 0) return new List<double> { AssemblyDetailing.Mm(dim.Value ?? 0) };
            return dim.Segments.Cast<DimensionSegment>().Select(s => AssemblyDetailing.Mm(s.Value ?? 0)).ToList();
        }
    }
}
