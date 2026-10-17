using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>
    /// Language-neutral text helpers: a lenient key for comparing names typed by people (case, accents, spaces and
    /// separators ignored) and edit-distance suggestions. Pure: no Revit types, unit-tested in tests/services.
    /// </summary>
    public static class TextMatch
    {
        /// <summary>
        /// Lower-case, accent-free, separator-free key: "Paredes", "paredes", "PAREDES" and "Paredes " share a key, and
        /// so do "Generic Models", "generic_models" and "GenericModels". "Pavimento Térreo" == "pavimento terreo".
        /// </summary>
        public static string Key(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-' || ch == '.') continue;
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>Same as <see cref="Key"/> but keeps single spaces between words (for display-ish comparisons).</summary>
        public static string Fold(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            var lastSpace = false;
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsWhiteSpace(ch))
                {
                    if (!lastSpace) sb.Append(' ');
                    lastSpace = true;
                    continue;
                }
                lastSpace = false;
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>Classic Levenshtein distance.</summary>
        public static int Distance(string a, string b)
        {
            a = a ?? string.Empty;
            b = b ?? string.Empty;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) prev[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var tmp = prev;
                prev = cur;
                cur = tmp;
            }
            return prev[b.Length];
        }

        /// <summary>
        /// Up to <paramref name="max"/> candidates closest to <paramref name="input"/>, compared by <see cref="Key"/>.
        /// A candidate that contains the input (or is contained by it) ranks first. Far-off candidates are dropped.
        /// </summary>
        public static IList<string> Suggest(string input, IEnumerable<string> candidates, int max = 5)
        {
            var key = Key(input);
            if (candidates == null) return new List<string>();
            var scored = new List<KeyValuePair<string, int>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c) || !seen.Add(c)) continue;
                var ck = Key(c);
                int score;
                if (key.Length > 0 && ck.Length > 0 && (ck.IndexOf(key, StringComparison.Ordinal) >= 0 ||
                                                        key.IndexOf(ck, StringComparison.Ordinal) >= 0))
                    score = Math.Abs(ck.Length - key.Length) / 4;
                else
                    score = Distance(key, ck);
                var threshold = Math.Max(2, Math.Max(key.Length, ck.Length) / 2);
                if (score <= threshold) scored.Add(new KeyValuePair<string, int>(c, score));
            }
            return scored.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(max).Select(kv => kv.Key).ToList();
        }
    }
}
