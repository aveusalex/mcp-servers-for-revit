using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Annotation.Support;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Annotation
{
    public sealed class RegionLoop
    {
        [JsonProperty("points", Required = Required.Always)]
        [MinLength(3)]
        [MaxLength(1000)]
        [Description("Closed outline in the view plane, mm (the last point joins the first).")]
        public List<ViewPointMm> Points { get; set; }
    }

    public sealed class FilledRegionItem
    {
        [JsonProperty("loops")]
        [MaxLength(50)]
        [Description("Outlines: the first is the boundary, the next ones are holes inside it. Give loops or rectangle.")]
        public List<RegionLoop> Loops { get; set; }

        [JsonProperty("rectangle")]
        [MaxLength(2)]
        [MinLength(2)]
        [Description("Two opposite corners of a rectangular region in the view plane, mm (instead of loops).")]
        public List<ViewPointMm> Rectangle { get; set; }

        [JsonProperty("regionType")]
        [Description("Filled region type for this region (overrides the call's regionType).")]
        public string RegionType { get; set; }
    }

    public sealed class CreateFilledRegionArgs
    {
        [JsonProperty("view")]
        [Description("View name or id (plan, section, elevation, detail, drafting, legend or sheet). Default: the active view.")]
        public string View { get; set; }

        [JsonProperty("regionType")]
        [Description("Filled region type by name or id (e.g. \"Sólido preto\"). Default: the document's default filled region type.")]
        public string RegionType { get; set; }

        [JsonProperty("lineStyle")]
        [Description("Line style of the boundary by name or id (e.g. \"<Invisible lines>\" / \"<Linhas invisíveis>\"). Default: the type's.")]
        public string LineStyle { get; set; }

        [JsonProperty("regions", Required = Required.Always)]
        [MinLength(1)]
        [MaxLength(100)]
        [Description("Regions to create, up to 100.")]
        public List<FilledRegionItem> Regions { get; set; }

        [JsonProperty("continueOnError")]
        [DefaultValue(false)]
        [Description("false (default): any invalid region rolls back everything. true: invalid ones are listed in failed[].")]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>ANO-06: filled regions from closed outlines (with holes) in a view's plane.</summary>
    [McpTool("create_filled_region", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "annotation",
        Description = "Create filled regions (região preenchida) in a 2D view from closed outlines in the view plane " +
                      "(mm): first loop = boundary, further loops = holes; or a rectangle. Filled region type and " +
                      "boundary line style by name (pt-BR or English); unknown names fail instead of falling back. " +
                      "Self-intersecting or open outlines are refused. Returns ids and areas (m², model scale). Undo: " +
                      "Ctrl+Z or delete the ids.")]
    public sealed class CreateFilledRegionTool : McpTool<CreateFilledRegionArgs>
    {
        protected override void Validate(CreateFilledRegionArgs a)
        {
            foreach (var r in a.Regions)
            {
                if (r == null) throw new ToolException("INVALID_ARGUMENTS", "regions must not contain null.");
                bool hasLoops = r.Loops != null && r.Loops.Count > 0, hasRect = r.Rectangle != null && r.Rectangle.Count > 0;
                if (hasLoops == hasRect) throw new ToolException("INVALID_ARGUMENTS", "Each region needs loops or rectangle (exactly one).");
            }
        }

        protected override object Run(ToolContext ctx, CreateFilledRegionArgs a)
        {
            var doc = ctx.Doc;
            var view = DraftingSupport.DraftableView(ctx, a.View, "filled regions");
            var types = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
            if (types.Count == 0) throw new ToolException("NOT_FOUND", "The document has no filled region types.");
            FilledRegionType TypeOf(string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    var def = doc.GetDefaultElementTypeId(ElementTypeGroup.FilledRegionType);
                    return doc.GetElement(def) as FilledRegionType ?? types.OrderBy(t => t.Name).First();
                }
                return ctx.Services.Lookup.ByNameOrId(text, types, "filled region type");
            }
            var defaultType = TypeOf(a.RegionType);
            ElementId lineStyleId = null;
            if (!string.IsNullOrWhiteSpace(a.LineStyle))
            {
                lineStyleId = DraftingSupport.LineStyle(ctx, a.LineStyle).Id;
                if (!FilledRegion.IsValidLineStyleIdForFilledRegion(doc, lineStyleId))
                    throw new ToolException("INVALID_ARGUMENTS", $"Line style '{a.LineStyle}' can't be used for a filled region boundary.");
            }

            var ids = new List<long>();
            var r = ItemLoop.Run(ctx, a.Regions, a.ContinueOnError, (item, index) =>
            {
                var type = string.IsNullOrWhiteSpace(item.RegionType) ? defaultType : TypeOf(item.RegionType);
                var raw = item.Rectangle != null && item.Rectangle.Count > 0
                    ? new List<List<ViewPointMm>> { Rect(item.Rectangle[0], item.Rectangle[1]) }
                    : item.Loops.Select(l => l.Points).ToList();
                var loops = new List<CurveLoop>();
                for (int i = 0; i < raw.Count; i++)
                    loops.Add(Loop(doc, view, raw[i], i == 0, i == 0 ? "boundary" : $"hole {i}"));
                FilledRegion region;
                try
                {
                    region = FilledRegion.Create(doc, type.Id, view.Id, loops);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    throw new ToolException("INVALID_GEOMETRY", "Revit refused the outlines (holes must be inside the boundary and must not touch): " + ex.Message);
                }
                if (lineStyleId != null) region.SetLineStyleId(lineStyleId);
                doc.Regenerate();
                var area = region.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                ids.Add(region.Id.GetValue());
                return new
                {
                    index,
                    id = region.Id.GetValue(),
                    regionType = type.Name,
                    loops = loops.Count,
                    areaM2 = area != null && area.HasValue ? Math.Round(area.AsDouble() * 0.09290304, 4) : (double?)null
                };
            }, i => i.Rectangle != null ? "rectangle" : $"{i.Loops?.Count ?? 0} loop(s)", "Region");

            return new
            {
                view = ViewPlane.DescribeView(view),
                created = r.Done,
                failed = r.Failed,
                ids,
                createdCount = r.Done.Count,
                failedCount = r.Failed.Count,
                dryRun = ctx.DryRun
            };
        }

        private static List<ViewPointMm> Rect(ViewPointMm a, ViewPointMm b) => new List<ViewPointMm>
        {
            new ViewPointMm { X = a.X, Y = a.Y }, new ViewPointMm { X = b.X, Y = a.Y },
            new ViewPointMm { X = b.X, Y = b.Y }, new ViewPointMm { X = a.X, Y = b.Y }
        };

        private static CurveLoop Loop(Document doc, View view, List<ViewPointMm> raw, bool outer, string what)
        {
            var pts2 = raw.Select(p => new XYZ(p.X, p.Y, 0)).ToList();
            if (pts2.Count > 3 && pts2[0].IsAlmostEqualTo(pts2[pts2.Count - 1])) pts2.RemoveAt(pts2.Count - 1);
            if (pts2.Count < 3) throw new ToolException("INVALID_GEOMETRY", $"The {what} needs at least 3 distinct points.");
            if (ArchGeometry.SelfIntersects(pts2)) throw new ToolException("INVALID_GEOMETRY", $"The {what} crosses itself.");
            // Boundary counter-clockwise, holes clockwise (in view-plane coordinates).
            if ((ArchGeometry.SignedArea(pts2) > 0) != outer) pts2.Reverse();
            var model = pts2.Select(p => ViewPlane.ToModel(view, p.X, p.Y)).ToList();
            var loop = new CurveLoop();
            for (int i = 0; i < model.Count; i++)
            {
                var a = model[i];
                var b = model[(i + 1) % model.Count];
                DraftingSupport.CheckLength(doc, a, b, $"Edge {i} of the {what}");
                loop.Append(Line.CreateBound(a, b));
            }
            return loop;
        }
    }
}
