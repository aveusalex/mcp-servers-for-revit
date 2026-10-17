using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;

namespace RevitMCPCommandSet.Tools.Views.Support
{
    /// <summary>Result of <see cref="ItemLoop.Run{T}"/>: per-item successes and failures.</summary>
    internal sealed class ItemLoopResult
    {
        public List<object> Done { get; } = new List<object>();
        public List<object> Failed { get; } = new List<object>();
    }

    /// <summary>
    /// The batch pattern of the views/sheets tools (same as create_sheets): all or nothing by default; with
    /// continueOnError each item runs in its own SubTransaction so a failed item leaves nothing behind.
    /// Must run inside the tool's transaction (Kind = Write).
    /// </summary>
    internal static class ItemLoop
    {
        public static ItemLoopResult Run<T>(ToolContext ctx, IList<T> items, bool continueOnError,
            Func<T, int, object> run, Func<T, string> label, string noun = "Item")
        {
            var result = new ItemLoopResult();
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (!continueOnError)
                {
                    try
                    {
                        result.Done.Add(run(item, index));
                    }
                    catch (ToolException ex)
                    {
                        throw new ToolException(ex.Code, $"{noun} [{index}] '{label(item)}': {ex.Message} Nothing was changed.",
                            ex.Hint ?? "Fix this item, or pass continueOnError:true to process the others.", ex.RecommendedTool);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        throw new ToolException("REVIT_ERROR", $"{noun} [{index}] '{label(item)}': Revit refused: {ex.Message} Nothing was changed.",
                            "Fix this item, or pass continueOnError:true to process the others.");
                    }
                    continue;
                }

                using (var sub = new SubTransaction(ctx.Doc))
                {
                    sub.Start();
                    try
                    {
                        result.Done.Add(run(item, index));
                        sub.Commit();
                    }
                    catch (Exception ex) when (ex is ToolException || ex is Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        sub.RollBack();
                        result.Failed.Add(new
                        {
                            index,
                            item = label(item),
                            code = (ex as ToolException)?.Code ?? "REVIT_ERROR",
                            message = ex.Message,
                            hint = (ex as ToolException)?.Hint
                        });
                    }
                }
            }

            if (result.Done.Count == 0 && result.Failed.Count > 0)
                throw new ToolException("ALL_ITEMS_FAILED",
                    $"Every {noun.ToLowerInvariant()} failed; nothing was changed. " +
                    string.Join(" ", result.Failed.GetRange(0, Math.Min(5, result.Failed.Count))
                        .ConvertAll(f => Newtonsoft.Json.JsonConvert.SerializeObject(f))),
                    "Fix the items listed in the message and try again.");
            return result;
        }
    }
}
