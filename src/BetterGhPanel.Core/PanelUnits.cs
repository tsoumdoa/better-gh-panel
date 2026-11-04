using System;
using System.Globalization;

namespace BetterGhPanel.Core
{
    public enum PanelUnitConversion
    {
        None = 0,
        MillimetersToMeters = 1,
        MetersToMillimeters = 2,
        SquareMillimetersToSquareMeters = 3,
        SquareMetersToSquareMillimeters = 4,
        RadiansToDegrees = 5,
        DegreesToRadians = 6,
        // Append new modes so existing saved definitions retain their enum values.
        CubicMillimetersToCubicMeters = 7,
        CubicMetersToCubicMillimeters = 8
    }

    public static class PanelUnits
    {
        public const int AutomaticDecimals = -1;
        public const int MaximumDecimals = 12;
        // Indexed by the persisted enum values. Each conversion's arithmetic,
        // presentation and optional geometry applicability live in one place.
        private static readonly Conversion[] Conversions =
        {
            new Conversion("No conversion", "", 1),
            new Conversion("mm → m", "m", 1000, divide: true, sourceUnits: "mm", power: 1),
            new Conversion("m → mm", "mm", 1000, sourceUnits: "m", power: 1),
            new Conversion("mm² → m²", "m²", 1000000, divide: true, sourceUnits: "mm", power: 2),
            new Conversion("m² → mm²", "mm²", 1000000, sourceUnits: "m", power: 2),
            new Conversion("rad → deg", "°", 180 / Math.PI),
            new Conversion("deg → rad", "rad", Math.PI / 180),
            new Conversion("mm³ → m³", "m³", 1000000000, divide: true, sourceUnits: "mm", power: 3),
            new Conversion("m³ → mm³", "mm³", 1000000000, sourceUnits: "m", power: 3)
        };

        public static int ConversionCount => Conversions.Length;

        public static double Convert(double value, PanelUnitConversion conversion)
        {
            var definition = Definition(conversion);
            // Keep division explicit: multiplying by its reciprocal can change
            // floating-point values carried by the output wire.
            return definition.Scale == 1 ? value : definition.Divide ? value / definition.Scale : value * definition.Scale;
        }

        public static bool IsRoundingFactor(double factor) => factor > 0 && !double.IsInfinity(factor);

        /// <summary>Rounds numeric output toward positive infinity after unit conversion. Zero disables rounding.</summary>
        public static double Transform(double value, PanelUnitConversion conversion, double roundUpFactor = 0, int decimals = AutomaticDecimals)
        {
            value = RoundUp(Convert(value, conversion), roundUpFactor);
            if (!double.IsNaN(value) && !double.IsInfinity(value) && decimals >= 0)
            {
                decimals = Math.Min(MaximumDecimals, decimals);
                try
                {
                    if (TryDecimal(value, out decimal number)) value = (double)Math.Round(number, decimals, MidpointRounding.AwayFromZero);
                    else value = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
                }
                catch (OverflowException) { value = Math.Round(value, decimals, MidpointRounding.AwayFromZero); }
            }
            return value == 0 ? 0 : value;
        }

        // A double-to-decimal cast retains only 15 significant digits on Framework.
        // Parse its round-trip text so large integers and decimal midpoints survive.
        private static bool TryDecimal(double value, out decimal number) => decimal.TryParse(
            value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number);

        private static double RoundUp(double value, double roundUpFactor)
        {
            if (!IsRoundingFactor(roundUpFactor) || double.IsNaN(value) || double.IsInfinity(value)) return value;
            try
            {
                if (TryDecimal(value, out decimal number) && TryDecimal(roundUpFactor, out decimal factor) && factor > 0)
                {
                    decimal remainder = number % factor;
                    return (double)(remainder == 0 ? number : number - remainder + (number > 0 ? factor : 0));
                }
            }
            catch (OverflowException) { }
            double quotient = value / roundUpFactor;
            if (Math.Abs(quotient) >= 4503599627370496d) return value;
            if (quotient == 0 && value > 0) return roundUpFactor;
            return Math.Ceiling(quotient) * roundUpFactor;
        }

        public static string Label(PanelUnitConversion conversion) => Definition(conversion).Label;

        public static string Symbol(PanelUnitConversion conversion) => Definition(conversion).Symbol;

        private static Conversion Definition(PanelUnitConversion conversion) =>
            Conversions[(uint)conversion < (uint)Conversions.Length ? (int)conversion : 0];

        private sealed class Conversion
        {
            internal Conversion(string label, string symbol, double scale, bool divide = false, string? sourceUnits = null, int power = 0)
            {
                Label = label;
                Symbol = symbol;
                Scale = scale;
                Divide = divide;
                SourceUnits = sourceUnits;
                Power = power;
            }

            internal readonly string Label, Symbol;
            internal readonly double Scale;
            internal readonly bool Divide;
            internal readonly string? SourceUnits;
            internal readonly int Power;
        }

        // Geometry retains its model units. Convert only a matching source unit and
        // dimension; numeric output conversion must never scale the geometry itself.
        public static string FormatGeometry(double value, int power, string units, PanelUnitConversion conversion, int decimals,
            bool includeUnits = true, double roundUpFactor = 0)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return "unavailable";
            var matchingConversion = GeometryConversion(power, units, conversion);
            if (matchingConversion != PanelUnitConversion.None)
            {
                var converted = Transform(value, matchingConversion, roundUpFactor, decimals);
                return double.IsInfinity(converted) ? "unavailable" : Format(converted,
                    includeUnits ? matchingConversion : PanelUnitConversion.None, decimals, "G6");
            }
            value = Transform(value, PanelUnitConversion.None, roundUpFactor, decimals);
            if (double.IsNaN(value) || double.IsInfinity(value)) return "unavailable";
            return Format(value, PanelUnitConversion.None, decimals, "G6") +
                (includeUnits ? " " + units + (power == 2 ? "²" : power == 3 ? "³" : "") : "");
        }

        /// <summary>Converts an unrounded geometry metric exactly as its displayed units are converted.</summary>
        public static double ConvertGeometry(double value, int power, string units, PanelUnitConversion conversion) =>
            Convert(value, GeometryConversion(power, units, conversion));

        private static PanelUnitConversion GeometryConversion(int power, string units, PanelUnitConversion conversion)
        {
            var definition = Definition(conversion);
            return definition.SourceUnits != null && definition.SourceUnits == units && definition.Power == power
                ? conversion : PanelUnitConversion.None;
        }

        // Automatic keeps the compact display without rounding the wire.
        public static string Format(double value, PanelUnitConversion conversion, int decimals,
            string automaticFormat = "G12")
        {
            decimals = Math.Max(AutomaticDecimals, Math.Min(MaximumDecimals, decimals));
            value = Transform(value, PanelUnitConversion.None, 0, decimals);
            if (double.IsNaN(value) || double.IsInfinity(value)) return "unavailable";
            string text = decimals >= 0 && TryDecimal(value, out decimal number)
                ? Math.Round(number, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, CultureInfo.InvariantCulture)
                : value.ToString(decimals < 0 ? automaticFormat : "F" + decimals, CultureInfo.InvariantCulture);
            string symbol = Symbol(conversion);
            return symbol.Length == 0 ? text : text + " " + symbol;
        }
    }
}
