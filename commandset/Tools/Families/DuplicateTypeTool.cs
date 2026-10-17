using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Editing;
using RevitMCPCommandSet.Tools.Families.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Families
{
    public sealed class DuplicateTypeArgs
    {
        [JsonProperty("sourceType", Required = Required.Always)]
        [Required]
        [Description("Type to duplicate: id, \"Family: Type\" or type name (any kind: loadable family, wall/floor, text, dimension, group type...).")]
        public string SourceType { get; set; }

        [JsonProperty("category")]
        [Description("Category (any language) to disambiguate the source type name.")]
        public string Category { get; set; }

        [JsonProperty("newName", Required = Required.Always)]
        [Required]
        [Description("Name of the new type (unique within its family).")]
        public string NewName { get; set; }

        [JsonProperty("typeParameters")]
        [MaxLength(100)]
        [Description("Type parameter values for the new type (set_type_parameters rules: wire units mm, m2, m3, deg, %).")]
        public List<ModifyParameterItem> TypeParameters { get; set; }

        [JsonProperty("ifExists")]
        [Description("When a type with newName already exists: error (default) or reuse it (typeParameters are then applied to it).")]
        [EnumValues("error", "reuse")]
        [DefaultValue("error")]
        public string IfExists { get; set; } = "error";
    }

    /// <summary>FAM-03: duplicate any ElementType, optionally with type parameter overrides.</summary>
    [McpTool("duplicate_type", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "families",
        Description = "Duplicate a type (loadable family type, wall/floor/roof type, text or dimension style, group " +
                      "type...) under a new name, optionally setting type parameters on the copy (wire units mm, m2, " +
                      "m3, deg, %; read back). ifExists=reuse returns the existing type of that name instead of failing. " +
                      "The original and its instances are not touched. Returns the new type id. Undo: Ctrl+Z.")]
    public sealed class DuplicateTypeTool : McpTool<DuplicateTypeArgs>
    {
        protected override object Run(ToolContext ctx, DuplicateTypeArgs a)
        {
            var source = TypeTools.ResolveType(ctx, a.SourceType, a.Category);
            var name = a.NewName.Trim();
            var existing = TypeTools.Sibling(ctx.Doc, source, name);
            bool reused = false;
            Autodesk.Revit.DB.ElementType created;
            if (existing != null)
            {
                if (a.IfExists != "reuse")
                    throw new ToolException("NAME_COLLISION", $"'{LookupService.FullName(existing)}' already exists (id {existing.Id.GetValue()}).",
                        "Choose another newName, or pass ifExists:\"reuse\".");
                created = existing;
                reused = true;
            }
            else created = TypeTools.Duplicate(ctx, source, name);

            ParameterWriteResult parameters = null;
            if (a.TypeParameters != null && a.TypeParameters.Count > 0)
            {
                var ps = ctx.Services.Parameters;
                var id = created.Id.GetValue().ToString(CultureInfo.InvariantCulture);
                var plan = ps.Plan(a.TypeParameters.Select(p => new ParameterWriteRequest { Element = id, Parameter = p.Parameter, Value = p.Value, Unit = p.Unit }).ToList(),
                    new ParameterPlanOptions { Target = ParameterTarget.Type });
                if (plan.HasErrors)
                    throw new ToolException("PARAMETER_WRITE_REFUSED", $"Nothing was created. {plan.FailureSummary()}");
                parameters = ps.Apply(plan);
                if (parameters.Failed > 0)
                    throw new ToolException("PARAMETER_WRITE_FAILED", "Nothing was created: " +
                        string.Join(" ", parameters.Items.Where(i => i.Status != "ok" && i.Status != "duplicate").Select(i => $"'{i.Parameter?.Name}': {i.Status} - {i.Message}")));
            }

            return new
            {
                typeId = created.Id.GetValue(),
                name = created.Name,
                familyName = LookupService.SafeFamilyName(created),
                sourceTypeId = source.Id.GetValue(),
                reused,
                parameters = parameters?.Items,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                (reused ? $"Reused '{LookupService.FullName(created)}'." : $"Created '{LookupService.FullName(created)}' from '{source.Name}'.")
            };
        }
    }
}
