using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing.Support
{
    /// <summary>
    /// Rebuilds a sketch-based element (floor, ceiling, footprint roof, filled region) with its outline transformed
    /// point by point, keeping type, level, offset, phase, comments and mark (and, for roofs, each edge's slope and
    /// overhang). Used by stretch_model; the caller deletes the original.
    /// </summary>
    internal static class SketchRebuild
    {
        /// <summary>What the last roof rebuild did with the curtain grid (for the tool's report).</summary>
        [ThreadStatic] public static string LastGridReport;

        public static Element Rebuild(Document doc, Element e, Sketch sketch, Func<XYZ, XYZ> map)
        {
            if (sketch == null) throw new ToolException("NOT_SUPPORTED", "The element has no sketch.");
            double tol = doc.Application.ShortCurveTolerance;
            Element created;
            switch (e)
            {
                case Floor floor:
                    created = Floor.Create(doc, Loops(sketch, map, tol), floor.GetTypeId(), floor.LevelId);
                    Copy(floor, created, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
                    break;
                case Ceiling ceiling:
                    created = Ceiling.Create(doc, Loops(sketch, map, tol), ceiling.GetTypeId(), ceiling.LevelId);
                    Copy(ceiling, created, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                    break;
                case FilledRegion region:
                    created = FilledRegion.Create(doc, region.GetTypeId(), region.OwnerViewId, Loops(sketch, map, tol));
                    break;
                case FootPrintRoof roof:
                    created = RebuildRoof(doc, roof, map, tol);
                    break;
                default:
                    throw new ToolException("NOT_SUPPORTED", $"{e.GetType().Name} can't be rebuilt (only floors, ceilings, footprint roofs and filled regions).");
            }
            Copy(e, created, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, BuiltInParameter.ALL_MODEL_MARK);
            try { if (created.CreatedPhaseId != e.CreatedPhaseId) created.CreatedPhaseId = e.CreatedPhaseId; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { /* phase can't be set for some categories */ }
            return created;
        }

        private static IList<CurveLoop> Loops(Sketch sketch, Func<XYZ, XYZ> map, double tol)
        {
            var loops = new List<CurveLoop>();
            foreach (CurveArray arr in sketch.Profile)
            {
                var curves = new List<Curve>();
                foreach (Curve c in arr)
                {
                    var m = Map(c, map, tol);
                    if (m != null) curves.Add(m);
                }
                if (curves.Count < 3) throw new ToolException("SKETCH_COLLAPSES", "A loop of the outline collapses inside the band.");
                loops.Add(CurveLoop.Create(curves));
            }
            return loops;
        }

        /// <summary>The transformed curve, or null when it collapses (a segment lying inside the removed band).</summary>
        private static Curve Map(Curve c, Func<XYZ, XYZ> map, double tol)
        {
            var p0 = map(c.GetEndPoint(0));
            var p1 = map(c.GetEndPoint(1));
            if (p0.DistanceTo(p1) < tol) return null;
            if (c is Line) return Line.CreateBound(p0, p1);
            if (c is Arc arc) return Arc.Create(p0, p1, map(arc.Evaluate(0.5, true)));
            throw new ToolException("NOT_SUPPORTED", $"The outline has a {c.GetType().Name}; only lines and arcs are stretched.");
        }

        private static Element RebuildRoof(Document doc, FootPrintRoof roof, Func<XYZ, XYZ> map, double tol)
        {
            var profiles = roof.GetProfiles();
            if (profiles.Size != 1) throw new ToolException("NOT_SUPPORTED", "Roofs with more than one outline loop are not rebuilt.");
            var originals = profiles.get_Item(0).Cast<ModelCurve>().ToList();
            var level = doc.GetElement(roof.LevelId) as Level ?? throw new ToolException("NOT_SUPPORTED", "The roof has no level.");
            // The footprint goes on the level's plane; the base offset is copied afterwards.
            double z = level.Elevation;
            Func<XYZ, XYZ> flat = p => { var q = map(p); return new XYZ(q.X, q.Y, z); };
            var footprint = new CurveArray();
            var edgeData = new List<(Curve mapped, bool slope, double angle, double overhang, double offset)>();
            foreach (var mc in originals)
            {
                var m = Map(mc.GeometryCurve, flat, tol);
                if (m == null) continue;
                footprint.Append(m);
                bool slope = roof.get_DefinesSlope(mc);
                edgeData.Add((m, slope, slope ? roof.get_SlopeAngle(mc) : 0, Safe(() => roof.get_Overhang(mc)), Safe(() => roof.get_Offset(mc))));
            }
            var type = doc.GetElement(roof.GetTypeId()) as RoofType
                       ?? throw new ToolException("NOT_SUPPORTED", "The roof type can't be used to build a footprint roof.");
            FootPrintRoof created;
            var newCurves = new ModelCurveArray(); // the API dereferences it: it must not be null
            // A curtain-system roof (sloped glazing) gets its look from hand-placed grid lines and mullions, which
            // the API can't recreate reliably (adding grid lines to a roof crashed Revit in testing): rebuild it as a
            // regular roof with the thinnest roof type, and say so.
            bool curtain = false;
            try { curtain = roof.CurtainGrids != null && roof.CurtainGrids.Size > 0; } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            if (curtain)
            {
                var regular = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>()
                    .Where(t => t.GetCompoundStructure() != null)
                    .OrderBy(t => t.GetCompoundStructure().GetWidth()).FirstOrDefault()
                    ?? throw new ToolException("NOT_SUPPORTED", "Curtain-system roof and no regular roof type to rebuild it with.");
                LastGridReport = $"curtain-system roof '{type.Name}' rebuilt as a regular roof of type '{regular.Name}' (same outline, slopes and height); its grid lines and mullions (the corrugated look) were not recreated";
                type = regular;
            }
            try { created = doc.Create.NewFootPrintRoof(footprint, level, type, out newCurves); }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Some roof types (curtain/sloped glazing systems) are refused here: build with a basic roof type,
                // then switch the type.
                var basic = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>()
                    .FirstOrDefault(t => t.Id != type.Id && t.GetCompoundStructure() != null)
                    ?? throw new ToolException("NOT_SUPPORTED", "No basic roof type to rebuild this roof with.");
                try { created = doc.Create.NewFootPrintRoof(footprint, level, basic, out newCurves); }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    throw new ToolException("ROOF_REBUILD_FAILED", $"Revit refused the footprint ({footprint.Size} edges at z {Math.Round(level.Elevation * 304.8)} mm, level '{level.Name}', types '{type.Name}' / '{basic.Name}'): {ex.GetType().Name}: {ex.Message}");
                }
                if (!curtain) created.ChangeTypeId(type.Id);
            }
            foreach (ModelCurve nc in newCurves)
            {
                var g = nc.GeometryCurve;
                var match = edgeData.FirstOrDefault(d => d.mapped.GetEndPoint(0).IsAlmostEqualTo(g.GetEndPoint(0)) && d.mapped.GetEndPoint(1).IsAlmostEqualTo(g.GetEndPoint(1))
                                                        || d.mapped.GetEndPoint(0).IsAlmostEqualTo(g.GetEndPoint(1)) && d.mapped.GetEndPoint(1).IsAlmostEqualTo(g.GetEndPoint(0)));
                if (match.mapped == null) continue;
                created.set_DefinesSlope(nc, match.slope);
                if (match.slope) created.set_SlopeAngle(nc, match.angle);
                try { created.set_Overhang(nc, match.overhang); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                try { created.set_Offset(nc, match.offset); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            }
            Copy(roof, created, BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM, BuiltInParameter.ROOF_UPTO_LEVEL_OFFSET_PARAM);
            return created;
        }

        private static double Safe(Func<double> get)
        {
            try { return get(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return 0; }
        }

        /// <summary>What is needed to place a hosted instance again on a rebuilt host.</summary>
        internal sealed class HostedCopy
        {
            public long OldId;
            public FamilySymbol Symbol;
            public XYZ Point;
            public Line Path;          // line-based families
            public XYZ Hand;
            public XYZ Facing;
            public bool FaceBased;
            public ElementId LevelId;
            public List<(Definition def, StorageType type, object value)> Values = new List<(Definition, StorageType, object)>();
        }

        public static HostedCopy Capture(FamilyInstance fi, Func<XYZ, XYZ> map)
        {
            var line = (fi.Location as LocationCurve)?.Curve as Line;
            var point = (fi.Location as LocationPoint)?.Point ?? line?.Evaluate(0.5, true)
                        ?? throw new ToolException("NOT_SUPPORTED", $"Hosted element {fi.Id.GetValue()} has no insertion point or line.");
            var copy = new HostedCopy
            {
                OldId = fi.Id.GetValue(),
                Symbol = fi.Symbol,
                Point = map(point),
                Path = line == null ? null : Line.CreateBound(map(line.GetEndPoint(0)), map(line.GetEndPoint(1))),
                Hand = fi.HandOrientation,
                Facing = fi.FacingOrientation,
                FaceBased = fi.Symbol.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased
                            || fi.Symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted && fi.HostFace != null,
                LevelId = fi.LevelId
            };
            foreach (Parameter p in fi.Parameters)
            {
                if (p.IsReadOnly || !p.HasValue || p.StorageType == StorageType.None || p.StorageType == StorageType.ElementId) continue;
                // Mark must stay unique: Revit gives the new instance its own.
                if (p.Definition is InternalDefinition idef && idef.BuiltInParameter == BuiltInParameter.ALL_MODEL_MARK) continue;
                object v = p.StorageType == StorageType.Double ? (object)p.AsDouble()
                    : p.StorageType == StorageType.Integer ? p.AsInteger() : (object)p.AsString();
                copy.Values.Add((p.Definition, p.StorageType, v));
            }
            return copy;
        }

        /// <summary>Places the captured instance on <paramref name="host"/>'s top (floors, roofs) or bottom (ceilings) face.</summary>
        public static (long oldId, long newId, string family) Replace(Document doc, HostedCopy c, Element host)
        {
            if (!c.Symbol.IsActive) c.Symbol.Activate();
            FamilyInstance placed = null;
            var faces = host is Ceiling
                ? HostObjectUtils.GetBottomFaces((HostObject)host)
                : HostObjectUtils.GetTopFaces((HostObject)host);
            foreach (var r in faces)
            {
                if (!(host.GetGeometryObjectFromReference(r) is Face face)) continue;
                var hit = face.Project(c.Point);
                if (hit == null) continue;
                try
                {
                    if (c.Path != null)
                    {
                        // Line-based on a face: the line projected onto the face.
                        var a = face.Project(c.Path.GetEndPoint(0))?.XYZPoint ?? c.Path.GetEndPoint(0);
                        var b = face.Project(c.Path.GetEndPoint(1))?.XYZPoint ?? c.Path.GetEndPoint(1);
                        placed = doc.Create.NewFamilyInstance(r, Line.CreateBound(a, b), c.Symbol);
                    }
                    else placed = doc.Create.NewFamilyInstance(r, hit.XYZPoint, c.Hand, c.Symbol);
                    break;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // not a face-based family: try the level-hosted overload below
                }
            }
            if (placed == null)
            {
                var level = doc.GetElement(c.LevelId) as Level;
                placed = doc.Create.NewFamilyInstance(c.Point, c.Symbol, host, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                // Turn it to the original orientation.
                var current = placed.HandOrientation;
                double angle = current.AngleOnPlaneTo(c.Hand, XYZ.BasisZ);
                if (Math.Abs(angle) > 1e-6)
                    ElementTransformUtils.RotateElement(doc, placed.Id, Line.CreateBound(c.Point, c.Point + XYZ.BasisZ), angle);
            }
            foreach (var (def, type, value) in c.Values)
            {
                var p = placed.get_Parameter(def);
                if (p == null || p.IsReadOnly) continue;
                try
                {
                    if (type == StorageType.Double) p.Set((double)value);
                    else if (type == StorageType.Integer) p.Set((int)value);
                    else p.Set((string)value ?? string.Empty);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { /* best effort */ }
            }
            return (c.OldId, placed.Id.GetValue(), c.Symbol.FamilyName);
        }

        private static void Copy(Element from, Element to, params BuiltInParameter[] ids)
        {
            foreach (var id in ids)
            {
                var src = from.get_Parameter(id);
                var dst = to.get_Parameter(id);
                if (src == null || dst == null || dst.IsReadOnly || !src.HasValue) continue;
                try
                {
                    switch (src.StorageType)
                    {
                        case StorageType.Double: dst.Set(src.AsDouble()); break;
                        case StorageType.Integer: dst.Set(src.AsInteger()); break;
                        case StorageType.String: dst.Set(src.AsString() ?? string.Empty); break;
                        case StorageType.ElementId: dst.Set(src.AsElementId()); break;
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { /* best effort */ }
            }
        }
    }
}
