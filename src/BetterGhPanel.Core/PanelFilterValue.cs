using System;
using System.Globalization;

namespace BetterGhPanel.Core
{
    /// <summary>The same metric drives comparisons and the optional value display.</summary>
    public static class PanelFilterValue
    {
        public static bool MatchesGeometry(PanelSearchMode mode, PanelGeometryInfo geometry) =>
            PanelProperties.IsProperty(mode) ? geometry.Properties.ContainsKey(mode) :
            mode >= PanelSearchMode.Length && mode <= PanelSearchMode.Volume &&
            (mode == PanelSearchMode.Area && geometry.HasArea ||
                string.Equals(geometry.MetricName, mode.ToString(), StringComparison.OrdinalIgnoreCase) ||
                mode == PanelSearchMode.Length && string.Equals(geometry.MetricName, "Radius", StringComparison.OrdinalIgnoreCase));

        public static bool TryScalarValue(PanelRow row, PanelSearchMode mode, PanelUnitConversion conversion, out double value)
        {
            value = 0;
            if (row.IsNull || !row.IsValid) return false;
            if (mode == PanelSearchMode.X || mode == PanelSearchMode.Y || mode == PanelSearchMode.Z)
            {
                var components = row.Components;
                if (components == null) return false;
                value = PanelUnits.ConvertGeometry(mode == PanelSearchMode.X ? components.X : mode == PanelSearchMode.Y ? components.Y : components.Z,
                    1, components.Units, conversion);
            }
            else
            {
                if (!row.NumericValue.HasValue) return false;
                value = row.NumericValue.Value;
            }
            return IsFinite(value);
        }

        public static bool TryGeometryValue(PanelRow row, PanelSearchMode mode, PanelUnitConversion conversion,
            PanelGeometryInfo? measured, out double value)
        {
            value = 0;
            if (PanelProperties.IsProperty(mode))
            {
                if (!PanelProperties.TryGet(row, mode, measured, out var property) || property == null || property.Kind != PanelPropertyKind.Number) return false;
                value = property.Converted(conversion);
                return IsFinite(value);
            }
            var geometry = measured ?? row.GeometryInfo;
            if (row.IsNull || !row.IsValid || geometry == null || !MatchesGeometry(mode, geometry) ||
                !MetricValue(mode, geometry).HasValue) return false;
            value = RawValue(mode, geometry);
            if (!IsFinite(value) || value < 0) return false;
            value = PanelUnits.ConvertGeometry(value, mode == PanelSearchMode.Area ? 2 : geometry.Power, geometry.Units, conversion);
            return IsFinite(value);
        }

        public static string Format(PanelSnapshot snapshot, PanelRow row, PanelSearchMode mode, int decimals,
            PanelGeometryInfo? measured = null, bool includeUnits = true)
        {
            if (mode == PanelSearchMode.ListLength)
                return snapshot.BranchItemCounts.TryGetValue(row.Path, out int count) ? count.ToString(CultureInfo.InvariantCulture) : "unavailable";
            if (PanelProperties.IsProperty(mode))
            {
                if (PanelProperties.TryGet(row, mode, measured, out var property))
                    return property?.Format(snapshot.UnitConversion, decimals, includeUnits) ?? ((measured ?? row.GeometryInfo)?.MeasurementUnavailable == true ? "unavailable" : "measuring…");
                if (row.IsValid && !row.IsNull) return "n/a";
            }
            if (row.IsNull) return "null";
            if (row.Validity == PanelValidity.Unverified) return "unverified";
            if (row.Validity == PanelValidity.VerifiedInvalid) return "invalid";
            if (mode == PanelSearchMode.Value)
                return row.NumericValue.HasValue && IsFinite(row.NumericValue.Value)
                    ? PanelUnits.Format(row.NumericValue.Value, includeUnits ? snapshot.UnitConversion : PanelUnitConversion.None,
                        decimals, CultureInfo.InvariantCulture) : "n/a";
            if (mode == PanelSearchMode.X || mode == PanelSearchMode.Y || mode == PanelSearchMode.Z)
                return TryScalarValue(row, mode, snapshot.UnitConversion, out double component)
                    ? PanelUnits.Format(component, PanelUnitConversion.None, decimals, CultureInfo.InvariantCulture) : "n/a";
            var geometry = measured ?? row.GeometryInfo;
            if (geometry == null || !MatchesGeometry(mode, geometry)) return "n/a";
            if (!TryGeometryValue(row, mode, snapshot.UnitConversion, geometry, out _))
                return geometry.CanMeasure && !geometry.MeasurementUnavailable && !MetricValue(mode, geometry).HasValue ? "measuring…" : "unavailable";
            return PanelUnits.FormatGeometry(RawValue(mode, geometry), mode == PanelSearchMode.Area ? 2 : geometry.Power,
                geometry.Units, snapshot.UnitConversion, decimals, includeUnits);
        }

        private static double? MetricValue(PanelSearchMode mode, PanelGeometryInfo geometry) =>
            mode == PanelSearchMode.Area && geometry.HasArea ? geometry.AreaValue : geometry.Value;

        // Length means circumference even when the source summary shows radius.
        private static double RawValue(PanelSearchMode mode, PanelGeometryInfo geometry) =>
            MetricValue(mode, geometry)!.Value * (mode == PanelSearchMode.Length && string.Equals(geometry.MetricName, "Radius", StringComparison.OrdinalIgnoreCase) ? 2 * Math.PI : 1);

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
