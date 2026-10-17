using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Query.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class GetLinkElementsArgs
    {
        [JsonProperty("link", Required = Required.Always)]
        [Description("The Revit link instance: its id, or its file / type name (\"Structure.rvt\" or \"Structure\").")]
        public string Link { get; set; }

        [JsonProperty("categories", Required = Required.Always)]
        [Description("Categories to read inside the link, any language: OST_Walls / Walls / Paredes (at least one).")]
        public List<string> Categories { get; set; }

        [JsonProperty("fields")]
        [Description("Parameters to return for each element (wire units), each {guid} | {builtIn} | {name}.")]
        public List<ParameterRef> Fields { get; set; }

        [JsonProperty("coordinates")]
        [Description("host (default): locations and boxes transformed into the host model; link: the link's own coordinates.")]
        [EnumValues("host", "link")]
        [DefaultValue("host")]
        public string Coordinates { get; set; } = "host";

        [JsonProperty("limit")]
        [Description("Page size, 1-2000 (default 200).")]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Elements to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>CON-21: read elements inside a Revit link (never writable).</summary>
    [McpTool("get_link_elements", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "Read elements inside a loaded Revit link (read-only): id, reference \"linkInstanceId:elementId\" " +
                      "(usable by get_element_details / get_element_parameters), name, category, family/type, level, " +
                      "location and bounding box in mm (in host coordinates by default, using the link's total " +
                      "transform) and the requested fields. Ids belong to the linked document. Links are never write " +
                      "targets: open the source RVT to edit it. Paged with total. Read-only.")]
    public sealed class GetLinkElementsTool : McpTool<GetLinkElementsArgs>
    {
        protected override void Validate(GetLinkElementsArgs args)
        {
            if (args.Categories == null || args.Categories.Count == 0)
                throw new ToolException("INVALID_ARGUMENTS", "categories must list at least one category.");
            args.Coordinates = (args.Coordinates ?? "host").Trim().ToLowerInvariant();
            QueryHelpers.CheckPaging(args.Limit, args.Offset, 2000);
        }

        protected override object Run(ToolContext ctx, GetLinkElementsArgs args)
        {
            var link = LinkSupport.Resolve(ctx.Doc, args.Link);
            var linkDoc = link.GetLinkDocument()
                          ?? throw new ToolException("LINK_NOT_LOADED", $"The link '{link.Name}' is not loaded.",
                              "Reload it in Manage Links, then retry.", "list_revit_links");

            var linkServices = new ToolServices(linkDoc);
            var categories = linkServices.Categories.ResolveAll(args.Categories);
            var elements = new FilteredElementCollector(linkDoc)
                .WherePasses(new ElementMulticategoryFilter(categories.Select(c => c.Id).ToList()))
                .WhereElementIsNotElementType()
                .OrderBy(e => e.Id.GetValue())
                .ToList();
            var page = Paging.Page(elements, args.Limit, args.Offset, 2000);
            var transform = args.Coordinates == "host" ? link.GetTotalTransform() : null;
            var ps = linkServices.Parameters;
            var linkId = link.Id.GetValue();

            var items = page.Items.Select(e =>
            {
                var d = QueryHelpers.Brief(linkDoc, e);
                d["reference"] = linkId.ToString(CultureInfo.InvariantCulture) + ":" + e.Id.GetValue().ToString(CultureInfo.InvariantCulture);
                d["location"] = QueryHelpers.Location(e, transform);
                BoundingBoxXYZ bb = null;
                try { bb = e.get_BoundingBox(null); } catch { /* none */ }
                d["boundingBox"] = QueryHelpers.Box(bb, transform);
                var f = QueryHelpers.Fields(ps, e, args.Fields);
                if (f != null) d["fields"] = f;
                return d;
            }).ToList();

            return new
            {
                link = new { id = linkId, name = link.Name, title = linkDoc.Title, pathName = linkDoc.PathName },
                coordinates = args.Coordinates,
                items,
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }
    }

    /// <summary>Finds a RevitLinkInstance by id or by file / type / instance name.</summary>
    public static class LinkSupport
    {
        public static RevitLinkInstance Resolve(Document doc, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ToolException("INVALID_ARGUMENTS", "A link id or name is required.");
            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            if (links.Count == 0)
                throw new ToolException("NOT_FOUND", "The target document has no Revit links.");
            if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                var byId = links.FirstOrDefault(l => l.Id.GetValue() == id);
                if (byId != null) return byId;
            }

            string Strip(string s)
            {
                var k = TextMatch.Key(s ?? string.Empty);
                return k.EndsWith(".rvt") ? k.Substring(0, k.Length - 4) : k;
            }

            var key = Strip(text);
            var candidates = links.Select(l =>
            {
                var typeName = (doc.GetElement(l.GetTypeId()) as RevitLinkType)?.Name;
                var instanceFile = (l.Name ?? string.Empty).Split(':')[0].Trim();
                return (l, names: new[] { Strip(typeName), Strip(instanceFile), TextMatch.Key(l.Name) });
            }).ToList();
            var hits = candidates.Where(c => c.names.Contains(key)).Select(c => c.l).ToList();
            if (hits.Count == 0)
                hits = candidates.Where(c => c.names.Any(n => n.Contains(key))).Select(c => c.l).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1)
                throw new ToolException("AMBIGUOUS_NAME",
                    $"'{text}' matches {hits.Count} link instances: {string.Join("; ", hits.Take(20).Select(h => $"{h.Name} (id {h.Id.GetValue()})"))}.",
                    "Pass the link instance id.", "list_revit_links");
            throw new ToolException("NOT_FOUND", $"No Revit link named '{text}'. Links: {string.Join("; ", links.Take(20).Select(l => $"{l.Name} (id {l.Id.GetValue()})"))}.",
                "Use list_revit_links to see the links.", "list_revit_links");
        }
    }
}
