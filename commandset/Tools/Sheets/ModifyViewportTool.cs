using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Sheets.Support;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Sheets
{
    public sealed class ModifyViewportItem
    {
        [JsonProperty("viewport", Required = Required.Always)]
        [Description("Viewport id, or the name/id of a view placed on exactly one sheet.")]
        public string Viewport { get; set; }

        [JsonProperty("x")]
        [Unit("mm")]
        [Description("New X of the viewport box centre on the sheet, paper mm (give x and y together).")]
        public double? X { get; set; }

        [JsonProperty("y")]
        [Unit("mm")]
        [Description("New Y of the viewport box centre on the sheet, paper mm.")]
        public double? Y { get; set; }

        [JsonProperty("dx")]
        [Unit("mm")]
        [Description("Move by this much along X, paper mm (instead of x/y).")]
        public double? Dx { get; set; }

        [JsonProperty("dy")]
        [Unit("mm")]
        [Description("Move by this much along Y, paper mm (instead of x/y).")]
        public double? Dy { get; set; }

        [JsonProperty("viewportType")]
        [Description("Viewport type name or id (e.g. a type without title, or \"Título com linha\").")]
        public string ViewportType { get; set; }

        [JsonProperty("rotation")]
        [EnumValues("none", "clockwise", "counterclockwise")]
        [Description("Rotation of the view on the sheet: none, clockwise or counterclockwise (90°).")]
        public string Rotation { get; set; }

        [JsonProperty("titleOnSheet")]
        [Description("Title on sheet (\"Título na folha\") of the placed view; \"\" clears it so the view name is shown.")]
        public string TitleOnSheet { get; set; }

        [JsonProperty("labelOffsetX")]
        [Unit("mm")]
        [Description("Title label offset from the viewport box's lower-left corner, X in paper mm (Revit 2022+).")]
        public double? LabelOffsetX { get; set; }

        [JsonProperty("labelOffsetY")]
        [Unit("mm")]
        [Description("Title label offset from the viewport box's lower-left corner, Y in paper mm.")]
        public double? LabelOffsetY { get; set; }

        [JsonProperty("labelLineLength")]
        [Unit("mm")]
        [Range(0, 2000)]
        [Description("Length of the title's extension line, paper mm.")]
        public double? LabelLineLength { get; set; }
    }

    public sealed class ModifyViewportArgs
    {
        [JsonProperty("items", Required = Required.Always)]
        [MinLength(1)]
        [MaxLength(200)]
        [Description("Viewports to change, up to 200.")]
        public List<ModifyViewportItem> Items { get; set; }

        [JsonProperty("continueOnError")]
        [DefaultValue(false)]
        [Description("false (default): any failure rolls back every change. true: failed ones are listed in failed[].")]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>FOL-07: move a viewport, change its type, rotation, title on sheet and label position.</summary>
    [McpTool("modify_viewport", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "sheets",
        Description = "Change viewports on sheets: move (x/y centre or dx/dy, paper mm), viewport type, rotation on " +
                      "the sheet (none/clockwise/counterclockwise), title on sheet (Título na folha), title label offset " +
                      "and extension-line length (paper mm). Each change is read back; the result lists before/after. " +
                      "Undo: Ctrl+Z.")]
    public sealed class ModifyViewportTool : McpTool<ModifyViewportArgs>
    {
        protected override void Validate(ModifyViewportArgs args)
        {
            foreach (var i in args.Items)
            {
                if (i == null) throw new ToolException("INVALID_ARGUMENTS", "items must not contain null.");
                if ((i.X == null) != (i.Y == null))
                    throw new ToolException("INVALID_ARGUMENTS", $"'{i.Viewport}': give x and y together.");
                if (i.X != null && (i.Dx != null || i.Dy != null))
                    throw new ToolException("INVALID_ARGUMENTS", $"'{i.Viewport}': use x/y or dx/dy, not both.");
                if ((i.LabelOffsetX == null) != (i.LabelOffsetY == null))
                    throw new ToolException("INVALID_ARGUMENTS", $"'{i.Viewport}': give labelOffsetX and labelOffsetY together.");
                i.Rotation = i.Rotation?.Trim().ToLowerInvariant();
                bool any = i.X != null || i.Dx != null || i.Dy != null || i.ViewportType != null || i.Rotation != null ||
                           i.TitleOnSheet != null || i.LabelOffsetX != null || i.LabelLineLength != null;
                if (!any) throw new ToolException("INVALID_ARGUMENTS", $"'{i.Viewport}': nothing to change.");
            }
        }

        protected override object Run(ToolContext ctx, ModifyViewportArgs args)
        {
            var doc = ctx.Doc;
            var r = ItemLoop.Run(ctx, args.Items, args.ContinueOnError, (item, index) =>
            {
                var vp = SheetSupport.Viewport(ctx, item.Viewport);
                var view = doc.GetElement(vp.ViewId) as View;
                var before = Snapshot(doc, vp, view);
                var changed = new List<string>();

                if (!string.IsNullOrWhiteSpace(item.ViewportType))
                {
                    var types = vp.GetValidTypes().Select(id => doc.GetElement(id) as ElementType).Where(t => t != null).ToList();
                    var type = ctx.Services.Lookup.ByNameOrId(item.ViewportType, types, "viewport type");
                    if (vp.GetTypeId() != type.Id)
                    {
                        vp.ChangeTypeId(type.Id);
                        if (vp.GetTypeId() != type.Id)
                            throw new ToolException("VIEWPORT_TYPE_NOT_APPLIED", $"Revit kept the viewport type of '{view.Name}'.");
                        changed.Add("viewportType");
                    }
                }

                if (item.Rotation != null)
                {
                    var rot = item.Rotation == "clockwise" ? ViewportRotation.Clockwise
                        : item.Rotation == "counterclockwise" ? ViewportRotation.Counterclockwise : ViewportRotation.None;
                    if (vp.Rotation != rot)
                    {
                        var centre = vp.GetBoxCenter();
                        vp.Rotation = rot;
                        doc.Regenerate();
                        vp.SetBoxCenter(centre); // rotation turns about a corner in some versions: keep the centre
                        if (vp.Rotation != rot)
                            throw new ToolException("VIEWPORT_NOT_ROTATED", $"Revit did not rotate the viewport of '{view.Name}'.");
                        changed.Add("rotation");
                    }
                }

                if (item.TitleOnSheet != null)
                {
                    var p = view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION);
                    if (p == null || p.IsReadOnly)
                        throw new ToolException("PARAMETER_READ_ONLY", $"'{view.Name}' has no editable Title on Sheet (template-controlled?).");
                    p.Set(item.TitleOnSheet);
                    if ((p.AsString() ?? "") != item.TitleOnSheet)
                        throw new ToolException("PARAMETER_NOT_APPLIED", $"Revit kept Title on Sheet '{p.AsString()}'.");
                    changed.Add("titleOnSheet");
                }

                if (item.X != null || item.Dx != null || item.Dy != null)
                {
                    if (vp.Pinned)
                        throw new ToolException("VIEWPORT_PINNED", $"Viewport of '{view.Name}' is pinned.", "Unpin it first (set_elements_pinned).", "set_elements_pinned");
                    var c = vp.GetBoxCenter();
                    var target = item.X != null
                        ? new XYZ(Mm.ToFt(item.X.Value), Mm.ToFt(item.Y.Value), c.Z)
                        : new XYZ(c.X + Mm.ToFt(item.Dx ?? 0), c.Y + Mm.ToFt(item.Dy ?? 0), c.Z);
                    vp.SetBoxCenter(target);
                    if (vp.GetBoxCenter().DistanceTo(target) > 1e-4)
                        throw new ToolException("VIEWPORT_NOT_MOVED", $"Revit did not move the viewport of '{view.Name}' as requested.");
                    changed.Add("position");
                }

                if (item.LabelOffsetX != null)
                {
                    vp.LabelOffset = new XYZ(Mm.ToFt(item.LabelOffsetX.Value), Mm.ToFt(item.LabelOffsetY.Value), 0);
                    changed.Add("labelOffset");
                }
                if (item.LabelLineLength != null)
                {
                    vp.LabelLineLength = Mm.ToFt(item.LabelLineLength.Value);
                    changed.Add("labelLineLength");
                }
                doc.Regenerate();
                return new { index, viewportId = vp.Id.GetValue(), changed, before, after = Snapshot(doc, vp, view) };
            }, i => i.Viewport, "Viewport");
            return new { modified = r.Done, failed = r.Failed, modifiedCount = r.Done.Count, failedCount = r.Failed.Count, dryRun = ctx.DryRun };
        }

        private static object Snapshot(Document doc, Viewport vp, View view)
        {
            object label = null;
            try { label = new { offset = Mm.Point2(vp.LabelOffset), lineLength = Mm.FromFt(vp.LabelLineLength) }; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { /* legends/schedules without label */ }
            return new
            {
                view = view?.Name,
                sheet = (doc.GetElement(vp.SheetId) as ViewSheet)?.SheetNumber,
                viewportType = doc.GetElement(vp.GetTypeId())?.Name,
                rotation = vp.Rotation.ToString().ToLowerInvariant(),
                titleOnSheet = view?.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString(),
                center = Mm.Point2(vp.GetBoxCenter()),
                box = SheetSupport.Rect(vp.GetBoxOutline()),
                label
            };
        }
    }
}
