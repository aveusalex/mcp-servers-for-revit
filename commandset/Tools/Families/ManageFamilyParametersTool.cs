using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Families
{
    public sealed class FamilyParameterOperation
    {
        [JsonProperty("op", Required = Required.Always)]
        [EnumValues("add", "set", "formula", "rename", "remove", "add_type")]
        [Description("add (new family parameter, optional initial value), set (value), formula (set or clear with \"\"), rename, remove, add_type (new family type named name, copying the current type).")]
        public string Op { get; set; }

        [JsonProperty("name", Required = Required.Always)]
        [Description("Parameter name (or the new type's name for add_type).")]
        public string Name { get; set; }

        [JsonProperty("newName")]
        [Description("rename: the new parameter name.")]
        public string NewName { get; set; }

        [JsonProperty("dataType")]
        [EnumValues("length", "area", "volume", "angle", "number", "integer", "text", "yesno", "material", "url")]
        [Description("add: data type. Default length.")]
        public string DataType { get; set; }

        [JsonProperty("group")]
        [EnumValues("dimensions", "constraints", "materials", "identity", "data", "text", "graphics", "construction", "general")]
        [Description("add: properties group (Cotas, Restrições, Materiais e acabamentos, Dados de identidade, Dados, Texto, Gráficos, Construção, Geral). Default from the data type.")]
        public string Group { get; set; }

        [JsonProperty("isInstance")]
        [DefaultValue(false)]
        [Description("add: instance parameter (true) or type parameter (false, default).")]
        public bool IsInstance { get; set; }

        [JsonProperty("value")]
        [Description("add/set: the value. Lengths in mm, areas m², volumes m³, angles degrees; yesno true/false; material by name; text as is.")]
        public JToken Value { get; set; }

        [JsonProperty("formula")]
        [Description("formula: the formula text, using parameter names as they appear in the family (e.g. \"Largura / 2\"); \"\" clears it.")]
        public string Formula { get; set; }

        [JsonProperty("types")]
        [MaxLength(200)]
        [Description("add/set of a type parameter value: family type names to set. Default: the current type. [\"*\"] = every type.")]
        public List<string> Types { get; set; }
    }

    public sealed class ManageFamilyParametersArgs
    {
        [JsonProperty("operations")]
        [MaxLength(100)]
        [Description("Operations in order, up to 100. Empty or omitted: only list the parameters (call with dryRun:true to list without approval).")]
        public List<FamilyParameterOperation> Operations { get; set; }

        [JsonProperty("includeValues")]
        [DefaultValue(true)]
        [Description("List each parameter's value in every family type (up to 50 types). Default true.")]
        public bool IncludeValues { get; set; } = true;
    }

    /// <summary>PAR-14: family parameters in the family editor (family documents only).</summary>
    [McpTool("manage_family_parameters", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "families",
        Description = "In a FAMILY document (the target must be an .rfa open in Revit, e.g. after edit_family): list the " +
                      "family parameters (type/instance, data type, group, formula, value per type) and add, set value, " +
                      "set/clear formula, rename or remove them, or add family types. Values use mm, m², m³ and degrees; " +
                      "formulas use the parameter names of the family. Operations run in order and all roll back if one " +
                      "fails. Save or load the family back into the project with edit_family operation load. Undo: Ctrl+Z.")]
    public sealed class ManageFamilyParametersTool : McpTool<ManageFamilyParametersArgs>
    {
        protected override void Validate(ManageFamilyParametersArgs a)
        {
            foreach (var o in a.Operations ?? new List<FamilyParameterOperation>())
            {
                if (o == null) throw new ToolException("INVALID_ARGUMENTS", "operations must not contain null.");
                o.Op = o.Op.Trim().ToLowerInvariant();
                if (o.Op == "rename" && string.IsNullOrWhiteSpace(o.NewName))
                    throw new ToolException("INVALID_ARGUMENTS", $"rename '{o.Name}' needs newName.");
                if (o.Op == "set" && (o.Value == null || o.Value.Type == JTokenType.Null))
                    throw new ToolException("INVALID_ARGUMENTS", $"set '{o.Name}' needs value.");
                if (o.Op == "formula" && o.Formula == null)
                    throw new ToolException("INVALID_ARGUMENTS", $"formula '{o.Name}' needs formula (\"\" clears it).");
            }
        }

        protected override object Run(ToolContext ctx, ManageFamilyParametersArgs a)
        {
            var doc = ctx.Doc;
            if (!doc.IsFamilyDocument)
                throw new ToolException("NOT_A_FAMILY_DOCUMENT", $"'{doc.Title}' is a project; family parameters are edited in the family document.",
                    "Open the family with edit_family (operation open), target that document, then call this tool.", "edit_family");
            var fm = doc.FamilyManager;
            var results = new List<object>();
            int i = 0;
            foreach (var o in a.Operations ?? new List<FamilyParameterOperation>())
            {
                try
                {
                    results.Add(Apply(ctx, fm, o));
                }
                catch (ToolException ex)
                {
                    throw new ToolException(ex.Code, $"Operation [{i}] {o.Op} '{o.Name}': {ex.Message} Nothing was changed.", ex.Hint, ex.RecommendedTool);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    throw new ToolException("REVIT_ERROR", $"Operation [{i}] {o.Op} '{o.Name}': Revit refused: {ex.Message} Nothing was changed.");
                }
                i++;
            }
            if (results.Count > 0) doc.Regenerate();

            return new
            {
                family = doc.Title,
                category = doc.OwnerFamily?.FamilyCategory?.Name,
                currentType = fm.CurrentType?.Name,
                types = fm.Types.Cast<FamilyType>().Select(t => t.Name).OrderBy(n => n).ToList(),
                applied = results,
                parameters = List(fm, a.IncludeValues),
                dryRun = ctx.DryRun
            };
        }

        private static object Apply(ToolContext ctx, FamilyManager fm, FamilyParameterOperation o)
        {
            switch (o.Op)
            {
                case "add":
                {
                    if (fm.get_Parameter(o.Name) != null)
                        throw new ToolException("NAME_IN_USE", $"The family already has a parameter '{o.Name}'.");
                    ctx.Services.Names.Validate(o.Name, "parameter name");
                    var (spec, defGroup) = Spec(o.DataType ?? "length");
                    var group = o.Group == null ? defGroup : GroupOf(o.Group);
                    var fp = fm.AddParameter(o.Name.Trim(), group, spec, o.IsInstance);
                    if (o.Value != null && o.Value.Type != JTokenType.Null) SetValue(ctx, fm, fp, o.Value, o.Types);
                    return new { op = o.Op, name = fp.Definition.Name, isInstance = fp.IsInstance };
                }
                case "set":
                {
                    var fp = Param(fm, o.Name);
                    var set = SetValue(ctx, fm, fp, o.Value, o.Types);
                    return new { op = o.Op, name = fp.Definition.Name, types = set };
                }
                case "formula":
                {
                    var fp = Param(fm, o.Name);
                    if (!string.IsNullOrEmpty(o.Formula) && !fp.CanAssignFormula)
                        throw new ToolException("FORMULA_NOT_ALLOWED", $"'{fp.Definition.Name}' can't take a formula (reporting or label-bound parameter?).");
                    try { fm.SetFormula(fp, string.IsNullOrEmpty(o.Formula) ? null : o.Formula); }
                    catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                    {
                        throw new ToolException("INVALID_FORMULA", ex.Message, "Use the parameter names exactly as in the family; units must match (e.g. lengths with lengths).");
                    }
                    return new { op = o.Op, name = fp.Definition.Name, formula = fp.Formula };
                }
                case "rename":
                {
                    var fp = Param(fm, o.Name);
                    if (fm.get_Parameter(o.NewName) != null)
                        throw new ToolException("NAME_IN_USE", $"The family already has a parameter '{o.NewName}'.");
                    if (fp.IsShared)
                        throw new ToolException("NOT_SUPPORTED", $"'{fp.Definition.Name}' is a shared parameter; its name comes from the shared parameter file.");
                    ctx.Services.Names.Validate(o.NewName, "parameter name");
                    fm.RenameParameter(fp, o.NewName.Trim());
                    return new { op = o.Op, from = o.Name, to = fp.Definition.Name };
                }
                case "remove":
                {
                    var fp = Param(fm, o.Name);
                    var name = fp.Definition.Name;
                    if (fp.Id.GetValue() < 0)
                        throw new ToolException("NOT_SUPPORTED", $"'{name}' is a built-in parameter and can't be removed.");
                    fm.RemoveParameter(fp);
                    return new { op = o.Op, name };
                }
                case "add_type":
                {
                    if (fm.CurrentType == null && fm.Types.Size > 0)
                        throw new ToolException("NO_CURRENT_TYPE", "The family has no current type.");
                    if (fm.Types.Cast<FamilyType>().Any(t => string.Equals(t.Name, o.Name, StringComparison.OrdinalIgnoreCase)))
                        throw new ToolException("NAME_IN_USE", $"The family already has a type '{o.Name}'.");
                    var t2 = fm.NewType(o.Name.Trim());
                    return new { op = o.Op, type = t2.Name };
                }
                default:
                    throw new ToolException("INVALID_ARGUMENTS", $"Unknown op '{o.Op}'.");
            }
        }

        private static FamilyParameter Param(FamilyManager fm, string name)
        {
            var fp = fm.get_Parameter(name);
            if (fp != null) return fp;
            var all = fm.GetParameters().ToList();
            fp = all.FirstOrDefault(p => string.Equals(p.Definition.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (fp != null) return fp;
            var names = all.Select(p => p.Definition.Name).OrderBy(n => n).Take(60);
            throw new ToolException("PARAMETER_NOT_FOUND", $"The family has no parameter '{name}'. Parameters: {string.Join(", ", names)}.");
        }

        private static List<string> SetValue(ToolContext ctx, FamilyManager fm, FamilyParameter fp, JToken value, List<string> typeNames)
        {
            if (!string.IsNullOrEmpty(fp.Formula))
                throw new ToolException("PARAMETER_HAS_FORMULA", $"'{fp.Definition.Name}' is driven by the formula '{fp.Formula}'.", "Clear the formula first (op formula with \"\").");
            if (fp.IsReporting)
                throw new ToolException("PARAMETER_READ_ONLY", $"'{fp.Definition.Name}' is a reporting parameter.");
            var all = fm.Types.Cast<FamilyType>().ToList();
            List<FamilyType> targets;
            if (typeNames == null || typeNames.Count == 0)
            {
                if (fm.CurrentType == null)
                    throw new ToolException("NO_CURRENT_TYPE", "The family has no type yet.", "Add one first (op add_type).");
                targets = new List<FamilyType> { fm.CurrentType };
            }
            else if (typeNames.Count == 1 && typeNames[0] == "*") targets = all;
            else
            {
                targets = new List<FamilyType>();
                foreach (var n in typeNames)
                {
                    var t = all.FirstOrDefault(x => string.Equals(x.Name, n?.Trim(), StringComparison.OrdinalIgnoreCase))
                            ?? throw new ToolException("TYPE_NOT_FOUND", $"No family type '{n}'. Types: {string.Join(", ", all.Select(x => x.Name))}.");
                    targets.Add(t);
                }
            }
            var original = fm.CurrentType;
            var done = new List<string>();
            foreach (var t in targets)
            {
                if (fm.CurrentType == null || fm.CurrentType.Name != t.Name) fm.CurrentType = t;
                SetOne(ctx, fm, fp, value);
                done.Add(t.Name);
            }
            if (original != null && fm.CurrentType?.Name != original.Name) fm.CurrentType = original;
            return done;
        }

        private static void SetOne(ToolContext ctx, FamilyManager fm, FamilyParameter fp, JToken value)
        {
            var spec = fp.Definition.GetDataType();
            switch (fp.StorageType)
            {
                case StorageType.Double:
                {
                    double v = Number(value, fp);
                    fm.Set(fp, ctx.Services.Units.ToInternal(v, spec, null, fp.Definition.Name));
                    break;
                }
                case StorageType.Integer:
                {
                    int v;
                    if (value.Type == JTokenType.Boolean) v = value.Value<bool>() ? 1 : 0;
                    else if (value.Type == JTokenType.String && bool.TryParse((string)value, out var b)) v = b ? 1 : 0;
                    else if (value.Type == JTokenType.String && ((string)value).Trim().ToLowerInvariant() is string s && (s == "sim" || s == "não" || s == "nao"))
                        v = s == "sim" ? 1 : 0;
                    else v = (int)Math.Round(Number(value, fp));
                    fm.Set(fp, v);
                    break;
                }
                case StorageType.String:
                    fm.Set(fp, value.Type == JTokenType.String ? (string)value : value.ToString(Formatting.None));
                    break;
                case StorageType.ElementId:
                {
                    var text = value.ToString().Trim();
                    ElementId id;
                    if (SpecTypeId.Reference.Material.Equals(spec)) id = ctx.Services.Lookup.Material(text).Id;
                    else if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) id = n.ToRevitElementId();
                    else throw new ToolException("INVALID_VALUE", $"'{fp.Definition.Name}' needs an element id.");
                    fm.Set(fp, id);
                    break;
                }
                default:
                    throw new ToolException("NOT_SUPPORTED", $"'{fp.Definition.Name}' can't be set.");
            }
        }

        private static double Number(JToken value, FamilyParameter fp)
        {
            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float) return value.Value<double>();
            if (value.Type == JTokenType.String &&
                double.TryParse(((string)value).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            throw new ToolException("INVALID_VALUE", $"'{fp.Definition.Name}' needs a number (got {value.ToString(Formatting.None)}).");
        }

        private static (ForgeTypeId spec, ForgeTypeId group) Spec(string dataType)
        {
            switch (dataType.Trim().ToLowerInvariant())
            {
                case "length": return (SpecTypeId.Length, GroupTypeId.Geometry);
                case "area": return (SpecTypeId.Area, GroupTypeId.Geometry);
                case "volume": return (SpecTypeId.Volume, GroupTypeId.Geometry);
                case "angle": return (SpecTypeId.Angle, GroupTypeId.Geometry);
                case "number": return (SpecTypeId.Number, GroupTypeId.Data);
                case "integer": return (SpecTypeId.Int.Integer, GroupTypeId.Data);
                case "text": return (SpecTypeId.String.Text, GroupTypeId.Text);
                case "yesno": return (SpecTypeId.Boolean.YesNo, GroupTypeId.Graphics);
                case "material": return (SpecTypeId.Reference.Material, GroupTypeId.Materials);
                case "url": return (SpecTypeId.String.Url, GroupTypeId.IdentityData);
                default: throw new ToolException("INVALID_ARGUMENTS", $"Unknown dataType '{dataType}'.");
            }
        }

        private static ForgeTypeId GroupOf(string group)
        {
            switch (group.Trim().ToLowerInvariant())
            {
                case "dimensions": return GroupTypeId.Geometry;
                case "constraints": return GroupTypeId.Constraints;
                case "materials": return GroupTypeId.Materials;
                case "identity": return GroupTypeId.IdentityData;
                case "data": return GroupTypeId.Data;
                case "text": return GroupTypeId.Text;
                case "graphics": return GroupTypeId.Graphics;
                case "construction": return GroupTypeId.Construction;
                case "general": return GroupTypeId.General;
                default: throw new ToolException("INVALID_ARGUMENTS", $"Unknown group '{group}'.");
            }
        }

        private static List<object> List(FamilyManager fm, bool includeValues)
        {
            var types = fm.Types.Cast<FamilyType>().OrderBy(t => t.Name).Take(50).ToList();
            return fm.GetParameters().OrderBy(p => p.Definition.Name).Select(p =>
            {
                Dictionary<string, string> values = null;
                if (includeValues)
                {
                    values = new Dictionary<string, string>();
                    foreach (var t in types)
                    {
                        string v;
                        try { v = t.HasValue(p) ? (t.AsValueString(p) ?? t.AsString(p)) : null; }
                        catch { v = null; }
                        values[t.Name] = v;
                    }
                }
                string dataType = null, group = null;
                try { dataType = LabelUtils.GetLabelForSpec(p.Definition.GetDataType()); } catch { /* category / unusual spec */ }
                try { group = LabelUtils.GetLabelForGroup(p.Definition.GetGroupTypeId()); } catch { /* none */ }
                return (object)new
                {
                    name = p.Definition.Name,
                    id = p.Id.GetValue(),
                    isInstance = p.IsInstance,
                    isShared = p.IsShared,
                    guid = p.IsShared ? p.GUID.ToString() : null,
                    builtIn = p.Id.GetValue() < 0,
                    dataType,
                    group,
                    formula = string.IsNullOrEmpty(p.Formula) ? null : p.Formula,
                    isReporting = p.IsReporting,
                    values
                };
            }).ToList();
        }
    }
}
