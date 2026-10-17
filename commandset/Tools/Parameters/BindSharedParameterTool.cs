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

namespace RevitMCPCommandSet.Tools.Parameters
{
    public sealed class BindSharedParameterArgs
    {
        [JsonProperty("guid")]
        [Description("GUID of the shared parameter in the shared parameter file (preferred).")]
        public string Guid { get; set; }

        [JsonProperty("name")]
        [Description("Name of the shared parameter, when no guid is given (must be unique in the file).")]
        public string Name { get; set; }

        [JsonProperty("sharedParameterFile")]
        [Description("Shared parameter .txt file to read, inside the allowed folders. Default: the file configured in Revit (left unchanged).")]
        public string SharedParameterFile { get; set; }

        [JsonProperty("categories", Required = Required.Always)]
        [Required]
        [MinLength(1)]
        [Description("Categories to bind to, in any language (OST_Doors, Doors, Portas).")]
        public List<string> Categories { get; set; }

        [JsonProperty("bindingKind")]
        [Description("instance (default) or type.")]
        [EnumValues("instance", "type")]
        [DefaultValue("instance")]
        public string BindingKind { get; set; } = "instance";

        [JsonProperty("group")]
        [Description("Properties palette group: label as Revit shows it (\"Dados\", \"Identity Data\"), short name (data, identity_data) or GroupTypeId. Default Data for new bindings; kept for existing ones unless given.")]
        public string Group { get; set; }

        [JsonProperty("mode")]
        [Description("add (default): fails if already bound. addCategories: add these categories to the existing binding. rebind: replace categories, kind and group.")]
        [EnumValues("add", "addCategories", "rebind")]
        [DefaultValue("add")]
        public string Mode { get; set; } = "add";

        [JsonProperty("varyByGroup")]
        [Description("Instance bindings only: allow values to vary between group instances.")]
        public bool? VaryByGroup { get; set; }
    }

    /// <summary>PAR-11: bind (or re-bind) a shared parameter to categories.</summary>
    [McpTool("bind_shared_parameter", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "parameters",
        Description = "Bind a shared parameter (by guid, or unique name, from the shared parameter file configured in " +
                      "Revit or one in the allowed folders) to categories as an instance or type parameter, in a " +
                      "properties group. mode add / addCategories / rebind (rebind can change kind and group; removing " +
                      "categories drops their values). Categories that don't accept parameters are refused. Project-only " +
                      "(non-shared) parameters are not created here. Check first with list_parameter_bindings. Needs " +
                      "approval. Undo: Ctrl+Z.")]
    public sealed class BindSharedParameterTool : McpTool<BindSharedParameterArgs>
    {
        protected override void Validate(BindSharedParameterArgs a)
        {
            if (string.IsNullOrWhiteSpace(a.Guid) && string.IsNullOrWhiteSpace(a.Name))
                throw new ToolException("INVALID_ARGUMENTS", "Give the shared parameter's guid (or its name).");
            if (!string.IsNullOrWhiteSpace(a.Guid) && !System.Guid.TryParse(a.Guid.Trim(), out _))
                throw new ToolException("INVALID_ARGUMENTS", $"'{a.Guid}' is not a GUID.");
        }

