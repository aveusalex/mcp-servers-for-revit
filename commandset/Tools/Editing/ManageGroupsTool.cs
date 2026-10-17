using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Editing.M1Support;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class ManageGroupsArgs
    {
        [JsonProperty("operation", Required = Required.Always)]
        [Required]
        [EnumValues("create", "ungroup", "addMembers", "removeMembers")]
        [Description("create (new group from elementIds), ungroup (groupId, or several group ids in elementIds), addMembers / removeMembers (groupId + elementIds).")]
        public string Operation { get; set; }

        [JsonProperty("elementIds")]
        [MaxLength(5000)]
        [Description("create: elements to group. addMembers/removeMembers: elements to add/remove. ungroup: group instance ids (alternative to groupId).")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("groupId")]
        [Description("The group instance for ungroup / addMembers / removeMembers.")]
        public long? GroupId { get; set; }

        [JsonProperty("name")]
        [Description("create / addMembers / removeMembers: name of the (new) group type. Default: Revit's name for create, the old name otherwise.")]
        public string Name { get; set; }

        [JsonProperty("detach")]
        [DefaultValue(false)]
        [Description("addMembers/removeMembers when the group type has other instances: true turns THIS instance into a new group type (the others keep the old one). Default false refuses, because the Revit API can't edit a group type's members in place.")]
        public bool Detach { get; set; }
    }

    /// <summary>EDT-08: create, ungroup, add/remove members (by ungroup + regroup, honestly limited).</summary>
    [McpTool("manage_groups", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "editing",
        Description = "Create a group from elements, ungroup instances, or add/remove members of a group instance. " +
                      "The Revit API has no in-place Edit Group: add/remove ungroups the instance and regroups it, keeping " +
                      "the type name only when it is the type's single instance; with other instances it refuses unless " +
                      "detach:true (this instance becomes a new type). Model and view-specific elements can't be mixed. " +
                      "Returns the group id and member ids. Undo: Ctrl+Z.")]
    public sealed class ManageGroupsTool : McpTool<ManageGroupsArgs>
    {
        protected override void Validate(ManageGroupsArgs a)
        {
            var hasIds = a.ElementIds != null && a.ElementIds.Count > 0;
            switch (a.Operation)
            {
                case "create":
                    if (!hasIds) throw new ToolException("INVALID_ARGUMENTS", "create needs elementIds.");
                    break;
                case "ungroup":
                    if (!a.GroupId.HasValue && !hasIds) throw new ToolException("INVALID_ARGUMENTS", "ungroup needs groupId (or group ids in elementIds).");
                    break;
                default:
                    if (!a.GroupId.HasValue || !hasIds)
                        throw new ToolException("INVALID_ARGUMENTS", $"{a.Operation} needs groupId and elementIds.");
                    break;
            }
            if (!string.IsNullOrEmpty(a.Name) && a.Operation == "ungroup")
                throw new ToolException("INVALID_ARGUMENTS", "name is not used by ungroup.");
        }

        protected override object Run(ToolContext ctx, ManageGroupsArgs a)
        {
            try
            {
                switch (a.Operation)
                {
                    case "create": return Create(ctx, a);
                    case "ungroup": return Ungroup(ctx, a);
                    default: return EditMembers(ctx, a);
                }
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("GROUP_FAILED", $"Revit refused the {a.Operation}: {ex.Message}");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                throw new ToolException("GROUP_FAILED", $"Revit refused the {a.Operation}: {ex.Message}");
            }
        }

        private static object Create(ToolContext ctx, ManageGroupsArgs a)
        {
            var doc = ctx.Doc;
            var elements = EditGeometry.Elements(doc, a.ElementIds);
            GroupOps.CheckGroupable(elements);
            if (!string.IsNullOrWhiteSpace(a.Name)) ctx.Services.Names.EnsureUniqueInClass(typeof(GroupType), a.Name, "group name");

            var group = doc.Create.NewGroup(elements.Select(e => e.Id).ToList())
                        ?? throw new ToolException("GROUP_FAILED", "Revit did not create the group.");
            if (!string.IsNullOrWhiteSpace(a.Name)) ctx.Services.Names.ApplyName(group.GroupType, a.Name, "group name");
            return Report(ctx, group, $"Created group '{group.GroupType.Name}' with {group.GetMemberIds().Count} member(s).");
        }

        private static object Ungroup(ToolContext ctx, ManageGroupsArgs a)
        {
            var doc = ctx.Doc;
            var groupIds = new List<long>();
            if (a.GroupId.HasValue) groupIds.Add(a.GroupId.Value);
            if (a.ElementIds != null) groupIds.AddRange(a.ElementIds);
            var groups = groupIds.Distinct().Select(id => GroupOps.Group(doc, id)).ToList();
            M1Elements.RefusePinned(groups, "ungrouped");

            var results = new List<object>();
            int members = 0;
            foreach (var g in groups)
            {
                var typeName = g.GroupType?.Name;
                var id = g.Id.GetValue();
                var released = M1Elements.Ids(g.UngroupMembers());
                members += released.Count;
                results.Add(new { groupId = id, groupType = typeName, memberIds = released.Take(2000).ToList(), memberCount = released.Count });
            }
            return new
            {
                operation = "ungroup",
                groups = results,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"Ungrouped {groups.Count} group(s) into {members} element(s). The group types stay in the project (purge_unused removes unused ones)."
            };
        }

        private static object EditMembers(ToolContext ctx, ManageGroupsArgs a)
        {
            var doc = ctx.Doc;
            var group = GroupOps.Group(doc, a.GroupId.Value);
            if (group.GroupId != null && group.GroupId != ElementId.InvalidElementId)
                throw new ToolException("NESTED_GROUP", $"Group {a.GroupId} is nested inside group {group.GroupId.GetValue()}; edit the outer group.");
            M1Elements.RefusePinned(new[] { group }, "edited");
            var type = group.GroupType;
            var oldName = type.Name;
            int others = GroupOps.InstanceCount(type) - 1;
            if (others > 0 && !a.Detach)
                throw new ToolException("GROUP_HAS_OTHER_INSTANCES",
                    $"Group type '{oldName}' has {others} other instance(s). The Revit API can't edit a group type's members in place, so changing this one would split it from the others.",
                    "Pass detach:true to make this instance a new group type, or edit the group in Revit (Edit Group) so all instances change.");

            var members = group.GetMemberIds().Select(i => i.GetValue()).ToList();
            var memberSet = new HashSet<long>(members);
            var targets = EditGeometry.Elements(doc, a.ElementIds);
            HashSet<long> finalIds;
            if (a.Operation == "addMembers")
            {
                var already = targets.Where(e => memberSet.Contains(e.Id.GetValue())).Select(e => e.Id.GetValue()).ToList();
                if (already.Count > 0)
                    throw new ToolException("ALREADY_MEMBER", $"Already in the group: {string.Join(", ", already.Take(20))}.");
                GroupOps.CheckGroupable(targets);
                bool groupIsDetail = GroupOps.Kind(type) != "model";
                if (targets.Any(e => M1Elements.IsViewSpecific(e) != groupIsDetail))
                    throw new ToolException("MIXED_GROUP_ELEMENTS",
                        groupIsDetail ? "A detail group can only take view-specific elements." : "A model group can only take model elements.");
                finalIds = new HashSet<long>(members.Concat(targets.Select(e => e.Id.GetValue())));
            }
            else
            {
                var notMembers = targets.Where(e => !memberSet.Contains(e.Id.GetValue())).Select(e => e.Id.GetValue()).ToList();
                if (notMembers.Count > 0)
                    throw new ToolException("NOT_A_MEMBER", $"Not direct members of group {a.GroupId}: {string.Join(", ", notMembers.Take(20))}.",
                        "Check the members with list_groups (groupId).", "list_groups");
                finalIds = new HashSet<long>(members.Where(m => !targets.Any(t => t.Id.GetValue() == m)));
                if (finalIds.Count == 0)
                    throw new ToolException("GROUP_WOULD_BE_EMPTY", "Removing these would leave the group empty.", "Ungroup it (operation ungroup) or delete it instead.");
            }

            var newName = string.IsNullOrWhiteSpace(a.Name) ? null : a.Name.Trim();
            if (newName != null && !string.Equals(newName, oldName, System.StringComparison.Ordinal))
                ctx.Services.Names.EnsureUniqueInClass(typeof(GroupType), newName, "group name");
            bool hasAttached = false;
            try { hasAttached = group.GetAvailableAttachedDetailGroupTypeIds().Count > 0; }
            catch { /* detail groups have none */ }
            if ((others > 0 || hasAttached) && string.Equals(newName, oldName, System.StringComparison.Ordinal))
                throw new ToolException("NAME_IN_USE", $"'{oldName}' stays in use by the old group type; give the new type another name.");

            group.UngroupMembers();
            var ids = finalIds.Select(i => i.ToRevitElementId()).Where(i => doc.GetElement(i) != null).ToList();
            var regrouped = doc.Create.NewGroup(ids) ?? throw new ToolException("GROUP_FAILED", "Revit did not recreate the group.");

            var warnings = new List<string>();
            if (others == 0 && !hasAttached)
            {
                // The old type has no instance left: drop it and give its name to the new one.
                if (type.IsValidObject) doc.Delete(type.Id);
                ctx.Services.Names.ApplyName(regrouped.GroupType, newName ?? oldName, "group name");
            }
            else
            {
                if (newName != null) ctx.Services.Names.ApplyName(regrouped.GroupType, newName, "group name");
                if (hasAttached)
                    warnings.Add($"'{oldName}' has attached detail groups, so the old type was kept and the regrouped instance is a new type without them.");
                if (others > 0)
                    warnings.Add($"Detached: the other {others} instance(s) of '{oldName}' keep the old members.");
            }
            warnings.Add("The group origin is recomputed by Revit; group type parameters of the old type are not carried over.");
            foreach (var w in warnings) ctx.Warnings.Add(new ToolWarning(w, new List<long> { regrouped.Id.GetValue() }));

            return Report(ctx, regrouped,
                $"{(a.Operation == "addMembers" ? "Added" : "Removed")} {targets.Count} member(s); group '{regrouped.GroupType.Name}' now has {regrouped.GetMemberIds().Count} member(s) (new instance id {regrouped.Id.GetValue()}).");
        }

        private static object Report(ToolContext ctx, Group g, string summary)
        {
            var members = M1Elements.Ids(g.GetMemberIds());
            return new
            {
                groupId = g.Id.GetValue(),
                groupTypeId = g.GroupType.Id.GetValue(),
                name = g.GroupType.Name,
                kind = GroupOps.Kind(g.GroupType),
                memberIds = members.Take(2000).ToList(),
                memberCount = members.Count,
                origin = GroupOps.Origin(g),
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + summary
            };
        }
    }
}
