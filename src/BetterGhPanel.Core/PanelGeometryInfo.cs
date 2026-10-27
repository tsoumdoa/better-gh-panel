namespace BetterGhPanel.Core
{
    /// <summary>Immutable geometry presentation data; contains no live geometry.</summary>
    public sealed class PanelGeometryInfo
    {
        private readonly double? _additionalLength;

        public PanelGeometryInfo(string description, double? value = null, int power = 1, string units = "units",
            string metricName = "Length", string metricLabel = "L", bool canMeasure = false, string? details = null,
            bool measurementUnavailable = false)
            : this(description, value, power, units, metricName, metricLabel, canMeasure, details, measurementUnavailable, null)
        {
        }

        private PanelGeometryInfo(string description, double? value, int power, string units,
            string metricName, string metricLabel, bool canMeasure, string? details, bool measurementUnavailable, double? additionalLength)
        {
            Description = description;
            Value = value;
            Power = power;
            Units = units;
            MetricName = metricName;
            MetricLabel = metricLabel;
            CanMeasure = canMeasure;
            Details = details ?? description;
            MeasurementUnavailable = measurementUnavailable;
            _additionalLength = additionalLength;
        }

        public string Description { get; }
        public double? Value { get; }
        public int Power { get; }
        public string Units { get; }
        public string MetricName { get; }
        public string MetricLabel { get; }
        public bool CanMeasure { get; }
        public string Details { get; }
        /// <summary>A completed measurement failed; unlike a pending status, this belongs to stable row presentation.</summary>
        public bool MeasurementUnavailable { get; }

        public PanelGeometryInfo WithUnavailableMeasurement() =>
            new PanelGeometryInfo(Description, null, Power, Units, MetricName, MetricLabel, false, Details, true, _additionalLength);

        /// <summary>Includes a second, unit-aware length in details without changing the compact metric.</summary>
        public PanelGeometryInfo WithAdditionalLength(double length) =>
            new PanelGeometryInfo(Description, Value, Power, Units, MetricName, MetricLabel, CanMeasure, Details, MeasurementUnavailable, length);

        public string FormatCompact(PanelUnitConversion conversion, int decimals) => Description +
            (Value.HasValue ? " · " + MetricLabel + " " + FormatValue(conversion, decimals) : "");

        public string FormatDetails(PanelUnitConversion conversion, int decimals) =>
            (Value.HasValue ? MetricName + ": " + FormatValue(conversion, decimals) + "\n" : "") +
            (_additionalLength.HasValue ? "Length: " + PanelUnits.FormatGeometry(_additionalLength.Value, 1, Units, conversion, decimals) + "\n" : "") +
            Details;

        private string FormatValue(PanelUnitConversion conversion, int decimals) =>
            PanelUnits.FormatGeometry(Value!.Value, Power, Units, conversion, decimals);
    }
}
