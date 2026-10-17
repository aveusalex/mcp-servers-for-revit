using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>
    /// Wire units ↔ Revit internal units by spec (ForgeTypeId): length mm, area m², volume m³, angle degrees,
    /// slope %; plain numbers unchanged. An unknown measurable spec fails closed with UNIT_UNSUPPORTED. The decision
    /// itself lives in the pure <see cref="UnitPolicy"/>; this class only maps SpecTypeId values to <see cref="UnitSpec"/>.
    /// </summary>
    public sealed class UnitsService
    {
        // Specs Revit stores in feet / ft² / ft³ / radians / rise-over-run. Anything not listed is OtherMeasurable.
        private static readonly Dictionary<string, UnitSpec> Known = BuildKnown();

        public UnitSpec Classify(ForgeTypeId spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.TypeId)) return UnitSpec.OtherMeasurable;
            return Known.TryGetValue(StripVersion(spec.TypeId), out var s) ? s : UnitSpec.OtherMeasurable;
        }

        /// <summary>Spec of a parameter's definition (empty spec → OtherMeasurable, i.e. fail-closed).</summary>
        public UnitSpec Classify(Parameter p) => Classify(SpecOf(p));

        public static ForgeTypeId SpecOf(Parameter p)
        {
            try { return p?.Definition?.GetDataType(); }
            catch { return null; }
        }

        /// <summary>Wire value → internal. <paramref name="unit"/> null = wire default; "internal" = raw.</summary>
        public double ToInternal(double value, ForgeTypeId spec, string unit = null, string paramName = null) =>
            UnitPolicy.ToInternal(value, Classify(spec), unit, paramName);

        /// <summary>Internal → wire value. Throws UNIT_UNSUPPORTED for specs without a known conversion.</summary>
        public double FromInternal(double raw, ForgeTypeId spec) => UnitPolicy.FromInternal(raw, Classify(spec), out _);

        /// <summary>Internal → wire value, or false (raw returned, unit "internal") when the spec has no conversion.</summary>
        public bool TryFromInternal(double raw, ForgeTypeId spec, out double value, out string unit)
        {
            var kind = Classify(spec);
            if (kind == UnitSpec.OtherMeasurable)
            {
                value = raw;
                unit = UnitPolicy.Internal;
                return false;
            }
            value = UnitPolicy.FromInternal(raw, kind, out unit);
            return true;
        }

        private static string StripVersion(string typeId)
        {
            var dash = typeId.LastIndexOf('-');
            return dash > 0 ? typeId.Substring(0, dash) : typeId;
        }

        private static Dictionary<string, UnitSpec> BuildKnown()
        {
            var d = new Dictionary<string, UnitSpec>();

            void Add(ForgeTypeId id, UnitSpec s)
            {
                if (id != null && !string.IsNullOrEmpty(id.TypeId)) d[StripVersion(id.TypeId)] = s;
            }

            Add(SpecTypeId.Number, UnitSpec.Dimensionless);
            Add(SpecTypeId.Currency, UnitSpec.Dimensionless);
            Add(SpecTypeId.Int.Integer, UnitSpec.Dimensionless);

            Add(SpecTypeId.Length, UnitSpec.Length);
            Add(SpecTypeId.SheetLength, UnitSpec.Length);
            Add(SpecTypeId.PipeSize, UnitSpec.Length);
            Add(SpecTypeId.DuctSize, UnitSpec.Length);
            Add(SpecTypeId.CableTraySize, UnitSpec.Length);
            Add(SpecTypeId.ConduitSize, UnitSpec.Length);
            Add(SpecTypeId.BarDiameter, UnitSpec.Length);
            Add(SpecTypeId.SectionDimension, UnitSpec.Length);
            Add(SpecTypeId.ReinforcementCover, UnitSpec.Length);
            Add(SpecTypeId.ReinforcementLength, UnitSpec.Length);
            Add(SpecTypeId.Displacement, UnitSpec.Length);

            Add(SpecTypeId.Area, UnitSpec.Area);
            Add(SpecTypeId.SectionArea, UnitSpec.Area);
            Add(SpecTypeId.Volume, UnitSpec.Volume);
            Add(SpecTypeId.Angle, UnitSpec.Angle);
            Add(SpecTypeId.RotationAngle, UnitSpec.Angle);
            Add(SpecTypeId.Slope, UnitSpec.Slope);
            return d;
        }
    }
}
