using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using Xunit;

namespace RevitMCPFramework.Tests
{
    public class BatchPolicyTests
    {
        private static readonly Dictionary<string, McpToolAttribute> Tools = new Dictionary<string, McpToolAttribute>
        {
            ["read_a"] = new McpToolAttribute("read_a") { Kind = ToolKind.Read },
            ["write_low"] = new McpToolAttribute("write_low") { Kind = ToolKind.Write, Risk = Risk.Low },
            ["write_medium"] = new McpToolAttribute("write_medium") { Kind = ToolKind.Write, Risk = Risk.Medium },
            ["write_high"] = new McpToolAttribute("write_high") { Kind = ToolKind.Write, Risk = Risk.High },
            ["ui_select"] = new McpToolAttribute("ui_select") { Kind = ToolKind.UI },
            ["execute_batch"] = new McpToolAttribute("execute_batch") { Kind = ToolKind.Write, Risk = Risk.Low, Batchable = false }
        };

        private static McpToolAttribute Lookup(string name) => Tools.TryGetValue(name, out var m) ? m : null;

        private static List<BatchStepRequest> Steps(params string[] tools) =>
            tools.Select(t => new BatchStepRequest(t, new JObject())).ToList();

        [Fact]
        public void Risk_is_the_highest_among_steps()
        {
            Assert.Equal(Risk.Medium, BatchPolicy.Validate(Steps("read_a", "write_medium", "write_low"), Lookup).Risk);
            Assert.Equal(Risk.High, BatchPolicy.Validate(Steps("write_high", "write_low"), Lookup).Risk);
            Assert.Equal(Risk.None, BatchPolicy.Validate(Steps("read_a"), Lookup).Risk);
            Assert.Equal(Risk.None, BatchPolicy.AggregateRisk(new Risk[0]));
        }

        [Fact]
        public void Plan_reports_kinds()
        {
            var plan = BatchPolicy.Validate(Steps("read_a", "write_low"), Lookup);
            Assert.True(plan.HasWrite);
            Assert.False(plan.HasUi);
            Assert.Equal(2, plan.Steps.Count);
        }

        [Theory]
        [InlineData(ToolErrorCodes.BatchEmpty)]
        public void Empty_batch_is_refused(string code)
        {
            Assert.Equal(code, Assert.Throws<ToolException>(() => BatchPolicy.Validate(Steps(), Lookup)).Code);
        }

        [Fact]
        public void Too_many_steps_are_refused()
        {
            var steps = Enumerable.Repeat("read_a", 5).ToArray();
            var ex = Assert.Throws<ToolException>(() => BatchPolicy.Validate(Steps(steps), Lookup, maxSteps: 4));
            Assert.Equal(ToolErrorCodes.BatchTooLarge, ex.Code);
        }

        [Fact]
        public void Unknown_tool_is_refused_with_step_index()
        {
            var ex = Assert.Throws<ToolException>(() => BatchPolicy.Validate(Steps("read_a", "nope"), Lookup));
            Assert.Equal(ToolErrorCodes.UnknownTool, ex.Code);
            Assert.Contains("Step 1", ex.Message);
        }

        [Fact]
        public void Non_batchable_tool_is_refused()
        {
            var ex = Assert.Throws<ToolException>(() => BatchPolicy.Validate(Steps("write_low", "execute_batch"), Lookup));
            Assert.Equal(ToolErrorCodes.StepNotBatchable, ex.Code);
        }

        [Fact]
        public void Ui_mixed_with_write_is_refused_but_ui_with_read_is_fine()
        {
            var ex = Assert.Throws<ToolException>(() => BatchPolicy.Validate(Steps("ui_select", "write_low"), Lookup));
            Assert.Equal(ToolErrorCodes.BatchMixesUiAndWrite, ex.Code);

            var plan = BatchPolicy.Validate(Steps("ui_select", "read_a"), Lookup);
            Assert.True(plan.HasUi);
        }

        [Fact]
        public void References_are_checked_before_execution()
        {
            var steps = new List<BatchStepRequest>
            {
                new BatchStepRequest("write_low", JObject.Parse("{ ids: ['$steps[1].changes.created[0]'] }")),
                new BatchStepRequest("write_low", new JObject())
            };
            var ex = Assert.Throws<ToolException>(() => BatchPolicy.Validate(steps, Lookup));
            Assert.Equal(ToolErrorCodes.BatchReferenceInvalid, ex.Code);

            steps = new List<BatchStepRequest>
            {
                new BatchStepRequest("write_low", new JObject()),
                new BatchStepRequest("write_low", JObject.Parse("{ ids: ['$steps[0].changes.created[0]'] }"))
            };
            var plan = BatchPolicy.Validate(steps, Lookup);
            Assert.False(plan.Steps[0].HasReferences);
            Assert.True(plan.Steps[1].HasReferences);
        }

        [Fact]
        public void Missing_tool_name_is_invalid()
        {
            var ex = Assert.Throws<ToolException>(() => BatchPolicy.Validate(new List<BatchStepRequest> { new BatchStepRequest(" ", null) }, Lookup));
            Assert.Equal(ToolErrorCodes.InvalidArguments, ex.Code);
        }
    }
}
