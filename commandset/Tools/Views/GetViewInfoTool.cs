using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Selection.Support;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Views
{
    public sealed class GetViewInfoArgs
    {
        [JsonProperty("view")]
        [Description("View name or id. Default: the active view (when the target is the active document).")]
        public string View { get; set; }

        [JsonProperty("viewType")]
        [Description("Revit view type to disambiguate a shared name (FloorPlan, CeilingPlan, Section...).")]
        public string ViewType { get; set; }

        [JsonProperty("includeFilters")]
        [DefaultValue(true)]
        [Description("Include the view filters applied to the view (or its template). Default true.")]
        public bool IncludeFilters { get; set; } = true;
    }

    /// <summary>CON-07: extended information about one view.</summary>
    [McpTool("get_view_info", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "views",
        Description = "Extended information about a view (default: the active view): type, level, scale, detail level, " +
                      "discipline, display style, phase and phase filter, template (and whether it controls the view), " +
                      "crop region and annotation crop, scope box, view range (top/cut/bottom/depth: level + offset and " +
                      "absolute elevation, mm) and underlay for plans, section box/lock/perspective for 3D, sheet " +
                      "placement, title on sheet, dependent views and the applied filters (visibility/enabled). Read only.")]
    public sealed class GetViewInfoTool : McpTool<GetViewInfoArgs>
    {
        protected override object Run(ToolContext ctx, GetViewInfoArgs a)
        {
            var doc = ctx.Doc;
            var active = SelectionSupport.ActiveViewOrNull(ctx);
            View view;
            if (string.IsNullOrWhiteSpace(a.View))
            {
                view = active ?? throw new ToolException("INVALID_ARGUMENTS",
                    "No view given and the target document is not the active one (no active view to read).", "Pass view (list_views).", "list_views");
            }
            else view = string.IsNullOrWhiteSpace(a.ViewType) ? ViewSupport.ViewOrTemplate(ctx, a.View) : SelectionSupport.View(ctx, a.View, a.ViewType);

            var template = view.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(view.ViewTemplateId) as View : null;
            string detail = null;
            try { detail = view.ViewType == ViewType.Schedule ? null : ViewSupport.DetailLevelName(view.DetailLevel); }
            catch { /* no detail level */ }

            string displayStyle = null;
            try { displayStyle = view.DisplayStyle.ToString(); } catch { /* sheets, schedules */ }

            var sheetNumber = Str(view, BuiltInParameter.VIEWER_SHEET_NUMBER);
            var viewport = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault(v => v.ViewId == view.Id);

            return new
            {
                viewId = view.Id.GetValue(),
                uniqueId = view.UniqueId,
                name = view.Name,
                viewType = view.ViewType.ToString(),
                isTemplate = view.IsTemplate,
                isActive = active != null && active.Id == view.Id,
                scale = ViewSupport.SafeScale(view),
                detailLevel = detail,
                discipline = ValueText(view, BuiltInParameter.VIEW_DISCIPLINE),
                displayStyle,
                level = (view as ViewPlan)?.GenLevel?.Name,
                phase = ElementName(doc, view, BuiltInParameter.VIEW_PHASE),
                phaseFilter = ElementName(doc, view, BuiltInParameter.VIEW_PHASE_FILTER),
                template = template == null ? null : new
                {
                    id = template.Id.GetValue(),
                    name = template.Name,
                    controlledParameters = SafeCount(() => template.GetTemplateParameterIds().Count - template.GetNonControlledTemplateParameterIds().Count)
                },
                crop = view.ViewType == ViewType.DrawingSheet || view.ViewType == ViewType.Schedule ? null : ViewSupport.DescribeCrop(view),
                annotationCrop = Bool(view, BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE),
                scopeBox = ElementName(doc, view, BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP),
                viewRange = view is ViewPlan plan ? ViewRange(doc, plan) : null,
                underlay = view is ViewPlan up ? Underlay(doc, up) : null,
                threeD = view is View3D v3 ? new
                {
                    isPerspective = v3.IsPerspective,
                    isLocked = v3.IsLocked,
                    sectionBoxActive = v3.IsSectionBoxActive
                } : null,
                direction = view is View3D || view.ViewType == ViewType.Section || view.ViewType == ViewType.Elevation || view.ViewType == ViewType.Detail
                    ? new { x = Math.Round(view.ViewDirection.X, 6), y = Math.Round(view.ViewDirection.Y, 6), z = Math.Round(view.ViewDirection.Z, 6) }
                    : null,
                sheet = sheetNumber == null ? null : new
                {
                    number = sheetNumber,
                    name = viewport != null ? (doc.GetElement(viewport.SheetId) as ViewSheet)?.Name : null,
                    viewportId = viewport?.Id.GetValue(),
                    detailNumber = Str(view, BuiltInParameter.VIEWPORT_DETAIL_NUMBER)
                },
                titleOnSheet = Str(view, BuiltInParameter.VIEW_DESCRIPTION),
                temporaryHideIsolate = SafeBool(() => view.IsTemporaryHideIsolateActive()),
                dependentViewIds = SafeList(() => view.GetDependentViewIds().Select(id => id.GetValue()).ToList()),
                primaryViewId = view.GetPrimaryViewId() != ElementId.InvalidElementId ? view.GetPrimaryViewId().GetValue() : (long?)null,
                filters = a.IncludeFilters ? Filters(doc, view) : null,
                filtersFromTemplate = a.IncludeFilters && template != null ? Filters(doc, template) : null
            };
        }

        internal static List<object> Filters(Document doc, View view)
        {
            if (!view.AreGraphicsOverridesAllowed()) return null;
            IList<ElementId> ids;
            try { ids = view.GetOrderedFilters(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
            return ids.Select(id =>
            {
                var f = doc.GetElement(id);
                return (object)new
                {
                    id = id.GetValue(),
                    name = f?.Name,
                    kind = f is SelectionFilterElement ? "selection" : "rule",
                    visible = view.GetFilterVisibility(id),
                    enabled = view.GetIsFilterEnabled(id)
                };
            }).ToList();
        }

        private static object ViewRange(Document doc, ViewPlan plan)
        {
            PlanViewRange range;
            try { range = plan.GetViewRange(); }
            catch { return null; }
            if (range == null) return null;
            object Plane(PlanViewPlane p)
            {
                var levelId = range.GetLevelId(p);
                var offset = range.GetOffset(p);
                string levelName = null;
                double? elevation = null;
                if (levelId == PlanViewRange.Unlimited) levelName = "unlimited";
                else if (levelId == PlanViewRange.LevelAbove) levelName = "level above";
                else if (levelId == PlanViewRange.LevelBelow) levelName = "level below";
                else if (levelId == PlanViewRange.Current) levelName = "associated level";
                else if (doc.GetElement(levelId) is Level l) { levelName = l.Name; elevation = Mm.FromFt(l.Elevation + offset); }
                return new { level = levelName, offsetMm = Mm.FromFt(offset), elevationMm = elevation };
            }
            return new
            {
                top = Plane(PlanViewPlane.TopClipPlane),
                cut = Plane(PlanViewPlane.CutPlane),
                bottom = Plane(PlanViewPlane.BottomClipPlane),
                depth = Plane(PlanViewPlane.ViewDepthPlane)
            };
        }

        private static object Underlay(Document doc, ViewPlan plan)
        {
            try
            {
                var baseId = plan.GetUnderlayBaseLevel();
                var topId = plan.GetUnderlayTopLevel();
                return new
                {
                    baseLevel = (doc.GetElement(baseId) as Level)?.Name,
                    topLevel = (doc.GetElement(topId) as Level)?.Name,
                    orientation = plan.GetUnderlayOrientation().ToString()
                };
            }
            catch { return null; }
        }

        private static string Str(Element e, BuiltInParameter bip)
        {
            var p = e.get_Parameter(bip);
            if (p == null || !p.HasValue) return null;
            var s = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static string ValueText(Element e, BuiltInParameter bip)
        {
            var p = e.get_Parameter(bip);
            return p == null || !p.HasValue ? null : p.AsValueString();
        }

        private static bool? Bool(Element e, BuiltInParameter bip)
        {
            var p = e.get_Parameter(bip);
            return p == null || !p.HasValue || p.StorageType != StorageType.Integer ? (bool?)null : p.AsInteger() != 0;
        }

        private static string ElementName(Document doc, Element e, BuiltInParameter bip)
        {
            var p = e.get_Parameter(bip);
            if (p == null || p.StorageType != StorageType.ElementId) return null;
            var id = p.AsElementId();
            return id == ElementId.InvalidElementId ? null : doc.GetElement(id)?.Name;
        }

        private static int? SafeCount(Func<int> f) { try { return f(); } catch { return null; } }
        private static bool? SafeBool(Func<bool> f) { try { return f(); } catch { return null; } }
        private static List<long> SafeList(Func<List<long>> f) { try { return f(); } catch { return null; } }
    }
}
