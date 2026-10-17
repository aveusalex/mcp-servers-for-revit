using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using Xunit;

namespace RevitMCPCommandSet.Services.Tests;

public class ParameterRefTests
{
    private sealed class Holder
    {
        [JsonProperty("parameter")] public ParameterRef? Parameter { get; set; }
    }

    [Fact]
    public void Plain_string_is_a_name()
    {
        var h = JsonConvert.DeserializeObject<Holder>("{\"parameter\":\"Comentários\"}")!;
        Assert.Equal("Comentários", h.Parameter!.Name);
        Assert.Null(h.Parameter.BuiltIn);
    }

    [Fact]
    public void Object_forms()
    {
        var h = JsonConvert.DeserializeObject<Holder>(
            "{\"parameter\":{\"builtIn\":\"ALL_MODEL_INSTANCE_COMMENTS\",\"guid\":\"6c5ef1a1-4b42-4a55-91d0-7f6ee8b8c111\"}}")!;
        Assert.Equal("ALL_MODEL_INSTANCE_COMMENTS", h.Parameter!.BuiltIn);
        Assert.Equal(Guid.Parse("6c5ef1a1-4b42-4a55-91d0-7f6ee8b8c111"), h.Parameter.ParsedGuid());
    }

    [Fact]
    public void Bad_guid_is_refused()
    {
        Assert.Equal("INVALID_ARGUMENTS", Assert.Throws<ToolException>(() => new ParameterRef { Guid = "nope" }.ParsedGuid()).Code);
    }

    [Fact]
    public void Round_trips_as_object()
    {
        var json = JsonConvert.SerializeObject(new ParameterRef { BuiltIn = "ALL_MODEL_MARK" });
        Assert.Equal("{\"builtIn\":\"ALL_MODEL_MARK\"}", json);
    }
}

public class ValueCoercionTests
{
    [Fact]
    public void Double_length_from_number_in_mm()
    {
        var v = ValueCoercion.Coerce(StorageKind.Double, new JValue(3000), UnitSpec.Length);
        Assert.Equal(3000 / 304.8, v.Double, 12);
        Assert.Equal("mm", v.Unit);
    }

    [Fact]
    public void Double_accepts_numeric_text_and_integer()
    {
        Assert.Equal(3000 / 304.8, ValueCoercion.Coerce(StorageKind.Double, "3000", UnitSpec.Length).Double, 12);
        Assert.Equal(2.5, ValueCoercion.Coerce(StorageKind.Double, "2,5", UnitSpec.Dimensionless).Double, 12);
        Assert.Equal(7, ValueCoercion.Coerce(StorageKind.Double, 7L, UnitSpec.Dimensionless).Double);
    }

