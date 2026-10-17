using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture
{
    public sealed class CutoutMm
    {
        [JsonProperty("center")]
        [Required]
        [Description("Centre of the cut-out (sink/cooktop) in plan, mm.")]
        public PointMm Center { get; set; }

        [JsonProperty("width")]
        [Required]
        [Unit("mm")]
        [Range(1, 10000)]
        [Description("Cut-out size along X before rotation, mm.")]
        public double Width { get; set; }

        [JsonProperty("depth")]
        [Required]
        [Unit("mm")]
        [Range(1, 10000)]
        [Description("Cut-out size along Y before rotation, mm.")]
        public double Depth { get; set; }

        [JsonProperty("rotation")]
        [Unit("deg")]
        [DefaultValue(0)]
        [Description("Counter-clockwise rotation in degrees. Default 0.")]
        public double Rotation { get; set; }
    }

    public sealed class CreateCountertopArgs
    {
        [JsonProperty("points")]
        [MinLength(3)]
        [MaxLength(200)]
        [Description("Outline of the countertop in plan, mm (give points or rectangle). Edge i runs from point i to point i+1 (the last edge closes the outline).")]
        public List<PointMm> Points { get; set; }

        [JsonProperty("rectangle")]
        [Description("Rectangular outline. Edges: 0 = from the origin along width (front), 1 = right side, 2 = back, 3 = left side (before rotation).")]
        public RectangleMm Rectangle { get; set; }

        [JsonProperty("level")]
        [Required]
        [Description("Level by name or id.")]
        public string Level { get; set; }

        [JsonProperty("height")]
        [Required]
        [Unit("mm")]
        [Range(0, 10000)]
        [Description("Height of the top surface above the level in mm (e.g. 900 kitchen, 800 bathroom).")]
        public double Height { get; set; }

        [JsonProperty("thickness")]
        [Required]
        [Unit("mm")]
        [Range(5, 300)]
        [Description("Stone thickness in mm (e.g. 20 or 30). With method floor it must match the floor type's thickness.")]
        public double Thickness { get; set; }

        [JsonProperty("method")]
        [Required]
        [EnumValues("floor", "directshape")]
        [Description("floor: a floor of the named floorType (schedulable as a floor). directshape: a Generic Model solid with the given material.")]
        public string Method { get; set; }

        [JsonProperty("floorType")]
        [Description("method floor: floor type for the top (e.g. \"Tampo granito 20 mm\").")]
        public string FloorType { get; set; }

        [JsonProperty("material")]
        [Description("Material by name or id for the directshape parts (top with method directshape, apron and backsplash with both methods).")]
        public string Material { get; set; }

        [JsonProperty("cutouts")]
        [MaxLength(20)]
        [Description("Rectangular cut-outs through the top (sink, cooktop).")]
        public List<CutoutMm> Cutouts { get; set; }

        [JsonProperty("frontEdges")]
        [MaxLength(200)]
        [Description("Edge indexes that get a front apron (saia). Default none.")]
        public List<int> FrontEdges { get; set; }

        [JsonProperty("apronHeight")]
        [Unit("mm")]
        [Range(0, 1000)]
        [Description("Total visible height of the front apron in mm, top thickness included (e.g. 40). Needs frontEdges.")]
        public double? ApronHeight { get; set; }

        [JsonProperty("backsplashEdges")]
        [MaxLength(200)]
        [Description("Edge indexes that get a backsplash (rodabanca/frontão). Default none.")]
        public List<int> BacksplashEdges { get; set; }

        [JsonProperty("backsplashHeight")]
        [Unit("mm")]
        [Range(0, 2000)]
        [Description("Backsplash height above the top in mm (e.g. 100). Needs backsplashEdges.")]
        public double? BacksplashHeight { get; set; }

        [JsonProperty("mark")]
        [Description("Value for the Mark parameter (e.g. \"T01\").")]
        public string Mark { get; set; }
    }

    /// <summary>ARQ-26: stone countertop (tampo) by outline, thickness, cut-outs, front apron and backsplash.</summary>
    [McpTool("create_countertop", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Create a stone countertop (tampo) from a plan outline or rectangle in mm: top surface height above " +
                      "the level, thickness, rectangular cut-outs (sink/cooktop), optional front apron (saia) and " +
                      "backsplash (rodabanca) on chosen edges. method floor uses a named floor type (thickness must " +
                      "match) so it schedules as a floor; method directshape builds a Generic Model solid with a material. " +
                      "Returns ids, area (m²) and volume (m³). One Ctrl+Z undoes the call.")]
    public sealed class CreateCountertopTool : McpTool<CreateCountertopArgs>
    {
        protected override void Validate(CreateCountertopArgs a)
        {
            if ((a.Points != null && a.Points.Count > 0) == (a.Rectangle != null))
                throw new ToolException("INVALID_ARGUMENTS", "Give points or rectangle (exactly one).");
            if (a.Method.Equals("floor", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(a.FloorType))
                throw new ToolException("INVALID_ARGUMENTS", "method floor needs floorType.");
            if ((a.FrontEdges?.Count ?? 0) > 0 && !(a.ApronHeight > a.Thickness))
                throw new ToolException("INVALID_ARGUMENTS", "frontEdges need apronHeight greater than thickness (it includes the top's thickness).");
            if ((a.BacksplashEdges?.Count ?? 0) > 0 && !(a.BacksplashHeight > 0))
                throw new ToolException("INVALID_ARGUMENTS", "backsplashEdges need backsplashHeight.");
            if (a.Height < a.Thickness)
                throw new ToolException("INVALID_ARGUMENTS", "height (top surface) must be at least the thickness.");
        }

        protected override object Run(ToolContext ctx, CreateCountertopArgs a)
        {
            var doc = ctx.Doc;
            var level = ctx.Services.Lookup.Level(a.Level);
            double top = level.Elevation + ArchGeometry.Ft(a.Height);
            double t = ArchGeometry.Ft(a.Thickness);
            double bottom = top - t;

            var raw = a.Rectangle != null ? ArchGeometry.RectanglePoints(a.Rectangle, bottom) : ArchGeometry.ToXyz(a.Points, bottom);
            var pts = ArchGeometry.CleanPolygon(raw, doc.Application.ShortCurveTolerance);
            // Keep the caller's edge numbering: orient counter-clockwise only for the solid, remembering if we reversed.
            var outer = ArchGeometry.PolygonLoop(doc, pts, "countertop outline");
            bool reversed = ArchGeometry.SignedArea(pts) < 0;
            var loops = new List<CurveLoop> { ArchGeometry.Oriented(outer, true) };
            int ci = 0;
            foreach (var c in a.Cutouts ?? new List<CutoutMm>())
            {
                var rect = new RectangleMm { Origin = new PointMm { X = 0, Y = 0 }, Width = c.Width, Depth = c.Depth, Rotation = c.Rotation };
                var corners = ArchGeometry.RectanglePoints(rect, bottom);
                var centreOffset = new XYZ(ArchGeometry.Ft(c.Center.X), ArchGeometry.Ft(c.Center.Y), 0) - (corners[0] + corners[2]) / 2;
                var moved = corners.Select(p => new XYZ(p.X + centreOffset.X, p.Y + centreOffset.Y, bottom)).ToList();
                if (moved.Any(p => !ArchGeometry.Contains(pts, p)))
                    throw new ToolException("INVALID_GEOMETRY", $"Cut-out {ci} is not fully inside the countertop outline.");
                loops.Add(ArchGeometry.Oriented(ArchGeometry.PolygonLoop(doc, moved, $"cut-out {ci}"), false));
                ci++;
            }

            int edgeCount = pts.Count;
            foreach (var e in (a.FrontEdges ?? new List<int>()).Concat(a.BacksplashEdges ?? new List<int>()))
                if (e < 0 || e >= edgeCount)
                    throw new ToolException("INVALID_ARGUMENTS", $"Edge {e} does not exist; the outline has edges 0 to {edgeCount - 1}.");

            ElementId materialId = ElementId.InvalidElementId;
            Material material = null;
            if (!string.IsNullOrWhiteSpace(a.Material))
            {
                material = ctx.Services.Lookup.Material(a.Material);
                materialId = material.Id;
            }

            var created = new List<object>();
            var ids = new List<long>();
            double areaFt2 = ArchGeometry.RegionArea(loops);
            double volumeFt3 = 0;
            string method = a.Method.ToLowerInvariant();
            if (method == "floor")
            {
                var type = TypeFinder.Resolve<FloorType>(doc, a.FloorType, "floor type", x => !x.IsFoundationSlab);
                var typeThickness = ArchGeometry.DoubleParam(type, BuiltInParameter.FLOOR_ATTR_DEFAULT_THICKNESS_PARAM);
                if (!double.IsNaN(typeThickness) && Math.Abs(typeThickness - t) > ArchGeometry.Ft(0.5))
                    throw new ToolException("TYPE_THICKNESS_MISMATCH",
                        $"Floor type '{LookupService.FullName(type)}' is {ArchGeometry.Fmt(ArchGeometry.Mm(typeThickness))} thick, not {ArchGeometry.Fmt(a.Thickness)}.",
                        "Use a floor type with that thickness, or method directshape.");
                var floor = SurfaceCreation.CreateFloor(doc, loops, type, level, ArchGeometry.Ft(a.Height), false);
                SetMark(floor, a.Mark);
                volumeFt3 += areaFt2 * t;
                created.Add(new { part = "top", kind = "floor", id = floor.Id.GetValue(), floorType = LookupService.FullName(type) });
                ids.Add(floor.Id.GetValue());
            }
            else
            {
                var solid = Extrude(loops, bottom, t, materialId);
                var ds = Shape(doc, new List<GeometryObject> { solid }, a.Mark, "top");
                volumeFt3 += solid.Volume;
                created.Add(new { part = "top", kind = "directshape", id = ds.Id.GetValue() });
                ids.Add(ds.Id.GetValue());
            }

            // Apron (saia) and backsplash (rodabanca) as Generic Model solids along the chosen edges, inside the outline.
            var extras = new List<GeometryObject>();
            foreach (var e in a.FrontEdges ?? new List<int>())
            {
                var strip = EdgeStrip(pts, e, reversed, t);
                double apron = ArchGeometry.Ft(a.ApronHeight.Value);
                var s = Extrude(new List<CurveLoop> { ArchGeometry.PolygonLoop(doc, strip.Select(p => new XYZ(p.X, p.Y, top - apron)).ToList(), $"apron {e}") },
                    top - apron, apron - t, materialId);
                extras.Add(s);
                volumeFt3 += s.Volume;
            }
            foreach (var e in a.BacksplashEdges ?? new List<int>())
            {
                var strip = EdgeStrip(pts, e, reversed, t);
                double h = ArchGeometry.Ft(a.BacksplashHeight.Value);
                var s = Extrude(new List<CurveLoop> { ArchGeometry.PolygonLoop(doc, strip.Select(p => new XYZ(p.X, p.Y, top)).ToList(), $"backsplash {e}") },
                    top, h, materialId);
                extras.Add(s);
                volumeFt3 += s.Volume;
            }
            if (extras.Count > 0)
            {
                var ds = Shape(doc, extras, null, "apron/backsplash");
                created.Add(new { part = "apron/backsplash", kind = "directshape", id = ds.Id.GetValue() });
                ids.Add(ds.Id.GetValue());
            }
            doc.Regenerate();
            if (ids.Any(id => doc.GetElement(id.ToRevitElementId()) == null))
                throw new ToolException("CREATE_FAILED", "Revit removed part of the countertop right after creating it.");

            if (method == "floor" && material != null && extras.Count == 0)
                ctx.Warnings.Add(new ToolWarning("material is not applied to a floor top: the floor type's layers define it."));

            return new
            {
                created,
                ids,
                level = level.Name,
                topElevationMm = a.Height,
                thicknessMm = a.Thickness,
                areaM2 = ArchGeometry.M2(areaFt2),
                volumeM3 = Math.Round(volumeFt3 * 0.028316846592, 4),
                cutouts = ci,
                material = material?.Name,
                edges = Enumerable.Range(0, edgeCount).Select(i => new
                {
                    index = i,
                    from = ArchGeometry.ToWire2(pts[i]),
                    to = ArchGeometry.ToWire2(pts[(i + 1) % edgeCount]),
                    lengthMm = ArchGeometry.Mm(pts[i].DistanceTo(pts[(i + 1) % edgeCount]))
                }).ToList()
            };
        }

        /// <summary>Plan strip of width <paramref name="width"/> along edge i, on the inner side of the outline.</summary>
        private static IList<XYZ> EdgeStrip(IList<XYZ> pts, int i, bool reversed, double width)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            var dir = (b - a).Normalize();
            // Inside is on the left of each edge for a counter-clockwise outline, on the right otherwise.
            var inward = XYZ.BasisZ.CrossProduct(dir);
            if (reversed) inward = inward.Negate();
            return new List<XYZ> { a, b, b + inward * width, a + inward * width };
        }

        private static Solid Extrude(IList<CurveLoop> loops, double z, double height, ElementId materialId)
        {
            var placed = ArchGeometry.AtElevation(loops, z);
            var options = new SolidOptions(materialId, ElementId.InvalidElementId);
            try
            {
                return GeometryCreationUtilities.CreateExtrusionGeometry(placed, XYZ.BasisZ, height, options);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("INVALID_GEOMETRY", "Revit could not build the solid: " + ex.Message);
            }
        }

        private static DirectShape Shape(Document doc, IList<GeometryObject> geometry, string mark, string part)
        {
            var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
            ds.ApplicationId = "RevitMCP";
            ds.ApplicationDataId = "countertop:" + part;
            if (!ds.IsValidShape(geometry))
                throw new ToolException("INVALID_GEOMETRY", $"The {part} solid is not a valid shape for Revit.");
            ds.SetShape(geometry);
            SetMark(ds, mark);
            var comments = ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (comments != null && !comments.IsReadOnly) comments.Set("Tampo - " + part);
            return ds;
        }

        private static void SetMark(Element e, string mark)
        {
            if (string.IsNullOrWhiteSpace(mark)) return;
            var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (p == null || p.IsReadOnly) throw new ToolException("PARAMETER_READ_ONLY", "Mark cannot be set on the countertop.");
            p.Set(mark);
            if (p.AsString() != mark) throw new ToolException("PARAMETER_NOT_APPLIED", $"Revit kept Mark '{p.AsString()}'.");
        }
    }
}
