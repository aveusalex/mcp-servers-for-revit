using System;

// NOTE (merge): front F1 owns the canonical definitions of these schema attributes. They are defined here only so
// the F2 pilot tools compile on their own; signatures match the convention agreed with F3's ManifestGen
// (matched by name). Drop this file when F1's versions land.
namespace RevitMCPCommandSet.Framework
{
    /// <summary>Wire unit of a numeric argument: "mm", "m2", "m3" or "deg".</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class UnitAttribute : Attribute
    {
        public UnitAttribute(string unit)
        {
            Unit = unit;
        }

        public string Unit { get; }
    }

    /// <summary>Allowed values of a string argument (or of the items of a string list).</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class EnumValuesAttribute : Attribute
    {
        public EnumValuesAttribute(params string[] values)
        {
            Values = values;
        }

        public string[] Values { get; }
    }
}
