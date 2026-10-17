using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Editing.M1Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class ListGroupsArgs
    {
        [JsonProperty("kind")]
        [EnumValues("all", "model", "detail", "attached")]
        [DefaultValue("all")]
        [Description("Which group types: all (default), model, detail or attached (attached detail groups of model groups).")]
        public string Kind { get; set; } = "all";

        [JsonProperty("nameContains")]
        [Description("Only group types whose name contains this text (case-insensitive).")]
        public string NameContains { get; set; }

        [JsonProperty("groupId")]
        [Description("A group instance id: lists that group's members (paged) instead of the group types.")]
        public long? GroupId { get; set; }

        [JsonProperty("includeInstances")]
        [DefaultValue(true)]
        [Description("List the instances of each group type (up to 50 per type). Default true.")]
        public bool IncludeInstances { get; set; } = true;

        [JsonProperty("limit")]
        [System.ComponentModel.DataAnnotations.Range(1, 1000)]
        [DefaultValue(100)]
        [Description("Page size, 1-1000 (default 100).")]
        public int Limit { get; set; } = 100;

        [JsonProperty("offset")]
        [System.ComponentModel.DataAnnotations.Range(0, int.MaxValue)]
        [DefaultValue(0)]
        [Description("Items to skip (paging).")]
        public int Offset { get; set; }
    }

    /// <summary>CON-18: group types, their instances and the members of one group.</summary>
    [McpTool("list_groups", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "List model, detail and attached detail group types with their instance count and instances " +
                      "(id, level, origin in mm, member count), or, with groupId, the members of one group instance " +
                      "(paged). Use it before manage_groups or place_group_instance. Read-only.")]
    public sealed class ListGroupsTool : McpTool<ListGroupsArgs>
    {
        protected override object Run(ToolContext ctx, ListGroupsArgs a)
        {
            var doc = ctx.Doc;
            if (a.GroupId.HasValue) return Members(ctx, a);

            var types = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>()
                .Where(t => a.Kind == "all" || GroupOps.Kind(t) == a.Kind)
                .Where(t => string.IsNullOrWhiteSpace(a.NameContains) ||
                            (t.Name ?? string.Empty).IndexOf(a.NameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(t => GroupOps.Kind(t)).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var page = ctx.Services.Paging.Of(types, a.Limit, a.Offset, 1000);
            var items = page.Items.Select(t =>
            {
                var instances = t.Groups.Cast<Group>().ToList();
                List<string> attached = null;
                if (GroupOps.Kind(t) == "model" && instances.Count > 0)
                {
                    try
                    {
                        attached = instances[0].GetAvailableAttachedDetailGroupTypeIds()
                            .Select(id => doc.GetElement(id)?.Name).Where(n => n != null).ToList();
                    }
                    catch { attached = null; }
                }
                return new
                {
                    typeId = t.Id.GetValue(),
                    name = t.Name,
                    kind = GroupOps.Kind(t),
                    instanceCount = instances.Count,
                    attachedDetailGroupTypes = attached,
                    instances = a.IncludeInstances ? instances.Take(50).Select(GroupOps.Describe).ToList() : null
                };
            }).ToList();

            return new
            {
                items,
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated,
                instanceTotal = types.Sum(GroupOps.InstanceCount)
            };
        }

        private static object Members(ToolContext ctx, ListGroupsArgs a)
        {
            var doc = ctx.Doc;
            var group = GroupOps.Group(doc, a.GroupId.Value);
            var members = group.GetMemberIds().Select(id => doc.GetElement(id)).Where(e => e != null).ToList();
            var page = ctx.Services.Paging.Of(members, a.Limit, a.Offset, 1000);
            return new
            {
                group = GroupOps.Describe(group),
                members = page.Items.Select(e => new
                {
                    id = e.Id.GetValue(),
                    category = e.Category?.Name ?? e.GetType().Name,
                    name = M1Elements.SafeName(e),
                    type = M1Elements.TypeName(e),
                    isGroup = e is Group
                }).ToList(),
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }
    }
}
