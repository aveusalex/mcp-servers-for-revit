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
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Families
{
    public sealed class LoadFamilyArgs
    {
        [JsonProperty("path", Required = Required.Always)]
        [Required]
        [Description("The .rfa file: absolute, or relative to Documents\\RevitMCP\\families. Must be inside the allowed folders (REVIT_MCP_ALLOWED_DIRS adds the office library).")]
        public string Path { get; set; }

        [JsonProperty("overwrite")]
        [Description("If the family is already loaded, reload it from the file. Default false (report it as already loaded).")]
        [DefaultValue(false)]
        public bool Overwrite { get; set; }

        [JsonProperty("overwriteParameterValues")]
        [Description("When reloading, also overwrite the parameter values of existing types. Default false.")]
        [DefaultValue(false)]
        public bool OverwriteParameterValues { get; set; }

        [JsonProperty("types")]
        [MaxLength(200)]
        [Description("Load only these types (names as in the family). Default: all types.")]
        public List<string> Types { get; set; }
    }

    /// <summary>FAM-07: load or reload an .rfa from the allowed folders.</summary>
    [McpTool("load_family", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "families", TimeoutSeconds = 120,
        Description = "Load (or reload with overwrite) a family .rfa into the project, only from the allowed folders " +
                      "(Documents\\RevitMCP by default, plus REVIT_MCP_ALLOWED_DIRS). Optionally load only some types. " +
                      "Returns the family id, category and its types with ids; an already-loaded family is reported, " +
                      "not duplicated. Undo: Ctrl+Z.")]
    public sealed class LoadFamilyTool : McpTool<LoadFamilyArgs>
    {
        private sealed class LoadOptions : IFamilyLoadOptions
        {
            private readonly bool _overwrite;
            private readonly bool _values;

            public LoadOptions(bool overwrite, bool values)
            {
                _overwrite = overwrite;
                _values = values;
            }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _values;
                return _overwrite;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _values;
                return _overwrite;
            }
        }

        protected override object Run(ToolContext ctx, LoadFamilyArgs a)
        {
            var doc = ctx.Doc;
            if (doc.IsFamilyDocument && doc.OwnerFamily == null)
                throw new ToolException("NOT_A_PROJECT", "Families can only be loaded into a project or another family.");
            var full = PathPolicy.Resolve(a.Path, "families");
            if (!full.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                throw new ToolException("INVALID_PATH", $"'{full}' is not an .rfa file.");
            if (!File.Exists(full))
                throw new ToolException("FILE_NOT_FOUND", $"'{full}' does not exist.");

            var familyName = System.IO.Path.GetFileNameWithoutExtension(full);
            var existing = FindFamily(doc, familyName);
            var options = new LoadOptions(a.Overwrite, a.OverwriteParameterValues);
            Family family = null;
            string status;
            var loadedTypes = new List<string>();
            var missingTypes = new List<string>();

            try
            {
                if (a.Types != null && a.Types.Count > 0)
                {
                    foreach (var t in a.Types.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct())
                    {
                        if (doc.LoadFamilySymbol(full, t.Trim(), options, out FamilySymbol symbol) && symbol != null)
                        {
                            loadedTypes.Add(symbol.Name);
                            family = symbol.Family;
                        }
                        else
                        {
                            var already = existing?.GetFamilySymbolIds().Select(id => doc.GetElement(id)).FirstOrDefault(s => s?.Name == t.Trim());
                            if (already != null) family = existing;
                            else missingTypes.Add(t);
                        }
                    }
                    status = loadedTypes.Count > 0 ? (existing != null ? "typesLoaded" : "loaded") : "alreadyLoaded";
                }
                else
                {
                    if (doc.LoadFamily(full, options, out family) && family != null)
                        status = existing != null ? "reloaded" : "loaded";
                    else
                    {
                        family = existing ?? FindFamily(doc, familyName);
                        status = "alreadyLoaded";
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("LOAD_FAILED", $"Revit could not load '{full}': {ex.Message}",
                    "The file may be from a newer Revit version, corrupt, or of a category this document can't hold.");
            }

            if (family == null)
                throw new ToolException("LOAD_FAILED", $"Revit did not load '{full}'" +
                    (missingTypes.Count > 0 ? $"; types not found in the file: {string.Join(", ", missingTypes)}." : "."),
                    existing == null ? null : "The family is already loaded; pass overwrite:true to reload it.");
            if (missingTypes.Count > 0)
                ctx.Warnings.Add(new ToolWarning("Types not found in the file: " + string.Join(", ", missingTypes)));
            if (status == "alreadyLoaded" && !a.Overwrite)
                ctx.Warnings.Add(new ToolWarning($"'{family.Name}' was already loaded and was not reloaded (pass overwrite:true to reload)."));

            var types = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>()
                .OrderBy(s => s.Name).Select(s => new { id = s.Id.GetValue(), name = s.Name, isActive = s.IsActive }).ToList();
            return new
            {
                status,
                path = full,
                familyId = family.Id.GetValue(),
                familyName = family.Name,
                category = family.FamilyCategory?.Name,
                categoryBuiltIn = CategoryService.BuiltInNameOf(family.FamilyCategory),
                typesLoaded = loadedTypes,
                types,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + $"'{family.Name}' {status} ({types.Count} type(s))."
            };
        }

        private static Family FindFamily(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
