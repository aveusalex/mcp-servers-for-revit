using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Selection.Support
{
    /// <summary>Helpers shared by the selection / view UI tools (wave W1).</summary>
    public static class SelectionSupport
    {
        /// <summary>The UIDocument of the target (the dispatcher already checked it is the active one for UI tools).</summary>
        public static UIDocument UiDoc(ToolContext ctx)
        {
            var uiDoc = ctx.UiApp?.ActiveUIDocument;
            if (uiDoc == null || uiDoc.Document == null || !uiDoc.Document.Equals(ctx.Doc))
                throw new ToolException(ToolErrorCodes.RequiresActiveDocument,
                    "This tool acts on Revit's UI and needs the target document to be the active one.",
                    "Ask the user to bring that project's window to the front.", "get_session_context");
            return uiDoc;
        }

        /// <summary>The active view of the target when it is the active document, else null.</summary>
        public static View ActiveViewOrNull(ToolContext ctx)
        {
            var uiDoc = ctx.UiApp?.ActiveUIDocument;
            return uiDoc?.Document != null && uiDoc.Document.Equals(ctx.Doc) ? uiDoc.ActiveView : null;
        }

        /// <summary>
        /// Element ids of the target document from ids / UniqueIds. Linked references and unknown ones go to
        /// <paramref name="notFound"/>.
        /// </summary>
        public static List<ElementId> ResolveIds(ToolContext ctx, IEnumerable<string> refs, List<object> notFound)
        {
            var result = new List<ElementId>();
            var seen = new HashSet<long>();
            foreach (var r in (refs ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                try
                {
                    var resolved = ctx.Services.Lookup.Resolve(r);
                    if (resolved.IsLinked)
                    {
                        notFound.Add(new { reference = r, code = "LINK_NOT_SUPPORTED", message = "Elements inside links can't be selected or shown by id; use the link instance." });
                        continue;
                    }
                    if (seen.Add(resolved.Element.Id.GetValue())) result.Add(resolved.Element.Id);
                }
                catch (ToolException ex)
                {
                    notFound.Add(new { reference = r, code = ex.Code, message = ex.Message });
                }
            }
            return result;
        }

        /// <summary>A saved selection (SelectionFilterElement) by name or id.</summary>
        public static SelectionFilterElement SavedSelection(ToolContext ctx, string nameOrId)
        {
            var all = new FilteredElementCollector(ctx.Doc).OfClass(typeof(SelectionFilterElement))
                .Cast<SelectionFilterElement>().ToList();
            if (all.Count == 0)
                throw new ToolException("NOT_FOUND", "The document has no saved selections.", null, "list_saved_selections");
            return ctx.Services.Lookup.ByNameOrId(nameOrId, all, "saved selection");
        }

        public static object ViewRef(View v) =>
            v == null ? null : new { id = v.Id.GetValue(), name = v.Name, viewType = v.ViewType.ToString() };

        /// <summary>A view by name or id, optionally restricted to a view type (disambiguates "Level 1" plan vs ceiling).</summary>
        public static View View(ToolContext ctx, string nameOrId, string viewType = null)
        {
            if (string.IsNullOrWhiteSpace(viewType)) return ctx.Services.Lookup.View(nameOrId);
            if (!System.Enum.TryParse<ViewType>(viewType.Trim(), true, out var vt))
                throw new ToolException("INVALID_ARGUMENTS", $"Unknown view type '{viewType}'.",
                    "Use Revit's ViewType names: FloorPlan, CeilingPlan, Elevation, Section, ThreeD, DraftingView, Legend, Schedule, DrawingSheet, AreaPlan, Detail.");
            var views = new FilteredElementCollector(ctx.Doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType == vt).ToList();
            return ctx.Services.Lookup.ByNameOrId(nameOrId, views, vt + " view", v => v.ViewType.ToString());
        }
    }
}
