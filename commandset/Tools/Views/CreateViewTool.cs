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
    public sealed class CreateViewItem
    {
        [JsonProperty("viewType", Required = Required.Always)]
        [Description("Kind of view: floor_plan, ceiling_plan, structural_plan, area_plan, 3d (isometric) or drafting. Sections, elevations and callouts have their own tools.")]
        [EnumValues("floor_plan", "ceiling_plan", "structural_plan", "area_plan", "3d", "drafting")]
        public string ViewType { get; set; }

        [JsonProperty("name")]
        [Description("View name (must be free among views of the same type). Omit to keep Revit's default name.")]
        public string Name { get; set; }

        [JsonProperty("level")]
        [Description("Level name or id. Required for plans; ignored for 3d and drafting.")]
        public string Level { get; set; }

        [JsonProperty("viewFamilyType")]
        [Description("View family type name or id (e.g. \"Planta de piso\"). Omit for the document's default type of that kind.")]
        public string ViewFamilyType { get; set; }

        [JsonProperty("areaScheme")]
        [Description("Area scheme name or id, for area_plan (e.g. \"Gross Building\"). Omit when the document has only one.")]
        public string AreaScheme { get; set; }

        [JsonProperty("scale")]
        [Description("Scale denominator: 100 means 1:100. Set before the template is applied.")]
        [Range(1, 24000)]
        public int? Scale { get; set; }

        [JsonProperty("detailLevel")]
        [Description("coarse, medium or fine.")]
        [EnumValues("coarse", "medium", "fine")]
        public string DetailLevel { get; set; }

        [JsonProperty("template")]
        [Description("View template name or id to assign (must be compatible with the view type).")]
        public string Template { get; set; }

        [JsonProperty("scopeBox")]
        [Description("Scope box name or id to crop the view (plans and 3D only).")]
        public string ScopeBox { get; set; }
    }

    public sealed class CreateViewArgs
    {
        [JsonProperty("items", Required = Required.Always)]
        [Description("Views to create, up to 100 per call.")]
        public List<CreateViewItem> Items { get; set; }

        [JsonProperty("continueOnError")]
        [Description("false (default): any failure rolls back every view of the call. true: failed views are skipped and listed in failed[].")]
        [DefaultValue(false)]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>VIS-02: batch creation of plan, ceiling, structural, area, 3D and drafting views.</summary>
    [McpTool("create_view", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views",
        Description = "Create views in batch: floor_plan, ceiling_plan, structural_plan, area_plan (needs a level), " +
                      "isometric 3d and drafting views, with optional name, view family type, scale (100 = 1:100), " +
                      "detail level, view template and scope box. Scale/detail are set before the template; every " +
                      "value is read back. Sections, elevations and callouts: create_section_view, " +
                      "create_elevation_view, create_callout_view. One transaction (one Ctrl+Z); all or nothing unless " +
                      "continueOnError. Undo: Ctrl+Z or delete the returned view ids.")]
    public sealed class CreateViewTool : McpTool<CreateViewArgs>
    {
        public const int MaxItems = 100;

        protected override void Validate(CreateViewArgs args)
        {
            if (args.Items == null || args.Items.Count == 0)
                throw new ToolException("INVALID_ARGUMENTS", "items must list at least one view.");
            if (args.Items.Count > MaxItems)
                throw new ToolException("TOO_MANY_ITEMS", $"{args.Items.Count} views requested; the limit is {MaxItems} per call.");
            if (args.Items.Any(i => i == null))
                throw new ToolException("INVALID_ARGUMENTS", "items must not contain null.");
            foreach (var i in args.Items)
            {
                i.ViewType = i.ViewType?.Trim().ToLowerInvariant();
                if (IsPlan(i.ViewType) && string.IsNullOrWhiteSpace(i.Level))
                    throw new ToolException("INVALID_ARGUMENTS", $"A {i.ViewType} needs a level.");
            }
            var named = args.Items.Where(i => !string.IsNullOrWhiteSpace(i.Name)).GroupBy(i => i.ViewType + "|" + i.Name.Trim().ToLowerInvariant())
                .Where(g => g.Count() > 1).Select(g => g.First().Name).ToList();
            if (named.Count > 0)
                throw new ToolException("NAME_COLLISION", "View name(s) repeated in the request: " + string.Join(", ", named) + ".");
        }

        private static bool IsPlan(string t) => t != null && t.EndsWith("_plan");

        protected override object Run(ToolContext ctx, CreateViewArgs args)
        {
            var r = ItemLoop.Run(ctx, args.Items, args.ContinueOnError, (item, index) => CreateOne(ctx, item, index),
                i => (i.ViewType ?? "?") + (string.IsNullOrWhiteSpace(i.Name) ? string.Empty : " " + i.Name), "View");
            return new { created = r.Done, failed = r.Failed, createdCount = r.Done.Count, failedCount = r.Failed.Count, dryRun = ctx.DryRun };
        }

        private static object CreateOne(ToolContext ctx, CreateViewItem item, int index)
        {
            var doc = ctx.Doc;
            var kind = item.ViewType.ToLowerInvariant();
            Level level = IsPlan(kind) ? ctx.Services.Lookup.Level(item.Level) : null;
            if (!string.IsNullOrWhiteSpace(item.Name))
                ctx.Services.Names.EnsureViewNameFree(item.Name, ExpectedType(kind));

            View view;
            switch (kind)
            {
                case "floor_plan":
                    view = ViewPlan.Create(doc, ViewSupport.FamilyType(ctx, ViewFamily.FloorPlan, item.ViewFamilyType).Id, level.Id);
                    break;
                case "ceiling_plan":
                    view = ViewPlan.Create(doc, ViewSupport.FamilyType(ctx, ViewFamily.CeilingPlan, item.ViewFamilyType).Id, level.Id);
                    break;
                case "structural_plan":
                    view = ViewPlan.Create(doc, ViewSupport.FamilyType(ctx, ViewFamily.StructuralPlan, item.ViewFamilyType).Id, level.Id);
                    break;
                case "area_plan":
                    view = ViewPlan.CreateAreaPlan(doc, AreaScheme(ctx, item.AreaScheme).Id, level.Id);
                    break;
                case "3d":
                    view = View3D.CreateIsometric(doc, ViewSupport.FamilyType(ctx, ViewFamily.ThreeDimensional, item.ViewFamilyType).Id);
                    break;
                case "drafting":
                    view = ViewDrafting.Create(doc, ViewSupport.FamilyType(ctx, ViewFamily.Drafting, item.ViewFamilyType).Id);
                    break;
                default:
                    throw new ToolException("INVALID_ARGUMENTS", $"Unknown viewType '{item.ViewType}'.");
            }
            if (view == null)
                throw new ToolException("VIEW_NOT_CREATED", $"Revit did not create the {kind} view.");

            ViewSupport.ApplyCommon(ctx, view, item.Name, item.Scale, item.DetailLevel, item.Template, item.ScopeBox);

            var created = doc.GetElement(view.Id) as View;
            if (created == null)
                throw new ToolException("VIEW_NOT_CREATED", $"The {kind} view disappeared after creation.");
            return new { index, view = ViewSupport.Describe(created) };
        }

        private static ViewType ExpectedType(string kind)
        {
            switch (kind)
            {
                case "floor_plan": return ViewType.FloorPlan;
                case "ceiling_plan": return ViewType.CeilingPlan;
                case "structural_plan": return ViewType.EngineeringPlan;
                case "area_plan": return ViewType.AreaPlan;
                case "3d": return ViewType.ThreeD;
                default: return ViewType.DraftingView;
            }
        }

        private static AreaScheme AreaScheme(ToolContext ctx, string reference)
        {
            var schemes = new FilteredElementCollector(ctx.Doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().ToList();
            if (schemes.Count == 0)
                throw new ToolException("AREA_SCHEME_NOT_FOUND", "The document has no area scheme.");
            if (string.IsNullOrWhiteSpace(reference))
            {
                if (schemes.Count == 1) return schemes[0];
                throw new ToolException("AREA_SCHEME_REQUIRED",
                    "The document has several area schemes: " + string.Join(", ", schemes.Select(s => s.Name)) + ".",
                    "Pass areaScheme.");
            }
            return ctx.Services.Lookup.ByNameOrId(reference, schemes, "area scheme");
        }
    }
}
