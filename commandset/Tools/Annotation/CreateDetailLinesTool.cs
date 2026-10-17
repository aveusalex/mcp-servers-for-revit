using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Annotation.Support;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Annotation
{
    public sealed class DetailShape
    {
        [JsonProperty("type", Required = Required.Always)]
        [EnumValues("line", "polyline", "rectangle", "circle", "arc")]
        [Description("line (points[0]→points[1]), polyline (points, optionally closed), rectangle (points[0] and points[1] are opposite corners), circle (center, radius) or arc (center, radius, startAngle, endAngle, counter-clockwise).")]
        public string Type { get; set; }

        [JsonProperty("points")]
        [MaxLength(500)]
        [Description("Points in the view plane, mm (plans: model X/Y; sections/elevations: x along the view, y = elevation; sheets: paper mm).")]
        public List<ViewPointMm> Points { get; set; }

        [JsonProperty("closed")]
        [DefaultValue(false)]
        [Description("polyline: also join the last point to the first.")]
        public bool Closed { get; set; }

        [JsonProperty("center")]
        [Description("circle/arc centre in the view plane, mm.")]
        public ViewPointMm Center { get; set; }

        [JsonProperty("radius")]
        [Unit("mm")]
        [Range(0.1, 1000000)]
        [Description("circle/arc radius, mm (model mm; paper mm on sheets).")]
        public double? Radius { get; set; }

        [JsonProperty("startAngle")]
        [Unit("deg")]
        [Description("arc start angle, degrees from the view's right direction, counter-clockwise.")]
        public double? StartAngle { get; set; }

        [JsonProperty("endAngle")]
        [Unit("deg")]
        [Description("arc end angle, degrees (greater than startAngle, at most startAngle + 360).")]
        public double? EndAngle { get; set; }

        [JsonProperty("lineStyle")]
        [Description("Line style for this shape (overrides the call's lineStyle).")]
        public string LineStyle { get; set; }
    }

    public sealed class CreateDetailLinesArgs
    {
        [JsonProperty("view")]
        [Description("View name or id (plan, section, elevation, detail, drafting, legend or sheet). Default: the active view.")]
        public string View { get; set; }

        [JsonProperty("lineStyle")]
        [Description("Line style (Lines subcategory) by name or id, e.g. \"<Thin Lines>\" / \"Linhas finas\" (brackets optional). Default: Revit's current line style.")]
        public string LineStyle { get; set; }

        [JsonProperty("shapes", Required = Required.Always)]
        [MinLength(1)]
        [MaxLength(500)]
        [Description("Shapes to draw, up to 500.")]
        public List<DetailShape> Shapes { get; set; }

        [JsonProperty("continueOnError")]
        [DefaultValue(false)]
        [Description("false (default): any invalid shape rolls back everything. true: invalid shapes are listed in failed[].")]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>ANO-05: detail lines and simple shapes in a view's plane.</summary>
    [McpTool("create_detail_lines", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "annotation",
        Description = "Draw detail lines in a 2D view (plan, section, elevation, detail, drafting, legend or sheet): " +
                      "lines, polylines (open or closed), rectangles, circles and arcs, with coordinates in the view " +
                      "plane in mm and an optional line style (by name, pt-BR or English). Not in 3D views. Returns the " +
                      "created ids per shape. Undo: Ctrl+Z or delete the ids.")]
    public sealed class CreateDetailLinesTool : McpTool<CreateDetailLinesArgs>
    {
        protected override void Validate(CreateDetailLinesArgs a)
        {
            foreach (var s in a.Shapes)
            {
                if (s == null) throw new ToolException("INVALID_ARGUMENTS", "shapes must not contain null.");
                s.Type = s.Type.Trim().ToLowerInvariant();
                int n = s.Points?.Count ?? 0;
                switch (s.Type)
                {
                    case "line":
                    case "rectangle":
                        if (n != 2) throw new ToolException("INVALID_ARGUMENTS", $"A {s.Type} needs exactly 2 points.");
                        break;
                    case "polyline":
                        if (n < 2) throw new ToolException("INVALID_ARGUMENTS", "A polyline needs at least 2 points.");
                        break;
                    case "circle":
                    case "arc":
                        if (s.Center == null || s.Radius == null) throw new ToolException("INVALID_ARGUMENTS", $"A {s.Type} needs center and radius.");
                        if (s.Type == "arc" && (s.StartAngle == null || s.EndAngle == null || s.EndAngle <= s.StartAngle || s.EndAngle - s.StartAngle > 360))
                            throw new ToolException("INVALID_ARGUMENTS", "An arc needs startAngle < endAngle ≤ startAngle + 360.");
                        break;
                }
            }
        }

        protected override object Run(ToolContext ctx, CreateDetailLinesArgs a)
        {
            var doc = ctx.Doc;
            var view = DraftingSupport.DraftableView(ctx, a.View, "detail lines");
            GraphicsStyle defaultStyle = string.IsNullOrWhiteSpace(a.LineStyle) ? null : DraftingSupport.LineStyle(ctx, a.LineStyle);
            var styleCache = new Dictionary<string, GraphicsStyle>(StringComparer.OrdinalIgnoreCase);
            var all = new List<long>();

            var r = ItemLoop.Run(ctx, a.Shapes, a.ContinueOnError, (s, index) =>
            {
                GraphicsStyle style = defaultStyle;
                if (!string.IsNullOrWhiteSpace(s.LineStyle))
                {
                    if (!styleCache.TryGetValue(s.LineStyle, out style))
                        styleCache[s.LineStyle] = style = DraftingSupport.LineStyle(ctx, s.LineStyle);
                }
                var curves = Curves(doc, view, s);
                var ids = new List<long>();
                foreach (var c in curves)
                {
                    var dc = doc.Create.NewDetailCurve(view, c);
                    if (style != null) dc.LineStyle = style;
                    ids.Add(dc.Id.GetValue());
                }
                all.AddRange(ids);
                return new { index, type = s.Type, ids, lineStyle = style?.Name };
            }, s => s.Type, "Shape");

            return new
            {
                view = ViewPlane.DescribeView(view),
                created = r.Done,
                failed = r.Failed,
                ids = all,
                createdCount = all.Count,
                failedCount = r.Failed.Count,
                dryRun = ctx.DryRun
            };
        }

        private static List<Curve> Curves(Document doc, View view, DetailShape s)
        {
            var list = new List<Curve>();
            switch (s.Type)
            {
                case "line":
                case "polyline":
                {
                    var pts = s.Points.Select(p => DraftingSupport.Point(view, p)).ToList();
                    if (s.Type == "polyline" && s.Closed && pts.Count > 2 && !pts[0].IsAlmostEqualTo(pts[pts.Count - 1])) pts.Add(pts[0]);
                    for (int i = 0; i + 1 < pts.Count; i++)
                    {
                        DraftingSupport.CheckLength(doc, pts[i], pts[i + 1], $"Segment {i}");
                        list.Add(Line.CreateBound(pts[i], pts[i + 1]));
                    }
                    break;
                }
                case "rectangle":
                {
                    var p0 = s.Points[0]; var p1 = s.Points[1];
                    var corners = new[]
                    {
                        new ViewPointMm { X = p0.X, Y = p0.Y }, new ViewPointMm { X = p1.X, Y = p0.Y },
                        new ViewPointMm { X = p1.X, Y = p1.Y }, new ViewPointMm { X = p0.X, Y = p1.Y }
                    }.Select(p => DraftingSupport.Point(view, p)).ToList();
                    for (int i = 0; i < 4; i++)
                    {
                        DraftingSupport.CheckLength(doc, corners[i], corners[(i + 1) % 4], "A rectangle side");
                        list.Add(Line.CreateBound(corners[i], corners[(i + 1) % 4]));
                    }
                    break;
                }
                case "circle":
                case "arc":
                {
                    var c = DraftingSupport.Point(view, s.Center);
                    double rad = DraftingSupport.Ft(s.Radius.Value);
                    if (rad < doc.Application.ShortCurveTolerance)
                        throw new ToolException("INVALID_GEOMETRY", "The radius is below Revit's minimum curve length.");
                    if (s.Type == "circle")
                    {
                        // Two half circles: a closed arc is not always accepted as a detail curve.
                        list.Add(Arc.Create(c, rad, 0, Math.PI, view.RightDirection, view.UpDirection));
                        list.Add(Arc.Create(c, rad, Math.PI, 2 * Math.PI, view.RightDirection, view.UpDirection));
                    }
                    else
                    {
                        double a0 = DraftingSupport.Rad(s.StartAngle.Value), a1 = DraftingSupport.Rad(s.EndAngle.Value);
                        if (a1 - a0 >= 2 * Math.PI - 1e-9)
                        {
                            list.Add(Arc.Create(c, rad, a0, a0 + Math.PI, view.RightDirection, view.UpDirection));
                            list.Add(Arc.Create(c, rad, a0 + Math.PI, a0 + 2 * Math.PI, view.RightDirection, view.UpDirection));
                        }
                        else list.Add(Arc.Create(c, rad, a0, a1, view.RightDirection, view.UpDirection));
                    }
                    break;
                }
            }
            return list;
        }
    }
}
