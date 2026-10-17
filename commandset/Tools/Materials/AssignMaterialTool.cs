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

namespace RevitMCPCommandSet.Tools.Materials
{
    public sealed class AssignMaterialArgs
    {
        [JsonProperty("elementIds", Required = Required.Always)]
        [Required]
        [MinLength(1)]
        [MaxLength(2000)]
        [Description("Elements to change (walls, floors, ceilings, roofs for target=layer; any element for target=parameter).")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("material", Required = Required.Always)]
        [Required]
        [Description("Material to apply: name or id.")]
        public string Material { get; set; }

        [JsonProperty("target")]
        [Description("layer (default): a layer of the type's structure (\"trocar acabamento\"). parameter: a material parameter of the element or its type.")]
        [EnumValues("layer", "parameter")]
        [DefaultValue("layer")]
        public string Target { get; set; } = "layer";

        [JsonProperty("layerIndex")]
        [Description("target=layer: exact layer index (see get_compound_structure). Alternative to layerFunction.")]
        public int? LayerIndex { get; set; }

        [JsonProperty("layerFunction")]
        [Description("target=layer: which layer by function (default finish1 = the finish).")]
        [EnumValues("finish1", "finish2", "structure", "substrate", "insulation", "membrane")]
        public string LayerFunction { get; set; }

        [JsonProperty("side")]
        [Description("target=layer with layerFunction: exterior (default; top for floors) takes the first matching layer, interior (bottom) the last.")]
        [EnumValues("exterior", "interior")]
        [DefaultValue("exterior")]
        public string Side { get; set; } = "exterior";

        [JsonProperty("parameter")]
        [Description("target=parameter: the material parameter ({builtIn} | {name} | {guid}), searched on the instance, then the type.")]
        public ParameterRef Parameter { get; set; }

        [JsonProperty("typeMutation")]
        [Description("When the change is on the type: duplicate (default; a new type is created and ONLY these elements switch to it), inPlace (edit the type: every instance changes) or deny (fail).")]
        [EnumValues("duplicate", "inPlace", "deny")]
        [DefaultValue("duplicate")]
        public string TypeMutation { get; set; } = "duplicate";

        [JsonProperty("newTypeName")]
        [Description("Name pattern of duplicated types; {type} = original type name, {material} = material name. Default \"{type} - {material}\". An existing type with that name and the same result is reused.")]
        public string NewTypeName { get; set; }
    }

    /// <summary>MAT-13: apply a material to a layer or parameter, duplicating the type so only these elements change.</summary>
    [McpTool("assign_material", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "materials",
        Description = "Apply a material to elements — \"trocar acabamento\": on a layer of a wall/floor/ceiling/roof type " +
                      "(by index, or by function such as finish1 on the exterior/interior side) or on a material " +
                      "parameter. A type change DUPLICATES the type by default (\"{type} - {material}\", reused when an " +
                      "identical one exists) and switches only the given elements to it; typeMutation inPlace edits the " +
                      "type for all its instances, deny refuses. Results are read back per type. Preview with " +
                      "dryRun:true (no approval), then run (needs approval). Undo: Ctrl+Z.")]
    public sealed class AssignMaterialTool : McpTool<AssignMaterialArgs>
    {
        protected override void Validate(AssignMaterialArgs a)
        {
            a.Target = (a.Target ?? "layer").Trim();
            if (a.Target == "parameter" && (a.Parameter == null || a.Parameter.IsEmpty))
                throw new ToolException("INVALID_ARGUMENTS", "target=parameter needs parameter.");
            if (a.Target == "layer" && a.LayerIndex.HasValue && a.LayerFunction != null)
                throw new ToolException("INVALID_ARGUMENTS", "Give layerIndex or layerFunction, not both.");
        }

