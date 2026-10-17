using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Manifest;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Core;
using RevitMCPCommandSet.Tools.Editing;
using Xunit;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>Test stand-in: the real McpTool&lt;T&gt; references Revit. Matched by name, like ManifestGen does.</summary>
    public abstract class McpTool<TArgs> where TArgs : class, new()
    {
    }
}

namespace RevitMCPFramework.Tests
{
    public sealed class SampleNested
    {
        [JsonProperty("x")]
        [Unit("mm")]
        [Description("X coordinate.")]
        public double X { get; set; }

        [JsonProperty("y")]
        [Required]
        [Unit("mm")]
        [Description("Y coordinate.")]
        public double? Y { get; set; }
    }

    public sealed class SampleArgs
    {
        [JsonProperty("elementIds")]
        [Required]
        [MinLength(1)]
        [MaxLength(3)]
        [Description("Element ids.")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("offset")]
        [Unit("mm")]
        [Range(-1000, 1000)]
        [Description("Offset.")]
        public double Offset { get; set; }

        [JsonProperty("mode")]
        [EnumValues("copy", "move")]
        [DefaultValue("move")]
        [Description("Mode.")]
        public string Mode { get; set; } = "move";

        [JsonProperty("point")]
        [Description("A point.")]
        public SampleNested Point { get; set; }

        [JsonProperty("scale")]
        [Description("Dimensionless factor: no unit needed.")]
        public double Scale { get; set; } = 1;
    }

    public sealed class BadArgs
    {
        [Description("Wall length.")]
        public double Length { get; set; }

        [Unit("cm")]
        [Description("Height.")]
        public double Height { get; set; }

        [Unit("mm")]
        [Description("Name.")]
        public string Name { get; set; }

        [EnumValues("a")]
        [Description("Count.")]
        public int Count { get; set; }
    }

    [McpTool("sample_move", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "editing",
        Description = "Moves elements by an offset in millimetres; undo with Ctrl+Z.")]
    public sealed class SampleMoveTool : McpTool<SampleArgs>
    {
    }

    [McpTool("a_sample_read", Description = "Reads something harmless for the tests only.")]
    public sealed class SampleReadTool : McpTool<SetElementsPinnedArgs>
    {
    }

    [McpTool("bad_tool", Kind = ToolKind.Write, Description = "short")]
    public sealed class BadTool : McpTool<BadArgs>
    {
    }

    public class ArgsBinderTests
    {
        [Fact]
        public void Binds_valid_arguments()
        {
            var args = ArgsBinder.Bind<SampleArgs>(JObject.Parse("{ elementIds: [1, 2], offset: 12.5, mode: 'COPY', point: { x: 1, y: 2 } }"));
            Assert.Equal(new long[] { 1, 2 }, args.ElementIds);
            Assert.Equal(12.5, args.Offset);
            Assert.Equal("COPY", args.Mode);
            Assert.Equal(2, args.Point.Y);
        }

        [Fact]
        public void Names_match_case_insensitively()
        {
            var args = ArgsBinder.Bind<SampleArgs>(JObject.Parse("{ ElementIDs: [7] }"));
            Assert.Equal(7, args.ElementIds[0]);
        }

        [Theory]
        [InlineData("{ }", "Missing required argument 'elementIds'")]
        [InlineData("{ elementIds: [1], extra: 1 }", "Unknown argument 'extra'")]
        [InlineData("{ elementIds: [] }", "at least 1")]
        [InlineData("{ elementIds: [1,2,3,4] }", "at most 3")]
        [InlineData("{ elementIds: [1], offset: 5000 }", "between -1000 and 1000")]
        [InlineData("{ elementIds: [1], mode: 'delete' }", "must be one of: copy, move")]
        [InlineData("{ elementIds: [1], point: { x: 1 } }", "Missing required argument 'point.y'")]
        [InlineData("{ elementIds: [1], point: { x: 1, y: 1, z: 1 } }", "Unknown argument 'point.z'")]
        [InlineData("{ elementIds: 'abc' }", "must be an array")]
        [InlineData("{ elementIds: [1], offset: 'far' }", "Invalid arguments")]
        public void Rejects_invalid_arguments(string json, string expected)
        {
            var ex = Assert.Throws<ToolException>(() => ArgsBinder.Bind<SampleArgs>(JObject.Parse(json)));
            Assert.Equal(ToolErrorCodes.InvalidArguments, ex.Code);
            Assert.Contains(expected, ex.Message);
            Assert.NotNull(ex.Hint);
        }

        [Fact]
        public void Pilot_args_bind()
        {
            var pinned = ArgsBinder.Bind<SetElementsPinnedArgs>(JObject.Parse("{ elementIds: [10, 11], pinned: false }"));
            Assert.False(pinned.Pinned);
            Assert.Throws<ToolException>(() => ArgsBinder.Bind<SetElementsPinnedArgs>(JObject.Parse("{ elementIds: [10] }")));

            var batch = ArgsBinder.Bind<ExecuteBatchArgs>(JObject.Parse(
                "{ steps: [ { tool: 'set_elements_pinned', args: { elementIds: ['$steps[0].changes.created[0]'], pinned: true } } ] }"));
            Assert.True(batch.StopOnError);
            Assert.Equal("set_elements_pinned", batch.Steps[0].Tool);
            Assert.Equal("$steps[0].changes.created[0]", (string)batch.Steps[0].Args["elementIds"][0]);
        }
    }

