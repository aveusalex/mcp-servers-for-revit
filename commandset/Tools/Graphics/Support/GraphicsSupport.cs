using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Graphics.Support
{
    public sealed class LineOverride
    {
        [JsonProperty("color")]
        [Description("Line colour: \"#RRGGBB\" or \"r,g,b\".")]
        public string Color { get; set; }

        [JsonProperty("weight")]
        [Description("Line weight (pen number) 1-16.")]
        [Range(1, 16)]
        public int? Weight { get; set; }

        [JsonProperty("pattern")]
        [Description("Line pattern name or id (\"Solid\" for continuous).")]
        public string Pattern { get; set; }
    }

    public sealed class FillOverride
    {
        [JsonProperty("color")]
        [Description("Pattern colour: \"#RRGGBB\" or \"r,g,b\".")]
        public string Color { get; set; }

        [JsonProperty("pattern")]
        [Description("Fill pattern name or id (\"solid\" for a solid fill, in any language).")]
        public string Pattern { get; set; }

        [JsonProperty("visible")]
        [Description("Show or hide this pattern.")]
        public bool? Visible { get; set; }
    }

    /// <summary>The graphic overrides schema shared by set_graphic_overrides, set_view_filter and create_view_filter.</summary>
    public sealed class OverrideSpec
    {
        [JsonProperty("projectionLine")]
        [Description("Projection (surface) lines.")]
        public LineOverride ProjectionLine { get; set; }

        [JsonProperty("cutLine")]
        [Description("Cut lines.")]
        public LineOverride CutLine { get; set; }

        [JsonProperty("surfaceForeground")]
        [Description("Surface foreground pattern.")]
        public FillOverride SurfaceForeground { get; set; }

        [JsonProperty("surfaceBackground")]
        [Description("Surface background pattern.")]
        public FillOverride SurfaceBackground { get; set; }

        [JsonProperty("cutForeground")]
        [Description("Cut foreground pattern.")]
        public FillOverride CutForeground { get; set; }

        [JsonProperty("cutBackground")]
        [Description("Cut background pattern.")]
        public FillOverride CutBackground { get; set; }

        [JsonProperty("transparency")]
        [Description("Surface transparency, 0 (opaque) to 100.")]
        [Range(0, 100)]
        public int? Transparency { get; set; }

        [JsonProperty("halftone")]
        [Description("Draw in halftone.")]
        public bool? Halftone { get; set; }

        [JsonIgnore]
        public bool IsEmpty => ProjectionLine == null && CutLine == null && SurfaceForeground == null && SurfaceBackground == null &&
                               CutForeground == null && CutBackground == null && !Transparency.HasValue && !Halftone.HasValue;
    }

    public static class GraphicsSupport
    {
        public static Color ParseColor(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ToolException("INVALID_ARGUMENTS", "A colour is empty.");
            var t = text.Trim();
            try
            {
                if (t.StartsWith("#", StringComparison.Ordinal) || (t.Length == 6 && t.All(Uri.IsHexDigit)))
                {
                    var hex = t.TrimStart('#');
                    if (hex.Length == 6)
                        return new Color(byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber),
                            byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber),
                            byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber));
                }
                var parts = t.Trim('(', ')').Split(',');
                if (parts.Length == 3)
                    return new Color(byte.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                        byte.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                        byte.Parse(parts[2].Trim(), CultureInfo.InvariantCulture));
            }
            catch (FormatException) { }
            catch (OverflowException) { }
            throw new ToolException("INVALID_ARGUMENTS", $"'{text}' is not a colour.", "Use \"#RRGGBB\" or \"r,g,b\" (0-255).");
        }

        public static string Hex(Color c) => c == null || !c.IsValid ? null : $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

        public static ElementId SolidFillId(Document doc)
        {
            var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .Where(f => f.GetFillPattern().IsSolidFill)
                .OrderBy(f => f.GetFillPattern().Target == FillPatternTarget.Drafting ? 0 : 1)
                .FirstOrDefault();
            if (solid == null) throw new ToolException("NOT_FOUND", "The document has no solid fill pattern.");
            return solid.Id;
        }

        public static ElementId FillPatternId(ToolContext ctx, string text)
        {
            var key = TextMatch.Key(text);
            if (key == "solid" || key == "solid fill" || key == "solido" || key == "preenchimento solido" || key == "<solid fill>")
                return SolidFillId(ctx.Doc);
            var all = new FilteredElementCollector(ctx.Doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().ToList();
            var drafting = all.Where(f => f.GetFillPattern().Target == FillPatternTarget.Drafting).ToList();
            try
            {
                return ctx.Services.Lookup.ByNameOrId(text, drafting, "fill pattern").Id;
            }
            catch (ToolException ex) when (ex.Code == "NOT_FOUND")
            {
                return ctx.Services.Lookup.ByNameOrId(text, all, "fill pattern", f => f.GetFillPattern().Target.ToString()).Id;
            }
        }

        public static ElementId LinePatternId(ToolContext ctx, string text)
        {
            var key = TextMatch.Key(text);
            if (key == "solid" || key == "continuous" || key == "solido" || key == "continua" || key == "<solid>")
                return LinePatternElement.GetSolidPatternId();
            var all = new FilteredElementCollector(ctx.Doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>().ToList();
            return ctx.Services.Lookup.ByNameOrId(text, all, "line pattern").Id;
        }

        /// <summary>Applies only what the spec sets onto <paramref name="ogs"/> (merge) and returns it.</summary>
        public static OverrideGraphicSettings Apply(ToolContext ctx, OverrideGraphicSettings ogs, OverrideSpec spec)
        {
            if (spec == null) return ogs;
            if (spec.ProjectionLine != null)
            {
                var l = spec.ProjectionLine;
                if (l.Color != null) ogs.SetProjectionLineColor(ParseColor(l.Color));
                if (l.Weight.HasValue) ogs.SetProjectionLineWeight(Weight(l.Weight.Value));
                if (l.Pattern != null) ogs.SetProjectionLinePatternId(LinePatternId(ctx, l.Pattern));
            }
            if (spec.CutLine != null)
            {
                var l = spec.CutLine;
                if (l.Color != null) ogs.SetCutLineColor(ParseColor(l.Color));
                if (l.Weight.HasValue) ogs.SetCutLineWeight(Weight(l.Weight.Value));
                if (l.Pattern != null) ogs.SetCutLinePatternId(LinePatternId(ctx, l.Pattern));
            }
            if (spec.SurfaceForeground != null)
            {
                var f = spec.SurfaceForeground;
                if (f.Pattern != null) ogs.SetSurfaceForegroundPatternId(FillPatternId(ctx, f.Pattern));
                else if (f.Color != null && ogs.SurfaceForegroundPatternId == ElementId.InvalidElementId)
                    ogs.SetSurfaceForegroundPatternId(SolidFillId(ctx.Doc));
                if (f.Color != null) ogs.SetSurfaceForegroundPatternColor(ParseColor(f.Color));
                if (f.Visible.HasValue) ogs.SetSurfaceForegroundPatternVisible(f.Visible.Value);
            }
            if (spec.SurfaceBackground != null)
            {
                var f = spec.SurfaceBackground;
                if (f.Pattern != null) ogs.SetSurfaceBackgroundPatternId(FillPatternId(ctx, f.Pattern));
                if (f.Color != null) ogs.SetSurfaceBackgroundPatternColor(ParseColor(f.Color));
                if (f.Visible.HasValue) ogs.SetSurfaceBackgroundPatternVisible(f.Visible.Value);
            }
            if (spec.CutForeground != null)
            {
                var f = spec.CutForeground;
                if (f.Pattern != null) ogs.SetCutForegroundPatternId(FillPatternId(ctx, f.Pattern));
                else if (f.Color != null && ogs.CutForegroundPatternId == ElementId.InvalidElementId)
                    ogs.SetCutForegroundPatternId(SolidFillId(ctx.Doc));
                if (f.Color != null) ogs.SetCutForegroundPatternColor(ParseColor(f.Color));
                if (f.Visible.HasValue) ogs.SetCutForegroundPatternVisible(f.Visible.Value);
            }
            if (spec.CutBackground != null)
            {
                var f = spec.CutBackground;
                if (f.Pattern != null) ogs.SetCutBackgroundPatternId(FillPatternId(ctx, f.Pattern));
                if (f.Color != null) ogs.SetCutBackgroundPatternColor(ParseColor(f.Color));
                if (f.Visible.HasValue) ogs.SetCutBackgroundPatternVisible(f.Visible.Value);
            }
            if (spec.Transparency.HasValue)
            {
                if (spec.Transparency < 0 || spec.Transparency > 100)
                    throw new ToolException("INVALID_ARGUMENTS", "transparency must be 0-100.");
                ogs.SetSurfaceTransparency(spec.Transparency.Value);
            }
            if (spec.Halftone.HasValue) ogs.SetHalftone(spec.Halftone.Value);
            return ogs;
        }

        private static int Weight(int w)
        {
            if (w < 1 || w > 16) throw new ToolException("INVALID_ARGUMENTS", $"Line weight must be 1-16 (got {w}).");
            return w;
        }

        /// <summary>The overrides that are set (empty object = no override).</summary>
        public static Dictionary<string, object> Describe(Document doc, OverrideGraphicSettings o)
        {
            var d = new Dictionary<string, object>();
            object Line(Color c, int w, ElementId p)
            {
                var x = new Dictionary<string, object>();
                if (c != null && c.IsValid) x["color"] = Hex(c);
                if (w != OverrideGraphicSettings.InvalidPenNumber) x["weight"] = w;
                if (p != null && p != ElementId.InvalidElementId) x["pattern"] = PatternName(doc, p);
                return x.Count == 0 ? null : x;
            }
            object Fill(Color c, ElementId p, bool visible)
            {
                var x = new Dictionary<string, object>();
                if (c != null && c.IsValid) x["color"] = Hex(c);
                if (p != null && p != ElementId.InvalidElementId) x["pattern"] = PatternName(doc, p);
                if (!visible) x["visible"] = false;
                return x.Count == 0 ? null : x;
            }
            void Put(string k, object v) { if (v != null) d[k] = v; }

            Put("projectionLine", Line(o.ProjectionLineColor, o.ProjectionLineWeight, o.ProjectionLinePatternId));
            Put("cutLine", Line(o.CutLineColor, o.CutLineWeight, o.CutLinePatternId));
            Put("surfaceForeground", Fill(o.SurfaceForegroundPatternColor, o.SurfaceForegroundPatternId, o.IsSurfaceForegroundPatternVisible));
            Put("surfaceBackground", Fill(o.SurfaceBackgroundPatternColor, o.SurfaceBackgroundPatternId, o.IsSurfaceBackgroundPatternVisible));
            Put("cutForeground", Fill(o.CutForegroundPatternColor, o.CutForegroundPatternId, o.IsCutForegroundPatternVisible));
            Put("cutBackground", Fill(o.CutBackgroundPatternColor, o.CutBackgroundPatternId, o.IsCutBackgroundPatternVisible));
            if (o.Transparency != 0) d["transparency"] = o.Transparency;
            if (o.Halftone) d["halftone"] = true;
            if (o.DetailLevel != ViewDetailLevel.Undefined) d["detailLevel"] = o.DetailLevel.ToString();
            return d;
        }

        private static string PatternName(Document doc, ElementId id)
        {
            if (id == LinePatternElement.GetSolidPatternId()) return "Solid";
            return doc.GetElement(id)?.Name ?? id.GetValue().ToString(CultureInfo.InvariantCulture);
        }

        public static void EnsureOverridesAllowed(View view)
        {
            if (view.IsTemplate) return;
            if (!view.AreGraphicsOverridesAllowed())
                throw new ToolException("VIEW_NOT_SUPPORTED", $"View '{view.Name}' ({view.ViewType}) does not allow graphic overrides.");
        }

        /// <summary>
        /// Fails with VIEW_CONTROLLED_BY_TEMPLATE when the view's template controls <paramref name="controlled"/>
        /// (e.g. VIS_GRAPHICS_FILTERS): changing the view would not stick.
        /// </summary>
        public static void EnsureNotControlled(Document doc, View view, BuiltInParameter controlled, string what)
        {
            if (view.IsTemplate) return;
            var templateId = view.ViewTemplateId;
            if (templateId == null || templateId == ElementId.InvalidElementId) return;
            var template = doc.GetElement(templateId) as View;
            if (template == null) return;
            var free = template.GetNonControlledTemplateParameterIds();
            if (free.Contains(new ElementId(controlled))) return;
            throw new ToolException("VIEW_CONTROLLED_BY_TEMPLATE",
                $"View '{view.Name}' gets its {what} from the template '{template.Name}'.",
                $"Apply the change to the template '{template.Name}' instead, or detach the template from the view.");
        }

        /// <summary>A view or a view template by name or id.</summary>
        public static View ViewOrTemplate(ToolContext ctx, string view, string template)
        {
            if (!string.IsNullOrWhiteSpace(view) && !string.IsNullOrWhiteSpace(template))
                throw new ToolException("INVALID_ARGUMENTS", "Give view or template, not both.");
            if (!string.IsNullOrWhiteSpace(template)) return ctx.Services.Lookup.ViewTemplate(template);
            if (!string.IsNullOrWhiteSpace(view)) return ctx.Services.Lookup.View(view);
            throw new ToolException("INVALID_ARGUMENTS", "Give view or template.");
        }
    }
}
