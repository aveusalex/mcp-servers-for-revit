using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;

namespace RevitMCPCommandSet.Tools.Parameters
{
    public sealed class ParameterWriteItem
    {
        [JsonProperty("elementId", Required = Required.Always)]
        [Description("Element id or UniqueId, as a string.")]
        public string ElementId { get; set; }

        [JsonProperty("parameter", Required = Required.Always)]
        [Description("Parameter reference: {guid} | {builtIn} | {name} (resolved in that order).")]
        public ParameterRef Parameter { get; set; }

        [JsonProperty("value", Required = Required.AllowNull)]
        [Description("New value. Numbers in wire units (length mm, area m2, volume m3, angle deg, slope %); yes/no as true/false; ElementId parameters take an id or a name (level, phase, material, type); null clears text and ElementId values.")]
        public JToken Value { get; set; }

        [JsonProperty("unit")]
        [Description("Optional unit for this number instead of the wire default: m, cm, mm, ft, in, m2, ft2, m3, l, deg, rad, %, ratio, or internal (raw Revit value).")]
        public string Unit { get; set; }
    }

    public sealed class SetParametersArgs
    {
        [JsonProperty("items")]
        [Description("Writes to apply: one {elementId, parameter, value, unit?} per value. Up to 500 per call.")]
        public List<ParameterWriteItem> Items { get; set; }

        [JsonProperty("elementIds")]
        [Description("Shortcut for the same value on several elements: element ids (strings), used with parameter + value instead of items.")]
        public List<string> ElementIds { get; set; }

        [JsonProperty("parameter")]
        [Description("Shortcut: the parameter to write on every element of elementIds.")]
        public ParameterRef Parameter { get; set; }

        [JsonProperty("value")]
        [Description("Shortcut: the value to write on every element of elementIds (same rules as items[].value).")]
        public JToken Value { get; set; }

        [JsonProperty("unit")]
        [Description("Shortcut: optional unit override for value.")]
        public string Unit { get; set; }

        [JsonProperty("atomic")]
        [Description("true (default): all or nothing — any invalid item, refused value or read-back mismatch rolls the whole call back. false: valid items are written and the rest reported in items[] with their status.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;

        [JsonProperty("allowCritical")]
        [Description("Allow parameters that change what an element is or where it lives (type, levels and constraints, phases, workset, host) and one Mark on many elements. Default false.")]
        [DefaultValue(false)]
        public bool AllowCritical { get; set; }
    }

    /// <summary>Shared engine of set_parameters / set_type_parameters (PAR-03), on ParameterService.</summary>
    public abstract class SetParametersToolBase : McpTool<SetParametersArgs>
    {
        public const int MaxItems = 500;

        protected abstract ParameterTarget Target { get; }

        protected override void Validate(SetParametersArgs args)
        {
            var hasItems = args.Items != null && args.Items.Count > 0;
            var hasShortcut = args.ElementIds != null && args.ElementIds.Count > 0;
            if (hasItems == hasShortcut)
                throw new ToolException("INVALID_ARGUMENTS",
                    "Pass either items[] or elementIds[] + parameter + value (not both, not neither).");
            if (hasShortcut && (args.Parameter == null || args.Parameter.IsEmpty))
                throw new ToolException("INVALID_ARGUMENTS", "parameter is required with elementIds.");
            var count = hasItems ? args.Items.Count : args.ElementIds.Count;
            if (count > MaxItems)
                throw new ToolException("TOO_MANY_ITEMS", $"{count} writes requested; the limit is {MaxItems} per call.",
                    "Split the request into several calls.");
            if (hasItems && args.Items.Any(i => i == null || i.Parameter == null || i.Parameter.IsEmpty || string.IsNullOrWhiteSpace(i.ElementId)))
                throw new ToolException("INVALID_ARGUMENTS", "Every item needs elementId and parameter.");
        }

        protected override object Run(ToolContext ctx, SetParametersArgs args)
        {
            var requests = args.Items != null && args.Items.Count > 0
                ? args.Items.Select(i => new ParameterWriteRequest
                    { Element = i.ElementId, Parameter = i.Parameter, Value = i.Value, Unit = i.Unit }).ToList()
                : args.ElementIds.Select(id => new ParameterWriteRequest
                    { Element = id, Parameter = args.Parameter, Value = args.Value, Unit = args.Unit }).ToList();

            var ps = ctx.Services.Parameters;
            var plan = ps.Plan(requests, new ParameterPlanOptions
            {
                Target = Target,
                AllowCritical = args.AllowCritical,
                MaxItems = MaxItems
            });

            if (args.Atomic && plan.HasErrors)
            {
                var first = plan.Failed.First();
                throw new ToolException("PARAMETER_WRITE_REFUSED",
                    $"{plan.Failed.Count} of {plan.Items.Count} item(s) are invalid; nothing was written. {plan.FailureSummary()}",
                    "Fix the listed items, or pass atomic:false to write only the valid ones.",
                    first.RecommendedTool);
            }

            var result = ps.Apply(plan);

            if (args.Atomic && result.Failed > 0)
            {
                var failures = string.Join(" ", result.Items.Where(i => i.Status != "ok" && i.Status != "duplicate").Take(10)
                    .Select(i => $"[{i.Index}] element {i.ElementId} '{i.Parameter?.Name}': {i.Status} - {i.Message}"));
                throw new ToolException("PARAMETER_WRITE_FAILED",
                    $"{result.Failed} write(s) did not stick, so the whole call was rolled back. {failures}",
                    "Check formulas, constraints and value ranges, or pass atomic:false to keep the writes that succeeded.");
            }

            return new
            {
                applied = result.Applied,
                failed = result.Failed,
                dryRun = ctx.DryRun,
                items = result.Items,
                revert = result.Revert,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + result.ChangeSummary,
                instancesAffected = Target == ParameterTarget.Type
                    ? result.Items.Where(i => i.Status == "ok").GroupBy(i => i.ElementId).Sum(g => g.First().InstancesAffected ?? 0)
                    : (int?)null
            };
        }
    }

    [McpTool("set_parameters", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "parameters",
        Description = "Write instance parameter values on 1..500 elements in one transaction (one Ctrl+Z). Reference " +
                      "parameters by guid, builtIn or name; numbers in wire units (mm, m2, m3, deg, %), ElementId " +
                      "parameters by id or name. Every write is read back: values Revit changed (formulas, rounding) " +
                      "come back as 'mismatch'. Atomic by default. Critical parameters (type, level, phase, workset, " +
                      "host) are blocked unless allowCritical. Type parameters are refused here: use " +
                      "set_type_parameters. Undo: Ctrl+Z, or send the returned revert[] back to this tool.")]
    public sealed class SetParametersTool : SetParametersToolBase
    {
        protected override ParameterTarget Target => ParameterTarget.Instance;
    }

    [McpTool("set_type_parameters", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "parameters",
        Description = "Write TYPE parameter values (affects every instance of the type; result reports " +
                      "instancesAffected). elementId may be a type id or an instance id (its type is used). Same " +
                      "rules as set_parameters: guid/builtIn/name references, wire units (mm, m2, m3, deg, %), " +
                      "read-back with 'mismatch', atomic by default, critical parameters blocked unless allowCritical. " +
                      "Needs human approval. Undo: Ctrl+Z, or send revert[] back to this tool.")]
    public sealed class SetTypeParametersTool : SetParametersToolBase
    {
        protected override ParameterTarget Target => ParameterTarget.Type;
    }
}