    public class ContractTests
    {
        private static readonly System.Type[] PilotArgs = { typeof(SetElementsPinnedArgs), typeof(ExecuteBatchArgs), typeof(ExecuteBatchStep) };

        [Fact]
        public void Pilot_tool_args_satisfy_the_contract()
        {
            foreach (var type in PilotArgs)
            {
                Assert.Empty(ArgsContract.FindContractViolations(type));
            }
        }

        [Fact]
        public void Sample_args_satisfy_the_contract()
        {
            Assert.Empty(ArgsContract.FindContractViolations(typeof(SampleArgs)));
        }

        [Fact]
        public void Measure_without_unit_and_bad_attributes_are_reported()
        {
            var problems = ArgsContract.FindContractViolations(typeof(BadArgs), "bad_tool");
            Assert.Contains(problems, p => p.Contains("bad_tool.Length") && p.Contains("without [Unit]"));
            Assert.Contains(problems, p => p.Contains("bad_tool.Height") && p.Contains("'cm'"));
            Assert.Contains(problems, p => p.Contains("bad_tool.Name") && p.Contains("non-numeric"));
            Assert.Contains(problems, p => p.Contains("bad_tool.Count") && p.Contains("[EnumValues]"));
        }

        [Fact]
        public void Validate_reports_tool_level_problems()
        {
            var problems = ManifestBuilder.Validate(new[] { typeof(SampleMoveTool), typeof(BadTool) });
            Assert.Contains(problems, p => p.Contains("bad_tool") && p.Contains("Description"));
            Assert.Contains(problems, p => p.Contains("bad_tool") && p.Contains("Risk"));
            Assert.DoesNotContain(problems, p => p.Contains("sample_move"));
        }

        [Fact]
        public void Finds_tools_and_their_args()
        {
            var types = ManifestBuilder.FindToolTypes(new[] { typeof(SampleMoveTool), typeof(SampleArgs), typeof(string) }).ToList();
            Assert.Equal(new[] { typeof(SampleMoveTool) }, types);
            Assert.Equal(typeof(SampleArgs), ManifestBuilder.FindArgsType(typeof(SampleMoveTool)));
            Assert.Equal("sample_move", ManifestBuilder.ReadMetadata(typeof(SampleMoveTool)).Name);
        }
    }

    public class ManifestTests
    {
        private static JObject Build() => ManifestBuilder.Build(new[] { typeof(SampleMoveTool), typeof(SampleReadTool) });

        [Fact]
        public void Manifest_follows_contract_d()
        {
            var manifest = Build();
            Assert.Equal(1, (int)manifest["version"]);
            Assert.Matches("^[0-9a-f]{64}$", (string)manifest["capabilityHash"]);

            var tools = (JArray)manifest["tools"];
            Assert.Equal(new[] { "a_sample_read", "sample_move" }, tools.Select(t => (string)t["name"]));

            var move = tools[1];
            Assert.Equal("write", (string)move["kind"]);
            Assert.Equal("low", (string)move["risk"]);
            Assert.Equal("editing", (string)move["toolset"]);
            Assert.True((bool)move["batchable"]);
            Assert.Equal(50, (int)move["timeoutSeconds"]);

            var schema = move["inputSchema"];
            Assert.False((bool)schema["additionalProperties"]);
            Assert.Equal(new[] { "elementIds" }, schema["required"].Select(t => (string)t));
            Assert.Equal("mm", (string)schema["properties"]["offset"]["x-unit"]);
            Assert.Equal(new[] { "copy", "move" }, schema["properties"]["mode"]["enum"].Select(t => (string)t));
            Assert.Equal("integer", (string)schema["properties"]["elementIds"]["items"]["type"]);
            Assert.Equal("object", (string)schema["properties"]["point"]["type"]);
        }

        [Fact]
        public void Runtime_manifest_equals_the_build_time_generator()
        {
            // The plugin's manifest must hash exactly like tools.manifest.json (ADDIN_OUTDATED check).
            var runtime = Build();
            var buildTime = ToolManifestBuilder.Build(new[] { typeof(SampleArgs), typeof(SampleMoveTool), typeof(SampleReadTool), typeof(string) });
            Assert.True(JToken.DeepEquals(runtime, buildTime));
        }

        [Fact]
        public void Hash_changes_when_a_schema_changes()
        {
            var a = Build();
            var b = ManifestBuilder.Build(new[] { typeof(SampleMoveTool) });
            Assert.NotEqual((string)a["capabilityHash"], (string)b["capabilityHash"]);
            Assert.Equal((string)a["capabilityHash"], (string)Build()["capabilityHash"]);
        }
    }
}
