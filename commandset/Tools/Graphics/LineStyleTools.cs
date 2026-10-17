using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Graphics.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Graphics
{
    /// <summary>Line styles = subcategories of the Lines category.</summary>
    internal static class LineStyleSupport
    {
        public const int MinWeight = 1;
        public const int MaxWeight = 16;

        public static Category LinesCategory(Document doc) =>
            doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines)
            ?? throw new ToolException("NOT_AVAILABLE", "This document has no Lines category (line styles are not available here).");

        public static List<Category> All(Document doc) =>
            LinesCategory(doc).SubCategories.Cast<Category>().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        public static Category Find(Document doc, string name)
        {
            var t = (name ?? "").Trim();
            var all = All(doc);
            if (long.TryParse(t, out var id))
            {
                var byId = all.FirstOrDefault(c => c.Id.GetValue() == id);
                if (byId != null) return byId;
            }
            return all.FirstOrDefault(c => string.Equals(c.Name, t, StringComparison.OrdinalIgnoreCase)) ??
                   all.FirstOrDefault(c => string.Equals(c.Name.Trim('<', '>'), t.Trim('<', '>'), StringComparison.OrdinalIgnoreCase));
        }

        public static string PatternName(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return null;
            if (id == LinePatternElement.GetSolidPatternId()) return "Solid";
            return doc.GetElement(id)?.Name;
        }

        public static object Describe(Document doc, Category c)
        {
            return new
            {
                name = c.Name,
                id = c.Id.GetValue(),
                graphicsStyleId = c.GetGraphicsStyle(GraphicsStyleType.Projection)?.Id.GetValue(),
                builtIn = c.Id.GetValue() < 0,
                color = GraphicsSupport.Hex(c.LineColor),
                weight = c.GetLineWeight(GraphicsStyleType.Projection),
                pattern = PatternName(doc, c.GetLinePatternId(GraphicsStyleType.Projection))
            };
        }
    }

    // ---- list_line_styles (read) --------------------------------------------------------------------------------

    public sealed class ListLineStylesArgs
    {
        [JsonProperty("nameContains")]
        [Description("Only styles whose name contains this text (case-insensitive).")]
        public string NameContains { get; set; }

        [JsonProperty("includePatterns")]
        [Description("Also list the line patterns available for manage_line_styles (default false).")]
        [DefaultValue(false)]
        public bool IncludePatterns { get; set; }

        [JsonProperty("limit")]
        [Description("Page size, 1-1000 (default 200).")]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Items to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    [McpTool("list_line_styles", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "graphics",
        Description = "List the line styles of the target document (subcategories of Lines, as in Manage > Object " +
                      "Styles > Annotation/Model lines): name, id, built-in or not, colour (#RRGGBB), projection line " +
                      "weight (1-16) and line pattern. includePatterns also lists the line patterns. Use the names in " +
                      "create_detail_lines (lineStyle) and manage_line_styles. Does not change the model.")]
    public sealed class ListLineStylesTool : McpTool<ListLineStylesArgs>
    {
        protected override void Validate(ListLineStylesArgs a)
        {
            if (a.Limit < 1 || a.Limit > 1000) throw new ToolException("INVALID_ARGUMENTS", "limit must be 1-1000.");
            if (a.Offset < 0) throw new ToolException("INVALID_ARGUMENTS", "offset must be >= 0.");
        }

        protected override object Run(ToolContext ctx, ListLineStylesArgs a)
        {
            var doc = ctx.Doc;
            var styles = LineStyleSupport.All(doc);
            if (!string.IsNullOrWhiteSpace(a.NameContains))
                styles = styles.Where(c => c.Name.IndexOf(a.NameContains.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var page = Paging.Page(styles, a.Limit, a.Offset, 1000);
            return new
            {
                items = page.Items.Select(c => LineStyleSupport.Describe(doc, c)).ToList(),
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated,
                patterns = a.IncludePatterns
                    ? new[] { "Solid" }.Concat(new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement))
                        .Cast<LinePatternElement>().Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)).ToList()
                    : null
            };
        }
    }

    // ---- manage_line_styles (write) -----------------------------------------------------------------------------

    public sealed class LineStyleOperation
    {
        [JsonProperty("op", Required = Required.Always)]
        [Description("create (a new line style; fails if the name exists) or update (an existing one, built-in included).")]
        [EnumValues("create", "update")]
        public string Op { get; set; }

        [JsonProperty("name", Required = Required.Always)]
        [Description("Line style name (or id for update).")]
        public string Name { get; set; }

        [JsonProperty("color")]
        [Description("Line colour, \"#RRGGBB\" or \"r,g,b\". create default: black.")]
        public string Color { get; set; }

        [JsonProperty("weight")]
        [Description("Projection line weight number, 1-16 (pen of Manage > Line Weights; not mm). create default: 1.")]
        [Range(1, 16)]
        public int? Weight { get; set; }

        [JsonProperty("pattern")]
        [Description("Line pattern name or id (\"Solid\" for continuous). create default: Solid.")]
        public string Pattern { get; set; }
    }

    public sealed class ManageLineStylesArgs
    {
        [JsonProperty("operations", Required = Required.Always)]
        [Description("Operations applied in order, all or nothing (up to 100).")]
        public List<LineStyleOperation> Operations { get; set; }
    }

    /// <summary>MAT-16 (MAPA create_line_style, own code): create line styles and set their colour, weight and pattern.</summary>
    [McpTool("manage_line_styles", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "graphics",
        Description = "Create line styles (new subcategories of Lines) and set the colour, projection line weight " +
                      "(1-16) and line pattern of new or existing ones (built-in styles like Thin Lines included). " +
                      "Operations run in order, all or nothing; an existing name on create fails instead of being " +
                      "silently reused. Renaming and deleting line styles are not offered (the API can't rename; " +
                      "deleting is a purge). Returns the styles read back. Undo: Ctrl+Z.")]
    public sealed class ManageLineStylesTool : McpTool<ManageLineStylesArgs>
    {
        private static readonly char[] Invalid = "{}[]|;<>?`~\\:".ToCharArray();

        protected override void Validate(ManageLineStylesArgs a)
        {
            if (a.Operations == null || a.Operations.Count == 0)
                throw new ToolException("INVALID_ARGUMENTS", "operations must list at least one operation.");
            if (a.Operations.Count > 100)
                throw new ToolException("INVALID_ARGUMENTS", "At most 100 operations per call.");
            for (var i = 0; i < a.Operations.Count; i++)
            {
                var o = a.Operations[i] ?? throw new ToolException("INVALID_ARGUMENTS", $"Operation [{i}] is empty.");
                o.Op = (o.Op ?? "").Trim().ToLowerInvariant();
                if (o.Op != "create" && o.Op != "update")
                    throw new ToolException("INVALID_ARGUMENTS", $"Operation [{i}]: op must be create or update.");
                if (string.IsNullOrWhiteSpace(o.Name))
                    throw new ToolException("INVALID_ARGUMENTS", $"Operation [{i}] needs a name.");
                if (o.Op == "create" && o.Name.IndexOfAny(Invalid) >= 0)
                    throw new ToolException("INVALID_ARGUMENTS", $"Operation [{i}]: '{o.Name}' contains a character Revit doesn't allow in names ({{ }} [ ] | ; < > ? ` ~ \\ :).");
                if (o.Op == "update" && o.Color == null && !o.Weight.HasValue && o.Pattern == null)
                    throw new ToolException("INVALID_ARGUMENTS", $"Operation [{i}] update of '{o.Name}' changes nothing.");
                if (o.Weight.HasValue && (o.Weight < LineStyleSupport.MinWeight || o.Weight > LineStyleSupport.MaxWeight))
                    throw new ToolException("INVALID_ARGUMENTS", $"Operation [{i}]: weight must be 1-16.");
                if (!string.IsNullOrWhiteSpace(o.Color)) GraphicsSupport.ParseColor(o.Color);
            }
        }

        protected override object Run(ToolContext ctx, ManageLineStylesArgs a)
        {
            var doc = ctx.Doc;
            var lines = LineStyleSupport.LinesCategory(doc);
            var touched = new List<Category>();
            var results = new List<object>();

            for (var i = 0; i < a.Operations.Count; i++)
            {
                var o = a.Operations[i];
                try
                {
                    Category c;
                    var existing = LineStyleSupport.Find(doc, o.Name);
                    if (o.Op == "create")
                    {
                        if (existing != null)
                            throw new ToolException("ALREADY_EXISTS", $"A line style named '{existing.Name}' already exists.",
                                "Use op=update to change it, or pick another name.");
                        try
                        {
                            c = doc.Settings.Categories.NewSubcategory(lines, o.Name.Trim());
                        }
                        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                        {
                            throw new ToolException("INVALID_ARGUMENTS", $"Revit refused the name '{o.Name}': {ex.Message}");
                        }
                        c.LineColor = new Color(0, 0, 0);
                        c.SetLineWeight(1, GraphicsStyleType.Projection);
                        c.SetLinePatternId(LinePatternElement.GetSolidPatternId(), GraphicsStyleType.Projection);
                    }
                    else
                    {
                        c = existing ?? throw new ToolException("NOT_FOUND", $"No line style named '{o.Name}'.",
                            "Available: " + string.Join(", ", LineStyleSupport.All(doc).Select(s => s.Name).Take(60)),
                            "list_line_styles");
                    }

                    if (!string.IsNullOrWhiteSpace(o.Color)) c.LineColor = GraphicsSupport.ParseColor(o.Color);
                    if (o.Weight.HasValue) c.SetLineWeight(o.Weight.Value, GraphicsStyleType.Projection);
                    if (!string.IsNullOrWhiteSpace(o.Pattern))
                        c.SetLinePatternId(GraphicsSupport.LinePatternId(ctx, o.Pattern), GraphicsStyleType.Projection);

                    if (!touched.Contains(c)) touched.Add(c);
                    results.Add(new { index = i, op = o.Op, name = c.Name, id = c.Id.GetValue() });
                }
                catch (ToolException ex)
                {
                    throw new ToolException(ex.Code, $"Operation [{i}] {o.Op} '{o.Name}': {ex.Message} Nothing was changed.", ex.Hint, ex.RecommendedTool);
                }
            }

            doc.Regenerate();
            return new
            {
                operations = results,
                styles = touched.Select(c => LineStyleSupport.Describe(doc, LineStyleSupport.Find(doc, c.Name) ?? c)).ToList()
            };
        }
    }
}
