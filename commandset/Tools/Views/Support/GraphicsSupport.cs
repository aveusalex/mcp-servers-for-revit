using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;

namespace RevitMCPCommandSet.Tools.Views.Support
{
    /// <summary>Graphic overrides for a category or a filter in a view (only the given fields change).</summary>
    public sealed class GraphicOverrides
    {
        [JsonProperty("projectionLineColor")]
        [Description("Projection line color as \"#RRGGBB\".")]
        public string ProjectionLineColor { get; set; }

        [JsonProperty("projectionLineWeight")]
        [Description("Projection line weight (pen number 1-16).")]
        [Range(1, 16)]
        public int? ProjectionLineWeight { get; set; }

        [JsonProperty("cutLineColor")]
        [Description("Cut line color as \"#RRGGBB\".")]
        public string CutLineColor { get; set; }

        [JsonProperty("surfaceColor")]
        [Description("Surface foreground pattern color as \"#RRGGBB\" (needs a surfacePattern, e.g. \"solid\").")]
        public string SurfaceColor { get; set; }

        [JsonProperty("surfacePattern")]
        [Description("Surface foreground fill pattern name or id; \"solid\" for the solid fill; \"none\" to clear.")]
        public string SurfacePattern { get; set; }

        [JsonProperty("cutColor")]
        [Description("Cut foreground pattern color as \"#RRGGBB\".")]
        public string CutColor { get; set; }

        [JsonProperty("cutPattern")]
        [Description("Cut foreground fill pattern name or id; \"solid\" for the solid fill; \"none\" to clear.")]
        public string CutPattern { get; set; }

        [JsonProperty("transparency")]
        [Description("Surface transparency in percent, 0-100.")]
        [Range(0, 100)]
        public int? Transparency { get; set; }

        [JsonProperty("halftone")]
        [Description("Draw in halftone.")]
        public bool? Halftone { get; set; }
    }

    internal static class GraphicsSupport
    {
        /// <summary>Starts from <paramref name="current"/> and changes only the fields given.</summary>
        public static OverrideGraphicSettings Build(ToolContext ctx, GraphicOverrides o, OverrideGraphicSettings current)
        {
            var s = current != null ? new OverrideGraphicSettings(current) : new OverrideGraphicSettings();
            if (o == null) return s;
            if (o.ProjectionLineColor != null) s.SetProjectionLineColor(Color(o.ProjectionLineColor));
            if (o.ProjectionLineWeight.HasValue) s.SetProjectionLineWeight(o.ProjectionLineWeight.Value);
            if (o.CutLineColor != null) s.SetCutLineColor(Color(o.CutLineColor));
            if (o.SurfacePattern != null) s.SetSurfaceForegroundPatternId(Pattern(ctx, o.SurfacePattern));
            if (o.SurfaceColor != null) s.SetSurfaceForegroundPatternColor(Color(o.SurfaceColor));
            if (o.CutPattern != null) s.SetCutForegroundPatternId(Pattern(ctx, o.CutPattern));
            if (o.CutColor != null) s.SetCutForegroundPatternColor(Color(o.CutColor));
            if (o.Transparency.HasValue) s.SetSurfaceTransparency(o.Transparency.Value);
            if (o.Halftone.HasValue) s.SetHalftone(o.Halftone.Value);
            return s;
        }

        public static Color Color(string hex)
        {
            var h = (hex ?? string.Empty).Trim().TrimStart('#');
            if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                throw new ToolException("INVALID_ARGUMENTS", $"'{hex}' is not a color; use \"#RRGGBB\".");
            return new Color((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        }

        public static string Hex(Color c) => c != null && c.IsValid ? $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}" : null;

        public static ElementId Pattern(ToolContext ctx, string text)
        {
            if (ViewSupport.IsNone(text)) return ElementId.InvalidElementId;
            var patterns = new FilteredElementCollector(ctx.Doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().ToList();
            var trimmed = text.Trim().ToLowerInvariant();
            if (trimmed == "solid" || trimmed == "solido" || trimmed == "sólido")
            {
                var solid = patterns.FirstOrDefault(p => p.GetFillPattern().IsSolidFill && p.GetFillPattern().Target == FillPatternTarget.Drafting)
                            ?? patterns.FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
                return solid?.Id ?? throw new ToolException("NOT_FOUND", "The document has no solid fill pattern.");
            }
            var drafting = patterns.Where(p => p.GetFillPattern().Target == FillPatternTarget.Drafting).ToList();
            try
            {
                return ctx.Services.Lookup.ByNameOrId(text, drafting, "fill pattern").Id;
            }
            catch (ToolException ex) when (ex.Code == "NOT_FOUND")
            {
                return ctx.Services.Lookup.ByNameOrId(text, patterns, "fill pattern").Id;
            }
        }

        public static bool IsOverridden(OverrideGraphicSettings s) =>
            s.ProjectionLineColor.IsValid || s.CutLineColor.IsValid || s.SurfaceForegroundPatternColor.IsValid ||
            s.CutForegroundPatternColor.IsValid || s.SurfaceForegroundPatternId != ElementId.InvalidElementId ||
            s.CutForegroundPatternId != ElementId.InvalidElementId || s.Halftone || s.Transparency > 0 ||
            s.ProjectionLineWeight != OverrideGraphicSettings.InvalidPenNumber ||
            s.CutLineWeight != OverrideGraphicSettings.InvalidPenNumber;

        public static object Describe(Document doc, OverrideGraphicSettings s)
        {
            if (s == null) return null;
            string Name(ElementId id) => id == ElementId.InvalidElementId ? null : doc.GetElement(id)?.Name;
            return new
            {
                projectionLineColor = Hex(s.ProjectionLineColor),
                projectionLineWeight = s.ProjectionLineWeight == OverrideGraphicSettings.InvalidPenNumber ? (int?)null : s.ProjectionLineWeight,
                cutLineColor = Hex(s.CutLineColor),
                surfaceColor = Hex(s.SurfaceForegroundPatternColor),
                surfacePattern = Name(s.SurfaceForegroundPatternId),
                cutColor = Hex(s.CutForegroundPatternColor),
                cutPattern = Name(s.CutForegroundPatternId),
                transparency = s.Transparency,
                halftone = s.Halftone
            };
        }

        /// <summary>Name of the template that controls <paramref name="bip"/> on this view, or null.</summary>
        public static string ControllingTemplate(View view, BuiltInParameter bip)
        {
            if (view.IsTemplate || view.ViewTemplateId == ElementId.InvalidElementId) return null;
            var template = view.Document.GetElement(view.ViewTemplateId) as View;
            if (template == null) return null;
            var id = new ElementId(bip);
            var controlled = template.GetTemplateParameterIds().Contains(id) &&
                             !template.GetNonControlledTemplateParameterIds().Contains(id);
            return controlled ? template.Name : null;
        }
    }
}
