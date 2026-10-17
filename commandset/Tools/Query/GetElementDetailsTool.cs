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
    public sealed class GetElementDetailsArgs
    {
        [JsonProperty("elementIds", Required = Required.Always)]
        [Description("Elements, as strings: id (\"123456\"), UniqueId, or \"linkInstanceId:elementId\" for an element inside a Revit link. 1-50.")]
        public List<string> ElementIds { get; set; }

        [JsonProperty("includeParameters")]
        [Description("Parameters to add: none (default), instance, type or all. For full metadata use get_element_parameters.")]
        [EnumValues("none", "instance", "type", "all")]
        [DefaultValue("none")]
        public string IncludeParameters { get; set; } = "none";

        [JsonProperty("parameters")]
        [Description("Only these parameters (max 20), each {guid} | {builtIn} | {name}. Implies includeParameters=all when that is none.")]
        public List<ParameterRef> Parameters { get; set; }
    }

    /// <summary>CON-02: batch dump of what an element is and where it sits.</summary>
    [McpTool("get_element_details", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "Details of up to 50 elements by id, UniqueId or \"linkInstanceId:elementId\": category, family and " +
                      "type, level, workset, phase created/demolished, design option, group, assembly, owner view, host, " +
                      "pinned, location (point and rotation, or curve end points, in mm and degrees) and bounding box in " +
                      "mm (linked elements: host coordinates). Optionally adds instance/type parameters in wire units " +
                      "(value + Revit's display text). Ids of a link's elements belong to the linked document. Read-only.")]
    public sealed class GetElementDetailsTool : McpTool<GetElementDetailsArgs>
    {
        protected override void Validate(GetElementDetailsArgs args)
        {
            if (args.ElementIds == null || args.ElementIds.Count == 0 || args.ElementIds.Count > 50)
                throw new ToolException("INVALID_ARGUMENTS", "elementIds must list 1 to 50 elements.",
                    "Use find_elements to page through larger sets.", "find_elements");
            if (args.Parameters != null && args.Parameters.Count > 20)
                throw new ToolException("INVALID_ARGUMENTS", "parameters accepts at most 20 entries.");
            var mode = (args.IncludeParameters ?? "none").Trim().ToLowerInvariant();
            if (mode != "none" && mode != "instance" && mode != "type" && mode != "all")
                throw new ToolException("INVALID_ARGUMENTS", "includeParameters must be none, instance, type or all.");
            if (mode == "none" && args.Parameters != null && args.Parameters.Count > 0) mode = "all";
            args.IncludeParameters = mode;
        }

        protected override object Run(ToolContext ctx, GetElementDetailsArgs args)
        {
            var notFound = new List<object>();
            var resolved = QueryHelpers.ResolveMany(ctx, args.ElementIds, notFound);
            var elements = resolved.Select(r => Describe(ctx, r, args)).ToList();
            if (elements.Count == 0)
                throw new ToolException("ELEMENT_NOT_FOUND", "None of the elements was found in the target document.",
                    "Check the ids against the targeted document, or use find_elements.", "find_elements");
            return new { elements, notFound };
        }

        private static object Describe(ToolContext ctx, ResolvedElement r, GetElementDetailsArgs args)
        {
            var e = r.Element;
            var doc = r.Document;
            var transform = r.IsLinked ? r.Link.GetTotalTransform() : null;
            var d = QueryHelpers.Brief(doc, e);
            d["reference"] = r.Ref?.Text;
            d["linkInstanceId"] = r.Link?.Id.GetValue();
            d["isType"] = e is ElementType;
            var type = e is ElementType ? null : ParameterService.TypeOf(e);
            d["typeId"] = type?.Id.GetValue();
            d["className"] = e.GetType().Name;
            d["pinned"] = SafePinned(e);

            if (doc.IsWorkshared)
            {
                try
                {
                    var ws = doc.GetWorksetTable().GetWorkset(e.WorksetId);
                    d["workset"] = ws == null ? null : new { id = (long)ws.Id.IntegerValue, name = ws.Name };
                }
                catch
                {
                    d["workset"] = null;
                }
            }

            d["phaseCreated"] = SafeRef(doc, () => e.CreatedPhaseId);
            d["phaseDemolished"] = SafeRef(doc, () => e.DemolishedPhaseId);
            d["designOption"] = e.DesignOption == null ? null : new { id = e.DesignOption.Id.GetValue(), name = e.DesignOption.Name };
            d["group"] = QueryHelpers.Ref(doc, e.GroupId);
            d["assembly"] = QueryHelpers.Ref(doc, e.AssemblyInstanceId);
            d["ownerView"] = e.ViewSpecific ? QueryHelpers.Ref(doc, e.OwnerViewId) : null;
            if (e is FamilyInstance fi)
            {
                d["host"] = fi.Host == null ? null : new { id = fi.Host.Id.GetValue(), name = QueryHelpers.SafeName(fi.Host), category = fi.Host.Category?.Name };
                d["superComponent"] = fi.SuperComponent == null ? null : (object)fi.SuperComponent.Id.GetValue();
                d["facingFlipped"] = fi.FacingFlipped;
                d["handFlipped"] = fi.HandFlipped;
                d["mirrored"] = fi.Mirrored;
            }
            d["location"] = QueryHelpers.Location(e, transform);
            d["boundingBox"] = QueryHelpers.Box(SafeBox(e), transform);
            if (r.IsLinked) d["coordinates"] = "host";

            if (args.IncludeParameters != "none")
            {
                var ps = r.IsLinked ? new ToolServices(doc).Parameters : ctx.Services.Parameters;
                d["parameters"] = Parameters(ps, e, type, args);
            }
            return d;
        }

        private static List<ParameterInfo> Parameters(ParameterService ps, Element e, ElementType type, GetElementDetailsArgs args)
        {
            var sources = new List<(Element owner, string on)>();
            if (e is ElementType) sources.Add((e, "type"));
            else
            {
                if (args.IncludeParameters != "type") sources.Add((e, "instance"));
                if (args.IncludeParameters != "instance" && type != null) sources.Add((type, "type"));
            }

            var list = new List<ParameterInfo>();
            foreach (var (owner, on) in sources)
            {
                IEnumerable<Parameter> found;
                if (args.Parameters != null && args.Parameters.Count > 0)
                {
                    var l = new List<Parameter>();
                    foreach (var pr in args.Parameters)
                    {
                        try
                        {
                            var p = ps.Find(owner, pr);
                            if (p != null) l.Add(p);
                        }
                        catch (ToolException)
                        {
                            // ambiguous/unknown on this owner: skip, the other owner may have it
                        }
                    }
                    found = l;
                }
                else
                {
                    found = owner.Parameters.Cast<Parameter>().OrderBy(p => p.Definition?.Name, System.StringComparer.CurrentCultureIgnoreCase);
                }
                list.AddRange(found.Select(p => ps.Describe(p, on, true)));
            }
            return list;
        }

        private static object SafeRef(Document doc, System.Func<ElementId> f)
        {
            try { return QueryHelpers.Ref(doc, f()); }
            catch { return null; }
        }

        private static bool? SafePinned(Element e)
        {
            try { return e.Pinned; }
            catch { return null; }
        }

        private static BoundingBoxXYZ SafeBox(Element e)
        {
            try { return e.get_BoundingBox(null); }
            catch { return null; }
        }
    }
}
