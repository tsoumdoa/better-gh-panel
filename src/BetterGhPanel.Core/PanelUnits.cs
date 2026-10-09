using System;
using System.Globalization;

namespace BetterGhPanel.Core
{
    public enum PanelUnitConversion
    {
        None,
        MillimetersToMeters,
        MetersToMillimeters,
        SquareMillimetersToSquareMeters,
        SquareMetersToSquareMillimeters,
        RadiansToDegrees,
        DegreesToRadians,
        // Append new modes so existing saved definitions retain their enum values.
        CubicMillimetersToCubicMeters,
        CubicMetersToCubicMillimeters
    }

    public static class PanelUnits
    {
        public const int AutomaticDecimals = -1;
        public const int MaximumDecimals = 12;
        public static int ConversionCount => 9;

        public static double Convert(double value, PanelUnitConversion conversion)
        {
            switch (conversion)
            {
                case PanelUnitConversion.MillimetersToMeters: return value / 1000;
                case PanelUnitConversion.MetersToMillimeters: return value * 1000;
                case PanelUnitConversion.SquareMillimetersToSquareMeters: return value / 1000000;
                case PanelUnitConversion.SquareMetersToSquareMillimeters: return value * 1000000;
                case PanelUnitConversion.CubicMillimetersToCubicMeters: return value / 1000000000;
                case PanelUnitConversion.CubicMetersToCubicMillimeters: return value * 1000000000;
                case PanelUnitConversion.RadiansToDegrees: return value * (180 / Math.PI);
                case PanelUnitConversion.DegreesToRadians: return value * (Math.PI / 180);
                default: return value;
            }
        }

        public static string Label(PanelUnitConversion conversion)
        {
            switch (conversion)
            {
                case PanelUnitConversion.MillimetersToMeters: return "mm → m";
                case PanelUnitConversion.MetersToMillimeters: return "m → mm";
                case PanelUnitConversion.SquareMillimetersToSquareMeters: return "mm² → m²";
                case PanelUnitConversion.SquareMetersToSquareMillimeters: return "m² → mm²";
                case PanelUnitConversion.CubicMillimetersToCubicMeters: return "mm³ → m³";
                case PanelUnitConversion.CubicMetersToCubicMillimeters: return "m³ → mm³";
                case PanelUnitConversion.RadiansToDegrees: return "rad → deg";
                case PanelUnitConversion.DegreesToRadians: return "deg → rad";
                default: return "No conversion";
            }
        }

        public static string Symbol(PanelUnitConversion conversion)
        {
            switch (conversion)
            {
                case PanelUnitConversion.MillimetersToMeters: return "m";
                case PanelUnitConversion.MetersToMillimeters: return "mm";
                case PanelUnitConversion.SquareMillimetersToSquareMeters: return "m²";
                case PanelUnitConversion.SquareMetersToSquareMillimeters: return "mm²";
                case PanelUnitConversion.CubicMillimetersToCubicMeters: return "m³";
                case PanelUnitConversion.CubicMetersToCubicMillimeters: return "mm³";
                case PanelUnitConversion.RadiansToDegrees: return "°";
                case PanelUnitConversion.DegreesToRadians: return "rad";
                default: return string.Empty;
            }
        }

        // Geometry retains its model units. Convert only a matching source unit and
        // dimension; numeric output conversion must never scale the geometry itself.
        public static string FormatGeometry(double value, int power, string units, PanelUnitConversion conversion, int decimals)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return "unavailable";
            bool toMeters = units == "mm" && (power == 1 && conversion == PanelUnitConversion.MillimetersToMeters ||
                power == 2 && conversion == PanelUnitConversion.SquareMillimetersToSquareMeters ||
                power == 3 && conversion == PanelUnitConversion.CubicMillimetersToCubicMeters);
            bool toMillimeters = units == "m" && (power == 1 && conversion == PanelUnitConversion.MetersToMillimeters ||
                power == 2 && conversion == PanelUnitConversion.SquareMetersToSquareMillimeters ||
                power == 3 && conversion == PanelUnitConversion.CubicMetersToCubicMillimeters);
            if (toMeters || toMillimeters)
            {
                var converted = Convert(value, conversion);
                return double.IsInfinity(converted) ? "unavailable" : Format(converted, conversion, decimals, CultureInfo.InvariantCulture, "G6");
            }
            return Format(value, PanelUnitConversion.None, decimals, CultureInfo.InvariantCulture, "G6") +
                " " + units + (power == 2 ? "²" : power == 3 ? "³" : "");
        }

        // Formatting never rounds the value carried by the output wire.
        public static string Format(double value, PanelUnitConversion conversion, int decimals,
            IFormatProvider? culture = null, string automaticFormat = "G12")
        {
            decimals = Math.Max(AutomaticDecimals, Math.Min(MaximumDecimals, decimals));
            string text = value.ToString(decimals < 0 ? automaticFormat : "F" + decimals,
                culture ?? CultureInfo.CurrentCulture);
            string symbol = Symbol(conversion);
            return symbol.Length == 0 ? text : text + " " + symbol;
        }
    }
}
