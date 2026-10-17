using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>Mirror of Revit's StorageType, so the coercion rules are testable without Revit.</summary>
    public enum StorageKind
    {
        None,
        Integer,
        Double,
        String,
        ElementId
    }

    /// <summary>A caller's value converted to what Revit's <c>Parameter.Set</c> overload for the storage type expects.</summary>
    public sealed class CoercedValue
    {
        public StorageKind Kind { get; set; }
        public string Text { get; set; }
        public int Integer { get; set; }

        /// <summary>Double in Revit internal units.</summary>
        public double Double { get; set; }

        /// <summary>Element id for ElementId parameters (-1 = none). Null when <see cref="IdName"/> must be resolved.</summary>
        public long? Id { get; set; }

        /// <summary>A name to resolve to an element id (level, phase, material, type...).</summary>
        public string IdName { get; set; }

        /// <summary>The unit the Double was given in ("mm", "internal"...), echoed back in results.</summary>
        public string Unit { get; set; }

        public bool IsClear { get; set; }
    }

    /// <summary>
    /// Rule 4 of the parameter contract: conversion by storage type. String ← anything; Integer ← integer, bool
    /// (yes/no → 1/0) or numeric text; Double ← number or numeric text in wire units (an integer never fails
    /// silently); ElementId ← id or a name to resolve. null clears String and ElementId and is refused for numbers.
    /// </summary>
    public static class ValueCoercion
    {
        private static readonly Regex NumberWithUnit =
            new Regex(@"^\s*([-+]?(?:\d+(?:[.,]\d*)?|[.,]\d+)(?:[eE][-+]?\d+)?)\s*([A-Za-z%°²³\u00C0-\u024F][^\s]*)?\s*$", RegexOptions.CultureInvariant);

        // A dot followed by exactly three digits ("3.000", "-1.250"): a pt-BR thousands group or a decimal.
        private static readonly Regex PtBrThousands =
            new Regex(@"^[-+]?\d{1,3}\.\d{3}$", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> TrueWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "true", "yes", "y", "sim", "s", "on", "verdadeiro" };

        private static readonly HashSet<string> FalseWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "false", "no", "n", "nao", "não", "off", "falso" };

        /// <summary>JSON token → null, bool, long, double or string. Objects and arrays are refused.</summary>
        public static object ToPrimitive(object value)
        {
            if (value is JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Null:
                    case JTokenType.Undefined:
                        return null;
                    case JTokenType.Boolean:
                        return (bool)token;
                    case JTokenType.Integer:
                        return (long)token;
                    case JTokenType.Float:
                        return (double)token;
                    case JTokenType.String:
                    case JTokenType.Guid:
                    case JTokenType.Date:
                        return token.ToString();
                    default:
                        throw new ToolException("INVALID_VALUE", "A parameter value must be a string, number, boolean or null.");
                }
            }
            switch (value)
            {
                case int i: return (long)i;
                case float f: return (double)f;
                case decimal m: return (double)m;
                default: return value;
            }
        }

        /// <param name="kind">The parameter's storage type.</param>
        /// <param name="value">The caller's value (JToken or primitive).</param>
        /// <param name="spec">For Double: what kind of quantity it is.</param>
        /// <param name="unit">Optional caller unit override for Double.</param>
        /// <param name="paramName">For messages.</param>
        public static CoercedValue Coerce(StorageKind kind, object value, UnitSpec spec = UnitSpec.Dimensionless,
            string unit = null, string paramName = null)
        {
            var v = ToPrimitive(value);
            var label = paramName == null ? "the parameter" : $"'{paramName}'";

            switch (kind)
            {
                case StorageKind.String:
                    return new CoercedValue
                    {
                        Kind = kind,
                        IsClear = v == null,
                        Text = v == null ? string.Empty :
                            v is bool b ? (b ? "true" : "false") :
                            Convert.ToString(v, CultureInfo.InvariantCulture)
                    };

                case StorageKind.Integer:
                    return new CoercedValue { Kind = kind, Integer = ToInteger(v, label) };

                case StorageKind.Double:
                {
                    var (number, inlineUnit) = ToDouble(v, label);
                    var effectiveUnit = string.IsNullOrWhiteSpace(unit) ? inlineUnit : unit;
                    var choice = UnitPolicy.Decide(spec, effectiveUnit, paramName);
                    return new CoercedValue { Kind = kind, Double = choice.ToInternal(number), Unit = choice.Label };
                }

                case StorageKind.ElementId:
                    if (v == null) return new CoercedValue { Kind = kind, Id = -1, IsClear = true };
                    if (v is long l) return new CoercedValue { Kind = kind, Id = l <= 0 ? -1 : l };
                    if (v is double d && Math.Abs(d - Math.Round(d)) < 1e-9)
                        return new CoercedValue { Kind = kind, Id = d <= 0 ? -1 : (long)Math.Round(d) };
                    if (v is string s)
                    {
                        var t = s.Trim();
                        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                            return new CoercedValue { Kind = kind, Id = parsed <= 0 ? -1 : parsed };
                        if (t.Length == 0) return new CoercedValue { Kind = kind, Id = -1, IsClear = true };
                        return new CoercedValue { Kind = kind, IdName = t };
                    }
                    throw new ToolException("INVALID_VALUE", $"{Cap(label)} takes an element id or an element name.");

                default:
                    throw new ToolException("INVALID_VALUE", $"{Cap(label)} has no value storage and cannot be set.");
            }
        }

        private static int ToInteger(object v, string label)
        {
            switch (v)
            {
                case null:
                    throw new ToolException("INVALID_VALUE", $"{Cap(label)} is an integer and cannot be cleared with null.");
                case bool b:
                    return b ? 1 : 0;
                case long l:
                    if (l < int.MinValue || l > int.MaxValue)
                        throw new ToolException("INVALID_VALUE", $"{l} is out of range for {label}.");
                    return (int)l;
                case double d:
                    if (Math.Abs(d - Math.Round(d)) > 1e-9 || d < int.MinValue || d > int.MaxValue)
                        throw new ToolException("INVALID_VALUE", $"{Cap(label)} is an integer; {d.ToString(CultureInfo.InvariantCulture)} is not.");
                    return (int)Math.Round(d);
                case string s:
                    var t = s.Trim();
                    if (TrueWords.Contains(t)) return 1;
                    if (FalseWords.Contains(t)) return 0;
                    if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
                    throw new ToolException("INVALID_VALUE", $"{Cap(label)} is an integer or yes/no; '{s}' is neither.");
                default:
                    throw new ToolException("INVALID_VALUE", $"Unsupported value for {label}.");
            }
        }

        /// <summary>Number (or numeric text, optionally followed by a unit like "3000 mm") → (value, inline unit).</summary>
        public static (double value, string unit) ToDouble(object v, string label = "the parameter")
        {
            switch (v)
            {
                case null:
                    throw new ToolException("INVALID_VALUE", $"{Cap(label)} is a number and cannot be cleared with null.",
                        "Pass 0 or the intended number.");
                case bool _:
                    throw new ToolException("INVALID_VALUE", $"{Cap(label)} is a number, not true/false.");
                case long l:
                    return (l, null);
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d))
                        throw new ToolException("INVALID_VALUE", $"{Cap(label)} must be a finite number.");
                    return (d, null);
                case string s:
                    var m = NumberWithUnit.Match(s);
                    if (m.Success)
                    {
                        var num = m.Groups[1].Value;
                        // "3.000" or "1.250.000" read as thousands in pt-BR but as decimals in invariant culture:
                        // refuse rather than guess.
                        if (PtBrThousands.IsMatch(num))
                            throw new ToolException("AMBIGUOUS_NUMBER",
                                $"{Cap(label)}: '{s}' is ambiguous (thousands separator or decimal point?).",
                                "Send a JSON number, e.g. 3000 or 3.0, instead of text.");
                        // Decimal comma is accepted only when there is no dot ("2,5" → 2.5; "1,000.5" is refused).
                        if (num.IndexOf(',') >= 0 && num.IndexOf('.') < 0 && num.Count(c => c == ',') == 1)
                            num = num.Replace(',', '.');
                        if (num.IndexOf(',') < 0 &&
                            double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                            return (parsed, m.Groups[2].Success ? m.Groups[2].Value : null);
                    }
                    throw new ToolException("INVALID_VALUE", $"{Cap(label)} is a number; '{s}' is not a number.",
                        "Send a JSON number in the wire unit (mm, m2, m3, deg).");
                default:
                    throw new ToolException("INVALID_VALUE", $"Unsupported value for {label}.");
            }
        }

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    /// <summary>Rule 8: read-back comparison. Pure.</summary>
    public static class ReadBack
    {
        /// <summary>Doubles are equal within 1e-9 relative (and 1e-9 absolute near zero).</summary>
        public static bool SameDouble(double requested, double actual)
        {
            var diff = Math.Abs(requested - actual);
            return diff <= 1e-9 || diff <= 1e-9 * Math.Max(Math.Abs(requested), Math.Abs(actual));
        }

        /// <summary>Strings compare exactly; null and empty are the same (Revit returns null for a cleared text).</summary>
        public static bool SameText(string requested, string actual) =>
            string.Equals(requested ?? string.Empty, actual ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rule 5: parameters whose change alters what an element is or where it lives (type, levels/constraints, phases,
    /// workset, host). Blocked unless allowCritical; dedicated tools are the intended path. Pure (enum names as text).
    /// </summary>
    public static class CriticalParameters
    {
        public static readonly IReadOnlyCollection<string> BuiltIns = new HashSet<string>(StringComparer.Ordinal)
        {
            "ELEM_TYPE_PARAM", "ELEM_FAMILY_AND_TYPE_PARAM", "ELEM_FAMILY_PARAM", "SYMBOL_ID_PARAM",
            "LEVEL_PARAM", "FAMILY_LEVEL_PARAM", "SCHEDULE_LEVEL_PARAM", "INSTANCE_REFERENCE_LEVEL_PARAM",
            "INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM", "ROOM_LEVEL_ID", "RBS_START_LEVEL_PARAM",
            "WALL_BASE_CONSTRAINT", "WALL_HEIGHT_TYPE", "FAMILY_BASE_LEVEL_PARAM", "FAMILY_TOP_LEVEL_PARAM",
            "STAIRS_BASE_LEVEL_PARAM", "STAIRS_TOP_LEVEL_PARAM", "ROOF_BASE_LEVEL_PARAM", "ROOF_CONSTRAINT_LEVEL_PARAM",
            "PHASE_CREATED", "PHASE_DEMOLISHED",
            "ELEM_PARTITION_PARAM",
            "HOST_ID_PARAM", "INSTANCE_FREE_HOST_PARAM",
        };

        /// <summary>"Mark" is unique per element; writing one value to several elements is blocked too.</summary>
        public const string Mark = "ALL_MODEL_MARK";

        public static bool IsCritical(string builtInName) => builtInName != null && BuiltIns.Contains(builtInName);

        /// <summary>
        /// Returns the reason a write is blocked, or null. <paramref name="sameValueCount"/> is how many elements in
        /// the request get the same value for this parameter.
        /// </summary>
        public static string BlockReason(string builtInName, int sameValueCount, bool allowCritical)
        {
            if (allowCritical || builtInName == null) return null;
            if (BuiltIns.Contains(builtInName))
                return $"{builtInName} changes the element's type, level, phase, workset or host.";
            if (builtInName == Mark && sameValueCount > 1)
                return $"The same Mark would be written to {sameValueCount} elements; Mark is meant to be unique.";
            return null;
        }
    }
}
