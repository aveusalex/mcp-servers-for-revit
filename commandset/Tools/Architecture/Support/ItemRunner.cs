using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;

namespace RevitMCPCommandSet.Tools.Architecture.Support
{
    /// <summary>
    /// Runs the items of a batch creation tool. Without continueOnError the first failure aborts the call (the
    /// dispatcher rolls everything back). With it, each item runs in its own SubTransaction so a failed item leaves
    /// nothing behind and is reported in failed[].
    /// </summary>
    public static class ItemRunner
    {
        public sealed class Outcome
        {
            public List<object> Created { get; } = new List<object>();
            public List<object> Failed { get; } = new List<object>();
        }

        public static Outcome Run<T>(Document doc, IList<T> items, bool continueOnError, string what,
            Func<T, int, object> createOne)
        {
            var outcome = new Outcome();
            for (int index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (!continueOnError)
                {
                    try
                    {
                        outcome.Created.Add(createOne(item, index));
                    }
                    catch (ToolException ex)
                    {
                        throw new ToolException(ex.Code, $"{what} [{index}]: {ex.Message} Nothing was created.",
                            ex.Hint ?? "Fix this item, or pass continueOnError:true to create the others.", ex.RecommendedTool);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        throw new ToolException("REVIT_ERROR", $"{what} [{index}]: Revit refused it: {ex.Message} Nothing was created.",
                            "Fix this item, or pass continueOnError:true to create the others.");
                    }
                    continue;
                }

                using (var sub = new SubTransaction(doc))
                {
                    sub.Start();
                    try
                    {
                        outcome.Created.Add(createOne(item, index));
                        sub.Commit();
                    }
                    catch (Exception ex) when (ex is ToolException || ex is Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                        outcome.Failed.Add(Failure(index, ex));
                    }
                }
            }
            return outcome;
        }

        public static object Failure(int index, Exception ex) => new
        {
            index,
            code = (ex as ToolException)?.Code ?? "REVIT_ERROR",
            message = ex.Message,
            hint = (ex as ToolException)?.Hint
        };

        /// <summary>Runs <paramref name="action"/> in a SubTransaction; returns null on success, else the error.</summary>
        public static Exception TryIsolated(Document doc, Action action)
        {
            using (var sub = new SubTransaction(doc))
            {
                sub.Start();
                try
                {
                    action();
                    sub.Commit();
                    return null;
                }
                catch (Exception ex) when (ex is ToolException || ex is Autodesk.Revit.Exceptions.ApplicationException)
                {
                    if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                    return ex;
                }
            }
        }
    }
}
