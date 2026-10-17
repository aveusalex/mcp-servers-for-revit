using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Query.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class ListFamiliesArgs
    {
        [JsonProperty("nameContains")]
        [Description("Only families whose name contains this text (case and accents ignored).")]
        public string NameContains { get; set; }

        [JsonProperty("categories")]
        [Description("Only families of these categories (any language: OST_Doors / Doors / Portas).")]
        public List<string> Categories { get; set; }

        [JsonProperty("includeTypes")]
        [Description("List the types of each family (default false).")]
        [DefaultValue(false)]
        public bool IncludeTypes { get; set; }

        [JsonProperty("includeInstanceCount")]
        [Description("Count placed instances per family (and per type when includeTypes) (default true).")]
        [DefaultValue(true)]
        public bool IncludeInstanceCount { get; set; } = true;

        [JsonProperty("onlyUnused")]
        [Description("Only families without any placed instance (default false).")]
        [DefaultValue(false)]
        public bool OnlyUnused { get; set; }

        [JsonProperty("inPlace")]
        [Description("false = only loadable families, true = only in-place families, any (default).")]
        [EnumValues("true", "false", "any")]
        [DefaultValue("any")]
        public string InPlace { get; set; } = "any";

        [JsonProperty("limit")]
        [Description("Page size, 1-2000 (default 200).")]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Families to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>CON-24: loaded (component) families with types and usage.</summary>
    [McpTool("list_families", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "List the loadable and in-place families of the model: id, name, category, in-place and editable " +
                      "flags, type count, placed instance count, optionally each type (id, name, instance count). " +
                      "Filter by name text, categories (any language), in-place, or onlyUnused (purge candidates). " +
                      "System families (walls, floors, roofs...) are not Family elements: use get_available_family_types " +
                      "or find_elements with elementKind=types for those. Ordered by category then name, paged. Read-only.")]
    public sealed class ListFamiliesTool : McpTool<ListFamiliesArgs>
    {
        protected override void Validate(ListFamiliesArgs args)
        {
            QueryHelpers.CheckPaging(args.Limit, args.Offset, 2000);
            args.InPlace = (args.InPlace ?? "any").Trim().ToLowerInvariant();
        }

        protected override object Run(ToolContext ctx, ListFamiliesArgs args)
        {
            var doc = ctx.Doc;
            IEnumerable<Family> families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>();
            var categories = ctx.Services.Categories.ResolveAll(args.Categories);
            if (categories.Count > 0)
            {
                var ids = new HashSet<long>(categories.Select(c => c.Id.GetValue()));
                families = families.Where(f => f.FamilyCategory != null && ids.Contains(f.FamilyCategory.Id.GetValue()));
            }
            if (!string.IsNullOrWhiteSpace(args.NameContains))
                families = families.Where(f => QueryHelpers.Contains(f.Name, args.NameContains));
            if (args.InPlace == "true") families = families.Where(f => f.IsInPlace);
            else if (args.InPlace == "false") families = families.Where(f => !f.IsInPlace);

            var needCounts = args.IncludeInstanceCount || args.OnlyUnused;
            Dictionary<long, int> perType = null;
            if (needCounts)
            {
                perType = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType()
                    .Select(e => e.GetTypeId().GetValue())
                    .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            }

            int CountOf(Family f, out List<ElementId> typeIds)
            {
                typeIds = f.GetFamilySymbolIds().ToList();
                if (perType == null) return -1;
                var n = 0;
                foreach (var t in typeIds) n += perType.TryGetValue(t.GetValue(), out var c) ? c : 0;
                return n;
            }

            var rows = families.Select(f =>
            {
                var n = CountOf(f, out var typeIds);
                return (f, n, typeIds);
            }).ToList();
            if (args.OnlyUnused) rows = rows.Where(r => r.n == 0).ToList();

            var ordered = rows.OrderBy(r => r.f.FamilyCategory?.Name ?? "")
                .ThenBy(r => r.f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var page = Paging.Page(ordered, args.Limit, args.Offset, 2000);

            var items = page.Items.Select(r => new
            {
                id = r.f.Id.GetValue(),
                name = r.f.Name,
                category = r.f.FamilyCategory?.Name,
                categoryBuiltIn = CategoryService.BuiltInNameOf(r.f.FamilyCategory),
                isInPlace = r.f.IsInPlace,
                isEditable = r.f.IsEditable,
                typeCount = r.typeIds.Count,
                instanceCount = args.IncludeInstanceCount ? r.n : (int?)null,
                types = args.IncludeTypes
                    ? r.typeIds.Select(t => doc.GetElement(t)).Where(t => t != null).OrderBy(t => t.Name)
                        .Select(t => new
                        {
                            id = t.Id.GetValue(),
                            name = t.Name,
                            instanceCount = perType == null ? (int?)null : (perType.TryGetValue(t.Id.GetValue(), out var c) ? c : 0)
                        }).ToList()
                    : null
            }).ToList();

            return new
            {
                items,
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }
    }
}
