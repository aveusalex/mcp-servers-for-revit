using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Views
{
    public sealed class SetViewCropArgs
    {
        [JsonProperty("views", Required = Required.Always)]
        [Description("Views (names or ids), up to 200.")]
        [MinLength(1)]
        [MaxLength(200)]
        public List<string> Views { get; set; }

        [JsonProperty("cropActive")]
        [Description("Turn the crop region on or off.")]
        public bool? CropActive { get; set; }

        [JsonProperty("cropVisible")]
        [Description("Show or hide the crop region boundary.")]
        public bool? CropVisible { get; set; }

        [JsonProperty("annotationCropActive")]
        [Description("Turn the annotation crop on or off (plans, sections, elevations).")]
        public bool? AnnotationCropActive { get; set; }

        [JsonProperty("rect")]
        [Description("New crop rectangle in model XY (mm). Plans only (for other views use fitElementIds). Turns the crop on.")]
        public RectXY Rect { get; set; }

        [JsonProperty("fitElementIds")]
        [Description("Crop each view to these elements' extents as seen in the view, plus padding. Turns the crop on.")]
        public List<string> FitElementIds { get; set; }

        [JsonProperty("padding")]
        [Description("Margin around fitElementIds, in mm (default 300).")]
        [Unit("mm")]
        [DefaultValue(300.0)]
        public double Padding { get; set; } = 300;

        [JsonProperty("scopeBox")]
        [Description("Scope box name or id to assign (it then drives the crop), or \"none\" to remove it.")]
        public string ScopeBox { get; set; }

        [JsonProperty("atomic")]
        [Description("true (default): any view that fails rolls the whole call back. false: failures are reported per view.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;
    }

    /// <summary>VIS-10: crop on/off/visible, by rectangle or elements, or by scope box.</summary>
    [McpTool("set_view_crop", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views",
        Description = "Set the crop of up to 200 views: cropActive, cropVisible, annotationCropActive, a model-XY " +
                      "rectangle in mm (plans), or fitElementIds + padding mm (any view; projected through the view's " +
                      "crop transform), or a scope box (\"none\" removes it; a scope box overrides a manual crop). " +
                      "Returns the crop before and after, read back. Atomic by default. Undo: Ctrl+Z.")]
    public sealed class SetViewCropTool : McpTool<SetViewCropArgs>
    {
        protected override void Validate(SetViewCropArgs args)
        {
            var hasRect = args.Rect?.Min != null && args.Rect.Max != null;
            var hasFit = args.FitElementIds != null && args.FitElementIds.Count > 0;
            if (hasRect && hasFit) throw new ToolException("INVALID_ARGUMENTS", "Pass rect or fitElementIds, not both.");
            if (!hasRect && !hasFit && args.CropActive == null && args.CropVisible == null && args.AnnotationCropActive == null &&
                string.IsNullOrWhiteSpace(args.ScopeBox))
                throw new ToolException("INVALID_ARGUMENTS", "Nothing to change.");
            if ((hasRect || hasFit) && args.ScopeBox != null && !ViewSupport.IsNone(args.ScopeBox))
                throw new ToolException("INVALID_ARGUMENTS", "A scope box drives the crop; don't pass rect/fitElementIds with it.");
            if (hasRect && (args.Rect.Max.X <= args.Rect.Min.X || args.Rect.Max.Y <= args.Rect.Min.Y))
                throw new ToolException("INVALID_ARGUMENTS", "rect.max must be above and right of rect.min.");
        }

        protected override object Run(ToolContext ctx, SetViewCropArgs args)
        {
            var corners = args.FitElementIds != null && args.FitElementIds.Count > 0
                ? ViewSupport.ElementCorners(ctx, args.FitElementIds)
                : null;
            var r = ItemLoop.Run(ctx, args.Views.Distinct().ToList(), !args.Atomic, (reference, index) =>
            {
                var view = ctx.Services.Lookup.View(reference);
                if (view is ViewSheet || view is ViewSchedule || view.ViewType == ViewType.Legend || view.ViewType == ViewType.DraftingView)
                    throw new ToolException("CROP_NOT_SUPPORTED", $"'{view.Name}' ({view.ViewType}) has no crop region.");
                var before = ViewSupport.DescribeCrop(view);

                if (!string.IsNullOrWhiteSpace(args.ScopeBox)) ViewSupport.SetScopeBox(ctx, view, args.ScopeBox);
                if (args.CropActive.HasValue) Set(view, () => view.CropBoxActive = args.CropActive.Value, () => view.CropBoxActive == args.CropActive.Value, "cropActive");

                if (args.Rect != null || corners != null)
                {
                    var scope = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)?.AsElementId();
                    if (scope != null && scope != ElementId.InvalidElementId)
                        throw new ToolException("CROP_LOCKED_BY_SCOPE_BOX", $"'{view.Name}' is cropped by a scope box.",
                            "Pass scopeBox:\"none\" together with the new crop.");
                    var points = corners;
                    if (args.Rect != null)
                    {
                        if (!(view is ViewPlan plan))
                            throw new ToolException("INVALID_ARGUMENTS", $"rect is for plans; '{view.Name}' is {view.ViewType}. Use fitElementIds.");
                        var z = plan.GenLevel?.Elevation ?? 0.0;
                        points = new List<XYZ>
                        {
                            new XYZ(Mm.ToFt(args.Rect.Min.X), Mm.ToFt(args.Rect.Min.Y), z),
                            new XYZ(Mm.ToFt(args.Rect.Max.X), Mm.ToFt(args.Rect.Max.Y), z),
                            new XYZ(Mm.ToFt(args.Rect.Min.X), Mm.ToFt(args.Rect.Max.Y), z),
                            new XYZ(Mm.ToFt(args.Rect.Max.X), Mm.ToFt(args.Rect.Min.Y), z)
                        };
                    }
                    var wanted = ViewSupport.Project(view.CropBox.Transform, points);
                    var pad = args.Rect != null ? 0 : Mm.ToFt(args.Padding);
                    ViewSupport.CropToPoints(view, points, pad);
                    var got = view.CropBox;
                    if (System.Math.Abs((got.Max.X - got.Min.X) - (wanted.MaxU - wanted.MinU + 2 * pad)) > 0.01 ||
                        System.Math.Abs((got.Max.Y - got.Min.Y) - (wanted.MaxV - wanted.MinV + 2 * pad)) > 0.01)
                        ctx.Warnings.Add(new ToolWarning($"Revit adjusted the crop of '{view.Name}' (template, dependent view or minimum size).",
                            new List<long> { view.Id.GetValue() }));
                }

                if (args.CropVisible.HasValue) Set(view, () => view.CropBoxVisible = args.CropVisible.Value, () => view.CropBoxVisible == args.CropVisible.Value, "cropVisible");
                if (args.AnnotationCropActive.HasValue)
                {
                    var p = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)
                            ?? throw new ToolException("NOT_SUPPORTED", $"'{view.Name}' has no annotation crop.");
                    var value = args.AnnotationCropActive.Value ? 1 : 0;
                    Set(view, () => p.Set(value), () => p.AsInteger() == value, "annotationCropActive");
                }

                return new { index, viewId = view.Id.GetValue(), name = view.Name, before, after = ViewSupport.DescribeCrop(view) };
            }, v => v, "View");

            return new { views = r.Done, failed = r.Failed, dryRun = ctx.DryRun };
        }

        private static void Set(View view, System.Action set, System.Func<bool> ok, string what)
        {
            set();
            if (!ok())
                throw new ToolException("CROP_NOT_APPLIED", $"Revit did not apply {what} to '{view.Name}'.",
                    "A view template, a scope box or the primary view (dependent views) may control it.");
        }
    }
}
