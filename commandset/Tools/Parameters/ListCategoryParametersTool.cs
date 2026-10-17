using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Parameters.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Parameters
{
    public sealed class ListCategoryParametersArgs
    {
        [JsonProperty("category", Required = Required.Always)]
        [Required]
        [Description("Category in any language: OST_Walls, Walls or Paredes.")]
        public string Category { get; set; }

        [JsonProperty("scope")]
        [Description("instance, type or both (default).")]
        [EnumValues("instance", "type", "both")]
        [DefaultValue("both")]
        public string Scope { get; set; } = "both";

        [JsonProperty("sampleSize")]
        [Description("How many elements (and types) to inspect, from the lowest ids (default 200, max 5000). Parameters can differ between families of one category.")]
        [Range(1, 5000)]
        [DefaultValue(200)]
        public int SampleSize { get; set; } = 200;

        [JsonProperty("withDistinctValues")]
        [Description("Return up to this many distinct values per parameter (default 0 = none, max 20).")]
        [Range(0, 20)]
        [DefaultValue(0)]
        public int WithDistinctValues { get; set; }

        [JsonProperty("nameFilter")]
        [Description("Only parameters whose name contains this text (case and accents ignored).")]
        public string NameFilter { get; set; }

        [JsonProperty("editableOnly")]
        [Description("Leave out read-only parameters. Default false.")]
        [DefaultValue(false)]
        public bool EditableOnly { get; set; }

        [JsonProperty("limit")]
        [Description("Page size (default 200).")]
        [Range(1, 2000)]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Parameters to skip, for paging (default 0).")]
        [Range(0, int.MaxValue)]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>PAR-02: which parameters exist in a category, with fill counts.</summary>
    [McpTool("list_category_parameters", Kind = ToolKind.Read, Toolset = "parameters", Idempotent = true,
        Description = "List the parameters found on elements of a category (any language: OST_Doors, Doors, Portas), " +
                      "instance and/or type, so you can pick the exact reference before reading or writing. Each entry " +
                      "has name, builtIn, guid, storageType, dataType, group, readOnly, on (instance/type), how many " +
                      "sampled elements have it (seenOn) and a value (filledCount), and optionally distinct values. " +
                      "Inspects a sample (sampleSize) of the category. Read-only.")]
    public sealed class ListCategoryParametersTool : McpTool<ListCategoryParametersArgs>
    {
        private sealed class Entry
        {
            public ParameterInfo Info;
            public int SeenOn;
            public int Filled;
            public bool AnyEditable;
            public HashSet<string> Distinct = new HashSet<string>(StringComparer.Ordinal);
        }

        protected override object Run(ToolContext ctx, ListCategoryParametersArgs a)
        {
            var scope = (a.Scope ?? "both").Trim().ToLowerInvariant();
            var category = ctx.Services.Categories.Resolve(a.Category);
            var ps = ctx.Services.Parameters;
            var filter = new ElementCategoryFilter(category.Id);

            var instances = new FilteredElementCollector(ctx.Doc).WherePasses(filter).WhereElementIsNotElementType();
            int totalInstances = instances.GetElementCount();
            var sample = instances.ToElements().OrderBy(e => e.Id.GetValue()).Take(a.SampleSize).ToList();

            var types = new List<Element>();
            int totalTypes = 0;
            if (scope != "instance")
            {
                var typeCollector = new FilteredElementCollector(ctx.Doc).WherePasses(filter).WhereElementIsElementType();
                totalTypes = typeCollector.GetElementCount();
                // Types actually used by the sample first, then the rest by id.
                var used = new HashSet<long>(sample.Select(e => e.GetTypeId()).Where(id => id != null && id != ElementId.InvalidElementId).Select(id => id.GetValue()));
                types = typeCollector.ToElements().OrderBy(t => used.Contains(t.Id.GetValue()) ? 0 : 1).ThenBy(t => t.Id.GetValue())
                    .Take(a.SampleSize).ToList();
            }

            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            if (scope != "type") foreach (var e in sample) Collect(ps, e, "instance", entries, a.WithDistinctValues);
            if (scope != "instance") foreach (var t in types) Collect(ps, t, "type", entries, a.WithDistinctValues);

            IEnumerable<Entry> list = entries.Values;
            if (!string.IsNullOrWhiteSpace(a.NameFilter))
            {
                var key = TextMatch.Fold(a.NameFilter);
                list = list.Where(x => TextMatch.Fold(x.Info.Name ?? string.Empty).Contains(key));
            }
            if (a.EditableOnly) list = list.Where(x => x.AnyEditable);

            var ordered = list.OrderBy(x => x.Info.On).ThenBy(x => x.Info.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var page = Paging.Page(ordered, a.Limit, a.Offset);
            return new
            {
                category = category.Name,
                categoryBuiltIn = category.BuiltInName,
                totalInstances,
                sampledInstances = scope == "type" ? 0 : sample.Count,
                totalTypes,
                sampledTypes = types.Count,
                parameters = page.Items.Select(x => new
                {
                    name = x.Info.Name,
                    builtIn = x.Info.BuiltIn,
                    guid = x.Info.Guid,
                    isShared = x.Info.IsShared,
                    on = x.Info.On,
                    storageType = x.Info.StorageType,
                    dataType = x.Info.DataType,
                    group = x.Info.Group,
                    unit = x.Info.Unit,
                    readOnly = !x.AnyEditable,
                    seenOn = x.SeenOn,
                    filledCount = x.Filled,
                    distinctValues = a.WithDistinctValues > 0 ? x.Distinct.OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).ToList() : null
                }),
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }

        private static void Collect(ParameterService ps, Element e, string on, Dictionary<string, Entry> entries, int distinctMax)
        {
            foreach (Parameter p in e.Parameters)
            {
                if (p?.Definition == null) continue;
                var id = ParameterService.Identify(p);
                var key = on + "|" + (id.Guid ?? id.BuiltIn ?? "name:" + id.Name) + "|" + p.StorageType;
                if (!entries.TryGetValue(key, out var entry))
                {
                    entry = new Entry { Info = ps.Describe(p, on) };
                    entries[key] = entry;
                }
                entry.SeenOn++;
                if (!p.IsReadOnly) entry.AnyEditable = true;
                var filled = p.StorageType == StorageType.String ? !string.IsNullOrEmpty(p.AsString())
                    : p.StorageType == StorageType.ElementId ? p.AsElementId() != ElementId.InvalidElementId
                    : p.HasValue;
                if (!filled) continue;
                entry.Filled++;
                if (distinctMax > 0 && entry.Distinct.Count < distinctMax)
                {
                    var text = p.StorageType == StorageType.String ? p.AsString() : ParameterService.SafeValueString(p) ?? ps.Snapshot(p).Text;
                    if (!string.IsNullOrEmpty(text)) entry.Distinct.Add(text);
                }
            }
        }
    }
}
