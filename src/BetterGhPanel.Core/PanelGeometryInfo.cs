namespace BetterGhPanel.Core
{
    /// <summary>Immutable geometry presentation data; contains no live geometry.</summary>
    public sealed class PanelGeometryInfo
    {
        public PanelGeometryInfo(string description, double? value = null, int power = 1, string units = "units",
            string metricName = "Length", string metricLabel = "L", bool canMeasure = false, string? details = null)
        {
            Description = description;
            Value = value;
            Power = power;
            Units = units;
            MetricName = metricName;
            MetricLabel = metricLabel;
            CanMeasure = canMeasure;
            Details = details ?? description;
        }

        public string Description { get; }
        public double? Value { get; }
        public int Power { get; }
        public string Units { get; }
        public string MetricName { get; }
        public string MetricLabel { get; }
        public bool CanMeasure { get; }
        public string Details { get; }

        public string FormatCompact(PanelUnitConversion conversion, int decimals) => Description +
            (Value.HasValue ? " · " + MetricLabel + " " + FormatValue(conversion, decimals) : "");

        public string FormatDetails(PanelUnitConversion conversion, int decimals) =>
            (Value.HasValue ? MetricName + ": " + FormatValue(conversion, decimals) + "\n" : "") + Details;

        private string FormatValue(PanelUnitConversion conversion, int decimals) =>
            PanelUnits.FormatGeometry(Value!.Value, Power, Units, conversion, decimals);
    }
}
