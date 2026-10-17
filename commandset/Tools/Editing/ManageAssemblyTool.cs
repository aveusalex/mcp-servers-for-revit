using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class ManageAssemblyArgs
    {
        [JsonProperty("operation", Required = Required.Always)]
        [Required]
        [EnumValues("create", "add", "remove", "rename", "disassemble")]
        [Description("create: new assembly (conjunto) from elementIds. add / remove: members of an existing assembly. rename: the assembly type name. disassemble: dissolve the assembly (members stay in the model; its assembly views and parts list are deleted, as Revit requires).")]
        public string Operation { get; set; }

        [JsonProperty("assemblyId")]
        [Description("Assembly instance id (add, remove, rename, disassemble).")]
        public long? AssemblyId { get; set; }

        [JsonProperty("elementIds")]
        [MaxLength(2000)]
        [Description("Elements to group (create) or to add / remove.")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("name")]
        [Description("Assembly type name (create, rename). Must be unique within its naming category. Default on create: Revit's automatic name.")]
        public string Name { get; set; }

        [JsonProperty("namingCategory")]
        [Description("create: category that names the assembly (pt-BR or English, e.g. \"Casework\" / \"Marcenaria\"); must be the category of one of the members. Default: the first element's category.")]
        public string NamingCategory { get; set; }
    }

    /// <summary>EDT-09: create assemblies and edit their members.</summary>
    [McpTool("manage_assembly", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "editing", Batchable = false,
        Description = "Assemblies (conjuntos, e.g. a millwork module or a prefab piece): create one from element ids with " +
                      "an optional type name and naming category, add or remove members, rename the assembly type, or " +
                      "disassemble it (members stay). Revit commits the new assembly before it can be named, so create " +
                      "runs as one undo step made of two transactions. Then use create_assembly_views for its views and " +
                      "schedules. Undo: Ctrl+Z.")]
    public sealed class ManageAssemblyTool : McpTool<ManageAssemblyArgs>, ISelfTransactedTool
    {
        protected override void Validate(ManageAssemblyArgs a)
        {
            a.Operation = a.Operation.Trim().ToLowerInvariant();
            bool needIds = a.Operation == "create" || a.Operation == "add" || a.Operation == "remove";
            if (needIds && (a.ElementIds == null || a.ElementIds.Count == 0))
                throw new ToolException("INVALID_ARGUMENTS", $"{a.Operation} needs elementIds.");
            if (a.Operation != "create" && !a.AssemblyId.HasValue)
                throw new ToolException("INVALID_ARGUMENTS", $"{a.Operation} needs assemblyId.");
            if (a.Operation == "rename" && string.IsNullOrWhiteSpace(a.Name))
                throw new ToolException("INVALID_ARGUMENTS", "rename needs name.");
        }

        protected override object Run(ToolContext ctx, ManageAssemblyArgs a)
        {
            using (var own = new OwnTransactions(ctx, "manage_assembly"))
            {
                object result;
                switch (a.Operation)
                {
                    case "create": result = Create(ctx, own, a); break;
                    case "add":
                    case "remove": result = Members(ctx, own, a); break;
                    case "rename": result = Rename(ctx, own, a); break;
                    default: result = Disassemble(ctx, own, a); break;
                }
                own.Complete();
                return result;
            }
        }

        private static List<ElementId> Elements(ToolContext ctx, IEnumerable<long> ids)
        {
            var list = new List<ElementId>();
            foreach (var id in ids.Distinct())
            {
                var e = ctx.Services.Lookup.Element(id);
                list.Add(e.Id);
            }
            return list;
        }

        private static AssemblyInstance Assembly(ToolContext ctx, long id) =>
            ctx.Services.Lookup.Element(id) as AssemblyInstance
            ?? throw new ToolException("NOT_AN_ASSEMBLY", $"{id} is not an assembly instance.", "Use find_elements with category Assemblies / Conjuntos.", "find_elements");

        private static object Describe(Document doc, AssemblyInstance asm) => new
        {
            assemblyId = asm.Id.GetValue(),
            typeId = asm.GetTypeId().GetValue(),
            name = asm.AssemblyTypeName,
            namingCategory = Category.GetCategory(doc, asm.NamingCategoryId)?.Name,
            memberIds = asm.GetMemberIds().Select(i => i.GetValue()).OrderBy(i => i).ToList()
        };

        private static string Prefix(ToolContext ctx) => ctx.DryRun ? "[dry run, rolled back] " : string.Empty;

        private static object Create(ToolContext ctx, OwnTransactions own, ManageAssemblyArgs a)
        {
            var doc = ctx.Doc;
            var ids = Elements(ctx, a.ElementIds);
            foreach (var id in ids)
            {
                var e = doc.GetElement(id);
                if (e.AssemblyInstanceId != null && e.AssemblyInstanceId != ElementId.InvalidElementId)
                    throw new ToolException("ALREADY_IN_ASSEMBLY", $"Element {id.GetValue()} already belongs to assembly {e.AssemblyInstanceId.GetValue()}.",
                        "Remove it from that assembly first (operation remove).");
            }
            ElementId naming;
            if (!string.IsNullOrWhiteSpace(a.NamingCategory)) naming = ctx.Services.Categories.Resolve(a.NamingCategory).Id;
            else naming = doc.GetElement(ids[0]).Category?.Id
                          ?? throw new ToolException("INVALID_ARGUMENTS", $"Element {ids[0].GetValue()} has no category; pass namingCategory.");
            if (!AssemblyInstance.IsValidNamingCategory(doc, naming, ids))
                throw new ToolException("INVALID_NAMING_CATEGORY", "The naming category must be the category of one of the members.",
                    "Pass namingCategory with a members' category.");
            if (!AssemblyInstance.AreElementsValidForAssembly(doc, ids, ElementId.InvalidElementId))
                throw new ToolException("ELEMENTS_NOT_VALID", "Revit does not accept these elements in one assembly (annotation, datum, view-specific or already assembled elements are not allowed).");

            var asm = own.Step("MCP: create assembly", () => AssemblyInstance.Create(doc, ids, naming));
            bool named = false;
            if (!string.IsNullOrWhiteSpace(a.Name))
            {
                var name = a.Name.Trim();
                try
                {
                    own.Step("MCP: name assembly", () => { asm.AssemblyTypeName = name; });
                    named = asm.AssemblyTypeName == name;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    ctx.Warnings.Add(new ToolWarning($"The assembly was created but not named '{name}': {ex.Message} (names must be unique within the naming category)."));
                }
            }
            return new
            {
                operation = "create",
                assembly = Describe(doc, asm),
                named,
                changeSummary = Prefix(ctx) + $"Assembly '{asm.AssemblyTypeName}' created with {ids.Count} member(s)."
            };
        }

        private static object Members(ToolContext ctx, OwnTransactions own, ManageAssemblyArgs a)
        {
            var doc = ctx.Doc;
            var asm = Assembly(ctx, a.AssemblyId.Value);
            var ids = Elements(ctx, a.ElementIds);
            var members = new HashSet<ElementId>(asm.GetMemberIds());
            List<ElementId> skipped;
            if (a.Operation == "add")
            {
                skipped = ids.Where(members.Contains).ToList();
                ids = ids.Where(i => !members.Contains(i)).ToList();
                if (ids.Count > 0 && !AssemblyInstance.AreElementsValidForAssembly(doc, ids, asm.Id))
                    throw new ToolException("ELEMENTS_NOT_VALID", "Revit does not accept these elements in this assembly (they may belong to another assembly or be of a category assemblies can't hold).");
                if (ids.Count > 0) own.Step("MCP: add to assembly", () => asm.AddMemberIds(ids));
            }
            else
            {
                skipped = ids.Where(i => !members.Contains(i)).ToList();
                ids = ids.Where(members.Contains).ToList();
                if (ids.Count > 0 && !AssemblyInstance.CanRemoveElementsFromAssembly(asm, ids))
                    throw new ToolException("CANNOT_REMOVE", "Revit can't remove these members (an assembly can't be left empty).",
                        "Use operation disassemble to dissolve it.");
                if (ids.Count > 0) own.Step("MCP: remove from assembly", () => asm.RemoveMemberIds(ids));
            }
            if (skipped.Count > 0)
                ctx.Warnings.Add(new ToolWarning($"{skipped.Count} element(s) skipped (already {(a.Operation == "add" ? "members" : "not members")}).",
                    skipped.Select(i => i.GetValue()).ToList()));
            return new
            {
                operation = a.Operation,
                changed = ids.Select(i => i.GetValue()).ToList(),
                skipped = skipped.Select(i => i.GetValue()).ToList(),
                assembly = Describe(doc, asm),
                changeSummary = Prefix(ctx) + $"{ids.Count} member(s) {(a.Operation == "add" ? "added to" : "removed from")} '{asm.AssemblyTypeName}'."
            };
        }

        private static object Rename(ToolContext ctx, OwnTransactions own, ManageAssemblyArgs a)
        {
            var doc = ctx.Doc;
            var asm = Assembly(ctx, a.AssemblyId.Value);
            var before = asm.AssemblyTypeName;
            var name = a.Name.Trim();
            var sharing = new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance))
                .Count(e => e.GetTypeId() == asm.GetTypeId() && e.Id != asm.Id);
            try { own.Step("MCP: rename assembly", () => { asm.AssemblyTypeName = name; }); }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("NAME_COLLISION", $"'{name}' can't be used: {ex.Message}", "Assembly names must be unique within the naming category.");
            }
            if (sharing > 0) ctx.Warnings.Add(new ToolWarning($"{sharing} other instance(s) of the same assembly type were renamed too."));
            return new
            {
                operation = "rename",
                before,
                assembly = Describe(doc, asm),
                changeSummary = Prefix(ctx) + $"Assembly '{before}' renamed to '{asm.AssemblyTypeName}'."
            };
        }

        private static object Disassemble(ToolContext ctx, OwnTransactions own, ManageAssemblyArgs a)
        {
            var asm = Assembly(ctx, a.AssemblyId.Value);
            var name = asm.AssemblyTypeName;
            var members = asm.GetMemberIds().Select(i => i.GetValue()).OrderBy(i => i).ToList();
            var id = asm.Id.GetValue();
            // Assembly views can't outlive their assembly, and Revit fails the transaction with an error
            // ("the views will be deleted") instead of deleting them: delete them first and say so.
            var views = new FilteredElementCollector(ctx.Doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => v.AssociatedAssemblyInstanceId == asm.Id)
                .Select(v => new { id = v.Id.GetValue(), name = v.Name }).ToList();
            own.Step("MCP: disassemble", () =>
            {
                if (views.Count > 0) ctx.Doc.Delete(views.Select(v => v.id.ToRevitElementId()).ToList());
                asm.Disassemble();
            });
            return new
            {
                operation = "disassemble",
                assemblyId = id,
                name,
                freedMemberIds = members,
                deletedAssemblyViews = views,
                changeSummary = Prefix(ctx) + $"Assembly '{name}' disassembled; {members.Count} element(s) kept" +
                                (views.Count > 0 ? $"; {views.Count} assembly view(s) deleted with it." : ".")
            };
        }
    }
}
