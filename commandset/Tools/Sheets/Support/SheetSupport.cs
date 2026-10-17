using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Sheets.Support
{
    /// <summary>Sheet, viewport and title block helpers. Sheet coordinates are paper mm from the sheet origin.</summary>
    internal static class SheetSupport
    {
        public static List<ViewSheet> AllSheets(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsTemplate).ToList();

        /// <summary>A sheet by element id, sheet number (exact, then ignoring case) or sheet name.</summary>
        public static ViewSheet Sheet(ToolContext ctx, string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new ToolException("INVALID_ARGUMENTS", "A sheet number, name or id is required.");
            var sheets = AllSheets(ctx.Doc);
            var text = reference.Trim();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                var byId = sheets.FirstOrDefault(s => s.Id.GetValue() == id);
                if (byId != null) return byId;
            }
            var byNumber = sheets.Where(s => string.Equals(s.SheetNumber, text, StringComparison.Ordinal)).ToList();
            if (byNumber.Count == 1) return byNumber[0];
            byNumber = sheets.Where(s => string.Equals(s.SheetNumber, text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byNumber.Count == 1) return byNumber[0];
            try
            {
                return NameMatcher.Single(text, sheets.Select(s => new NamedCandidate<ViewSheet>(s.Name, s, "number " + s.SheetNumber)), "sheet");
            }
            catch (ToolException ex) when (ex.Code == "NOT_FOUND")
            {
                var suggestions = TextMatch.Suggest(text, sheets.Select(s => s.SheetNumber).Concat(sheets.Select(s => s.Name)));
                throw new ToolException("SHEET_NOT_FOUND", $"No sheet with number, name or id '{reference}'.",
                    suggestions.Count > 0 ? "Did you mean: " + string.Join(", ", suggestions) + "?" : "Use list_sheets.", "list_sheets");
            }
        }

        public static List<FamilyInstance> TitleBlocks(Document doc, ElementId sheetId) =>
            new FilteredElementCollector(doc, sheetId).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();

        /// <summary>A viewport by its id, or by the id/name of a view placed on exactly one sheet.</summary>
        public static Viewport Viewport(ToolContext ctx, string reference)
        {
            var text = reference?.Trim() ?? string.Empty;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
                ctx.Doc.GetElement(id.ToRevitElementId()) is Viewport direct)
                return direct;
            var view = ctx.Services.Lookup.View(text);
            var ports = new FilteredElementCollector(ctx.Doc).OfClass(typeof(Viewport)).Cast<Viewport>()
                .Where(v => v.ViewId == view.Id).ToList();
            if (ports.Count == 0)
                throw new ToolException("VIEWPORT_NOT_FOUND", $"View '{view.Name}' is not placed on any sheet.", "Use place_on_sheet first.", "place_on_sheet");
            if (ports.Count > 1)
                throw new ToolException("AMBIGUOUS_NAME", $"View '{view.Name}' is placed on {ports.Count} sheets (legend?).",
                    "Pass the viewport id (see get_sheet_layout).", "get_sheet_layout");
            return ports[0];
        }

        public static object Rect(Outline o) => o == null ? null : new
        {
            min = new { x = Mm.FromFt(o.MinimumPoint.X), y = Mm.FromFt(o.MinimumPoint.Y) },
            max = new { x = Mm.FromFt(o.MaximumPoint.X), y = Mm.FromFt(o.MaximumPoint.Y) },
            width = Mm.FromFt(o.MaximumPoint.X - o.MinimumPoint.X),
            height = Mm.FromFt(o.MaximumPoint.Y - o.MinimumPoint.Y)
        };

        public static object Rect(BoundingBoxXYZ b) => b == null ? null : new
        {
            min = new { x = Mm.FromFt(b.Min.X), y = Mm.FromFt(b.Min.Y) },
            max = new { x = Mm.FromFt(b.Max.X), y = Mm.FromFt(b.Max.Y) },
            width = Mm.FromFt(b.Max.X - b.Min.X),
            height = Mm.FromFt(b.Max.Y - b.Min.Y)
        };

        /// <summary>"A-2" &lt; "A-10": compares digit runs numerically.</summary>
        public static int NaturalCompare(string a, string b)
        {
            var ra = Regex.Split(a ?? string.Empty, @"(\d+)");
            var rb = Regex.Split(b ?? string.Empty, @"(\d+)");
            for (var i = 0; i < Math.Min(ra.Length, rb.Length); i++)
            {
                int c;
                if (long.TryParse(ra[i], out var na) && long.TryParse(rb[i], out var nb)) c = na.CompareTo(nb);
                else c = string.Compare(ra[i], rb[i], StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
            return ra.Length.CompareTo(rb.Length);
        }

        public sealed class NaturalComparer : IComparer<string>
        {
            public int Compare(string x, string y) => NaturalCompare(x, y);
        }
    }
}
