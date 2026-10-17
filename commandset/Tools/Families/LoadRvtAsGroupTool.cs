using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Families
{
    public sealed class LoadRvtAsGroupArgs
    {
        [JsonProperty("path", Required = Required.Always)]
        [Required]
        [Description("The .rvt to bring in: absolute, or relative to Documents\\RevitMCP\\models; must be in the allowed folders.")]
        public string Path { get; set; }

        [JsonProperty("groupName")]
        [Description("Model group type name. Default: the file name (a number is added if taken).")]
        public string GroupName { get; set; }

        [JsonProperty("offset")]
        [Description("Move the copied elements by this vector (mm). Default: same coordinates as in the file (internal origin to internal origin).")]
        public Point3Mm Offset { get; set; }

        [JsonProperty("categories")]
        [MaxLength(100)]
        [Description("Only these model categories (pt-BR or English). Default: every model element that has geometry (no levels, grids, views or annotations).")]
        public List<string> Categories { get; set; }
    }

    /// <summary>
    /// FAM-18: Revit's "Load as Group" has no API, so this opens the file in the background, copies its model
    /// elements and groups them. Annotations, views and attached detail groups are not brought in.
    /// </summary>
    [McpTool("load_rvt_as_group", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "families", Batchable = false, TimeoutSeconds = 600,
        Description = "Bring another RVT (e.g. a standard bathroom or kitchen layout) into the project as a model group. " +
                      "Revit's 'Load as Group' command has no API, so the file (allowed folders only) is opened in the " +
                      "background, its model elements are copied (optionally only some categories, moved by an offset in mm) " +
                      "and grouped under groupName; the file is then closed unchanged. Not brought in: views, annotations, " +
                      "levels, grids and attached detail groups; Revit may create levels or types it can't match (reported). " +
                      "dryRun only counts what would be copied. Undo: Ctrl+Z.")]
    public sealed class LoadRvtAsGroupTool : McpTool<LoadRvtAsGroupArgs>, ISelfTransactedTool
    {
        private static readonly BuiltInCategory[] Excluded =
        {
            BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids, BuiltInCategory.OST_Cameras, BuiltInCategory.OST_Viewers,
            BuiltInCategory.OST_Elev, BuiltInCategory.OST_Sections, BuiltInCategory.OST_VolumeOfInterest, BuiltInCategory.OST_RvtLinks,
            BuiltInCategory.OST_ProjectBasePoint, BuiltInCategory.OST_SharedBasePoint, BuiltInCategory.OST_ProjectInformation,
            BuiltInCategory.OST_SketchLines, BuiltInCategory.OST_Constraints, BuiltInCategory.OST_CLines, BuiltInCategory.OST_Matchline,
            BuiltInCategory.OST_IOSSketchGrid
        };

        private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
        }

        protected override object Run(ToolContext ctx, LoadRvtAsGroupArgs a)
        {
            var doc = ctx.Doc;
            if (doc.IsFamilyDocument)
                throw new ToolException("NOT_A_PROJECT", "Groups from RVT files can only be loaded into a project.");
            var full = PathPolicy.Resolve(a.Path, "models");
            if (!full.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                throw new ToolException("INVALID_PATH", $"'{full}' is not an .rvt file.");
            if (!File.Exists(full))
                throw new ToolException("FILE_NOT_FOUND", $"'{full}' does not exist.");
            if (!string.IsNullOrEmpty(doc.PathName) && string.Equals(System.IO.Path.GetFullPath(doc.PathName), full, StringComparison.OrdinalIgnoreCase))
                throw new ToolException("INVALID_PATH", "That file is the target project itself.");

            HashSet<long> onlyCategories = null;
            if (a.Categories != null && a.Categories.Count > 0)
                onlyCategories = new HashSet<long>(ctx.Services.Categories.ResolveAll(a.Categories).Select(c => c.Id.GetValue()));

            var app = doc.Application;
            var source = app.Documents.Cast<Document>().FirstOrDefault(d => !d.IsLinked && !string.IsNullOrEmpty(d.PathName) &&
                string.Equals(System.IO.Path.GetFullPath(d.PathName), full, StringComparison.OrdinalIgnoreCase));
            bool openedHere = source == null;
            if (openedHere)
            {
                try
                {
                    var info = BasicFileInfo.Extract(full);
                    var options = new OpenOptions();
                    if (info.IsWorkshared) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                    options.Audit = false;
                    source = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(full), options);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    throw new ToolException("OPEN_FAILED", $"Revit could not open '{full}': {ex.Message}",
                        "The file may be from a newer Revit version, in use, or corrupt.");
                }
            }

            try
            {
                if (source.IsFamilyDocument)
                    throw new ToolException("INVALID_PATH", $"'{full}' is a family; use load_family.", null, "load_family");

                var excluded = new HashSet<long>(Excluded.Select(c => (long)(int)c));
                var ids = new FilteredElementCollector(source).WhereElementIsNotElementType().ToElements()
                    .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && !e.ViewSpecific)
                    .Where(e => !excluded.Contains(e.Category.Id.GetValue()))
                    .Where(e => e.GroupId == null || e.GroupId == ElementId.InvalidElementId) // group members come with their group
                    .Where(e => !(e is Level || e is Grid || e is SketchPlane || e is View || e is RevitLinkInstance || e is ImportInstance))
                    .Where(e => onlyCategories == null || onlyCategories.Contains(e.Category.Id.GetValue()))
                    .Where(e => e is Group || e.get_BoundingBox(null) != null)
                    .Select(e => e.Id).ToList();
                var byCategory = ids.Select(id => source.GetElement(id).Category.Name).GroupBy(n => n)
                    .OrderByDescending(g => g.Count()).Select(g => new { category = g.Key, count = g.Count() }).ToList();
                if (ids.Count == 0)
                    throw new ToolException("NOTHING_TO_COPY", $"'{System.IO.Path.GetFileName(full)}' has no model elements" + (onlyCategories != null ? " of those categories." : "."));

                var name = string.IsNullOrWhiteSpace(a.GroupName) ? System.IO.Path.GetFileNameWithoutExtension(full) : a.GroupName.Trim();
                if (ctx.DryRun)
                {
                    return new
                    {
                        path = full,
                        wouldCopy = ids.Count,
                        byCategory,
                        groupName = name,
                        changeSummary = $"[dry run] {ids.Count} element(s) would be copied from '{System.IO.Path.GetFileName(full)}' and grouped as '{name}'."
                    };
                }

                var levelsBefore = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(Level)).Select(e => e.Id.GetValue()));
                var transform = a.Offset == null ? Transform.Identity
                    : Transform.CreateTranslation(new XYZ(a.Offset.X, a.Offset.Y, a.Offset.Z ?? 0) / 304.8);
                using (var own = new OwnTransactions(ctx, "load_rvt_as_group"))
                {
                    var result = own.Step("MCP: load RVT as group", () =>
                    {
                        var options = new CopyPasteOptions();
                        options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
                        var copied = ElementTransformUtils.CopyElements(source, ids, doc, transform, options);
                        doc.Regenerate();
                        var groupable = copied.Where(id =>
                        {
                            var e = doc.GetElement(id);
                            return e != null && !(e is ElementType) && !(e is Level) && !(e is View) && e.Category != null &&
                                   e.Category.CategoryType == CategoryType.Model && (e.GroupId == null || e.GroupId == ElementId.InvalidElementId);
                        }).ToList();
                        if (groupable.Count == 0)
                            throw new ToolException("COPY_FAILED", "Revit copied nothing that can be grouped.");
                        Group group;
                        try { group = doc.Create.NewGroup(groupable); }
                        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                        {
                            throw new ToolException("GROUP_FAILED", $"The elements were copied but Revit could not group them: {ex.Message}",
                                "Try fewer categories (categories), e.g. only furniture and casework.");
                        }
                        var taken = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
                        var finalName = name;
                        for (int i = 2; taken.Contains(finalName) && !string.Equals(group.GroupType.Name, finalName, StringComparison.OrdinalIgnoreCase); i++)
                            finalName = $"{name} {i}";
                        try { group.GroupType.Name = finalName; }
                        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                        {
                            ctx.Warnings.Add(new ToolWarning($"The group was created but not renamed to '{finalName}': {ex.Message}"));
                        }
                        return new { group, copied = copied.Count, grouped = groupable.Count };
                    });
                    own.Complete();

                    var newLevels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .Where(l => !levelsBefore.Contains(l.Id.GetValue())).Select(l => l.Name).ToList();
                    if (newLevels.Count > 0)
                        ctx.Warnings.Add(new ToolWarning($"Revit created level(s) it could not match: {string.Join(", ", newLevels)}. Re-host the group or delete them if unwanted."));
                    if (result.grouped < result.copied)
                        ctx.Warnings.Add(new ToolWarning($"{result.copied - result.grouped} copied element(s) could not be part of the group (types, levels or non-groupable elements) and stay in the model."));
                    var g = result.group;
                    return new
                    {
                        path = full,
                        groupId = g.Id.GetValue(),
                        groupTypeId = g.GroupType.Id.GetValue(),
                        groupName = g.GroupType.Name,
                        copied = result.copied,
                        grouped = result.grouped,
                        byCategory,
                        createdLevels = newLevels,
                        changeSummary = $"{result.grouped} element(s) from '{System.IO.Path.GetFileName(full)}' grouped as '{g.GroupType.Name}'."
                    };
                }
            }
            finally
            {
                if (openedHere && source != null && source.IsValidObject)
                {
                    try { source.Close(false); }
                    catch (Exception ex) { ctx.Warnings.Add(new ToolWarning($"The source file could not be closed: {ex.Message}")); }
                }
            }
        }
    }
}
