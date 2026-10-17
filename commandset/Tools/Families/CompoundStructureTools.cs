using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Tools.Families.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Families
{
    public sealed class GetCompoundStructureArgs
    {
        [JsonProperty("type")]
        [Description("Wall, floor, roof or ceiling type: id, \"Family: Type\" or type name.")]
        public string Type { get; set; }

        [JsonProperty("elementId")]
        [Description("Alternative: an element whose type is read.")]
        public long? ElementId { get; set; }

        [JsonProperty("category")]
        [Description("Category (any language) to disambiguate the type name, e.g. Walls / Pisos.")]
        public string Category { get; set; }
    }

    /// <summary>FAM-05: layers of a wall/floor/roof/ceiling type.</summary>
    [McpTool("get_compound_structure", Kind = ToolKind.Read, Toolset = "families", Idempotent = true,
        Description = "Read the layer structure of a basic wall, floor, roof or ceiling type (by type or by an element " +
                      "of it): per layer index, function (structure, substrate, insulation, finish1, finish2, membrane), " +
                      "material, thickness in mm, core, variable and structural-material flags; total thickness, shell " +
                      "layer counts and how many instances use the type. Index 0 is the exterior (walls) or the top " +
                      "(floors/roofs). Stacked and curtain walls have no layers. Read-only.")]
    public sealed class GetCompoundStructureTool : McpTool<GetCompoundStructureArgs>
    {
        protected override void Validate(GetCompoundStructureArgs a)
        {
            if (string.IsNullOrWhiteSpace(a.Type) == (a.ElementId == null))
                throw new ToolException("INVALID_ARGUMENTS", "Give exactly one of type or elementId.");
        }

        protected override object Run(ToolContext ctx, GetCompoundStructureArgs a)
        {
            var type = a.ElementId.HasValue ? TypeTools.TypeOfElement(ctx, a.ElementId.Value) : TypeTools.ResolveType(ctx, a.Type, a.Category);
            var cs = TypeTools.Structure(type, out _);
            return TypeTools.Summary(ctx.Doc, type, cs);
        }
    }

    public sealed class LayerArg
    {
        [JsonProperty("function", Required = Required.Always)]
        [Description("structure, substrate, insulation, finish1, finish2, membrane or structuralDeck.")]
        [EnumValues("structure", "substrate", "insulation", "finish1", "finish2", "membrane", "structuralDeck")]
        public string Function { get; set; }

        [JsonProperty("material")]
        [Description("Material name or id; omit or \"byCategory\" for <By Category>.")]
        public string Material { get; set; }

        [JsonProperty("thickness", Required = Required.Always)]
        [Description("Layer thickness (0 only for membranes).")]
        [Unit("mm")]
        public double Thickness { get; set; }
    }

    public sealed class LayerPatchArg
    {
        [JsonProperty("index", Required = Required.Always)]
        [Description("Layer index as returned by get_compound_structure.")]
        public int Index { get; set; }

        [JsonProperty("function")]
        [Description("New function.")]
        [EnumValues("structure", "substrate", "insulation", "finish1", "finish2", "membrane", "structuralDeck")]
        public string Function { get; set; }

        [JsonProperty("material")]
        [Description("New material, name or id (\"byCategory\" for <By Category>).")]
        public string Material { get; set; }

        [JsonProperty("thickness")]
        [Description("New thickness.")]
        [Unit("mm")]
        public double? Thickness { get; set; }
    }

    public sealed class SetCompoundStructureArgs
    {
        [JsonProperty("type")]
        [Description("Wall, floor, roof or ceiling type: id, \"Family: Type\" or type name.")]
        public string Type { get; set; }

        [JsonProperty("elementId")]
        [Description("Alternative: an element of the type. With the default duplicate behaviour this element is switched to the new type.")]
        public long? ElementId { get; set; }

        [JsonProperty("category")]
        [Description("Category (any language) to disambiguate the type name.")]
        public string Category { get; set; }

        [JsonProperty("inPlace")]
        [Description("Edit the type itself (changes EVERY instance). Default false: duplicate the type first and edit the copy.")]
        [DefaultValue(false)]
        public bool InPlace { get; set; }

        [JsonProperty("newTypeName")]
        [Description("Name of the duplicated type. Default: the original name with \" (2)\" (first free).")]
        public string NewTypeName { get; set; }

        [JsonProperty("assignTo")]
        [MaxLength(5000)]
        [Description("Elements to switch to the new (duplicated) type. Default: elementId when given.")]
        public List<long> AssignTo { get; set; }

        [JsonProperty("layers")]
        [MaxLength(50)]
        [Description("Full replacement of the layers, exterior/top first. Alternative to patch.")]
        public List<LayerArg> Layers { get; set; }

        [JsonProperty("exteriorShellLayers")]
        [Description("With layers: how many layers are outside the core on the exterior/top side.")]
        public int? ExteriorShellLayers { get; set; }

        [JsonProperty("interiorShellLayers")]
        [Description("With layers: how many layers are outside the core on the interior/bottom side.")]
        public int? InteriorShellLayers { get; set; }

        [JsonProperty("patch")]
        [MaxLength(50)]
        [Description("Changes to existing layers by index (function, material, thickness).")]
        public List<LayerPatchArg> Patch { get; set; }

        [JsonProperty("variableLayerIndex")]
        [Description("Floors/roofs: layer whose thickness varies with shape editing; -1 clears it.")]
        public int? VariableLayerIndex { get; set; }

        [JsonProperty("structuralMaterialIndex")]
        [Description("Layer that gives the structural material.")]
        public int? StructuralMaterialIndex { get; set; }
    }

    /// <summary>FAM-06: edit layers of a system type, duplicating it first unless told otherwise.</summary>
    [McpTool("set_compound_structure", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "families",
        Description = "Edit the layers of a basic wall, floor, roof or ceiling type: full replacement (layers with " +
                      "function, material, thickness in mm, shell counts) or patch by index; variable and structural " +
                      "layer. By default the type is DUPLICATED first (newTypeName) and only elementId / assignTo are " +
                      "switched to the copy, so other instances keep their structure; inPlace:true edits the type " +
                      "itself. Revit's validity check runs before writing; the result is read back. Preview with " +
                      "dryRun:true (no approval), then run (needs approval). Undo: Ctrl+Z.")]
    public sealed class SetCompoundStructureTool : McpTool<SetCompoundStructureArgs>
    {
        protected override void Validate(SetCompoundStructureArgs a)
        {
            if (string.IsNullOrWhiteSpace(a.Type) == (a.ElementId == null))
                throw new ToolException("INVALID_ARGUMENTS", "Give exactly one of type or elementId.");
            bool hasLayers = (a.Layers?.Count ?? 0) > 0, hasPatch = (a.Patch?.Count ?? 0) > 0;
            if (hasLayers && hasPatch) throw new ToolException("INVALID_ARGUMENTS", "Use layers or patch, not both.");
            if (!hasLayers && !hasPatch && a.VariableLayerIndex == null && a.StructuralMaterialIndex == null)
                throw new ToolException("INVALID_ARGUMENTS", "Nothing to change: give layers, patch, variableLayerIndex or structuralMaterialIndex.");
            if (a.InPlace && (a.NewTypeName != null || (a.AssignTo?.Count ?? 0) > 0))
                throw new ToolException("INVALID_ARGUMENTS", "newTypeName / assignTo only apply when duplicating (inPlace:false).");
            foreach (var l in a.Layers ?? new List<LayerArg>())
                if (l == null || l.Thickness < 0) throw new ToolException("INVALID_ARGUMENTS", "Layer thickness must be 0 or more.");
        }

        protected override object Run(ToolContext ctx, SetCompoundStructureArgs a)
        {
            var doc = ctx.Doc;
            var type = a.ElementId.HasValue ? TypeTools.TypeOfElement(ctx, a.ElementId.Value) : TypeTools.ResolveType(ctx, a.Type, a.Category);
            var cs = TypeTools.Structure(type, out _);
            if (cs.IsVerticallyCompound)
                throw new ToolException("NOT_SUPPORTED", $"'{type.Name}' has a vertically compound structure (regions); editing it through the API is not supported here.");
            var before = TypeTools.Summary(doc, type, cs);

            if ((a.Layers?.Count ?? 0) > 0)
            {
                int oldCount = cs.LayerCount;
                int oldExt = cs.GetNumberOfShellLayers(ShellLayerType.Exterior), oldInt = cs.GetNumberOfShellLayers(ShellLayerType.Interior);
                var layers = a.Layers.Select(l => new CompoundStructureLayer(EditGeometry.Ft(l.Thickness), TypeTools.ParseFunction(l.Function),
                    MaterialId(ctx, l.Material))).ToList();
                cs.SetLayers(layers);
                int ext = a.ExteriorShellLayers ?? (layers.Count == oldCount ? oldExt : 0);
                int inn = a.InteriorShellLayers ?? (layers.Count == oldCount ? oldInt : 0);
                if (ext + inn >= layers.Count)
                    throw new ToolException("INVALID_STRUCTURE", "The shell layers leave no core layer.", "Lower exteriorShellLayers / interiorShellLayers.");
                cs.SetNumberOfShellLayers(ShellLayerType.Exterior, ext);
                cs.SetNumberOfShellLayers(ShellLayerType.Interior, inn);
            }
            foreach (var p in a.Patch ?? new List<LayerPatchArg>())
            {
                if (p.Index < 0 || p.Index >= cs.LayerCount)
                    throw new ToolException("INVALID_ARGUMENTS", $"Layer index {p.Index} does not exist (0..{cs.LayerCount - 1}).");
                if (p.Function != null) cs.SetLayerFunction(p.Index, TypeTools.ParseFunction(p.Function));
                if (p.Material != null) cs.SetMaterialId(p.Index, MaterialId(ctx, p.Material));
                if (p.Thickness.HasValue)
                {
                    if (p.Thickness.Value < 0) throw new ToolException("INVALID_ARGUMENTS", "thickness must be 0 or more.");
                    cs.SetLayerWidth(p.Index, EditGeometry.Ft(p.Thickness.Value));
                }
            }
            if (a.VariableLayerIndex.HasValue)
            {
                var i = a.VariableLayerIndex.Value;
                if (i >= 0 && (i >= cs.LayerCount || !cs.CanLayerBeVariable(i)))
                    throw new ToolException("INVALID_ARGUMENTS", $"Layer {i} can't be the variable layer.");
                cs.VariableLayerIndex = i < 0 ? -1 : i;
            }
            if (a.StructuralMaterialIndex.HasValue)
            {
                var i = a.StructuralMaterialIndex.Value;
                if (i < 0 || i >= cs.LayerCount || !cs.CanLayerBeStructuralMaterial(i))
                    throw new ToolException("INVALID_ARGUMENTS", $"Layer {i} can't give the structural material.");
                cs.StructuralMaterialIndex = i;
            }
            TypeTools.EnsureValid(doc, cs, type);

            var target = type;
            var assigned = new List<long>();
            if (!a.InPlace)
            {
                var name = string.IsNullOrWhiteSpace(a.NewTypeName) ? TypeTools.FreeName(doc, type, type.Name + " (2)") : a.NewTypeName.Trim();
                target = TypeTools.Duplicate(ctx, type, name);
            }
            try
            {
                ((HostObjAttributes)target).SetCompoundStructure(cs);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("SET_FAILED", $"Revit refused the new structure: {ex.Message}");
            }

            if (!a.InPlace)
            {
                var ids = a.AssignTo ?? (a.ElementId.HasValue && !(doc.GetElement(a.ElementId.Value.ToRevitElementId()) is ElementType)
                    ? new List<long> { a.ElementId.Value } : new List<long>());
                foreach (var e in EditGeometry.Elements(doc, ids))
                {
                    if (e.GetTypeId() == target.Id) continue;
                    if (!e.GetValidTypes().Contains(target.Id))
                        throw new ToolException("TYPE_NOT_VALID", $"Element {e.Id.GetValue()} can't use '{target.Name}'.");
                    e.ChangeTypeId(target.Id);
                    if (e.GetTypeId() != target.Id) throw new ToolException("TYPE_CHANGE_FAILED", $"Element {e.Id.GetValue()} kept its type.");
                    assigned.Add(e.Id.GetValue());
                }
                if (assigned.Count == 0)
                    ctx.Warnings.Add(new ToolWarning($"The new type '{target.Name}' is not used by any element yet; pass assignTo or use change_element_type."));
            }
            else
            {
                var n = TypeTools.CountInstances(doc, type);
                if (n > 0) ctx.Warnings.Add(new ToolWarning($"Edited in place: {n} instance(s) of '{type.Name}' changed."));
            }

            return new
            {
                duplicated = !a.InPlace,
                sourceTypeId = type.Id.GetValue(),
                assignedElementIds = assigned,
                before,
                after = TypeTools.Summary(doc, target, ((HostObjAttributes)target).GetCompoundStructure()),
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                (a.InPlace ? $"Edited '{type.Name}' in place." : $"Created '{target.Name}' with the new layers; {assigned.Count} element(s) switched.")
            };
        }

        internal static ElementId MaterialId(ToolContext ctx, string material)
        {
            if (string.IsNullOrWhiteSpace(material)) return ElementId.InvalidElementId;
            var key = material.Trim().ToLowerInvariant().Replace(" ", "");
            if (key == "bycategory" || key == "<bycategory>" || key == "none" || key == "<porcategoria>" || key == "porcategoria")
                return ElementId.InvalidElementId;
            return ctx.Services.Lookup.Material(material).Id;
        }
    }
}
