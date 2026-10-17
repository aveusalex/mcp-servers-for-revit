using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Query;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Parameters.Support
{
    /// <summary>What a bulk tool acts on: explicit ids, or categories narrowed by view, selection and where[].</summary>
    public sealed class ElementScopeRequest
    {
        public IList<string> ElementIds { get; set; }
        public IList<string> Categories { get; set; }
        public string View { get; set; }
        public bool UseSelection { get; set; }
        public IList<WhereCondition> Where { get; set; }

        /// <summary>Collect element types instead of instances (category mode only).</summary>
        public bool Types { get; set; }

        public bool IsEmpty =>
            (ElementIds == null || ElementIds.Count == 0) && (Categories == null || Categories.Count == 0) && !UseSelection;
    }

    /// <summary>
    /// Resolves an <see cref="ElementScopeRequest"/> against the target document with the shared services. Ordered by
    /// id, deterministic. Linked elements are refused (read-only).
    /// </summary>
    public static class ElementScope
    {
        /// <summary>Validates where[] in place (operator normalised, regex checked).</summary>
        public static void ValidateWhere(IList<WhereCondition> where)
        {
            foreach (var w in where ?? new List<WhereCondition>())
            {
                if (w?.Parameter == null || w.Parameter.IsEmpty)
                    throw new ToolException("INVALID_ARGUMENTS", "Every where[] condition needs a parameter.");
                w.Op = WhereEvaluator.NormalizeOperator(w.Op);
                WhereEvaluator.ValidateCondition(w.Op, w.Value);
                var s = (w.Scope ?? "auto").Trim().ToLowerInvariant();
                if (s != "auto" && s != "instance" && s != "type")
                    throw new ToolException("INVALID_ARGUMENTS", $"where[].scope must be auto, instance or type (got '{w.Scope}').");
                w.Scope = s;
            }
        }

        public static List<Element> Collect(ToolContext ctx, ElementScopeRequest request)
        {
            if (request.IsEmpty)
                throw new ToolException("INVALID_ARGUMENTS", "Say which elements: elementIds, categories or useSelection.",
                    "Use find_elements first if you need to search.", "find_elements");

            var services = ctx.Services;
            var doc = ctx.Doc;
            IEnumerable<Element> elements;

            if (request.ElementIds != null && request.ElementIds.Count > 0)
            {
                var list = new List<Element>();
                foreach (var reference in request.ElementIds.Distinct())
                    list.Add(services.Lookup.Element(reference));
                elements = list;
            }
            else if (request.UseSelection)
            {
                elements = SelectedElements(ctx);
            }
            else
            {
                FilteredElementCollector collector = !string.IsNullOrWhiteSpace(request.View)
                    ? new FilteredElementCollector(doc, services.Lookup.View(request.View).Id)
                    : new FilteredElementCollector(doc);
                collector = request.Types ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType();
                var categories = services.Categories.ResolveAll(request.Categories);
                collector = collector.WherePasses(new ElementMulticategoryFilter(categories.Select(c => c.Id).ToList()));
                elements = collector.ToElements();
            }

            if (request.UseSelection && request.Categories != null && request.Categories.Count > 0)
            {
                var ids = new HashSet<long>(services.Categories.ResolveAll(request.Categories).Select(c => c.Id.GetValue()));
                elements = elements.Where(e => e.Category != null && ids.Contains(e.Category.Id.GetValue()));
            }

            var where = request.Where ?? new List<WhereCondition>();
            if (where.Count > 0)
            {
                var ps = services.Parameters;
                elements = elements.Where(e => where.All(w =>
                    WhereEvaluator.Matches(ps.Snapshot(FindScoped(ps, e, w.Parameter, w.Scope)), w.Op, w.Value)));
            }

            return elements.GroupBy(e => e.Id.GetValue()).Select(g => g.First()).OrderBy(e => e.Id.GetValue()).ToList();
        }

        /// <summary>The current selection, when the target is the active document.</summary>
        public static List<Element> SelectedElements(ToolContext ctx)
        {
            var uidoc = ctx.UiApp?.ActiveUIDocument;
            if (uidoc == null || !(ReferenceEquals(uidoc.Document, ctx.Doc) || uidoc.Document.Equals(ctx.Doc)))
                throw new ToolException(ToolErrorCodes.RequiresActiveDocument,
                    "useSelection reads Revit's current selection, so the target must be the active document.",
                    "Pass elementIds instead, or bring the target project to the front.", "get_selected_elements");
            var ids = uidoc.Selection.GetElementIds();
            if (ids.Count == 0)
                throw new ToolException("SELECTION_EMPTY", "Nothing is selected in Revit.",
                    "Ask the user to select the elements, or pass elementIds.");
            return ids.Select(id => ctx.Doc.GetElement(id)).Where(e => e != null).ToList();
        }

        /// <summary>A parameter read for where[]: instance, type or auto (instance, then type).</summary>
        public static Parameter FindScoped(ParameterService ps, Element e, ParameterRef r, string scope)
        {
            if (scope != "type" && !(e is ElementType))
            {
                var p = ps.Find(e, r);
                if (p != null || scope == "instance") return p;
            }
            var t = e is ElementType ? e : ParameterService.TypeOf(e);
            return t == null ? null : ps.Find(t, r);
        }

        public static string SafeName(Element e)
        {
            try { return e.Name; }
            catch { return null; }
        }
    }
}
