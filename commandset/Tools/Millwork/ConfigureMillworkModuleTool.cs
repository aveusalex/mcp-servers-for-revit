using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Millwork.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Millwork
{
    public sealed class ConfigureMillworkModuleArgs
    {
        [JsonProperty("module")]
        [Required]
        [Description("The module: its group id, the id of any of its parts, or its mark (e.g. \"M01\").")]
        public string Module { get; set; }

        [JsonProperty("changes")]
        [Required]
        [Description("Only what changes; everything else is kept (including where the group is now, if it was moved or rotated). Same fields as create_millwork_module. Pass \"-\" to clear a material, mark or name.")]
        public MillworkDesign Changes { get; set; }
    }

    /// <summary>ARQ-25: rebuild a module created by create_millwork_module with changed parameters.</summary>
    [McpTool("configure_millwork_module", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Change a millwork module created by create_millwork_module (adjust size, doors, hinge side, " +
                      "drawers, shelves, dividers, front style, thickness, toe kick, materials, mark, position...): the " +
                      "module's stored parameters are merged with the changes and the module is rebuilt in place (its " +
                      "current position and rotation are kept if the group was moved), replacing the old parts and " +
                      "group. Returns the new group and part ids and the cutting list. One Ctrl+Z undoes the call.")]
    public sealed class ConfigureMillworkModuleTool : McpTool<ConfigureMillworkModuleArgs>
    {
        protected override object Run(ToolContext ctx, ConfigureMillworkModuleArgs a)
        {
            var doc = ctx.Doc;
            var module = MillworkRevit.Find(ctx, a.Module);
            var before = module.Design;
            var baseline = JsonConvert.DeserializeObject<ResolvedDesign>(JsonConvert.SerializeObject(before));

            // Keep the module where it is now: the group may have been moved or rotated since it was built.
            Level level = null;
            try { level = ctx.Services.Lookup.Level(baseline.Level); }
            catch (ToolException) { /* level renamed or deleted: resolved below */ }
            if (level == null && string.IsNullOrWhiteSpace(a.Changes.Level))
                throw new ToolException("NOT_FOUND", $"The module's level '{baseline.Level}' no longer exists.", "Pass changes.level.");
            if (level != null)
            {
                var now = MillworkRevit.CurrentPlacement(module, level, out var residual);
                if (now != null)
                {
                    baseline.X = Math.Round(now.Origin.X * 304.8, 1);
                    baseline.Y = Math.Round(now.Origin.Y * 304.8, 1);
                    baseline.Elevation = Math.Round((now.Origin.Z - level.Elevation) * 304.8, 1);
                    baseline.Rotation = MillworkRevit.NormalizeDeg(now.Angle);
                    if (residual > 2)
                        ctx.Warnings.Add(new ToolWarning($"The module's parts no longer match its stored layout (off by up to {Math.Round(residual)} mm: mirrored or edited?). It was rebuilt from the stored layout at the fitted position."));
                }
            }

            var design = MillworkLayout.Resolve(a.Changes, baseline);
            // An elevation relative to a different level: keep the absolute height unless elevation was given.
            var newLevel = ctx.Services.Lookup.Level(design.Level);
            if (level != null && newLevel.Id != level.Id && a.Changes.Elevation == null)
                design.Elevation = Math.Round(baseline.Elevation + (level.Elevation - newLevel.Elevation) * 304.8, 1);
            design.Level = newLevel.Name;
            var notes = new List<string>();
            var parts = MillworkLayout.Build(design, notes);
            var materials = MillworkRevit.ResolveMaterials(ctx, design);

            // Remove the old module: ungroup, delete its parts, and its group type when no other copy uses it.
            var oldIds = module.Parts.Select(p => p.Shape.Id).ToList();
            var oldGroupId = module.Group?.Id.GetValue();
            GroupType oldType = module.Group?.GroupType;
            string keepName = oldType?.Name;
            // Keep the id: after UngroupMembers the GroupType wrapper can be invalid while the type is still in the model.
            var oldTypeId = oldType?.Id;
            if (module.Group != null)
            {
                if (module.Group.Pinned)
                    throw new ToolException("ELEMENT_PINNED", "The module's group is pinned.", "Unpin it first (set_elements_pinned).", "set_elements_pinned");
                var members = module.Group.UngroupMembers();
                int others = members.Count(id => !oldIds.Contains(id));
                if (others > 0)
                    ctx.Warnings.Add(new ToolWarning($"{others} element(s) that were in the module's group but are not millwork parts were kept, ungrouped.", null, "info"));
            }
            doc.Delete(oldIds);
            bool typeReused = false;
            if (oldTypeId != null && doc.GetElement(oldTypeId) is GroupType)
            {
                // GroupType.Groups is stale until a regeneration, and the ungrouped group itself is only removed at commit
                // (live test): count the other instances directly, ignoring this module's own group.
                doc.Regenerate();
                bool stillUsed = new FilteredElementCollector(doc).OfClass(typeof(Group))
                    .Cast<Group>().Any(g => g.GroupType?.Id == oldTypeId && g.Id.GetValue() != oldGroupId);
                if (!stillUsed) doc.Delete(oldTypeId);
                else typeReused = true;
            }

            var placement = MillworkRevit.PlacementOf(design, newLevel);
            string groupName = !typeReused && keepName != null && string.Equals(before.Mark, design.Mark, StringComparison.Ordinal) &&
                               SameSize(before, design)
                ? keepName
                : MillworkRevit.GroupNameFor(design);
            var (group, created) = MillworkRevit.BuildModule(doc, module.ModuleId, design, parts, placement, materials, groupName);

            // Second chance for the old type (live test: it survived the delete above): once the new group exists,
            // remove any model group type with the old name that no group uses any more.
            if (!typeReused && keepName != null && !string.Equals(keepName, group?.GroupType?.Name, StringComparison.Ordinal))
            {
                doc.Regenerate();
                var usedTypeIds = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>()
                    .Where(g => g.GroupType != null && g.Id.GetValue() != oldGroupId).Select(g => g.GroupType.Id.GetValue()));
                var orphans = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>()
                    .Where(t => t.Name == keepName && !usedTypeIds.Contains(t.Id.GetValue())).Select(t => t.Id).ToList();
                foreach (var id in orphans)
                {
                    try { doc.Delete(id); }
                    catch (Exception ex)
                    {
                        ctx.Warnings.Add(new ToolWarning($"The old group type '{keepName}' could not be removed: {ex.Message}", null, "info"));
                    }
                }
            }
            foreach (var n in notes) ctx.Warnings.Add(new ToolWarning(n, null, "info"));
            if (typeReused)
                ctx.Warnings.Add(new ToolWarning("Other copies of the old group still exist; only this one was rebuilt (the others keep the old layout).", null, "info"));

            return new
            {
                previous = new { groupId = oldGroupId, deletedParts = oldIds.Count },
                changed = Diff(before, design),
                module = CreateMillworkModuleTool.Report(doc, module.ModuleId, design, group, created)
            };
        }

        private static bool SameSize(ResolvedDesign a, ResolvedDesign b) =>
            a.Kind == b.Kind && Math.Abs(a.Width - b.Width) < 0.01 && Math.Abs(a.Height - b.Height) < 0.01 && Math.Abs(a.Depth - b.Depth) < 0.01;

        private static List<object> Diff(ResolvedDesign a, ResolvedDesign b)
        {
            var ja = Newtonsoft.Json.Linq.JObject.FromObject(a);
            var jb = Newtonsoft.Json.Linq.JObject.FromObject(b);
            var list = new List<object>();
            foreach (var p in jb.Properties())
            {
                if (p.Name == "drawerHeightsUsed") continue;
                var old = ja[p.Name];
                if (!Newtonsoft.Json.Linq.JToken.DeepEquals(old, p.Value))
                    list.Add(new { field = p.Name, from = old, to = p.Value });
            }
            return list;
        }
    }
}
