using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>
    /// What kind of quantity a Double parameter holds, decoupled from Revit's ForgeTypeId so the conversion decision
    /// can be unit-tested. The Revit-side <see cref="UnitsService"/> maps SpecTypeId values to this.
    /// </summary>
    public enum UnitSpec
    {
        /// <summary>Plain number (Number, Integer, Currency...): written and read as is.</summary>
        Dimensionless,
        /// <summary>Wire: mm.</summary>
        Length,
        /// <summary>Wire: m².</summary>
        Area,
        /// <summary>Wire: m³.</summary>
        Volume,
        /// <summary>Wire: degrees.</summary>
        Angle,
        /// <summary>Wire: percent (Revit stores rise/run).</summary>
        Slope,
        /// <summary>Force, flow, temperature...: no automatic conversion, only unit "internal" (fail-closed).</summary>
        OtherMeasurable
    }

    /// <summary>A concrete unit: its wire label and the factor that takes a value in it to Revit internal units.</summary>
    public sealed class UnitChoice
    {
        public UnitChoice(string label, double toInternal)
        {
            Label = label;
            ToInternalFactor = toInternal;
        }

        /// <summary>"mm", "m2", "deg", "%", "internal", or null for dimensionless.</summary>
        public string Label { get; }

        /// <summary>internal = value × factor.</summary>
        public double ToInternalFactor { get; }

        public double ToInternal(double value) => value * ToInternalFactor;
        public double FromInternal(double raw) => raw / ToInternalFactor;
    }

    /// <summary>
    /// Pure unit decision. Wire defaults: length mm, area m², volume m³, angle degrees, slope %. A caller may override
    /// with an explicit unit compatible with the spec ("m", "cm", "ft", "in", "ft2", "rad", "ratio"...) or "internal"
    /// to pass Revit's raw value. Anything else fails closed with UNIT_UNSUPPORTED; nothing is guessed.
    /// Inspired by KenLP/RevitMCPServer UnitConversionPolicy (MIT).
    /// </summary>
    public static class UnitPolicy
    {
        public const string Internal = "internal";

        private const double FeetPerMm = 1.0 / 304.8;
        private const double SqFeetPerSqM = 1.0 / 0.09290304;
        private const double CuFeetPerCuM = 1.0 / 0.028316846592;

        private static readonly UnitChoice NoConversion = new UnitChoice(null, 1.0);
        private static readonly UnitChoice InternalChoice = new UnitChoice(Internal, 1.0);

        private static readonly Dictionary<UnitSpec, Dictionary<string, UnitChoice>> Table =
            new Dictionary<UnitSpec, Dictionary<string, UnitChoice>>
            {
                [UnitSpec.Length] = Units(
                    ("mm", FeetPerMm, new[] { "mm", "millimeter", "millimeters", "milimetro", "milimetros" }),
                    ("cm", FeetPerMm * 10, new[] { "cm", "centimeter", "centimeters", "centimetro", "centimetros" }),
                    ("m", FeetPerMm * 1000, new[] { "m", "meter", "meters", "metre", "metres", "metro", "metros" }),
                    ("ft", 1.0, new[] { "ft", "feet", "foot", "pes" }),
                    ("in", 1.0 / 12, new[] { "in", "inch", "inches", "polegada", "polegadas" })),
                [UnitSpec.Area] = Units(
                    ("m2", SqFeetPerSqM, new[] { "m2", "m²", "sqm", "square_meters", "square meters" }),
                    ("cm2", SqFeetPerSqM / 10000, new[] { "cm2", "cm²" }),
                    ("mm2", SqFeetPerSqM / 1000000, new[] { "mm2", "mm²" }),
                    ("ft2", 1.0, new[] { "ft2", "ft²", "sqft", "square_feet", "square feet" })),
                [UnitSpec.Volume] = Units(
                    ("m3", CuFeetPerCuM, new[] { "m3", "m³", "cubic_meters", "cubic meters" }),
                    ("l", CuFeetPerCuM / 1000, new[] { "l", "liter", "liters", "litre", "litres", "litro", "litros" }),
                    ("ft3", 1.0, new[] { "ft3", "ft³", "cubic_feet", "cubic feet" })),
                [UnitSpec.Angle] = Units(
                    ("deg", Math.PI / 180.0, new[] { "deg", "degree", "degrees", "°", "grau", "graus" }),
                    ("rad", 1.0, new[] { "rad", "radian", "radians", "radiano", "radianos" })),
                [UnitSpec.Slope] = Units(
                    ("%", 0.01, new[] { "%", "percent", "pct", "porcento" }),
                    ("ratio", 1.0, new[] { "ratio", "razao" }),
                    ("deg", double.NaN, new[] { "deg", "degree", "degrees", "grau", "graus" })),
            };

        private static readonly Dictionary<UnitSpec, string> Defaults = new Dictionary<UnitSpec, string>
        {
            [UnitSpec.Length] = "mm",
            [UnitSpec.Area] = "m2",
            [UnitSpec.Volume] = "m3",
            [UnitSpec.Angle] = "deg",
            [UnitSpec.Slope] = "%",
        };

        /// <summary>The wire unit used when the caller gives none (null for dimensionless and unsupported specs).</summary>
        public static string DefaultLabel(UnitSpec spec) => Defaults.TryGetValue(spec, out var l) ? l : null;

        /// <summary>
        /// Decide the unit a caller's value is expressed in. <paramref name="unit"/> null/empty = the wire default.
        /// Throws UNIT_UNSUPPORTED when the spec has no automatic conversion or the unit does not fit the spec.
        /// </summary>
        public static UnitChoice Decide(UnitSpec spec, string unit, string paramName = null)
        {
            var u = NormalizeUnit(unit);
            if (u == Internal) return InternalChoice;

            var label = paramName == null ? "This parameter" : $"Parameter '{paramName}'";

            if (spec == UnitSpec.Dimensionless)
            {
                if (u.Length == 0 || u == "none" || u == "number") return NoConversion;
                throw new ToolException("UNIT_UNSUPPORTED",
                    $"{label} is a plain number; unit '{unit}' does not apply.", "Omit 'unit' for this parameter.");
            }

            if (spec == UnitSpec.OtherMeasurable)
                throw new ToolException("UNIT_UNSUPPORTED",
                    $"{label} has a measurable type with no automatic conversion (only length, area, volume, angle and slope are converted).",
                    "Pass unit:\"internal\" and the value in Revit internal units, or use a display string.");

            if (u.Length == 0) u = Defaults[spec];
            var table = Table[spec];
            if (table.TryGetValue(u, out var choice))
            {
                if (spec == UnitSpec.Slope && double.IsNaN(choice.ToInternalFactor))
                    throw new ToolException("UNIT_UNSUPPORTED", $"{label} is a slope; degrees are not supported.",
                        "Pass the slope in percent (default) or unit:\"ratio\".");
                return choice;
            }

            var allowed = string.Join(", ", table.Values.Where(c => !double.IsNaN(c.ToInternalFactor))
                .Select(c => c.Label).Distinct());
            throw new ToolException("UNIT_UNSUPPORTED",
                $"{label} is a {spec.ToString().ToLowerInvariant()}; unit '{unit}' does not fit it.",
                $"Use one of: {allowed}, or internal.");
        }

        /// <summary>Raw internal value → wire value (default unit). Throws UNIT_UNSUPPORTED for OtherMeasurable.</summary>
        public static double FromInternal(double raw, UnitSpec spec, out string unitLabel)
        {
            var choice = Decide(spec, null);
            unitLabel = choice.Label;
            return choice.FromInternal(raw);
        }

        /// <summary>Wire value → raw internal value.</summary>
        public static double ToInternal(double value, UnitSpec spec, string unit = null, string paramName = null) =>
            Decide(spec, unit, paramName).ToInternal(value);

        private static string NormalizeUnit(string unit)
        {
            if (string.IsNullOrWhiteSpace(unit)) return string.Empty;
            var u = unit.Trim().ToLowerInvariant();
            return u == Internal ? u : u.Replace("á", "a").Replace("ã", "a").Replace("é", "e").Replace("í", "i");
        }

        private static Dictionary<string, UnitChoice> Units(params (string label, double factor, string[] names)[] defs)
        {
            var d = new Dictionary<string, UnitChoice>(StringComparer.Ordinal);
            foreach (var (label, factor, names) in defs)
            {
                var c = new UnitChoice(label, factor);
                foreach (var n in names) d[n] = c;
            }
            return d;
        }
    }
}
