using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Families
{
    public sealed class EditFamilyArgs
    {
        [JsonProperty("operation", Required = Required.Always)]
        [EnumValues("open", "load")]
        [Description("open: open a loaded family in the family editor (from the project). load: load an open family document back into the project, replacing the family.")]
        public string Operation { get; set; }

        [JsonProperty("element")]
        [Description("open: an instance or type id of the family (alternative to family).")]
        public string Element { get; set; }

        [JsonProperty("family")]
        [Description("open: family name or id in the project. load from the project window: name of the open family document to load.")]
        public string Family { get; set; }

        [JsonProperty("project")]
        [Description("load from the family editor: title of the open project to load into. Default: the only open project.")]
        public string Project { get; set; }

        [JsonProperty("overwriteParameterValues")]
        [DefaultValue(false)]
        [Description("load: also overwrite the parameter values of the existing types in the project with the family's (Revit's \"Overwrite the existing version and its parameter values\"). Default false: only the geometry/definition is replaced.")]
        public bool OverwriteParameterValues { get; set; }

        [JsonProperty("saveFamily")]
        [DefaultValue(false)]
        [Description("load: save the family file first (only when it lives in an allowed folder, e.g. the copy made by open).")]
        public bool SaveFamily { get; set; }

        [JsonProperty("closeFamily")]
        [DefaultValue(false)]
        [Description("load: close the family document afterwards without saving (not possible while it is the active window).")]
        public bool CloseFamily { get; set; }

        [JsonProperty("overwriteFile")]
        [DefaultValue(true)]
        [Description("open: overwrite an older working copy of the family in Documents\\RevitMCP\\families. Default true.")]
        public bool OverwriteFile { get; set; } = true;
    }

    /// <summary>FAM-15: open a loaded family in the family editor and load it back into the project.</summary>
    [McpTool("edit_family", Kind = ToolKind.UI, Risk = Risk.Medium, Toolset = "families", Batchable = false, TimeoutSeconds = 120,
        Description = "open: take a loaded family (by instance/type id or family name), save a working copy to " +
                      "Documents\\RevitMCP\\families\\<family>.rfa and open it in Revit's family editor as the active " +
                      "document (edit it with manage_family_parameters targeting that document). load: load an open family " +
                      "document back into the project, replacing the existing family (optionally overwriting the types' " +
                      "parameter values), optionally saving and closing it. In-place and system families can't be edited. " +
                      "Changes the project on load: undo with Ctrl+Z in the project.")]
    public sealed class EditFamilyTool : McpTool<EditFamilyArgs>
    {
        private sealed class LoadOptions : IFamilyLoadOptions
        {
            private readonly bool _values;
            public LoadOptions(bool values) { _values = values; }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _values;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _values;
                return true;
            }
        }

        protected override void Validate(EditFamilyArgs a)
        {
            a.Operation = a.Operation.Trim().ToLowerInvariant();
            if (a.Operation == "open" && string.IsNullOrWhiteSpace(a.Element) == string.IsNullOrWhiteSpace(a.Family))
                throw new ToolException("INVALID_ARGUMENTS", "open needs element or family (exactly one).");
        }

        protected override object Run(ToolContext ctx, EditFamilyArgs a) =>
            a.Operation == "open" ? Open(ctx, a) : Load(ctx, a);

        private static object Open(ToolContext ctx, EditFamilyArgs a)
        {
            var doc = ctx.Doc;
            if (doc.IsFamilyDocument && !string.IsNullOrWhiteSpace(a.Element))
                throw new ToolException("NOT_SUPPORTED", "open works from a project (or a host family) with a family name; this is a family document.");
            Family family = null;
            if (!string.IsNullOrWhiteSpace(a.Element))
            {
                var e = ctx.Services.Lookup.Element(a.Element);
                family = e is Family f ? f : e is FamilyInstance fi ? fi.Symbol?.Family : e is FamilySymbol fs ? fs.Family : null;
                if (family == null)
                    throw new ToolException("NOT_A_FAMILY", $"Element {a.Element} ({e.Category?.Name}) is not a loadable family instance or type (system families can't be edited).");
            }
            else
            {
                var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
                family = ctx.Services.Lookup.ByNameOrId(a.Family, families, "family", f => f.FamilyCategory?.Name);
            }
            if (family.IsInPlace)
                throw new ToolException("NOT_SUPPORTED", $"'{family.Name}' is an in-place family; edit it in place in Revit.");
            if (!family.IsEditable)
                throw new ToolException("NOT_SUPPORTED", $"'{family.Name}' can't be edited (locked or built-in family).");

            var safe = new string(family.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            var path = PathPolicy.Resolve(Path.Combine("families", safe + ".rfa"), null, true);
            var already = ctx.UiApp.Application.Documents.Cast<Document>()
                .FirstOrDefault(d => d.IsFamilyDocument && string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase));
            if (already != null)
                throw new ToolException("ALREADY_OPEN", $"The working copy '{path}' is already open in Revit.",
                    "Switch to it, or load it back (edit_family operation load) and close it first.");
            if (File.Exists(path) && !a.OverwriteFile)
                throw new ToolException("FILE_EXISTS", $"'{path}' already exists.", "Pass overwriteFile:true to replace the old working copy.");

            Document famDoc;
            try { famDoc = doc.EditFamily(family); }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("EDIT_FAMILY_FAILED", $"Revit could not open '{family.Name}' for editing: {ex.Message}");
            }
            try
            {
                famDoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 });
            }
            finally
            {
                famDoc.Close(false);
            }
            var ui = ctx.UiApp.OpenAndActivateDocument(path);
            if (safe != family.Name)
                ctx.Warnings.Add(new ToolWarning($"The file name differs from the family name ('{safe}'); loading it back creates or replaces a family named '{safe}'."));
            return new
            {
                operation = "open",
                family = family.Name,
                familyId = family.Id.GetValue(),
                category = family.FamilyCategory?.Name,
                path,
                activeDocument = ui?.Document?.Title,
                next = "Target this family document for manage_family_parameters, then call edit_family {operation:\"load\"} to load it back."
            };
        }

        private static object Load(ToolContext ctx, EditFamilyArgs a)
        {
            var app = ctx.UiApp.Application;
            var open = app.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList();
            Document famDoc, project;
            if (ctx.Doc.IsFamilyDocument)
            {
                famDoc = ctx.Doc;
                var projects = open.Where(d => !d.IsFamilyDocument).ToList();
                if (!string.IsNullOrWhiteSpace(a.Project))
                    project = projects.FirstOrDefault(d => string.Equals(d.Title, a.Project.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                                          string.Equals(Path.GetFileNameWithoutExtension(d.Title), a.Project.Trim(), StringComparison.OrdinalIgnoreCase))
                              ?? throw new ToolException("NOT_FOUND", $"No open project '{a.Project}'. Open: {string.Join(", ", projects.Select(d => d.Title))}.");
                else if (projects.Count == 1) project = projects[0];
                else throw new ToolException(projects.Count == 0 ? "NOT_FOUND" : "AMBIGUOUS_NAME",
                    projects.Count == 0 ? "No project is open to load the family into." : $"Several projects are open: {string.Join(", ", projects.Select(d => d.Title))}.",
                    "Pass project.");
            }
            else
            {
                project = ctx.Doc;
                var fams = open.Where(d => d.IsFamilyDocument).ToList();
                if (string.IsNullOrWhiteSpace(a.Family))
                {
                    if (fams.Count != 1)
                        throw new ToolException("INVALID_ARGUMENTS", fams.Count == 0 ? "No family document is open." :
                            $"Several family documents are open ({string.Join(", ", fams.Select(d => d.Title))}): pass family.");
                    famDoc = fams[0];
                }
                else
                    famDoc = fams.FirstOrDefault(d => string.Equals(Path.GetFileNameWithoutExtension(d.Title), a.Family.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                                      string.Equals(d.Title, a.Family.Trim(), StringComparison.OrdinalIgnoreCase))
                             ?? throw new ToolException("NOT_FOUND", $"No open family document '{a.Family}'. Open: {string.Join(", ", fams.Select(d => d.Title))}.");
            }
            if (famDoc.IsModifiable || project.IsModifiable)
                throw new ToolException(ToolErrorCodes.DocumentBusy, "A document is in the middle of an edit.", "Ask the user to finish the current command in Revit.");
            if (project.IsReadOnly)
                throw new ToolException(ToolErrorCodes.DocumentReadOnly, $"'{project.Title}' is read-only.");

            bool saved = false;
            if (a.SaveFamily)
            {
                if (!string.IsNullOrEmpty(famDoc.PathName) && PathPolicy.IsAllowed(famDoc.PathName))
                {
                    famDoc.Save();
                    saved = true;
                }
                else ctx.Warnings.Add(new ToolWarning("The family file was not saved: it is not in an allowed folder (only copies made by edit_family open, or REVIT_MCP_ALLOWED_DIRS)."));
            }

            var name = Path.GetFileNameWithoutExtension(famDoc.Title);
            var existing = new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            Family loaded;
            try
            {
                loaded = famDoc.LoadFamily(project, new LoadOptions(a.OverwriteParameterValues));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("LOAD_FAILED", $"Revit could not load '{famDoc.Title}' into '{project.Title}': {ex.Message}");
            }
            if (loaded == null)
                throw new ToolException("LOAD_FAILED", $"Revit did not load '{famDoc.Title}' into '{project.Title}'.");

            bool closed = false;
            if (a.CloseFamily)
            {
                var active = ctx.UiApp.ActiveUIDocument?.Document;
                if (active != null && active.Equals(famDoc))
                    ctx.Warnings.Add(new ToolWarning("The family document is the active window, so it was not closed; close it in Revit (or switch to the project and call again with closeFamily)."));
                else
                {
                    famDoc.Close(false);
                    closed = true;
                }
            }

            var symbols = loaded.GetFamilySymbolIds().Select(id => project.GetElement(id)?.Name).Where(n => n != null).OrderBy(n => n).ToList();
            int instances = new FilteredElementCollector(project).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Count(fi => fi.Symbol?.Family?.Id == loaded.Id);
            return new
            {
                operation = "load",
                family = loaded.Name,
                familyId = loaded.Id.GetValue(),
                project = project.Title,
                replaced = existing != null,
                overwriteParameterValues = a.OverwriteParameterValues,
                types = symbols,
                instances,
                familySaved = saved,
                familyClosed = closed
            };
        }
    }
}
