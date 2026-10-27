using System;
using System.Globalization;

namespace BetterGhPanel.Core
{
    /// <summary>The same metric drives comparisons and the optional value display.</summary>
    public static class PanelFilterValue
    {
        public static bool MatchesGeometry(PanelSearchMode mode, PanelGeometryInfo geometry) =>
            mode >= PanelSearchMode.Length && mode <= PanelSearchMode.Volume &&
            (string.Equals(geometry.MetricName, mode.ToString(), StringComparison.OrdinalIgnoreCase) ||
                mode == PanelSearchMode.Length && string.Equals(geometry.MetricName, "Radius", StringComparison.OrdinalIgnoreCase));

        public static bool TryGeometryValue(PanelRow row, PanelSearchMode mode, PanelUnitConversion conversion,
            PanelGeometryInfo? measured, out double value)
        {
            value = 0;
            var geometry = measured ?? row.GeometryInfo;
            if (row.IsNull || !row.IsValid || geometry == null || !MatchesGeometry(mode, geometry) ||
                !geometry.Value.HasValue || !IsFinite(geometry.Value.Value) || geometry.Value.Value < 0) return false;
            value = RawValue(mode, geometry);
            value = PanelUnits.ConvertGeometry(value, geometry.Power, geometry.Units, conversion);
            return IsFinite(value);
        }

        public static string Format(PanelSnapshot snapshot, PanelRow row, PanelSearchMode mode, int decimals,
            PanelGeometryInfo? measured = null, bool includeUnits = true)
        {
            if (mode == PanelSearchMode.ListLength)
                return snapshot.BranchItemCounts.TryGetValue(row.Path, out int count) ? count.ToString(CultureInfo.InvariantCulture) : "unavailable";
            if (row.IsNull) return "null";
            if (row.Validity == PanelValidity.Unverified) return "unverified";
            if (row.Validity == PanelValidity.VerifiedInvalid) return "invalid";
            if (mode == PanelSearchMode.Value)
                return row.NumericValue.HasValue && IsFinite(row.NumericValue.Value)
                    ? PanelUnits.Format(row.NumericValue.Value, includeUnits ? snapshot.UnitConversion : PanelUnitConversion.None,
                        decimals, CultureInfo.InvariantCulture) : "n/a";
            var geometry = measured ?? row.GeometryInfo;
            if (geometry == null || !MatchesGeometry(mode, geometry)) return "n/a";
            if (!TryGeometryValue(row, mode, snapshot.UnitConversion, geometry, out _))
                return geometry.CanMeasure && !geometry.MeasurementUnavailable && !geometry.Value.HasValue ? "measuring…" : "unavailable";
            return PanelUnits.FormatGeometry(RawValue(mode, geometry), geometry.Power, geometry.Units, snapshot.UnitConversion, decimals, includeUnits);
        }

        // Length means circumference even when the source summary shows radius.
        private static double RawValue(PanelSearchMode mode, PanelGeometryInfo geometry) =>
            geometry.Value!.Value * (mode == PanelSearchMode.Length && string.Equals(geometry.MetricName, "Radius", StringComparison.OrdinalIgnoreCase) ? 2 * Math.PI : 1);

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
