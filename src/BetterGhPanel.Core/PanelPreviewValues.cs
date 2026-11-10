using System;
using System.Drawing;
using System.Globalization;

namespace BetterGhPanel.Core
{
    public enum PanelDotPlacement { Centre, BoxTop, BoxBottom }
    public enum PanelValuePalette { Off, Viridis, Heat, BlueWhiteRed }

    /// <summary>Captured dot value. Pending replacements can retain a published number until measured.</summary>
    public readonly struct PanelPreviewValue
    {
        public PanelPreviewValue(string metric, string unit, double? number, string text, bool pending = false)
        { Metric = metric; Unit = unit; Number = number; Text = text; Pending = pending; Key = metric + "\n" + unit; }
        public string Metric { get; }
        public string Unit { get; }
        public double? Number { get; }
        public string Text { get; }
        public bool Pending { get; }
        public string Key { get; }
        public PanelPreviewValue RetainNumber(PanelPreviewValue previous) => Pending && previous.Number.HasValue &&
            (string.IsNullOrEmpty(Metric) || Metric == previous.Metric) && (string.IsNullOrEmpty(Unit) || Unit == previous.Unit)
                ? new PanelPreviewValue(previous.Metric, previous.Unit, previous.Number, previous.Text, pending: true) : this;
        public string Label(int? order, bool values) => (order?.ToString(CultureInfo.InvariantCulture) ?? "") +
            (values && (Number.HasValue || Pending) ? (order.HasValue ? " · " : "") + (Pending ? "…" : Text) : "");
    }

    public sealed class PanelPreviewRange
    {
        public PanelPreviewRange(PanelPreviewValue value)
        { Metric = value.Metric; Unit = value.Unit; Min = Max = value.Number!.Value; MinText = MaxText = NumberText(value.Text); }
        public string Metric { get; }
        public string Unit { get; }
        public double Min { get; private set; }
        public double Max { get; private set; }
        public string MinText { get; private set; }
        public string MaxText { get; private set; }
        public void Add(double value, string? text = null)
        {
            if (value < Min) { Min = value; if (text != null) MinText = NumberText(text); }
            if (value > Max) { Max = value; if (text != null) MaxText = NumberText(text); }
        }
        private string NumberText(string text) => Unit.Length > 0 && text.EndsWith(" " + Unit, StringComparison.Ordinal)
            ? text.Substring(0, text.Length - Unit.Length - 1) : text;
        public int Bucket(double value)
        {
            if (Min == Max) return PanelPreviewValues.Buckets / 2;
            double span = Max - Min;
            double fraction = double.IsInfinity(span) ? (value / 2 - Min / 2) / (Max / 2 - Min / 2) : (value - Min) / span;
            return (int)Math.Round(Math.Max(0, Math.Min(1, fraction)) * (PanelPreviewValues.Buckets - 1));
        }
    }

