using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Sheets.Support;
using RevitMCPCommandSet.Tools.Views;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Sheets
{
    public sealed class DuplicateSheetArgs
    {
        [JsonProperty("sheet", Required = Required.Always)]
        [Description("Sheet to duplicate (number, name or id).")]
        public string Sheet { get; set; }

        [JsonProperty("copies")]
        [Description("Number of copies, 1-20 (default 1).")]
        [Range(1, 20)]
        [DefaultValue(1)]
        public int Copies { get; set; } = 1;

        [JsonProperty("numbers")]
        [Description("Sheet numbers of the copies (one per copy, must be free). Omit for \"<source number>-1\", \"-2\"...")]
        public List<string> Numbers { get; set; }

        [JsonProperty("names")]
        [Description("Sheet names of the copies (one per copy). Omit to keep the source name.")]
        public List<string> Names { get; set; }

        [JsonProperty("viewOption")]
        [Description("What to do with the placed views: none (default; only legends/schedules/annotations are copied), duplicate, with_detailing or as_dependent (each view is duplicated and placed at the same position).")]
        [EnumValues("none", "duplicate", "with_detailing", "as_dependent")]
        [DefaultValue("none")]
        public string ViewOption { get; set; } = "none";

        [JsonProperty("viewNamePattern")]
        [Description("Name of duplicated views; tokens {view} (source view name) and {number} (new sheet number). Default \"{view} - {number}\".")]
        [DefaultValue("{view} - {number}")]
        public string ViewNamePattern { get; set; } = "{view} - {number}";

        [JsonProperty("keepLegends")]
        [Description("Place the same legends at the same positions (default true).")]
        [DefaultValue(true)]
        public bool KeepLegends { get; set; } = true;

        [JsonProperty("keepSchedules")]
        [Description("Place the same schedules at the same positions (default true).")]
        [DefaultValue(true)]
        public bool KeepSchedules { get; set; } = true;

        [JsonProperty("copyAnnotations")]
        [Description("Copy sheet annotations: text, generic annotations, detail lines/components, images, detail groups (default true).")]
        [DefaultValue(true)]
        public bool CopyAnnotations { get; set; } = true;

        [JsonProperty("copyParameters")]
        [Description("Copy writable sheet and title block instance values (except number and name; default true).")]
        [DefaultValue(true)]
        public bool CopyParameters { get; set; } = true;

        [JsonProperty("copyRevisions")]
        [Description("Copy the manually assigned revisions (default false).")]
        [DefaultValue(false)]
        public bool CopyRevisions { get; set; }
    }

    /// <summary>FOL-08: duplicate a sheet N times with title block, values, legends, schedules, annotations and views.</summary>
    [McpTool("duplicate_sheet", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "sheets", TimeoutSeconds = 120,
        Description = "Duplicate a sheet 1-20 times: same title block type and (copyParameters) its writable values, " +
                      "legends and schedules at the same positions, sheet annotations (text, generic annotations, " +
                      "detail lines/components, images, detail groups) and optionally the placed views duplicated " +
                      "(duplicate / with_detailing / as_dependent) and placed at the same centres with the same " +
                      "viewport type. Numbers given or \"<source>-1\"...; works the same on Revit 2022-2026. Needs " +
                      "approval. Undo: Ctrl+Z.")]
    public sealed class DuplicateSheetTool : McpTool<DuplicateSheetArgs>
    {
        private static readonly BuiltInCategory[] AnnotationCategories =
        {
            BuiltInCategory.OST_TextNotes, BuiltInCategory.OST_GenericAnnotation, BuiltInCategory.OST_Lines,
            BuiltInCategory.OST_DetailComponents, BuiltInCategory.OST_RasterImages, BuiltInCategory.OST_IOSDetailGroups
        };

        private static readonly BuiltInParameter[] NotCopied =
        {
            BuiltInParameter.SHEET_NUMBER, BuiltInParameter.SHEET_NAME, BuiltInParameter.VIEW_NAME,
            BuiltInParameter.SHEET_CURRENT_REVISION, BuiltInParameter.SHEET_CURRENT_REVISION_DATE,
            BuiltInParameter.SHEET_CURRENT_REVISION_DESCRIPTION, BuiltInParameter.SHEET_CURRENT_REVISION_ISSUED,
            BuiltInParameter.SHEET_CURRENT_REVISION_ISSUED_BY, BuiltInParameter.SHEET_CURRENT_REVISION_ISSUED_TO
        };

        protected override void Validate(DuplicateSheetArgs args)
        {
            args.ViewOption = (args.ViewOption ?? "none").ToLowerInvariant();
            if (args.Numbers != null && args.Numbers.Count != args.Copies)
                throw new ToolException("INVALID_ARGUMENTS", $"numbers has {args.Numbers.Count} entries for {args.Copies} copies.");
            if (args.Names != null && args.Names.Count != args.Copies)
                throw new ToolException("INVALID_ARGUMENTS", $"names has {args.Names.Count} entries for {args.Copies} copies.");
            if (args.Numbers != null)
            {
                var dup = NameRules.DuplicatesIn(args.Numbers);
                if (dup.Count > 0) throw new ToolException("NAME_COLLISION", "Sheet number(s) repeated: " + string.Join(", ", dup));
            }
        }

        protected override object Run(ToolContext ctx, DuplicateSheetArgs args)
        {
            var doc = ctx.Doc;
            var source = SheetSupport.Sheet(ctx, args.Sheet);
            if (source.IsPlaceholder)
                throw new ToolException("SHEET_IS_PLACEHOLDER", $"Sheet {source.SheetNumber} is a placeholder.", "Use create_sheets for placeholders.", "create_sheets");
            var sourceTbs = SheetSupport.TitleBlocks(doc, source.Id);
            if (sourceTbs.Count > 1)
                ctx.Warnings.Add(new ToolWarning($"Sheet {source.SheetNumber} has {sourceTbs.Count} title blocks; only the first is reproduced."));
            var sourceTb = sourceTbs.FirstOrDefault();
            var viewports = source.GetAllViewports().Select(id => doc.GetElement(id) as Viewport).Where(v => v != null).ToList();
            var schedules = new FilteredElementCollector(doc, source.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                .Where(s => !s.IsTitleblockRevisionSchedule).ToList();
            var annotations = args.CopyAnnotations
                ? new FilteredElementCollector(doc, source.Id).WhereElementIsNotElementType()
                    .Where(e => e.OwnerViewId == source.Id && e.Category != null && e.GroupId == ElementId.InvalidElementId &&
                                AnnotationCategories.Any(c => e.Category.Id.GetValue() == (long)c))
                    .Select(e => e.Id).ToList()
                : new List<ElementId>();
            var duplicateViews = args.ViewOption != "none";
            var option = DuplicateViewTool.ParseOption(args.ViewOption);
            if (duplicateViews)
            {
                foreach (var vp in viewports)
                {
                    var v = doc.GetElement(vp.ViewId) as View;
                    if (v.ViewType != ViewType.Legend && !v.CanViewBeDuplicated(option))
                        throw new ToolException("CANNOT_DUPLICATE", $"View '{v.Name}' cannot be duplicated as {args.ViewOption}.", "Try viewOption duplicate or none.");
                }
            }

            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reservedViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var created = new List<object>();
            for (var n = 0; n < args.Copies; n++)
            {
                var number = args.Numbers?[n] ?? NextNumber(ctx, source.SheetNumber, reserved);
                ctx.Services.Names.EnsureSheetNumberFree(number);
                reserved.Add(number);
                var name = args.Names?[n] ?? source.Name;

                var sheet = ViewSheet.Create(doc, sourceTb?.Symbol.Id ?? ElementId.InvalidElementId);
                ctx.Services.Names.ApplySheetNumber(sheet, number);
                ctx.Services.Names.ApplyName(sheet, name, "sheet name");

                var copiedValues = 0;
                if (args.CopyParameters)
                {
                    copiedValues += CopyValues(source, sheet);
                    var newTb = SheetSupport.TitleBlocks(doc, sheet.Id).FirstOrDefault();
                    if (sourceTb != null && newTb != null) copiedValues += CopyValues(sourceTb, newTb);
                }

                var placed = new List<object>();
                foreach (var vp in viewports)
                {
                    var view = doc.GetElement(vp.ViewId) as View;
                    var center = vp.GetBoxCenter();
                    View toPlace;
                    if (view.ViewType == ViewType.Legend)
                    {
                        if (!args.KeepLegends) continue;
                        toPlace = view;
                    }
                    else if (duplicateViews)
                    {
                        toPlace = doc.GetElement(view.Duplicate(option)) as View
                                  ?? throw new ToolException("VIEW_NOT_CREATED", $"Revit did not duplicate '{view.Name}'.");
                        var viewName = ViewSupport.FreeViewName(doc, ViewSupport.Expand(args.ViewNamePattern,
                            new Dictionary<string, string> { ["view"] = view.Name, ["number"] = number }), toPlace.ViewType, reservedViews);
                        ctx.Services.Names.ApplyName(toPlace, viewName, "view name");
                    }
                    else continue;

                    var newVp = Viewport.Create(doc, sheet.Id, toPlace.Id, center)
                                ?? throw new ToolException("VIEWPORT_NOT_CREATED", $"Revit did not place '{toPlace.Name}'.");
                    if (newVp.GetTypeId() != vp.GetTypeId()) newVp.ChangeTypeId(vp.GetTypeId());
                    doc.Regenerate();
                    newVp.SetBoxCenter(center);
                    placed.Add(new { view = toPlace.Name, viewId = toPlace.Id.GetValue(), viewportId = newVp.Id.GetValue(), duplicatedFrom = toPlace.Id == view.Id ? null : view.Name });
                }

                if (args.KeepSchedules)
                {
                    foreach (var s in schedules)
                    {
                        var inst = ScheduleSheetInstance.Create(doc, sheet.Id, s.ScheduleId, s.Point);
                        placed.Add(new { view = doc.GetElement(s.ScheduleId)?.Name, viewId = s.ScheduleId.GetValue(), scheduleInstanceId = inst.Id.GetValue(), duplicatedFrom = (string)null });
                    }
                }

                var copiedAnnotations = 0;
                if (annotations.Count > 0)
                    copiedAnnotations = ElementTransformUtils.CopyElements(source, annotations, sheet, Transform.Identity, new CopyPasteOptions()).Count;

                if (args.CopyRevisions)
                {
                    sheet.SetAdditionalRevisionIds(source.GetAdditionalRevisionIds());
                }

                created.Add(new
                {
                    sheetId = sheet.Id.GetValue(),
                    number = sheet.SheetNumber,
                    name = sheet.Name,
                    titleBlock = sourceTb == null ? null : sourceTb.Symbol.FamilyName + ": " + sourceTb.Symbol.Name,
                    copiedValues,
                    placed,
                    copiedAnnotations,
                    revisions = args.CopyRevisions ? sheet.GetAdditionalRevisionIds().Count : (int?)null
                });
            }

            var skippedViews = duplicateViews ? 0 : viewports.Count(v => (doc.GetElement(v.ViewId) as View)?.ViewType != ViewType.Legend);
            if (skippedViews > 0)
                ctx.Warnings.Add(new ToolWarning($"{skippedViews} placed view(s) were not copied (viewOption none: a view can be on one sheet only)."));
            return new { source = new { sheetId = source.Id.GetValue(), number = source.SheetNumber, name = source.Name }, created, dryRun = ctx.DryRun };
        }

        private static string NextNumber(ToolContext ctx, string baseNumber, ISet<string> reserved)
        {
            var used = new HashSet<string>(ctx.Services.Names.SheetNumbers(), StringComparer.OrdinalIgnoreCase);
            used.UnionWith(reserved);
            for (var i = 1; ; i++)
            {
                var candidate = $"{baseNumber}-{i}";
                if (!used.Contains(candidate)) return candidate;
            }
        }

        /// <summary>Copies writable, non-ElementId values that differ; returns how many were copied.</summary>
        private static int CopyValues(Element from, Element to)
        {
            var count = 0;
            foreach (Parameter p in from.Parameters)
            {
                if (p.IsReadOnly || !p.HasValue || p.StorageType == StorageType.ElementId || p.StorageType == StorageType.None) continue;
                if (p.Definition is InternalDefinition def && NotCopied.Contains(def.BuiltInParameter)) continue;
                var q = p.IsShared ? to.get_Parameter(p.GUID) : p.Definition is InternalDefinition d && d.BuiltInParameter != BuiltInParameter.INVALID
                    ? to.get_Parameter(d.BuiltInParameter)
                    : to.LookupParameter(p.Definition.Name);
                if (q == null || q.IsReadOnly || q.StorageType != p.StorageType) continue;
                try
                {
                    switch (p.StorageType)
                    {
                        case StorageType.String:
                            if (q.AsString() == p.AsString()) continue;
                            q.Set(p.AsString() ?? string.Empty);
                            break;
                        case StorageType.Integer:
                            if (q.AsInteger() == p.AsInteger()) continue;
                            q.Set(p.AsInteger());
                            break;
                        case StorageType.Double:
                            if (Math.Abs(q.AsDouble() - p.AsDouble()) < 1e-9) continue;
                            q.Set(p.AsDouble());
                            break;
                    }
                    count++;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // formula-driven or otherwise locked: leave the default
                }
            }
            return count;
        }
    }
}
