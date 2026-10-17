using System.Collections.Generic;
using System.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    /// <summary>EDT-04: pin or unpin elements. Small write used to validate transactions, dryRun and batches.</summary>
    [McpTool("set_elements_pinned", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "editing", Idempotent = true,
        Description = "Pins or unpins elements by id in the target document. Elements already in the requested state " +
                      "are reported as unchanged; ids that don't exist are reported as notFound. Undo with Ctrl+Z " +
                      "or by calling it again with the opposite value. Supports dryRun.")]
    public sealed class SetElementsPinnedTool : McpTool<SetElementsPinnedArgs>
    {
        protected override object Run(ToolContext ctx, SetElementsPinnedArgs args)
        {
            bool pinned = args.Pinned ?? true;
            var changed = new List<long>();
            var unchanged = new List<long>();
            var notFound = new List<long>();
            var failed = new List<object>();

            foreach (var id in args.ElementIds.Distinct())
            {
                var element = ctx.Doc.GetElement(id.ToRevitElementId());
                if (element == null)
                {
                    notFound.Add(id);
                    continue;
                }
                if (element.Pinned == pinned)
                {
                    unchanged.Add(id);
                    continue;
                }

                try
                {
                    element.Pinned = pinned;
                    if (element.Pinned == pinned) changed.Add(id);
                    else failed.Add(new { id, reason = "Revit ignored the change for this element." });
                }
                catch (System.Exception ex)
                {
                    failed.Add(new { id, reason = ex.Message });
                }
            }

            if (changed.Count == 0 && unchanged.Count == 0)
            {
                throw new ToolException("NOT_FOUND",
                    $"None of the {args.ElementIds.Count} element id(s) could be {(pinned ? "pinned" : "unpinned")}.",
                    "Check the ids with a query tool against the same document.");
            }

            foreach (var id in notFound)
            {
                ctx.Warnings.Add(new ToolWarning($"Element {id} was not found.", new List<long> { id }));
            }

            return new
            {
                pinned,
                requested = args.ElementIds.Count,
                changed,
                unchanged,
                notFound,
                failed
            };
        }
    }
}
