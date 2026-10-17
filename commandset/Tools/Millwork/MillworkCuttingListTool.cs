using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Millwork.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Millwork
{
    public sealed class MillworkCuttingListArgs
    {
        [JsonProperty("modules")]
        [MaxLength(500)]
        [Description("Modules to include (group id, part id or mark). Default: every module created by create_millwork_module.")]
        public List<string> Modules { get; set; }

        [JsonProperty("groupBy")]
        [EnumValues("module", "material")]
        [DefaultValue("module")]
        [Description("module (default): rows per module. material: identical pieces of all modules merged (for ordering/cutting).")]
        public string GroupBy { get; set; } = "module";

        [JsonProperty("sheetLength")]
        [Unit("mm")]
        [Range(500, 4000)]
        [DefaultValue(2750)]
        [Description("Raw sheet length for the sheet estimate, mm. Default 2750 (common MDF sheet in Brazil).")]
        public double SheetLength { get; set; } = 2750;

        [JsonProperty("sheetWidth")]
        [Unit("mm")]
        [Range(300, 2500)]
        [DefaultValue(1850)]
        [Description("Raw sheet width for the sheet estimate, mm. Default 1850.")]
        public double SheetWidth { get; set; } = 1850;

        [JsonProperty("wastePercent")]
        [Range(0, 60)]
        [DefaultValue(15)]
        [Description("Cutting waste allowance in percent for the sheet estimate. Default 15.")]
        public double WastePercent { get; set; } = 15;
    }

    /// <summary>ARQ-24/25 companion: cutting list (plano de corte simplificado) of the millwork modules.</summary>
    [McpTool("millwork_cutting_list", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "architecture",
        Description = "Cutting list of millwork modules created by create_millwork_module: piece, quantity, length x " +
                      "width x thickness (mm), material and area (m²), per module or merged by material, plus totals per " +
                      "material and thickness and an estimate of raw sheets (area-based with a waste allowance; not a " +
                      "nesting optimisation). Reads the parameters stored on the parts; does not change the model.")]
    public sealed class MillworkCuttingListTool : McpTool<MillworkCuttingListArgs>
    {
        protected override object Run(ToolContext ctx, MillworkCuttingListArgs a)
        {
            var doc = ctx.Doc;
            var all = MillworkRevit.AllModules(doc);
            List<ModuleInstance> modules;
            if (a.Modules != null && a.Modules.Count > 0)
                modules = a.Modules.Select(m => MillworkRevit.Find(ctx, m, all)).GroupBy(m => m.Key).Select(g => g.First()).ToList();
            else
                modules = all;
            if (modules.Count == 0)
                return new { modules = new object[0], rows = new object[0], totals = new object[0], message = "No millwork modules created by create_millwork_module in this document." };

            bool byMaterial = string.Equals(a.GroupBy, "material", StringComparison.OrdinalIgnoreCase);
            var flat = new List<(MillworkPart part, string material, string module)>();
            var described = new List<object>();
            foreach (var m in modules.OrderBy(m => m.Design.Mark ?? m.Group?.GroupType?.Name ?? m.ModuleId))
            {
                var label = m.Design.Mark ?? m.Group?.GroupType?.Name ?? m.ModuleId;
                foreach (var (shape, record) in m.Parts)
                    flat.Add((record.Part, MillworkRevit.MaterialName(doc, record.MaterialId), byMaterial ? null : label));
                var d = m.Design;
                described.Add(new
                {
                    module = label,
                    moduleId = m.ModuleId,
                    groupId = m.Group?.Id.GetValue(),
                    name = d.Name,
                    kind = d.Kind,
                    widthMm = d.Width,
                    heightMm = d.Height,
                    depthMm = d.Depth,
                    parts = m.Parts.Count
                });
            }

            var rows = MillworkRevit.CuttingList(flat);
            double sheetM2 = a.SheetLength * a.SheetWidth / 1e6;
            var totals = flat.GroupBy(p => (p.material ?? "(sem material)", p.part.PanelDims()[2]))
                .Select(g =>
                {
                    double area = g.Sum(p => { var d = p.part.PanelDims(); return d[0] * d[1] / 1e6; });
                    bool oversize = g.Any(p => { var d = p.part.PanelDims(); return d[0] > Math.Max(a.SheetLength, a.SheetWidth) || d[1] > Math.Min(a.SheetLength, a.SheetWidth); });
                    return new
                    {
                        material = g.Key.Item1,
                        thicknessMm = g.Key.Item2,
                        pieces = g.Count(),
                        areaM2 = Math.Round(area, 3),
                        sheetsEstimate = (int)Math.Ceiling(area * (1 + a.WastePercent / 100.0) / sheetM2),
                        piecesLargerThanSheet = oversize
                    };
                })
                .OrderBy(t => t.material).ThenByDescending(t => t.thicknessMm).ToList();
            if (totals.Any(t => t.piecesLargerThanSheet))
                ctx.Warnings.Add(new ToolWarning("Some pieces are larger than the raw sheet: they need joints or a larger sheet."));

            return new
            {
                modules = described,
                groupBy = byMaterial ? "material" : "module",
                rows,
                totals,
                sheet = new { lengthMm = a.SheetLength, widthMm = a.SheetWidth, wastePercent = a.WastePercent },
                totalPieces = flat.Count,
                totalAreaM2 = Math.Round(rows.Sum(r => r.AreaM2), 3)
            };
        }
    }
}
