using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>Where a parameter is looked up.</summary>
    public enum ParameterTarget
    {
        /// <summary>On the element itself.</summary>
        Instance,
        /// <summary>On the element's type (a type id may be passed directly).</summary>
        Type,
        /// <summary>Instance first, then type; the result says where it was found.</summary>
        Auto
    }

    /// <summary>A parameter found on an element or on its type.</summary>
    public sealed class ResolvedParameter
    {
        public Parameter Parameter { get; set; }

        /// <summary>The element that owns the parameter (the type for type parameters).</summary>
        public Element Owner { get; set; }

        /// <summary>"instance" or "type".</summary>
        public string On { get; set; }
    }

    /// <summary>Identity of a parameter as returned to the caller: enough to reference it again unambiguously.</summary>
    public sealed class ParameterIdentity
    {
        [JsonProperty("name")] public string Name { get; set; }

        [JsonProperty("builtIn", NullValueHandling = NullValueHandling.Ignore)]
        public string BuiltIn { get; set; }

        [JsonProperty("guid", NullValueHandling = NullValueHandling.Ignore)]
        public string Guid { get; set; }

        /// <summary>The most stable reference: guid → builtIn → name.</summary>
        public ParameterRef ToRef() =>
            Guid != null ? new ParameterRef { Guid = Guid } :
            BuiltIn != null ? new ParameterRef { BuiltIn = BuiltIn } :
            new ParameterRef { Name = Name };
    }

    /// <summary>Full description of one parameter value (PAR-01 output).</summary>
    public sealed class ParameterInfo
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("builtIn", NullValueHandling = NullValueHandling.Ignore)] public string BuiltIn { get; set; }
        [JsonProperty("guid", NullValueHandling = NullValueHandling.Ignore)] public string Guid { get; set; }
        [JsonProperty("isShared")] public bool IsShared { get; set; }

        /// <summary>"instance" or "type".</summary>
        [JsonProperty("on")] public string On { get; set; }

        [JsonProperty("storageType")] public string StorageType { get; set; }
        [JsonProperty("dataType", NullValueHandling = NullValueHandling.Ignore)] public string DataType { get; set; }
        [JsonProperty("group", NullValueHandling = NullValueHandling.Ignore)] public string Group { get; set; }
        [JsonProperty("readOnly")] public bool ReadOnly { get; set; }
        [JsonProperty("hasValue")] public bool HasValue { get; set; }

        /// <summary>Wire value: mm/m²/m³/deg/% for measures, the element name for ElementId, text, integer.</summary>
        [JsonProperty("value")] public object Value { get; set; }

        [JsonProperty("unit", NullValueHandling = NullValueHandling.Ignore)] public string Unit { get; set; }

        /// <summary>Revit internal value (feet, radians, element id...).</summary>
        [JsonProperty("raw", NullValueHandling = NullValueHandling.Ignore)] public object Raw { get; set; }

        /// <summary>Revit's formatted text in the project's units and language (informative only).</summary>
        [JsonProperty("display", NullValueHandling = NullValueHandling.Ignore)] public string Display { get; set; }
    }

    /// <summary>One requested write.</summary>
    public sealed class ParameterWriteRequest
    {
        /// <summary>Element reference (id, UniqueId). Linked elements are refused.</summary>
        public string Element { get; set; }

        public ParameterRef Parameter { get; set; }

        /// <summary>JSON value (JToken) or primitive.</summary>
        public object Value { get; set; }

        /// <summary>Optional unit override for numbers ("m", "cm", "internal"...).</summary>
        public string Unit { get; set; }
    }

    public sealed class ParameterPlanOptions
    {
        public ParameterTarget Target { get; set; } = ParameterTarget.Instance;
        public bool AllowCritical { get; set; }
        public int MaxItems { get; set; } = 500;
    }

    /// <summary>A validated (or failed) write, ready for <see cref="ParameterService.Apply"/>.</summary>
    public sealed class PlannedWrite
    {
        public int Index { get; set; }
        public string ElementText { get; set; }
        public Element Source { get; set; }
        public Element Owner { get; set; }
        public Parameter Parameter { get; set; }
        public ParameterIdentity Identity { get; set; }
        public string ResolvedOn { get; set; }
        public CoercedValue Value { get; set; }
        public ElementId IdValue { get; set; }
        public object RequestedWire { get; set; }
        public object OldWire { get; set; }
        public string OldUnit { get; set; }
        public string OldDisplay { get; set; }
        public int? InstancesAffected { get; set; }

        /// <summary>planned, duplicate, or an error status (not_found, ambiguous, read_only, critical, invalid_value...).</summary>
        public string Status { get; set; } = "planned";

        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public string RecommendedTool { get; set; }
        public bool IsValid => ErrorCode == null;
    }

    public sealed class ParameterPlan
    {
        public ParameterPlanOptions Options { get; set; }
        public List<PlannedWrite> Items { get; } = new List<PlannedWrite>();
        public IList<PlannedWrite> Failed => Items.Where(i => !i.IsValid).ToList();
        public bool HasErrors => Items.Any(i => !i.IsValid);

        /// <summary>One line per failed item, for an atomic refusal message.</summary>
        public string FailureSummary(int max = 10) =>
            string.Join(" ", Failed.Take(max).Select(f => $"[{f.Index}] {f.ElementText}: {f.ErrorCode} - {f.Message}")) +
            (Failed.Count > max ? $" (+{Failed.Count - max} more)" : string.Empty);
    }

    public sealed class ParameterWriteItemResult
    {
        [JsonProperty("index")] public int Index { get; set; }
        [JsonProperty("elementId")] public long? ElementId { get; set; }
        [JsonProperty("uniqueId", NullValueHandling = NullValueHandling.Ignore)] public string UniqueId { get; set; }
        [JsonProperty("parameter", NullValueHandling = NullValueHandling.Ignore)] public ParameterIdentity Parameter { get; set; }
        [JsonProperty("resolvedOn", NullValueHandling = NullValueHandling.Ignore)] public string ResolvedOn { get; set; }
        [JsonProperty("old", NullValueHandling = NullValueHandling.Ignore)] public object Old { get; set; }
        [JsonProperty("new", NullValueHandling = NullValueHandling.Ignore)] public object New { get; set; }
        [JsonProperty("unit", NullValueHandling = NullValueHandling.Ignore)] public string Unit { get; set; }
        [JsonProperty("display", NullValueHandling = NullValueHandling.Ignore)] public string Display { get; set; }

        /// <summary>ok | mismatch | set_failed | duplicate | or the plan error status.</summary>
        [JsonProperty("status")] public string Status { get; set; }

        [JsonProperty("changed", NullValueHandling = NullValueHandling.Ignore)] public bool? Changed { get; set; }
        [JsonProperty("requested", NullValueHandling = NullValueHandling.Ignore)] public object Requested { get; set; }
        [JsonProperty("code", NullValueHandling = NullValueHandling.Ignore)] public string Code { get; set; }
        [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)] public string Message { get; set; }
        [JsonProperty("recommendedTool", NullValueHandling = NullValueHandling.Ignore)] public string RecommendedTool { get; set; }
        [JsonProperty("instancesAffected", NullValueHandling = NullValueHandling.Ignore)] public int? InstancesAffected { get; set; }
    }

    /// <summary>A write that undoes one applied item (feed it back to set_parameters / set_type_parameters).</summary>
    public sealed class RevertItem
    {
        [JsonProperty("elementId")] public string ElementId { get; set; }
        [JsonProperty("parameter")] public ParameterRef Parameter { get; set; }
        [JsonProperty("value")] public object Value { get; set; }
        [JsonProperty("unit", NullValueHandling = NullValueHandling.Ignore)] public string Unit { get; set; }
    }

    public sealed class ParameterWriteResult
    {
        [JsonProperty("applied")] public int Applied { get; set; }
        [JsonProperty("failed")] public int Failed { get; set; }
        [JsonProperty("items")] public List<ParameterWriteItemResult> Items { get; } = new List<ParameterWriteItemResult>();
        [JsonProperty("revert")] public List<RevertItem> Revert { get; } = new List<RevertItem>();
        [JsonProperty("changeSummary")] public string ChangeSummary { get; set; }
    }

    /// <summary>
    /// The parameter contract (research D, 8 rules) implemented once: reference by GUID → BuiltIn → name (ambiguity is
    /// an error), instance/type, wire units by spec, conversion by StorageType with ElementId by name, critical
    /// parameters blocked, Plan (validate everything, write nothing) then Apply (Set + mandatory read-back + revert[]).
    /// Transactions, dryRun rollback and atomicity across the call belong to the dispatcher.
    /// </summary>
    public sealed class ParameterService
    {
        private readonly Document _doc;
        private readonly UnitsService _units;
        private readonly LookupService _lookup;
        private readonly Dictionary<long, int> _instanceCounts = new Dictionary<long, int>();

        public ParameterService(Document doc, UnitsService units, LookupService lookup)
        {
            _doc = doc;
            _units = units;
            _lookup = lookup;
        }

        // ---- resolution (rules 1 and 2) -------------------------------------------------------------------------

        /// <summary>The element's type (or the element itself when it is a type); null when it has none.</summary>
        public static ElementType TypeOf(Element element)
        {
            if (element is ElementType t) return t;
            var id = element.GetTypeId();
            if (id == null || id == ElementId.InvalidElementId) return null;
            return element.Document.GetElement(id) as ElementType;
        }

        /// <summary>
        /// Finds a parameter on exactly this element. Null when absent. Two different parameters with the same name
        /// → AMBIGUOUS_PARAMETER listing builtIn/guid of each (never the first one).
        /// </summary>
        public Parameter Find(Element owner, ParameterRef reference)
        {
            if (reference == null || reference.IsEmpty)
                throw new ToolException("INVALID_ARGUMENTS", "A parameter reference needs a guid, builtIn or name.");

            var guid = reference.ParsedGuid();
            if (guid.HasValue) return owner.get_Parameter(guid.Value);

            if (!string.IsNullOrWhiteSpace(reference.BuiltIn))
                return owner.get_Parameter(ParseBuiltIn(reference.BuiltIn));

            var name = reference.Name.Trim();
            var hits = Distinct(owner.GetParameters(name));
            if (hits.Count == 0)
                hits = Distinct(owner.Parameters.Cast<Parameter>()
                    .Where(p => TextMatch.Fold(p.Definition?.Name) == TextMatch.Fold(name)));
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1)
                throw new ToolException("AMBIGUOUS_PARAMETER",
                    $"'{name}' matches {hits.Count} parameters on element {owner.Id.GetValue()}: " +
                    string.Join("; ", hits.Select(p => Describe(Identify(p)))) + ".",
                    "Reference it by builtIn or guid instead of name.");

            // A BuiltInParameter enum name passed as a plain string.
            if (TryParseBuiltIn(name, out var bip)) return owner.get_Parameter(bip);
            return null;
        }

        /// <summary>Resolves a parameter on an element honouring <paramref name="target"/>.</summary>
        public ResolvedParameter Resolve(Element element, ParameterRef reference, ParameterTarget target)
        {
            if (target != ParameterTarget.Type && !(element is ElementType))
            {
                var p = Find(element, reference);
                if (p != null) return new ResolvedParameter { Parameter = p, Owner = element, On = "instance" };
                if (target == ParameterTarget.Instance)
                {
                    var type = TypeOf(element);
                    if (type != null && Find(type, reference) != null)
                        throw new ToolException("PARAMETER_ON_TYPE",
                            $"'{reference}' is a type parameter of element {element.Id.GetValue()}; changing it affects every instance of '{type.Name}'.",
                            "Use set_type_parameters to change type parameters.", "set_type_parameters");
                    throw NotFound(element, reference);
                }
            }

            var owner = element is ElementType ? element : TypeOf(element);
            if (owner == null)
                throw new ToolException("TYPE_NOT_FOUND", $"Element {element.Id.GetValue()} has no type.");
            var tp = Find(owner, reference);
            if (tp != null) return new ResolvedParameter { Parameter = tp, Owner = owner, On = "type" };
            throw NotFound(owner, reference);
        }

        private ToolException NotFound(Element owner, ParameterRef reference)
        {
            var names = owner.Parameters.Cast<Parameter>().Select(p => p.Definition?.Name).Where(n => n != null);
            var suggestions = reference.Name != null ? TextMatch.Suggest(reference.Name, names) : new List<string>();
            return new ToolException("PARAMETER_NOT_FOUND",
                $"Element {owner.Id.GetValue()} has no parameter '{reference}'.",
                suggestions.Count > 0
                    ? "Did you mean: " + string.Join(", ", suggestions.Select(s => $"'{s}'")) + "?"
                    : "Use get_element_parameters to list the element's parameters.",
                "get_element_parameters");
        }

        // ---- description and reading (rule 3) ------------------------------------------------------------------

        public static ParameterIdentity Identify(Parameter p)
        {
            var id = new ParameterIdentity { Name = p.Definition?.Name };
            if (p.Definition is InternalDefinition internalDef && internalDef.BuiltInParameter != BuiltInParameter.INVALID)
                id.BuiltIn = internalDef.BuiltInParameter.ToString();
            if (p.IsShared)
            {
                try { id.Guid = p.GUID.ToString(); }
                catch { /* not shared after all */ }
            }
            return id;
        }

        public ParameterInfo Describe(Parameter p, string on, bool compact = false)
        {
            var identity = Identify(p);
            var info = new ParameterInfo
            {
                Name = identity.Name,
                BuiltIn = identity.BuiltIn,
                Guid = identity.Guid,
                IsShared = p.IsShared,
                On = on,
                StorageType = p.StorageType.ToString(),
                ReadOnly = p.IsReadOnly,
                HasValue = p.HasValue,
            };
            if (!compact)
            {
                info.DataType = UnitsService.SpecOf(p)?.TypeId;
                info.Group = GroupOf(p);
                info.Display = SafeValueString(p);
            }
            info.Value = ReadWire(p, out var unit, out var raw);
            info.Unit = unit;
            if (!compact) info.Raw = raw;
            return info;
        }

        /// <summary>The value in wire form (and its unit), plus Revit's raw value.</summary>
        public object ReadWire(Parameter p, out string unit, out object raw)
        {
            unit = null;
            raw = null;
            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.AsString();
                case StorageType.Integer:
                    raw = p.AsInteger();
                    return raw;
                case StorageType.Double:
                {
                    var d = p.AsDouble();
                    raw = d;
                    _units.TryFromInternal(d, UnitsService.SpecOf(p), out var wire, out unit);
                    return Math.Round(wire, 9);
                }
                case StorageType.ElementId:
                {
                    var id = p.AsElementId();
                    var v = id?.GetValue() ?? -1;
                    raw = v;
                    if (v == -1) return null;
                    if (v < 0) return v; // built-in ids (categories, parameters)
                    return DocOf(p).GetElement(id)?.Name ?? (object)v;
                }
                default:
                    return null;
            }
        }

        /// <summary>Value prepared for where[] comparison: wire number for measures, names for ElementId.</summary>
        public ValueSnapshot Snapshot(Parameter p)
        {
            if (p == null) return ValueSnapshot.Missing;
            var s = new ValueSnapshot { Exists = true };
            switch (p.StorageType)
            {
                case StorageType.String:
                    s.Text = p.AsString();
                    break;
                case StorageType.Integer:
                    s.Number = p.AsInteger();
                    s.Text = SafeValueString(p) ?? p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    break;
                case StorageType.Double:
                    if (_units.TryFromInternal(p.AsDouble(), UnitsService.SpecOf(p), out var wire, out _)) s.Number = wire;
                    s.Text = SafeValueString(p);
                    break;
                case StorageType.ElementId:
                    var id = p.AsElementId();
                    if (id != null && id != ElementId.InvalidElementId)
                        s.Text = id.GetValue() > 0 ? DocOf(p).GetElement(id)?.Name : SafeValueString(p);
                    break;
            }
            if (!p.HasValue && s.Number.HasValue && p.StorageType == StorageType.Double) s.Number = null;
            return s;
        }

        // ---- plan (rules 4 and 5) ------------------------------------------------------------------------------

        /// <summary>Validates every write without changing the model. Item failures are recorded, not thrown.</summary>
        public ParameterPlan Plan(IList<ParameterWriteRequest> requests, ParameterPlanOptions options)
        {
            options = options ?? new ParameterPlanOptions();
            if (requests == null || requests.Count == 0)
                throw new ToolException("INVALID_ARGUMENTS", "Nothing to write: pass at least one item.");
            if (requests.Count > options.MaxItems)
                throw new ToolException("TOO_MANY_ITEMS", $"{requests.Count} writes requested; the limit is {options.MaxItems} per call.",
                    "Split the request into several calls.");

            var plan = new ParameterPlan { Options = options };
            for (var i = 0; i < requests.Count; i++)
            {
                var item = new PlannedWrite { Index = i, ElementText = requests[i].Element };
                plan.Items.Add(item);
                try
                {
                    PlanOne(item, requests[i], options);
                }
                catch (ToolException ex)
                {
                    Fail(item, ex);
                }
            }

            CheckDuplicatesAndBulk(plan, options);
            return plan;
        }

        private void PlanOne(PlannedWrite item, ParameterWriteRequest request, ParameterPlanOptions options)
        {
            var element = _lookup.Element(request.Element);
            item.Source = element;
            if (options.Target == ParameterTarget.Instance && element is ElementType)
                throw new ToolException("TARGET_IS_TYPE", $"Element {element.Id.GetValue()} is a type.",
                    "Use set_type_parameters to change type parameters.", "set_type_parameters");

            var resolved = Resolve(element, request.Parameter, options.Target);
            var p = resolved.Parameter;
            item.Owner = resolved.Owner;
            item.Parameter = p;
            item.ResolvedOn = resolved.On;
            item.Identity = Identify(p);
            item.OldWire = ReadWire(p, out var oldUnit, out var oldRaw);
            if (p.StorageType == StorageType.ElementId) item.OldWire = oldRaw;
            item.OldUnit = oldUnit;
            item.OldDisplay = SafeValueString(p);
            if (resolved.On == "type") item.InstancesAffected = CountInstances(resolved.Owner);

            if (p.IsReadOnly)
                throw new ToolException("READ_ONLY", $"'{item.Identity.Name}' is read-only (computed, formula-driven or locked by Revit).");

            var block = CriticalParameters.BlockReason(item.Identity.BuiltIn, 1, options.AllowCritical);
            if (block != null)
                throw new ToolException("CRITICAL_PARAMETER", block + " Blocked by default.", CriticalHint(item.Identity.BuiltIn));

            var kind = KindOf(p.StorageType);
            var spec = kind == StorageKind.Double ? _units.Classify(p) : UnitSpec.Dimensionless;
            item.Value = ValueCoercion.Coerce(kind, request.Value, spec, request.Unit, item.Identity.Name);
            item.RequestedWire = ValueCoercion.ToPrimitive(request.Value);

            if (kind == StorageKind.ElementId)
            {
                item.IdValue = item.Value.IdName != null
                    ? ResolveIdByName(p, resolved.Owner, item.Value.IdName)
                    : item.Value.Id.Value <= 0 ? ElementId.InvalidElementId : item.Value.Id.Value.ToRevitElementId();
                if (item.IdValue != ElementId.InvalidElementId && _doc.GetElement(item.IdValue) == null)
                    throw new ToolException("ELEMENT_NOT_FOUND", $"Element id {item.IdValue.GetValue()} does not exist.");
            }
        }

        private void CheckDuplicatesAndBulk(ParameterPlan plan, ParameterPlanOptions options)
        {
            var valid = plan.Items.Where(i => i.IsValid).ToList();

            // The same parameter of the same owner written twice (e.g. two instances of one type).
            foreach (var group in valid.GroupBy(i => (i.Owner.Id.GetValue(), i.Parameter.Id.GetValue())).Where(g => g.Count() > 1))
            {
                var first = group.First();
                foreach (var other in group.Skip(1))
                {
                    if (SameRequestedValue(first, other))
                    {
                        other.Status = "duplicate";
                    }
                    else
                    {
                        Fail(other, new ToolException("CONFLICTING_WRITE",
                            $"'{other.Identity.Name}' of element {other.Owner.Id.GetValue()} is already written by item {first.Index} with another value."));
                    }
                }
            }

            // Mark written with one value on several elements.
            if (!options.AllowCritical)
            {
                foreach (var group in valid.Where(i => i.IsValid && i.Status != "duplicate" && i.Identity.BuiltIn == CriticalParameters.Mark)
                             .GroupBy(i => i.Value.Text ?? string.Empty).Where(g => g.Count() > 1))
                {
                    var reason = CriticalParameters.BlockReason(CriticalParameters.Mark, group.Count(), false);
                    foreach (var i in group)
                        Fail(i, new ToolException("CRITICAL_PARAMETER", reason, "Pass allowCritical:true if duplicate marks are intended."));
                }
            }
        }

        private static bool SameRequestedValue(PlannedWrite a, PlannedWrite b)
        {
            switch (a.Value.Kind)
            {
                case StorageKind.String: return a.Value.Text == b.Value.Text;
                case StorageKind.Integer: return a.Value.Integer == b.Value.Integer;
                case StorageKind.Double: return ReadBack.SameDouble(a.Value.Double, b.Value.Double);
                case StorageKind.ElementId: return a.IdValue == b.IdValue;
                default: return false;
            }
        }

        // ---- apply (rules 6 to 8: the dispatcher owns the transaction, rollback and dryRun) --------------------

        /// <summary>
        /// Writes every valid item and reads it back. Must run inside the tool's transaction. Items that failed in
        /// <see cref="Plan"/> are reported, not written. Atomicity (refusing on any failure) is the caller's choice:
        /// throw a ToolException after this returns with failures and the dispatcher rolls everything back.
        /// </summary>
        public ParameterWriteResult Apply(ParameterPlan plan)
        {
            var result = new ParameterWriteResult();
            foreach (var item in plan.Items)
            {
                var r = new ParameterWriteItemResult
                {
                    Index = item.Index,
                    ElementId = (item.Owner ?? item.Source)?.Id.GetValue(),
                    UniqueId = (item.Owner ?? item.Source)?.UniqueId,
                    Parameter = item.Identity,
                    ResolvedOn = item.ResolvedOn,
                    Old = item.OldWire,
                    InstancesAffected = item.InstancesAffected,
                };
                result.Items.Add(r);

                if (!item.IsValid)
                {
                    r.Status = item.Status;
                    r.Code = item.ErrorCode;
                    r.Message = item.Message;
                    r.RecommendedTool = item.RecommendedTool;
                    result.Failed++;
                    continue;
                }
                if (item.Status == "duplicate")
                {
                    r.Status = "duplicate";
                    continue;
                }

                var p = item.Parameter;
                bool accepted;
                try
                {
                    accepted = Set(p, item);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
                {
                    r.Status = "set_failed";
                    r.Code = "SET_FAILED";
                    r.Message = $"Revit refused the value: {ex.Message}";
                    result.Failed++;
                    continue;
                }

                r.New = ReadWire(p, out var newUnit, out var newRaw);
                if (p.StorageType == StorageType.ElementId) r.New = newRaw;
                r.Unit = newUnit ?? item.OldUnit;
                r.Display = SafeValueString(p);

                if (!accepted)
                {
                    r.Status = "set_failed";
                    r.Code = "SET_FAILED";
                    r.Message = "Revit did not accept the value (Parameter.Set returned false).";
                    result.Failed++;
                    continue;
                }

                if (!ReadBackMatches(p, item))
                {
                    r.Status = "mismatch";
                    r.Code = "READBACK_MISMATCH";
                    r.Requested = item.RequestedWire;
                    r.Message = "Revit stored a different value than requested (formula, rounding or a constraint).";
                    result.Failed++;
                }
                else
                {
                    r.Status = "ok";
                    result.Applied++;
                }
                r.Changed = !Equals(Normalize(item.OldWire), Normalize(r.New));

                result.Revert.Add(new RevertItem
                {
                    ElementId = item.Owner.Id.GetValue().ToString(CultureInfo.InvariantCulture),
                    Parameter = item.Identity.ToRef(),
                    Value = item.OldWire,
                    Unit = item.OldUnit == UnitPolicy.Internal ? UnitPolicy.Internal : null,
                });
            }

            var owners = result.Items.Where(i => i.Status == "ok").Select(i => i.ElementId).Distinct().Count();
            result.ChangeSummary = $"{result.Applied} value(s) written on {owners} element(s); {result.Failed} failed.";
            return result;
        }

        private static object Normalize(object v) => v is double d ? Math.Round(d, 6) : v is int i ? (long)i : v;

        private static bool Set(Parameter p, PlannedWrite item)
        {
            switch (item.Value.Kind)
            {
                case StorageKind.String: return p.Set(item.Value.Text ?? string.Empty);
                case StorageKind.Integer: return p.Set(item.Value.Integer);
                case StorageKind.Double: return p.Set(item.Value.Double);
                case StorageKind.ElementId: return p.Set(item.IdValue);
                default: return false;
            }
        }

        private static bool ReadBackMatches(Parameter p, PlannedWrite item)
        {
            switch (item.Value.Kind)
            {
                case StorageKind.String: return ReadBack.SameText(item.Value.Text, p.AsString());
                case StorageKind.Integer: return p.AsInteger() == item.Value.Integer;
                case StorageKind.Double: return ReadBack.SameDouble(item.Value.Double, p.AsDouble());
                case StorageKind.ElementId: return p.AsElementId() == item.IdValue;
                default: return false;
            }
        }

        // ---- ElementId by name ---------------------------------------------------------------------------------

        /// <summary>
        /// Resolves a name for an ElementId parameter: the type list for ELEM_TYPE_PARAM, materials for material
        /// specs, levels / phases by built-in name, otherwise elements of the same class (and category) as the
        /// current value. AMBIGUOUS_NAME / NOT_FOUND as usual; ID_NAME_UNRESOLVABLE when the kind is unknown.
        /// </summary>
        public ElementId ResolveIdByName(Parameter p, Element owner, string name)
        {
            var builtIn = Identify(p).BuiltIn ?? string.Empty;

            if (builtIn == "ELEM_TYPE_PARAM" || builtIn == "SYMBOL_ID_PARAM" || builtIn == "ELEM_FAMILY_AND_TYPE_PARAM")
            {
                var valid = owner.GetValidTypes().Select(id => _doc.GetElement(id)).OfType<ElementType>().ToList();
                FamilyTypeName.Parse(name, out var family, out var typeName);
                var pool = family == null ? valid
                    : valid.Where(t => TextMatch.Key(LookupService.SafeFamilyName(t)) == TextMatch.Key(family)).ToList();
                return NameMatcher.Single(typeName, pool.Select(t => new NamedCandidate<ElementId>(t.Name, t.Id, LookupService.FullName(t))), "type");
            }

            if (IsMaterialSpec(p) || builtIn.IndexOf("MATERIAL", StringComparison.Ordinal) >= 0)
                return _lookup.Material(name).Id;

            var current = p.AsElementId();
            var currentElement = current != null && current.GetValue() > 0 ? _doc.GetElement(current) : null;
            if (currentElement is Level || builtIn.IndexOf("LEVEL", StringComparison.Ordinal) >= 0 ||
                builtIn == "WALL_HEIGHT_TYPE" || builtIn.EndsWith("_CONSTRAINT", StringComparison.Ordinal))
                return _lookup.Level(name).Id;
            if (currentElement is Phase || builtIn.IndexOf("PHASE", StringComparison.Ordinal) >= 0)
                return _lookup.Phase(name).Id;
            if (currentElement is Material) return _lookup.Material(name).Id;
            if (currentElement is View) return _lookup.View(name).Id;

            if (currentElement != null)
            {
                var collector = new FilteredElementCollector(_doc).OfClass(currentElement.GetType());
                if (currentElement.Category != null) collector = collector.OfCategoryId(currentElement.Category.Id);
                return NameMatcher.Single(name,
                    collector.ToElements().Select(e => new NamedCandidate<ElementId>(e.Name, e.Id, "id " + e.Id.GetValue())),
                    currentElement.Category?.Name ?? currentElement.GetType().Name);
            }

            throw new ToolException("ID_NAME_UNRESOLVABLE",
                $"Cannot tell what kind of element '{Identify(p).Name}' refers to, so '{name}' cannot be resolved by name.",
                "Pass the element id as a number.");
        }

        private static bool IsMaterialSpec(Parameter p)
        {
            var spec = UnitsService.SpecOf(p);
            return spec != null && spec.TypeId != null && spec.TypeId.IndexOf("material", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---- helpers -------------------------------------------------------------------------------------------

        private int CountInstances(Element type)
        {
            var key = type.Id.GetValue();
            if (_instanceCounts.TryGetValue(key, out var n)) return n;
            FilteredElementCollector collector;
            if (type is FamilySymbol)
                collector = new FilteredElementCollector(_doc).WherePasses(new FamilyInstanceFilter(_doc, type.Id));
            else if (type.Category != null)
                collector = new FilteredElementCollector(_doc).OfCategoryId(type.Category.Id).WhereElementIsNotElementType();
            else
                collector = new FilteredElementCollector(_doc).WhereElementIsNotElementType();
            n = type is FamilySymbol ? collector.GetElementCount() : collector.ToElements().Count(e => e.GetTypeId() == type.Id);
            _instanceCounts[key] = n;
            return n;
        }

        private static void Fail(PlannedWrite item, ToolException ex)
        {
            item.ErrorCode = ex.Code;
            item.Message = ex.Message + (string.IsNullOrEmpty(ex.Hint) ? string.Empty : " " + ex.Hint);
            item.RecommendedTool = ex.RecommendedTool;
            item.Status = StatusFor(ex.Code);
        }

        private static string StatusFor(string code)
        {
            switch (code)
            {
                case "READ_ONLY": return "read_only";
                case "CRITICAL_PARAMETER": return "critical";
                case "AMBIGUOUS_PARAMETER":
                case "AMBIGUOUS_NAME": return "ambiguous";
                case "PARAMETER_NOT_FOUND":
                case "ELEMENT_NOT_FOUND":
                case "NOT_FOUND":
                case "TYPE_NOT_FOUND": return "not_found";
                case "UNIT_UNSUPPORTED": return "unit_unsupported";
                case "INVALID_VALUE": return "invalid_value";
                default: return "error";
            }
        }

        private static string CriticalHint(string builtIn)
        {
            if (builtIn != null && builtIn.StartsWith("PHASE_", StringComparison.Ordinal))
                return "Pass allowCritical:true if this is intended (a dedicated set_element_phase tool is planned).";
            if (builtIn == "ELEM_PARTITION_PARAM")
                return "Pass allowCritical:true if this is intended (a dedicated assign_workset tool is planned).";
            return "Pass allowCritical:true if this change is intended.";
        }

        private static StorageKind KindOf(StorageType t)
        {
            switch (t)
            {
                case StorageType.Integer: return StorageKind.Integer;
                case StorageType.Double: return StorageKind.Double;
                case StorageType.String: return StorageKind.String;
                case StorageType.ElementId: return StorageKind.ElementId;
                default: return StorageKind.None;
            }
        }

        private static List<Parameter> Distinct(IEnumerable<Parameter> ps) =>
            (ps ?? Enumerable.Empty<Parameter>()).Where(p => p != null)
                .GroupBy(p => p.Id.GetValue()).Select(g => g.First()).ToList();

        private static string Describe(ParameterIdentity id) =>
            id.Guid != null ? $"{id.Name} (guid {id.Guid})" :
            id.BuiltIn != null ? $"{id.Name} (builtIn {id.BuiltIn})" :
            id.Name;

        public static BuiltInParameter ParseBuiltIn(string text)
        {
            if (TryParseBuiltIn(text, out var bip)) return bip;
            var suggestions = TextMatch.Suggest(text, Enum.GetNames(typeof(BuiltInParameter)), 3);
            throw new ToolException("PARAMETER_NOT_FOUND", $"'{text}' is not a BuiltInParameter name.",
                suggestions.Count > 0 ? "Did you mean: " + string.Join(", ", suggestions) + "?" : null);
        }

        private static bool TryParseBuiltIn(string text, out BuiltInParameter bip)
        {
            bip = BuiltInParameter.INVALID;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim();
            if (t.IndexOf(',') >= 0 || t.IndexOf(' ') >= 0 || char.IsDigit(t[0]) || t[0] == '-') return false;
            return Enum.TryParse(t, false, out bip) && Enum.IsDefined(typeof(BuiltInParameter), bip) && bip != BuiltInParameter.INVALID;
        }

        private static string GroupOf(Parameter p)
        {
            try { return p.Definition?.GetGroupTypeId()?.TypeId; }
            catch { return null; }
        }

        /// <summary>The document that owns the parameter (a link document for linked elements).</summary>
        private Document DocOf(Parameter p) => p.Element?.Document ?? _doc;

        public static string SafeValueString(Parameter p)
        {
            try { return p.AsValueString(); }
            catch { return null; }
        }
    }
}
