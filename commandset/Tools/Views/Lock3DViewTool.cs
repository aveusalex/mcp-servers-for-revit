using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Views
{
    public sealed class Lock3DViewArgs
    {
        [JsonProperty("view", Required = Required.Always)]
        [Required]
        [Description("3D view by name or id (never the active view implicitly).")]
        public string View { get; set; }

        [JsonProperty("unlock")]
        [DefaultValue(false)]
        [Description("false (default): save the orientation and lock the view. true: unlock it (its tags and dimensions are hidden until it is locked again).")]
        public bool Unlock { get; set; }
    }

    /// <summary>VIS-27: Save Orientation and Lock View, the precondition for tags and dimensions in a 3D view.</summary>
    [McpTool("lock_3d_view", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views",
        Description = "Save the orientation of a 3D view and lock it (Salvar orientação e travar vista), which Revit " +
                      "requires before tags (tag_elements, including material tags) or annotations can be placed in 3D, " +
                      "e.g. the 3D view of a millwork assembly. unlock:true unlocks it. Already locked/unlocked views " +
                      "are reported, not changed. Undo: Ctrl+Z.")]
    public sealed class Lock3DViewTool : McpTool<Lock3DViewArgs>
    {
        protected override object Run(ToolContext ctx, Lock3DViewArgs a)
        {
            var view = ctx.Services.Lookup.View(a.View) as View3D;
            if (view == null)
                throw new ToolException("VIEW_NOT_SUPPORTED", $"'{a.View}' is not a 3D view.", "Pass a 3D view (list_views).", "list_views");
            if (view.IsTemplate)
                throw new ToolException("VIEW_NOT_SUPPORTED", $"'{view.Name}' is a view template.");

            bool was = view.IsLocked;
            bool changed = false;
            if (a.Unlock)
            {
                if (was) { view.Unlock(); changed = true; }
            }
            else if (!was)
            {
                if (!view.CanBeLocked())
                    throw new ToolException("VIEW_CANNOT_LOCK", $"Revit can't lock '{view.Name}'.",
                        "Use an orthographic 3D view that is not a template or a camera Revit refuses to lock.");
                view.SaveOrientationAndLock();
                changed = true;
            }

            return new
            {
                view = new { id = view.Id.GetValue(), name = view.Name, perspective = view.IsPerspective },
                locked = view.IsLocked,
                changed,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                (!changed ? $"'{view.Name}' was already {(was ? "locked" : "unlocked")}."
                                    : $"'{view.Name}' {(a.Unlock ? "unlocked" : "orientation saved and locked")}.")
            };
        }
    }
}
