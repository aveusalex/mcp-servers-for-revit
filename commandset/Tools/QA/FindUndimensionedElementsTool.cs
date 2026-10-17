using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.QA.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA
{
    /// <summary>QA-07: elements visible in a view that no dimension in that view references.</summary>
    [McpTool("find_undimensioned_elements", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "qa", Idempotent = true,
        TimeoutSeconds = 120,
        Description = "List the elements of some categories visible in a view that no dimension in that view references " +
                      "(default categories: doors, windows and walls when none are given). Only dimensions in that view " +
                      "count; references to grids or levels don't count as dimensioning the element. Returns counts per " +
                      "category and ids with the element centre in mm, paged. Read-only.")]
    public sealed class FindUndimensionedElementsTool : McpTool<ViewQaArgs>
    {
        private static readonly string[] Defaults = { "OST_Doors", "OST_Windows", "OST_Walls" };

        protected override object Run(ToolContext ctx, ViewQaArgs a)
        {
            var doc = ctx.Doc;
            var view = QaSupport.GraphicView(ctx, a.View);
            var cats = ctx.Services.Categories.ResolveAll(a.Categories == null || a.Categories.Count == 0 ? Defaults : a.Categories);

            var referenced = new HashSet<long>();
            int dimensionCount = 0;
            foreach (var d in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                dimensionCount++;
                ReferenceArray refs;
                try { refs = d.References; }
                catch { continue; }
                if (refs == null) continue;
                foreach (Reference r in refs)
                {
                    if (r == null || r.LinkedElementId != ElementId.InvalidElementId) continue;
                    referenced.Add(r.ElementId.GetValue());
                }
            }

            var elements = FindUntaggedElementsTool.InView(doc, view, cats);
            var missing = elements.Where(e => !referenced.Contains(e.Id.GetValue())).ToList();
            var perCategory = elements.GroupBy(e => e.Category?.Name ?? "?").Select(g => new
            {
                category = g.Key,
                visible = g.Count(),
                undimensioned = g.Count(e => !referenced.Contains(e.Id.GetValue()))
            }).ToList();

            if (a.CountOnly)
                return new { view = new { id = view.Id.GetValue(), name = view.Name }, dimensionCount, visible = elements.Count, undimensionedCount = missing.Count, perCategory };

            var page = Paging.Page(missing, a.Limit, a.Offset);
            return new
            {
                view = new { id = view.Id.GetValue(), name = view.Name },
                dimensionCount,
                visible = elements.Count,
                undimensionedCount = missing.Count,
                perCategory,
                items = page.Items.Select(e => QaSupport.Brief(doc, e, view, true)).ToList(),
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }
    }
}
