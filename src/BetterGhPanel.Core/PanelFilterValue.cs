using System;
using System.Globalization;

namespace BetterGhPanel.Core
{
    /// <summary>The same metric drives comparisons and the optional value display.</summary>
    public static class PanelFilterValue
    {
        /// <summary>Presented typed value; null preserves an unavailable row's output slot.</summary>
        public static PanelPropertyValue? OutputValue(PanelSnapshot snapshot, PanelRow row, PanelSearchMode mode, PanelGeometryInfo? measured, int decimals = PanelUnits.AutomaticDecimals, double roundUpFactor = 0)
        {
            if (mode == PanelSearchMode.ListLength) return PanelPropertyValue.Numeric(snapshot.BranchItemCounts[row.Path]);
            if (PanelProperties.IsProperty(mode))
            {
                if (!PanelProperties.TryGet(row, mode, measured, out var property) || property == null) return null;
                if (property.Kind != PanelPropertyKind.Number) return property;
                double number = property.Converted(snapshot.UnitConversion);
                return IsFinite(number) ? PanelPropertyValue.Numeric(PanelUnits.Transform(number, PanelUnitConversion.None, roundUpFactor, decimals)) : null;
            }
            bool available = mode == PanelSearchMode.Value || mode == PanelSearchMode.X || mode == PanelSearchMode.Y || mode == PanelSearchMode.Z
                ? TryScalarValue(row, mode, snapshot.UnitConversion, out double value)
                : TryGeometryValue(row, mode, snapshot.UnitConversion, measured, out value);
            return available ? PanelPropertyValue.Numeric(PanelUnits.Transform(value, PanelUnitConversion.None, mode == PanelSearchMode.Value ? 0 : roundUpFactor, decimals)) : null;
        }

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
            PanelGeometryInfo? measured = null, bool includeUnits = true, double roundUpFactor = 0)
        {
            if (mode == PanelSearchMode.ListLength)
                return snapshot.BranchItemCounts.TryGetValue(row.Path, out int count) ? count.ToString(CultureInfo.InvariantCulture) : "unavailable";
            if (PanelProperties.IsProperty(mode))
            {
                if (PanelProperties.TryGet(row, mode, measured, out var property))
                    return property?.Format(snapshot.UnitConversion, decimals, includeUnits, roundUpFactor) ?? ((measured ?? row.GeometryInfo)?.MeasurementUnavailable == true ? "unavailable" : "measuring…");
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
                    ? PanelUnits.Format(PanelUnits.Transform(component, PanelUnitConversion.None, roundUpFactor, decimals), PanelUnitConversion.None, decimals, CultureInfo.InvariantCulture) : "n/a";
            var geometry = measured ?? row.GeometryInfo;
            if (geometry == null || !MatchesGeometry(mode, geometry)) return "n/a";
            if (!TryGeometryValue(row, mode, snapshot.UnitConversion, geometry, out _))
                return geometry.CanMeasure && !geometry.MeasurementUnavailable && !MetricValue(mode, geometry).HasValue ? "measuring…" : "unavailable";
            return PanelUnits.FormatGeometry(RawValue(mode, geometry), mode == PanelSearchMode.Area ? 2 : geometry.Power,
                geometry.Units, snapshot.UnitConversion, decimals, includeUnits, roundUpFactor);
        }

        public static string RoundingDescription(PanelSnapshot snapshot, PanelRow row, PanelQueryOptions options, PanelGeometryInfo? measured = null)
        {
            if (row.IsNull || !row.IsValid) return "";
            PanelPropertyValue raw;
            var mode = options.SearchMode;
            var geometry = measured ?? row.GeometryInfo;
            if ((!options.DisplayFilterValue || mode == PanelSearchMode.Value) && row.RawNumericValue.HasValue)
                raw = PanelPropertyValue.Numeric(row.RawNumericValue.Value);
            else if (options.DisplayFilterValue && PanelProperties.IsProperty(mode))
            {
                if (!PanelProperties.TryGet(row, mode, measured, out var property) || property?.Kind != PanelPropertyKind.Number) return "";
                raw = property;
            }
            else if (options.DisplayFilterValue && (mode == PanelSearchMode.X || mode == PanelSearchMode.Y || mode == PanelSearchMode.Z))
            {
                if (!TryScalarValue(row, mode, snapshot.UnitConversion, out double component)) return "";
                raw = PanelPropertyValue.Numeric(component);
            }
            else
            {
                if (mode == PanelSearchMode.ListLength && options.DisplayFilterValue || geometry?.Value == null) return "";
                raw = PanelPropertyValue.Numeric(options.DisplayFilterValue ? RawValue(mode, geometry) : geometry.Value.Value,
                    options.DisplayFilterValue && mode == PanelSearchMode.Area ? 2 : geometry.Power, geometry.Units);
            }
            double number = raw.Converted(snapshot.UnitConversion);
            double factored = PanelUnits.Transform(number, PanelUnitConversion.None, options.RoundUpFactor);
            double shown = PanelUnits.Transform(number, PanelUnitConversion.None, options.RoundUpFactor, options.DecimalPlaces);
            if (shown == number || !IsFinite(number) || !IsFinite(shown)) return "";
            string plain = raw.Format(snapshot.UnitConversion, PanelUnits.AutomaticDecimals, false);
            string suffix = raw.Format(snapshot.UnitConversion, PanelUnits.AutomaticDecimals, true).Substring(plain.Length);
            if (row.RawNumericValue.HasValue && (!options.DisplayFilterValue || mode == PanelSearchMode.Value))
                suffix = PanelUnits.Symbol(snapshot.UnitConversion).Length == 0 ? "" : " " + PanelUnits.Symbol(snapshot.UnitConversion);
            string reason = factored != number ? " · rounded up by " + options.RoundUpFactor.ToString("R", CultureInfo.InvariantCulture) : "";
            if (shown != factored) reason += " · " + options.DecimalPlaces.ToString(CultureInfo.InvariantCulture) + " decimal places";
            return "Shown " + PanelUnits.Format(shown, PanelUnitConversion.None, options.DecimalPlaces) + suffix +
                " · raw " + number.ToString("R", CultureInfo.InvariantCulture) + suffix + reason;
        }

        private static double? MetricValue(PanelSearchMode mode, PanelGeometryInfo geometry) =>
            mode == PanelSearchMode.Area && geometry.HasArea ? geometry.AreaValue : geometry.Value;

        // Length means circumference even when the source summary shows radius.
        private static double RawValue(PanelSearchMode mode, PanelGeometryInfo geometry) =>
            MetricValue(mode, geometry)!.Value * (mode == PanelSearchMode.Length && string.Equals(geometry.MetricName, "Radius", StringComparison.OrdinalIgnoreCase) ? 2 * Math.PI : 1);

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
