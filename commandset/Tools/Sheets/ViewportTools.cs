using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Sheets.Support;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Sheets
{
    // ---- FOL-04 ------------------------------------------------------------------------------------------------------

    public sealed class PlaceOnSheetItem
    {
        [JsonProperty("sheet", Required = Required.Always)]
        [Description("Sheet number, name or id.")]
        public string Sheet { get; set; }

        [JsonProperty("view", Required = Required.Always)]
        [Description("View, legend or schedule to place (name or id).")]
        public string View { get; set; }

        [JsonProperty("x", Required = Required.Always)]
        [Description("X of the anchor point on the sheet, paper mm (sheet coordinates).")]
        [Unit("mm")]
        public double X { get; set; }

        [JsonProperty("y", Required = Required.Always)]
        [Description("Y of the anchor point on the sheet, paper mm (sheet coordinates).")]
        [Unit("mm")]
        public double Y { get; set; }

        [JsonProperty("anchor")]
        [Description("Which point of the placed box lands on x/y: center (default for views), top_left (default for schedules) or bottom_left.")]
        [EnumValues("center", "top_left", "bottom_left")]
        public string Anchor { get; set; }

        [JsonProperty("viewportType")]
        [Description("Viewport type name or id (views only), e.g. a type without title.")]
        public string ViewportType { get; set; }
    }

    public sealed class PlaceOnSheetArgs
    {
        [JsonProperty("items", Required = Required.Always)]
        [Description("Placements, up to 200 per call.")]
        [MinLength(1)]
        [MaxLength(200)]
        public List<PlaceOnSheetItem> Items { get; set; }

        [JsonProperty("continueOnError")]
        [Description("false (default): any failure rolls back every placement. true: failed ones are listed in failed[].")]
        [DefaultValue(false)]
        public bool ContinueOnError { get; set; }
    }

    [McpTool("place_on_sheet", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "sheets",
        Description = "Place views and legends (as viewports) and schedules (as schedule instances) on sheets at x/y " +
                      "in paper mm, anchored by center, top_left or bottom_left of the placed box, with an optional " +
                      "viewport type. Checks Viewport.CanAddViewToSheet (a view can be on one sheet only; legends and " +
                      "schedules on many). Final positions are read back. Undo: Ctrl+Z or delete the returned ids.")]
    public sealed class PlaceOnSheetTool : McpTool<PlaceOnSheetArgs>
    {
        protected override void Validate(PlaceOnSheetArgs args)
        {
            foreach (var i in args.Items)
            {
                if (i == null) throw new ToolException("INVALID_ARGUMENTS", "items must not contain null.");
                i.Anchor = i.Anchor?.Trim().ToLowerInvariant();
            }
        }

        protected override object Run(ToolContext ctx, PlaceOnSheetArgs args)
        {
            var doc = ctx.Doc;
            var r = ItemLoop.Run(ctx, args.Items, args.ContinueOnError, (item, index) =>
            {
                var sheet = SheetSupport.Sheet(ctx, item.Sheet);
                if (sheet.IsPlaceholder)
                    throw new ToolException("SHEET_IS_PLACEHOLDER", $"Sheet {sheet.SheetNumber} is a placeholder; nothing can be placed on it.");
                var view = ctx.Services.Lookup.View(item.View);
                if (view is ViewSheet)
                    throw new ToolException("VIEW_NOT_PLACEABLE", $"'{view.Name}' is a sheet.");
                var target = new XYZ(Mm.ToFt(item.X), Mm.ToFt(item.Y), 0);

                if (view is ViewSchedule schedule)
                {
                    if (!string.IsNullOrWhiteSpace(item.ViewportType))
                        throw new ToolException("INVALID_ARGUMENTS", "viewportType applies to views, not schedules.");
                    if (schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule)
                        throw new ToolException("VIEW_NOT_PLACEABLE", $"Schedule '{schedule.Name}' cannot be placed on a sheet.");
                    var instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, target);
                    doc.Regenerate();
                    var anchor = item.Anchor ?? "top_left";
                    if (anchor != "top_left")
                    {
                        var bb = instance.get_BoundingBox(sheet);
                        if (bb != null)
                        {
                            var at = anchor == "center" ? (bb.Min + bb.Max) / 2 : new XYZ(bb.Min.X, bb.Min.Y, 0);
                            instance.Point = instance.Point + new XYZ(target.X - at.X, target.Y - at.Y, 0);
                            doc.Regenerate();
                        }
                    }
                    return new
                    {
                        index,
                        sheet = sheet.SheetNumber,
                        view = schedule.Name,
                        kind = "schedule",
                        scheduleInstanceId = instance.Id.GetValue(),
                        point = Mm.Point2(instance.Point),
                        extents = SheetSupport.Rect(instance.get_BoundingBox(sheet))
                    };
                }

                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    throw new ToolException("VIEW_NOT_PLACEABLE",
                        $"'{view.Name}' cannot be placed on {sheet.SheetNumber} (already on a sheet, a template, or an unsupported view type).",
                        "Duplicate the view first (duplicate_view), or remove it from its current sheet.", "duplicate_view");
                var vp = Viewport.Create(doc, sheet.Id, view.Id, target)
                         ?? throw new ToolException("VIEWPORT_NOT_CREATED", $"Revit did not place '{view.Name}'.");
                if (!string.IsNullOrWhiteSpace(item.ViewportType))
                {
                    var types = vp.GetValidTypes().Select(id => doc.GetElement(id) as ElementType).Where(t => t != null).ToList();
                    var type = ctx.Services.Lookup.ByNameOrId(item.ViewportType, types, "viewport type");
                    vp.ChangeTypeId(type.Id);
                    if (vp.GetTypeId() != type.Id)
                        throw new ToolException("VIEWPORT_TYPE_NOT_APPLIED", $"Revit kept the viewport type of '{view.Name}'.");
                }
                doc.Regenerate();
                var a = item.Anchor ?? "center";
                if (a != "center")
                {
                    var o = vp.GetBoxOutline();
                    var at = a == "top_left" ? new XYZ(o.MinimumPoint.X, o.MaximumPoint.Y, 0) : new XYZ(o.MinimumPoint.X, o.MinimumPoint.Y, 0);
                    vp.SetBoxCenter(vp.GetBoxCenter() + new XYZ(target.X - at.X, target.Y - at.Y, 0));
                    doc.Regenerate();
                }
                return new
                {
                    index,
                    sheet = sheet.SheetNumber,
                    view = view.Name,
                    kind = view.ViewType == ViewType.Legend ? "legend" : "view",
                    viewportId = vp.Id.GetValue(),
                    viewportType = doc.GetElement(vp.GetTypeId())?.Name,
                    center = Mm.Point2(vp.GetBoxCenter()),
                    box = SheetSupport.Rect(vp.GetBoxOutline())
                };
            }, i => $"{i.View} -> {i.Sheet}", "Placement");
            return new { placed = r.Done, failed = r.Failed, placedCount = r.Done.Count, failedCount = r.Failed.Count, dryRun = ctx.DryRun };
        }
    }

    // ---- FOL-06 ------------------------------------------------------------------------------------------------------

    public sealed class AlignViewportsArgs
    {
        [JsonProperty("reference", Required = Required.Always)]
        [Description("Reference viewport: viewport id, or the name/id of a view placed on exactly one sheet.")]
        public string Reference { get; set; }

        [JsonProperty("targets", Required = Required.Always)]
        [Description("Viewports to move (same forms as reference); may be on other sheets. Up to 200.")]
        [MinLength(1)]
        [MaxLength(200)]
        public List<string> Targets { get; set; }

        [JsonProperty("mode", Required = Required.Always)]
        [Description("same_model_point (same model point at the same paper position: aligns plans across sheets), left, right, top, bottom (box edges), center_h (same X centre), center_v (same Y centre) or center.")]
        [EnumValues("same_model_point", "left", "right", "top", "bottom", "center_h", "center_v", "center")]
        public string Mode { get; set; }
    }

    [McpTool("align_viewports", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "sheets",
        Description = "Align viewports to a reference viewport, on the same sheet or across sheets. same_model_point " +
                      "puts the same model point at the same paper position (typical for stacked floor plans; warns " +
                      "on different scales or orientations); left/right/top/bottom/center_h/center_v/center align the " +
                      "viewport boxes (box outline in paper mm). Each move is read back. Undo: Ctrl+Z.")]
    public sealed class AlignViewportsTool : McpTool<AlignViewportsArgs>
    {
        protected override void Validate(AlignViewportsArgs args)
        {
            args.Mode = args.Mode.ToLowerInvariant();
        }

        protected override object Run(ToolContext ctx, AlignViewportsArgs args)
        {
            var doc = ctx.Doc;
            var reference = SheetSupport.Viewport(ctx, args.Reference);
            var refView = doc.GetElement(reference.ViewId) as View;
            var refBox = reference.GetBoxOutline();
            XYZ modelPoint = null, refSheetPoint = null;
            if (args.Mode == "same_model_point")
            {
                var cb = refView.CropBox;
                modelPoint = cb.Transform.OfPoint((cb.Min + cb.Max) / 2);
                refSheetPoint = ToSheet(reference, refView, modelPoint);
            }

            var moved = new List<object>();
            foreach (var t in args.Targets.Distinct())
            {
                var vp = SheetSupport.Viewport(ctx, t);
                if (vp.Id == reference.Id) continue;
                var view = doc.GetElement(vp.ViewId) as View;
                var before = vp.GetBoxCenter();
                var box = vp.GetBoxOutline();
                XYZ delta;
                switch (args.Mode)
                {
                    case "same_model_point":
                        if (ViewSupport.SafeScale(view) != ViewSupport.SafeScale(refView))
                            ctx.Warnings.Add(new ToolWarning($"'{view.Name}' is 1:{ViewSupport.SafeScale(view)} and the reference 1:{ViewSupport.SafeScale(refView)}; only the chosen point lines up.",
                                new List<long> { vp.Id.GetValue() }));
                        if (!view.ViewDirection.IsAlmostEqualTo(refView.ViewDirection))
                            ctx.Warnings.Add(new ToolWarning($"'{view.Name}' looks in a different direction than the reference.", new List<long> { vp.Id.GetValue() }));
                        delta = refSheetPoint - ToSheet(vp, view, modelPoint);
                        break;
                    case "left": delta = new XYZ(refBox.MinimumPoint.X - box.MinimumPoint.X, 0, 0); break;
                    case "right": delta = new XYZ(refBox.MaximumPoint.X - box.MaximumPoint.X, 0, 0); break;
                    case "top": delta = new XYZ(0, refBox.MaximumPoint.Y - box.MaximumPoint.Y, 0); break;
                    case "bottom": delta = new XYZ(0, refBox.MinimumPoint.Y - box.MinimumPoint.Y, 0); break;
                    case "center_h": delta = new XYZ(Mid(refBox).X - Mid(box).X, 0, 0); break;
                    case "center_v": delta = new XYZ(0, Mid(refBox).Y - Mid(box).Y, 0); break;
                    default: delta = new XYZ(Mid(refBox).X - Mid(box).X, Mid(refBox).Y - Mid(box).Y, 0); break;
                }
                delta = new XYZ(delta.X, delta.Y, 0);
                if (vp.Pinned)
                    throw new ToolException("VIEWPORT_PINNED", $"Viewport of '{view.Name}' is pinned.", "Unpin it first (set_elements_pinned).", "set_elements_pinned");
                vp.SetBoxCenter(before + delta);
                var after = vp.GetBoxCenter();
                if (after.DistanceTo(before + delta) > 1e-4)
                    throw new ToolException("VIEWPORT_NOT_MOVED", $"Revit did not move the viewport of '{view.Name}' as requested.");
                moved.Add(new
                {
                    viewportId = vp.Id.GetValue(),
                    view = view.Name,
                    sheet = (doc.GetElement(vp.SheetId) as ViewSheet)?.SheetNumber,
                    before = Mm.Point2(before),
                    after = Mm.Point2(after),
                    moved = new { x = Mm.FromFt(delta.X), y = Mm.FromFt(delta.Y) }
                });
            }
            if (moved.Count == 0) throw new ToolException("INVALID_ARGUMENTS", "No target other than the reference.");
            return new { reference = new { viewportId = reference.Id.GetValue(), view = refView.Name }, mode = args.Mode, moved, dryRun = ctx.DryRun };
        }

        private static XYZ Mid(Outline o) => (o.MinimumPoint + o.MaximumPoint) / 2;

        private static XYZ ToSheet(Viewport vp, View view, XYZ model)
        {
#if !REVIT2023_OR_GREATER
            // Revit 2022 has no model-to-sheet transforms: map through the crop box, assuming the viewport box is
            // centred on the crop region (true when the crop is on and no annotation sticks out of it).
            var cb = view.CropBox;
            var local = cb.Transform.Inverse.OfPoint(model);
            var mid = (cb.Min + cb.Max) / 2;
            var scale = ViewSupport.SafeScale(view) ?? 1;
            var c = vp.GetBoxCenter();
            return new XYZ(c.X + (local.X - mid.X) / scale, c.Y + (local.Y - mid.Y) / scale, 0);
#else
            var transforms = view.GetModelToProjectionTransforms();
            if (transforms == null || transforms.Count == 0)
                throw new ToolException("NOT_SUPPORTED", $"'{view.Name}' has no model-to-paper transform (legend or drafting view?).",
                    "Use an edge/centre mode instead.");
            var projected = transforms[0].GetModelToProjectionTransform().OfPoint(model);
            var sheet = vp.GetProjectionToSheetTransform().OfPoint(projected);
            return new XYZ(sheet.X, sheet.Y, 0);
#endif
        }
    }
}
