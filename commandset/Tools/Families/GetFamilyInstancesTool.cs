using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Tools.Families.Support;
using RevitMCPCommandSet.Tools.Parameters.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Families
{
    public sealed class GetFamilyInstancesArgs
    {
        [JsonProperty("family")]
        [Description("Family name (every type of it).")]
        public string Family { get; set; }

        [JsonProperty("type")]
        [Description("One type: id, \"Family: Type\" or type name.")]
        public string Type { get; set; }

        [JsonProperty("category")]
        [Description("Category (any language) to disambiguate names.")]
        public string Category { get; set; }

        [JsonProperty("view")]
        [Description("Only instances visible in this view (name or id).")]
        public string View { get; set; }

        [JsonProperty("level")]
        [Description("Only instances on this level (name or id).")]
        public string Level { get; set; }

        [JsonProperty("fields")]
        [Description("Extra parameters to return per instance ({guid} | {builtIn} | {name}, instance then type), in wire units.")]
        public List<ParameterRef> Fields { get; set; }

        [JsonProperty("limit")]
        [Description("Page size (default 200).")]
        [Range(1, 5000)]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Instances to skip (default 0).")]
        [Range(0, int.MaxValue)]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>FAM-10: instances of a loadable family or type, with location, host and level.</summary>
    [McpTool("get_family_instances", Kind = ToolKind.Read, Toolset = "families", Idempotent = true,
        Description = "List the instances of a loadable family or of one type: id, type, level, host (id and category), " +
                      "location in mm (point + rotation in degrees, or curve ends), facing/hand flipped, group membership " +
                      "and optional extra parameter fields; filter by view and level; paged with an exact total. " +
                      "Read-only.")]
    public sealed class GetFamilyInstancesTool : McpTool<GetFamilyInstancesArgs>
    {
        protected override void Validate(GetFamilyInstancesArgs a)
        {
            if (string.IsNullOrWhiteSpace(a.Family) && string.IsNullOrWhiteSpace(a.Type))
                throw new ToolException("INVALID_ARGUMENTS", "Give family or type.");
        }

        protected override object Run(ToolContext ctx, GetFamilyInstancesArgs a)
        {
            var doc = ctx.Doc;
            List<ElementId> symbolIds;
            if (!string.IsNullOrWhiteSpace(a.Type))
            {
                var type = TypeTools.ResolveType(ctx, string.IsNullOrWhiteSpace(a.Family) || a.Type.Contains(":") ? a.Type : a.Family + ": " + a.Type, a.Category);
                if (!(type is FamilySymbol))
                    throw new ToolException("NOT_A_FAMILY_TYPE", $"'{LookupService.FullName(type)}' is a system type; use find_elements with typeName.", null, "find_elements");
                symbolIds = new List<ElementId> { type.Id };
            }
            else
            {
                var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
                if (!string.IsNullOrWhiteSpace(a.Category))
                {
                    var cat = ctx.Services.Categories.Resolve(a.Category).Id;
                    families = families.Where(f => f.FamilyCategory?.Id == cat).ToList();
                }
                var family = NameMatcher.Single(a.Family, families.Select(f => new NamedCandidate<Family>(f.Name, f, f.FamilyCategory?.Name)), "family");
                symbolIds = family.GetFamilySymbolIds().ToList();
            }

            var filters = symbolIds.Select(id => (ElementFilter)new FamilyInstanceFilter(doc, id)).ToList();
            if (filters.Count == 0) return new { items = new object[0], total = 0, limit = a.Limit, offset = a.Offset, truncated = false };
            ElementFilter filter = filters.Count == 1 ? filters[0] : new LogicalOrFilter(filters);
            var collector = string.IsNullOrWhiteSpace(a.View)
                ? new FilteredElementCollector(doc)
                : new FilteredElementCollector(doc, ctx.Services.Lookup.View(a.View).Id);
            IEnumerable<Element> elements = collector.WherePasses(filter).ToElements();
            if (!string.IsNullOrWhiteSpace(a.Level))
            {
                var level = ctx.Services.Lookup.Level(a.Level).Id;
                elements = elements.Where(e => e.LevelId == level || (e as FamilyInstance)?.Host?.LevelId == level && e.LevelId == ElementId.InvalidElementId);
            }

            var ps = ctx.Services.Parameters;
            var page = Paging.Page(elements.OrderBy(e => e.Id.GetValue()).ToList(), a.Limit, a.Offset);
            return new
            {
                items = page.Items.Select(e =>
                {
                    var fi = e as FamilyInstance;
                    var type = ParameterService.TypeOf(e);
                    var host = fi?.Host;
                    Dictionary<string, object> fields = null;
                    if (a.Fields != null && a.Fields.Count > 0)
                    {
                        fields = new Dictionary<string, object>();
                        foreach (var f in a.Fields)
                        {
                            var p = ElementScope.FindScoped(ps, e, f, "auto");
                            if (p == null) { fields[f.ToString()] = null; continue; }
                            var v = ps.ReadWire(p, out var unit, out _);
                            fields[f.ToString()] = unit == null ? v : new Dictionary<string, object> { ["value"] = v, ["unit"] = unit };
                        }
                    }
                    return new
                    {
                        id = e.Id.GetValue(),
                        uniqueId = e.UniqueId,
                        familyName = type == null ? null : LookupService.SafeFamilyName(type),
                        typeName = type?.Name,
                        typeId = type?.Id.GetValue(),
                        level = doc.GetElement(e.LevelId)?.Name,
                        hostId = host?.Id.GetValue(),
                        hostCategory = host?.Category?.Name,
                        location = EditGeometry.Location(e),
                        facingFlipped = fi?.FacingFlipped,
                        handFlipped = fi?.HandFlipped,
                        groupId = e.GroupId == null || e.GroupId == ElementId.InvalidElementId ? (long?)null : e.GroupId.GetValue(),
                        superComponentId = (fi?.SuperComponent)?.Id.GetValue(),
                        fields
                    };
                }),
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }
    }
}