    public static class PanelPreviewValues
    {
        public const int Buckets = 65;
        public const int ColourCount = 1 + 3 * Buckets;
        private static readonly int[][] Stops = {
            new[] { 0x440154, 0x3b528b, 0x21918c, 0x5ec962, 0xfde725 },
            new[] { 0x000004, 0x51127c, 0xb73779, 0xfc8961, 0xfcfdbf },
            new[] { 0x2166ac, 0x92c5de, 0xf7f7f7, 0xf4a582, 0xb2182b }
        };
        public static string PaletteLabel(PanelValuePalette palette) => palette == PanelValuePalette.BlueWhiteRed ? "Blue–white–red" : palette.ToString();
        public static string PlacementLabel(PanelDotPlacement placement) => placement == PanelDotPlacement.BoxTop ? "Box top" : placement == PanelDotPlacement.BoxBottom ? "Box bottom" : "Centre";
        public static Color Colour(PanelValuePalette palette, int bucket, bool reverse)
        {
            if (palette == PanelValuePalette.Off) return Color.FromArgb(215, 221, 215);
            double t = Math.Max(0, Math.Min(Buckets - 1, bucket)) / (double)(Buckets - 1);
            if (reverse) t = 1 - t;
            t *= 4;
            int i = Math.Min(3, (int)t); double f = t - i;
            var stops = Stops[(int)palette - 1]; int a = stops[i], b = stops[i + 1];
            int Channel(int shift) => (int)Math.Floor(((a >> shift) & 255) + (((b >> shift) & 255) - ((a >> shift) & 255)) * f + .5);
            return Color.FromArgb(Channel(16), Channel(8), Channel(0));
        }
        public static Color TextColour(Color fill)
        {
            double Linear(byte channel) { double n = channel / 255.0; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
            double l = .2126 * Linear(fill.R) + .7152 * Linear(fill.G) + .0722 * Linear(fill.B);
            return (l + .05) / .05 >= 1.05 / (l + .05) ? Color.Black : Color.White;
        }

        public static PanelPreviewValue Sample(PanelSnapshot snapshot, PanelRow row, PanelSearchMode search, PanelGeometryInfo? measured, int decimals, double factor)
        {
            if (!row.IsValid || row.IsNull) return default;
            var geometry = measured ?? row.GeometryInfo;
            var mode = search;
            bool explicitMetric = mode == PanelSearchMode.Length || mode == PanelSearchMode.Area || mode == PanelSearchMode.Volume ||
                mode == PanelSearchMode.X || mode == PanelSearchMode.Y || mode == PanelSearchMode.Z ||
                PanelProperties.IsProperty(mode) && PanelProperties.Kind(mode) == PanelPropertyKind.Number;
            if (!explicitMetric)
            {
                if (geometry == null) return default;
                if (geometry.CanMeasure && !geometry.MeasurementUnavailable && !geometry.Value.HasValue)
                    return new PanelPreviewValue(geometry.MetricName, "", null, "", pending: true);
                if (geometry.MetricName == "Radius" && geometry.Value.HasValue)
                {
                    string radiusText = PanelUnits.FormatGeometry(geometry.Value.Value, geometry.Power, geometry.Units, snapshot.UnitConversion, decimals, roundUpFactor: factor);
                    string numberText = PanelUnits.FormatGeometry(geometry.Value.Value, geometry.Power, geometry.Units, snapshot.UnitConversion, decimals, false, factor);
                    return PanelUnits.TryParseOperand(numberText, out double radius)
                        ? new PanelPreviewValue("Radius", PanelPropertyValue.Numeric(1, geometry.Power, geometry.Units).UnitSymbol(snapshot.UnitConversion), radius, radiusText) : default;
                }
                if (geometry.MetricName != "Length" && geometry.MetricName != "Area" && geometry.MetricName != "Volume") return default;
                mode = geometry.MetricName == "Length" ? PanelSearchMode.Length : geometry.MetricName == "Area" ? PanelSearchMode.Area : PanelSearchMode.Volume;
            }
            string metric = PanelProperties.IsProperty(mode) ? PanelProperties.Label(mode) : mode.ToString();
            string unit = PanelFilterValue.UnitSymbol(snapshot, row, mode, geometry);
            // Vectors are dimensionless even in a document with model units.
            bool axis = mode == PanelSearchMode.X || mode == PanelSearchMode.Y || mode == PanelSearchMode.Z;
            bool vector = axis && row.TypeName.IndexOf("Vector", StringComparison.OrdinalIgnoreCase) >= 0;
            if (vector && row.Components != null)
            {
                var components = row.Components;
                var scalar = PanelPropertyValue.Numeric(mode == PanelSearchMode.X ? components.X : mode == PanelSearchMode.Y ? components.Y : components.Z);
                double number = scalar.Presented(PanelUnitConversion.None, decimals, 0);
                if (double.IsNaN(number) || double.IsInfinity(number)) return default;
                return new PanelPreviewValue("Vector " + metric, "", number, scalar.Format(PanelUnitConversion.None, decimals, true));
            }
            var value = PanelFilterValue.OutputValue(snapshot, row, mode, geometry, decimals, factor);
            string text = PanelFilterValue.Format(snapshot, row, mode, decimals, geometry, roundUpFactor: factor);
            if (value?.Kind == PanelPropertyKind.Number && !double.IsNaN(value.Number) && !double.IsInfinity(value.Number))
            {
                double number = value.Number;
                if (!axis && !PanelProperties.IsProperty(mode) && decimals < 0 &&
                    PanelUnits.TryParseOperand(PanelFilterValue.Format(snapshot, row, mode, decimals, geometry, false, factor), out double displayed)) number = displayed;
                if (axis && unit.Length > 0) text += " " + unit;
                return new PanelPreviewValue(metric, unit, number, text);
            }
            return new PanelPreviewValue(metric, unit, null, "", text == "measuring…");
        }
    }
}
