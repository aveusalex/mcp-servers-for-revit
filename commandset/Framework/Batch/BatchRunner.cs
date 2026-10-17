using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// Runs execute_batch on Revit's thread: validates every step first, then runs them in one
    /// <c>TransactionGroup("MCP: Batch (n)")</c> with one transaction per step. <c>Assimilate()</c> on success gives
    /// a single Ctrl+Z; a failure with stopOnError, or dryRun, rolls the whole group back.
    /// </summary>
    /// <remarks>Design: KenLP (contract, MIT, Copyright (c) 2026 Le Phu) + bimwright (group; Apache-2.0, rewritten).</remarks>
    internal static class BatchRunner
    {
        public static ToolResult Run(ToolContext ctx, IReadOnlyList<BatchStepRequest> steps, bool stopOnError)
        {
            var plan = BatchPolicy.Validate(steps, name => ToolRegistry.TryGet(name, out var d) ? d.Metadata : null);
            var doc = ctx.Doc;
            long logMark = RevitMCPCommandSet.Tools.Query.Support.SessionChangeLog.CurrentSeq;

            // Check the arguments of every step that doesn't depend on an earlier result, before anything runs.
            foreach (var step in plan.Steps.Where(s => !s.HasReferences))
            {
                ToolRegistry.TryGet(step.Request.Tool, out var descriptor);
                var args = (JObject)step.Request.Args.DeepClone();
                ToolDispatcher.TakeDryRun(args);
                args.Remove("document");
                try
                {
                    (descriptor.CreateInstance() as IMcpToolArgsCheck)?.CheckArgs(args);
                }
                catch (ToolException ex)
                {
                    throw new ToolException(ex.Code, $"Step {step.Index} ('{step.Request.Tool}'): {ex.Message}", ex.Hint, ex.RecommendedTool);
                }
            }

            if (plan.HasUi) ToolRunner.EnsureActive(ctx.UiApp, doc, "execute_batch");
            if (plan.HasWrite) ToolRunner.EnsureWritable(doc);

            var batchId = Guid.NewGuid().ToString("N");
            var envelopes = new List<JObject>();
            var summaries = new JArray();
            var total = new ChangeAccumulator();
            int failed = 0;
            int abortedAt = -1;
            TransactionGroup group = null;

            try
            {
                using (DocumentChangeTracker.Track(doc, total))
                {
                    if (plan.HasWrite)
                    {
                        group = new TransactionGroup(doc, $"MCP: Batch ({plan.Steps.Count})");
                        group.Start();
                    }

                    foreach (var step in plan.Steps)
                    {
                        ToolRegistry.TryGet(step.Request.Tool, out var descriptor);
                        ToolResult result;
                        try
                        {
                            var args = (JObject)StepReferences.Resolve(step.Request.Args, envelopes, step.Index);
                            ToolDispatcher.TakeDryRun(args);
                            args.Remove("document");
                            result = ToolRunner.Run(ctx.UiApp, doc, descriptor, args, ctx.DryRun, ctx.RequestId, batchId, insideGroup: true);
                        }
                        catch (Exception ex)
                        {
                            result = ToolErrors.Fail(ex, ctx.DryRun);
                        }

                        var envelope = EnvelopeJson.ToJson(result);
                        envelopes.Add(envelope);

                        var summary = new JObject { ["index"] = step.Index, ["tool"] = step.Request.Tool };
                        foreach (var property in envelope.Properties().Where(p => p.Name != "dryRun"))
                        {
                            summary[property.Name] = property.Value.DeepClone();
                        }
                        summaries.Add(summary);

                        foreach (var warning in result.Warnings ?? new List<ToolWarning>())
                        {
                            ctx.Warnings.Add(new ToolWarning($"Step {step.Index} ({step.Request.Tool}): {warning.Message}",
                                warning.ElementIds, warning.Severity));
                        }

                        if (!result.Ok)
                        {
                            failed++;
                            if (!stopOnError)
                            {
                                ctx.Warnings.Add(new ToolWarning(
                                    $"Step {step.Index} ({step.Request.Tool}) failed and was skipped: {result.Error?.Code}: {result.Error?.Message}",
                                    null, "error"));
                            }
                            if (stopOnError)
                            {
                                abortedAt = step.Index;
                                break;
                            }
                        }
                    }
                }

                bool keep = group != null && abortedAt < 0 && !ctx.DryRun;
                if (group != null)
                {
                    if (keep) group.Assimilate();
                    else
                    {
                        group.RollBack();
                        // No DocumentChanged for a rolled-back group: forget its commits in the change log.
                        RevitMCPCommandSet.Tools.Query.Support.SessionChangeLog.DiscardSince(doc, logMark);
                    }
                }

                var data = new JObject
                {
                    ["batchId"] = batchId,
                    ["risk"] = ManifestBuilder.Lower(plan.Risk),
                    ["committed"] = keep,
                    ["stepCount"] = plan.Steps.Count,
                    ["executed"] = envelopes.Count,
                    ["failed"] = failed,
                    ["steps"] = summaries
                };

                var envelopeResult = new ToolResult
                {
                    Ok = abortedAt < 0,
                    Data = data,
                    DryRun = ctx.DryRun,
                    Changes = abortedAt < 0 ? total.ToChangeSet() : new ChangeSet()
                };

                if (abortedAt >= 0)
                {
                    var stepError = envelopes[abortedAt]["error"] as JObject;
                    var tool = plan.Steps[abortedAt].Request.Tool;
                    envelopeResult.Error = new ToolError
                    {
                        Code = ToolErrorCodes.BatchStepFailed,
                        Message = $"Step {abortedAt} ('{tool}') failed with {(string)stepError?["code"]}: {(string)stepError?["message"]} " +
                                  "The whole batch was rolled back; nothing was changed.",
                        Hint = (string)stepError?["hint"] ?? "Fix that step and run the batch again.",
                        RecommendedTool = (string)stepError?["recommendedTool"]
                    };
                }

                return envelopeResult;
            }
            finally
            {
                if (group != null)
                {
                    if (group.HasStarted() && !group.HasEnded())
                    {
                        group.RollBack();
                        RevitMCPCommandSet.Tools.Query.Support.SessionChangeLog.DiscardSince(doc, logMark);
                    }
                    group.Dispose();
                }
            }
        }
    }
}
