using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture
{
    public sealed class CreateReferencePlaneArgs
    {
        [JsonProperty("view")]
        [Required]
        [Description("View the plane is drawn in, by name or id (never the active view implicitly). Plans, sections, elevations and 3D views work.")]
        public string View { get; set; }

        [JsonProperty("bubbleEnd")]
        [Required]
        [Description("Bubble end in mm. z defaults to the plan's level elevation (0 in other views).")]
        public Point3Mm BubbleEnd { get; set; }

        [JsonProperty("freeEnd")]
        [Required]
        [Description("Free end in mm. z defaults like bubbleEnd.")]
        public Point3Mm FreeEnd { get; set; }

        [JsonProperty("cutVector")]
        [Description("A direction lying in the plane, perpendicular to bubbleEnd→freeEnd. Default: the view direction (vertical in plans), giving a plane seen edge-on in that view.")]
        public DirectionArg CutVector { get; set; }

        [JsonProperty("name")]
        [Description("Name of the reference plane (must be unique). Optional.")]
        public string Name { get; set; }
    }

    /// <summary>ARQ-17: reference plane in an explicit view.</summary>
    [McpTool("create_reference_plane", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Create a reference plane between two points in mm (bubble end and free end) in an explicit view " +
                      "(name or id, never the active view implicitly). cutVector, a direction in the plane perpendicular " +
                      "to the line, defaults to the view direction, so in a plan you get a vertical plane. Optional unique " +
                      "name. Returns the id, ends and normal read back. Undo: Ctrl+Z.")]
    public sealed class CreateReferencePlaneTool : McpTool<CreateReferencePlaneArgs>
    {
        protected override object Run(ToolContext ctx, CreateReferencePlaneArgs a)
        {
            var doc = ctx.Doc;
            var view = ctx.Services.Lookup.View(a.View);
            if (view is ViewSheet || view.ViewType == ViewType.Schedule || view.ViewType == ViewType.Legend)
                throw new ToolException("INVALID_VIEW", $"'{view.Name}' is a {view.ViewType}; reference planes go in plans, sections, elevations, 3D or drafting views.");

            double defaultZ = (view as ViewPlan)?.GenLevel?.Elevation ?? 0;
            var bubble = new XYZ(ArchGeometry.Ft(a.BubbleEnd.X), ArchGeometry.Ft(a.BubbleEnd.Y),
                a.BubbleEnd.Z.HasValue ? ArchGeometry.Ft(a.BubbleEnd.Z.Value) : defaultZ);
            var free = new XYZ(ArchGeometry.Ft(a.FreeEnd.X), ArchGeometry.Ft(a.FreeEnd.Y),
                a.FreeEnd.Z.HasValue ? ArchGeometry.Ft(a.FreeEnd.Z.Value) : defaultZ);
            var line = free - bubble;
            if (line.GetLength() <= doc.Application.ShortCurveTolerance)
                throw new ToolException("INVALID_GEOMETRY", "bubbleEnd and freeEnd are too close.");

            var cut = a.CutVector != null ? EditGeometry.ToDirection(a.CutVector, "cutVector") : view.ViewDirection;
            if (Math.Abs(cut.Normalize().DotProduct(line.Normalize())) > 1e-6)
                throw new ToolException("INVALID_GEOMETRY",
                    a.CutVector != null
                        ? "cutVector must be perpendicular to the line bubbleEnd→freeEnd."
                        : $"The line is not perpendicular to the view direction of '{view.Name}'; draw it in the view's plane or pass cutVector.");

            if (!string.IsNullOrWhiteSpace(a.Name))
                ctx.Services.Names.EnsureUniqueInClass(typeof(ReferencePlane), a.Name, "reference plane name");

            ReferencePlane rp;
            try
            {
                rp = doc.Create.NewReferencePlane(bubble, free, cut, view);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("CREATE_FAILED", $"Revit refused the reference plane: {ex.Message}");
            }
            if (rp == null) throw new ToolException("CREATE_FAILED", "Revit did not create the reference plane.");
            if (!string.IsNullOrWhiteSpace(a.Name)) ctx.Services.Names.ApplyName(rp, a.Name.Trim(), "reference plane name");

            return new
            {
                id = rp.Id.GetValue(),
                name = rp.Name,
                view = view.Name,
                bubbleEnd = ArchGeometry.ToWire(rp.BubbleEnd),
                freeEnd = ArchGeometry.ToWire(rp.FreeEnd),
                normal = new { dx = Math.Round(rp.Normal.X, 6), dy = Math.Round(rp.Normal.Y, 6), dz = Math.Round(rp.Normal.Z, 6) },
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + $"Created reference plane {rp.Id.GetValue()} in '{view.Name}'."
            };
        }
    }
}
