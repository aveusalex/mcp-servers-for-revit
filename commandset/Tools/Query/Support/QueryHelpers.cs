using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query.Support
{
    /// <summary>Small read helpers shared by the query and selection tools (wave W1). Revit-dependent, no state.</summary>
    public static class QueryHelpers
    {
        // ---- units ----------------------------------------------------------------------------------------------

        public static double Mm(double feet) => Math.Round(UnitPolicy.FromInternal(feet, UnitSpec.Length, out _), 2);
        public static double M2(double sqFeet) => Math.Round(UnitPolicy.FromInternal(sqFeet, UnitSpec.Area, out _), 4);
        public static double M3(double cuFeet) => Math.Round(UnitPolicy.FromInternal(cuFeet, UnitSpec.Volume, out _), 4);
        public static double Deg(double radians) => Math.Round(radians * 180.0 / Math.PI, 4);
        public static double FeetFromMm(double mm) => UnitPolicy.ToInternal(mm, UnitSpec.Length);

        public static object Point(XYZ p) => p == null ? null : new { x = Mm(p.X), y = Mm(p.Y), z = Mm(p.Z) };

        public static object Vector(XYZ v) =>
            v == null ? null : new { x = Math.Round(v.X, 6), y = Math.Round(v.Y, 6), z = Math.Round(v.Z, 6) };

        public static object Box(BoundingBoxXYZ bb, Transform transform = null)
        {
            if (bb == null) return null;
            var t = bb.Transform ?? Transform.Identity;
            if (transform != null) t = transform.Multiply(t);
            if (t.IsIdentity) return new { min = Point(bb.Min), max = Point(bb.Max) };
            // Transform the 8 corners and take the axis-aligned box around them.
            var corners = new List<XYZ>();
            foreach (var x in new[] { bb.Min.X, bb.Max.X })
            foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
            foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                corners.Add(t.OfPoint(new XYZ(x, y, z)));
            var min = new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z));
            var max = new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z));
            return new { min = Point(min), max = Point(max) };
        }

        // ---- elements -------------------------------------------------------------------------------------------

        public static string SafeName(Element e)
        {
            if (e == null) return null;
            try { return e.Name; }
            catch { return null; }
        }

        public static long? IdOrNull(ElementId id) =>
            id == null || id == ElementId.InvalidElementId ? (long?)null : id.GetValue();

        /// <summary>{id, name} of a referenced element, or null when the id is invalid.</summary>
        public static object Ref(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return null;
            var e = doc.GetElement(id);
            return new { id = id.GetValue(), name = SafeName(e), category = e?.Category?.Name };
        }

        /// <summary>A compact description shared by the list outputs.</summary>
        public static Dictionary<string, object> Brief(Document doc, Element e)
        {
            var type = e is ElementType ? null : ParameterService.TypeOf(e);
            var d = new Dictionary<string, object>
            {
                ["id"] = e.Id.GetValue(),
                ["uniqueId"] = e.UniqueId,
                ["name"] = SafeName(e),
                ["category"] = e.Category?.Name,
                ["categoryBuiltIn"] = CategoryService.BuiltInNameOf(e.Category),
                ["familyName"] = type == null ? null : LookupService.SafeFamilyName(type),
                ["typeName"] = type?.Name,
            };
            var levelId = LevelIdOf(e);
            d["level"] = levelId == null ? null : SafeName(doc.GetElement(levelId));
            return d;
        }

        public static ElementId LevelIdOf(Element e)
        {
            var id = e.LevelId;
            if (id != null && id != ElementId.InvalidElementId) return id;
            // Some elements (beams, columns...) keep the level in a parameter.
            foreach (var bip in new[] { BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.FAMILY_LEVEL_PARAM,
                         BuiltInParameter.SCHEDULE_LEVEL_PARAM })
            {
                try
                {
                    var p = e.get_Parameter(bip);
                    var v = p?.AsElementId();
                    if (v != null && v != ElementId.InvalidElementId) return v;
                }
                catch
                {
                    // parameter not applicable
                }
            }
            return null;
        }

        /// <summary>Location: point (+ rotation) or curve (start/end/length), in mm and degrees.</summary>
        public static object Location(Element e, Transform transform = null)
        {
            try
            {
                switch (e.Location)
                {
                    case LocationPoint lp:
                    {
                        double? rotation = null;
                        try { rotation = Deg(lp.Rotation); }
                        catch { /* not every point location has a rotation */ }
                        var p = transform == null ? lp.Point : transform.OfPoint(lp.Point);
                        return new { kind = "point", point = Point(p), rotationDeg = rotation };
                    }
                    case LocationCurve lc when lc.Curve != null:
                    {
                        var c = transform == null ? lc.Curve : lc.Curve.CreateTransformed(transform);
                        return new
                        {
                            kind = "curve",
                            curveType = c.GetType().Name,
                            start = c.IsBound ? Point(c.GetEndPoint(0)) : null,
                            end = c.IsBound ? Point(c.GetEndPoint(1)) : null,
                            lengthMm = c.IsBound ? Mm(c.Length) : (double?)null
                        };
                    }
                }
            }
            catch
            {
                // fall through
            }
            return null;
        }

        /// <summary>A point that represents the element: location point, curve midpoint, else bounding box centre.</summary>
        public static XYZ RepresentativePoint(Element e, out string how)
        {
            try
            {
                if (e.Location is LocationPoint lp)
                {
                    how = "location";
                    return lp.Point;
                }
                if (e.Location is LocationCurve lc && lc.Curve != null && lc.Curve.IsBound)
                {
                    how = "curveMidpoint";
                    return lc.Curve.Evaluate(0.5, true);
                }
            }
            catch
            {
                // fall back to the bounding box
            }
            var bb = e.get_BoundingBox(null);
            if (bb != null)
            {
                how = "boundingBoxCenter";
                return (bb.Min + bb.Max) / 2.0;
            }
            how = null;
            return null;
        }

        // ---- ids and references --------------------------------------------------------------------------------

        public static string IdText(long id) => id.ToString(CultureInfo.InvariantCulture);

        /// <summary>Resolves references (id / UniqueId / link:id) and collects the ones that fail.</summary>
        public static List<ResolvedElement> ResolveMany(ToolContext ctx, IEnumerable<string> refs, List<object> notFound,
            bool allowLinked = true)
        {
            var result = new List<ResolvedElement>();
            foreach (var r in (refs ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
            {
                try
                {
                    var resolved = ctx.Services.Lookup.Resolve(r);
                    if (resolved.IsLinked && !allowLinked)
                        throw new ToolException("LINK_READ_ONLY", $"'{r}' is inside a Revit link; this tool works on the target document only.");
                    result.Add(resolved);
                }
                catch (ToolException ex)
                {
                    notFound.Add(new { reference = r, code = ex.Code, message = ex.Message });
                }
            }
            return result;
        }

        // ---- phases ---------------------------------------------------------------------------------------------

        public static Phase LastPhase(Document doc)
        {
            var phases = doc.Phases;
            return phases == null || phases.Size == 0 ? null : phases.get_Item(phases.Size - 1);
        }

        /// <summary>The named phase, or the project's last phase when <paramref name="nameOrId"/> is empty.</summary>
        public static Phase PhaseOrLast(ToolContext ctx, string nameOrId)
        {
            if (!string.IsNullOrWhiteSpace(nameOrId)) return ctx.Services.Lookup.Phase(nameOrId);
            return LastPhase(ctx.Doc) ?? throw new ToolException("NOT_FOUND", "The document has no phases.");
        }

        public static string PhaseStatus(Element e, ElementId phaseId)
        {
            try { return e.GetPhaseStatus(phaseId).ToString(); }
            catch { return null; }
        }

        // ---- text filters ----------------------------------------------------------------------------------------

        public static bool Contains(string text, string needle) =>
            string.IsNullOrWhiteSpace(needle) ||
            (text != null && TextMatch.Fold(text).IndexOf(TextMatch.Fold(needle), StringComparison.Ordinal) >= 0);

        /// <summary>Validates limit/offset like the other framework tools.</summary>
        public static void CheckPaging(int limit, int offset, int max)
        {
            if (limit < 1 || limit > max)
                throw new ToolException("INVALID_ARGUMENTS", $"limit must be between 1 and {max}.");
            if (offset < 0)
                throw new ToolException("INVALID_ARGUMENTS", "offset must be 0 or more.");
        }

        /// <summary>Reads requested parameters (instance first, then type) in wire units.</summary>
        public static Dictionary<string, object> Fields(ParameterService ps, Element e, IList<ParameterRef> fields)
        {
            if (fields == null || fields.Count == 0) return null;
            var values = new Dictionary<string, object>();
            foreach (var f in fields)
            {
                Parameter p = null;
                try
                {
                    p = e is ElementType ? null : ps.Find(e, f);
                    if (p == null)
                    {
                        var t = e is ElementType ? e : ParameterService.TypeOf(e);
                        if (t != null) p = ps.Find(t, f);
                    }
                }
                catch (ToolException)
                {
                    p = null;
                }
                if (p == null)
                {
                    values[f.ToString()] = null;
                    continue;
                }
                var v = ps.ReadWire(p, out var unit, out _);
                values[f.ToString()] = unit == null ? v : new Dictionary<string, object> { ["value"] = v, ["unit"] = unit };
            }
            return values;
        }
    }
}
