using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.QA.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA
{
    public sealed class ViewQaArgs
    {
        [JsonProperty("view")]
        [Required]
        [Description("View name or id (plan, section, elevation or 3D).")]
        public string View { get; set; }

        [JsonProperty("categories")]
        [Description("Categories to check, any language (OST_Doors / Doors / Portas). Default: doors, windows and rooms (find_untagged_elements) or doors, windows and walls (find_undimensioned_elements).")]
        public List<string> Categories { get; set; }

        [JsonProperty("countOnly")]
        [Description("Only return the counts (default false).")]
        [DefaultValue(false)]
        public bool CountOnly { get; set; }

        [JsonProperty("limit")]
        [Description("Elements per page, 1-5000 (default 200).")]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Elements to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>QA-06: elements visible in a view that no tag in that view points to.</summary>
    [McpTool("find_untagged_elements", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "qa", Idempotent = true,
        TimeoutSeconds = 120,
        Description = "List the elements of some categories (default doors, windows, rooms) visible in a view that have " +
                      "no tag in that view (element tags, room/space/area tags; tags of linked elements are ignored). " +
                      "Returns counts per category and ids with the element centre in mm, paged. Read-only.")]
    public sealed class FindUntaggedElementsTool : McpTool<ViewQaArgs>
    {
        internal static readonly string[] DefaultCategories = { "OST_Doors", "OST_Windows", "OST_Rooms" };

        protected override object Run(ToolContext ctx, ViewQaArgs a)
        {
            var doc = ctx.Doc;
            var view = QaSupport.GraphicView(ctx, a.View);
            var cats = ctx.Services.Categories.ResolveAll(a.Categories == null || a.Categories.Count == 0 ? DefaultCategories : a.Categories);

            var tagged = new HashSet<long>();
            foreach (var t in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                try
                {
                    foreach (var id in t.GetTaggedLocalElementIds()) tagged.Add(id.GetValue());
                }
                catch
                {
                    // tag of a linked element or orphaned tag
                }
            }
            foreach (var t in new FilteredElementCollector(doc, view.Id).OfClass(typeof(SpatialElementTag)))
            {
                try
                {
                    switch (t)
                    {
                        case RoomTag rt when rt.Room != null: tagged.Add(rt.Room.Id.GetValue()); break;
                        case Autodesk.Revit.DB.Mechanical.SpaceTag st when st.Space != null: tagged.Add(st.Space.Id.GetValue()); break;
                        case AreaTag at when at.Area != null: tagged.Add(at.Area.Id.GetValue()); break;
                    }
                }
                catch
                {
                    // tag of a room in a link
                }
            }

            var elements = InView(doc, view, cats);
            var untagged = elements.Where(e => !tagged.Contains(e.Id.GetValue())).ToList();
            var perCategory = elements.GroupBy(e => e.Category?.Name ?? "?").Select(g => new
            {
                category = g.Key,
                visible = g.Count(),
                untagged = g.Count(e => !tagged.Contains(e.Id.GetValue()))
            }).ToList();

            if (a.CountOnly)
                return new { view = new { id = view.Id.GetValue(), name = view.Name }, visible = elements.Count, untaggedCount = untagged.Count, perCategory };

            var page = Paging.Page(untagged, a.Limit, a.Offset);
            return new
            {
                view = new { id = view.Id.GetValue(), name = view.Name },
                visible = elements.Count,
                untaggedCount = untagged.Count,
                perCategory,
                items = page.Items.Select(e => QaSupport.Brief(doc, e, view, true)).ToList(),
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }

        /// <summary>Instances of the categories visible in the view (rooms without area are skipped).</summary>
        internal static List<Element> InView(Document doc, View view, IList<ResolvedCategory> cats)
        {
            return new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .WherePasses(new ElementMulticategoryFilter(cats.Select(c => c.Id).ToList()))
                .ToElements()
                .Where(e => !(e is SpatialElement s) || s.Area > 0)
                .OrderBy(e => e.Id.GetValue())
                .ToList();
        }
    }
}
