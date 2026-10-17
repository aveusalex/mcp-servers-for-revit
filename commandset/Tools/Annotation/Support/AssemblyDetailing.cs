using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Annotation.Support
{
    /// <summary>A view of an assembly that was skipped, and why.</summary>
    public sealed class SkippedView
    {
        public long id { get; set; }
        public string name { get; set; }
        public string reason { get; set; }
    }

    /// <summary>Shared lookups for detailing assembly views (dimension_assembly, tag_assembly).</summary>
    public static class AssemblyDetailing
    {
        /// <summary>The assembly by id or (partial) name; with neither, the only assembly in the model.</summary>
        public static AssemblyInstance Resolve(Document doc, long? id, string name)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)).Cast<AssemblyInstance>().ToList();
            if (id.HasValue)
            {
                return doc.GetElement(id.Value.ToRevitElementId()) as AssemblyInstance
                       ?? throw new ToolException("NOT_FOUND", $"{id} is not an assembly.", "List them with find_elements (category Assemblies / Montagens).");
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                var n = name.Trim();
                var exact = all.Where(a => string.Equals(a.AssemblyTypeName, n, StringComparison.OrdinalIgnoreCase)).ToList();
                var hits = exact.Count > 0 ? exact
                    : all.Where(a => (a.AssemblyTypeName ?? string.Empty).IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (hits.Count == 1) return hits[0];
                if (all.Count == 0)
                    throw new ToolException("NOT_FOUND", "The model has no assemblies.", "Create one with manage_assembly.", "manage_assembly");
                throw new ToolException(hits.Count == 0 ? "NOT_FOUND" : "AMBIGUOUS_NAME",
                    $"{hits.Count} assemblies match '{n}'. Available: {string.Join(", ", all.Select(a => $"'{a.AssemblyTypeName}' ({a.Id.GetValue()})"))}.",
                    "Pass assemblyId.");
            }
            if (all.Count == 1) return all[0];
            throw new ToolException(all.Count == 0 ? "NOT_FOUND" : "AMBIGUOUS_TARGET",
                all.Count == 0 ? "The model has no assemblies." : $"The model has {all.Count} assemblies; say which one.",
                all.Count == 0 ? "Create one with manage_assembly." : "Pass assemblyId or assemblyName.",
                all.Count == 0 ? "manage_assembly" : null);
        }

        /// <summary>
        /// Views to detail: the given ones, or every view of the assembly. Schedules, sheets and (unless
        /// <paramref name="allowLocked3D"/>) 3D views are skipped with a reason.
        /// </summary>
        public static List<View> Views(ToolContext ctx, AssemblyInstance asm, IList<string> refs, bool allowLocked3D, List<SkippedView> skipped)
        {
            var doc = ctx.Doc;
            var candidates = refs != null && refs.Count > 0
                ? refs.Select(r => ctx.Services.Lookup.View(r)).ToList()
                : new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && v.AssociatedAssemblyInstanceId == asm.Id).OrderBy(v => v.Id.GetValue()).ToList();
            var result = new List<View>();
            foreach (var v in candidates)
            {
                string reason = null;
                if (v is ViewSchedule || v is ViewSheet) reason = "schedule or sheet";
                else if (v is View3D v3)
                {
                    if (!allowLocked3D) reason = "3D view (dimensions are not placed in 3D)";
                    else if (!v3.IsLocked) reason = "3D view not locked (use lock_3d_view first)";
                }
                if (reason == null) result.Add(v);
                else skipped.Add(new SkippedView { id = v.Id.GetValue(), name = v.Name, reason = reason });
            }
            if (result.Count == 0)
                throw new ToolException("NO_VIEWS", $"Assembly '{asm.AssemblyTypeName}' has no view that can be annotated." +
                                                    (skipped.Count > 0 ? " Skipped: " + string.Join("; ", skipped.Select(s => $"'{s.name}' ({s.reason})")) : string.Empty),
                    "Create its views with create_assembly_views.", "create_assembly_views");
            return result;
        }

        public static List<Element> Members(Document doc, AssemblyInstance asm) =>
            asm.GetMemberIds().Select(doc.GetElement).Where(e => e != null).ToList();

        /// <summary>Extent of the elements in the view's right/up axes (feet).</summary>
        public static (double minR, double maxR, double minU, double maxU) Extents(View view, IEnumerable<Element> elements)
        {
            double minR = double.MaxValue, maxR = double.MinValue, minU = double.MaxValue, maxU = double.MinValue;
            foreach (var e in elements)
            {
                var bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                foreach (var c in Corners(bb))
                {
                    double r = c.DotProduct(view.RightDirection), u = c.DotProduct(view.UpDirection);
                    minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
                    minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                }
            }
            if (minR == double.MaxValue) throw new ToolException("NO_GEOMETRY", "The assembly's members have no geometry.");
            return (minR, maxR, minU, maxU);
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ bb)
        {
            var t = bb.Transform ?? Transform.Identity;
            foreach (var x in new[] { bb.Min.X, bb.Max.X })
            foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
            foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                yield return t.OfPoint(new XYZ(x, y, z));
        }

        /// <summary>A point given in view axes (right, up), on the view's plane.</summary>
        public static XYZ InView(View view, double right, double up) =>
            view.RightDirection * right + view.UpDirection * up + view.ViewDirection * view.Origin.DotProduct(view.ViewDirection);

        /// <summary>Model length (feet) of a paper distance in mm at the view's scale.</summary>
        public static double PaperToModel(View view, double paperMm)
        {
            int scale;
            try { scale = Math.Max(1, view.Scale); }
            catch { scale = 1; }
            return paperMm * scale / 304.8;
        }

        public static double Mm(double ft) => Math.Round(ft * 304.8, 1);
    }
}
