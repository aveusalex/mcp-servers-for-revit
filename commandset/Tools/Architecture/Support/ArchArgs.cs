using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;

namespace RevitMCPCommandSet.Tools.Architecture.Support
{
    /// <summary>A plan point in millimetres (project internal coordinates, X east / Y north of the internal origin).</summary>
    /// <remarks>No Revit dependency: shared by several architecture tools.</remarks>
    public sealed class PointMm
    {
        [JsonProperty("x")]
        [Required]
        [Unit("mm")]
        [Description("X coordinate in mm from the project's internal origin.")]
        public double X { get; set; }

        [JsonProperty("y")]
        [Required]
        [Unit("mm")]
        [Description("Y coordinate in mm from the project's internal origin.")]
        public double Y { get; set; }
    }

    /// <summary>A 3D point in millimetres.</summary>
    public sealed class Point3Mm
    {
        [JsonProperty("x")]
        [Required]
        [Unit("mm")]
        [Description("X coordinate in mm from the project's internal origin.")]
        public double X { get; set; }

        [JsonProperty("y")]
        [Required]
        [Unit("mm")]
        [Description("Y coordinate in mm from the project's internal origin.")]
        public double Y { get; set; }

        [JsonProperty("z")]
        [Unit("mm")]
        [Description("Z in mm. For level-based placement it is ignored (use offset); for face placement it helps pick the face.")]
        public double? Z { get; set; }
    }

    /// <summary>An axis-aligned (optionally rotated) rectangle given by one corner and its sizes.</summary>
    public sealed class RectangleMm
    {
        [JsonProperty("origin")]
        [Required]
        [Description("Corner the rectangle starts from (before rotation), in mm.")]
        public PointMm Origin { get; set; }

        [JsonProperty("width")]
        [Required]
        [Unit("mm")]
        [Range(1, 1000000)]
        [Description("Size along X (before rotation), in mm.")]
        public double Width { get; set; }

        [JsonProperty("depth")]
        [Required]
        [Unit("mm")]
        [Range(1, 1000000)]
        [Description("Size along Y (before rotation), in mm.")]
        public double Depth { get; set; }

        [JsonProperty("rotation")]
        [Unit("deg")]
        [DefaultValue(0)]
        [Description("Counter-clockwise rotation around the origin corner, in degrees. Default 0.")]
        public double Rotation { get; set; }
    }

    /// <summary>A closed polygon.</summary>
    public sealed class PolygonMm
    {
        [JsonProperty("points")]
        [Required]
        [MinLength(3)]
        [MaxLength(500)]
        [Description("Closed list of plan points in mm (do not repeat the first point).")]
        public List<PointMm> Points { get; set; }
    }

    /// <summary>
    /// The outline of a floor, ceiling or countertop: a polygon (with optional holes) or the boundary of a room.
    /// Exactly one of points / rectangle / room.
    /// </summary>
    public sealed class SurfaceBoundary
    {
        [JsonProperty("points")]
        [MinLength(3)]
        [MaxLength(500)]
        [Description("Closed outline as plan points in mm (the last point connects back to the first; do not repeat the first point).")]
        public List<PointMm> Points { get; set; }

        [JsonProperty("rectangle")]
        [Description("Rectangular outline (alternative to points).")]
        public RectangleMm Rectangle { get; set; }

        [JsonProperty("holes")]
        [MaxLength(100)]
        [Description("Openings inside the outline (shafts, cut-outs). Only with points/rectangle.")]
        public List<PolygonMm> Holes { get; set; }

        [JsonProperty("room")]
        [Description("Room whose boundary is used, by element id, room number or room name (ambiguous names fail with the candidates).")]
        public string Room { get; set; }

        [JsonProperty("boundaryLocation")]
        [EnumValues("finish", "center", "coreBoundary", "coreCenter")]
        [DefaultValue("finish")]
        [Description("With room: which wall line bounds the outline. finish (default) = inner finish face of the walls; center = wall centreline; coreBoundary / coreCenter = wall core.")]
        public string BoundaryLocation { get; set; }

        [JsonProperty("boundaryOffset")]
        [Unit("mm")]
        [DefaultValue(0)]
        [Description("Grows (positive) or shrinks (negative) the whole outline, holes included, in mm. E.g. -10 leaves a 10 mm gap to the walls.")]
        public double BoundaryOffset { get; set; }
    }
}
