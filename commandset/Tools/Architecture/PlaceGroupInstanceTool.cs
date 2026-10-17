using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Tools.Editing.M1Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture
{
    public sealed class GroupPlacementItem
    {
        [JsonProperty("groupType")]
        [Required]
        [Description("Model group type by name or id (see list_groups). An ambiguous name fails with the candidates.")]
        public string GroupType { get; set; }

        [JsonProperty("location")]
        [Required]
        [Description("Where the group's origin goes, in mm. z is used only when level is not given.")]
        public Point3Mm Location { get; set; }

        [JsonProperty("level")]
        [Description("Level by name or id: the origin is placed at the level's elevation + offset.")]
        public string Level { get; set; }

        [JsonProperty("offset")]
        [Unit("mm")]
        [DefaultValue(0)]
        [Description("Height above the level in mm (only with level). Default 0.")]
        public double Offset { get; set; }

        [JsonProperty("rotation")]
        [Unit("deg")]
        [DefaultValue(0)]
        [Description("Rotation about the vertical axis through the origin, degrees counter-clockwise. Default 0.")]
        public double Rotation { get; set; }
    }

    public sealed class PlaceGroupInstanceArgs
    {
        [JsonProperty("items")]
        [Required]
        [MinLength(1)]
        [MaxLength(200)]
        [Description("Group instances to place.")]
        public List<GroupPlacementItem> Items { get; set; }

        [JsonProperty("continueOnError")]
        [DefaultValue(false)]
        [Description("false (default): any failure rolls back the whole call. true: failed items are skipped and listed in failed[].")]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>ARQ-20: place instances of model group types.</summary>
    [McpTool("place_group_instance", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Place instances of an existing model group type (named or by id; see list_groups) with its origin " +
                      "at a point in mm, optionally on a level + offset, rotated in degrees. Detail groups are refused " +
                      "(they belong to a view). Returns the new group ids, origin read back and member count. Undo: Ctrl+Z.")]
    public sealed class PlaceGroupInstanceTool : McpTool<PlaceGroupInstanceArgs>
    {
        protected override void Validate(PlaceGroupInstanceArgs a)
        {
            for (int i = 0; i < a.Items.Count; i++)
            {
                var it = a.Items[i] ?? throw new ToolException("INVALID_ARGUMENTS", $"items[{i}] is null.");
                if (string.IsNullOrWhiteSpace(it.GroupType)) throw new ToolException("INVALID_ARGUMENTS", $"items[{i}]: groupType is required.");
                if (it.Location == null) throw new ToolException("INVALID_ARGUMENTS", $"items[{i}]: location is required.");
            }
        }

        protected override object Run(ToolContext ctx, PlaceGroupInstanceArgs a)
        {
            var doc = ctx.Doc;
            var all = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>().ToList();
            var model = all.Where(t => GroupOps.Kind(t) == "model").ToList();

            var outcome = ItemRunner.Run(doc, a.Items, a.ContinueOnError, "Group", (item, index) =>
            {
                GroupType type;
                try
                {
                    type = ctx.Services.Lookup.ByNameOrId(item.GroupType, model, "model group type");
                }
                catch (ToolException) when (all.Any(t => GroupOps.Kind(t) != "model" &&
                                                         (string.Equals(t.Name, item.GroupType, StringComparison.OrdinalIgnoreCase) ||
                                                          t.Id.GetValue().ToString() == item.GroupType.Trim())))
                {
                    throw new ToolException("DETAIL_GROUP_NOT_SUPPORTED",
                        $"'{item.GroupType}' is a detail group type; detail groups belong to a view and are not placed by this tool.");
                }

                double z;
                Level level = null;
                if (!string.IsNullOrWhiteSpace(item.Level))
                {
                    level = ctx.Services.Lookup.Level(item.Level);
                    z = level.Elevation + ArchGeometry.Ft(item.Offset);
                }
                else z = ArchGeometry.Ft(item.Location.Z ?? 0);

                var at = new XYZ(ArchGeometry.Ft(item.Location.X), ArchGeometry.Ft(item.Location.Y), z);
                var group = doc.Create.PlaceGroup(at, type)
                            ?? throw new ToolException("CREATE_FAILED", $"Revit did not place group '{type.Name}'.");
                if (Math.Abs(item.Rotation) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, group.Id, Line.CreateBound(at, at + XYZ.BasisZ), ArchGeometry.Rad(item.Rotation));
                doc.Regenerate();
                if (!group.IsValidObject) throw new ToolException("CREATE_FAILED", "Revit removed the group right after placing it.");
                var placedLevel = doc.GetElement(group.LevelId) as Level;
                return new
                {
                    index,
                    id = group.Id.GetValue(),
                    groupType = type.Name,
                    groupTypeId = type.Id.GetValue(),
                    origin = GroupOps.Origin(group),
                    level = placedLevel?.Name ?? level?.Name,
                    rotationDeg = item.Rotation,
                    memberCount = group.GetMemberIds().Count
                };
            });

            return new
            {
                created = outcome.Created,
                failed = outcome.Failed,
                createdCount = outcome.Created.Count,
                failedCount = outcome.Failed.Count
            };
        }
    }
}
