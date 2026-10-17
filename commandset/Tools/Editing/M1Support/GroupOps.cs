using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing.M1Support
{
    /// <summary>Group kinds and lookups shared by manage_groups, list_groups and place_group_instance.</summary>
    public static class GroupOps
    {
        public static string Kind(GroupType t)
        {
            var cat = t?.Category?.Id;
            if (cat == null) return "model";
            if (cat == new ElementId(BuiltInCategory.OST_IOSAttachedDetailGroups)) return "attached";
            if (cat == new ElementId(BuiltInCategory.OST_IOSDetailGroups)) return "detail";
            return "model";
        }

        public static Group Group(Document doc, long id)
        {
            var e = doc.GetElement(id.ToRevitElementId());
            if (e == null)
                throw new ToolException("ELEMENT_NOT_FOUND", $"Group {id} does not exist.", "List the groups with list_groups.", "list_groups");
            if (e is Group g) return g;
            if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId)
                throw new ToolException("NOT_A_GROUP", $"Element {id} is a member of group {e.GroupId.GetValue()}, not a group.",
                    $"Use groupId {e.GroupId.GetValue()}.", "list_groups");
            throw new ToolException("NOT_A_GROUP", $"Element {id} ({e.Category?.Name ?? e.GetType().Name}) is not a group.", null, "list_groups");
        }

        public static int InstanceCount(GroupType t)
        {
            try { return t.Groups.Size; }
            catch { return 0; }
        }

        /// <summary>Location of the group's origin, in mm.</summary>
        public static object Origin(Group g) => (g.Location as LocationPoint) != null
            ? EditGeometry.Point(((LocationPoint)g.Location).Point)
            : null;

        public static object Describe(Group g)
        {
            var doc = g.Document;
            var level = doc.GetElement(g.LevelId) as Level;
            return new
            {
                id = g.Id.GetValue(),
                groupType = g.GroupType?.Name,
                groupTypeId = g.GroupType?.Id.GetValue(),
                kind = Kind(g.GroupType),
                level = level?.Name,
                ownerViewId = M1Elements.IsViewSpecific(g) ? g.OwnerViewId.GetValue() : (long?)null,
                origin = Origin(g),
                memberCount = g.GetMemberIds().Count,
                parentGroupId = g.GroupId != null && g.GroupId != ElementId.InvalidElementId ? g.GroupId.GetValue() : (long?)null
            };
        }

        /// <summary>Elements Revit accepts in a new group: not element types, views or groups' own members.</summary>
        public static void CheckGroupable(IList<Element> elements)
        {
            var bad = elements.Where(e => e is ElementType || e is View || e is Family).Select(e => e.Id.GetValue()).ToList();
            if (bad.Count > 0)
                throw new ToolException("NOT_GROUPABLE", $"Types, families and views can't be grouped: {string.Join(", ", bad.Take(20))}.");
            M1Elements.RefuseGroupMembers(elements, "grouped");
            var viewSpecific = elements.Where(M1Elements.IsViewSpecific).ToList();
            if (viewSpecific.Count > 0 && viewSpecific.Count < elements.Count)
                throw new ToolException("MIXED_GROUP_ELEMENTS",
                    $"{viewSpecific.Count} of the elements are view-specific (annotation/detail) and the others are model elements; Revit groups them separately.",
                    "Make a model group with the model elements and a detail group with the view-specific ones (two calls).");
            if (viewSpecific.Select(e => e.OwnerViewId).Distinct().Count() > 1)
                throw new ToolException("MIXED_GROUP_ELEMENTS", "A detail group can only hold elements of one view.");
        }
    }
}
