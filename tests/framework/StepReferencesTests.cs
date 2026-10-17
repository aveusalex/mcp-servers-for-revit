using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using Xunit;

namespace RevitMCPFramework.Tests
{
    public class StepReferencesTests
    {
        private static readonly List<JObject> Results = new List<JObject>
        {
            JObject.Parse("{ ok: true, data: { viewId: 77, names: ['a','b'] }, changes: { created: [101, 102], modified: [], deleted: [] } }"),
            JObject.Parse("{ ok: true, data: { sheet: { id: 900 } }, changes: { created: [], modified: [5], deleted: [] } }")
        };

        [Theory]
        [InlineData("$steps[0].changes.created[0]", 0, ".changes.created[0]")]
        [InlineData("$steps[12].data.viewId", 12, ".data.viewId")]
        [InlineData("$steps[1]", 1, "")]
        public void Parses_well_formed_references(string raw, int index, string path)
        {
            Assert.True(StepReferences.TryParse(raw, out var reference));
            Assert.Equal(index, reference.StepIndex);
            Assert.Equal(path, reference.Path);
        }

        [Theory]
        [InlineData("$steps[x].data")]
        [InlineData("$steps[0].data.")]
        [InlineData("$steps[0]data")]
        [InlineData("steps[0].data")]
        public void Rejects_malformed_references(string raw)
        {
            Assert.False(StepReferences.TryParse(raw, out _));
        }

        [Fact]
        public void Resolves_nested_values_with_types_preserved()
        {
            var args = JObject.Parse("{ elementIds: ['$steps[0].changes.created[1]', 5], view: '$steps[0].data.viewId', sheet: '$steps[1].data.sheet', label: 'plain' }");

            var resolved = (JObject)StepReferences.Resolve(args, Results, 2);

            Assert.Equal(JTokenType.Integer, resolved["elementIds"][0].Type);
            Assert.Equal(102L, (long)resolved["elementIds"][0]);
            Assert.Equal(77L, (long)resolved["view"]);
            Assert.Equal(900L, (long)resolved["sheet"]["id"]);
            Assert.Equal("plain", (string)resolved["label"]);
            // The original args are not modified.
            Assert.Equal("$steps[0].data.viewId", (string)args["view"]);
        }

        [Fact]
        public void Shorthand_looks_in_data_then_changes()
        {
            var args = JObject.Parse("{ a: '$steps[0].viewId', b: '$steps[0].created[0]', c: '$steps[1].modified[0]' }");
            var resolved = StepReferences.Resolve(args, Results, 2);
            Assert.Equal(77L, (long)resolved["a"]);
            Assert.Equal(101L, (long)resolved["b"]);
            Assert.Equal(5L, (long)resolved["c"]);
        }

        [Fact]
        public void Double_dollar_escapes_a_literal()
        {
            var resolved = StepReferences.Resolve(JObject.Parse("{ text: '$$steps[0].data' }"), Results, 1);
            Assert.Equal("$steps[0].data", (string)resolved["text"]);
        }

        [Fact]
        public void Missing_value_is_unresolved()
        {
            var ex = Assert.Throws<ToolException>(() =>
                StepReferences.Resolve(JObject.Parse("{ id: '$steps[1].changes.created[0]' }"), Results, 2));
            Assert.Equal(ToolErrorCodes.BatchReferenceUnresolved, ex.Code);
        }

        [Fact]
        public void Forward_and_self_references_are_invalid()
        {
            var ex = Assert.Throws<ToolException>(() => StepReferences.Validate(JObject.Parse("{ id: '$steps[2].data.x' }"), 2));
            Assert.Equal(ToolErrorCodes.BatchReferenceInvalid, ex.Code);

            ex = Assert.Throws<ToolException>(() => StepReferences.Validate(JObject.Parse("{ id: '$steps[0' }"), 1));
            Assert.Equal(ToolErrorCodes.BatchReferenceInvalid, ex.Code);

            StepReferences.Validate(JObject.Parse("{ id: '$steps[0].data.x' }"), 1); // earlier step: fine
        }

        [Fact]
        public void Lookup_of_empty_path_returns_the_envelope()
        {
            Assert.Same(Results[0], StepReferences.Lookup(Results[0], ""));
        }
    }
}
