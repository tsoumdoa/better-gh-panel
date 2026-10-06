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
        DegreesToRadians
    }

    public static class PanelUnits
    {
        public const int AutomaticDecimals = -1;
        public const int MaximumDecimals = 12;
        public static int ConversionCount => 7;

        public static double Convert(double value, PanelUnitConversion conversion)
        {
            switch (conversion)
            {
                case PanelUnitConversion.MillimetersToMeters: return value / 1000;
                case PanelUnitConversion.MetersToMillimeters: return value * 1000;
                case PanelUnitConversion.SquareMillimetersToSquareMeters: return value / 1000000;
                case PanelUnitConversion.SquareMetersToSquareMillimeters: return value * 1000000;
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
                case PanelUnitConversion.RadiansToDegrees: return "°";
                case PanelUnitConversion.DegreesToRadians: return "rad";
                default: return string.Empty;
            }
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
