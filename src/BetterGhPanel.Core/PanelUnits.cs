using System;
using System.Threading;

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
            new Conversion("rad → deg", "°", 180 / Math.PI, sourceUnits: "rad", power: -1),
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
            double converted = Convert(value, conversion);
            value = RoundUp(converted, roundUpFactor);
            if (!double.IsNaN(value) && !double.IsInfinity(value) && decimals >= 0 && Math.Abs(value) < PanelNumber.IntegerBoundary)
                value = IsRoundingFactor(roundUpFactor)
                    ? PanelNumber.FactorDecimalCeiling(converted, value, roundUpFactor, Math.Min(MaximumDecimals, decimals))
                    : PanelNumber.ToDouble(PanelNumber.RoundedDecimal(value, Math.Min(MaximumDecimals, decimals)));
            return value == 0 ? 0 : value;
        }

        public static bool TryParseOperand(string text, out double value) => PanelNumber.TryParse(text, out value);

        // Settings usually share one factor across the entire solve/query. Cache
        // its guarded decimal cast, rather than converting it for every value.
        private sealed class Factor
        {
            internal readonly double Value;
            internal readonly decimal Decimal;
            internal readonly bool Faithful;
            internal Factor(double value) { Value = value; Faithful = PanelNumber.FaithfulDecimal(value, out Decimal); }
        }
        private static Factor _factor = new Factor(0);

        private static double RoundUp(double value, double roundUpFactor)
        {
            if (!IsRoundingFactor(roundUpFactor) || double.IsNaN(value) || double.IsInfinity(value)) return value;
            long bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
            int exponent = (int)((bits >> 52) & 2047);
            double resolution = exponent <= 52 ? BitConverter.Int64BitsToDouble(1L << Math.Max(0, exponent - 1)) : BitConverter.Int64BitsToDouble((long)(exponent - 52) << 52);
            if (roundUpFactor < resolution) return value;
            var prepared = Volatile.Read(ref _factor);
            if (prepared.Value != roundUpFactor) { prepared = new Factor(roundUpFactor); Volatile.Write(ref _factor, prepared); }
            try
            {
                if (prepared.Faithful && prepared.Decimal > 0 && Math.Abs(value) < 79228162514264337593543950336d)
                {
                    decimal number = (decimal)value, factor = prepared.Decimal;
                    if (number == 0 && value > 0) return roundUpFactor;
                    if ((double)number != value) return DoubleCeil(value, roundUpFactor);
                    decimal remainder = number % factor;
                    if (remainder == 0) return value;
                    double rounded = PanelNumber.ToDouble(number - remainder + (number > 0 ? factor : 0));
                    return rounded >= value ? rounded : DoubleCeil(value, roundUpFactor);
                }
            }
            catch (OverflowException) { }
            return DoubleCeil(value, roundUpFactor);
        }

        private static double DoubleCeil(double value, double roundUpFactor)
        {
            double quotient = value / roundUpFactor;
            if (Math.Abs(quotient) >= 4503599627370496d) return value;
            if (quotient == 0 && value > 0) return roundUpFactor;
            return Math.Max(value, Math.Ceiling(quotient) * roundUpFactor);
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

        /// <summary>Runtime-independent round-trip text for a finite unique numeric value.</summary>
        public static string FormatRoundTrip(double value) => double.IsNaN(value) || double.IsInfinity(value)
            ? "unavailable" : PanelNumber.RoundTrip(value);

        // Automatic keeps the compact display without rounding the wire.
        public static string Format(double value, PanelUnitConversion conversion, int decimals,
            string automaticFormat = "G12")
        {
            decimals = Math.Max(AutomaticDecimals, Math.Min(MaximumDecimals, decimals));
            if (double.IsNaN(value) || double.IsInfinity(value)) return "unavailable";
            string text = decimals >= 0 ? PanelNumber.Fixed(value, decimals)
                : PanelNumber.General(value, automaticFormat == "G6" ? 6 : 12);
            string symbol = Symbol(conversion);
            return symbol.Length == 0 ? text : text + " " + symbol;
        }
    }
}
