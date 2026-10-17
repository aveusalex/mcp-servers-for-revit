using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>
    /// Pure name checks run before Revit is asked to take a name, so a bad name fails with a clear code instead of a
    /// swallowed exception and a placeholder name. Character set measured by KenLP/RevitMCPServer NameRules (MIT):
    /// Revit refuses these in view, sheet, level, type and family names.
    /// </summary>
    public static class NameRules
    {
        public static readonly char[] IllegalChars = @"\:{}[]|;<>?~`".ToCharArray();

        /// <summary>The illegal characters present in <paramref name="name"/>, in first-seen order.</summary>
        public static char[] IllegalCharsIn(string name) =>
            (name ?? string.Empty).Where(ch => Array.IndexOf(IllegalChars, ch) >= 0 || char.IsControl(ch)).Distinct().ToArray();

        /// <summary>Throws INVALID_NAME for an empty name, surrounding blanks or characters Revit refuses.</summary>
        public static void Validate(string name, string what = "name")
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ToolException("INVALID_NAME", $"The {what} is empty.");
            if (name.Trim().Length != name.Length)
                throw new ToolException("INVALID_NAME", $"The {what} '{name}' starts or ends with blanks.",
                    "Trim the value.");
            var bad = IllegalCharsIn(name);
            if (bad.Length > 0)
                throw new ToolException("INVALID_NAME",
                    $"The {what} '{name}' contains character(s) Revit does not allow: {string.Join(" ", bad.Select(Show))}.",
                    "Revit refuses: " + string.Join(" ", IllegalChars.Select(Show)) + ". Replace them, e.g. with '-'.");
        }

        /// <summary>
        /// Throws NAME_COLLISION when <paramref name="name"/> is already used (case-insensitive, as Revit compares).
        /// <paramref name="existing"/> are the names of the other elements in the same uniqueness scope.
        /// </summary>
        public static void EnsureUnique(string name, IEnumerable<string> existing, string what = "name")
        {
            if (existing != null && existing.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException("NAME_COLLISION", $"The {what} '{name}' is already used in this document.",
                    "Pick another value, or reuse the existing element.");
        }

        /// <summary>
        /// Values that appear more than once within one request (case-insensitive), e.g. two new sheets with the same number.
        /// </summary>
        public static IList<string> DuplicatesIn(IEnumerable<string> names) =>
            (names ?? Enumerable.Empty<string>()).Where(n => n != null)
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        private static string Show(char c) => char.IsControl(c) ? $"U+{(int)c:X4}" : c.ToString();
    }
}
