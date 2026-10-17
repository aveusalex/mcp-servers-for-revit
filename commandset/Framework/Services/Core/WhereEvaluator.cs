using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>A parameter value prepared for comparison (built by the Revit side).</summary>
    public sealed class ValueSnapshot
    {
        /// <summary>False when the element has no such parameter.</summary>
        public bool Exists { get; set; }

        /// <summary>Text form: the string, the element name for ElementId, the display text otherwise.</summary>
        public string Text { get; set; }

        /// <summary>Number in wire units (mm, m², m³, deg, %); null for text/ElementId or unsupported specs.</summary>
        public double? Number { get; set; }

        public static readonly ValueSnapshot Missing = new ValueSnapshot { Exists = false };
    }

    /// <summary>
    /// The where[] operator set used by find_elements (and later bulk updates). Numbers compare in wire units
    /// (mm, m², ...), never in feet; text compares case-insensitively; an absent parameter matches only is_empty.
    /// Operator model inspired by KenLP/RevitMCPServer WhereSupport (MIT), rewritten.
    /// </summary>
    public static class WhereEvaluator
    {
        public static readonly string[] Operators =
        {
            "eq", "neq", "contains", "not_contains", "starts_with", "ends_with", "regex",
            "gt", "gte", "lt", "lte", "is_empty", "not_empty"
        };

        public static string NormalizeOperator(string op)
        {
            var o = (op ?? "eq").Trim().ToLowerInvariant();
            switch (o)
            {
                case "=": case "==": case "equals": case "equal": return "eq";
                case "!=": case "<>": case "ne": case "not_equals": return "neq";
                case ">": case "greater": return "gt";
                case ">=": case "greater_equal": return "gte";
                case "<": case "less": return "lt";
                case "<=": case "less_equal": return "lte";
                case "startswith": case "starts": return "starts_with";
                case "endswith": case "ends": return "ends_with";
                case "notcontains": case "not_contain": return "not_contains";
                case "matches": case "regexp": return "regex";
                case "empty": case "isempty": case "is_null": return "is_empty";
                case "notempty": case "has_value": case "exists": return "not_empty";
            }
            if (Array.IndexOf(Operators, o) >= 0) return o;
            throw new ToolException("INVALID_ARGUMENTS", $"Unknown where operator '{op}'.",
                "Use one of: " + string.Join(", ", Operators) + ".");
        }

        /// <summary>Validates a regex up front so a bad pattern fails the call instead of matching nothing.</summary>
        public static void ValidateCondition(string op, object value)
        {
            var o = NormalizeOperator(op);
            value = ValueCoercion.ToPrimitive(value);
            if (o == "is_empty" || o == "not_empty") return;
            if (value == null)
                throw new ToolException("INVALID_ARGUMENTS", $"Operator '{o}' needs a value.");
            if (o == "regex")
            {
                try { _ = new Regex(Convert.ToString(value, CultureInfo.InvariantCulture)); }
                catch (ArgumentException ex) { throw new ToolException("INVALID_ARGUMENTS", "Invalid regex: " + ex.Message); }
            }
            if ((o == "gt" || o == "gte" || o == "lt" || o == "lte") && ToNumber(value) == null)
                throw new ToolException("INVALID_ARGUMENTS", $"Operator '{o}' needs a numeric value.");
        }

        public static bool Matches(ValueSnapshot actual, string op, object expected)
        {
            var o = NormalizeOperator(op);
            expected = ValueCoercion.ToPrimitive(expected);
            actual = actual ?? ValueSnapshot.Missing;
            var empty = !actual.Exists || (actual.Number == null && string.IsNullOrWhiteSpace(actual.Text));

            if (o == "is_empty") return empty;
            if (o == "not_empty") return !empty;
            if (!actual.Exists) return false;

            var expectedNumber = ToNumber(expected);
            var expectedText = expected == null ? string.Empty : Convert.ToString(expected, CultureInfo.InvariantCulture);
            var text = actual.Text ?? string.Empty;

            switch (o)
            {
                case "eq":
                    if (actual.Number.HasValue && expectedNumber.HasValue) return NearlyEqual(actual.Number.Value, expectedNumber.Value);
                    return string.Equals(TextMatch.Fold(text), TextMatch.Fold(expectedText), StringComparison.Ordinal);
                case "neq":
                    if (actual.Number.HasValue && expectedNumber.HasValue) return !NearlyEqual(actual.Number.Value, expectedNumber.Value);
                    return !string.Equals(TextMatch.Fold(text), TextMatch.Fold(expectedText), StringComparison.Ordinal);
                case "contains":
                    return TextMatch.Fold(text).IndexOf(TextMatch.Fold(expectedText), StringComparison.Ordinal) >= 0;
                case "not_contains":
                    return TextMatch.Fold(text).IndexOf(TextMatch.Fold(expectedText), StringComparison.Ordinal) < 0;
                case "starts_with":
                    return TextMatch.Fold(text).StartsWith(TextMatch.Fold(expectedText), StringComparison.Ordinal);
                case "ends_with":
                    return TextMatch.Fold(text).EndsWith(TextMatch.Fold(expectedText), StringComparison.Ordinal);
                case "regex":
                    try
                    {
                        return Regex.IsMatch(text, expectedText, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                            TimeSpan.FromMilliseconds(200));
                    }
                    catch (RegexMatchTimeoutException) { return false; }
                case "gt":
                case "gte":
                case "lt":
                case "lte":
                    if (!actual.Number.HasValue || !expectedNumber.HasValue) return false;
                    var a = actual.Number.Value;
                    var b = expectedNumber.Value;
                    switch (o)
                    {
                        case "gt": return a > b && !NearlyEqual(a, b);
                        case "gte": return a > b || NearlyEqual(a, b);
                        case "lt": return a < b && !NearlyEqual(a, b);
                        default: return a < b || NearlyEqual(a, b);
                    }
            }
            return false;
        }

        /// <summary>Numbers are compared with a small tolerance (1e-6 in wire units, i.e. a nanometre for mm).</summary>
        public static bool NearlyEqual(double a, double b) => Math.Abs(a - b) <= 1e-6 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));

        public static double? ToNumber(object value)
        {
            var v = ValueCoercion.ToPrimitive(value);
            switch (v)
            {
                case bool b: return b ? 1 : 0;
                case long l: return l;
                case double d: return d;
                case string s when double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p): return p;
                default: return null;
            }
        }
    }
}
