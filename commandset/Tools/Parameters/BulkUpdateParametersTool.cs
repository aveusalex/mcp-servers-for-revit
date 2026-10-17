using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Parameters.Support;
using RevitMCPCommandSet.Tools.Query;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Parameters
{
    public sealed class BulkUpdateParametersArgs
    {
        [JsonProperty("elementIds")]
        [Description("Explicit elements (ids or UniqueIds as strings). Alternative to categories / useSelection.")]
        [MaxLength(2000)]
        public List<string> ElementIds { get; set; }

        [JsonProperty("categories")]
        [Description("Categories in any language (OST_Doors, Doors, Portas): every element of these categories, narrowed by view and where[].")]
        public List<string> Categories { get; set; }

        [JsonProperty("view")]
        [Description("Only elements visible in this view (name or id). Used with categories.")]
        public string View { get; set; }

        [JsonProperty("useSelection")]
        [Description("Act on Revit's current selection (target must be the active document). Default false.")]
        [DefaultValue(false)]
        public bool UseSelection { get; set; }

        [JsonProperty("where")]
        [Description("Extra conditions on parameters, all must match (same operators as find_elements).")]
        public List<WhereCondition> Where { get; set; }

        [JsonProperty("parameter", Required = Required.Always)]
        [Required]
        [Description("The parameter to change: {guid} | {builtIn} | {name}.")]
        public ParameterRef Parameter { get; set; }

        [JsonProperty("target")]
        [Description("Where the parameter lives: instance (default), type (writes each distinct type once; affects all its instances) or auto (instance, then type).")]
        [EnumValues("instance", "type", "auto")]
        [DefaultValue("instance")]
        public string Target { get; set; } = "instance";

        [JsonProperty("operation", Required = Required.Always)]
        [Required]
        [Description("set (value), prefix / suffix (value added to the current text, with separator), findReplace (find → replace, plain text), regexReplace (find is a .NET regex, replace may use $1), clear, copyFrom (value of sourceParameter on the same element).")]
        [EnumValues("set", "prefix", "suffix", "findReplace", "regexReplace", "clear", "copyFrom")]
        public string Operation { get; set; }

        [JsonProperty("value")]
        [Description("For set: the value (numbers in wire units mm, m2, m3, deg, %; ElementId parameters by id or name). For prefix/suffix: the text to add.")]
        public JToken Value { get; set; }

        [JsonProperty("unit")]
        [Description("Optional unit override for a numeric set value: m, cm, mm, ft, in, m2, ft2, m3, l, deg, rad, %, ratio or internal.")]
        public string Unit { get; set; }

        [JsonProperty("find")]
        [Description("Text (findReplace) or regular expression (regexReplace) to look for.")]
        public string Find { get; set; }

        [JsonProperty("replace")]
        [Description("Replacement text for findReplace / regexReplace (default empty).")]
        public string Replace { get; set; }

        [JsonProperty("separator")]
        [Description("Text between the current value and the prefix/suffix (default none).")]
        public string Separator { get; set; }

        [JsonProperty("ignoreCase")]
        [Description("findReplace / regexReplace ignore case. Default false.")]
        [DefaultValue(false)]
        public bool IgnoreCase { get; set; }

        [JsonProperty("sourceParameter")]
        [Description("For copyFrom: the parameter to read on each element (instance first, then type).")]
        public ParameterRef SourceParameter { get; set; }

        [JsonProperty("onlyEmpty")]
        [Description("Only write elements whose parameter is currently empty. Default false.")]
        [DefaultValue(false)]
        public bool OnlyEmpty { get; set; }

        [JsonProperty("maxElements")]
        [Description("Safety limit: refuse when more elements than this would be written (default 500, max 2000).")]
        [Range(1, 2000)]
        [DefaultValue(500)]
        public int MaxElements { get; set; } = 500;

        [JsonProperty("sampleSize")]
        [Description("How many before/after rows to return (default 20).")]
        [Range(0, 500)]
        [DefaultValue(20)]
        public int SampleSize { get; set; } = 20;

        [JsonProperty("atomic")]
        [Description("true (default): any invalid item or read-back mismatch rolls everything back. false: write what can be written and report the rest.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;

        [JsonProperty("allowCritical")]
        [Description("Allow critical parameters (type, level, phase, workset, host; one Mark on many elements). Default false.")]
        [DefaultValue(false)]
        public bool AllowCritical { get; set; }
    }

    /// <summary>PAR-04: "update where" on the ParameterService engine (plan, apply, read-back, revert).</summary>
    [McpTool("bulk_update_parameters", Kind = ToolKind.Write, Risk = Risk.Medium, DryRunDefault = true, Toolset = "parameters",
        Description = "Apply one change to a parameter on every element that matches a scope: explicit elementIds, " +
                      "categories (any language) narrowed by view and where[] conditions, or the current selection. " +
                      "Operations: set, prefix, suffix, findReplace, regexReplace, clear, copyFrom another parameter; " +
                      "onlyEmpty skips filled values. ALWAYS call it first with dryRun:true (no approval needed) to get " +
                      "matched/skipped counts and a before/after sample, then run it for real (needs approval). Numbers " +
                      "in wire units (mm, m2, m3, deg, %). Every write is read back; atomic by default. Undo: Ctrl+Z or " +
                      "send revert[] to set_parameters / set_type_parameters.")]
    public sealed class BulkUpdateParametersTool : McpTool<BulkUpdateParametersArgs>
    {
        protected override void Validate(BulkUpdateParametersArgs a)
        {
            if ((a.ElementIds?.Count ?? 0) == 0 && (a.Categories?.Count ?? 0) == 0 && !a.UseSelection)
                throw new ToolException("INVALID_ARGUMENTS", "Say which elements: elementIds, categories or useSelection.");
            if (a.Parameter == null || a.Parameter.IsEmpty)
                throw new ToolException("INVALID_ARGUMENTS", "parameter is required.");
            ElementScope.ValidateWhere(a.Where);
            a.Target = (a.Target ?? "instance").Trim().ToLowerInvariant();
            if (a.Operation == "copyFrom")
            {
                if (a.SourceParameter == null || a.SourceParameter.IsEmpty)
                    throw new ToolException("INVALID_ARGUMENTS", "operation 'copyFrom' needs sourceParameter.");
            }
            else if (a.Operation == "set")
            {
                if (a.Value == null)
                    throw new ToolException("INVALID_ARGUMENTS", "operation 'set' needs value (use operation 'clear' to empty it).");
            }
            else
            {
                TextOperation.Create(a.Operation, TextOf(a.Value), a.Find, a.Replace, a.Separator, a.IgnoreCase);
            }
        }

        protected override object Run(ToolContext ctx, BulkUpdateParametersArgs a)
        {
            var ps = ctx.Services.Parameters;
            var target = a.Target == "type" ? ParameterTarget.Type : a.Target == "auto" ? ParameterTarget.Auto : ParameterTarget.Instance;
            var elements = ElementScope.Collect(ctx, new ElementScopeRequest
            {
                ElementIds = a.ElementIds, Categories = a.Categories, View = a.View, UseSelection = a.UseSelection, Where = a.Where
            });

            TextOperation text = a.Operation == "set" || a.Operation == "copyFrom"
                ? null
                : TextOperation.Create(a.Operation, TextOf(a.Value), a.Find, a.Replace, a.Separator, a.IgnoreCase);

            var requests = new List<ParameterWriteRequest>();
            var skipped = new List<(long id, string reason)>();
            var seenOwners = new HashSet<long>();

            foreach (var e in elements)
            {
                ResolvedParameter resolved;
                try
                {
                    resolved = ps.Resolve(e, a.Parameter, target);
                }
                catch (ToolException ex)
                {
                    skipped.Add((e.Id.GetValue(), ex.Code == "PARAMETER_ON_TYPE" ? "parameter_on_type" : "parameter_missing"));
                    continue;
                }

                var p = resolved.Parameter;
                if (resolved.On == "type" && !seenOwners.Add(resolved.Owner.Id.GetValue()))
                {
                    skipped.Add((e.Id.GetValue(), "type_already_planned"));
                    continue;
                }
                if (a.OnlyEmpty && HasContent(p))
                {
                    skipped.Add((e.Id.GetValue(), "not_empty"));
                    continue;
                }

                object value;
                string unit = a.Unit;
                if (a.Operation == "set")
                {
                    value = a.Value;
                }
                else if (a.Operation == "copyFrom")
                {
                    var source = ElementScope.FindScoped(ps, e, a.SourceParameter, "auto");
                    if (source == null)
                    {
                        skipped.Add((e.Id.GetValue(), "source_missing"));
                        continue;
                    }
                    if (!CanCopy(source, p))
                    {
                        skipped.Add((e.Id.GetValue(), "incompatible_type"));
                        continue;
                    }
                    value = CopyValue(ps, source, p, out unit);
                }
                else if (text.Operation == TextOperation.Clear)
                {
                    if (!HasContent(p))
                    {
                        skipped.Add((e.Id.GetValue(), "unchanged"));
                        continue;
                    }
                    value = null;
                }
                else
                {
                    if (p.StorageType != StorageType.String)
                    {
                        skipped.Add((e.Id.GetValue(), "not_text"));
                        continue;
                    }
                    var current = p.AsString() ?? string.Empty;
                    if (!text.Matches(current))
                    {
                        skipped.Add((e.Id.GetValue(), "no_match"));
                        continue;
                    }
                    var next = text.Apply(current);
                    if (next == current)
                    {
                        skipped.Add((e.Id.GetValue(), "unchanged"));
                        continue;
                    }
                    value = next;
                }

                requests.Add(new ParameterWriteRequest
                {
                    Element = (resolved.On == "type" ? resolved.Owner : e).Id.GetValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Parameter = a.Parameter,
                    Value = value,
                    Unit = unit
                });
            }

            var skippedSummary = skipped.GroupBy(s => s.reason).ToDictionary(g => g.Key, g => g.Count());
            if (requests.Count == 0)
            {
                return new
                {
                    matched = elements.Count,
                    planned = 0,
                    applied = 0,
                    failed = 0,
                    skipped = skippedSummary,
                    skippedSample = skipped.Take(20).Select(s => new { elementId = s.id, reason = s.reason }),
                    changeSummary = "Nothing to write."
                };
            }
            if (requests.Count > a.MaxElements)
                throw new ToolException("TOO_MANY_ELEMENTS",
                    $"{requests.Count} writes would be made; maxElements is {a.MaxElements}.",
                    "Narrow the scope (categories, view, where[]) or raise maxElements (up to 2000).");

            var plan = ps.Plan(requests, new ParameterPlanOptions
            {
                Target = target,
                AllowCritical = a.AllowCritical,
                MaxItems = 2000
            });
            if (a.Atomic && plan.HasErrors)
                throw new ToolException("PARAMETER_WRITE_REFUSED",
                    $"{plan.Failed.Count} of {plan.Items.Count} write(s) are invalid; nothing was written. {plan.FailureSummary()}",
                    "Fix the cause, narrow the scope, or pass atomic:false to write only the valid ones.",
                    plan.Failed.First().RecommendedTool);

            var result = ps.Apply(plan);
            if (a.Atomic && result.Failed > 0)
                throw new ToolException("PARAMETER_WRITE_FAILED",
                    $"{result.Failed} write(s) did not stick, so everything was rolled back. " +
                    string.Join(" ", result.Items.Where(i => i.Status != "ok" && i.Status != "duplicate").Take(10)
                        .Select(i => $"[element {i.ElementId}] {i.Status}: {i.Message}")),
                    "Check formulas and value ranges, or pass atomic:false.");

            return new
            {
                matched = elements.Count,
                planned = requests.Count,
                applied = result.Applied,
                failed = result.Failed,
                skipped = skippedSummary,
                skippedSample = skipped.Take(20).Select(s => new { elementId = s.id, reason = s.reason }),
                sample = result.Items.Where(i => i.Status == "ok").Take(a.SampleSize)
                    .Select(i => new { elementId = i.ElementId, resolvedOn = i.ResolvedOn, before = i.Old, after = i.New, unit = i.Unit }),
                failures = result.Items.Where(i => i.Status != "ok" && i.Status != "duplicate").Take(50),
                revert = result.Revert,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + result.ChangeSummary
            };
        }

        private static string TextOf(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            return token.Type == JTokenType.String ? (string)token : token.ToString(Formatting.None);
        }

        private static bool HasContent(Parameter p)
        {
            if (p.StorageType == StorageType.String) return !string.IsNullOrEmpty(p.AsString());
            if (p.StorageType == StorageType.ElementId) return p.AsElementId() != ElementId.InvalidElementId;
            return p.HasValue;
        }

        /// <summary>Same storage and data type, or a text target (which takes the formatted value).</summary>
        internal static bool CanCopy(Parameter source, Parameter target)
        {
            if (target.StorageType == StorageType.String) return true;
            if (source.StorageType != target.StorageType) return false;
            if (source.StorageType != StorageType.Double) return true;
            var a = UnitsService.SpecOf(source);
            var b = UnitsService.SpecOf(target);
            return a == null || b == null ? a == b : a.TypeId == b.TypeId;
        }

        /// <summary>The source value in a form the target accepts: raw internal for measures, text for text targets.</summary>
        internal static object CopyValue(ParameterService ps, Parameter source, Parameter target, out string unit)
        {
            unit = null;
            if (target.StorageType == StorageType.String)
                return source.StorageType == StorageType.String ? source.AsString() : ParameterService.SafeValueString(source);
            switch (source.StorageType)
            {
                case StorageType.Double:
                    unit = UnitPolicy.Internal;
                    return source.AsDouble();
                case StorageType.Integer:
                    return source.AsInteger();
                case StorageType.ElementId:
                    var id = source.AsElementId();
                    return id == null || id == ElementId.InvalidElementId ? null : (object)id.GetValue();
                default:
                    return source.AsString();
            }
        }
    }
}
