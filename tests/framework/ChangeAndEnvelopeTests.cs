using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using Xunit;

namespace RevitMCPFramework.Tests
{
    public class ChangeAccumulatorTests
    {
        [Fact]
        public void Keeps_net_membership()
        {
            var acc = new ChangeAccumulator();
            acc.Created(1);
            acc.Modified(1);      // still created
            acc.Created(2);
            acc.Deleted(2);       // created + deleted = nothing
            acc.Modified(3);
            acc.Modified(3);
            acc.Deleted(4);
            acc.Modified(5);
            acc.Deleted(5);       // modified then deleted = deleted

            var set = acc.ToChangeSet();
            Assert.Equal(new long[] { 1 }, set.Created);
            Assert.Equal(new long[] { 3 }, set.Modified);
            Assert.Equal(new long[] { 4, 5 }, set.Deleted);
            Assert.False(set.Truncated);
        }

        [Fact]
        public void Caps_each_list_and_marks_truncated()
        {
            var acc = new ChangeAccumulator(cap: 3);
            for (long i = 0; i < 10; i++) acc.Created(i);
            acc.Modified(100);

            var set = acc.ToChangeSet();
            Assert.Equal(3, set.Created.Count);
            Assert.Single(set.Modified);
            Assert.True(set.Truncated);
        }

        [Fact]
        public void Default_cap_is_500()
        {
            var acc = new ChangeAccumulator();
            for (long i = 0; i < 501; i++) acc.Created(i);
            var set = acc.ToChangeSet();
            Assert.Equal(500, set.Created.Count);
            Assert.True(set.Truncated);
        }

        [Fact]
        public void Flush_keeps_ids_the_tool_added()
        {
            var target = new ChangeSet();
            target.Modified.Add(9);
            var acc = new ChangeAccumulator();
            acc.Modified(9);
            acc.Modified(10);
            acc.FlushTo(target);
            Assert.Equal(new long[] { 9, 10 }, target.Modified);
        }
    }

    public class EnvelopeJsonTests
    {
        [Fact]
        public void Success_envelope_has_camel_case_keys()
        {
            var changes = new ChangeSet();
            changes.Created.Add(42);
            var json = EnvelopeJson.ToJson(new ToolResult
            {
                Ok = true,
                Data = new { count = 1 },
                Changes = changes,
                Warnings = new List<ToolWarning> { new ToolWarning("Overlap", new List<long> { 42 }) },
                DryRun = true
            });

            Assert.True((bool)json["ok"]);
            Assert.Equal(1, (int)json["data"]["count"]);
            Assert.Equal(42L, (long)json["changes"]["created"][0]);
            Assert.Null(json["changes"]["truncated"]);
            Assert.Equal("Overlap", (string)json["warnings"][0]["message"]);
            Assert.Equal(42L, (long)json["warnings"][0]["elementIds"][0]);
            Assert.True((bool)json["dryRun"]);
            Assert.Null(json["error"]);
        }

        [Fact]
        public void Tool_exception_maps_to_error_with_hint()
        {
            var json = EnvelopeJson.ToJson(ToolErrors.Fail(new ToolException("NOT_FOUND", "No such wall.", "Use find_elements.", "find_elements")));
            Assert.False((bool)json["ok"]);
            Assert.Equal(JTokenType.Null, json["data"].Type);
            Assert.Null(json["changes"]);
            Assert.Equal("NOT_FOUND", (string)json["error"]["code"]);
            Assert.Equal("Use find_elements.", (string)json["error"]["hint"]);
            Assert.Equal("find_elements", (string)json["error"]["recommendedTool"]);
            Assert.Empty((JArray)json["warnings"]);
        }

        [Fact]
        public void Other_exceptions_become_internal_error()
        {
            var error = ToolErrors.FromException(new System.Reflection.TargetInvocationException(new System.ArgumentException("bad")));
            Assert.Equal(ToolErrorCodes.InternalError, error.Code);
            Assert.Contains("ArgumentException", error.Message);
            Assert.NotNull(error.Hint);

            var wrapped = ToolErrors.FromException(new System.Reflection.TargetInvocationException(new ToolException("X", "y")));
            Assert.Equal("X", wrapped.Code);
        }

        [Fact]
        public void Error_warnings_carry_severity()
        {
            var json = EnvelopeJson.WarningToJson(new ToolWarning("Can't join", null, "error"));
            Assert.Equal("error", (string)json["severity"]);
            Assert.Null(json["elementIds"]);
        }
    }
}
