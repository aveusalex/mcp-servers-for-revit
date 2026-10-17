using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Architecture.Support;

namespace RevitMCPCommandSet.Tools.Architecture
{
    public sealed class SetTrueNorthArgs
    {
        [JsonProperty("angle")]
        [Required]
        [Unit("deg")]
        [Description("Angle between project north and true north in degrees, as Revit stores it in the active project location (ProjectPosition.Angle; positive = counter-clockwise). Check the read-back 'angleToTrueNorthParameterDeg', which is what the Project Base Point shows.")]
        public double Angle { get; set; }

        [JsonProperty("mode")]
        [EnumValues("absolute", "relative")]
        [DefaultValue("absolute")]
        [Description("absolute (default): set the angle to this value. relative: add it to the current angle.")]
        public string Mode { get; set; } = "absolute";
    }

    /// <summary>ARQ-13: rotate true north (the project's position relative to shared coordinates).</summary>
    [McpTool("set_true_north", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "architecture", Batchable = false,
        Description = "Set the angle between project north and true north of the active project location, in degrees " +
                      "(absolute, or relative to the current one). The model does not move; views oriented to True North, " +
                      "sun studies and shared coordinates change. Returns the previous and new angle (and the value the " +
                      "Project Base Point shows). Undo: Ctrl+Z, or call again with the previous angle.")]
    public sealed class SetTrueNorthTool : McpTool<SetTrueNorthArgs>
    {
        protected override void Validate(SetTrueNorthArgs a)
        {
            if (double.IsNaN(a.Angle) || double.IsInfinity(a.Angle) || Math.Abs(a.Angle) > 3600)
                throw new ToolException("INVALID_ARGUMENTS", "angle must be a finite number of degrees.");
        }

        protected override object Run(ToolContext ctx, SetTrueNorthArgs a)
        {
            var doc = ctx.Doc;
            var location = doc.ActiveProjectLocation
                           ?? throw new ToolException("NO_PROJECT_LOCATION", "The document has no active project location.");
            var position = location.GetProjectPosition(XYZ.Zero)
                           ?? throw new ToolException("NO_PROJECT_LOCATION", "Revit returned no project position.");
            double before = position.Angle;
            double target = Normalize(a.Mode == "relative" ? before + ArchGeometry.Rad(a.Angle) : ArchGeometry.Rad(a.Angle));
            double baseBefore = BasePointAngle(doc);

            location.SetProjectPosition(XYZ.Zero, new ProjectPosition(position.EastWest, position.NorthSouth, position.Elevation, target));
            doc.Regenerate();

            var after = location.GetProjectPosition(XYZ.Zero).Angle;
            if (Math.Abs(Normalize(after - target)) > 1e-6)
                throw new ToolException("ANGLE_NOT_APPLIED",
                    $"Revit kept the angle at {ArchGeometry.Deg(after)}° instead of {ArchGeometry.Deg(target)}°.");
            ctx.Warnings.Add(new ToolWarning("Views oriented to True North, sun settings and shared coordinates are affected."));

            return new
            {
                projectLocation = location.Name,
                previousAngleDeg = ArchGeometry.Deg(before),
                angleDeg = ArchGeometry.Deg(after),
                previousAngleToTrueNorthParameterDeg = double.IsNaN(baseBefore) ? (double?)null : ArchGeometry.Deg(baseBefore),
                angleToTrueNorthParameterDeg = double.IsNaN(BasePointAngle(doc)) ? (double?)null : ArchGeometry.Deg(BasePointAngle(doc)),
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"True north angle {ArchGeometry.Deg(before)}° → {ArchGeometry.Deg(after)}° (location '{location.Name}')."
            };
        }

        /// <summary>Wraps to (-π, π], the range Revit accepts.</summary>
        private static double Normalize(double rad)
        {
            while (rad > Math.PI) rad -= 2 * Math.PI;
            while (rad <= -Math.PI) rad += 2 * Math.PI;
            return rad;
        }

        private static double BasePointAngle(Document doc)
        {
            try
            {
                var bp = BasePoint.GetProjectBasePoint(doc);
                var p = bp?.get_Parameter(BuiltInParameter.BASEPOINT_ANGLETON_PARAM);
                return p != null && p.StorageType == StorageType.Double ? p.AsDouble() : double.NaN;
            }
            catch { return double.NaN; }
        }
    }
}
