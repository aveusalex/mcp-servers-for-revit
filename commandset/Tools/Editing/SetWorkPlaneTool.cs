using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    /// <summary>A direction (unit-less; normalized by the tool).</summary>
    public sealed class DirectionXyz
    {
        [JsonProperty("dx", Required = Required.Always)]
        [Description("X component of the direction.")]
        public double Dx { get; set; }

        [JsonProperty("dy", Required = Required.Always)]
        [Description("Y component of the direction.")]
        public double Dy { get; set; }

        [JsonProperty("dz", Required = Required.Always)]
        [Description("Z component of the direction.")]
        public double Dz { get; set; }
    }

    public sealed class SetWorkPlaneArgs
    {
        [JsonProperty("view", Required = Required.Always)]
        [Required]
        [Description("View whose work plane is set (plan, section, elevation or 3D): name or id. It does not need to be the active view.")]
        public string View { get; set; }

        [JsonProperty("level")]
        [Description("Use this level (name or id) as the work plane.")]
        public string Level { get; set; }

        [JsonProperty("referencePlane")]
        [Description("Use this named reference plane (name or id) as the work plane.")]
        public string ReferencePlane { get; set; }

        [JsonProperty("grid")]
        [Description("Use this (straight) grid (name or id) as the work plane: a vertical plane through it.")]
        public string Grid { get; set; }

        [JsonProperty("elementId")]
        [Description("Use a planar face of this element (with face).")]
        public long? ElementId { get; set; }

        [JsonProperty("face")]
        [Description("With elementId: which face, e.g. face+z (top), face-y, exterior/interior (walls), or a family reference (top, front...).")]
        public string Face { get; set; }

        [JsonProperty("origin")]
        [Description("Free plane through this point (mm; z default 0), with normal, when no datum fits.")]
        public Point3Mm Origin { get; set; }

        [JsonProperty("normal")]
        [Description("Normal of the free plane, e.g. {dx:0, dy:1, dz:0} for a plane facing north.")]
        public DirectionXyz Normal { get; set; }

        [JsonProperty("showWorkPlane")]
        [DefaultValue(false)]
        [Description("Also show the work plane grid in the view (Revit's API can show it but not hide it). Default false.")]
        public bool ShowWorkPlane { get; set; }
    }

    /// <summary>VIS-26: set a view's work plane (the plane model lines and work-plane-based families use).</summary>
    [McpTool("set_work_plane", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "editing",
        Description = "Set the work plane (plano de trabalho) of a view from a level, a named reference plane, a grid, a " +
                      "planar face of an element (face+z = top, exterior/interior of a wall...) or a free plane (origin " +
                      "mm + normal). It is a property of the view stored in the model, so the view does not need to be " +
                      "active; Revit refuses planes that are perpendicular to the view (e.g. a vertical plane in a plan). " +
                      "Optionally shows the plane. Undo: Ctrl+Z.")]
    public sealed class SetWorkPlaneTool : McpTool<SetWorkPlaneArgs>
    {
        protected override void Validate(SetWorkPlaneArgs a)
        {
            int n = (a.Level != null ? 1 : 0) + (a.ReferencePlane != null ? 1 : 0) + (a.Grid != null ? 1 : 0) +
                    (a.ElementId.HasValue ? 1 : 0) + (a.Origin != null || a.Normal != null ? 1 : 0);
            if (n != 1)
                throw new ToolException("INVALID_ARGUMENTS", "Give exactly one of level, referencePlane, grid, elementId (+face) or origin+normal.");
            if ((a.Origin == null) != (a.Normal == null))
                throw new ToolException("INVALID_ARGUMENTS", "A free plane needs both origin and normal.");
            if (a.ElementId.HasValue && string.IsNullOrWhiteSpace(a.Face))
                throw new ToolException("INVALID_ARGUMENTS", "elementId needs face (e.g. face+z, exterior, top).");
        }

        protected override object Run(ToolContext ctx, SetWorkPlaneArgs a)
        {
            var doc = ctx.Doc;
            var view = ctx.Services.Lookup.View(a.View);
            if (view is ViewSchedule || view is ViewSheet || view.ViewType == ViewType.DraftingView || view.ViewType == ViewType.Legend)
                throw new ToolException("VIEW_NOT_SUPPORTED", $"'{view.Name}' ({view.ViewType}) has no model work plane.");

            var before = view.SketchPlane;
            string source;
            SketchPlane plane;
            if (a.Level != null)
            {
                var level = ctx.Services.Lookup.Level(a.Level);
                plane = SketchPlane.Create(doc, level.Id);
                source = $"level '{level.Name}'";
            }
            else if (a.ReferencePlane != null)
            {
                var rp = ctx.Services.Lookup.ByNameOrId(a.ReferencePlane,
                    new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList(), "reference plane");
                plane = SketchPlane.Create(doc, rp.Id);
                source = $"reference plane '{rp.Name}'";
            }
            else if (a.Grid != null)
            {
                var grid = ctx.Services.Lookup.ByNameOrId(a.Grid,
                    new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList(), "grid");
                if (grid.IsCurved) throw new ToolException("NOT_SUPPORTED", $"Grid '{grid.Name}' is curved; only straight grids can be work planes.");
                plane = SketchPlane.Create(doc, grid.Id);
                source = $"grid '{grid.Name}'";
            }
            else if (a.ElementId.HasValue)
            {
                var e = ctx.Services.Lookup.Element(a.ElementId.Value);
                var picked = ReferencePicker.Pick(e, a.Face);
                try
                {
                    plane = SketchPlane.Create(doc, picked.Reference);
                    source = picked.Description;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) when (picked.Plane != null)
                {
                    plane = SketchPlane.Create(doc, picked.Plane);
                    source = picked.Description + " (as a free plane: Revit did not accept the face reference, so it won't follow the face if it moves)";
                }
            }
            else
            {
                var n = new XYZ(a.Normal.Dx, a.Normal.Dy, a.Normal.Dz);
                if (n.IsZeroLength()) throw new ToolException("INVALID_ARGUMENTS", "normal can't be zero.");
                var o = new XYZ(a.Origin.X, a.Origin.Y, a.Origin.Z ?? 0) / 304.8;
                plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(n.Normalize(), o));
                source = "free plane";
            }

            try
            {
                view.SketchPlane = plane;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ArgumentException || ex is Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                throw new ToolException("WORK_PLANE_REJECTED", $"Revit refused {source} as the work plane of '{view.Name}': {ex.Message}",
                    "In plans use a horizontal plane (level, face+z); in sections/elevations use a plane parallel to the view.");
            }
            if (a.ShowWorkPlane) view.ShowActiveWorkPlane();

            var p = plane.GetPlane();
            return new
            {
                view = new { id = view.Id.GetValue(), name = view.Name, viewType = view.ViewType.ToString() },
                sketchPlaneId = plane.Id.GetValue(),
                source,
                origin = new { x = Math.Round(p.Origin.X * 304.8, 1), y = Math.Round(p.Origin.Y * 304.8, 1), z = Math.Round(p.Origin.Z * 304.8, 1) },
                normal = new { x = Math.Round(p.Normal.X, 6), y = Math.Round(p.Normal.Y, 6), z = Math.Round(p.Normal.Z, 6) },
                previousSketchPlaneId = before?.Id.GetValue(),
                workPlaneShown = a.ShowWorkPlane,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + $"Work plane of '{view.Name}' set to {source}."
            };
        }
    }
}
