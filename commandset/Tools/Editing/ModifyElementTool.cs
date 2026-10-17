using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Tools.Query;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class ModifyParameterItem
    {
        [JsonProperty("parameter", Required = Required.Always)]
        [Description("Parameter reference: {guid} | {builtIn} | {name}.")]
        public ParameterRef Parameter { get; set; }

        [JsonProperty("value", Required = Required.AllowNull)]
        [Description("New value (wire units mm, m2, m3, deg, %; ElementId parameters by id or name; null clears text).")]
        public JToken Value { get; set; }

        [JsonProperty("unit")]
        [Description("Optional unit override for a number (m, cm, mm, ft, in, deg, rad, %, internal...).")]
        public string Unit { get; set; }
    }

    public sealed class ModifyElementArgs
    {
        [JsonProperty("elementId", Required = Required.Always)]
        [Required]
        [Description("The element to edit (id or UniqueId).")]
        public string ElementId { get; set; }

        [JsonProperty("parameters")]
        [Description("Instance parameter values to write (same rules as set_parameters; type parameters are refused).")]
        [MaxLength(100)]
        public List<ModifyParameterItem> Parameters { get; set; }

        [JsonProperty("move")]
        [Description("Displacement in mm.")]
        public PointMm Move { get; set; }

        [JsonProperty("rotate")]
        [Description("Rotation about the vertical axis through the element's location point (or bounding-box centre).")]
        [Unit("deg")]
        public double? Rotate { get; set; }

        [JsonProperty("flipFacing")]
        [Description("Flip the facing orientation (doors, windows, families that allow it).")]
        [DefaultValue(false)]
        public bool FlipFacing { get; set; }

        [JsonProperty("flipHand")]
        [Description("Flip the hand orientation (doors, windows, families that allow it).")]
        [DefaultValue(false)]
        public bool FlipHand { get; set; }

        [JsonProperty("flipWorkPlane")]
        [Description("Flip a work-plane-based family to the other side of its work plane.")]
        [DefaultValue(false)]
        public bool FlipWorkPlane { get; set; }

        [JsonProperty("pinned")]
        [Description("Pin (true) or unpin (false) after the other edits.")]
        public bool? Pinned { get; set; }

        [JsonProperty("allowCritical")]
        [Description("Allow critical parameters (level, phase, workset, host...). Default false.")]
        [DefaultValue(false)]
        public bool AllowCritical { get; set; }
    }

    /// <summary>EDT-03: several edits on ONE element in one transaction (one Ctrl+Z).</summary>
    [McpTool("modify_element", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "editing",
        Description = "Several edits on ONE element in a single transaction (one Ctrl+Z), in this order: flips " +
                      "(facing, hand, work plane), instance parameter writes (set_parameters rules, read back), move " +
                      "(mm), rotate about the vertical axis through the element (degrees), then pin/unpin. A pinned " +
                      "element is unpinned for the move/rotate when pinned:false is given. Everything is rolled back " +
                      "if any step fails. Returns what was applied, the read-back location and revert[] for the " +
                      "parameters. Undo: Ctrl+Z.")]
    public sealed class ModifyElementTool : McpTool<ModifyElementArgs>
    {
        protected override void Validate(ModifyElementArgs a)
        {
            bool any = (a.Parameters?.Count ?? 0) > 0 || a.Move != null || a.Rotate.HasValue || a.FlipFacing || a.FlipHand ||
                       a.FlipWorkPlane || a.Pinned.HasValue;
            if (!any) throw new ToolException("INVALID_ARGUMENTS", "Nothing to do: give parameters, move, rotate, a flip or pinned.");
            if (a.Parameters != null && a.Parameters.Any(p => p?.Parameter == null || p.Parameter.IsEmpty))
                throw new ToolException("INVALID_ARGUMENTS", "Every parameters[] item needs a parameter reference.");
        }

        protected override object Run(ToolContext ctx, ModifyElementArgs a)
        {
            var doc = ctx.Doc;
            var e = ctx.Services.Lookup.Element(a.ElementId);
            var applied = new List<string>();
            var idText = e.Id.GetValue().ToString(CultureInfo.InvariantCulture);

            // 1. flips
            if (a.FlipFacing || a.FlipHand || a.FlipWorkPlane)
            {
                if (!(e is FamilyInstance fi))
                    throw new ToolException("CANNOT_FLIP", $"Element {idText} is not a family instance, so it can't be flipped.");
                if (a.FlipFacing)
                {
                    if (!fi.CanFlipFacing) throw new ToolException("CANNOT_FLIP", "This family does not allow flipping the facing.");
                    var before = fi.FacingFlipped;
                    fi.flipFacing();
                    if (fi.FacingFlipped == before) throw new ToolException("CANNOT_FLIP", "Revit did not flip the facing.");
                    applied.Add("flipFacing");
                }
                if (a.FlipHand)
                {
                    if (!fi.CanFlipHand) throw new ToolException("CANNOT_FLIP", "This family does not allow flipping the hand.");
                    var before = fi.HandFlipped;
                    fi.flipHand();
                    if (fi.HandFlipped == before) throw new ToolException("CANNOT_FLIP", "Revit did not flip the hand.");
                    applied.Add("flipHand");
                }
                if (a.FlipWorkPlane)
                {
                    if (!fi.CanFlipWorkPlane) throw new ToolException("CANNOT_FLIP", "This instance can't be flipped on its work plane.");
                    var before = fi.IsWorkPlaneFlipped;
                    fi.IsWorkPlaneFlipped = !before;
                    if (fi.IsWorkPlaneFlipped == before) throw new ToolException("CANNOT_FLIP", "Revit did not flip the work plane.");
                    applied.Add("flipWorkPlane");
                }
            }

            // 2. parameters
            ParameterWriteResult paramResult = null;
            if (a.Parameters != null && a.Parameters.Count > 0)
            {
                var ps = ctx.Services.Parameters;
                var plan = ps.Plan(a.Parameters.Select(p => new ParameterWriteRequest
                {
                    Element = idText, Parameter = p.Parameter, Value = p.Value, Unit = p.Unit
                }).ToList(), new ParameterPlanOptions { Target = ParameterTarget.Instance, AllowCritical = a.AllowCritical });
                if (plan.HasErrors)
                    throw new ToolException("PARAMETER_WRITE_REFUSED", $"Nothing was changed. {plan.FailureSummary()}",
                        "Fix the parameters (type parameters go through set_type_parameters).", plan.Failed.First().RecommendedTool);
                paramResult = ps.Apply(plan);
                if (paramResult.Failed > 0)
                    throw new ToolException("PARAMETER_WRITE_FAILED", "Nothing was changed: " +
                        string.Join(" ", paramResult.Items.Where(i => i.Status != "ok" && i.Status != "duplicate")
                            .Select(i => $"'{i.Parameter?.Name}': {i.Status} - {i.Message}")));
                applied.Add("parameters");
            }

            // 3-4. move / rotate (unpin first when asked to end unpinned)
            if (a.Move != null || a.Rotate.HasValue)
            {
                if (e.Pinned)
                {
                    if (a.Pinned == false) { e.Pinned = false; applied.Add("unpinned"); }
                    else throw new ToolException("ELEMENT_PINNED", $"Element {idText} is pinned.", "Pass pinned:false to unpin it, or use set_elements_pinned.");
                }
                try
                {
                    if (a.Move != null)
                    {
                        ElementTransformUtils.MoveElement(doc, e.Id, EditGeometry.ToXyz(a.Move));
                        applied.Add("move");
                    }
                    if (a.Rotate.HasValue)
                    {
                        var center = EditGeometry.Anchor(e) ?? throw new ToolException("NO_LOCATION", $"Element {idText} has no location to rotate about.");
                        ElementTransformUtils.RotateElement(doc, e.Id, Line.CreateBound(center, center + XYZ.BasisZ), a.Rotate.Value * Math.PI / 180.0);
                        applied.Add("rotate");
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    throw new ToolException("TRANSFORM_FAILED", "Revit refused the move/rotate: " + ex.Message);
                }
            }

            // 5. pin
            if (a.Pinned.HasValue && e.Pinned != a.Pinned.Value)
            {
                e.Pinned = a.Pinned.Value;
                if (e.Pinned != a.Pinned.Value) throw new ToolException("PIN_FAILED", "Revit did not change the pinned state.");
                applied.Add(a.Pinned.Value ? "pinned" : "unpinned");
            }

            var fam = e as FamilyInstance;
            return new
            {
                elementId = e.Id.GetValue(),
                applied = applied.Distinct(),
                parameters = paramResult?.Items,
                revert = paramResult?.Revert,
                location = EditGeometry.Location(e),
                pinned = e.Pinned,
                facingFlipped = fam?.FacingFlipped,
                handFlipped = fam?.HandFlipped,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + $"Element {idText}: {string.Join(", ", applied.Distinct())}."
            };
        }
    }
}