        protected override object Run(ToolContext ctx, AssignMaterialArgs a)
        {
            var doc = ctx.Doc;
            var material = ctx.Services.Lookup.Material(a.Material);
            var elements = EditGeometry.Elements(doc, a.ElementIds);
            var results = new List<object>();

            if (a.Target == "parameter")
            {
                var instanceElements = new List<Element>();
                var byType = new Dictionary<long, (ElementType type, List<Element> elements)>();
                foreach (var e in elements)
                {
                    var resolved = ctx.Services.Parameters.Resolve(e, a.Parameter, ParameterTarget.Auto);
                    if (resolved.Parameter.StorageType != StorageType.ElementId)
                        throw new ToolException("NOT_A_MATERIAL_PARAMETER", $"'{resolved.Parameter.Definition?.Name}' does not hold an element (material).");
                    if (resolved.On == "instance") instanceElements.Add(e);
                    else
                    {
                        var t = (ElementType)resolved.Owner;
                        if (!byType.TryGetValue(t.Id.GetValue(), out var g)) byType[t.Id.GetValue()] = g = (t, new List<Element>());
                        g.elements.Add(e);
                    }
                }
                foreach (var e in instanceElements)
                {
                    var p = ctx.Services.Parameters.Find(e, a.Parameter);
                    SetMaterialParam(p, material, e);
                    results.Add(new { elementId = e.Id.GetValue(), action = "instance", parameter = p.Definition?.Name });
                }
                foreach (var g in byType.Values)
                {
                    results.Add(MutateType(ctx, a, g.type, g.elements, material,
                        t => ctx.Services.Parameters.Find(t, a.Parameter)?.AsElementId() == material.Id,
                        t => SetMaterialParam(ctx.Services.Parameters.Find(t, a.Parameter), material, t),
                        null));
                }
            }
            else
            {
                foreach (var g in elements.GroupBy(e => e.GetTypeId().GetValue()))
                {
                    var type = ParameterService.TypeOf(g.First())
                               ?? throw new ToolException("TYPE_NOT_FOUND", $"Element {g.First().Id.GetValue()} has no type.");
                    var cs = TypeTools.Structure(type, out _);
                    int index = LayerIndex(cs, a);
                    results.Add(MutateType(ctx, a, type, g.ToList(), material,
                        t => ((HostObjAttributes)t).GetCompoundStructure()?.GetMaterialId(index) == material.Id,
                        t =>
                        {
                            var s = ((HostObjAttributes)t).GetCompoundStructure();
                            s.SetMaterialId(index, material.Id);
                            ((HostObjAttributes)t).SetCompoundStructure(s);
                            if (((HostObjAttributes)t).GetCompoundStructure().GetMaterialId(index) != material.Id)
                                throw new ToolException("SET_FAILED", $"Revit did not keep '{material.Name}' on layer {index} of '{t.Name}'.");
                        },
                        index));
                }
            }

            return new
            {
                material = new { id = material.Id.GetValue(), name = material.Name },
                target = a.Target,
                results,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"'{material.Name}' applied to {elements.Count} element(s) ({results.Count} group(s))."
            };
        }

