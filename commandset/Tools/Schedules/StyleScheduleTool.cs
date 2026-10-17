using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Graphics.Support;
using RevitMCPCommandSet.Tools.Schedules.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Schedules
{
    public sealed class ScheduleCellStyleSpec
    {
        [JsonProperty("textColor")]
        [Description("Text colour, \"#RRGGBB\" or \"r,g,b\".")]
        public string TextColor { get; set; }

        [JsonProperty("backgroundColor")]
        [Description("Background (shading) colour, \"#RRGGBB\" or \"r,g,b\".")]
        public string BackgroundColor { get; set; }

        [JsonProperty("bold")]
        [Description("Bold text on/off.")]
        public bool? Bold { get; set; }

        internal bool IsEmpty => string.IsNullOrWhiteSpace(TextColor) && string.IsNullOrWhiteSpace(BackgroundColor) && !Bold.HasValue;
    }

    public sealed class ScheduleStripesSpec
    {
        [JsonProperty("enabled", Required = Required.Always)]
        [Description("Turn Revit's striped rows on or off.")]
        public bool Enabled { get; set; }

        [JsonProperty("firstColor")]
        [Description("Colour of the first row of the pattern (\"#RRGGBB\"). Default: keep Revit's.")]
        public string FirstColor { get; set; }

        [JsonProperty("secondColor")]
        [Description("Colour of the second row of the pattern (\"#RRGGBB\"). Default: keep Revit's.")]
        public string SecondColor { get; set; }

        [JsonProperty("onSheets")]
        [Description("Also show the stripes where the schedule is placed on sheets (Revit default: off).")]
        public bool? OnSheets { get; set; }
    }

    public sealed class StyleScheduleArgs
    {
        [JsonProperty("schedule", Required = Required.Always)]
        [Description("Schedule name or id.")]
        public string Schedule { get; set; }

        [JsonProperty("title")]
        [Description("Style of the title cells (header section).")]
        public ScheduleCellStyleSpec Title { get; set; }

        [JsonProperty("headings")]
        [Description("Style of the column heading cells (heading rows of the body, grouped headings included).")]
        public ScheduleCellStyleSpec Headings { get; set; }

        [JsonProperty("columns")]
        [Description("Style of whole columns, stored on each field (ScheduleField style): use it to shade/colour the data of some columns.")]
        public ScheduleCellStyleSpec Columns { get; set; }

        [JsonProperty("columnFields")]
        [Description("Restrict 'columns' to these fields (column heading or parameter name). Default: every visible column.")]
        public List<string> ColumnFields { get; set; }

        [JsonProperty("stripes")]
        [Description("Alternating row colours (Revit's native striped rows).")]
        public ScheduleStripesSpec Stripes { get; set; }

        [JsonProperty("gridLinesOnSheet")]
        [Description("Show the grid lines when the schedule is on a sheet.")]
        public bool? GridLinesOnSheet { get; set; }

        [JsonProperty("titleTextType")]
        [Description("Text type (TextNoteType name or id) for the title: font, size and the type's colour.")]
        public string TitleTextType { get; set; }

        [JsonProperty("headerTextType")]
        [Description("Text type for the column headings.")]
        public string HeaderTextType { get; set; }

        [JsonProperty("bodyTextType")]
        [Description("Text type for the body (data rows).")]
        public string BodyTextType { get; set; }
    }

    /// <summary>
    /// TAB-11 (MAPA colorize_schedule_header / colorize_schedule_stripe, own code): colours, bold and shading of a
    /// schedule's title, headings and columns; native striped rows; grid lines; text types.
    /// Revit has no API (nor UI) to shade individual data rows: per-row colour is only possible with striped rows
    /// (two colours alternating) or conditional formatting (not exposed for colours here).
    /// </summary>
    [McpTool("style_schedule", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "schedules",
        Description = "Format a schedule: text colour, background (shading) and bold of the title cells, the column " +
                      "headings and whole columns (field style); Revit's native striped rows (on/off, two colours, " +
                      "optionally on sheets); grid lines on sheets; and the text types (font/size) of title, headings " +
                      "and body. Individual data rows can't be coloured in Revit except by the stripes. Only what is " +
                      "passed changes; returns the styles read back. Undo: Ctrl+Z.")]
    public sealed class StyleScheduleTool : McpTool<StyleScheduleArgs>
    {
        protected override void Validate(StyleScheduleArgs a)
        {
            bool any = (a.Title != null && !a.Title.IsEmpty) || (a.Headings != null && !a.Headings.IsEmpty) ||
                       (a.Columns != null && !a.Columns.IsEmpty) || a.Stripes != null || a.GridLinesOnSheet.HasValue ||
                       !string.IsNullOrWhiteSpace(a.TitleTextType) || !string.IsNullOrWhiteSpace(a.HeaderTextType) ||
                       !string.IsNullOrWhiteSpace(a.BodyTextType);
            if (!any)
                throw new ToolException("INVALID_ARGUMENTS", "Nothing to change: pass title, headings, columns, stripes, gridLinesOnSheet or a text type.");
            if (a.ColumnFields != null && a.ColumnFields.Count > 0 && (a.Columns == null || a.Columns.IsEmpty))
                throw new ToolException("INVALID_ARGUMENTS", "columnFields needs a 'columns' style.");
            foreach (var spec in new[] { a.Title, a.Headings, a.Columns }.Where(s => s != null))
            {
                if (!string.IsNullOrWhiteSpace(spec.TextColor)) GraphicsSupport.ParseColor(spec.TextColor);
                if (!string.IsNullOrWhiteSpace(spec.BackgroundColor)) GraphicsSupport.ParseColor(spec.BackgroundColor);
            }
            if (a.Stripes != null)
            {
                if (!string.IsNullOrWhiteSpace(a.Stripes.FirstColor)) GraphicsSupport.ParseColor(a.Stripes.FirstColor);
                if (!string.IsNullOrWhiteSpace(a.Stripes.SecondColor)) GraphicsSupport.ParseColor(a.Stripes.SecondColor);
            }
        }

        protected override object Run(ToolContext ctx, StyleScheduleArgs a)
        {
            var doc = ctx.Doc;
            var schedule = ScheduleSupport.Find(ctx, a.Schedule);
            var def = schedule.Definition;
            var applied = new Dictionary<string, object>();

            // Text types first (they don't depend on the table layout).
            var textTypes = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
            if (!string.IsNullOrWhiteSpace(a.TitleTextType))
                schedule.TitleTextTypeId = ctx.Services.Lookup.ByNameOrId(a.TitleTextType, textTypes, "text type").Id;
            if (!string.IsNullOrWhiteSpace(a.HeaderTextType))
                schedule.HeaderTextTypeId = ctx.Services.Lookup.ByNameOrId(a.HeaderTextType, textTypes, "text type").Id;
            if (!string.IsNullOrWhiteSpace(a.BodyTextType))
                schedule.BodyTextTypeId = ctx.Services.Lookup.ByNameOrId(a.BodyTextType, textTypes, "text type").Id;

            if (a.GridLinesOnSheet.HasValue) def.ShowGridLines = a.GridLinesOnSheet.Value;

            if (a.Stripes != null)
            {
                schedule.HasStripedRows = a.Stripes.Enabled;
                if (!string.IsNullOrWhiteSpace(a.Stripes.FirstColor))
                    schedule.SetStripedRowsColor(StripedRowPattern.FirstRow, GraphicsSupport.ParseColor(a.Stripes.FirstColor));
                if (!string.IsNullOrWhiteSpace(a.Stripes.SecondColor))
                    schedule.SetStripedRowsColor(StripedRowPattern.SecondRow, GraphicsSupport.ParseColor(a.Stripes.SecondColor));
                if (a.Stripes.OnSheets.HasValue) schedule.UseStripedRowsOnSheets = a.Stripes.OnSheets.Value;
            }

            var visible = def.GetFieldOrder().Select(def.GetField).Where(f => !f.IsHidden).ToList();
            var table = schedule.GetTableData();

            if (a.Title != null && !a.Title.IsEmpty)
            {
                var header = table.GetSectionData(SectionType.Header);
                if (header == null || header.NumberOfRows == 0 || !def.ShowTitle)
                    ctx.Warnings.Add(new ToolWarning("The schedule shows no title, so the title style was not applied (turn the title on in its Appearance).", null, "info"));
                else
                    applied["titleCells"] = StyleRows(header, header.FirstRowNumber, header.LastRowNumber, a.Title, ctx);
            }

            if (a.Headings != null && !a.Headings.IsEmpty)
            {
                var body = table.GetSectionData(SectionType.Body);
                var headingRow = def.ShowHeaders ? FindHeadingRow(schedule, body, visible) : (int?)null;
                if (headingRow == null)
                    ctx.Warnings.Add(new ToolWarning("No column heading row was found (headings hidden?), so the heading style was not applied.", null, "info"));
                else
                    applied["headingCells"] = StyleRows(body, body.FirstRowNumber, headingRow.Value, a.Headings, ctx);
            }

            if (a.Columns != null && !a.Columns.IsEmpty)
            {
                var targets = visible;
                if (a.ColumnFields != null && a.ColumnFields.Count > 0)
                {
                    targets = new List<ScheduleField>();
                    foreach (var name in a.ColumnFields.Where(n => !string.IsNullOrWhiteSpace(n)))
                    {
                        var key = name.Trim();
                        var f = visible.FirstOrDefault(x => string.Equals(x.ColumnHeading, key, StringComparison.OrdinalIgnoreCase)) ??
                                visible.FirstOrDefault(x => string.Equals(x.GetName(), key, StringComparison.OrdinalIgnoreCase));
                        if (f == null)
                            throw new ToolException("NOT_FOUND", $"The schedule has no visible column '{key}'.",
                                "Visible columns: " + string.Join(", ", visible.Select(x => x.ColumnHeading)));
                        if (!targets.Contains(f)) targets.Add(f);
                    }
                }
                foreach (var f in targets)
                {
                    var st = f.GetStyle();
                    ApplySpec(st, a.Columns);
                    f.SetStyle(st);
                }
                applied["columns"] = targets.Select(f => f.ColumnHeading).ToList();
            }

            doc.Regenerate(); // table data and styles are stale until regenerated
            return new
            {
                schedule = new { id = schedule.Id.GetValue(), name = schedule.Name },
                applied,
                readBack = ReadBack(schedule, visible)
            };
        }

        private static void ApplySpec(TableCellStyle st, ScheduleCellStyleSpec spec)
        {
            var o = st.GetCellStyleOverrideOptions();
            if (!string.IsNullOrWhiteSpace(spec.TextColor))
            {
                st.TextColor = GraphicsSupport.ParseColor(spec.TextColor);
                o.FontColor = true;
            }
            if (!string.IsNullOrWhiteSpace(spec.BackgroundColor))
            {
                st.BackgroundColor = GraphicsSupport.ParseColor(spec.BackgroundColor);
                o.BackgroundColor = true;
            }
            if (spec.Bold.HasValue)
            {
                st.IsFontBold = spec.Bold.Value;
                o.Bold = true;
            }
            st.SetCellStyleOverrideOptions(o);
        }

        private static int StyleRows(TableSectionData sec, int fromRow, int toRow, ScheduleCellStyleSpec spec, ToolContext ctx)
        {
            int styled = 0, refused = 0;
            for (var r = fromRow; r <= toRow; r++)
            for (var c = sec.FirstColumnNumber; c <= sec.LastColumnNumber; c++)
            {
                bool allowed;
                try { allowed = sec.AllowOverrideCellStyle(r, c); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { allowed = false; }
                if (!allowed) { refused++; continue; }
                var st = new TableCellStyle(sec.GetTableCellStyle(r, c));
                ApplySpec(st, spec);
                sec.SetCellStyle(r, c, st);
                styled++;
            }
            if (styled == 0)
                throw new ToolException("NOT_SUPPORTED", "Revit does not allow overriding the style of those cells in this schedule. Nothing was changed.");
            if (refused > 0)
                ctx.Warnings.Add(new ToolWarning($"{refused} cell(s) do not accept a style override (merged or locked by Revit) and were left as they were.", null, "info"));
            return styled;
        }

        /// <summary>The body row holding the column headings (most visible columns show their heading), or null.</summary>
        private static int? FindHeadingRow(ViewSchedule schedule, TableSectionData body, List<ScheduleField> visible)
        {
            if (body == null || body.NumberOfRows == 0 || visible.Count == 0) return null;
            var last = Math.Min(body.LastRowNumber, body.FirstRowNumber + 6);
            for (var r = body.FirstRowNumber; r <= last; r++)
            {
                int matches = 0;
                for (var i = 0; i < visible.Count && body.FirstColumnNumber + i <= body.LastColumnNumber; i++)
                {
                    string text;
                    try { text = schedule.GetCellText(SectionType.Body, r, body.FirstColumnNumber + i); }
                    catch (Autodesk.Revit.Exceptions.ArgumentException) { continue; }
                    if (string.Equals((text ?? "").Trim(), (visible[i].ColumnHeading ?? "").Trim(), StringComparison.Ordinal)) matches++;
                }
                if (matches > 0 && matches * 2 >= visible.Count) return r;
            }
            return null;
        }

        private static object Style(TableCellStyle st)
        {
            if (st == null) return null;
            var o = st.GetCellStyleOverrideOptions();
            return new
            {
                textColor = o.FontColor ? GraphicsSupport.Hex(st.TextColor) : null,
                backgroundColor = o.BackgroundColor ? GraphicsSupport.Hex(st.BackgroundColor) : null,
                bold = o.Bold ? st.IsFontBold : (bool?)null
            };
        }

        private static object ReadBack(ViewSchedule schedule, List<ScheduleField> visible)
        {
            var doc = schedule.Document;
            var def = schedule.Definition;
            var table = schedule.GetTableData();
            var header = table.GetSectionData(SectionType.Header);
            var body = table.GetSectionData(SectionType.Body);
            object title = null, heading = null;
            try
            {
                if (header != null && header.NumberOfRows > 0)
                    title = Style(header.GetTableCellStyle(header.FirstRowNumber, header.FirstColumnNumber));
                var hr = def.ShowHeaders ? FindHeadingRow(schedule, body, visible) : null;
                if (hr != null) heading = Style(body.GetTableCellStyle(hr.Value, body.FirstColumnNumber));
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* read-back is best effort */ }

            string TypeName(ElementId id) => id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id)?.Name;
            return new
            {
                title,
                headings = heading,
                columns = visible.Take(50).Select(f => new { heading = f.ColumnHeading, style = Style(f.GetStyle()) }).ToList(),
                stripes = new
                {
                    enabled = schedule.HasStripedRows,
                    firstColor = GraphicsSupport.Hex(schedule.GetStripedRowsColor(StripedRowPattern.FirstRow)),
                    secondColor = GraphicsSupport.Hex(schedule.GetStripedRowsColor(StripedRowPattern.SecondRow)),
                    onSheets = schedule.UseStripedRowsOnSheets
                },
                gridLinesOnSheet = def.ShowGridLines,
                titleTextType = TypeName(schedule.TitleTextTypeId),
                headerTextType = TypeName(schedule.HeaderTextTypeId),
                bodyTextType = TypeName(schedule.BodyTextTypeId)
            };
        }
    }
}
