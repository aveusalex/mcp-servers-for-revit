using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Graphics.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Graphics
{
    public sealed class ColorGradient
    {
        [JsonProperty("from", Required = Required.Always)]
        [Description("Colour of the smallest value (\"#RRGGBB\").")]
        public string From { get; set; }

        [JsonProperty("to", Required = Required.Always)]
        [Description("Colour of the largest value (\"#RRGGBB\").")]
        public string To { get; set; }
    }

    public sealed class ColorElementsByParameterArgs
    {
        [JsonProperty("view", Required = Required.Always)]
        [Description("View (name or id) where the colours are applied.")]
        public string View { get; set; }

        [JsonProperty("categories", Required = Required.Always)]
        [Description("Categories to colour, in any language (OST_Walls, Walls, Paredes).")]
        public List<string> Categories { get; set; }

        [JsonProperty("parameter", Required = Required.Always)]
        [Description("Parameter whose value picks the colour: {builtIn} | {guid} | {name}; read on the instance, then on its type.")]
        public ParameterRef Parameter { get; set; }

        [JsonProperty("colorMap")]
        [Description("Explicit colours by value, e.g. {\"Demolir\": \"#FF0000\"} (value as Revit displays it, or the wire value; case and accents ignored). Other values get palette colours unless onlyMapped.")]
        public Dictionary<string, string> ColorMap { get; set; }

        [JsonProperty("gradient")]
        [Description("For numeric parameters: interpolate from/to colours between the smallest and largest value instead of a palette.")]
        public ColorGradient Gradient { get; set; }

        [JsonProperty("onlyMapped")]
        [Description("Colour only the values listed in colorMap (default false).")]
        [DefaultValue(false)]
        public bool OnlyMapped { get; set; }

        [JsonProperty("noValueColor")]
        [Description("Colour for elements without the parameter or with an empty value. Default: leave them as they are.")]
        public string NoValueColor { get; set; }

        [JsonProperty("target")]
        [Description("What to colour: surface (default, solid foreground fill of surfaces and cuts), lines, or both.")]
        [EnumValues("surface", "lines", "both")]
        [DefaultValue("surface")]
        public string Target { get; set; } = "surface";

        [JsonProperty("elementIds")]
        [Description("Only these elements (ids) among those visible in the view.")]
        public List<long> ElementIds { get; set; }
    }

    /// <summary>
    /// MAT-06: extends the legacy color_elements with colorMap, gradient and a value→colour→count legend table
    /// (no legend view is drawn yet). Existing overrides are merged, not replaced.
    /// </summary>
    [McpTool("color_elements_by_parameter", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "graphics", TimeoutSeconds = 120,
        Description = "Colour the elements of some categories visible in a view by the value of a parameter " +
                      "(instance, then type): distinct palette colours per value, an explicit colorMap {value: " +
                      "\"#RRGGBB\"}, or a gradient for numeric values (sorted numerically). Colours surfaces (solid " +
                      "fill), lines, or both, merging with existing overrides. Returns the legend: value, colour, " +
                      "element count. Undo: Ctrl+Z or clear_graphic_overrides.")]
    public sealed class ColorElementsByParameterTool : McpTool<ColorElementsByParameterArgs>
    {
        private static readonly string[] Palette =
        {
            "#E6194B", "#3CB44B", "#4363D8", "#F58231", "#911EB4", "#42D4F4", "#F032E6", "#BFEF45", "#FABED4", "#469990",
            "#DCBEFF", "#9A6324", "#FFE119", "#800000", "#AAFFC3", "#808000", "#FFD8B1", "#000075", "#A9A9A9", "#00A86B"
        };

        protected override void Validate(ColorElementsByParameterArgs args)
        {
            if (args.Categories == null || args.Categories.Count(c => !string.IsNullOrWhiteSpace(c)) == 0)
                throw new ToolException("INVALID_ARGUMENTS", "categories must list at least one category.");
            if (args.Parameter == null || args.Parameter.IsEmpty)
                throw new ToolException("INVALID_ARGUMENTS", "parameter is required.");
            if (args.OnlyMapped && (args.ColorMap == null || args.ColorMap.Count == 0))
                throw new ToolException("INVALID_ARGUMENTS", "onlyMapped needs colorMap.");
            if (args.Gradient != null && args.ColorMap != null && args.ColorMap.Count > 0)
                throw new ToolException("INVALID_ARGUMENTS", "Use colorMap or gradient, not both.");
            foreach (var kv in args.ColorMap ?? new Dictionary<string, string>()) GraphicsSupport.ParseColor(kv.Value);
            if (args.Gradient != null) { GraphicsSupport.ParseColor(args.Gradient.From); GraphicsSupport.ParseColor(args.Gradient.To); }
            if (args.NoValueColor != null) GraphicsSupport.ParseColor(args.NoValueColor);
            args.Target = (args.Target ?? "surface").Trim().ToLowerInvariant();
        }

        private sealed class Group
        {
            public string Display;
            public string WireText;
            public double? Number;
            public List<ElementId> Ids = new List<ElementId>();
            public Color Color;
        }

        protected override object Run(ToolContext ctx, ColorElementsByParameterArgs args)
        {
            var doc = ctx.Doc;
            var ps = ctx.Services.Parameters;
            var view = ctx.Services.Lookup.View(args.View);
            GraphicsSupport.EnsureOverridesAllowed(view);
            var categories = ctx.Services.Categories.ResolveAll(args.Categories);
            var only = args.ElementIds == null ? null : new HashSet<long>(args.ElementIds);

            var elements = new FilteredElementCollector(doc, view.Id)
                .WherePasses(new ElementMulticategoryFilter(categories.Select(c => c.Id).ToList()))
                .WhereElementIsNotElementType().ToElements()
                .Where(e => only == null || only.Contains(e.Id.GetValue()))
                .ToList();
            if (elements.Count == 0)
                throw new ToolException("NO_ELEMENTS", $"No element of {string.Join(", ", categories.Select(c => c.Name))} is visible in '{view.Name}'.");

            var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
            var noValue = new List<ElementId>();
            foreach (var e in elements)
            {
                var p = ps.Find(e, args.Parameter);
                // Some type values also exist on the instance but empty (e.g. SYMBOL_NAME_PARAM on walls): fall
                // back to the type whenever the instance has no value, not only when the parameter is missing.
                if (p == null || !p.HasValue || string.IsNullOrWhiteSpace(SafeDisplay(p) ?? p.AsString()))
                {
                    var t = ParameterService.TypeOf(e);
                    var tp = t != null ? ps.Find(t, args.Parameter) : null;
                    if (tp != null && tp.HasValue) p = tp;
                }
                if (p == null || !p.HasValue) { noValue.Add(e.Id); continue; }
                var wire = ps.ReadWire(p, out var unit, out _);
                var display = SafeDisplay(p) ?? Convert.ToString(wire, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(display) && (wire == null || string.IsNullOrWhiteSpace(Convert.ToString(wire, CultureInfo.InvariantCulture))))
                {
                    noValue.Add(e.Id);
                    continue;
                }
                var key = TextMatch.Key(display ?? "");
                if (!groups.TryGetValue(key, out var g))
                {
                    double? number = null;
                    if (wire is double d) number = d;
                    else if (wire is int i) number = i;
                    else if (wire is long l) number = l;
                    groups[key] = g = new Group { Display = display, WireText = Convert.ToString(wire, CultureInfo.InvariantCulture), Number = number };
                }
                g.Ids.Add(e.Id);
            }

            var ordered = groups.Values.All(x => x.Number.HasValue)
                ? groups.Values.OrderBy(x => x.Number.Value).ToList()
                : groups.Values.OrderBy(x => x.Display, StringComparer.CurrentCultureIgnoreCase).ToList();

            // Pick colours.
            var map = (args.ColorMap ?? new Dictionary<string, string>())
                .ToDictionary(kv => TextMatch.Key(kv.Key), kv => GraphicsSupport.ParseColor(kv.Value));
            if (args.Gradient != null)
            {
                if (!ordered.All(x => x.Number.HasValue))
                    throw new ToolException("NOT_NUMERIC", $"'{args.Parameter}' has non-numeric values; gradient needs numbers.");
                var from = GraphicsSupport.ParseColor(args.Gradient.From);
                var to = GraphicsSupport.ParseColor(args.Gradient.To);
                var min = ordered.Count > 0 ? ordered.First().Number.Value : 0;
                var max = ordered.Count > 0 ? ordered.Last().Number.Value : 0;
                foreach (var g in ordered)
                {
                    var t = max > min ? (g.Number.Value - min) / (max - min) : 0.0;
                    g.Color = new Color(Lerp(from.Red, to.Red, t), Lerp(from.Green, to.Green, t), Lerp(from.Blue, to.Blue, t));
                }
            }
            else
            {
                var next = 0;
                var usedMapKeys = new HashSet<string>();
                foreach (var g in ordered)
                {
                    if (map.TryGetValue(TextMatch.Key(g.Display), out var c) || map.TryGetValue(TextMatch.Key(g.WireText ?? ""), out c))
                    {
                        g.Color = c;
                        usedMapKeys.Add(TextMatch.Key(g.Display));
                    }
                    else if (!args.OnlyMapped)
                    {
                        g.Color = GraphicsSupport.ParseColor(Palette[next++ % Palette.Length]);
                    }
                }
                if (ordered.Count > Palette.Length && map.Count == 0)
                    ctx.Warnings.Add(new ToolWarning($"{ordered.Count} distinct values but only {Palette.Length} palette colours: colours repeat."));
            }

            var solid = args.Target != "lines" ? GraphicsSupport.SolidFillId(doc) : ElementId.InvalidElementId;
            int colored = 0;
            void Paint(ElementId id, Color c)
            {
                var ogs = view.GetElementOverrides(id);
                if (args.Target != "lines")
                {
                    ogs.SetSurfaceForegroundPatternId(solid);
                    ogs.SetSurfaceForegroundPatternColor(c);
                    ogs.SetSurfaceForegroundPatternVisible(true);
                    ogs.SetCutForegroundPatternId(solid);
                    ogs.SetCutForegroundPatternColor(c);
                    ogs.SetCutForegroundPatternVisible(true);
                }
                if (args.Target != "surface")
                {
                    ogs.SetProjectionLineColor(c);
                    ogs.SetCutLineColor(c);
                }
                view.SetElementOverrides(id, ogs);
                colored++;
            }

            foreach (var g in ordered.Where(x => x.Color != null))
                foreach (var id in g.Ids) Paint(id, g.Color);
            Color noValueColor = args.NoValueColor == null ? null : GraphicsSupport.ParseColor(args.NoValueColor);
            if (noValueColor != null) foreach (var id in noValue) Paint(id, noValueColor);

            var legend = ordered.Select(g => new
            {
                value = g.Display,
                wireValue = g.WireText,
                color = g.Color == null ? null : GraphicsSupport.Hex(g.Color),
                count = g.Ids.Count
            }).ToList();

            return new
            {
                view = view.Name,
                viewId = view.Id.GetValue(),
                parameter = args.Parameter.ToString(),
                elementsConsidered = elements.Count,
                elementsColored = colored,
                noValueCount = noValue.Count,
                noValueColor = noValueColor == null ? null : GraphicsSupport.Hex(noValueColor),
                legend
            };
        }

        private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

        private static string SafeDisplay(Parameter p)
        {
            try
            {
                if (p.StorageType == StorageType.String) return p.AsString();
                return p.AsValueString();
            }
            catch
            {
                return null;
            }
        }
    }
}
