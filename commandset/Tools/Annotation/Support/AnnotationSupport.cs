using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Annotation.Support
{
    /// <summary>A point in a view's plane, in mm (see <see cref="ViewPlane"/>).</summary>
    public sealed class ViewPointMm
    {
        [JsonProperty("x", Required = Required.Always)]
        [Description("Horizontal coordinate in the view plane. Plans: model X.")]
        [Unit("mm")]
        public double X { get; set; }

        [JsonProperty("y", Required = Required.Always)]
        [Description("Vertical coordinate in the view plane. Plans: model Y; sections/elevations: elevation Z.")]
        [Unit("mm")]
        public double Y { get; set; }
    }

    /// <summary>
    /// Coordinates in a view's plane: x = point · RightDirection and y = point · UpDirection (mm, project internal
    /// origin). In plans that is simply model X/Y; in sections/elevations y is the elevation (Z) and x runs along the
    /// view's horizontal direction.
    /// </summary>
    public static class ViewPlane
    {
        public static XYZ ToModel(View view, double xMm, double yMm)
        {
            var x = UnitPolicy.ToInternal(xMm, UnitSpec.Length);
            var y = UnitPolicy.ToInternal(yMm, UnitSpec.Length);
            var o = view.Origin;
            var r = view.RightDirection;
            var u = view.UpDirection;
            return o + r * (x - o.DotProduct(r)) + u * (y - o.DotProduct(u));
        }

        public static XYZ Offset(View view, double dxMm, double dyMm) =>
            view.RightDirection * UnitPolicy.ToInternal(dxMm, UnitSpec.Length) +
            view.UpDirection * UnitPolicy.ToInternal(dyMm, UnitSpec.Length);

        public static object FromModel(View view, XYZ p)
        {
            if (p == null) return null;
            return new
            {
                x = Math.Round(UnitPolicy.FromInternal(p.DotProduct(view.RightDirection), UnitSpec.Length, out _), 1),
                y = Math.Round(UnitPolicy.FromInternal(p.DotProduct(view.UpDirection), UnitSpec.Length, out _), 1)
            };
        }

        /// <summary>Views that can hold annotations (text, tags, dimensions); 3D only when locked.</summary>
        public static View RequireAnnotatable(ToolContext ctx, string viewRef, bool allowSheetsAndDrafting, string what)
        {
            var view = ctx.Services.Lookup.View(viewRef);
            if (view is ViewSchedule)
                throw new ToolException("VIEW_NOT_SUPPORTED", $"'{view.Name}' is a schedule; {what} cannot be placed on it.");
            if (view is View3D v3 && !v3.IsLocked)
                throw new ToolException("VIEW_NOT_SUPPORTED", $"'{view.Name}' is an unlocked 3D view; {what} needs a plan, section, elevation or a locked 3D view.");
            if (!allowSheetsAndDrafting && (view is ViewSheet || view.ViewType == ViewType.DraftingView || view.ViewType == ViewType.Legend))
                throw new ToolException("VIEW_NOT_SUPPORTED", $"'{view.Name}' ({view.ViewType}) shows no model elements; {what} needs a model view.");
            return view;
        }

        public static object DescribeView(View view) => new
        {
            id = view.Id.GetValue(),
            name = view.Name,
            viewType = view.ViewType.ToString(),
            scale = SafeScale(view),
            rightDirection = new { x = Math.Round(view.RightDirection.X, 6), y = Math.Round(view.RightDirection.Y, 6), z = Math.Round(view.RightDirection.Z, 6) },
            upDirection = new { x = Math.Round(view.UpDirection.X, 6), y = Math.Round(view.UpDirection.Y, 6), z = Math.Round(view.UpDirection.Z, 6) }
        };

        private static int? SafeScale(View view)
        {
            try { return view.Scale; }
            catch { return null; }
        }
    }

    /// <summary>Tag creation for any category: rooms, areas and spaces get their own tag classes.</summary>
    public static class TagSupport
    {
        /// <summary>Keys of elements already tagged in the view: "id" for local, "linkId:id" for linked.</summary>
        public static HashSet<string> TaggedKeys(Document doc, View view)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (IndependentTag tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)))
            {
                try
                {
                    foreach (var id in tag.GetTaggedLocalElementIds())
                        if (id != ElementId.InvalidElementId) keys.Add(Key(id.GetValue(), null));
                    foreach (var r in tag.GetTaggedReferences())
                        if (r.LinkedElementId != ElementId.InvalidElementId)
                            keys.Add(Key(r.LinkedElementId.GetValue(), r.ElementId.GetValue()));
                }
                catch
                {
                    // orphaned tag
                }
            }
            foreach (var e in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType())
            {
                if (!(e is RoomTag rt)) continue;
                try
                {
                    var t = rt.TaggedRoomId;
                    if (t.LinkInstanceId != ElementId.InvalidElementId)
                        keys.Add(Key(t.LinkedElementId.GetValue(), t.LinkInstanceId.GetValue()));
                    else if (rt.TaggedLocalRoomId != ElementId.InvalidElementId)
                        keys.Add(Key(rt.TaggedLocalRoomId.GetValue(), null));
                }
                catch { }
            }
            foreach (var e in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_AreaTags).WhereElementIsNotElementType())
            {
                if (e is AreaTag at && at.Area != null) keys.Add(Key(at.Area.Id.GetValue(), null));
            }
            foreach (var e in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_MEPSpaceTags).WhereElementIsNotElementType())
            {
                if (e is SpaceTag st && st.Space != null) keys.Add(Key(st.Space.Id.GetValue(), null));
            }
            return keys;
        }

        public static string Key(long elementId, long? linkInstanceId) =>
            linkInstanceId.HasValue
                ? linkInstanceId.Value.ToString(CultureInfo.InvariantCulture) + ":" + elementId.ToString(CultureInfo.InvariantCulture)
                : elementId.ToString(CultureInfo.InvariantCulture);

        /// <summary>Tag category for an element category (OST_Doors → OST_DoorTags), or null when unknown.</summary>
        public static BuiltInCategory? TagCategoryFor(Category category)
        {
            var name = CategoryService.BuiltInNameOf(category);
            if (name == null) return null;
            var candidates = new List<string> { name + "Tags" };
            if (name.EndsWith("ies", StringComparison.Ordinal)) candidates.Add(name.Substring(0, name.Length - 3) + "yTags");
            if (name.EndsWith("es", StringComparison.Ordinal)) candidates.Add(name.Substring(0, name.Length - 2) + "Tags");
            if (name.EndsWith("s", StringComparison.Ordinal)) candidates.Add(name.Substring(0, name.Length - 1) + "Tags");
            foreach (var c in candidates)
            {
                if (Enum.TryParse(c, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic)) return bic;
            }
            return null;
        }

        /// <summary>A tag type by "Family: Type" / type name / id, for tagging elements of <paramref name="elementCategory"/>.</summary>
        public static FamilySymbol ResolveTagType(ToolContext ctx, string text, Category elementCategory)
        {
            var tagCat = TagCategoryFor(elementCategory);
            if (tagCat.HasValue)
            {
                try
                {
                    return ctx.Services.Lookup.FamilySymbol(text, tagCat.Value);
                }
                catch (ToolException ex) when (ex.Code == "NOT_FOUND")
                {
                    try { return ctx.Services.Lookup.FamilySymbol(text, BuiltInCategory.OST_MultiCategoryTags); }
                    catch (ToolException) { throw ex; }
                }
            }
            var symbol = ctx.Services.Lookup.FamilySymbol(text);
            if (symbol.Category == null || symbol.Category.CategoryType != CategoryType.Annotation)
                throw new ToolException("NOT_A_TAG_TYPE", $"'{text}' is not an annotation (tag) type.");
            return symbol;
        }

        /// <summary>Point to tag at: location point, curve middle, or bounding-box centre (in host coordinates).</summary>
        public static XYZ AnchorPoint(Element e, View view, Transform linkTransform)
        {
            XYZ p = null;
            switch (e.Location)
            {
                case LocationPoint lp:
                    p = lp.Point;
                    break;
                case LocationCurve lc when lc.Curve != null:
                    p = lc.Curve.Evaluate(0.5, true);
                    break;
            }
            if (p == null)
            {
                var bb = (linkTransform == null ? e.get_BoundingBox(view) : null) ?? e.get_BoundingBox(null);
                if (bb != null) p = (bb.Min + bb.Max) / 2;
            }
            if (p == null) return null;
            return linkTransform == null ? p : linkTransform.OfPoint(p);
        }

        /// <summary>Creates one tag. Throws <see cref="ToolException"/> with a stable code on expected failures.</summary>
        public static Element CreateTag(Document doc, View view, Element element, RevitLinkInstance link, FamilySymbol tagType,
            bool leader, TagOrientation orientation, XYZ point)
        {
            if (tagType != null && !tagType.IsActive) tagType.Activate();
            try
            {
                if (element is Room room)
                {
                    if (!(view is ViewPlan) && !(view is ViewSection))
                        throw new ToolException("VIEW_NOT_SUPPORTED", "Rooms can only be tagged in plan or section views.");
                    var linkId = link == null ? new LinkElementId(room.Id) : new LinkElementId(link.Id, room.Id);
                    var tag = doc.Create.NewRoomTag(linkId, new UV(point.X, point.Y), view.Id);
                    if (tag == null) throw new ToolException("TAG_FAILED", "Revit did not create the room tag.");
                    if (tagType != null) tag.ChangeTypeId(tagType.Id);
                    tag.HasLeader = leader;
                    return tag;
                }
                if (element is Area area)
                {
                    if (link != null) throw new ToolException("LINK_NOT_SUPPORTED", "Areas inside links cannot be tagged.");
                    if (!(view is ViewPlan plan) || plan.ViewType != ViewType.AreaPlan)
                        throw new ToolException("VIEW_NOT_SUPPORTED", "Areas can only be tagged in area plans.");
                    var tag = doc.Create.NewAreaTag(plan, area, new UV(point.X, point.Y));
                    if (tag == null) throw new ToolException("TAG_FAILED", "Revit did not create the area tag.");
                    if (tagType != null) tag.ChangeTypeId(tagType.Id);
                    tag.HasLeader = leader;
                    return tag;
                }
                if (element is Space space)
                {
                    if (link != null) throw new ToolException("LINK_NOT_SUPPORTED", "Spaces inside links cannot be tagged.");
                    var tag = doc.Create.NewSpaceTag(space, new UV(point.X, point.Y), view);
                    if (tag == null) throw new ToolException("TAG_FAILED", "Revit did not create the space tag.");
                    if (tagType != null) tag.ChangeTypeId(tagType.Id);
                    tag.HasLeader = leader;
                    return tag;
                }

                var reference = new Reference(element);
                if (link != null) reference = reference.CreateLinkReference(link);
                var created = tagType != null
                    ? IndependentTag.Create(doc, tagType.Id, view.Id, reference, leader, orientation, point)
                    : IndependentTag.Create(doc, view.Id, reference, leader, TagMode.TM_ADDBY_CATEGORY, orientation, point);
                if (created == null) throw new ToolException("TAG_FAILED", "Revit did not create the tag.");
                return created;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("TAG_FAILED", ex.Message,
                    "Check that a tag family for this category is loaded (or pass tagType) and that the element is visible in the view.");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                throw new ToolException("TAG_FAILED", ex.Message,
                    "Load a tag family for this category, or pass tagType.");
            }
        }

        public static TagOrientation Orientation(string text)
        {
            switch ((text ?? "horizontal").Trim().ToLowerInvariant())
            {
                case "vertical": return TagOrientation.Vertical;
                case "model": return TagOrientation.AnyModelDirection;
                default: return TagOrientation.Horizontal;
            }
        }
    }
}
