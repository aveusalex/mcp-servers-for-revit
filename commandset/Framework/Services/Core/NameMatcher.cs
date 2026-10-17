using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>A named candidate for <see cref="NameMatcher"/>.</summary>
    public sealed class NamedCandidate<T>
    {
        public NamedCandidate(string name, T item, string detail = null)
        {
            Name = name ?? string.Empty;
            Item = item;
            Detail = detail;
        }

        public string Name { get; }
        public T Item { get; }

        /// <summary>Extra text shown next to the name in an AMBIGUOUS_NAME error (e.g. "id 1234").</summary>
        public string Detail { get; }
    }

    /// <summary>
    /// Picks exactly one candidate by name, or fails with a useful error. Never picks "the first one":
    /// exact (ordinal) match → exact ignoring case → match by <see cref="TextMatch.Key"/> (accents, spaces, case).
    /// Several hits at the first level that has any → AMBIGUOUS_NAME listing them; none → NOT_FOUND with suggestions.
    /// </summary>
    public static class NameMatcher
    {
        public const int MaxListed = 20;

        public static T Single<T>(string query, IEnumerable<NamedCandidate<T>> candidates, string what)
        {
            var list = (candidates ?? Enumerable.Empty<NamedCandidate<T>>()).ToList();
            var hits = FindBest(query, list);
            if (hits.Count == 1) return hits[0].Item;

            if (hits.Count > 1)
            {
                var names = hits.Take(MaxListed).Select(h => h.Detail == null ? h.Name : $"{h.Name} ({h.Detail})");
                throw new ToolException("AMBIGUOUS_NAME",
                    $"{Capitalize(what)} name '{query}' matches {hits.Count} elements: {string.Join("; ", names)}.",
                    "Pass the element id instead of the name, or use the exact name.");
            }

            throw NotFound(query, list.Select(c => c.Name), what);
        }

        /// <summary>The candidates that match at the strictest level that has any match (may be empty).</summary>
        public static IList<NamedCandidate<T>> FindBest<T>(string query, IList<NamedCandidate<T>> list)
        {
            query = query ?? string.Empty;
            var exact = list.Where(c => string.Equals(c.Name, query, StringComparison.Ordinal)).ToList();
            if (exact.Count > 0) return exact;
            var trimmed = query.Trim();
            var ignoreCase = list.Where(c => string.Equals(c.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ignoreCase.Count > 0) return ignoreCase;
            var key = TextMatch.Key(query);
            if (key.Length == 0) return new List<NamedCandidate<T>>();
            return list.Where(c => TextMatch.Key(c.Name) == key).ToList();
        }

        public static ToolException NotFound(string query, IEnumerable<string> names, string what)
        {
            var suggestions = TextMatch.Suggest(query, names);
            var hint = suggestions.Count > 0
                ? "Did you mean: " + string.Join(", ", suggestions.Select(s => $"'{s}'")) + "?"
                : $"List the available {what} names first.";
            return new ToolException("NOT_FOUND", $"No {what} named '{query}' was found.", hint);
        }

        private static string Capitalize(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    /// <summary>Splits "Family: Type" (the form Revit shows) into its parts.</summary>
    public static class FamilyTypeName
    {
        /// <summary>
        /// "Folha A1: A1" → ("Folha A1", "A1"). A string without ':' returns (null, text). Revit forbids ':' in family
        /// and type names, so the first colon is the separator.
        /// </summary>
        public static void Parse(string text, out string family, out string type)
        {
            text = (text ?? string.Empty).Trim();
            var i = text.IndexOf(':');
            if (i < 0)
            {
                family = null;
                type = text;
                return;
            }
            family = text.Substring(0, i).Trim();
            type = text.Substring(i + 1).Trim();
            if (family.Length == 0) family = null;
        }
    }
}