        private static object MutateType(ToolContext ctx, AssignMaterialArgs a, ElementType type, List<Element> elements, Material material,
            Func<ElementType, bool> alreadyApplied, Action<ElementType> apply, int? layerIndex)
        {
            var doc = ctx.Doc;
            var ids = elements.Select(e => e.Id.GetValue()).ToList();
            if (alreadyApplied(type))
                return new { sourceType = TypeTools.Describe(type), action = "unchanged", layerIndex, elementIds = ids };

            switch (a.TypeMutation)
            {
                case "deny":
                    throw new ToolException("TYPE_MUTATION_DENIED",
                        $"Applying the material changes the type '{LookupService.FullName(type)}' and typeMutation is deny.",
                        "Use typeMutation duplicate (default) to create a new type for these elements.");
                case "inPlace":
                    apply(type);
                    var n = TypeTools.CountInstances(doc, type);
                    ctx.Warnings.Add(new ToolWarning($"Type '{type.Name}' edited in place: {n} instance(s) changed."));
                    return new { sourceType = TypeTools.Describe(type), action = "inPlace", layerIndex, instancesAffected = n, elementIds = ids };
            }

            var name = (a.NewTypeName ?? "{type} - {material}").Replace("{type}", type.Name).Replace("{material}", material.Name).Trim();
            NameRules.Validate(name, "type name");
            ElementType target;
            string action;
            var sibling = TypeTools.Sibling(doc, type, name);
            if (sibling != null && alreadyApplied(sibling) && SameOtherwise(type, sibling, layerIndex))
            {
                target = sibling;
                action = "reused";
            }
            else
            {
                if (sibling != null)
                {
                    var free = TypeTools.FreeName(doc, type, name);
                    ctx.Warnings.Add(new ToolWarning($"A different type is already named '{name}'; the new type is '{free}'."));
                    name = free;
                }
                target = TypeTools.Duplicate(ctx, type, name);
                apply(target);
                action = "duplicated";
            }

            foreach (var e in elements)
            {
                if (e.GetTypeId() == target.Id) continue;
                if (!e.GetValidTypes().Contains(target.Id))
                    throw new ToolException("TYPE_NOT_VALID", $"Element {e.Id.GetValue()} can't use '{target.Name}'.");
                e.ChangeTypeId(target.Id);
                if (e.GetTypeId() != target.Id)
                    throw new ToolException("TYPE_CHANGE_FAILED", $"Element {e.Id.GetValue()} kept its type.");
            }
            return new { sourceType = TypeTools.Describe(type), targetType = TypeTools.Describe(target), action, layerIndex, elementIds = ids };
        }

        /// <summary>A reusable sibling must have the same layers as the source apart from the changed one.</summary>
        private static bool SameOtherwise(ElementType a, ElementType b, int? layerIndex)
        {
            if (!layerIndex.HasValue) return true;
            var la = (a as HostObjAttributes)?.GetCompoundStructure()?.GetLayers();
            var lb = (b as HostObjAttributes)?.GetCompoundStructure()?.GetLayers();
            if (la == null || lb == null || la.Count != lb.Count) return false;
            for (int i = 0; i < la.Count; i++)
            {
                if (Math.Abs(la[i].Width - lb[i].Width) > 1e-9 || la[i].Function != lb[i].Function) return false;
                if (i != layerIndex.Value && la[i].MaterialId != lb[i].MaterialId) return false;
            }
            return true;
        }

        private static void SetMaterialParam(Parameter p, Material material, Element owner)
        {
            if (p == null) throw new ToolException("PARAMETER_NOT_FOUND", $"Element {owner.Id.GetValue()} has no such parameter.");
            if (p.IsReadOnly) throw new ToolException("READ_ONLY", $"'{p.Definition?.Name}' is read-only on element {owner.Id.GetValue()}.");
            if (!p.Set(material.Id) || p.AsElementId() != material.Id)
                throw new ToolException("SET_FAILED", $"Revit did not keep '{material.Name}' in '{p.Definition?.Name}' of element {owner.Id.GetValue()}.");
        }

        private static int LayerIndex(CompoundStructure cs, AssignMaterialArgs a)
        {
            if (a.LayerIndex.HasValue)
            {
                if (a.LayerIndex.Value < 0 || a.LayerIndex.Value >= cs.LayerCount)
                    throw new ToolException("INVALID_ARGUMENTS", $"Layer index {a.LayerIndex} does not exist (0..{cs.LayerCount - 1}).",
                        "Check the layers with get_compound_structure.", "get_compound_structure");
                return a.LayerIndex.Value;
            }
            var function = TypeTools.ParseFunction(a.LayerFunction ?? "finish1");
            var layers = cs.GetLayers();
            var matches = Enumerable.Range(0, layers.Count).Where(i => layers[i].Function == function).ToList();
            if (matches.Count == 0)
                throw new ToolException("LAYER_NOT_FOUND", $"The type has no {TypeTools.FunctionName(function)} layer.",
                    "Pass layerIndex (see get_compound_structure), or add the layer with set_compound_structure.", "get_compound_structure");
            return a.Side == "interior" ? matches.Last() : matches.First();
        }
    }
}
