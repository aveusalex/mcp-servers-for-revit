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
    public sealed class SectionItem
    {
        [JsonProperty("start", Required = Required.Always)]
        [Description("Start of the section line in plan (mm, project internal origin).")]
        public PointXY Start { get; set; }

        [JsonProperty("end", Required = Required.Always)]
        [Description("End of the section line in plan (mm). The line sets the width of the section.")]
        public PointXY End { get; set; }

        [JsonProperty("lookTo")]
        [Description("Which side of the line start->end the section looks at: left (default; drawn west->east it looks north) or right.")]
        [EnumValues("left", "right")]
        [DefaultValue("left")]
        public string LookTo { get; set; } = "left";

        [JsonProperty("level")]
        [Description("Reference level for bottomOffset (name or id). Omit for the lowest level.")]
        public string Level { get; set; }

        [JsonProperty("bottomOffset")]
        [Description("Bottom of the section relative to the reference level, in mm (default -1000).")]
        [Unit("mm")]
        [DefaultValue(-1000.0)]
        public double BottomOffset { get; set; } = -1000;

        [JsonProperty("height")]
        [Description("Height of the section from its bottom, in mm (default 6000).")]
        [Unit("mm")]
        [DefaultValue(6000.0)]
        public double Height { get; set; } = 6000;

        [JsonProperty("farClipDepth")]
        [Description("How far the section sees beyond the cut line, in mm (default 5000).")]
        [Unit("mm")]
        [DefaultValue(5000.0)]
        public double FarClipDepth { get; set; } = 5000;

        [JsonProperty("name")]
        [Description("Section name (must be free among sections). Omit for Revit's default name.")]
        public string Name { get; set; }

        [JsonProperty("viewFamilyType")]
        [Description("Section view type name or id. Omit for the default section type.")]
        public string ViewFamilyType { get; set; }

        [JsonProperty("scale")]
        [Description("Scale denominator: 50 means 1:50.")]
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

    public sealed class CreateSectionViewArgs
    {
        [JsonProperty("items", Required = Required.Always)]
        [Description("Sections to create, up to 50 per call.")]
        public List<SectionItem> Items { get; set; }

        [JsonProperty("continueOnError")]
        [Description("false (default): any failure rolls back all sections. true: failed ones are listed in failed[].")]
        [DefaultValue(false)]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>VIS-03: vertical sections from a plan line, with a proper view transform.</summary>
    [McpTool("create_section_view", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views",
        Description = "Create vertical building sections from a line in plan (start/end in mm). The section looks to " +
                      "the left of start->end by default (lookTo:right flips it), spans the line's length, goes from " +
                      "level + bottomOffset up by height and sees farClipDepth mm beyond the cut. The resulting view " +
                      "direction and crop are read back. Optional name, type, scale (50 = 1:50), detail level, " +
                      "template. Undo: Ctrl+Z or delete the returned view ids.")]
    public sealed class CreateSectionViewTool : McpTool<CreateSectionViewArgs>
    {
        protected override void Validate(CreateSectionViewArgs args)
        {
            if (args.Items == null || args.Items.Count == 0)
                throw new ToolException("INVALID_ARGUMENTS", "items must list at least one section.");
            if (args.Items.Count > 50)
                throw new ToolException("TOO_MANY_ITEMS", $"{args.Items.Count} sections requested; the limit is 50 per call.");
            foreach (var i in args.Items)
            {
                if (i == null || i.Start == null || i.End == null)
                    throw new ToolException("INVALID_ARGUMENTS", "Every item needs start and end.");
                var len = System.Math.Sqrt(System.Math.Pow(i.End.X - i.Start.X, 2) + System.Math.Pow(i.End.Y - i.Start.Y, 2));
                if (len < 10) throw new ToolException("INVALID_ARGUMENTS", "The section line must be at least 10 mm long.");
                if (i.Height <= 0) throw new ToolException("INVALID_ARGUMENTS", "height must be positive.");
                if (i.FarClipDepth <= 0) throw new ToolException("INVALID_ARGUMENTS", "farClipDepth must be positive.");
            }
        }

        protected override object Run(ToolContext ctx, CreateSectionViewArgs args)
        {
            var r = ItemLoop.Run(ctx, args.Items, args.ContinueOnError, (item, index) => CreateOne(ctx, item, index),
                i => i.Name ?? "section", "Section");
            return new { created = r.Done, failed = r.Failed, createdCount = r.Done.Count, failedCount = r.Failed.Count, dryRun = ctx.DryRun };
        }

        private static object CreateOne(ToolContext ctx, SectionItem item, int index)
        {
            var doc = ctx.Doc;
            if (!string.IsNullOrWhiteSpace(item.Name)) ctx.Services.Names.EnsureViewNameFree(item.Name, ViewType.Section);
            var level = !string.IsNullOrWhiteSpace(item.Level)
                ? ctx.Services.Lookup.Level(item.Level)
                : new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
            var baseZ = level?.Elevation ?? 0.0;

            var a = new XYZ(Mm.ToFt(item.Start.X), Mm.ToFt(item.Start.Y), 0);
            var b = new XYZ(Mm.ToFt(item.End.X), Mm.ToFt(item.End.Y), 0);
            if (string.Equals(item.LookTo, "right", System.StringComparison.OrdinalIgnoreCase))
            {
                var tmp = a; a = b; b = tmp;
            }
            var dir = (b - a).Normalize();
            var up = XYZ.BasisZ;
            var viewDir = dir.CrossProduct(up); // towards the viewer; the section looks along -viewDir (left of dir)
            var half = a.DistanceTo(b) / 2;
            var mid = (a + b) / 2;

            var t = Transform.Identity;
            t.Origin = new XYZ(mid.X, mid.Y, 0);
            t.BasisX = dir;
            t.BasisY = up;
            t.BasisZ = viewDir;

            var bottom = baseZ + Mm.ToFt(item.BottomOffset);
            var box = new BoundingBoxXYZ
            {
                Transform = t,
                Min = new XYZ(-half, bottom, -Mm.ToFt(item.FarClipDepth)),
                Max = new XYZ(half, bottom + Mm.ToFt(item.Height), 0)
            };

            var vft = ViewSupport.FamilyType(ctx, ViewFamily.Section, item.ViewFamilyType);
            var view = ViewSection.CreateSection(doc, vft.Id, box);
            if (view == null) throw new ToolException("VIEW_NOT_CREATED", "Revit did not create the section.");
            ViewSupport.ApplyCommon(ctx, view, item.Name, item.Scale, item.DetailLevel, item.Template, null);

            var looking = view.ViewDirection.Negate();
            return new
            {
                index,
                view = ViewSupport.Describe(view),
                looksToward = new { x = System.Math.Round(looking.X, 4), y = System.Math.Round(looking.Y, 4), z = System.Math.Round(looking.Z, 4) },
                crop = ViewSupport.DescribeCrop(view)
            };
        }
    }
}