    [Fact]
    public void Double_inline_unit_and_explicit_override()
    {
        Assert.Equal(3 / 0.3048, ValueCoercion.Coerce(StorageKind.Double, "3 m", UnitSpec.Length).Double, 9);
        Assert.Equal(3 / 0.3048, ValueCoercion.Coerce(StorageKind.Double, 3, UnitSpec.Length, "m").Double, 9);
        Assert.Equal("m", ValueCoercion.Coerce(StorageKind.Double, 3, UnitSpec.Length, "m").Unit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData("abc")]
    [InlineData("1,000.5")]
    public void Double_refuses_null_bool_and_text(object? value)
    {
        Assert.Equal("INVALID_VALUE",
            Assert.Throws<ToolException>(() => ValueCoercion.Coerce(StorageKind.Double, value, UnitSpec.Length)).Code);
    }

    [Fact]
    public void Double_with_unsupported_spec_fails_closed()
    {
        Assert.Equal("UNIT_UNSUPPORTED",
            Assert.Throws<ToolException>(() => ValueCoercion.Coerce(StorageKind.Double, 1, UnitSpec.OtherMeasurable)).Code);
        Assert.Equal(1, ValueCoercion.Coerce(StorageKind.Double, 1, UnitSpec.OtherMeasurable, "internal").Double);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    [InlineData("sim", 1)]
    [InlineData("Não", 0)]
    [InlineData("yes", 1)]
    [InlineData("12", 12)]
    [InlineData(12L, 12)]
    [InlineData(3.0, 3)]
    public void Integer_coercion(object value, int expected)
    {
        Assert.Equal(expected, ValueCoercion.Coerce(StorageKind.Integer, value).Integer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2.5)]
    [InlineData("talvez")]
    [InlineData(5_000_000_000L)]
    public void Integer_refusals(object? value)
    {
        Assert.Equal("INVALID_VALUE", Assert.Throws<ToolException>(() => ValueCoercion.Coerce(StorageKind.Integer, value)).Code);
    }

    [Fact]
    public void String_coercion_and_clear()
    {
        Assert.Equal("120", ValueCoercion.Coerce(StorageKind.String, 120L).Text);
        Assert.Equal("1.5", ValueCoercion.Coerce(StorageKind.String, 1.5).Text);
        var cleared = ValueCoercion.Coerce(StorageKind.String, JValue.CreateNull());
        Assert.True(cleared.IsClear);
        Assert.Equal("", cleared.Text);
    }

    [Fact]
    public void ElementId_by_id_name_or_clear()
    {
        Assert.Equal(311L, ValueCoercion.Coerce(StorageKind.ElementId, 311L).Id);
        Assert.Equal(311L, ValueCoercion.Coerce(StorageKind.ElementId, "311").Id);
        var byName = ValueCoercion.Coerce(StorageKind.ElementId, "Térreo");
        Assert.Null(byName.Id);
        Assert.Equal("Térreo", byName.IdName);
        Assert.Equal(-1L, ValueCoercion.Coerce(StorageKind.ElementId, null).Id);
    }

    [Fact]
    public void Objects_are_refused()
    {
        Assert.Equal("INVALID_VALUE",
            Assert.Throws<ToolException>(() => ValueCoercion.Coerce(StorageKind.String, new JObject())).Code);
    }
}

public class ReadBackAndCriticalTests
{
    [Fact]
    public void Read_back_tolerances()
    {
        Assert.True(ReadBack.SameDouble(9.842519685, 9.842519685 + 1e-12));
        Assert.False(ReadBack.SameDouble(9.84, 9.85));
        Assert.True(ReadBack.SameText(null, ""));
        Assert.False(ReadBack.SameText("a", "A"));
    }

    [Theory]
    [InlineData("ELEM_TYPE_PARAM")]
    [InlineData("WALL_BASE_CONSTRAINT")]
    [InlineData("PHASE_CREATED")]
    [InlineData("ELEM_PARTITION_PARAM")]
    public void Critical_parameters_blocked_unless_allowed(string bip)
    {
        Assert.NotNull(CriticalParameters.BlockReason(bip, 1, allowCritical: false));
        Assert.Null(CriticalParameters.BlockReason(bip, 1, allowCritical: true));
    }

    [Fact]
    public void Mark_only_blocked_in_bulk()
    {
        Assert.Null(CriticalParameters.BlockReason("ALL_MODEL_MARK", 1, false));
        Assert.NotNull(CriticalParameters.BlockReason("ALL_MODEL_MARK", 2, false));
        Assert.Null(CriticalParameters.BlockReason("ALL_MODEL_INSTANCE_COMMENTS", 50, false));
        Assert.Null(CriticalParameters.BlockReason(null, 50, false));
    }
}

public class WhereEvaluatorTests
{
    private static ValueSnapshot Num(double n, string? text = null) => new() { Exists = true, Number = n, Text = text ?? n.ToString() };
    private static ValueSnapshot Txt(string t) => new() { Exists = true, Text = t };

    [Fact]
    public void Numbers_compare_in_wire_units()
    {
        Assert.True(WhereEvaluator.Matches(Num(3000), "gte", 3000));
        Assert.True(WhereEvaluator.Matches(Num(3000.0000001), "eq", "3000"));
        Assert.False(WhereEvaluator.Matches(Num(2999), ">=", 3000));
        Assert.True(WhereEvaluator.Matches(Num(2999), "<", new JValue(3000)));
    }

    [Fact]
    public void Text_is_case_and_accent_insensitive()
    {
        Assert.True(WhereEvaluator.Matches(Txt("Pavimento Térreo"), "contains", "terreo"));
        Assert.True(WhereEvaluator.Matches(Txt("EI-60"), "starts_with", "ei"));
        Assert.True(WhereEvaluator.Matches(Txt("Porta 90"), "regex", "^porta \\d+$"));
        Assert.True(WhereEvaluator.Matches(Txt("A"), "neq", "B"));
    }

    [Fact]
    public void Missing_parameter_only_matches_is_empty()
    {
        Assert.True(WhereEvaluator.Matches(ValueSnapshot.Missing, "is_empty", null));
        Assert.False(WhereEvaluator.Matches(ValueSnapshot.Missing, "neq", "x"));
        Assert.False(WhereEvaluator.Matches(ValueSnapshot.Missing, "not_empty", null));
        Assert.True(WhereEvaluator.Matches(Txt(""), "is_empty", null));
    }

    [Fact]
    public void Bad_conditions_fail_up_front()
    {
        Assert.Equal("INVALID_ARGUMENTS", Assert.Throws<ToolException>(() => WhereEvaluator.NormalizeOperator("between")).Code);
        Assert.Throws<ToolException>(() => WhereEvaluator.ValidateCondition("regex", "(unclosed"));
        Assert.Throws<ToolException>(() => WhereEvaluator.ValidateCondition("gt", "abc"));
        Assert.Throws<ToolException>(() => WhereEvaluator.ValidateCondition("eq", null));
        WhereEvaluator.ValidateCondition("is_empty", null);
    }
}

public class TextMatchTests
{
    [Fact]
    public void Key_folds_case_accents_and_separators()
    {
        Assert.Equal(TextMatch.Key("Generic Models"), TextMatch.Key("generic_models"));
        Assert.Equal("pavimentoterreo", TextMatch.Key(" Pavimento Térreo "));
    }

    [Fact]
    public void Distance()
    {
        Assert.Equal(3, TextMatch.Distance("kitten", "sitting"));
        Assert.Equal(0, TextMatch.Distance("", ""));
    }
}
