using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Parameters
{
    public sealed class ListParameterBindingsArgs
    {
        [JsonProperty("nameFilter")]
        [Description("Only parameters whose name contains this text (case and accents ignored).")]
        public string NameFilter { get; set; }

        [JsonProperty("guid")]
        [Description("Only the shared parameter with this GUID.")]
        public string Guid { get; set; }

        [JsonProperty("kind")]
        [Description("all (default), shared (shared parameters bound to the project) or project (project-only parameters).")]
        [EnumValues("all", "shared", "project")]
        [DefaultValue("all")]
        public string Kind { get; set; } = "all";

        [JsonProperty("category")]
        [Description("Only bindings that include this category (any language).")]
        public string Category { get; set; }

        [JsonProperty("includeCategories")]
        [Description("List the bound categories of each parameter (default true).")]
        [DefaultValue(true)]
        public bool IncludeCategories { get; set; } = true;

        [JsonProperty("limit")]
        [Description("Page size (default 200).")]
        [Range(1, 2000)]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Entries to skip, for paging (default 0).")]
        [Range(0, int.MaxValue)]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>PAR-09: project and shared parameter bindings of the document.</summary>
    [McpTool("list_parameter_bindings", Kind = ToolKind.Read, Toolset = "parameters", Idempotent = true,
        Description = "List the project and shared parameters bound in the document: name, guid (shared), isShared, " +
                      "bindingKind (instance/type), dataType, group, variesAcrossGroups and the bound categories (localized " +
                      "name plus OST_ name). Filter by name, guid, kind or category; paged. Use before " +
                      "bind_shared_parameter to see what is already bound. Read-only.")]
    public sealed class ListParameterBindingsTool : McpTool<ListParameterBindingsArgs>
    {
        protected override object Run(ToolContext ctx, ListParameterBindingsArgs a)
        {
            var doc = ctx.Doc;
            var kind = (a.Kind ?? "all").Trim().ToLowerInvariant();
            Guid? guidFilter = null;
            if (!string.IsNullOrWhiteSpace(a.Guid))
            {
                if (!System.Guid.TryParse(a.Guid.Trim(), out var g))
                    throw new ToolException("INVALID_ARGUMENTS", $"'{a.Guid}' is not a GUID.");
                guidFilter = g;
            }
            long? categoryId = string.IsNullOrWhiteSpace(a.Category) ? (long?)null : ctx.Services.Categories.Resolve(a.Category).Id.GetValue();
            var nameKey = string.IsNullOrWhiteSpace(a.NameFilter) ? null : TextMatch.Fold(a.NameFilter);

            var rows = new List<BindingRow>();
            var it = doc.ParameterBindings.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                var row = BindingRow.From(doc, it.Key, it.Current as ElementBinding);
                if (row == null) continue;
                if (kind == "shared" && !row.IsShared) continue;
                if (kind == "project" && row.IsShared) continue;
                if (guidFilter.HasValue && row.GuidValue != guidFilter) continue;
                if (nameKey != null && !TextMatch.Fold(row.Name ?? string.Empty).Contains(nameKey)) continue;
                if (categoryId.HasValue && !row.CategoryIds.Contains(categoryId.Value)) continue;
                rows.Add(row);
            }

            var page = Paging.Page(rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), a.Limit, a.Offset);
            return new
            {
                items = page.Items.Select(r => new
                {
                    name = r.Name,
                    id = r.Id,
                    guid = r.GuidValue?.ToString(),
                    isShared = r.IsShared,
                    bindingKind = r.BindingKind,
                    dataType = r.DataType,
                    group = r.Group,
                    groupLabel = r.GroupLabel,
                    variesAcrossGroups = r.VariesAcrossGroups,
                    categoryCount = r.Categories.Count,
                    categories = a.IncludeCategories ? r.Categories : null
                }),
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }
    }

    /// <summary>One entry of the document's BindingMap. Also used by bind_shared_parameter.</summary>
    internal sealed class BindingRow
    {
        public Definition Definition;
        public ElementBinding Binding;
        public string Name;
        public long? Id;
        public Guid? GuidValue;
        public bool IsShared;
        public string BindingKind;
        public string DataType;
        public string Group;
        public string GroupLabel;
        public bool? VariesAcrossGroups;
        public List<object> Categories = new List<object>();
        public HashSet<long> CategoryIds = new HashSet<long>();

        public static BindingRow From(Document doc, Definition def, ElementBinding binding)
        {
            if (def == null || binding == null) return null;
            var row = new BindingRow
            {
                Definition = def,
                Binding = binding,
                Name = def.Name,
                BindingKind = binding is TypeBinding ? "type" : "instance"
            };
            try { row.DataType = def.GetDataType()?.TypeId; } catch { /* older definitions */ }
            try
            {
                var group = def.GetGroupTypeId();
                row.Group = group?.TypeId;
                if (group != null && !string.IsNullOrEmpty(group.TypeId)) row.GroupLabel = LabelUtils.GetLabelForGroup(group);
            }
            catch { /* no group label */ }

            if (def is InternalDefinition internalDef)
            {
                row.VariesAcrossGroups = internalDef.VariesAcrossGroups;
                var element = doc.GetElement(internalDef.Id);
                row.Id = internalDef.Id.GetValue();
                if (element is SharedParameterElement shared)
                {
                    row.IsShared = true;
                    row.GuidValue = shared.GuidValue;
                }
            }
            if (binding.Categories != null)
            {
                foreach (Category c in binding.Categories)
                {
                    if (c == null) continue;
                    row.CategoryIds.Add(c.Id.GetValue());
                    row.Categories.Add(new { name = c.Name, builtIn = CategoryService.BuiltInNameOf(c) });
                }
            }
            return row;
        }

        public static List<BindingRow> All(Document doc)
        {
            var rows = new List<BindingRow>();
            var it = doc.ParameterBindings.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                var row = From(doc, it.Key, it.Current as ElementBinding);
                if (row != null) rows.Add(row);
            }
            return rows;
        }
    }
}
