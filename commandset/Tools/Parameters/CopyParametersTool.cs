using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Parameters
{
    public sealed class CopyParametersArgs
    {
        [JsonProperty("sourceElementId", Required = Required.Always)]
        [Required]
        [Description("Element to copy from (id or UniqueId).")]
        public string SourceElementId { get; set; }

        [JsonProperty("targetElementIds", Required = Required.Always)]
        [Required]
        [MinLength(1)]
        [MaxLength(500)]
        [Description("Elements to copy to (ids or UniqueIds).")]
        public List<string> TargetElementIds { get; set; }

        [JsonProperty("parameters")]
        [Description("Parameters to copy, each {guid} | {builtIn} | {name}. Omit to copy every editable parameter the elements share (Mark, IFC GUIDs and critical parameters are left out).")]
        public List<ParameterRef> Parameters { get; set; }

        [JsonProperty("scope")]
        [Description("instance (default): instance parameters onto the targets. type: type parameters of the source's type onto each target's type (affects all their instances). both.")]
        [EnumValues("instance", "type", "both")]
        [DefaultValue("instance")]
        public string Scope { get; set; } = "instance";

        [JsonProperty("mapping")]
        [Description("Optional renames for other categories: {\"source parameter name\": \"target parameter name\"}.")]
        public Dictionary<string, string> Mapping { get; set; }

        [JsonProperty("atomic")]
        [Description("true (default): any refused write rolls everything back. false: copy what can be copied.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;

        [JsonProperty("allowCritical")]
        [Description("Allow critical parameters (level, phase, workset, host...) when listed explicitly. Default false.")]
        [DefaultValue(false)]
        public bool AllowCritical { get; set; }
    }

    /// <summary>PAR-05: parameter "match properties" brush, on the ParameterService engine.</summary>
    [McpTool("copy_parameters", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "parameters",
        Description = "Copy parameter values from one source element to 1..500 target elements (match properties). " +
                      "Copies the listed parameters, or every editable parameter they share; only when storage and " +
                      "data type match (any value can go into a text parameter). Measures are copied exactly in " +
                      "internal units. scope=type copies type parameters onto the targets' types. mapping renames " +
                      "parameters between categories. Skipped pairs are reported with a reason. Read-back and revert[] " +
                      "as set_parameters. Undo: Ctrl+Z or send revert[] to set_parameters / set_type_parameters.")]
    public sealed class CopyParametersTool : McpTool<CopyParametersArgs>
    {
        private static readonly HashSet<string> NeverByDefault = new HashSet<string>(StringComparer.Ordinal)
        {
            "ALL_MODEL_MARK", "IFC_GUID", "IFC_TYPE_GUID", "IFC_EXPORT_ELEMENT_AS", "ELEM_TYPE_PARAM",
            "ROOM_NUMBER", "DOOR_NUMBER", "SHEET_NUMBER", "VIEW_NAME", "ALL_MODEL_TYPE_NAME", "SYMBOL_NAME_PARAM"
        };

        protected override void Validate(CopyParametersArgs a)
        {
            a.Scope = (a.Scope ?? "instance").Trim().ToLowerInvariant();
            if (a.Parameters != null && a.Parameters.Any(p => p == null || p.IsEmpty))
                throw new ToolException("INVALID_ARGUMENTS", "parameters[] items need a guid, builtIn or name.");
        }

        protected override object Run(ToolContext ctx, CopyParametersArgs a)
        {
            var ps = ctx.Services.Parameters;
            var source = ctx.Services.Lookup.Element(a.SourceElementId);
            var sourceType = ParameterService.TypeOf(source);
            bool wantInstance = a.Scope != "type", wantType = a.Scope != "instance";
            if (wantType && sourceType == null && !(source is ElementType))
                throw new ToolException("TYPE_NOT_FOUND", $"Element {source.Id.GetValue()} has no type to copy type parameters from.");

            var pairs = new List<(Parameter param, string on)>();
            var skipped = new List<object>();
            if (a.Parameters != null && a.Parameters.Count > 0)
            {
                foreach (var r in a.Parameters)
                {
                    Parameter p = null;
                    string on = null;
                    if (wantInstance && !(source is ElementType)) { p = ps.Find(source, r); on = "instance"; }
                    if (p == null && wantType && sourceType != null) { p = ps.Find(sourceType, r); on = "type"; }
                    if (p == null)
                        throw new ToolException("PARAMETER_NOT_FOUND", $"The source element has no parameter '{r}' in scope '{a.Scope}'.",
                            "Use get_element_parameters on the source element.", "get_element_parameters");
                    pairs.Add((p, on));
                }
            }
            else
            {
                if (wantInstance && !(source is ElementType)) pairs.AddRange(Copyable(source, a.AllowCritical).Select(p => (p, "instance")));
                if (wantType && sourceType != null) pairs.AddRange(Copyable(sourceType, a.AllowCritical).Select(p => (p, "type")));
            }

            var requests = new List<ParameterWriteRequest>();
            var typeOwnersDone = new HashSet<long>();
            foreach (var targetRef in a.TargetElementIds.Distinct())
            {
                var target = ctx.Services.Lookup.Element(targetRef);
                if (target.Id == source.Id) continue;
                foreach (var group in pairs.GroupBy(x => x.on))
                {
                    var owner = group.Key == "type" ? (target is ElementType ? target : ParameterService.TypeOf(target)) : target;
                    if (owner == null || (group.Key == "type" && (owner.Id == sourceType?.Id || !typeOwnersDone.Add(owner.Id.GetValue()))))
                        continue;
                    foreach (var (sp, on) in group)
                    {
                        var identity = ParameterService.Identify(sp);
                        var targetParamRef = MapRef(identity, a.Mapping);
                        Parameter tp;
                        try { tp = ps.Find(owner, targetParamRef); }
                        catch (ToolException ex) { skipped.Add(Skip(owner, identity, ex.Code.ToLowerInvariant())); continue; }
                        if (tp == null) { skipped.Add(Skip(owner, identity, "parameter_missing")); continue; }
                        if (tp.IsReadOnly) { skipped.Add(Skip(owner, identity, "read_only")); continue; }
                        if (!BulkUpdateParametersTool.CanCopy(sp, tp)) { skipped.Add(Skip(owner, identity, "incompatible_type")); continue; }
                        if (SameValue(sp, tp)) { skipped.Add(Skip(owner, identity, "unchanged")); continue; }
                        requests.Add(new ParameterWriteRequest
                        {
                            Element = owner.Id.GetValue().ToString(CultureInfo.InvariantCulture),
                            Parameter = targetParamRef,
                            Value = BulkUpdateParametersTool.CopyValue(ps, sp, tp, out var unit),
                            Unit = unit
                        });
                    }
                }
            }

            if (requests.Count == 0)
                return new { applied = 0, failed = 0, skippedCount = skipped.Count, skipped = skipped.Take(100), changeSummary = "Nothing to copy: every pair was skipped or already equal." };

            var plan = ps.Plan(requests, new ParameterPlanOptions { Target = ParameterTarget.Auto, AllowCritical = a.AllowCritical, MaxItems = 5000 });
            if (a.Atomic && plan.HasErrors)
                throw new ToolException("PARAMETER_WRITE_REFUSED",
                    $"{plan.Failed.Count} of {plan.Items.Count} copy item(s) are invalid; nothing was written. {plan.FailureSummary()}",
                    "List only the parameters you need, or pass atomic:false.");
            var result = ps.Apply(plan);
            if (a.Atomic && result.Failed > 0)
                throw new ToolException("PARAMETER_WRITE_FAILED",
                    $"{result.Failed} write(s) did not stick, so everything was rolled back. " +
                    string.Join(" ", result.Items.Where(i => i.Status != "ok" && i.Status != "duplicate").Take(10)
                        .Select(i => $"[element {i.ElementId} '{i.Parameter?.Name}'] {i.Status}: {i.Message}")),
                    "Pass atomic:false to keep the copies that worked.");

            return new
            {
                sourceElementId = source.Id.GetValue(),
                applied = result.Applied,
                failed = result.Failed,
                items = result.Items,
                skippedCount = skipped.Count,
                skipped = skipped.Take(100),
                revert = result.Revert,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + result.ChangeSummary
            };
        }

        private static IEnumerable<Parameter> Copyable(Element e, bool allowCritical)
        {
            foreach (Parameter p in e.Parameters)
            {
                if (p == null || p.IsReadOnly || p.StorageType == StorageType.None || !p.HasValue) continue;
                var id = ParameterService.Identify(p);
                if (id.BuiltIn != null && NeverByDefault.Contains(id.BuiltIn)) continue;
                if (CriticalParameters.IsCritical(id.BuiltIn)) continue;
                yield return p;
            }
        }

        private static ParameterRef MapRef(ParameterIdentity identity, Dictionary<string, string> mapping)
        {
            if (mapping != null && identity.Name != null)
            {
                var hit = mapping.FirstOrDefault(kv => string.Equals(kv.Key.Trim(), identity.Name, StringComparison.OrdinalIgnoreCase));
                if (hit.Key != null && !string.IsNullOrWhiteSpace(hit.Value)) return ParameterRef.ByName(hit.Value.Trim());
            }
            return identity.ToRef();
        }

        private static bool SameValue(Parameter s, Parameter t)
        {
            if (t.StorageType == StorageType.String)
            {
                var v = s.StorageType == StorageType.String ? s.AsString() : ParameterService.SafeValueString(s);
                return ReadBack.SameText(v, t.AsString());
            }
            switch (s.StorageType)
            {
                case StorageType.Double: return t.HasValue && ReadBack.SameDouble(s.AsDouble(), t.AsDouble());
                case StorageType.Integer: return t.HasValue && s.AsInteger() == t.AsInteger();
                case StorageType.ElementId: return s.AsElementId() == t.AsElementId();
                default: return false;
            }
        }

        private static object Skip(Element owner, ParameterIdentity id, string reason) =>
            new { elementId = owner.Id.GetValue(), parameter = id.Name, reason };
    }
}
