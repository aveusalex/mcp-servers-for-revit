using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class WallAttachArg
    {
        [JsonProperty("side", Required = Required.Always)]
        [Description("top or base of the wall.")]
        [EnumValues("top", "base")]
        public string Side { get; set; }

        [JsonProperty("targetId")]
        [Description("Floor, roof, ceiling (or other valid target) to attach to / detach from. For detach, omit to detach every target on that side.")]
        public long? TargetId { get; set; }
    }

    public sealed class SetWallBaseTopArgs
    {
        [JsonProperty("wallIds", Required = Required.Always)]
        [Required]
        [MinLength(1)]
        [MaxLength(1000)]
        [Description("Walls to change.")]
        public List<long> WallIds { get; set; }

        [JsonProperty("baseLevel")]
        [Description("Base constraint level (name or id).")]
        public string BaseLevel { get; set; }

        [JsonProperty("baseOffset")]
        [Description("Base offset from the base level.")]
        [Unit("mm")]
        public double? BaseOffset { get; set; }

        [JsonProperty("topLevel")]
        [Description("Top constraint level (name or id), or \"unconnected\" to use unconnectedHeight.")]
        public string TopLevel { get; set; }

        [JsonProperty("topOffset")]
        [Description("Top offset from the top level.")]
        [Unit("mm")]
        public double? TopOffset { get; set; }

        [JsonProperty("unconnectedHeight")]
        [Description("Height when the top is unconnected.")]
        [Unit("mm")]
        public double? UnconnectedHeight { get; set; }

        [JsonProperty("attach")]
        [Description("Attach the top or base to a floor/roof/ceiling (Revit 2025 or newer).")]
        public WallAttachArg Attach { get; set; }

        [JsonProperty("detach")]
        [Description("Detach the top or base (Revit 2025 or newer).")]
        public WallAttachArg Detach { get; set; }
    }

    /// <summary>EDT-10: the dedicated path for wall base/top constraints (blocked as critical in set_parameters).</summary>
    [McpTool("set_wall_base_top", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "editing",
        Description = "Set the base and top of walls: base level + offset, top level + offset or \"unconnected\" with a " +
                      "height (all in mm, levels by name or id), and attach/detach the top or base to floors, roofs or " +
                      "ceilings (attach needs Revit 2025+). Checks that the top stays above the base for every wall " +
                      "before changing anything, reads every value back and returns before/after per wall. Changing " +
                      "the base level moves hosted elements with the wall. Undo: Ctrl+Z.")]
    public sealed class SetWallBaseTopTool : McpTool<SetWallBaseTopArgs>
    {
        private const double Tol = 1e-6;

        protected override void Validate(SetWallBaseTopArgs a)
        {
            bool any = a.BaseLevel != null || a.BaseOffset.HasValue || a.TopLevel != null || a.TopOffset.HasValue ||
                       a.UnconnectedHeight.HasValue || a.Attach != null || a.Detach != null;
            if (!any) throw new ToolException("INVALID_ARGUMENTS", "Nothing to change.");
            if (a.UnconnectedHeight.HasValue && a.UnconnectedHeight <= 0)
                throw new ToolException("INVALID_ARGUMENTS", "unconnectedHeight must be positive.");
            if (a.Attach != null && a.Attach.TargetId == null)
                throw new ToolException("INVALID_ARGUMENTS", "attach needs targetId.");
        }

        protected override object Run(ToolContext ctx, SetWallBaseTopArgs a)
        {
            var doc = ctx.Doc;
            var walls = EditGeometry.Elements(doc, a.WallIds).Select(e => e as Wall ?? throw new ToolException("NOT_A_WALL",
                $"Element {e.Id.GetValue()} is not a wall.")).ToList();

            Level baseLevel = a.BaseLevel == null ? null : ctx.Services.Lookup.Level(a.BaseLevel);
            bool unconnected = a.TopLevel != null && a.TopLevel.Trim().Equals("unconnected", StringComparison.OrdinalIgnoreCase);
            Level topLevel = a.TopLevel == null || unconnected ? null : ctx.Services.Lookup.Level(a.TopLevel);

            // Plan and check every wall first.
            var plans = new List<(Wall w, ElementId baseId, double baseOff, ElementId topId, double topOff, double height, object before)>();
            foreach (var w in walls)
            {
                var pBase = Param(w, BuiltInParameter.WALL_BASE_CONSTRAINT);
                var pBaseOff = Param(w, BuiltInParameter.WALL_BASE_OFFSET);
                var pTop = Param(w, BuiltInParameter.WALL_HEIGHT_TYPE);
                var pTopOff = Param(w, BuiltInParameter.WALL_TOP_OFFSET);
                var pHeight = Param(w, BuiltInParameter.WALL_USER_HEIGHT_PARAM);

                var baseId = baseLevel?.Id ?? pBase.AsElementId();
                var baseOff = a.BaseOffset.HasValue ? EditGeometry.Ft(a.BaseOffset.Value) : pBaseOff.AsDouble();
                var topId = unconnected ? ElementId.InvalidElementId : topLevel?.Id ?? pTop.AsElementId();
                var topOff = a.TopOffset.HasValue ? EditGeometry.Ft(a.TopOffset.Value) : pTopOff.AsDouble();
                var height = a.UnconnectedHeight.HasValue ? EditGeometry.Ft(a.UnconnectedHeight.Value) : pHeight.AsDouble();

                var baseElev = Elevation(doc, baseId) + baseOff;
                var topElev = topId == ElementId.InvalidElementId ? baseElev + height : Elevation(doc, topId) + topOff;
                if (topElev <= baseElev + Tol)
                    throw new ToolException("TOP_BELOW_BASE",
                        $"Wall {w.Id.GetValue()}: the top ({EditGeometry.Mm(topElev)} mm) would not be above the base ({EditGeometry.Mm(baseElev)} mm). Nothing was changed.",
                        "Adjust the levels/offsets, or use an unconnected top with a positive height.");
                plans.Add((w, baseId, baseOff, topId, topOff, height, Snapshot(doc, w)));
            }

            var results = new List<object>();
            foreach (var plan in plans)
            {
                var w = plan.w;
                var currentTopElev = TopElevation(doc, w);
                var newBaseElev = Elevation(doc, plan.baseId) + plan.baseOff;
                // Raise the top first when the new base would pass the current top.
                if (newBaseElev >= currentTopElev - Tol) SetTop(doc, w, plan.topId, plan.topOff, plan.height);
                SetId(w, BuiltInParameter.WALL_BASE_CONSTRAINT, plan.baseId);
                SetDouble(w, BuiltInParameter.WALL_BASE_OFFSET, plan.baseOff);
                SetTop(doc, w, plan.topId, plan.topOff, plan.height);
                results.Add(new { wallId = w.Id.GetValue(), before = plan.before, after = Snapshot(doc, w) });
            }

            if (a.Attach != null || a.Detach != null) Attachments(doc, walls, a);

            return new
            {
                changed = results.Count,
                walls = results,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + $"{results.Count} wall(s) updated."
            };
        }

        private static void Attachments(Document doc, List<Wall> walls, SetWallBaseTopArgs a)
        {
#if REVIT2025_OR_GREATER
            foreach (var w in walls)
            {
                if (a.Detach != null)
                {
                    var loc = a.Detach.Side == "base" ? AttachmentLocation.Base : AttachmentLocation.Top;
                    var targets = a.Detach.TargetId.HasValue
                        ? new List<ElementId> { a.Detach.TargetId.Value.ToRevitElementId() }
                        : w.GetAttachmentIds(loc).ToList();
                    foreach (var t in targets) w.RemoveAttachment(t, loc);
                }
                if (a.Attach != null)
                {
                    var loc = a.Attach.Side == "base" ? AttachmentLocation.Base : AttachmentLocation.Top;
                    var target = a.Attach.TargetId.Value.ToRevitElementId();
                    if (doc.GetElement(target) == null)
                        throw new ToolException("ELEMENT_NOT_FOUND", $"Attach target {a.Attach.TargetId} not found.");
                    try
                    {
                        w.AddAttachment(target, loc);
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                    {
                        throw new ToolException("ATTACH_FAILED", $"Wall {w.Id.GetValue()} can't attach to {a.Attach.TargetId}: {ex.Message}");
                    }
                    if (!w.GetAttachmentIds(loc).Contains(target))
                        throw new ToolException("ATTACH_FAILED", $"Revit did not attach wall {w.Id.GetValue()} to {a.Attach.TargetId}.");
                }
            }
#else
            throw new ToolException("NOT_SUPPORTED_IN_VERSION",
                "Attaching or detaching walls through the API needs Revit 2025 or newer.",
                "Ask the user to use Attach Top/Base in Revit, or set levels and offsets instead.");
#endif
        }

        private static void SetTop(Document doc, Wall w, ElementId topId, double topOff, double height)
        {
            SetId(w, BuiltInParameter.WALL_HEIGHT_TYPE, topId);
            if (topId == ElementId.InvalidElementId) SetDouble(w, BuiltInParameter.WALL_USER_HEIGHT_PARAM, height);
            else SetDouble(w, BuiltInParameter.WALL_TOP_OFFSET, topOff);
        }

        private static double TopElevation(Document doc, Wall w)
        {
            var top = Param(w, BuiltInParameter.WALL_HEIGHT_TYPE).AsElementId();
            var baseElev = Elevation(doc, Param(w, BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId()) + Param(w, BuiltInParameter.WALL_BASE_OFFSET).AsDouble();
            return top == ElementId.InvalidElementId
                ? baseElev + Param(w, BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble()
                : Elevation(doc, top) + Param(w, BuiltInParameter.WALL_TOP_OFFSET).AsDouble();
        }

        private static object Snapshot(Document doc, Wall w)
        {
            var top = Param(w, BuiltInParameter.WALL_HEIGHT_TYPE).AsElementId();
            return new
            {
                baseLevel = doc.GetElement(Param(w, BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId())?.Name,
                baseOffsetMm = EditGeometry.Mm(Param(w, BuiltInParameter.WALL_BASE_OFFSET).AsDouble()),
                topLevel = top == ElementId.InvalidElementId ? "unconnected" : doc.GetElement(top)?.Name,
                topOffsetMm = top == ElementId.InvalidElementId ? (double?)null : EditGeometry.Mm(Param(w, BuiltInParameter.WALL_TOP_OFFSET).AsDouble()),
                heightMm = EditGeometry.Mm(Param(w, BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble())
            };
        }

        private static double Elevation(Document doc, ElementId levelId) =>
            (doc.GetElement(levelId) as Level)?.Elevation ?? throw new ToolException("LEVEL_NOT_FOUND", $"Level {levelId.GetValue()} not found.");

        private static Parameter Param(Wall w, BuiltInParameter bip) =>
            w.get_Parameter(bip) ?? throw new ToolException("PARAMETER_NOT_FOUND", $"Wall {w.Id.GetValue()} has no {bip} (stacked or in-place wall?).");

        private static void SetId(Wall w, BuiltInParameter bip, ElementId value)
        {
            var p = Param(w, bip);
            if (p.AsElementId() == value) return;
            if (p.IsReadOnly || !p.Set(value) || p.AsElementId() != value)
                throw new ToolException("SET_FAILED", $"Revit did not accept {bip} on wall {w.Id.GetValue()}.");
        }

        private static void SetDouble(Wall w, BuiltInParameter bip, double value)
        {
            var p = Param(w, bip);
            if (Math.Abs(p.AsDouble() - value) < Tol) return;
            if (p.IsReadOnly)
                throw new ToolException("READ_ONLY", $"{bip} is read-only on wall {w.Id.GetValue()} (attached or constrained?).");
            if (!p.Set(value) || Math.Abs(p.AsDouble() - value) > 1e-6)
                throw new ToolException("SET_FAILED", $"Revit did not keep {bip} = {EditGeometry.Mm(value)} mm on wall {w.Id.GetValue()}.");
        }
    }
}
