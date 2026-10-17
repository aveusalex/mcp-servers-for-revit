using System.Linq;
using RevitMCPCommandSet.Framework;

namespace RevitMCPCommandSet.Tools.Core
{
    [McpTool("execute_batch", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "core", Batchable = false,
        TimeoutSeconds = 50,
        Description = "Runs several tool calls against one document as a single atomic change: one Ctrl+Z undoes " +
                      "the whole batch. Every step is validated before anything runs; UI steps can't be mixed with " +
                      "model changes and only batchable tools are accepted (max 200 steps). Use \"$steps[i].path\" in " +
                      "args to pass a value from an earlier step (e.g. \"$steps[0].changes.created[0]\"). With dryRun " +
                      "the batch runs and is rolled back, returning what would change. The approval risk of a batch " +
                      "is the highest risk among its steps. Returns per-step results plus the combined change set.")]
    public sealed class ExecuteBatchTool : McpTool<ExecuteBatchArgs>, ISelfTransactedTool
    {
        protected override object Run(ToolContext ctx, ExecuteBatchArgs args)
        {
            var steps = args.Steps.Select(s => new BatchStepRequest(s.Tool, s.Args)).ToList();
            return BatchRunner.Run(ctx, steps, args.StopOnError);
        }
    }
}
