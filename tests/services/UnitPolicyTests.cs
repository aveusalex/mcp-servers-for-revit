using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using Xunit;

namespace RevitMCPCommandSet.Services.Tests;

public class UnitPolicyTests
{
    [Theory]
    [InlineData(UnitSpec.Length, 304.8, 1.0)]
    [InlineData(UnitSpec.Area, 0.09290304, 1.0)]
    [InlineData(UnitSpec.Volume, 0.028316846592, 1.0)]
    [InlineData(UnitSpec.Angle, 180.0, Math.PI)]
    [InlineData(UnitSpec.Slope, 50.0, 0.5)]
    [InlineData(UnitSpec.Dimensionless, 42.0, 42.0)]
    public void Wire_defaults_convert_to_internal(UnitSpec spec, double wire, double expectedInternal)
    {
        Assert.Equal(expectedInternal, UnitPolicy.ToInternal(wire, spec), 9);
    }

    [Theory]
    [InlineData(UnitSpec.Length, "mm")]
    [InlineData(UnitSpec.Area, "m2")]
    [InlineData(UnitSpec.Volume, "m3")]
    [InlineData(UnitSpec.Angle, "deg")]
    [InlineData(UnitSpec.Slope, "%")]
    public void FromInternal_round_trips_and_reports_unit(UnitSpec spec, string label)
    {
        var raw = 1.2345;
        var wire = UnitPolicy.FromInternal(raw, spec, out var unit);
        Assert.Equal(label, unit);
        Assert.Equal(raw, UnitPolicy.ToInternal(wire, spec), 12);
    }

    [Theory]
    [InlineData("m", 3.0, 3000 / 304.8)]
    [InlineData("cm", 300.0, 3000 / 304.8)]
    [InlineData("ft", 2.0, 2.0)]
    [InlineData("in", 12.0, 1.0)]
    [InlineData("internal", 5.0, 5.0)]
    [InlineData("METROS", 1.0, 1000 / 304.8)]
    public void Length_overrides(string unit, double value, double expected)
    {
        Assert.Equal(expected, UnitPolicy.ToInternal(value, UnitSpec.Length, unit), 9);
    }

    [Fact]
    public void Other_measurable_fails_closed()
    {
        var ex = Assert.Throws<ToolException>(() => UnitPolicy.ToInternal(10, UnitSpec.OtherMeasurable));
        Assert.Equal("UNIT_UNSUPPORTED", ex.Code);
        Assert.Contains("internal", ex.Hint);
        Assert.Throws<ToolException>(() => UnitPolicy.FromInternal(1, UnitSpec.OtherMeasurable, out _));
    }

    [Fact]
    public void Other_measurable_accepts_internal()
    {
        Assert.Equal(10, UnitPolicy.ToInternal(10, UnitSpec.OtherMeasurable, "internal"));
    }

    [Theory]
    [InlineData(UnitSpec.Length, "m2")]
    [InlineData(UnitSpec.Area, "mm")]
    [InlineData(UnitSpec.Angle, "mm")]
    [InlineData(UnitSpec.Slope, "deg")]
    [InlineData(UnitSpec.Dimensionless, "mm")]
    [InlineData(UnitSpec.Length, "furlong")]
    public void Incompatible_unit_is_refused(UnitSpec spec, string unit)
    {
        var ex = Assert.Throws<ToolException>(() => UnitPolicy.Decide(spec, unit, "Width"));
        Assert.Equal("UNIT_UNSUPPORTED", ex.Code);
    }
}
