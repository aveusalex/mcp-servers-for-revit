using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>One step of execute_batch as the caller sent it.</summary>
    public sealed class BatchStepRequest
    {
        public BatchStepRequest(string tool, JObject args)
        {
            Tool = tool;
            Args = args ?? new JObject();
        }

        public string Tool { get; }
        public JObject Args { get; }
    }

    /// <summary>A step that passed validation, with the metadata of its tool.</summary>
    public sealed class BatchPlanStep
    {
        public BatchPlanStep(int index, BatchStepRequest request, McpToolAttribute metadata)
        {
            Index = index;
            Request = request;
            Metadata = metadata;
        }

        public int Index { get; }
        public BatchStepRequest Request { get; }
        public McpToolAttribute Metadata { get; }

        /// <summary>True when the args contain "$steps[i]..." references (validated again after resolution).</summary>
        public bool HasReferences => StepReferences.FindAll(Request.Args).Any();
    }

    public sealed class BatchPlan
    {
        public BatchPlan(IReadOnlyList<BatchPlanStep> steps)
        {
            Steps = steps;
            Risk = BatchPolicy.AggregateRisk(steps.Select(s => s.Metadata.Risk));
            HasWrite = steps.Any(s => s.Metadata.Kind == ToolKind.Write);
            HasUi = steps.Any(s => s.Metadata.Kind == ToolKind.UI);
        }

        public IReadOnlyList<BatchPlanStep> Steps { get; }

        /// <summary>The highest risk among the steps: what the broker's approval gate uses for the batch.</summary>
        public Risk Risk { get; }

        public bool HasWrite { get; }
        public bool HasUi { get; }
    }

    /// <summary>
    /// Rules a batch must pass before any step runs. Mixed-kind refusal adapted from KenLP's BatchPolicy
    /// (MIT, Copyright (c) 2026 Le Phu).
    /// </summary>
    /// <remarks>No Revit dependency: covered by the framework tests.</remarks>
    public static class BatchPolicy
    {
        /// <summary>Same ceiling the broker enforces (Contract E).</summary>
        public const int MaxSteps = 200;

        public static Risk AggregateRisk(IEnumerable<Risk> risks)
        {
            var max = Risk.None;
            foreach (var risk in risks)
            {
                if (risk > max) max = risk;
            }
            return max;
        }

        /// <summary>
        /// Validates the whole batch. <paramref name="lookup"/> returns the metadata of a tool, or null when no
        /// such tool exists. Throws <see cref="ToolException"/> on the first problem found.
        /// </summary>
        public static BatchPlan Validate(IReadOnlyList<BatchStepRequest> steps, Func<string, McpToolAttribute> lookup,
            int maxSteps = MaxSteps)
        {
            if (steps == null || steps.Count == 0)
            {
                throw new ToolException(ToolErrorCodes.BatchEmpty, "The batch has no steps.",
                    "Pass steps: [{ tool, args }, ...].");
            }
            if (steps.Count > maxSteps)
            {
                throw new ToolException(ToolErrorCodes.BatchTooLarge,
                    $"The batch has {steps.Count} steps; the limit is {maxSteps}.",
                    "Split the work into several batches.");
            }

            var planned = new List<BatchPlanStep>(steps.Count);
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (step == null || string.IsNullOrWhiteSpace(step.Tool))
                {
                    throw new ToolException(ToolErrorCodes.InvalidArguments, $"Step {i} has no tool name.");
                }

                var metadata = lookup(step.Tool);
                if (metadata == null)
                {
                    throw new ToolException(ToolErrorCodes.UnknownTool,
                        $"Step {i}: there is no tool named '{step.Tool}' in the batch-capable tool set.",
                        "Only framework tools can run in a batch; legacy commands can't.");
                }
                if (!metadata.Batchable)
                {
                    throw new ToolException(ToolErrorCodes.StepNotBatchable,
                        $"Step {i}: tool '{step.Tool}' can't run inside a batch.",
                        "Call it on its own, before or after the batch.");
                }

                StepReferences.Validate(step.Args, i);
                planned.Add(new BatchPlanStep(i, step, metadata));
            }

            var plan = new BatchPlan(planned);
            if (plan.HasUi && plan.HasWrite)
            {
                throw new ToolException(ToolErrorCodes.BatchMixesUiAndWrite,
                    "The batch mixes UI steps with model changes. UI effects can't be rolled back with the model.",
                    "Run the UI steps (selection, active view) in a separate call.");
            }
            return plan;
        }
    }
}
