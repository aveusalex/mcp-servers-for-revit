using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture
{
    public sealed class CreateSimilarArgs
    {
        [JsonProperty("sourceElementId")]
        [Required]
        [Description("The existing element to imitate (its type, level, offset, rotation, flips and host are reused unless overridden).")]
        public long SourceElementId { get; set; }

        [JsonProperty("location")]
        [Required]
        [Description("Insertion point in mm, or the START of the line for walls, beams, line-based families, grids and lines. z only matters for lines and when no level applies.")]
        public Point3Mm Location { get; set; }

        [JsonProperty("end")]
        [Description("END of the line in mm (walls, beams/braces, line-based families, grids, model/detail lines).")]
        public Point3Mm End { get; set; }

        [JsonProperty("level")]
        [Description("Level by name or id. Default: the source's level.")]
        public string Level { get; set; }

        [JsonProperty("offset")]
        [Unit("mm")]
        [Description("Offset from the level in mm (wall base offset / instance elevation). Default: the source's.")]
        public double? Offset { get; set; }

        [JsonProperty("rotation")]
        [Unit("deg")]
        [Description("Rotation in degrees for point-placed instances. Default: the source's rotation.")]
        public double? Rotation { get; set; }

        [JsonProperty("hostId")]
        [Description("Host for hosted / face-based families (e.g. another wall for a door). Default: the source's host.")]
        public long? HostId { get; set; }

        [JsonProperty("copyParameters")]
        [DefaultValue(false)]
        [Description("Also copy the source's writable project/shared/family instance parameters and Comments (never Mark, levels, offsets or other built-in geometry parameters). Default false.")]
        public bool CopyParameters { get; set; }
    }

    /// <summary>ARQ-21: "Create Similar": a new element of the same type (and settings) as an existing one.</summary>
    [McpTool("create_similar", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Create a new element like an existing one (Revit's Create Similar): same type, level, offset, " +
                      "rotation, flips and host unless overridden, at a new point or line in mm. Supports family instances " +
                      "(point, hosted, face-based, line-based, structural columns/beams), walls (location/end = centerline), " +
                      "grids and model/detail lines. Floors, ceilings, roofs and rooms are refused with the tool to use. " +
                      "copyParameters copies user parameters. Returns the new id read back. Undo: Ctrl+Z.")]
    public sealed class CreateSimilarTool : McpTool<CreateSimilarArgs>
    {
        protected override object Run(ToolContext ctx, CreateSimilarArgs a)
        {
            var doc = ctx.Doc;
            var source = doc.GetElement(a.SourceElementId.ToRevitElementId())
                         ?? throw new ToolException("ELEMENT_NOT_FOUND", $"Element {a.SourceElementId} does not exist.", null, "find_elements");
            Element created;
            object detail;
            try
            {
                switch (source)
                {
                    case Wall wall:
                        created = SimilarWall(ctx, wall, a);
                        detail = null;
                        break;
                    case FamilyInstance fi:
                        created = SimilarInstance(ctx, fi, a, out detail);
                        break;
                    case Grid grid:
                        created = SimilarGrid(ctx, grid, a);
                        detail = null;
                        break;
                    case CurveElement curve when curve is ModelCurve || curve is DetailCurve:
                        created = SimilarLine(ctx, curve, a);
                        detail = null;
                        break;
                    default:
                        throw Unsupported(source);
                }
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("CREATE_FAILED", $"Revit refused to create the element: {ex.Message}");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                throw new ToolException("CREATE_FAILED", $"Revit refused to create the element: {ex.Message}");
            }

            object parameters = null;
            if (a.CopyParameters) parameters = CopyParameters(source, created);
            doc.Regenerate();
            if (!created.IsValidObject) throw new ToolException("CREATE_FAILED", "Revit removed the new element right after creating it.");

            return new
            {
                id = created.Id.GetValue(),
                sourceId = source.Id.GetValue(),
                category = created.Category?.Name,
                type = TypeName(created),
                level = (doc.GetElement(created.LevelId) as Level)?.Name,
                location = Where(created),
                placement = detail,
                parameters,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"Created {created.Category?.Name ?? "element"} {created.Id.GetValue()} like {source.Id.GetValue()}."
            };
        }

        // ---- per kind ---------------------------------------------------------------------------------------------

        private static Wall SimilarWall(ToolContext ctx, Wall src, CreateSimilarArgs a)
        {
            var doc = ctx.Doc;
            if (a.End == null) throw new ToolException("INVALID_ARGUMENTS", "A wall needs location (start) and end: its new centerline in mm.");
            var level = ResolveLevel(ctx, a, src.LevelId);
            double z = level.Elevation;
            var line = Segment(doc, a.Location, a.End, z, z);
            double height = ArchGeometry.DoubleParam(src, BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (double.IsNaN(height) || height <= 0) height = ArchGeometry.Ft(3000);
            double baseOffset = a.Offset.HasValue ? ArchGeometry.Ft(a.Offset.Value) : Zero(ArchGeometry.DoubleParam(src, BuiltInParameter.WALL_BASE_OFFSET));
            bool structural = src.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1;

            var wall = Wall.Create(doc, line, src.WallType.Id, level.Id, height, baseOffset, false, structural)
                       ?? throw new ToolException("CREATE_FAILED", "Revit did not create the wall.");
            doc.Regenerate();
            var top = src.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId();
            if (top != null && top != ElementId.InvalidElementId)
            {
                wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(top);
                var topOffset = ArchGeometry.DoubleParam(src, BuiltInParameter.WALL_TOP_OFFSET);
                if (!double.IsNaN(topOffset)) wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(topOffset);
            }
            if (src.Flipped) wall.Flip();
            // Switching the location line keeps the body on the given centerline (see create_walls).
            var keyRef = src.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger();
            if (keyRef.HasValue) wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set(keyRef.Value);
            return wall;
        }

        private static Element SimilarInstance(ToolContext ctx, FamilyInstance src, CreateSimilarArgs a, out object detail)
        {
            var doc = ctx.Doc;
            var symbol = src.Symbol;
            var placement = symbol.Family.FamilyPlacementType;
            var srcLevelId = src.LevelId != ElementId.InvalidElementId
                ? src.LevelId
                : src.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsElementId()
                  ?? src.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId();

            // Structural framing (beams, braces) and columns: placed with their structural type.
            if (placement == FamilyPlacementType.CurveDrivenStructural || src.StructuralType == StructuralType.Beam ||
                src.StructuralType == StructuralType.Brace)
            {
                if (a.End == null) throw new ToolException("INVALID_ARGUMENTS", "A beam/brace needs location (start) and end.");
                var level = ResolveLevel(ctx, a, srcLevelId);
                var srcCurve = (src.Location as LocationCurve)?.Curve;
                double z0 = a.Location.Z.HasValue ? ArchGeometry.Ft(a.Location.Z.Value) : srcCurve?.GetEndPoint(0).Z ?? level.Elevation;
                double z1 = a.End.Z.HasValue ? ArchGeometry.Ft(a.End.Z.Value) : srcCurve?.GetEndPoint(1).Z ?? level.Elevation;
                TypeFinder.Activate(doc, symbol);
                var st = src.StructuralType == StructuralType.NonStructural ? StructuralType.Beam : src.StructuralType;
                var beam = doc.Create.NewFamilyInstance(Segment(doc, a.Location, a.End, z0, z1), symbol, level, st)
                           ?? throw new ToolException("CREATE_FAILED", "Revit did not create the framing member.");
                detail = "structural line";
                return beam;
            }
            if (src.StructuralType == StructuralType.Column && src.Location is LocationPoint)
            {
                var level = ResolveLevel(ctx, a, srcLevelId);
                TypeFinder.Activate(doc, symbol);
                var at = new XYZ(ArchGeometry.Ft(a.Location.X), ArchGeometry.Ft(a.Location.Y), level.Elevation);
                var column = doc.Create.NewFamilyInstance(at, symbol, level, StructuralType.Column)
                             ?? throw new ToolException("CREATE_FAILED", "Revit did not create the column.");
                CopyDouble(src, column, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
                CopyElementId(src, column, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
                CopyDouble(src, column, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
                Rotate(doc, column, a.Rotation ?? SourceRotation(src));
                detail = "structural column";
                return column;
            }

            long? hostId = a.HostId;
            if (!hostId.HasValue && src.Host != null &&
                (placement == FamilyPlacementType.OneLevelBasedHosted || placement == FamilyPlacementType.WorkPlaneBased ||
                 placement == FamilyPlacementType.CurveBased))
                hostId = src.Host.Id.GetValue();
            if (src.Host is Level) hostId = a.HostId; // level-hosted: no element host

            var item = new FamilyInstanceItem
            {
                FamilyType = symbol.Id.GetValue().ToString(CultureInfo.InvariantCulture),
                Placement = "auto",
                Location = a.Location,
                End = a.End,
                Level = !string.IsNullOrWhiteSpace(a.Level) ? a.Level
                    : srcLevelId != null && srcLevelId != ElementId.InvalidElementId ? srcLevelId.GetValue().ToString(CultureInfo.InvariantCulture) : null,
                Offset = a.Offset ?? FamilyPlacement.ElevationMm(src) ?? 0,
                HostId = hostId,
                Rotation = a.Rotation ?? SourceRotation(src),
                FlipFacing = src.FacingFlipped,
                FlipHand = src.HandFlipped
            };
            if (placement == FamilyPlacementType.CurveBased && a.End == null)
                throw new ToolException("INVALID_ARGUMENTS", "This family is line-based: give location (start) and end.");
            var placed = JObject.FromObject(FamilyPlacement.Place(ctx, symbol, item, 0));
            detail = (string)placed["placement"];
            var id = (long)placed["id"];
            return doc.GetElement(id.ToRevitElementId());
        }

        private static Grid SimilarGrid(ToolContext ctx, Grid src, CreateSimilarArgs a)
        {
            if (a.End == null) throw new ToolException("INVALID_ARGUMENTS", "A grid needs location (start) and end.");
            var doc = ctx.Doc;
            var z = src.Curve.GetEndPoint(0).Z;
            var grid = Grid.Create(doc, Segment(doc, a.Location, a.End, z, z))
                       ?? throw new ToolException("CREATE_FAILED", "Revit did not create the grid.");
            if (grid.GetTypeId() != src.GetTypeId()) grid.ChangeTypeId(src.GetTypeId());
            return grid;
        }

        private static CurveElement SimilarLine(ToolContext ctx, CurveElement src, CreateSimilarArgs a)
        {
            if (a.End == null) throw new ToolException("INVALID_ARGUMENTS", "A line needs location (start) and end.");
            var doc = ctx.Doc;
            var srcCurve = src.GeometryCurve;
            double z0 = a.Location.Z.HasValue ? ArchGeometry.Ft(a.Location.Z.Value) : srcCurve.GetEndPoint(0).Z;
            double z1 = a.End.Z.HasValue ? ArchGeometry.Ft(a.End.Z.Value) : z0;
            CurveElement created;
            if (src is DetailCurve)
            {
                var view = doc.GetElement(src.OwnerViewId) as View
                           ?? throw new ToolException("CREATE_FAILED", "The source detail line has no view.");
                created = doc.Create.NewDetailCurve(view, Segment(doc, a.Location, a.End, z0, z0));
            }
            else
            {
                var line = Segment(doc, a.Location, a.End, z0, z1);
                var dir = line.Direction;
                XYZ normal = Math.Abs(dir.Z) < 1e-9 ? XYZ.BasisZ
                    : (dir.CrossProduct(XYZ.BasisZ).GetLength() < 1e-9 ? XYZ.BasisX : dir.CrossProduct(XYZ.BasisZ).Normalize());
                var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(normal, line.GetEndPoint(0)));
                created = doc.Create.NewModelCurve(line, plane);
            }
            if (created == null) throw new ToolException("CREATE_FAILED", "Revit did not create the line.");
            var style = src.LineStyle;
            if (style != null) created.LineStyle = style;
            return created;
        }

        // ---- helpers ----------------------------------------------------------------------------------------------

        private static ToolException Unsupported(Element e)
        {
            string tool = null;
            switch (e)
            {
                case Floor _: tool = "create_floor"; break;
                case Ceiling _: tool = "create_ceiling"; break;
                case RoofBase _: tool = null; break;
                case SpatialElement _: tool = "create_rooms"; break;
                case Level _: tool = "create_or_update_levels"; break;
                case Group _: tool = "place_group_instance"; break;
            }
            var type = TypeName(e);
            return new ToolException("UNSUPPORTED_SOURCE",
                $"create_similar does not handle {e.Category?.Name ?? e.GetType().Name} elements (they need a boundary or are not typed instances).",
                (tool != null ? $"Use {tool}" : "Create it with the matching creation tool") +
                (type != null ? $" with the type '{type}' (type id {e.GetTypeId().GetValue()})." : "."),
                tool);
        }

        private static Level ResolveLevel(ToolContext ctx, CreateSimilarArgs a, ElementId sourceLevelId)
        {
            if (!string.IsNullOrWhiteSpace(a.Level)) return ctx.Services.Lookup.Level(a.Level);
            if (sourceLevelId != null && ctx.Doc.GetElement(sourceLevelId) is Level l) return l;
            throw new ToolException("INVALID_ARGUMENTS", "The source has no level; pass level (name or id).");
        }

        private static Line Segment(Document doc, Point3Mm a, Point3Mm b, double z0, double z1)
        {
            var p = new XYZ(ArchGeometry.Ft(a.X), ArchGeometry.Ft(a.Y), z0);
            var q = new XYZ(ArchGeometry.Ft(b.X), ArchGeometry.Ft(b.Y), z1);
            if (p.DistanceTo(q) <= doc.Application.ShortCurveTolerance)
                throw new ToolException("INVALID_GEOMETRY", "location and end are too close.");
            return Line.CreateBound(p, q);
        }

        private static double SourceRotation(FamilyInstance fi)
        {
            try { return fi.Location is LocationPoint lp ? ArchGeometry.Deg(lp.Rotation) : 0; }
            catch { return 0; }
        }

        private static void Rotate(Document doc, Element e, double deg)
        {
            if (Math.Abs(deg) < 1e-9 || !(e.Location is LocationPoint lp)) return;
            ElementTransformUtils.RotateElement(doc, e.Id, Line.CreateBound(lp.Point, lp.Point + XYZ.BasisZ), ArchGeometry.Rad(deg));
        }

        private static double Zero(double v) => double.IsNaN(v) ? 0 : v;

        private static void CopyDouble(Element from, Element to, BuiltInParameter bip)
        {
            var v = ArchGeometry.DoubleParam(from, bip);
            var p = to.get_Parameter(bip);
            if (!double.IsNaN(v) && p != null && !p.IsReadOnly) p.Set(v);
        }

        private static void CopyElementId(Element from, Element to, BuiltInParameter bip)
        {
            var v = from.get_Parameter(bip)?.AsElementId();
            var p = to.get_Parameter(bip);
            if (v != null && v != ElementId.InvalidElementId && p != null && !p.IsReadOnly) p.Set(v);
        }

        /// <summary>Copies user (project/shared/family) parameters and Comments; never built-in geometry or Mark.</summary>
        private static object CopyParameters(Element from, Element to)
        {
            var copied = new List<string>();
            var failed = new List<object>();
            foreach (Parameter p in from.Parameters)
            {
                if (p == null || !p.HasValue || p.IsReadOnly) continue;
                if (p.StorageType == StorageType.ElementId || p.StorageType == StorageType.None) continue;
                var bip = (p.Definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
                if (bip != BuiltInParameter.INVALID && bip != BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) continue;
                var name = p.Definition.Name;
                Parameter target = p.IsShared ? to.get_Parameter(p.GUID) : bip != BuiltInParameter.INVALID ? to.get_Parameter(bip) : to.LookupParameter(name);
                if (target == null || target.IsReadOnly || target.StorageType != p.StorageType) continue;
                bool ok;
                try
                {
                    switch (p.StorageType)
                    {
                        case StorageType.String: ok = target.Set(p.AsString() ?? string.Empty); break;
                        case StorageType.Integer: ok = target.Set(p.AsInteger()); break;
                        default: ok = target.Set(p.AsDouble()); break;
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    failed.Add(new { name, reason = ex.Message });
                    continue;
                }
                if (ok) copied.Add(name);
                else failed.Add(new { name, reason = "Revit did not accept the value." });
            }
            return new { copied, failed };
        }

        private static string TypeName(Element e)
        {
            var id = e.GetTypeId();
            return id != null && id != ElementId.InvalidElementId && e.Document.GetElement(id) is ElementType t ? LookupService.FullName(t) : null;
        }

        private static object Where(Element e)
        {
            switch (e.Location)
            {
                case LocationPoint lp:
                    double? rot = null;
                    try { rot = ArchGeometry.Deg(lp.Rotation); } catch { /* not rotatable */ }
                    return new { point = ArchGeometry.ToWire(lp.Point), rotationDeg = rot };
                case LocationCurve lc when lc.Curve != null:
                    return new { start = ArchGeometry.ToWire(lc.Curve.GetEndPoint(0)), end = ArchGeometry.ToWire(lc.Curve.GetEndPoint(1)) };
            }
            if (e is Grid g) return new { start = ArchGeometry.ToWire(g.Curve.GetEndPoint(0)), end = ArchGeometry.ToWire(g.Curve.GetEndPoint(1)) };
            if (e is CurveElement ce) return new { start = ArchGeometry.ToWire(ce.GeometryCurve.GetEndPoint(0)), end = ArchGeometry.ToWire(ce.GeometryCurve.GetEndPoint(1)) };
            return null;
        }
    }
}
