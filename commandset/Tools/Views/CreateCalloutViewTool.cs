using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Views.Support;

namespace RevitMCPCommandSet.Tools.Views
{
    public sealed class CreateCalloutViewArgs
    {
        [JsonProperty("parentView", Required = Required.Always)]
        [Description("View (name or id) the callout is drawn in: a plan, section, elevation or detail view.")]
        public string ParentView { get; set; }

        [JsonProperty("rect")]
        [Description("Callout rectangle in model XY (mm). Plans only; for sections/elevations use fitElementIds.")]
        public RectXY Rect { get; set; }

        [JsonProperty("fitElementIds")]
        [Description("Element ids to frame instead of rect: the callout covers their extents as seen in the parent view, plus padding.")]
        public List<string> FitElementIds { get; set; }

        [JsonProperty("padding")]
        [Description("Margin around fitElementIds, in mm (default 300).")]
        [Unit("mm")]
        [DefaultValue(300.0)]
        public double Padding { get; set; } = 300;

        [JsonProperty("kind")]
        [Description("plan = callout of the same family as a plan parent (a cropped plan); detail = detail view. Default: plan for plan parents, detail otherwise.")]
        [EnumValues("plan", "detail")]
        public string Kind { get; set; }

        [JsonProperty("viewFamilyType")]
        [Description("View type name or id for the callout; overrides kind.")]
        public string ViewFamilyType { get; set; }

        [JsonProperty("name")]
        [Description("Callout view name (must be free). Omit for Revit's default name.")]
        public string Name { get; set; }

        [JsonProperty("scale")]
        [Description("Scale denominator (20 = 1:20).")]
        [Range(1, 24000)]
        public int? Scale { get; set; }

        [JsonProperty("detailLevel")]
        [Description("coarse, medium or fine.")]
        [EnumValues("coarse", "medium", "fine")]
        public string DetailLevel { get; set; }

        [JsonProperty("template")]
        [Description("View template name or id to assign.")]
        public string Template { get; set; }
    }

    /// <summary>VIS-05: callout in a plan/section by rectangle or by elements, in the parent's coordinates.</summary>
    [McpTool("create_callout_view", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views",
        Description = "Create a callout view inside a parent view (plan, section, elevation, detail), framed by a " +
                      "model-XY rectangle in mm (plans) or by elements plus padding mm (any parent; corners are " +
                      "projected through the parent's view transform). kind plan (cropped plan, plan parents) or " +
                      "detail. Optional name, scale (20 = 1:20), detail level, template. The callout's crop is read " +
                      "back. Undo: Ctrl+Z or delete the returned view id.")]
    public sealed class CreateCalloutViewTool : McpTool<CreateCalloutViewArgs>
    {
        protected override void Validate(CreateCalloutViewArgs args)
        {
            args.Kind = args.Kind?.Trim().ToLowerInvariant();
            var hasRect = args.Rect?.Min != null && args.Rect.Max != null;
            var hasFit = args.FitElementIds != null && args.FitElementIds.Count > 0;
            if (hasRect == hasFit)
                throw new ToolException("INVALID_ARGUMENTS", "Pass either rect or fitElementIds (exactly one).");
            if (hasRect && (args.Rect.Max.X - args.Rect.Min.X < 10 || args.Rect.Max.Y - args.Rect.Min.Y < 10))
                throw new ToolException("INVALID_ARGUMENTS", "rect.max must be at least 10 mm above and right of rect.min.");
        }

        protected override object Run(ToolContext ctx, CreateCalloutViewArgs args)
        {
            var doc = ctx.Doc;
            var parent = ctx.Services.Lookup.View(args.ParentView);
            var isPlan = parent is ViewPlan;
            if (!(isPlan || parent is ViewSection || parent.ViewType == ViewType.Detail))
                throw new ToolException("CALLOUT_NOT_SUPPORTED", $"Callouts can't be drawn in '{parent.Name}' ({parent.ViewType}).",
                    "Use a plan, section, elevation or detail view as parent.");
            if (!string.IsNullOrWhiteSpace(args.Name))
                ctx.Services.Names.EnsureViewNameFree(args.Name, isPlan && args.Kind != "detail" ? parent.ViewType : ViewType.Detail);

            var cropT = parent.CropBox?.Transform ?? Transform.Identity;
            List<XYZ> points;
            if (args.Rect != null)
            {
                if (!isPlan)
                    throw new ToolException("INVALID_ARGUMENTS", "rect is for plan parents; use fitElementIds for sections and elevations.");
                var z = (parent as ViewPlan).GenLevel?.Elevation ?? 0.0;
                points = new List<XYZ>
                {
                    new XYZ(Mm.ToFt(args.Rect.Min.X), Mm.ToFt(args.Rect.Min.Y), z),
                    new XYZ(Mm.ToFt(args.Rect.Max.X), Mm.ToFt(args.Rect.Max.Y), z),
                    new XYZ(Mm.ToFt(args.Rect.Min.X), Mm.ToFt(args.Rect.Max.Y), z),
                    new XYZ(Mm.ToFt(args.Rect.Max.X), Mm.ToFt(args.Rect.Min.Y), z)
                };
            }
            else points = ViewSupport.ElementCorners(ctx, args.FitElementIds);

            var pad = args.Rect != null ? 0 : Mm.ToFt(args.Padding);
            var r = ViewSupport.Project(cropT, points);
            var p1 = cropT.OfPoint(new XYZ(r.MinU - pad, r.MinV - pad, 0));
            var p2 = cropT.OfPoint(new XYZ(r.MaxU + pad, r.MaxV + pad, 0));

            ViewFamilyType vft;
            if (!string.IsNullOrWhiteSpace(args.ViewFamilyType))
            {
                var types = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().ToList();
                vft = ctx.Services.Lookup.ByNameOrId(args.ViewFamilyType, types, "view family type", t => t.ViewFamily.ToString());
            }
            else if (isPlan && args.Kind != "detail")
                vft = doc.GetElement(parent.GetTypeId()) as ViewFamilyType
                      ?? ViewSupport.FamilyType(ctx, ViewFamily.FloorPlan, null);
            else
                vft = ViewSupport.FamilyType(ctx, ViewFamily.Detail, null);

            View callout;
            try
            {
                callout = ViewSection.CreateCallout(doc, parent.Id, vft.Id, p1, p2);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("CALLOUT_NOT_CREATED", $"Revit refused the callout: {ex.Message}",
                    "For plan parents use a plan type of the same family or a detail type; for sections use a detail type.");
            }
            if (callout == null) throw new ToolException("CALLOUT_NOT_CREATED", "Revit did not create the callout.");
            ViewSupport.ApplyCommon(ctx, callout, args.Name, args.Scale, args.DetailLevel, args.Template, null);

            return new
            {
                parent = parent.Name,
                viewFamilyType = vft.Name,
                view = ViewSupport.Describe(callout),
                crop = ViewSupport.DescribeCrop(callout),
                dryRun = ctx.DryRun
            };
        }
    }
}
