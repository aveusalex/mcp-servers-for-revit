using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Views.Support
{
    /// <summary>A point in model plan coordinates (project internal origin), in mm.</summary>
    public sealed class PointXY
    {
        [JsonProperty("x", Required = Required.Always)]
        [Description("X coordinate in mm (project internal origin).")]
        [Unit("mm")]
        public double X { get; set; }

        [JsonProperty("y", Required = Required.Always)]
        [Description("Y coordinate in mm (project internal origin).")]
        [Unit("mm")]
        public double Y { get; set; }
    }

    /// <summary>An axis-aligned rectangle in model plan coordinates, in mm.</summary>
    public sealed class RectXY
    {
        [JsonProperty("min", Required = Required.Always)]
        [Description("Lower-left corner in model coordinates (mm).")]
        public PointXY Min { get; set; }

        [JsonProperty("max", Required = Required.Always)]
        [Description("Upper-right corner in model coordinates (mm).")]
        public PointXY Max { get; set; }
    }

    /// <summary>mm (wire) ↔ feet (Revit internal).</summary>
    internal static class Mm
    {
        public const double PerFoot = 304.8;
        public static double ToFt(double mm) => mm / PerFoot;
        public static double FromFt(double ft) => Math.Round(ft * PerFoot, 2);
        public static object Point(XYZ p) => p == null ? null : new { x = FromFt(p.X), y = FromFt(p.Y), z = FromFt(p.Z) };
        public static object Point2(XYZ p) => p == null ? null : new { x = FromFt(p.X), y = FromFt(p.Y) };
    }

    /// <summary>A rectangle expressed in a view's own coordinates (u along BasisX, v along BasisY, w along BasisZ).</summary>
    internal struct ViewRect
    {
        public double MinU, MinV, MaxU, MaxV, MinW, MaxW;
        public bool IsEmpty => MaxU <= MinU || MaxV <= MinV;
    }

    /// <summary>Shared helpers of the views tools (resolution, naming, common properties, crop geometry).</summary>
    internal static class ViewSupport
    {
        // ---- view family types ------------------------------------------------------------------------------------

        public static ViewFamilyType FamilyType(ToolContext ctx, ViewFamily family, string name)
        {
            var types = new FilteredElementCollector(ctx.Doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == family).ToList();
            if (types.Count == 0)
                throw new ToolException("VIEW_TYPE_NOT_FOUND", $"The document has no view type of family {family}.",
                    "Load or create a view type of that family in Revit first.");
            if (!string.IsNullOrWhiteSpace(name))
                return ctx.Services.Lookup.ByNameOrId(name, types, $"{family} view type");

            var group = DefaultGroup(family);
            if (group.HasValue)
            {
                try
                {
                    var id = ctx.Doc.GetDefaultElementTypeId(group.Value);
                    var def = types.FirstOrDefault(t => t.Id == id);
                    if (def != null) return def;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // no default for this group
                }
            }
            return types.OrderBy(t => t.Id.GetValue()).First();
        }

        private static ElementTypeGroup? DefaultGroup(ViewFamily family)
        {
            switch (family)
            {
                case ViewFamily.FloorPlan: return ElementTypeGroup.ViewTypeFloorPlan;
                case ViewFamily.CeilingPlan: return ElementTypeGroup.ViewTypeCeilingPlan;
                case ViewFamily.StructuralPlan: return ElementTypeGroup.ViewTypeStructuralPlan;
                case ViewFamily.ThreeDimensional: return ElementTypeGroup.ViewType3D;
                case ViewFamily.Drafting: return ElementTypeGroup.ViewTypeDrafting;
                case ViewFamily.Section: return ElementTypeGroup.ViewTypeSection;
                case ViewFamily.Elevation: return ElementTypeGroup.ViewTypeElevation;
                case ViewFamily.Detail: return ElementTypeGroup.ViewTypeDetailView;
                default: return null;
            }
        }

        // ---- enums ------------------------------------------------------------------------------------------------

        public static readonly string[] DetailLevels = { "coarse", "medium", "fine" };

        public static ViewDetailLevel ParseDetailLevel(string text)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "coarse": return ViewDetailLevel.Coarse;
                case "medium": return ViewDetailLevel.Medium;
                case "fine": return ViewDetailLevel.Fine;
                default:
                    throw new ToolException("INVALID_ARGUMENTS", $"detailLevel must be coarse, medium or fine (got '{text}').");
            }
        }

        public static string DetailLevelName(ViewDetailLevel level) => level.ToString().ToLowerInvariant();

        // ---- lookup -----------------------------------------------------------------------------------------------

        /// <summary>A view or a view template (filters, visibility and templates apply to both).</summary>
        public static View ViewOrTemplate(ToolContext ctx, string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new ToolException("INVALID_ARGUMENTS", "A view name or id is required.");
            var all = new FilteredElementCollector(ctx.Doc).OfClass(typeof(View)).Cast<View>().ToList();
            return ctx.Services.Lookup.ByNameOrId(reference, all, "view or view template",
                v => (v.IsTemplate ? "template " : string.Empty) + v.ViewType);
        }

        public static Element ScopeBox(ToolContext ctx, string reference)
        {
            var boxes = new FilteredElementCollector(ctx.Doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                .WhereElementIsNotElementType().ToElements().ToList();
            return ctx.Services.Lookup.ByNameOrId(reference, boxes, "scope box");
        }

        public static bool IsNone(string text) =>
            string.Equals(text?.Trim(), "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text?.Trim(), "nenhum", StringComparison.OrdinalIgnoreCase);

        // ---- description ------------------------------------------------------------------------------------------

        public static int? SafeScale(View v)
        {
            try { return v.Scale; }
            catch { return null; }
        }

        public static object Describe(View v)
        {
            var doc = v.Document;
            var template = v.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(v.ViewTemplateId) as View : null;
            string detail = null;
            try { detail = v.ViewType == ViewType.Schedule ? null : DetailLevelName(v.DetailLevel); }
            catch { /* views without detail level */ }
            var primary = v.GetPrimaryViewId();
            return new
            {
                viewId = v.Id.GetValue(),
                uniqueId = v.UniqueId,
                name = v.Name,
                viewType = v.ViewType.ToString(),
                isTemplate = v.IsTemplate,
                scale = SafeScale(v),
                detailLevel = detail,
                level = (v as ViewPlan)?.GenLevel?.Name,
                template = template?.Name,
                templateId = template?.Id.GetValue(),
                primaryViewId = primary != ElementId.InvalidElementId ? primary.GetValue() : (long?)null
            };
        }

        // ---- names ------------------------------------------------------------------------------------------------

        /// <summary>
        /// A free view name for generated names: <paramref name="desired"/> or "desired (2)", "(3)"... Names given
        /// explicitly by the caller must use <see cref="NamesService.EnsureViewNameFree"/> instead (collision = error).
        /// </summary>
        public static string FreeViewName(Document doc, string desired, ViewType type, ISet<string> reserved = null)
        {
            NameRules.Validate(desired, "view name");
            var used = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType == type).Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
            if (reserved != null) used.UnionWith(reserved);
            var name = desired;
            for (var n = 2; used.Contains(name); n++) name = $"{desired} ({n})";
            reserved?.Add(name);
            return name;
        }

        /// <summary>Replaces {token} placeholders, then removes characters Revit forbids in names.</summary>
        public static string Expand(string pattern, IDictionary<string, string> tokens)
        {
            var text = pattern ?? string.Empty;
            foreach (var kv in tokens)
                text = text.Replace("{" + kv.Key + "}", kv.Value ?? string.Empty);
            foreach (var c in NameRules.IllegalChars) text = text.Replace(c.ToString(), string.Empty);
            return text.Trim();
        }

        // ---- common properties ------------------------------------------------------------------------------------

        /// <summary>
        /// Sets scale and detail level (before the template, which may lock them), then the template, scope box and
        /// name; each value is read back.
        /// </summary>
        public static void ApplyCommon(ToolContext ctx, View view, string name, int? scale, string detailLevel,
            string template, string scopeBox, bool nameIsGenerated = false)
        {
            if (scale.HasValue) SetScale(view, scale.Value);
            if (!string.IsNullOrWhiteSpace(detailLevel)) SetDetailLevel(view, ParseDetailLevel(detailLevel));
            if (!string.IsNullOrWhiteSpace(scopeBox)) SetScopeBox(ctx, view, scopeBox);
            if (!string.IsNullOrWhiteSpace(template)) AssignTemplate(view, ctx.Services.Lookup.ViewTemplate(template));
            if (!string.IsNullOrWhiteSpace(name))
            {
                var finalName = nameIsGenerated ? FreeViewName(ctx.Doc, name, view.ViewType) : name;
                if (!nameIsGenerated) ctx.Services.Names.EnsureViewNameFree(name, view.ViewType, false, view.Id);
                ctx.Services.Names.ApplyName(view, finalName, "view name");
            }
        }

        public static void SetScale(View view, int scale)
        {
            if (scale < 1 || scale > 24000)
                throw new ToolException("INVALID_ARGUMENTS", $"scale must be between 1 and 24000 (got {scale}).");
            if (view.Scale == scale) return;
            try
            {
                view.Scale = scale;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("SCALE_NOT_APPLIED", $"Revit refused scale 1:{scale} on '{view.Name}': {ex.Message}",
                    "If a view template controls the scale, change the template or remove it first.");
            }
            if (view.Scale != scale)
                throw new ToolException("SCALE_NOT_APPLIED", $"'{view.Name}' kept scale 1:{view.Scale} instead of 1:{scale}.",
                    "A view template probably controls the scale.");
        }

        public static void SetDetailLevel(View view, ViewDetailLevel level)
        {
            if (view.DetailLevel == level) return;
            try
            {
                view.DetailLevel = level;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("DETAIL_LEVEL_NOT_APPLIED", $"Revit refused detail level {level} on '{view.Name}': {ex.Message}",
                    "If a view template controls the detail level, change the template instead.");
            }
            if (view.DetailLevel != level)
                throw new ToolException("DETAIL_LEVEL_NOT_APPLIED", $"'{view.Name}' kept detail level {view.DetailLevel}.",
                    "A view template probably controls the detail level.");
        }

        public static void AssignTemplate(View view, View template)
        {
            if (!view.IsValidViewTemplate(template.Id))
                throw new ToolException("TEMPLATE_INCOMPATIBLE",
                    $"Template '{template.Name}' ({template.ViewType}) cannot be assigned to '{view.Name}' ({view.ViewType}).",
                    "Use list_view_templates with the view's type to find compatible templates.", "list_view_templates");
            view.ViewTemplateId = template.Id;
            if (view.ViewTemplateId != template.Id)
                throw new ToolException("TEMPLATE_NOT_APPLIED", $"Revit did not assign template '{template.Name}' to '{view.Name}'.");
        }

        public static void SetScopeBox(ToolContext ctx, View view, string scopeBox)
        {
            var p = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
            if (p == null)
                throw new ToolException("SCOPE_BOX_NOT_SUPPORTED", $"'{view.Name}' ({view.ViewType}) does not accept a scope box.");
            var id = IsNone(scopeBox) ? ElementId.InvalidElementId : ScopeBox(ctx, scopeBox).Id;
            if (p.IsReadOnly)
                throw new ToolException("SCOPE_BOX_NOT_APPLIED", $"The scope box of '{view.Name}' is read-only here.",
                    "A view template or the primary view (for dependent views) may control it.");
            p.Set(id);
            if (p.AsElementId() != id)
                throw new ToolException("SCOPE_BOX_NOT_APPLIED", $"Revit did not apply the scope box to '{view.Name}'.");
        }

        // ---- geometry ---------------------------------------------------------------------------------------------

        /// <summary>The 8 corners of a box, in model coordinates (its Transform applied).</summary>
        public static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            var t = box.Transform ?? Transform.Identity;
            var a = box.Min;
            var b = box.Max;
            foreach (var x in new[] { a.X, b.X })
            foreach (var y in new[] { a.Y, b.Y })
            foreach (var z in new[] { a.Z, b.Z })
                yield return t.OfPoint(new XYZ(x, y, z));
        }

        /// <summary>Model points expressed in the coordinates of <paramref name="viewTransform"/> (the crop box transform).</summary>
        public static ViewRect Project(Transform viewTransform, IEnumerable<XYZ> modelPoints)
        {
            var inv = viewTransform.Inverse;
            var r = new ViewRect
            {
                MinU = double.MaxValue, MinV = double.MaxValue, MinW = double.MaxValue,
                MaxU = double.MinValue, MaxV = double.MinValue, MaxW = double.MinValue
            };
            foreach (var p in modelPoints)
            {
                var q = inv.OfPoint(p);
                r.MinU = Math.Min(r.MinU, q.X); r.MaxU = Math.Max(r.MaxU, q.X);
                r.MinV = Math.Min(r.MinV, q.Y); r.MaxV = Math.Max(r.MaxV, q.Y);
                r.MinW = Math.Min(r.MinW, q.Z); r.MaxW = Math.Max(r.MaxW, q.Z);
            }
            return r;
        }

        /// <summary>Union of the model bounding boxes of elements of the target document. Throws when none has one.</summary>
        public static List<XYZ> ElementCorners(ToolContext ctx, IEnumerable<string> references)
        {
            var points = new List<XYZ>();
            foreach (var reference in references ?? Enumerable.Empty<string>())
            {
                var e = ctx.Services.Lookup.Element(reference);
                var bb = e.get_BoundingBox(null);
                if (bb == null)
                {
                    ctx.Warnings.Add(new ToolWarning($"Element {reference} has no model bounding box and was ignored.",
                        new List<long> { e.Id.GetValue() }));
                    continue;
                }
                points.AddRange(Corners(bb));
            }
            if (points.Count == 0)
                throw new ToolException("NO_GEOMETRY", "None of the elements has a bounding box to fit.");
            return points;
        }

        /// <summary>Sets the crop region of a view to cover the given model points (+ padding, ft), keeping its depth.</summary>
        public static void CropToPoints(View view, IEnumerable<XYZ> modelPoints, double paddingFt)
        {
            var cb = view.CropBox;
            var r = Project(cb.Transform, modelPoints);
            var box = new BoundingBoxXYZ
            {
                Transform = cb.Transform,
                Min = new XYZ(r.MinU - paddingFt, r.MinV - paddingFt, cb.Min.Z),
                Max = new XYZ(r.MaxU + paddingFt, r.MaxV + paddingFt, cb.Max.Z)
            };
            if (box.Max.X - box.Min.X < 1e-3 || box.Max.Y - box.Min.Y < 1e-3)
                throw new ToolException("INVALID_CROP", $"The crop region for '{view.Name}' would be empty.");
            if (!view.CropBoxActive) view.CropBoxActive = true;
            view.CropBox = box;
        }

        /// <summary>The current crop region, as model-coordinate corners and as width/height in mm.</summary>
        public static object DescribeCrop(View view)
        {
            BoundingBoxXYZ cb;
            try { cb = view.CropBox; }
            catch { return null; }
            if (cb == null) return null;
            var t = cb.Transform ?? Transform.Identity;
            var corners = new[] { t.OfPoint(new XYZ(cb.Min.X, cb.Min.Y, cb.Min.Z)), t.OfPoint(new XYZ(cb.Max.X, cb.Max.Y, cb.Min.Z)) };
            return new
            {
                active = view.CropBoxActive,
                visible = view.CropBoxVisible,
                width = Mm.FromFt(cb.Max.X - cb.Min.X),
                height = Mm.FromFt(cb.Max.Y - cb.Min.Y),
                min = Mm.Point(corners[0]),
                max = Mm.Point(corners[1])
            };
        }

        public static string Inv(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
