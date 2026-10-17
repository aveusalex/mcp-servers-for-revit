using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Millwork.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Millwork
{
    public sealed class CreateMillworkModuleArgs : MillworkDesign
    {
    }

    /// <summary>ARQ-24: parametric millwork module (marcenaria) built from panels, without office families.</summary>
    [McpTool("create_millwork_module", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "architecture",
        Description = "Create a parametric millwork module (marcenaria: base/inferior, wall/aéreo or tall/torre) from " +
                      "MDF panels, each a Casework DirectShape with its own material, grouped as one Revit group: sides, " +
                      "bottom, top or rails, back, toe kick, dividers, shelves, doors (count, hinge side, swing/lift/flap, " +
                      "overlay or inset, gap) and drawers (fronts + boxes). Requires kind, width, height, depth (mm), " +
                      "level and point (front-left corner; rotation CCW). Returns the group id, part ids and a cutting " +
                      "list (piece, qty, length x width x thickness mm, material). Change it later with " +
                      "configure_millwork_module. One Ctrl+Z undoes the call.")]
    public sealed class CreateMillworkModuleTool : McpTool<CreateMillworkModuleArgs>
    {
        protected override void Validate(CreateMillworkModuleArgs a)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(a.Kind)) missing.Add("kind");
            if (a.Width == null) missing.Add("width");
            if (a.Height == null) missing.Add("height");
            if (a.Depth == null) missing.Add("depth");
            if (string.IsNullOrWhiteSpace(a.Level)) missing.Add("level");
            if (a.Point == null) missing.Add("point");
            if (missing.Count > 0)
                throw new ToolException("INVALID_ARGUMENTS", "Missing required argument(s): " + string.Join(", ", missing) + ".");
        }

        protected override object Run(ToolContext ctx, CreateMillworkModuleArgs a)
        {
            var design = MillworkLayout.Resolve(a);
            var notes = new List<string>();
            var parts = MillworkLayout.Build(design, notes);
            var level = ctx.Services.Lookup.Level(design.Level);
            design.Level = level.Name;
            var materials = MillworkRevit.ResolveMaterials(ctx, design);
            var placement = MillworkRevit.PlacementOf(design, level);
            var moduleId = Guid.NewGuid().ToString("N").Substring(0, 12);
            var (group, created) = MillworkRevit.BuildModule(ctx.Doc, moduleId, design, parts, placement, materials,
                MillworkRevit.GroupNameFor(design));
            foreach (var n in notes) ctx.Warnings.Add(new ToolWarning(n, null, "info"));
            return Report(ctx.Doc, moduleId, design, group, created);
        }

        internal static object Report(Document doc, string moduleId, ResolvedDesign d, Group group,
            List<(DirectShape shape, MillworkPart part, Material material)> created)
        {
            var label = d.Mark ?? group.GroupType?.Name;
            var rows = MillworkRevit.CuttingList(created.Select(c => (c.part, c.material?.Name, label)));
            int doors = created.Count(c => c.part.Role.StartsWith("door_", StringComparison.Ordinal));
            return new
            {
                moduleId,
                groupId = group.Id.GetValue(),
                groupName = group.GroupType?.Name,
                mark = d.Mark,
                name = d.Name,
                kind = d.Kind,
                size = new { widthMm = d.Width, heightMm = d.Height, depthMm = d.Depth },
                placement = new { level = d.Level, x = d.X, y = d.Y, elevationMm = d.Elevation, rotationDeg = d.Rotation },
                summary = new
                {
                    parts = created.Count,
                    bays = d.Dividers + 1,
                    shelvesPerBay = d.Shelves,
                    doors,
                    doorOpening = d.Doors > 0 ? d.DoorOpening : null,
                    hinges = created.Where(c => c.part.Role.StartsWith("door_", StringComparison.Ordinal))
                        .Select(c => c.part.Note?.Replace("hinge:", "")).ToList(),
                    frontStyle = d.FrontStyle,
                    drawers = d.Drawers,
                    drawerFrontHeightsMm = d.DrawerHeightsUsed,
                    topPanel = d.TopPanel,
                    toeKickMm = d.ToeKickHeight
                },
                partIds = created.Select(c => c.shape.Id.GetValue()).ToList(),
                parts = created.Select(c => MillworkRevit.PartRow(c.shape, c.part, c.material?.Name)).ToList(),
                cuttingList = rows,
                totalAreaM2 = Math.Round(rows.Sum(r => r.AreaM2), 3)
            };
        }
    }
}
