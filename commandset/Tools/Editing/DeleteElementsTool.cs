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
    public sealed class DeleteElementsArgs
    {
        [JsonProperty("elementIds", Required = Required.Always)]
        [Required]
        [MinLength(1)]
        [MaxLength(5000)]
        [Description("Elements to delete. Every id must exist and be deletable, otherwise nothing is deleted.")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("allowPinned")]
        [DefaultValue(false)]
        [Description("Also delete pinned elements. Default false: pinned elements make the whole call fail.")]
        public bool AllowPinned { get; set; }

        [JsonProperty("listLimit")]
        [System.ComponentModel.DataAnnotations.Range(0, 2000)]
        [DefaultValue(200)]
        [Description("Maximum deleted elements listed one by one, 0-2000 (default 200). Counts are always complete.")]
        public int ListLimit { get; set; } = 200;
    }

    /// <summary>EDT-05: delete elements with an all-or-nothing pre-check and a preview of the dependents.</summary>
    [McpTool("delete_elements", Kind = ToolKind.Write, Risk = Risk.Medium, DryRunDefault = true, Toolset = "editing",
        Description = "Delete elements by id, all or nothing: missing, pinned (unless allowPinned) or undeletable ids " +
                      "make the call fail before anything is deleted. Revit also deletes dependents (tags, dimensions, " +
                      "hosted doors/windows, views of a level...); the result lists them by category. Preview by default: " +
                      "without dryRun:false it only reports what would be deleted. Undo: Ctrl+Z (one transaction).")]
    public sealed class DeleteElementsTool : McpTool<DeleteElementsArgs>
    {
        protected override object Run(ToolContext ctx, DeleteElementsArgs a)
        {
            var doc = ctx.Doc;
            var elements = EditGeometry.Elements(doc, a.ElementIds);
            if (!a.AllowPinned) M1Elements.RefusePinned(elements, "deleted");

            var activeView = M1Elements.ActiveViewId(ctx);
            if (activeView != null && elements.Any(e => e.Id == activeView))
                throw new ToolException("CANNOT_DELETE", $"Element {activeView.GetValue()} is the active view, which Revit can't delete.",
                    "Ask the user to switch to another view first.");

            var refused = elements.Where(e => !DocumentValidation.CanDeleteElement(doc, e.Id)).ToList();
            if (refused.Count > 0)
                throw new ToolException("CANNOT_DELETE",
                    $"Revit can't delete {refused.Count} of the elements, so nothing was deleted: " +
                    string.Join("; ", refused.Take(20).Select(e => $"{e.Id.GetValue()} ({e.Category?.Name ?? e.GetType().Name})")) + ".",
                    "Remove them from elementIds (e.g. the last level/phase, the active view, internal elements).");

            var ids = elements.Select(e => e.Id).ToList();

            // Preview the cascade inside a sub-transaction so the dependents can still be described.
            ICollection<ElementId> wouldDelete;
            using (var sub = new SubTransaction(doc))
            {
                sub.Start();
                try
                {
                    wouldDelete = doc.Delete(ids);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                    throw new ToolException("CANNOT_DELETE", $"Revit refused to delete the elements: {ex.Message} Nothing was deleted.");
                }
                sub.RollBack();
            }

            var requested = new HashSet<long>(ids.Select(i => i.GetValue()));
            var described = wouldDelete.Select(i => doc.GetElement(i)).Where(e => e != null).ToList();
            var dependents = described.Where(e => !requested.Contains(e.Id.GetValue())).ToList();
            var byCategory = dependents.GroupBy(e => e.Category?.Name ?? e.GetType().Name)
                .OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count());
            var views = described.Where(e => e is View).Select(e => e.Id.GetValue()).ToList();
            if (views.Count > 0)
                ctx.Warnings.Add(new ToolWarning($"{views.Count} view(s)/sheet(s) are deleted with these elements.", views.Take(50).ToList()));

            // Describe before deleting: afterwards the elements are gone.
            var dependentList = dependents.Take(a.ListLimit).Select(M1Elements.Describe).ToList();
            var requestedList = elements.Take(a.ListLimit).Select(M1Elements.Describe).ToList();

            var deleted = doc.Delete(ids);
            var deletedIds = M1Elements.Ids(deleted);
            int dependentsCount = deletedIds.Count(i => !requested.Contains(i));

            return new
            {
                requestedCount = requested.Count,
                deletedCount = deletedIds.Count,
                dependentsCount,
                dependentsByCategory = byCategory,
                requested = requestedList,
                dependents = dependentList,
                deletedIds = deletedIds.Take(5000).ToList(),
                listTruncated = dependents.Count > a.ListLimit || requested.Count > a.ListLimit,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] Would delete " : "Deleted ") +
                                $"{deletedIds.Count} element(s): {requested.Count} requested + {dependentsCount} dependent(s)." +
                                (ctx.DryRun ? " Call again with dryRun:false to delete." : string.Empty)
            };
        }
    }
}
