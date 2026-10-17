using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Annotation
{
    public sealed class AlignmentReference
    {
        [JsonProperty("elementId", Required = Required.Always)]
        [Description("Element that owns the reference.")]
        public long ElementId { get; set; }

        [JsonProperty("reference")]
        [DefaultValue("auto")]
        [Description("Which reference: " + ReferencePicker.Values + ".")]
        public string Reference { get; set; } = "auto";
    }

    public sealed class CreateLockedAlignmentArgs
    {
        [JsonProperty("view", Required = Required.Always)]
        [Required]
        [Description("Plan, section or elevation where both references are seen edge-on (name or id).")]
        public string View { get; set; }

        [JsonProperty("reference1", Required = Required.Always)]
        [Required]
        [Description("The reference that stays (e.g. a wall's interior face or a reference plane).")]
        public AlignmentReference Reference1 { get; set; }

        [JsonProperty("reference2", Required = Required.Always)]
        [Required]
        [Description("The reference locked to it (e.g. the side of a cabinet).")]
        public AlignmentReference Reference2 { get; set; }
    }

    /// <summary>ANO-13: locked alignment (cadeado) between two references that already coincide.</summary>
    [McpTool("create_locked_alignment", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "annotation",
        Description = "Lock two references together (alinhar com cadeado), e.g. the side of a millwork cabinet to a " +
                      "wall's interior face, or a face to a reference plane, so they move together. References: reference " +
                      "planes, levels, grids, wall exterior/interior, family instance left/right/front/back/top/bottom/" +
                      "centers, or the largest planar face facing +x/-x/+y/-y/+z/-z. Revit only locks references that are " +
                      "already parallel and coincident: when they are apart the tool reports the gap in mm (move the " +
                      "element first, e.g. transform_elements). Undo: Ctrl+Z or delete the alignment.")]
    public sealed class CreateLockedAlignmentTool : McpTool<CreateLockedAlignmentArgs>
    {
        protected override object Run(ToolContext ctx, CreateLockedAlignmentArgs a)
        {
            var doc = ctx.Doc;
            var view = ctx.Services.Lookup.View(a.View);
            if (view is ViewSheet || view is ViewSchedule || view.ViewType == ViewType.DraftingView || view.ViewType == ViewType.Legend)
                throw new ToolException("VIEW_NOT_SUPPORTED", $"'{view.Name}' ({view.ViewType}) shows no model; use a plan, section or elevation.");
            if (a.Reference1.ElementId == a.Reference2.ElementId && string.Equals(a.Reference1.Reference, a.Reference2.Reference, StringComparison.OrdinalIgnoreCase))
                throw new ToolException("INVALID_ARGUMENTS", "The two references are the same.");

            var e1 = ctx.Services.Lookup.Element(a.Reference1.ElementId);
            var e2 = ctx.Services.Lookup.Element(a.Reference2.ElementId);
            var r1 = ReferencePicker.Pick(e1, a.Reference1.Reference);
            var r2 = ReferencePicker.Pick(e2, a.Reference2.Reference);

            double? gapMm = null;
            bool? parallel = null;
            if (r1.Plane != null && r2.Plane != null)
            {
                parallel = Math.Abs(Math.Abs(r1.Plane.Normal.DotProduct(r2.Plane.Normal)) - 1) < 1e-6;
                if (parallel == true)
                    gapMm = Math.Round(Math.Abs((r2.Plane.Origin - r1.Plane.Origin).DotProduct(r1.Plane.Normal)) * 304.8, 1);
            }
            if (parallel == false)
                throw new ToolException("NOT_PARALLEL", $"{r1.Description} and {r2.Description} are not parallel, so they can't be aligned.");
            if (gapMm.HasValue && gapMm.Value > 0.5)
                throw new ToolException("NOT_ALIGNED", $"{r2.Description} is {gapMm.Value} mm away from {r1.Description}; Revit only locks references that already coincide.",
                    "Move the element by that distance first (transform_elements), then lock.", "transform_elements");

            Dimension alignment;
            try
            {
                alignment = doc.IsFamilyDocument
                    ? doc.FamilyCreate.NewAlignment(view, r1.Reference, r2.Reference)
                    : doc.Create.NewAlignment(view, r1.Reference, r2.Reference);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("ALIGNMENT_FAILED", $"Revit could not lock {r2.Description} to {r1.Description} in '{view.Name}': {ex.Message}",
                    "Both references must be seen edge-on in the view (e.g. vertical faces in a plan) and coincide" +
                    (gapMm.HasValue ? "." : "; their distance could not be measured, check that they touch."));
            }
            if (alignment == null)
                throw new ToolException("ALIGNMENT_FAILED", "Revit returned no alignment.");
            try { alignment.IsLocked = true; }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("LOCK_FAILED", $"The alignment was created but Revit refused to lock it: {ex.Message}");
            }
            return new
            {
                alignmentId = alignment.Id.GetValue(),
                locked = alignment.IsLocked,
                view = new { id = view.Id.GetValue(), name = view.Name },
                reference1 = r1.Description,
                reference2 = r2.Description,
                gapMm,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + $"{r2.Description} locked to {r1.Description}."
            };
        }
    }
}
