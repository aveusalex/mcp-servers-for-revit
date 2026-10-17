using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing.Support
{
    /// <summary>A geometric reference picked by name, with its plane when it is planar (null otherwise).</summary>
    public sealed class PickedReference
    {
        public Reference Reference { get; set; }
        public Plane Plane { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Picks references of elements without a mouse: reference planes, levels, grids, wall sides, the standard
    /// references of family instances (left/right/front/back/top/bottom/centers) and planar faces by direction.
    /// </summary>
    public static class ReferencePicker
    {
        public const string Values =
            "auto (reference plane / level / grid), exterior / interior (walls), left / right / front / back / top / bottom / " +
            "centerLeftRight / centerFrontBack / centerElevation (family instances), face+x / face-x / face+y / face-y / face+z / face-z " +
            "(largest planar face pointing that way)";

        public static PickedReference Pick(Element e, string which)
        {
            var key = (which ?? "auto").Trim().ToLowerInvariant().Replace(" ", string.Empty);
            if (key == "auto" || key == "plane")
            {
                switch (e)
                {
                    case ReferencePlane rp:
                        return new PickedReference { Reference = rp.GetReference(), Plane = rp.GetPlane(), Description = $"reference plane '{rp.Name}'" };
                    case Level lv:
                        return new PickedReference { Reference = new Reference(lv), Plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, lv.Elevation)), Description = $"level '{lv.Name}'" };
                    case Grid g:
                        Plane gp = null;
                        if (g.Curve is Line gl && !gl.Direction.CrossProduct(XYZ.BasisZ).IsZeroLength())
                            gp = Plane.CreateByNormalAndOrigin(gl.Direction.CrossProduct(XYZ.BasisZ).Normalize(), gl.Origin);
                        return new PickedReference { Reference = new Reference(g), Plane = gp, Description = $"grid '{g.Name}'" };
                    default:
                        throw new ToolException("INVALID_ARGUMENTS", $"Element {e.Id.GetValue()} ({e.Category?.Name}) needs an explicit reference.",
                            "Use one of: " + Values);
                }
            }
            if (key == "exterior" || key == "interior")
            {
                if (!(e is Wall wall))
                    throw new ToolException("INVALID_ARGUMENTS", $"'{key}' is only for walls; element {e.Id.GetValue()} is {e.Category?.Name}.");
                var refs = HostObjectUtils.GetSideFaces(wall, key == "exterior" ? ShellLayerType.Exterior : ShellLayerType.Interior);
                var r = refs.FirstOrDefault() ?? throw new ToolException("REFERENCE_NOT_FOUND", $"Wall {e.Id.GetValue()} has no {key} face.");
                var face = e.GetGeometryObjectFromReference(r) as PlanarFace;
                return new PickedReference { Reference = r, Plane = face == null ? null : Plane.CreateByNormalAndOrigin(face.FaceNormal, face.Origin), Description = $"{key} face of wall {e.Id.GetValue()}" };
            }
            if (key.StartsWith("face", StringComparison.Ordinal))
            {
                var dir = Direction(key.Substring(4));
                var best = PlanarFaces(e).Where(f => f.Item2.OfVector(f.Item1.FaceNormal).DotProduct(dir) > 0.999)
                    .OrderByDescending(f => f.Item1.Area).FirstOrDefault();
                if (best == null)
                    throw new ToolException("REFERENCE_NOT_FOUND", $"Element {e.Id.GetValue()} has no planar face pointing {key.Substring(4)}.");
                var n = best.Item2.OfVector(best.Item1.FaceNormal);
                var o = best.Item2.OfPoint(best.Item1.Origin);
                var reference = best.Item1.Reference;
                if (best.Item3) reference = InstanceReference(e, reference);
                return new PickedReference { Reference = reference, Plane = Plane.CreateByNormalAndOrigin(n, o), Description = $"{key} of element {e.Id.GetValue()}" };
            }
            if (e is FamilyInstance fi && TryInstanceType(key, out var type))
            {
                var r = fi.GetReferences(type).FirstOrDefault()
                        ?? throw new ToolException("REFERENCE_NOT_FOUND", $"The family of element {e.Id.GetValue()} defines no '{key}' reference.",
                            "Use a face+x / face-y ... face reference instead.");
                return new PickedReference { Reference = r, Plane = null, Description = $"{key} reference of family instance {e.Id.GetValue()}" };
            }
            throw new ToolException("INVALID_ARGUMENTS", $"Unknown reference '{which}' for element {e.Id.GetValue()} ({e.Category?.Name}).", "Use one of: " + Values);
        }

        private static XYZ Direction(string s)
        {
            switch (s)
            {
                case "+x": case "x": return XYZ.BasisX;
                case "-x": return -XYZ.BasisX;
                case "+y": case "y": return XYZ.BasisY;
                case "-y": return -XYZ.BasisY;
                case "+z": case "z": case "top": return XYZ.BasisZ;
                case "-z": case "bottom": return -XYZ.BasisZ;
                default: throw new ToolException("INVALID_ARGUMENTS", $"Unknown face direction '{s}' (use +x, -x, +y, -y, +z, -z).");
            }
        }

        private static bool TryInstanceType(string key, out FamilyInstanceReferenceType type)
        {
            switch (key)
            {
                case "left": type = FamilyInstanceReferenceType.Left; return true;
                case "right": type = FamilyInstanceReferenceType.Right; return true;
                case "front": type = FamilyInstanceReferenceType.Front; return true;
                case "back": type = FamilyInstanceReferenceType.Back; return true;
                case "top": type = FamilyInstanceReferenceType.Top; return true;
                case "bottom": type = FamilyInstanceReferenceType.Bottom; return true;
                case "centerleftright": type = FamilyInstanceReferenceType.CenterLeftRight; return true;
                case "centerfrontback": type = FamilyInstanceReferenceType.CenterFrontBack; return true;
                case "centerelevation": type = FamilyInstanceReferenceType.CenterElevation; return true;
                default: type = FamilyInstanceReferenceType.NotAReference; return false;
            }
        }

        /// <summary>
        /// A face reference taken from a family symbol's geometry, turned into a reference of the instance (the
        /// stable-representation form "instanceUniqueId:0:INSTANCE:symbolReference").
        /// </summary>
        private static Reference InstanceReference(Element instance, Reference symbolReference)
        {
            try
            {
                var doc = instance.Document;
                var stable = $"{instance.UniqueId}:0:INSTANCE:{symbolReference.ConvertToStableRepresentation(doc)}";
                return Reference.ParseFromStableRepresentation(doc, stable) ?? symbolReference;
            }
            catch
            {
                return symbolReference;
            }
        }

        /// <summary>
        /// Planar faces with references, the transform to model coordinates, and whether the face comes from a
        /// family symbol (its reference must be converted to an instance reference).
        /// </summary>
        public static List<Tuple<PlanarFace, Transform, bool>> PlanarFaces(Element e)
        {
            var result = new List<Tuple<PlanarFace, Transform, bool>>();
            var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine };
            void Walk(GeometryElement ge, Transform t, bool fromSymbol)
            {
                if (ge == null) return;
                foreach (var obj in ge)
                {
                    if (obj is Solid s && s.Volume > 1e-9)
                    {
                        foreach (Face f in s.Faces)
                            if (f is PlanarFace pf && pf.Reference != null) result.Add(Tuple.Create(pf, t, fromSymbol));
                    }
                    else if (obj is GeometryInstance gi)
                    {
                        Walk(gi.GetSymbolGeometry(), t.Multiply(gi.Transform), true);
                    }
                }
            }
            Walk(e.get_Geometry(options), Transform.Identity, false);
            return result;
        }
    }
}
