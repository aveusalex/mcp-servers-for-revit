using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class CreateAssemblyViewsArgs
    {
        [JsonProperty("assemblyId", Required = Required.Always)]
        [Description("Assembly instance id (from manage_assembly or find_elements).")]
        public long AssemblyId { get; set; }

        [JsonProperty("views")]
        [MaxLength(13)]
        [EnumValues("3d", "front", "back", "left", "right", "top", "bottom", "sectionA", "sectionB", "horizontal", "partList", "materialTakeoff", "schedule")]
        [Description("What to create: 3d (orthographic), elevations front/back/left/right/top/bottom, detail sections sectionA/sectionB/horizontal, partList (parts schedule), materialTakeoff, schedule (single category, see scheduleCategory). Default: 3d, front, top, partList.")]
        public List<string> Views { get; set; }

        [JsonProperty("scheduleCategory")]
        [Description("Category of the 'schedule' item (pt-BR or English). Default: the assembly's naming category.")]
        public string ScheduleCategory { get; set; }

        [JsonProperty("scale")]
        [Range(1, 10000)]
        [Description("View scale 1:N for the model views (e.g. 20). Default: Revit's.")]
        public int? Scale { get; set; }

        [JsonProperty("viewTemplate")]
        [Description("View template (name or id) applied to the model views. Default: none.")]
        public string ViewTemplate { get; set; }

        [JsonProperty("createSheet")]
        [DefaultValue(false)]
        [Description("Also create an assembly sheet and place the new views and schedules on it in a row. Default false.")]
        public bool CreateSheet { get; set; }

        [JsonProperty("titleBlock")]
        [Description("Title block type for the sheet (\"Family: Type\", type name or id). Default: the first title block in the project.")]
        public string TitleBlock { get; set; }
    }

    /// <summary>VIS-25: assembly views, schedules and sheet (AssemblyViewUtils).</summary>
    [McpTool("create_assembly_views", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views", TimeoutSeconds = 120,
        Description = "Create the views of an assembly (conjunto): 3D orthographic, elevations (front/back/left/right/top/" +
                      "bottom), detail sections, part list, material takeoff and a single-category schedule, optionally " +
                      "with a scale and view template, and optionally an assembly sheet with them placed in a row. These " +
                      "views only show the assembly's members. Returns the ids and names. Undo: Ctrl+Z or delete the views.")]
    public sealed class CreateAssemblyViewsTool : McpTool<CreateAssemblyViewsArgs>
    {
        protected override object Run(ToolContext ctx, CreateAssemblyViewsArgs a)
        {
            var doc = ctx.Doc;
            var asm = ctx.Services.Lookup.Element(a.AssemblyId) as AssemblyInstance
                      ?? throw new ToolException("NOT_AN_ASSEMBLY", $"{a.AssemblyId} is not an assembly instance.", "Create one with manage_assembly.", "manage_assembly");
            if (!asm.AllowsAssemblyViewCreation())
                throw new ToolException("NOT_SUPPORTED", $"Revit does not allow views for assembly '{asm.AssemblyTypeName}' (it may have been created in this same transaction).");

            var wanted = (a.Views == null || a.Views.Count == 0 ? new List<string> { "3d", "front", "top", "partList" } : a.Views)
                .Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var template = string.IsNullOrWhiteSpace(a.ViewTemplate) ? ElementId.InvalidElementId : ctx.Services.Lookup.ViewTemplate(a.ViewTemplate).Id;
            ElementId scheduleCategory = null;
            if (wanted.Any(v => v.Equals("schedule", StringComparison.OrdinalIgnoreCase)))
                scheduleCategory = string.IsNullOrWhiteSpace(a.ScheduleCategory) ? asm.NamingCategoryId : ctx.Services.Categories.Resolve(a.ScheduleCategory).Id;
            FamilySymbol titleBlock = null;
            if (a.CreateSheet)
            {
                titleBlock = string.IsNullOrWhiteSpace(a.TitleBlock)
                    ? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault()
                    : ctx.Services.Lookup.FamilySymbol(a.TitleBlock, BuiltInCategory.OST_TitleBlocks);
                if (titleBlock == null) ctx.Warnings.Add(new ToolWarning("The project has no title block; the sheet was created without one."));
            }

            var created = new List<View>();
            var failed = new List<object>();
            bool withTemplate = template != ElementId.InvalidElementId;
            foreach (var v in wanted)
            {
                try
                {
                    View view;
                    var key = v.ToLowerInvariant();
                    switch (key)
                    {
                        case "3d": view = AssemblyViewUtils.Create3DOrthographic(doc, asm.Id, template, withTemplate); break;
                        case "partlist": view = AssemblyViewUtils.CreatePartList(doc, asm.Id, template, withTemplate); break;
                        case "materialtakeoff": view = AssemblyViewUtils.CreateMaterialTakeoff(doc, asm.Id, template, withTemplate); break;
                        case "schedule": view = AssemblyViewUtils.CreateSingleCategorySchedule(doc, asm.Id, scheduleCategory, template, withTemplate); break;
                        default: view = AssemblyViewUtils.CreateDetailSection(doc, asm.Id, Orientation(key), template, withTemplate); break;
                    }
                    if (a.Scale.HasValue && !(view is ViewSchedule) && !withTemplate)
                    {
                        try { view.Scale = a.Scale.Value; }
                        catch (Exception ex) { ctx.Warnings.Add(new ToolWarning($"Scale not set on '{view.Name}': {ex.Message}")); }
                    }
                    created.Add(view);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ToolException)
                {
                    failed.Add(new { view = v, reason = ex.Message });
                }
            }
            if (created.Count == 0)
                throw new ToolException("CREATE_FAILED", "No assembly view was created: " + JsonConvert.SerializeObject(failed));

            object sheetInfo = null;
            if (a.CreateSheet)
            {
                var sheet = AssemblyViewUtils.CreateSheet(doc, asm.Id, titleBlock?.Id ?? ElementId.InvalidElementId);
                doc.Regenerate();
                var placed = Place(doc, sheet, created, ctx.Warnings);
                sheetInfo = new { id = sheet.Id.GetValue(), number = sheet.SheetNumber, name = sheet.Name, placed };
            }

            return new
            {
                assemblyId = asm.Id.GetValue(),
                assembly = asm.AssemblyTypeName,
                views = created.Select(v => new { id = v.Id.GetValue(), name = v.Name, viewType = v.ViewType.ToString() }).ToList(),
                failed,
                sheet = sheetInfo,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"{created.Count} assembly view(s) created for '{asm.AssemblyTypeName}'" + (a.CreateSheet ? " and a sheet." : ".")
            };
        }

        private static AssemblyDetailViewOrientation Orientation(string key)
        {
            switch (key)
            {
                case "front": return AssemblyDetailViewOrientation.ElevationFront;
                case "back": return AssemblyDetailViewOrientation.ElevationBack;
                case "left": return AssemblyDetailViewOrientation.ElevationLeft;
                case "right": return AssemblyDetailViewOrientation.ElevationRight;
                case "top": return AssemblyDetailViewOrientation.ElevationTop;
                case "bottom": return AssemblyDetailViewOrientation.ElevationBottom;
                case "sectiona": return AssemblyDetailViewOrientation.DetailSectionA;
                case "sectionb": return AssemblyDetailViewOrientation.DetailSectionB;
                case "horizontal": return AssemblyDetailViewOrientation.HorizontalDetail;
                default: throw new ToolException("INVALID_ARGUMENTS", $"Unknown assembly view '{key}'.");
            }
        }

        /// <summary>Places views left to right across the sheet's middle and schedules down the right side.</summary>
        private static List<object> Place(Document doc, ViewSheet sheet, List<View> views, IList<ToolWarning> warnings)
        {
            var placed = new List<object>();
            var outline = sheet.Outline;
            double width = outline.Max.U - outline.Min.U, height = outline.Max.V - outline.Min.V;
            if (width <= 0 || height <= 0) { width = 841 / 304.8; height = 594 / 304.8; }
            var models = views.Where(v => !(v is ViewSchedule)).ToList();
            var schedules = views.OfType<ViewSchedule>().ToList();
            double usable = width * (schedules.Count > 0 ? 0.7 : 0.9);
            for (int i = 0; i < models.Count; i++)
            {
                var p = new XYZ(outline.Min.U + width * 0.05 + usable * (i + 0.5) / models.Count, outline.Min.V + height * 0.55, 0);
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, models[i].Id))
                {
                    warnings.Add(new ToolWarning($"'{models[i].Name}' could not be placed on the sheet."));
                    continue;
                }
                var vp = Viewport.Create(doc, sheet.Id, models[i].Id, p);
                placed.Add(new { viewId = models[i].Id.GetValue(), viewportId = vp.Id.GetValue() });
            }
            for (int i = 0; i < schedules.Count; i++)
            {
                var p = new XYZ(outline.Min.U + width * 0.78, outline.Max.V - height * (0.08 + 0.3 * i), 0);
                try
                {
                    var si = ScheduleSheetInstance.Create(doc, sheet.Id, schedules[i].Id, p);
                    placed.Add(new { viewId = schedules[i].Id.GetValue(), scheduleInstanceId = si.Id.GetValue() });
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    warnings.Add(new ToolWarning($"Schedule '{schedules[i].Name}' could not be placed: {ex.Message}"));
                }
            }
            return placed;
        }
    }
}
