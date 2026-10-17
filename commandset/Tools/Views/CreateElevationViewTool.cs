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
    public sealed class CreateElevationViewArgs
    {
        [JsonProperty("plan", Required = Required.Always)]
        [Description("Floor/ceiling plan (name or id) where the elevation marker is placed.")]
        public string Plan { get; set; }

        [JsonProperty("point", Required = Required.Always)]
        [Description("Marker position in plan, mm (project internal origin).")]
        public PointXY Point { get; set; }

        [JsonProperty("directions")]
        [Description("Directions the cameras look to (model axes: north = +Y, east = +X). Default all four. An interior elevation of the north wall looks north; an exterior 'north facade' elevation looks south.")]
        [EnumValues("north", "south", "east", "west")]
        public List<string> Directions { get; set; }

        [JsonProperty("viewFamilyType")]
        [Description("Elevation view type name or id (e.g. an interior elevation type). Omit for the default elevation type.")]
        public string ViewFamilyType { get; set; }

        [JsonProperty("scale")]
        [Description("Scale denominator (50 = 1:50). Default: the plan's scale.")]
        [Range(1, 24000)]
        public int? Scale { get; set; }

        [JsonProperty("namePattern")]
        [Description("Name pattern with {dir} (N/S/E/W) and {direction} (north...), e.g. \"Elev. hall - {dir}\". Collisions get \" (2)\". Omit for Revit's default names.")]
        public string NamePattern { get; set; }

        [JsonProperty("template")]
        [Description("View template name or id to assign to every elevation.")]
        public string Template { get; set; }

        [JsonProperty("fitElementIds")]
        [Description("Optional element ids (e.g. a room): each elevation is cropped to their extents seen from its direction, far clip included.")]
        public List<string> FitElementIds { get; set; }

        [JsonProperty("padding")]
        [Description("Margin around fitElementIds for the crop, in mm (default 300).")]
        [Unit("mm")]
        [DefaultValue(300.0)]
        public double Padding { get; set; } = 300;
    }

    /// <summary>VIS-04: elevation marker with 1-4 elevations, mapped by real view direction.</summary>
    [McpTool("create_elevation_view", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views",
        Description = "Place an elevation marker on a plan at a point (mm) and create 1-4 elevations by the direction " +
                      "the camera looks to (north = +Y, east = +X of the model). Each view's real direction is read " +
                      "back. Optional view type (e.g. interior elevation), scale, name pattern with {dir}, template, " +
                      "and crop to elements (fitElementIds + padding mm, e.g. a room). Undo: Ctrl+Z or delete the " +
                      "returned view ids.")]
    public sealed class CreateElevationViewTool : McpTool<CreateElevationViewArgs>
    {
        protected override void Validate(CreateElevationViewArgs args)
        {
            if (args.Point == null) throw new ToolException("INVALID_ARGUMENTS", "point is required.");
            if (args.Directions != null && args.Directions.Count == 0)
                throw new ToolException("INVALID_ARGUMENTS", "directions must list at least one direction (or be omitted).");
            if (args.Padding < 0) throw new ToolException("INVALID_ARGUMENTS", "padding must be 0 or more.");
        }

        protected override object Run(ToolContext ctx, CreateElevationViewArgs args)
        {
            var doc = ctx.Doc;
            var plan = ctx.Services.Lookup.View(args.Plan) as ViewPlan;
            if (plan == null)
                throw new ToolException("NOT_A_PLAN", $"'{args.Plan}' is not a plan view; elevation markers are placed on plans.");
            var directions = (args.Directions ?? ElevationSupport.Directions.ToList())
                .Select(d => d.Trim().ToLowerInvariant()).Distinct().ToList();
            var vft = ViewSupport.FamilyType(ctx, ViewFamily.Elevation, args.ViewFamilyType);
            var scale = args.Scale ?? ViewSupport.SafeScale(plan) ?? 100;
            var z = plan.GenLevel?.Elevation ?? 0.0;
            var point = new XYZ(Mm.ToFt(args.Point.X), Mm.ToFt(args.Point.Y), z);
            var corners = args.FitElementIds != null && args.FitElementIds.Count > 0
                ? ViewSupport.ElementCorners(ctx, args.FitElementIds)
                : null;

            var views = ElevationSupport.Create(doc, vft, plan, point, scale, directions);
            var created = new List<object>();
            var reserved = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var d in directions)
            {
                var v = views[d];
                if (corners != null) ElevationSupport.CropTo(v, corners, Mm.ToFt(args.Padding));
                string name = null;
                if (!string.IsNullOrWhiteSpace(args.NamePattern))
                    name = ViewSupport.FreeViewName(doc, ViewSupport.Expand(args.NamePattern, new Dictionary<string, string>
                        { ["dir"] = ElevationSupport.Letter(d), ["direction"] = d }), v.ViewType, reserved);
                ViewSupport.ApplyCommon(ctx, v, null, args.Scale, null, args.Template, null);
                if (name != null) ctx.Services.Names.ApplyName(v, name, "view name");
                created.Add(new
                {
                    direction = d,
                    looksTo = ElevationSupport.LooksTo(v),
                    view = ViewSupport.Describe(v),
                    crop = ViewSupport.DescribeCrop(v)
                });
            }

            return new { plan = plan.Name, markerPoint = Mm.Point(point), created, createdCount = created.Count, dryRun = ctx.DryRun };
        }
    }
}
