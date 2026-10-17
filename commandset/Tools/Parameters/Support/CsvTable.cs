using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RevitMCPCommandSet.Framework;

namespace RevitMCPCommandSet.Tools.Parameters.Support
{
    /// <summary>
    /// CSV as Excel pt-BR reads it: UTF-8 with BOM, ';' between fields, quotes around fields that need them. Also the
    /// header convention shared by export_parameters_table / import_parameters_table:
    /// <c>Name [unit] {builtIn:X}</c> or <c>Name {guid:G}</c>. Pure (no Revit dependency).
    /// </summary>
    public static class CsvTable
    {
        public static void Write(string path, IList<string> header, IEnumerable<IList<string>> rows, char delimiter = ';')
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.Write(Line(header, delimiter));
                writer.Write("\r\n");
                foreach (var row in rows)
                {
                    writer.Write(Line(row, delimiter));
                    writer.Write("\r\n");
                }
            }
        }

        public static string Line(IList<string> fields, char delimiter)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append(delimiter);
                sb.Append(Escape(fields[i], delimiter));
            }
            return sb.ToString();
        }

        private static string Escape(string value, char delimiter)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            bool quote = value.IndexOf(delimiter) >= 0 || value.IndexOf('"') >= 0 || value.IndexOf('\n') >= 0 ||
                         value.IndexOf('\r') >= 0 || value.StartsWith(" ", StringComparison.Ordinal) || value.EndsWith(" ", StringComparison.Ordinal);
            return quote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        /// <summary>Reads a CSV file (any BOM). Delimiter null = detect ';', ',' or tab from the header line.</summary>
        public static List<List<string>> Read(string path, char? delimiter = null)
        {
            string text;
            using (var reader = new StreamReader(path, Encoding.UTF8, true)) text = reader.ReadToEnd();
            return Parse(text, delimiter);
        }

        public static List<List<string>> Parse(string text, char? delimiter = null)
        {
            var d = delimiter ?? Detect(text);
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else field.Append(c);
                    continue;
                }
                if (c == '"' && field.Length == 0) { inQuotes = true; continue; }
                if (c == d) { row.Add(field.ToString()); field.Clear(); continue; }
                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(field.ToString());
                    field.Clear();
                    if (!(row.Count == 1 && row[0].Length == 0)) rows.Add(row);
                    row = new List<string>();
                    continue;
                }
                field.Append(c);
            }
            if (inQuotes) throw new ToolException("INVALID_FILE", "The CSV file ends inside a quoted field.");
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                if (!(row.Count == 1 && row[0].Length == 0)) rows.Add(row);
            }
            return rows;
        }

        private static char Detect(string text)
        {
            var end = text.IndexOfAny(new[] { '\r', '\n' });
            var first = end < 0 ? text : text.Substring(0, end);
            int semi = Count(first, ';'), comma = Count(first, ','), tab = Count(first, '\t');
            if (tab > semi && tab > comma) return '\t';
            return comma > semi ? ',' : ';';
        }

        private static int Count(string s, char c)
        {
            int n = 0;
            foreach (var ch in s) if (ch == c) n++;
            return n;
        }

        // ---- header convention -------------------------------------------------------------------------------------

        private static readonly Regex HeaderPattern = new Regex(
            @"^(?<name>.*?)\s*(\[(?<unit>[^\]]+)\])?\s*(\{(?<kind>builtIn|guid):(?<ref>[^}]+)\})?\s*$",
            RegexOptions.CultureInvariant);

        public static string Header(string name, string unit, string builtIn, string guid)
        {
            var h = name ?? string.Empty;
            if (!string.IsNullOrEmpty(unit)) h += " [" + unit + "]";
            if (!string.IsNullOrEmpty(guid)) h += " {guid:" + guid + "}";
            else if (!string.IsNullOrEmpty(builtIn)) h += " {builtIn:" + builtIn + "}";
            return h;
        }

        public static void ParseHeader(string header, out string name, out string unit, out string builtIn, out string guid)
        {
            name = unit = builtIn = guid = null;
            var m = HeaderPattern.Match(header ?? string.Empty);
            if (!m.Success)
            {
                name = header?.Trim();
                return;
            }
            name = m.Groups["name"].Value.Trim();
            if (m.Groups["unit"].Success) unit = m.Groups["unit"].Value.Trim();
            if (m.Groups["kind"].Success)
            {
                if (m.Groups["kind"].Value == "guid") guid = m.Groups["ref"].Value.Trim();
                else builtIn = m.Groups["ref"].Value.Trim();
            }
        }

        // ---- numbers -----------------------------------------------------------------------------------------------

        public static string FormatNumber(double value, char decimalSeparator)
        {
            var s = Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
            return decimalSeparator == ',' ? s.Replace('.', ',') : s;
        }

        /// <summary>Parses a number written with the declared decimal separator (thousands separators refused).</summary>
        public static bool TryParseNumber(string text, char decimalSeparator, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim().Replace(" ", string.Empty).Replace(" ", string.Empty);
            var other = decimalSeparator == ',' ? '.' : ',';
            if (t.IndexOf(other) >= 0) return false;
            if (decimalSeparator == ',') t = t.Replace(',', '.');
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
