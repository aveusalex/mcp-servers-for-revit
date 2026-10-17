using System;
using System.Globalization;
using System.Text.RegularExpressions;
using RevitMCPCommandSet.Framework;

namespace RevitMCPCommandSet.Tools.Parameters.Support
{
    /// <summary>
    /// A text edit applied to an existing value: set, prefix, suffix, find/replace, regex replace or clear. Shared by
    /// bulk_update_parameters, rename_elements and rename_types. Pure (no Revit dependency).
    /// </summary>
    public sealed class TextOperation
    {
        public const string Set = "set";
        public const string Prefix = "prefix";
        public const string Suffix = "suffix";
        public const string FindReplace = "findReplace";
        public const string RegexReplace = "regexReplace";
        public const string Clear = "clear";

        private Regex _regex;

        public string Operation { get; private set; }
        public string Value { get; private set; }
        public string Find { get; private set; }
        public string Replace { get; private set; }
        public string Separator { get; private set; }
        public bool IgnoreCase { get; private set; }

        /// <summary>Validates the combination up front (bad regex, missing find...) and throws INVALID_ARGUMENTS.</summary>
        public static TextOperation Create(string operation, string value, string find, string replace, string separator,
            bool ignoreCase)
        {
            var op = Normalize(operation);
            var t = new TextOperation
            {
                Operation = op,
                Value = value,
                Find = find,
                Replace = replace ?? string.Empty,
                Separator = separator ?? string.Empty,
                IgnoreCase = ignoreCase
            };
            switch (op)
            {
                case Prefix:
                case Suffix:
                    if (string.IsNullOrEmpty(value))
                        throw new ToolException("INVALID_ARGUMENTS", $"operation '{op}' needs value (the text to add).");
                    break;
                case FindReplace:
                    if (string.IsNullOrEmpty(find))
                        throw new ToolException("INVALID_ARGUMENTS", "operation 'findReplace' needs find.");
                    break;
                case RegexReplace:
                    if (string.IsNullOrEmpty(find))
                        throw new ToolException("INVALID_ARGUMENTS", "operation 'regexReplace' needs find (the pattern).");
                    try
                    {
                        t._regex = new Regex(find, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant,
                            TimeSpan.FromSeconds(1));
                    }
                    catch (ArgumentException ex)
                    {
                        throw new ToolException("INVALID_ARGUMENTS", $"find is not a valid regular expression: {ex.Message}");
                    }
                    break;
            }
            return t;
        }

        public static string Normalize(string operation)
        {
            var o = (operation ?? Set).Trim();
            switch (o.ToLowerInvariant())
            {
                case "set": return Set;
                case "prefix": return Prefix;
                case "suffix": return Suffix;
                case "findreplace": case "find_replace": case "replace": return FindReplace;
                case "regexreplace": case "regex_replace": case "regex": return RegexReplace;
                case "clear": return Clear;
                default:
                    throw new ToolException("INVALID_ARGUMENTS", $"Unknown text operation '{operation}'.",
                        "Use set, prefix, suffix, findReplace, regexReplace or clear.");
            }
        }

        /// <summary>True for operations that derive the new text from the current one.</summary>
        public bool NeedsCurrent => Operation != Set && Operation != Clear;

        /// <summary>
        /// The new text. <paramref name="counter"/> replaces "{n}" in value/replace (renumbering inside renames).
        /// </summary>
        public string Apply(string current, int counter = 0)
        {
            current = current ?? string.Empty;
            var value = Expand(Value, counter);
            switch (Operation)
            {
                case Set: return value ?? string.Empty;
                case Clear: return string.Empty;
                case Prefix:
                    return current.Length == 0 ? value : value + Separator + current;
                case Suffix:
                    return current.Length == 0 ? value : current + Separator + value;
                case FindReplace:
                    return ReplaceAll(current, Find, Expand(Replace, counter), IgnoreCase);
                case RegexReplace:
                    try
                    {
                        return _regex.Replace(current, Expand(Replace, counter));
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        throw new ToolException("INVALID_ARGUMENTS", "The regular expression took too long; simplify it.");
                    }
                default:
                    return current;
            }
        }

        /// <summary>Whether find/regex matches the text (an operation that would change nothing is skipped).</summary>
        public bool Matches(string current)
        {
            current = current ?? string.Empty;
            switch (Operation)
            {
                case FindReplace:
                    return current.IndexOf(Find, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) >= 0;
                case RegexReplace:
                    return _regex.IsMatch(current);
                default:
                    return true;
            }
        }

        private static string Expand(string text, int counter) =>
            text == null ? null : text.Replace("{n}", counter.ToString(CultureInfo.InvariantCulture));

        private static string ReplaceAll(string input, string find, string replacement, bool ignoreCase)
        {
            if (!ignoreCase) return input.Replace(find, replacement);
            var comparison = StringComparison.OrdinalIgnoreCase;
            var result = new System.Text.StringBuilder();
            int start = 0, index;
            while ((index = input.IndexOf(find, start, comparison)) >= 0)
            {
                result.Append(input, start, index - start).Append(replacement);
                start = index + find.Length;
            }
            result.Append(input, start, input.Length - start);
            return result.ToString();
        }
    }
}