        protected override object Run(ToolContext ctx, BindSharedParameterArgs a)
        {
            var doc = ctx.Doc;
            if (doc.IsFamilyDocument)
                throw new ToolException("NOT_A_PROJECT", "Parameter bindings exist only in projects; this is a family document.");
            var app = ctx.UiApp?.Application ?? doc.Application;

            var definition = FindDefinition(app, a, out var fileUsed);
            var categories = ctx.Services.Categories.ResolveAll(a.Categories);
            var categorySet = app.Create.NewCategorySet();
            foreach (var c in categories)
            {
                if (c.Category == null)
                    throw new ToolException("CATEGORY_NOT_BINDABLE", $"'{c.Name}' has no category object in this document.");
                if (!c.Category.AllowsBoundParameters)
                    throw new ToolException("CATEGORY_NOT_BINDABLE", $"'{c.Name}' does not accept project or shared parameters.");
                categorySet.Insert(c.Category);
            }

            var rows = BindingRow.All(doc);
            var existing = rows.FirstOrDefault(r => r.GuidValue == definition.GUID);
            var sameName = rows.FirstOrDefault(r => r.GuidValue != definition.GUID &&
                                                    string.Equals(r.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
            if (existing == null && sameName != null)
                throw new ToolException("NAME_CONFLICT",
                    $"Another {(sameName.IsShared ? "shared" : "project")} parameter named '{definition.Name}' is already bound" +
                    (sameName.GuidValue.HasValue ? $" (guid {sameName.GuidValue})" : string.Empty) + ".",
                    "Use list_parameter_bindings to inspect it; bind a parameter with a different name.", "list_parameter_bindings");

            var mode = (a.Mode ?? "add").Trim();
            var kind = (a.BindingKind ?? "instance").Trim().ToLowerInvariant();
            var before = existing == null ? null : Snapshot(existing);
            bool ok;
            string action;

            if (existing == null)
            {
                var group = a.Group == null ? GroupTypeId.Data : ResolveGroup(a.Group);
                ok = doc.ParameterBindings.Insert(definition, NewBinding(app, kind, categorySet), group);
                action = "bound";
            }
            else if (mode == "add")
            {
                throw new ToolException("ALREADY_BOUND",
                    $"'{definition.Name}' is already bound as {existing.BindingKind} to {existing.Categories.Count} category(ies).",
                    "Use mode addCategories to add categories, or rebind to replace them.", "list_parameter_bindings");
            }
            else
            {
                var group = a.Group == null ? existing.Definition.GetGroupTypeId() : ResolveGroup(a.Group);
                if (mode == "addCategories")
                {
                    if (existing.BindingKind != kind)
                        ctx.Warnings.Add(new ToolWarning(
                            $"'{definition.Name}' stays a {existing.BindingKind} parameter: addCategories keeps the kind (use mode rebind to change it)."));
                    kind = existing.BindingKind;
                    foreach (Category c in existing.Binding.Categories) if (c != null && !categorySet.Contains(c)) categorySet.Insert(c);
                    action = "categoriesAdded";
                }
                else
                {
                    var dropped = existing.CategoryIds.Except(categories.Select(c => c.Id.GetValue())).ToList();
                    if (dropped.Count > 0)
                        ctx.Warnings.Add(new ToolWarning($"Rebind removes {dropped.Count} category(ies) from '{definition.Name}'; their values are lost."));
                    action = "rebound";
                }
                ok = doc.ParameterBindings.ReInsert(existing.Definition, NewBinding(app, kind, categorySet), group);
            }
            if (!ok)
                throw new ToolException("BIND_FAILED", $"Revit refused to bind '{definition.Name}'.",
                    "Check the categories and that the parameter's data type is allowed for them.");

            var after = BindingRow.All(doc).FirstOrDefault(r => r.GuidValue == definition.GUID);
            if (after == null)
                throw new ToolException("BIND_FAILED", $"'{definition.Name}' is not bound after the change.");

            if (a.VaryByGroup.HasValue && after.Definition is InternalDefinition internalDef)
            {
                if (after.BindingKind != "instance")
                    ctx.Warnings.Add(new ToolWarning("varyByGroup applies to instance bindings only; ignored."));
                else
                {
                    try { internalDef.SetAllowVaryBetweenGroups(doc, a.VaryByGroup.Value); }
                    catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        ctx.Warnings.Add(new ToolWarning("Could not change varyByGroup: " + ex.Message));
                    }
                    after = BindingRow.All(doc).First(r => r.GuidValue == definition.GUID);
                }
            }

            return new
            {
                action,
                name = definition.Name,
                guid = definition.GUID.ToString(),
                sharedParameterFile = fileUsed,
                before,
                after = Snapshot(after),
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"'{definition.Name}' {action}: {after.BindingKind}, {after.Categories.Count} category(ies)."
            };
        }

        private static object Snapshot(BindingRow r) => new
        {
            bindingKind = r.BindingKind,
            group = r.Group,
            groupLabel = r.GroupLabel,
            variesAcrossGroups = r.VariesAcrossGroups,
            categories = r.Categories
        };

        private static Binding NewBinding(Autodesk.Revit.ApplicationServices.Application app, string kind, CategorySet set) =>
            kind == "type" ? (Binding)app.Create.NewTypeBinding(set) : app.Create.NewInstanceBinding(set);

        private static ExternalDefinition FindDefinition(Autodesk.Revit.ApplicationServices.Application app, BindSharedParameterArgs a, out string fileUsed)
        {
            var original = app.SharedParametersFilename;
            DefinitionFile file;
            if (!string.IsNullOrWhiteSpace(a.SharedParameterFile))
            {
                fileUsed = PathPolicy.Resolve(a.SharedParameterFile, "shared-parameters");
                if (!File.Exists(fileUsed))
                    throw new ToolException("FILE_NOT_FOUND", $"'{fileUsed}' does not exist.");
                try
                {
                    app.SharedParametersFilename = fileUsed;
                    file = app.OpenSharedParameterFile();
                }
                finally
                {
                    app.SharedParametersFilename = original;
                }
            }
            else
            {
                fileUsed = original;
                if (string.IsNullOrWhiteSpace(original) || !File.Exists(original))
                    throw new ToolException("NO_SHARED_PARAMETER_FILE", "Revit has no shared parameter file configured (or it is missing).",
                        "Pass sharedParameterFile with a file inside the allowed folders, or ask the user to set it in Manage > Shared Parameters.");
                file = app.OpenSharedParameterFile();
            }
            if (file == null)
                throw new ToolException("INVALID_FILE", $"'{fileUsed}' could not be read as a shared parameter file.");

            var all = file.Groups.Cast<DefinitionGroup>()
                .SelectMany(g => g.Definitions.Cast<Definition>().OfType<ExternalDefinition>().Select(d => (group: g.Name, def: d)))
                .ToList();
            if (!string.IsNullOrWhiteSpace(a.Guid))
            {
                var guid = System.Guid.Parse(a.Guid.Trim());
                var hit = all.FirstOrDefault(x => x.def.GUID == guid).def;
                if (hit == null)
                    throw new ToolException("PARAMETER_NOT_FOUND", $"GUID {guid} is not in '{fileUsed}'.");
                return hit;
            }

            var matches = all.Where(x => TextMatch.Fold(x.def.Name) == TextMatch.Fold(a.Name.Trim())).ToList();
            if (matches.Count == 1) return matches[0].def;
            if (matches.Count > 1)
                throw new ToolException("AMBIGUOUS_PARAMETER",
                    $"'{a.Name}' appears {matches.Count} times in the file: " +
                    string.Join("; ", matches.Select(m => $"group '{m.group}', guid {m.def.GUID}")) + ".",
                    "Pass the guid instead.");
            var suggestions = TextMatch.Suggest(a.Name, all.Select(x => x.def.Name));
            throw new ToolException("PARAMETER_NOT_FOUND", $"No shared parameter named '{a.Name}' in '{fileUsed}'.",
                suggestions.Count > 0 ? "Did you mean: " + string.Join(", ", suggestions) + "?" : null);
        }

        /// <summary>A built-in properties group by label (any Revit language), short name or GroupTypeId.</summary>
        internal static ForgeTypeId ResolveGroup(string text)
        {
            var t = text.Trim();
            var groups = ParameterUtils.GetAllBuiltInGroups();
            var byId = groups.FirstOrDefault(g => string.Equals(g.TypeId, t, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;
            var key = TextMatch.Key(t).Replace(" ", "").Replace("_", "");
            var byShort = groups.FirstOrDefault(g => TextMatch.Key(Short(g)).Replace("_", "") == key);
            if (byShort != null) return byShort;
            return NameMatcher.Single(t, groups.Select(g => new NamedCandidate<ForgeTypeId>(Label(g), g, g.TypeId)), "parameter group");
        }

        private static string Short(ForgeTypeId g)
        {
            var id = g.TypeId ?? string.Empty;
            var colon = id.IndexOf(':');
            var dash = id.LastIndexOf('-');
            return colon >= 0 && dash > colon ? id.Substring(colon + 1, dash - colon - 1) : id;
        }

        private static string Label(ForgeTypeId g)
        {
            try { return LabelUtils.GetLabelForGroup(g); }
            catch { return Short(g); }
        }
    }
}
