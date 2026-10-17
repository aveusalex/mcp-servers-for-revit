using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Query.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class GetProjectCoordinatesArgs
    {
        [JsonProperty("link")]
        [Description("Optional Revit link (id or name): also return its transform into the host.")]
        public string Link { get; set; }
    }

    /// <summary>CON-23: internal origin, base points, shared coordinates, true north, named locations.</summary>
    [McpTool("get_project_coordinates", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "The model's coordinate systems: internal origin, Project Base Point and Survey Point (position " +
                      "in internal coordinates in mm, their shared values N/S, E/W, elevation in mm, angle to true north " +
                      "in degrees, clipped flag), the active shared location (offset and rotation of the internal origin " +
                      "in shared coordinates), named locations, and site latitude/longitude. Every tool works in internal " +
                      "coordinates in mm; use this to convert. With link: that link's transform into the host (origin mm, " +
                      "rotation deg). Read-only.")]
    public sealed class GetProjectCoordinatesTool : McpTool<GetProjectCoordinatesArgs>
    {
        protected override object Run(ToolContext ctx, GetProjectCoordinatesArgs args)
        {
            var doc = ctx.Doc;
            var result = new Dictionary<string, object>
            {
                ["units"] = "Positions in mm in the internal coordinate system; angles in degrees, counter-clockwise."
            };

            try
            {
                var io = InternalOrigin.Get(doc);
                result["internalOrigin"] = new { id = io.Id.GetValue(), sharedPosition = QueryHelpers.Point(io.SharedPosition) };
            }
            catch
            {
                result["internalOrigin"] = null;
            }

            result["projectBasePoint"] = Describe(SafeGet(() => BasePoint.GetProjectBasePoint(doc)));
            result["surveyPoint"] = Describe(SafeGet(() => BasePoint.GetSurveyPoint(doc)));

            var active = doc.ActiveProjectLocation;
            if (active != null)
            {
                ProjectPosition pos = null;
                try { pos = active.GetProjectPosition(XYZ.Zero); } catch { /* none */ }
                result["activeLocation"] = new
                {
                    id = active.Id.GetValue(),
                    name = active.Name,
                    eastWestMm = pos == null ? (double?)null : QueryHelpers.Mm(pos.EastWest),
                    northSouthMm = pos == null ? (double?)null : QueryHelpers.Mm(pos.NorthSouth),
                    elevationMm = pos == null ? (double?)null : QueryHelpers.Mm(pos.Elevation),
                    angleToTrueNorthDeg = pos == null ? (double?)null : QueryHelpers.Deg(pos.Angle)
                };
            }

            var named = new List<object>();
            foreach (ProjectLocation pl in doc.ProjectLocations)
                named.Add(new { id = pl.Id.GetValue(), name = pl.Name, isActive = active != null && pl.Id == active.Id });
            result["namedLocations"] = named;

            try
            {
                var site = doc.SiteLocation;
                if (site != null)
                {
                    result["site"] = new
                    {
                        placeName = site.PlaceName,
                        latitudeDeg = QueryHelpers.Deg(site.Latitude),
                        longitudeDeg = QueryHelpers.Deg(site.Longitude),
                        timeZoneHours = site.TimeZone,
                        elevationMm = QueryHelpers.Mm(site.Elevation)
                    };
                }
            }
            catch
            {
                result["site"] = null;
            }

            if (!string.IsNullOrWhiteSpace(args.Link))
            {
                var link = LinkSupport.Resolve(doc, args.Link);
                var t = link.GetTotalTransform();
                var linkDoc = link.GetLinkDocument();
                result["link"] = new
                {
                    id = link.Id.GetValue(),
                    name = link.Name,
                    loaded = linkDoc != null,
                    origin = QueryHelpers.Point(t.Origin),
                    rotationDeg = QueryHelpers.Deg(Math.Atan2(t.BasisX.Y, t.BasisX.X)),
                    basisX = QueryHelpers.Vector(t.BasisX),
                    basisY = QueryHelpers.Vector(t.BasisY),
                    basisZ = QueryHelpers.Vector(t.BasisZ),
                    note = "Host point = origin + x*basisX + y*basisY + z*basisZ (link internal coordinates in, host internal out)."
                };
            }

            return result;
        }

        private static object Describe(BasePoint bp)
        {
            if (bp == null) return null;
            double? Read(BuiltInParameter bip, bool angle = false)
            {
                try
                {
                    var p = bp.get_Parameter(bip);
                    if (p == null) return null;
                    return angle ? QueryHelpers.Deg(p.AsDouble()) : QueryHelpers.Mm(p.AsDouble());
                }
                catch
                {
                    return null;
                }
            }

            bool? clipped = null;
            try { clipped = bp.Clipped; } catch { /* n/a */ }
            return new
            {
                id = bp.Id.GetValue(),
                position = QueryHelpers.Point(bp.Position),
                sharedPosition = QueryHelpers.Point(bp.SharedPosition),
                northSouthMm = Read(BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM),
                eastWestMm = Read(BuiltInParameter.BASEPOINT_EASTWEST_PARAM),
                elevationMm = Read(BuiltInParameter.BASEPOINT_ELEVATION_PARAM),
                angleToTrueNorthDeg = bp.IsShared ? null : Read(BuiltInParameter.BASEPOINT_ANGLETON_PARAM, true),
                clipped,
                pinned = bp.Pinned
            };
        }

        private static T SafeGet<T>(Func<T> f) where T : class
        {
            try { return f(); }
            catch { return null; }
        }
    }
}
