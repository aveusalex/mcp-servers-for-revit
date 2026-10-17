using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework.Services;

namespace RevitMCPCommandSet.Tools.Schedules.Support
{
    /// <summary>One row of the rendered schedule body.</summary>
    public sealed class ScheduleRow
    {
        /// <summary>Row index in the body section (0-based, column-heading rows included).</summary>
        public int Index { get; set; }

        /// <summary>"heading" (column headings / heading groups), "blank" or "row" (data, group header/footer, totals).</summary>
        public string Kind { get; set; }

        public List<string> Cells { get; set; }
    }

    /// <summary>
    /// Reads a schedule as Revit renders it (cell text with the document's formatting), keeping only the visible
    /// columns: hidden fields are dropped when Revit includes them in the table data.
    /// </summary>
    public sealed class ScheduleTable
    {
        public string Title { get; private set; }
        public List<string> Columns { get; private set; }
        public List<ScheduleRow> HeadingRows { get; } = new List<ScheduleRow>();
        public List<ScheduleRow> Rows { get; } = new List<ScheduleRow>();
        public int RawColumnCount { get; private set; }

        public static ScheduleTable Read(ViewSchedule schedule)
        {
            var table = new ScheduleTable();
            var def = schedule.Definition;
            var fields = Enumerable.Range(0, def.GetFieldCount()).Select(def.GetField).ToList();
            var visible = fields.Where(f => !f.IsHidden).ToList();

            var data = schedule.GetTableData();
            var header = data.GetSectionData(SectionType.Header);
            if (header != null && header.NumberOfRows > 0 && header.NumberOfColumns > 0)
            {
                var parts = new List<string>();
                for (var r = header.FirstRowNumber; r <= header.LastRowNumber; r++)
                {
                    var text = SafeText(schedule, SectionType.Header, r, header.FirstColumnNumber);
                    if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
                }
                table.Title = parts.Count > 0 ? string.Join(" / ", parts) : null;
            }

            var body = data.GetSectionData(SectionType.Body);
            var columnCount = body?.NumberOfColumns ?? 0;
            table.RawColumnCount = columnCount;

            // Map table columns to fields: either only the visible ones, or all of them (then drop hidden).
            List<int> keep;
            if (columnCount == fields.Count && fields.Count != visible.Count)
            {
                keep = Enumerable.Range(0, columnCount).Where(i => !fields[i].IsHidden).ToList();
                table.Columns = visible.Select(f => f.ColumnHeading).ToList();
            }
            else
            {
                keep = Enumerable.Range(0, columnCount).ToList();
                table.Columns = columnCount == visible.Count
                    ? visible.Select(f => f.ColumnHeading).ToList()
                    : keep.Select(i => i < visible.Count ? visible[i].ColumnHeading : "Column " + (i + 1)).ToList();
            }

            if (body == null || body.NumberOfRows == 0) return table;

            var headingKeys = table.Columns.Select(TextMatch.Key).ToList();
            var rows = new List<ScheduleRow>();
            for (var r = body.FirstRowNumber; r <= body.LastRowNumber; r++)
            {
                var cells = keep.Select(c => SafeText(schedule, SectionType.Body, r, body.FirstColumnNumber + c)).ToList();
                rows.Add(new ScheduleRow
                {
                    Index = r - body.FirstRowNumber,
                    Cells = cells,
                    Kind = cells.All(string.IsNullOrWhiteSpace) ? "blank" : "row"
                });
            }

            // Column headings live at the top of the body (with heading groups above them) when headers are shown.
            var headingRow = -1;
            if (def.ShowHeaders)
            {
                for (var i = 0; i < Math.Min(rows.Count, 6); i++)
                {
                    var matches = rows[i].Cells.Select((c, k) => k < headingKeys.Count && TextMatch.Key(c) == headingKeys[k]).Count(m => m);
                    if (matches > 0 && matches * 2 >= Math.Max(1, headingKeys.Count))
                    {
                        headingRow = i;
                        break;
                    }
                }
            }

            for (var i = 0; i < rows.Count; i++)
            {
                if (i <= headingRow)
                {
                    rows[i].Kind = "heading";
                    table.HeadingRows.Add(rows[i]);
                }
                else
                {
                    table.Rows.Add(rows[i]);
                }
            }
            return table;
        }

        private static string SafeText(ViewSchedule schedule, SectionType section, int row, int column)
        {
            try
            {
                return schedule.GetCellText(section, row, column) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
