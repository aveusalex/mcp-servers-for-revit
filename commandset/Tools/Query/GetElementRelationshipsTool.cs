using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Query.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class GetElementRelationshipsArgs
    {
        [JsonProperty("ids", Required = Required.Always)]
        [Description("Element ids of the target document (1-50).")]
        public List<long> Ids { get; set; }

        [JsonProperty("includeDependents")]
        [Description("Also list dependent elements (what Revit deletes with it: tags, sketches, hosted elements...). Default false.")]
        [DefaultValue(false)]
        public bool IncludeDependents { get; set; }

        [JsonProperty("dependentCategories")]
        [Description("Only dependents of these categories (any language). Default: all with a category.")]
        public List<string> DependentCategories { get; set; }

        [JsonProperty("dependentsLimit")]
        [Description("Most dependents listed per element, 1-1000 (default 100); the count stays exact.")]
        [DefaultValue(100)]
        public int DependentsLimit { get; set; } = 100;
    }

    /// <summary>CON-17: host, group, assembly, nesting, joins, inserts and dependents.</summary>
    [McpTool("get_element_relationships", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "How elements relate to others: host (and host face element), group and assembly they belong to " +
                      "(and members when the element is itself a group or assembly), owner view, design option, nested " +
                      "family super/sub-components, hosted inserts (doors/windows/openings of a wall, roof, floor), " +
                      "geometry joins, and optionally dependents (elements Revit deletes with it), filtered by " +
                      "category and capped per element. Useful before deleting or moving something. Read-only.")]
    public sealed class GetElementRelationshipsTool : McpTool<GetElementRelationshipsArgs>
    {
        protected override void Validate(GetElementRelationshipsArgs args)
        {
            if (args.Ids == null || args.Ids.Count == 0 || args.Ids.Count > 50)
                throw new ToolException("INVALID_ARGUMENTS", "ids must list 1 to 50 elements.");
            if (args.DependentsLimit < 1 || args.DependentsLimit > 1000)
                throw new ToolException("INVALID_ARGUMENTS", "dependentsLimit must be between 1 and 1000.");
        }

        protected override object Run(ToolContext ctx, GetElementRelationshipsArgs args)
        {
            var doc = ctx.Doc;
            var depCats = ctx.Services.Categories.ResolveAll(args.DependentCategories);
            var items = new List<object>();
            var notFound = new List<long>();
            foreach (var id in args.Ids.Distinct())
            {
                var e = doc.GetElement(id.ToRevitElementId());
                if (e == null)
                {
                    notFound.Add(id);
                    continue;
                }
                var d = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = QueryHelpers.SafeName(e),
                    ["category"] = e.Category?.Name,
                    ["group"] = QueryHelpers.Ref(doc, e.GroupId),
                    ["assembly"] = QueryHelpers.Ref(doc, e.AssemblyInstanceId),
                    ["ownerView"] = e.ViewSpecific ? QueryHelpers.Ref(doc, e.OwnerViewId) : null,
                    ["designOption"] = e.DesignOption == null ? null : new { id = e.DesignOption.Id.GetValue(), name = e.DesignOption.Name }
                };

                if (e is FamilyInstance fi)
                {
                    d["host"] = fi.Host == null ? null : Brief(fi.Host);
                    try
                    {
                        var hf = fi.HostFace;
                        if (hf != null && hf.ElementId != ElementId.InvalidElementId && (fi.Host == null || hf.ElementId != fi.Host.Id))
                            d["hostFaceElement"] = QueryHelpers.Ref(doc, hf.ElementId);
                    }
                    catch
                    {
                        // no host face
                    }
                    d["superComponent"] = fi.SuperComponent == null ? null : Brief(fi.SuperComponent);
                    d["subComponents"] = SafeIds(() => fi.GetSubComponentIds()).Select(x => Brief(doc.GetElement(x))).Where(x => x != null).ToList();
                }
                if (e is HostObject ho)
                {
                    var inserts = SafeIds(() => ho.FindInserts(true, true, true, true));
                    d["inserts"] = inserts.Take(args.DependentsLimit).Select(x => Brief(doc.GetElement(x))).Where(x => x != null).ToList();
                    d["insertCount"] = inserts.Count;
                }
                if (e is Group g)
                {
                    var members = SafeIds(() => g.GetMemberIds());
                    d["memberCount"] = members.Count;
                    d["members"] = members.Take(args.DependentsLimit).Select(x => x.GetValue()).ToList();
                }
                if (e is AssemblyInstance ai)
                {
                    var members = SafeIds(() => ai.GetMemberIds());
                    d["memberCount"] = members.Count;
                    d["members"] = members.Take(args.DependentsLimit).Select(x => x.GetValue()).ToList();
                }

                var joined = SafeIds(() => JoinGeometryUtils.GetJoinedElements(doc, e));
                d["joinedElements"] = joined.Take(args.DependentsLimit).Select(x => Brief(doc.GetElement(x))).Where(x => x != null).ToList();

                if (args.IncludeDependents)
                {
                    ElementFilter filter = depCats.Count > 0
                        ? (ElementFilter)new ElementMulticategoryFilter(depCats.Select(c => c.Id).ToList())
                        : null;
                    var deps = SafeIds(() => e.GetDependentElements(filter))
                        .Where(x => x != e.Id)
                        .Select(x => doc.GetElement(x))
                        .Where(x => x?.Category != null)
                        .ToList();
                    d["dependentCount"] = deps.Count;
                    d["dependentsByCategory"] = deps.GroupBy(x => x.Category.Name).ToDictionary(gr => gr.Key, gr => gr.Count());
                    d["dependents"] = deps.Take(args.DependentsLimit).Select(Brief).ToList();
                    d["dependentsTruncated"] = deps.Count > args.DependentsLimit;
                }
                items.Add(d);
            }
            return new { items, notFound };
        }

        private static object Brief(Element e) =>
            e == null ? null : new { id = e.Id.GetValue(), name = QueryHelpers.SafeName(e), category = e.Category?.Name };

        private static IList<ElementId> SafeIds(System.Func<ICollection<ElementId>> f)
        {
            try { return f()?.ToList() ?? new List<ElementId>(); }
            catch { return new List<ElementId>(); }
        }
    }
}
