using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RevitMCPCommandSet.Framework.Services;

namespace RevitMCPCommandSet.Tools.QA.Support
{
    /// <summary>A naming rule for one kind of element.</summary>
    public sealed class NamingRule
    {
        public string Kind { get; set; }
        public string Format { get; set; }
        public Regex Regex { get; set; }
        public string Source { get; set; }
    }

    /// <summary>
    /// Reads naming conventions from the office knowledge files (knowledge/nomenclatura.md and knowledge/folhas.md):
    /// under a "## Vistas" / "## Níveis" / "## Tipos de família" / "## Numeração" heading, the first line
    /// "Formato: `&lt;A&gt;_&lt;B&gt;`" becomes a regex. &lt;n&gt;, &lt;nn&gt;, &lt;nnn&gt; are digits; &lt;DISCIPLINA&gt;
    /// is one of the codes listed on a "Disciplinas:" line; other fields are any non-empty text.
    /// </summary>
    public static class NamingConvention
    {
        private static readonly Regex FormatLine = new Regex(@"^\s*Formato:\s*`([^`]+)`", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Token = new Regex(@"<([^<>]+)>", RegexOptions.CultureInvariant);
        private static readonly Regex Code = new Regex(@"`([A-Z0-9]{1,8})`", RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, string> Headings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vistas"] = "views",
            ["views"] = "views",
            ["niveis"] = "levels",
            ["levels"] = "levels",
            ["tipos de familia"] = "types",
            ["types"] = "types",
            ["numeracao"] = "sheets",
            ["sheet numbers"] = "sheets",
        };

        public static List<NamingRule> Load(out List<string> sources)
        {
            sources = new List<string>();
            var files = new List<(string file, string[] lines)>();
            foreach (var name in new[] { "nomenclatura.md", "folhas.md" })
            {
                var file = QaSupport.KnowledgeFile(name);
                if (file == null) continue;
                try { files.Add((file, File.ReadAllLines(file, Encoding.UTF8))); }
                catch { continue; }
                sources.Add(file);
            }
            // Discipline codes may be listed in one file and used in another (sheet numbers).
            var disciplines = files.SelectMany(f => f.lines)
                .Where(l => l.IndexOf("Disciplinas", StringComparison.OrdinalIgnoreCase) >= 0)
                .SelectMany(l => Code.Matches(l).Cast<Match>().Select(m => m.Groups[1].Value)).Distinct().ToList();
            return files.SelectMany(f => Parse(f.lines, f.file, disciplines))
                .GroupBy(r => r.Kind).Select(g => g.First()).ToList();
        }

        public static IEnumerable<NamingRule> Parse(string[] lines, string source, IList<string> disciplines)
        {
            string kind = null;
            bool taken = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    var h = TextMatch.Fold(line.Substring(3));
                    kind = Headings.TryGetValue(h, out var k) ? k : null;
                    taken = false;
                    continue;
                }
                if (kind == null || taken) continue;
                var m = FormatLine.Match(line);
                if (!m.Success) continue;
                taken = true;
                var format = m.Groups[1].Value.Trim();
                yield return new NamingRule { Kind = kind, Format = format, Regex = ToRegex(format, disciplines), Source = source };
            }
        }

        public static Regex ToRegex(string format, IList<string> disciplines)
        {
            var sb = new StringBuilder("^");
            int pos = 0;
            foreach (Match t in Token.Matches(format))
            {
                sb.Append(Regex.Escape(format.Substring(pos, t.Index - pos)));
                var name = t.Groups[1].Value.Trim();
                if (Regex.IsMatch(name, "^n+$")) sb.Append(@"\d{" + name.Length + "}");
                else if (TextMatch.Key(name) == "disciplina" && disciplines != null && disciplines.Count > 0)
                    sb.Append("(?:" + string.Join("|", disciplines.Select(Regex.Escape)) + ")");
                else sb.Append(".+?");
                pos = t.Index + t.Length;
            }
            sb.Append(Regex.Escape(format.Substring(pos)));
            sb.Append("$");
            return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
        }
    }
}
