using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;

namespace RevitMCPCommandSet.Tools.Annotation.Support
{
    /// <summary>Shared helpers of create_detail_lines and create_filled_region (view-plane 2D drafting).</summary>
    public static class DraftingSupport
    {
        public const double MmPerFt = 304.8;

        /// <summary>A view that accepts detail lines / filled regions (no 3D, no schedules).</summary>
        public static View DraftableView(ToolContext ctx, string viewRef, string what)
        {
            View view;
            if (string.IsNullOrWhiteSpace(viewRef))
            {
                var uiDoc = ctx.UiApp?.ActiveUIDocument;
                view = uiDoc?.Document != null && uiDoc.Document.Equals(ctx.Doc) ? uiDoc.ActiveView : null;
                if (view == null)
                    throw new ToolException("INVALID_ARGUMENTS", "No view given and the target document is not the active one.", "Pass view (list_views).", "list_views");
            }
            else view = ctx.Services.Lookup.View(viewRef);
            if (view is View3D || view is ViewSchedule || view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.SystemBrowser)
                throw new ToolException("VIEW_NOT_SUPPORTED", $"'{view.Name}' ({view.ViewType}) does not accept {what}.",
                    "Use a plan, section, elevation, detail, drafting view, legend or sheet.");
            return view;
        }

        /// <summary>Line styles (subcategories of Lines) by name or id; accepts the name with or without &lt;&gt;.</summary>
        public static GraphicsStyle LineStyle(ToolContext ctx, string text)
        {
            var lines = ctx.Doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            var styles = new List<GraphicsStyle>();
            foreach (Category sub in lines.SubCategories)
            {
                var gs = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                if (gs != null) styles.Add(gs);
            }
            var t = text.Trim();
            var exact = styles.FirstOrDefault(s => string.Equals(s.Name, t, StringComparison.OrdinalIgnoreCase)) ??
                        styles.FirstOrDefault(s => string.Equals(s.Name.Trim('<', '>'), t.Trim('<', '>'), StringComparison.OrdinalIgnoreCase));
            return exact ?? ctx.Services.Lookup.ByNameOrId(t, styles, "line style");
        }

        /// <summary>A view-plane point (mm) to model coordinates on the view's plane.</summary>
        public static XYZ Point(View view, ViewPointMm p) => ViewPlane.ToModel(view, p.X, p.Y);

        public static double Ft(double mm) => mm / MmPerFt;

        public static double Rad(double deg) => deg * Math.PI / 180.0;

        public static void CheckLength(Document doc, XYZ a, XYZ b, string what)
        {
            if (a.DistanceTo(b) < doc.Application.ShortCurveTolerance)
                throw new ToolException("INVALID_GEOMETRY", $"{what} is shorter than Revit's minimum ({Math.Round(doc.Application.ShortCurveTolerance * MmPerFt, 2)} mm).");
        }
    }
}
