using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
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
    public sealed class FamilyInstanceItem
    {
        [JsonProperty("familyType")]
        [Required]
        [Description("Family type as \"Family: Type\", type name or id. An ambiguous name returns placed:false with the candidates.")]
        public string FamilyType { get; set; }

        [JsonProperty("category")]
        [Description("Category to narrow the type search (e.g. \"Mobiliário\", \"Furniture\", \"OST_LightingFixtures\").")]
        public string Category { get; set; }

        [JsonProperty("placement")]
        [EnumValues("auto", "point", "host", "face", "line")]
        [DefaultValue("auto")]
        [Description("auto (default) follows the family: point = free on a level; host = wall/floor/ceiling-hosted family; face = on a face of hostId (face-based or work-plane families, e.g. lights on a ceiling); line = line-based family from location to end.")]
        public string Placement { get; set; }

        [JsonProperty("location")]
        [Required]
        [Description("Insertion point in mm (start point for line placement). z is only used to pick a face.")]
        public Point3Mm Location { get; set; }

        [JsonProperty("end")]
        [Description("End point in mm for line placement.")]
        public Point3Mm End { get; set; }

        [JsonProperty("level")]
        [Description("Level by name or id. Required for point/line placement; for hosted placement defaults to the host's level.")]
        public string Level { get; set; }

        [JsonProperty("offset")]
        [Unit("mm")]
        [DefaultValue(0)]
        [Description("Elevation from the level in mm (e.g. 2100 for a wall-mounted item). Default 0. Not used for face placement.")]
        public double Offset { get; set; }

        [JsonProperty("hostId")]
        [Description("Host element id (wall, floor, ceiling, roof or any element with faces) for host/face placement.")]
        public long? HostId { get; set; }

        [JsonProperty("faceSide")]
        [EnumValues("auto", "bottom", "top", "side", "nearest")]
        [DefaultValue("auto")]
        [Description("face placement: which face of the host. auto = bottom of ceilings, top of floors/roofs, the nearest side of walls.")]
        public string FaceSide { get; set; }

        [JsonProperty("rotation")]
        [Unit("deg")]
        [DefaultValue(0)]
        [Description("Rotation around the vertical axis in degrees, counter-clockwise. Default 0. Ignored for wall-hosted placement.")]
        public double Rotation { get; set; }

        [JsonProperty("flipFacing")]
        [DefaultValue(false)]
        [Description("Flip the facing orientation after placing. Default false.")]
        public bool FlipFacing { get; set; }

        [JsonProperty("flipHand")]
        [DefaultValue(false)]
        [Description("Flip the hand orientation after placing. Default false.")]
        public bool FlipHand { get; set; }

        [JsonProperty("parameters")]
        [Description("Instance parameters to set after placing, by name, e.g. {\"Marca\": \"M01\"}; same value rules as set_parameters.")]
        public Dictionary<string, JToken> Parameters { get; set; }
    }

    public sealed class PlaceFamilyInstanceArgs
    {
        [JsonProperty("items")]
        [Required]
        [MinLength(1)]
        [MaxLength(500)]
        [Description("Instances to place.")]
        public List<FamilyInstanceItem> Items { get; set; }

        [JsonProperty("continueOnError")]
        [DefaultValue(false)]
        [Description("false (default): any failure rolls back the whole call. true: failed items are skipped and listed in failed[]. Ambiguous types never abort: they come back as placed:false.")]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>ARQ-06: place family instances (point, hosted, on a face, along a line).</summary>
    [McpTool("place_family_instance", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Place family instances (furniture, fixtures, lights, equipment, plants, generic models): free on a " +
                      "level with offset, hosted on a wall/floor/ceiling, on a face of a host (e.g. lights under a " +
                      "ceiling), or along a line for line-based families. Coordinates in mm, rotation in degrees. The type " +
                      "must be named; an ambiguous name returns placed:false with candidates. Instance parameters can be set " +
                      "on creation. Returns ids and read-back location/level/host. One Ctrl+Z undoes the call.")]
    public sealed class PlaceFamilyInstanceTool : McpTool<PlaceFamilyInstanceArgs>
    {
        protected override void Validate(PlaceFamilyInstanceArgs args)
        {
            for (int i = 0; i < args.Items.Count; i++)
            {
                var it = args.Items[i] ?? throw new ToolException("INVALID_ARGUMENTS", $"items[{i}] is null.");
                var p = (it.Placement ?? "auto").ToLowerInvariant();
                if (p == "line" && it.End == null)
                    throw new ToolException("INVALID_ARGUMENTS", $"items[{i}]: line placement needs end.");
                if ((p == "host" || p == "face") && !it.HostId.HasValue)
                    throw new ToolException("INVALID_ARGUMENTS", $"items[{i}]: {p} placement needs hostId.");
            }
        }

        protected override object Run(ToolContext ctx, PlaceFamilyInstanceArgs args)
        {
            var doc = ctx.Doc;
            var notPlaced = new List<object>();
            var outcome = ItemRunner.Run(doc, args.Items, args.ContinueOnError, "Instance", (item, index) =>
            {
                var categories = string.IsNullOrWhiteSpace(item.Category)
                    ? null
                    : new List<BuiltInCategory> { ctx.Services.Categories.Resolve(item.Category).BuiltIn
                        ?? throw new ToolException("CATEGORY_NOT_SUPPORTED", $"'{item.Category}' is not a built-in category.") };
                var symbol = TypeFinder.Symbol(doc, item.FamilyType, categories, out var candidates);
                if (symbol == null)
                {
                    var skipped = new { index, placed = false, reason = "AMBIGUOUS_NAME", message = $"'{item.FamilyType}' matches several family types; pick one.", candidates };
                    notPlaced.Add(skipped);
                    return skipped;
                }
                return FamilyPlacement.Place(ctx, symbol, item, index);
            });
            var placed = outcome.Created.Where(c => !notPlaced.Contains(c)).ToList();
            return new
            {
                created = placed,
                notPlaced,
                failed = outcome.Failed,
                createdCount = placed.Count,
                notPlacedCount = notPlaced.Count,
                failedCount = outcome.Failed.Count
            };
        }
    }

    /// <summary>The placement logic, shared with other architecture tools.</summary>
    public static class FamilyPlacement
    {
        public static object Place(ToolContext ctx, FamilySymbol symbol, FamilyInstanceItem item, int index)
        {
            var doc = ctx.Doc;
            var family = symbol.Family;
            var placementType = family.FamilyPlacementType;
            var requested = (item.Placement ?? "auto").ToLowerInvariant();
            Element host = null;
            if (item.HostId.HasValue)
            {
                host = doc.GetElement(item.HostId.Value.ToRevitElementId())
                       ?? throw new ToolException("ELEMENT_NOT_FOUND", $"Host {item.HostId.Value} does not exist.");
            }

            string mode = requested;
            if (mode == "auto")
            {
                switch (placementType)
                {
                    case FamilyPlacementType.OneLevelBasedHosted: mode = "host"; break;
                    case FamilyPlacementType.WorkPlaneBased: mode = host != null ? "face" : "point"; break;
                    case FamilyPlacementType.CurveBased: mode = "line"; break;
                    case FamilyPlacementType.OneLevelBased:
                    case FamilyPlacementType.TwoLevelsBased: mode = "point"; break;
                    default:
                        throw new ToolException("PLACEMENT_NOT_SUPPORTED",
                            $"'{LookupService.FullName(symbol)}' is a {placementType} family, which this tool does not place.",
                            placementType == FamilyPlacementType.ViewBased
                                ? "Detail/annotation families go in a view, not on a level."
                                : "Use a point, hosted, face-based or line-based family.");
                }
            }
            if (mode == "line" && item.End == null)
                throw new ToolException("INVALID_ARGUMENTS", $"'{LookupService.FullName(symbol)}' is line-based: give end (and location as start).");
            if (mode == "host" && host == null)
                throw new ToolException("HOST_REQUIRED", $"'{LookupService.FullName(symbol)}' is a hosted family: give hostId (the wall, floor or ceiling).");
            if (mode == "face" && host == null)
                throw new ToolException("HOST_REQUIRED", "Face placement needs hostId.");

            TypeFinder.Activate(doc, symbol);
            Level level = !string.IsNullOrWhiteSpace(item.Level) ? ctx.Services.Lookup.Level(item.Level) : null;
            if (level == null && host != null) level = doc.GetElement(host.LevelId) as Level;
            if (level == null && mode != "face")
                throw new ToolException("INVALID_ARGUMENTS", "level is required (by name or id).");

            double angle = ArchGeometry.Rad(item.Rotation);
            var p = new XYZ(ArchGeometry.Ft(item.Location.X), ArchGeometry.Ft(item.Location.Y), 0);
            FamilyInstance instance;
            bool rotateAfter = true;
            bool setOffset = true;
            string faceInfo = null;

            switch (mode)
            {
                case "point":
                {
                    var at = new XYZ(p.X, p.Y, level.Elevation);
                    instance = doc.Create.NewFamilyInstance(at, symbol, level, StructuralType.NonStructural);
                    break;
                }
                case "host":
                {
                    var at = new XYZ(p.X, p.Y, level.Elevation + ArchGeometry.Ft(item.Offset));
                    if (host is Wall wall)
                    {
                        var curve = ((LocationCurve)wall.Location).Curve;
                        var proj = curve.Project(new XYZ(p.X, p.Y, curve.GetEndPoint(0).Z));
                        at = new XYZ(proj.XYZPoint.X, proj.XYZPoint.Y, at.Z);
                        rotateAfter = false;
                    }
                    instance = doc.Create.NewFamilyInstance(at, symbol, host, level, StructuralType.NonStructural);
                    break;
                }
                case "face":
                {
                    var face = PickFace(host, item, level);
                    var onFace = face.Point;
                    var n = face.Normal;
                    XYZ refDir = Math.Abs(n.Z) > 0.9
                        ? new XYZ(Math.Cos(angle), Math.Sin(angle), 0)
                        : XYZ.BasisZ.CrossProduct(n).Normalize();
                    if (Math.Abs(n.Z) > 0.9) rotateAfter = false;
                    instance = doc.Create.NewFamilyInstance(face.Reference, onFace, refDir, symbol);
                    setOffset = false;
                    faceInfo = face.Side;
                    break;
                }
                case "line":
                {
                    var z = level?.Elevation ?? 0;
                    var a = new XYZ(p.X, p.Y, z);
                    var b = new XYZ(ArchGeometry.Ft(item.End.X), ArchGeometry.Ft(item.End.Y), z);
                    if (a.DistanceTo(b) <= doc.Application.ShortCurveTolerance)
                        throw new ToolException("INVALID_GEOMETRY", "location and end are too close for a line-based instance.");
                    var line = Line.CreateBound(a, b);
                    if (host != null)
                    {
                        var face = PickFace(host, item, level);
                        var fa = face.Face.Project(a)?.XYZPoint ?? a;
                        var fb = face.Face.Project(b)?.XYZPoint ?? b;
                        instance = doc.Create.NewFamilyInstance(face.Reference, Line.CreateBound(fa, fb), symbol);
                        setOffset = false;
                        faceInfo = face.Side;
                    }
                    else instance = doc.Create.NewFamilyInstance(line, symbol, level, StructuralType.NonStructural);
                    rotateAfter = false;
                    break;
                }
                default:
                    throw new ToolException("INVALID_ARGUMENTS", $"Unknown placement '{mode}'.");
            }

            if (instance == null)
                throw new ToolException("CREATE_FAILED", "Revit did not place the instance (NewFamilyInstance returned nothing).");
            doc.Regenerate();

            if (setOffset && mode != "host" && Math.Abs(item.Offset) > 1e-9)
                SetElevation(instance, ArchGeometry.Ft(item.Offset));
            if (rotateAfter && Math.Abs(angle) > 1e-9 && instance.Location is LocationPoint lp)
            {
                var axis = Line.CreateBound(lp.Point, lp.Point + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, instance.Id, axis, angle);
            }
            else if (!rotateAfter && Math.Abs(angle) > 1e-9 && mode == "host")
                ctx.Warnings.Add(new ToolWarning($"Item {index}: rotation is ignored for wall-hosted instances (they follow the wall).",
                    new List<long> { instance.Id.GetValue() }));
            if (item.FlipFacing)
            {
                if (!instance.CanFlipFacing) throw new ToolException("FLIP_NOT_SUPPORTED", "This family cannot flip its facing.");
                instance.flipFacing();
            }
            if (item.FlipHand)
            {
                if (!instance.CanFlipHand) throw new ToolException("FLIP_NOT_SUPPORTED", "This family cannot flip its hand.");
                instance.flipHand();
            }
            var parameters = ParamWriter.Apply(ctx, instance, item.Parameters);
            doc.Regenerate();
            if (!instance.IsValidObject)
                throw new ToolException("CREATE_FAILED", "Revit removed the instance right after placing it.");

            return ReadBack(instance, index, mode, faceInfo, parameters);
        }

        public static object ReadBack(FamilyInstance fi, int index, string mode, string face, object parameters)
        {
            var doc = fi.Document;
            XYZ at = (fi.Location as LocationPoint)?.Point;
            var curve = (fi.Location as LocationCurve)?.Curve;
            var level = doc.GetElement(fi.LevelId) as Level;
            return new
            {
                index,
                placed = true,
                id = fi.Id.GetValue(),
                familyType = LookupService.FullName(fi.Symbol),
                category = fi.Category?.Name,
                placement = mode,
                face,
                level = level?.Name,
                hostId = fi.Host?.Id.GetValue(),
                location = at == null ? null : ArchGeometry.ToWire(at),
                start = curve == null ? null : ArchGeometry.ToWire(curve.GetEndPoint(0)),
                end = curve == null ? null : ArchGeometry.ToWire(curve.GetEndPoint(1)),
                rotationDeg = (fi.Location as LocationPoint) != null ? SafeRotation((LocationPoint)fi.Location) : (double?)null,
                elevationFromLevelMm = ElevationMm(fi),
                facingFlipped = fi.FacingFlipped,
                handFlipped = fi.HandFlipped,
                parameters
            };
        }

        private static double? SafeRotation(LocationPoint lp)
        {
            try { return ArchGeometry.Deg(lp.Rotation); }
            catch { return null; }
        }

        private static readonly BuiltInParameter[] ElevationParams =
        {
            BuiltInParameter.INSTANCE_ELEVATION_PARAM,
            BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM,
            BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM
        };

        public static double? ElevationMm(FamilyInstance fi)
        {
            foreach (var bip in ElevationParams)
            {
                var v = ArchGeometry.DoubleParam(fi, bip);
                if (!double.IsNaN(v)) return ArchGeometry.Mm(v);
            }
            return null;
        }

        public static void SetElevation(FamilyInstance fi, double feet)
        {
            foreach (var bip in ElevationParams)
            {
                var p = fi.get_Parameter(bip);
                if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) continue;
                p.Set(feet);
                if (Math.Abs(p.AsDouble() - feet) > 1e-6)
                    throw new ToolException("PARAMETER_NOT_APPLIED", $"Revit kept the elevation at {ArchGeometry.Fmt(ArchGeometry.Mm(p.AsDouble()))}.");
                return;
            }
            throw new ToolException("PARAMETER_NOT_FOUND", "This instance has no writable elevation/offset parameter, so offset cannot be applied.",
                "Place it with offset 0, or use a family that has 'Elevation from Level'.");
        }

        public sealed class PickedFace
        {
            public PlanarFace Face { get; set; }
            public Reference Reference { get; set; }
            public XYZ Point { get; set; }
            public XYZ Normal { get; set; }
            public string Side { get; set; }
        }

        /// <summary>The planar face of the host under/over/beside the requested point.</summary>
        public static PickedFace PickFace(Element host, FamilyInstanceItem item, Level level)
        {
            var side = (item.FaceSide ?? "auto").ToLowerInvariant();
            if (side == "auto")
            {
                if (host is Ceiling) side = "bottom";
                else if (host is Floor || host is RoofBase) side = "top";
                else if (host is Wall) side = "side";
                else side = "nearest";
            }
            var x = ArchGeometry.Ft(item.Location.X);
            var y = ArchGeometry.Ft(item.Location.Y);
            double? z = item.Location.Z.HasValue ? ArchGeometry.Ft(item.Location.Z.Value) : (double?)null;

            var options = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
            var geometry = host.get_Geometry(options);
            if (geometry == null)
                throw new ToolException("NO_FACE", $"Element {host.Id.GetValue()} has no geometry to place on.");

            PickedFace best = null;
            double bestDist = double.MaxValue;
            foreach (var solid in Solids(geometry))
            {
                foreach (Face f in solid.Faces)
                {
                    if (!(f is PlanarFace pf) || pf.Reference == null) continue;
                    var n = pf.FaceNormal;
                    if (side == "bottom" && n.Z > -0.9) continue;
                    if (side == "top" && n.Z < 0.9) continue;
                    if (side == "side" && Math.Abs(n.Z) > 0.1) continue;
                    // A point at the face's height (when z is unknown) or the given z.
                    var probeZ = z ?? (Math.Abs(n.Z) > 0.9 ? pf.Origin.Z : (level?.Elevation ?? pf.Origin.Z) + ArchGeometry.Ft(item.Offset));
                    var probe = new XYZ(x, y, probeZ);
                    var proj = pf.Project(probe);
                    if (proj == null) continue;
                    var d = proj.Distance;
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = new PickedFace { Face = pf, Reference = pf.Reference, Point = proj.XYZPoint, Normal = n, Side = side };
                    }
                }
            }
            if (best == null)
                throw new ToolException("NO_FACE",
                    $"No {side} face of element {host.Id.GetValue()} lies over/under the point ({ArchGeometry.Fmt(item.Location.X)}, {ArchGeometry.Fmt(item.Location.Y)}).",
                    "Check that the point is inside the host's outline, or pass faceSide.");
            return best;
        }

        private static IEnumerable<Solid> Solids(GeometryElement geometry)
        {
            foreach (var obj in geometry)
            {
                if (obj is Solid s && s.Faces.Size > 0) yield return s;
            }
        }
    }
}
