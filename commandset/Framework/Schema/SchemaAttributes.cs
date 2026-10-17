using System;
using System.Collections.Generic;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// Unit of a numeric argument as it travels on the wire. A numeric measure (length, area, angle...)
    /// argument must declare one (the contract test fails otherwise). Dimensionless numbers (ratios,
    /// scale factors) need none. Matched by name by ManifestGen: keep the class name and ctor.
    /// </summary>
    /// <remarks>No Revit dependency: shared with the manifest generator and the framework tests.</remarks>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
    public sealed class UnitAttribute : Attribute
    {
        /// <summary>Units allowed on the wire: millimetres, square metres, cubic metres, or degrees.</summary>
        public static readonly IReadOnlyCollection<string> Allowed = new[] { "mm", "m2", "m3", "deg" };

        public UnitAttribute(string unit)
        {
            Unit = unit;
        }

        public string Unit { get; }
    }

    /// <summary>
    /// Closed list of accepted values for a string argument (or for the items of a string array).
    /// Becomes <c>enum</c> in the JSON schema and is checked when the arguments are bound.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
    public sealed class EnumValuesAttribute : Attribute
    {
        public EnumValuesAttribute(params string[] values)
        {
            Values = values ?? new string[0];
        }

        public string[] Values { get; }
    }

    /// <summary>
    /// Marks a tool that can run without a resolved target document (e.g. get_session_context).
    /// Every other tool fails with DOCUMENT_NOT_FOUND when the broker's target is gone.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class NoDocumentRequiredAttribute : Attribute
    {
    }
}
