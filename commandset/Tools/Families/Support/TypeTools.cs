using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Families.Support
{
    /// <summary>Type lookup, duplication with a free name, instance counts and compound-structure description.</summary>
    public static class TypeTools
    {
        /// <summary>A type by id, "Family: Type" or type name, optionally limited to a category (any language).</summary>
        public static ElementType ResolveType(ToolContext ctx, string text, string category = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ToolException("INVALID_ARGUMENTS", "A type (id or name) is required.");
            if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                var e = ctx.Doc.GetElement(id.ToRevitElementId());
                if (e is ElementType byId) return byId;
                if (e != null && ParameterService.TypeOf(e) is ElementType ofInstance)
                    throw new ToolException("NOT_A_TYPE", $"{id} is an instance, not a type (its type is {ofInstance.Id.GetValue()} '{LookupService.FullName(ofInstance)}').");
            }
            BuiltInCategory? bic = null;
            if (!string.IsNullOrWhiteSpace(category)) bic = ctx.Services.Categories.Resolve(category).BuiltIn;
            return ctx.Services.Lookup.ElementType(text, bic);
        }

        /// <summary>The type of an element, or the element when it is a type.</summary>
        public static ElementType TypeOfElement(ToolContext ctx, long elementId)
        {
            var e = ctx.Doc.GetElement(elementId.ToRevitElementId())
                    ?? throw new ToolException("ELEMENT_NOT_FOUND", $"Element {elementId} not found.", null, "find_elements");
            return ParameterService.TypeOf(e) ?? throw new ToolException("TYPE_NOT_FOUND", $"Element {elementId} has no type.");
        }

        /// <summary>"Name", "Name (2)", "Name (3)"... the first that is free in the type's family.</summary>
        public static string FreeName(Document doc, ElementType type, string desired)
        {
            var family = LookupService.SafeFamilyName(type);
            var taken = new HashSet<string>(new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
                .Where(t => t.GetType() == type.GetType() && string.Equals(LookupService.SafeFamilyName(t), family, StringComparison.Ordinal))
                .Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(desired)) return desired;
            for (int i = 2; i < 1000; i++)
            {
                var candidate = $"{desired} ({i})";
                if (!taken.Contains(candidate)) return candidate;
            }
            throw new ToolException("NAME_COLLISION", $"No free name found for '{desired}'.");
        }

        /// <summary>An existing type of the same family with that name, or null.</summary>
        public static ElementType Sibling(Document doc, ElementType type, string name)
        {
            var family = LookupService.SafeFamilyName(type);
            return new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
                .FirstOrDefault(t => t.GetType() == type.GetType() &&
                                     string.Equals(LookupService.SafeFamilyName(t), family, StringComparison.Ordinal) &&
                                     string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Duplicates the type under a free name (checked first) and reads the name back. Inside the transaction.</summary>
        public static ElementType Duplicate(ToolContext ctx, ElementType source, string name)
        {
            ctx.Services.Names.EnsureTypeNameFree(source, name);
            ElementType copy;
            try
            {
                copy = source.Duplicate(name) as ElementType;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("DUPLICATE_FAILED", $"Revit could not duplicate '{source.Name}': {ex.Message}");
            }
            if (copy == null) throw new ToolException("DUPLICATE_FAILED", $"Revit did not duplicate '{source.Name}'.");
            if (!string.Equals(copy.Name, name, StringComparison.Ordinal))
                throw new ToolException("NAME_NOT_APPLIED", $"The new type is named '{copy.Name}' instead of '{name}'.");
            return copy;
        }

        public static int CountInstances(Document doc, ElementType type)
        {
            if (type is FamilySymbol)
                return new FilteredElementCollector(doc).WherePasses(new FamilyInstanceFilter(doc, type.Id)).GetElementCount();
            var c = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (type.Category != null) c = c.OfCategoryId(type.Category.Id);
            return c.ToElements().Count(x => x.GetTypeId() == type.Id);
        }

        public static List<Element> Instances(Document doc, ElementType type)
        {
            if (type is FamilySymbol)
                return new FilteredElementCollector(doc).WherePasses(new FamilyInstanceFilter(doc, type.Id)).ToElements().ToList();
            var c = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (type.Category != null) c = c.OfCategoryId(type.Category.Id);
            return c.ToElements().Where(x => x.GetTypeId() == type.Id).ToList();
        }

        public static object Describe(ElementType t) => t == null ? null : new
        {
            id = t.Id.GetValue(),
            name = t.Name,
            familyName = LookupService.SafeFamilyName(t),
            category = t.Category?.Name
        };

        // ---- compound structures ---------------------------------------------------------------------------------

        public static readonly string[] FunctionNames = { "structure", "substrate", "insulation", "finish1", "finish2", "membrane", "structuralDeck" };

        public static MaterialFunctionAssignment ParseFunction(string text)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant().Replace("_", "").Replace(" ", ""))
            {
                case "structure": case "estrutura": return MaterialFunctionAssignment.Structure;
                case "substrate": case "substrato": return MaterialFunctionAssignment.Substrate;
                case "insulation": case "thermalairlayer": case "isolamento": return MaterialFunctionAssignment.Insulation;
                case "finish1": case "acabamento1": return MaterialFunctionAssignment.Finish1;
                case "finish2": case "acabamento2": return MaterialFunctionAssignment.Finish2;
                case "membrane": case "membranelayer": case "membrana": return MaterialFunctionAssignment.Membrane;
                case "structuraldeck": return MaterialFunctionAssignment.StructuralDeck;
                default:
                    throw new ToolException("INVALID_ARGUMENTS", $"Unknown layer function '{text}'.", "Use " + string.Join(", ", FunctionNames) + ".");
            }
        }

        public static string FunctionName(MaterialFunctionAssignment f)
        {
            switch (f)
            {
                case MaterialFunctionAssignment.Structure: return "structure";
                case MaterialFunctionAssignment.Substrate: return "substrate";
                case MaterialFunctionAssignment.Insulation: return "insulation";
                case MaterialFunctionAssignment.Finish1: return "finish1";
                case MaterialFunctionAssignment.Finish2: return "finish2";
                case MaterialFunctionAssignment.Membrane: return "membrane";
                case MaterialFunctionAssignment.StructuralDeck: return "structuralDeck";
                default: return f.ToString();
            }
        }

        /// <summary>The compound structure of a wall/floor/roof/ceiling type, or a clear error.</summary>
        public static CompoundStructure Structure(ElementType type, out HostObjAttributes host)
        {
            host = type as HostObjAttributes;
            var cs = host?.GetCompoundStructure();
            if (cs == null)
                throw new ToolException("NO_COMPOUND_STRUCTURE",
                    $"'{LookupService.FullName(type)}' has no layer structure (only basic walls, floors, roofs and ceilings do; stacked and curtain walls don't).");
            return cs;
        }

        public static object Layers(Document doc, CompoundStructure cs)
        {
            var layers = cs.GetLayers();
            int first = cs.GetFirstCoreLayerIndex(), last = cs.GetLastCoreLayerIndex();
            return layers.Select((l, i) => new
            {
                index = i,
                function = FunctionName(l.Function),
                materialId = l.MaterialId == ElementId.InvalidElementId ? (long?)null : l.MaterialId.GetValue(),
                materialName = l.MaterialId == ElementId.InvalidElementId ? "<By Category>" : doc.GetElement(l.MaterialId)?.Name,
                thicknessMm = EditGeometry.Mm(l.Width),
                isCore = i >= first && i <= last,
                isVariable = cs.VariableLayerIndex == i,
                isStructuralMaterial = cs.StructuralMaterialIndex == i,
                wraps = SafeWraps(cs, i)
            }).ToList();
        }

        private static bool? SafeWraps(CompoundStructure cs, int i)
        {
            try { return cs.ParticipatesInWrapping(i); }
            catch { return null; }
        }

        public static object Summary(Document doc, ElementType type, CompoundStructure cs) => new
        {
            typeId = type.Id.GetValue(),
            typeName = type.Name,
            familyName = LookupService.SafeFamilyName(type),
            kind = type.GetType().Name,
            totalThicknessMm = EditGeometry.Mm(cs.GetWidth()),
            exteriorShellLayers = cs.GetNumberOfShellLayers(ShellLayerType.Exterior),
            interiorShellLayers = cs.GetNumberOfShellLayers(ShellLayerType.Interior),
            variableLayerIndex = cs.VariableLayerIndex < 0 ? (int?)null : cs.VariableLayerIndex,
            instances = CountInstances(doc, type),
            layers = Layers(doc, cs),
            note = type is WallType ? "Index 0 is the exterior side." : "Index 0 is the top."
        };

        /// <summary>Validates a structure for a type and turns Revit's error map into a message.</summary>
        public static void EnsureValid(Document doc, CompoundStructure cs, ElementType type)
        {
            IDictionary<int, CompoundStructureError> errors;
            IDictionary<int, int> twoLayerErrors;
            if (cs.IsValid(doc, out errors, out twoLayerErrors)) return;
            var parts = errors.Select(kv => $"layer {kv.Key}: {kv.Value}")
                .Concat(twoLayerErrors.Select(kv => $"layers {kv.Key}/{kv.Value}")).Take(10);
            throw new ToolException("INVALID_STRUCTURE", $"The new layer structure of '{type.Name}' is not valid: {string.Join("; ", parts)}.",
                "Check thicknesses (only membranes may be 0), core boundaries and the variable layer.");
        }
    }
}
