using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing.M1Support
{
    /// <summary>Small element helpers shared by the wave-M1 editing tools (arrays, groups, deletion, geometry).</summary>
    public static class M1Elements
    {
        public static string SafeName(Element e)
        {
            try { return e.Name; }
            catch { return null; }
        }

        public static string TypeName(Element e)
        {
            try
            {
                var typeId = e.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId) return null;
                return e.Document.GetElement(typeId) is ElementType t ? LookupService.FullName(t) : null;
            }
            catch { return null; }
        }

        /// <summary>id, category, name and type of an element (for previews and reports).</summary>
        public static object Describe(Element e) => new
        {
            id = e.Id.GetValue(),
            category = e.Category?.Name ?? e.GetType().Name,
            name = SafeName(e),
            type = TypeName(e)
        };

        public static List<long> Ids(IEnumerable<ElementId> ids) => ids.Select(i => i.GetValue()).ToList();

        /// <summary>Fails with ELEMENT_PINNED when any element is pinned.</summary>
        public static void RefusePinned(IEnumerable<Element> elements, string action)
        {
            var pinned = elements.Where(e => e.Pinned).Select(e => e.Id.GetValue()).ToList();
            if (pinned.Count > 0)
                throw new ToolException("ELEMENT_PINNED",
                    $"{pinned.Count} element(s) are pinned, so they were not {action}: {string.Join(", ", pinned.Take(20))}.",
                    "Unpin them with set_elements_pinned first.", "set_elements_pinned");
        }

        /// <summary>Fails with ELEMENT_IN_GROUP when any element is a member of a group (edit the group instead).</summary>
        public static void RefuseGroupMembers(IEnumerable<Element> elements, string action)
        {
            var inGroup = elements.Where(e => e.GroupId != null && e.GroupId != ElementId.InvalidElementId)
                .Select(e => e.Id.GetValue()).ToList();
            if (inGroup.Count > 0)
                throw new ToolException("ELEMENT_IN_GROUP",
                    $"{inGroup.Count} element(s) are members of a group, so they can't be {action} on their own: {string.Join(", ", inGroup.Take(20))}.",
                    "Use the group instance id instead, or ungroup it first with manage_groups.", "manage_groups");
        }

        /// <summary>True when the element is drawn in one view only (detail lines, tags, detail groups...).</summary>
        public static bool IsViewSpecific(Element e) => e.OwnerViewId != null && e.OwnerViewId != ElementId.InvalidElementId;

        /// <summary>The element's bounding box in model coordinates, or null.</summary>
        public static BoundingBoxXYZ Box(Element e)
        {
            var view = IsViewSpecific(e) ? e.Document.GetElement(e.OwnerViewId) as View : null;
            return e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
        }

        public static bool BoxesOverlap(BoundingBoxXYZ a, BoundingBoxXYZ b, double tolerance)
        {
            if (a == null || b == null) return false;
            return a.Min.X <= b.Max.X + tolerance && b.Min.X <= a.Max.X + tolerance &&
                   a.Min.Y <= b.Max.Y + tolerance && b.Min.Y <= a.Max.Y + tolerance &&
                   a.Min.Z <= b.Max.Z + tolerance && b.Min.Z <= a.Max.Z + tolerance;
        }

        /// <summary>The active view of <paramref name="doc"/> when it is the active document, else null.</summary>
        public static ElementId ActiveViewId(ToolContext ctx)
        {
            try
            {
                var uidoc = ctx.UiApp?.ActiveUIDocument;
                if (uidoc == null || !uidoc.Document.Equals(ctx.Doc)) return null;
                return uidoc.ActiveView?.Id;
            }
            catch { return null; }
        }
    }
}
