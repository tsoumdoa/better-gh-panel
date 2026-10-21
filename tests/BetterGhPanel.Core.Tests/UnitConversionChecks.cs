using System;
using BetterGhPanel.Core;

internal static class UnitConversionChecks
{
    internal static void Run()
    {
        PersistedModesAndPresentation();
        ExactScalarArithmetic();
        GeometryApplicability();
        UnknownModes();
    }

    private static void PersistedModesAndPresentation()
    {
        // This order is part of saved Grasshopper definitions and menu indexing.
        var modes = new[]
        {
            PanelUnitConversion.None, PanelUnitConversion.MillimetersToMeters, PanelUnitConversion.MetersToMillimeters,
            PanelUnitConversion.SquareMillimetersToSquareMeters, PanelUnitConversion.SquareMetersToSquareMillimeters,
            PanelUnitConversion.RadiansToDegrees, PanelUnitConversion.DegreesToRadians,
            PanelUnitConversion.CubicMillimetersToCubicMeters, PanelUnitConversion.CubicMetersToCubicMillimeters
        };
        var labels = new[] { "No conversion", "mm → m", "m → mm", "mm² → m²", "m² → mm²", "rad → deg", "deg → rad", "mm³ → m³", "m³ → mm³" };
        var symbols = new[] { "", "m", "mm", "m²", "mm²", "°", "rad", "m³", "mm³" };
        Check(PanelUnits.ConversionCount == modes.Length && Enum.GetValues(typeof(PanelUnitConversion)).Length == modes.Length,
            "Every persisted conversion is exposed by the unit menu");
        for (int index = 0; index < modes.Length; index++)
        {
            Check((int)modes[index] == index, "Existing conversion IDs remain stable");
            Check(PanelUnits.Label(modes[index]) == labels[index] && PanelUnits.Symbol(modes[index]) == symbols[index],
                "Menu labels and output suffixes retain their meaning for saved conversion " + index);
        }
    }

    private static void ExactScalarArithmetic()
    {
        // These fractions distinguish division from multiplication by a rounded
        // reciprocal. Wire values must retain their original floating-point bits.
        var values = new[] { 25.0 / 3, 0.1, -1234.56789, double.Epsilon, double.MaxValue,
            double.PositiveInfinity, double.NegativeInfinity, -0.0, double.NaN };
        foreach (double value in values)
        {
            SameBits(value, PanelUnits.Convert(value, PanelUnitConversion.None));
            SameBits(value / 1000, PanelUnits.Convert(value, PanelUnitConversion.MillimetersToMeters));
            SameBits(value * 1000, PanelUnits.Convert(value, PanelUnitConversion.MetersToMillimeters));
            SameBits(value / 1000000, PanelUnits.Convert(value, PanelUnitConversion.SquareMillimetersToSquareMeters));
            SameBits(value * 1000000, PanelUnits.Convert(value, PanelUnitConversion.SquareMetersToSquareMillimeters));
            SameBits(value / 1000000000, PanelUnits.Convert(value, PanelUnitConversion.CubicMillimetersToCubicMeters));
            SameBits(value * 1000000000, PanelUnits.Convert(value, PanelUnitConversion.CubicMetersToCubicMillimeters));
            SameBits(value * (180 / Math.PI), PanelUnits.Convert(value, PanelUnitConversion.RadiansToDegrees));
            SameBits(value * (Math.PI / 180), PanelUnits.Convert(value, PanelUnitConversion.DegreesToRadians));
        }
    }

    private static void GeometryApplicability()
    {
        var toMeters = new[] { PanelUnitConversion.MillimetersToMeters,
            PanelUnitConversion.SquareMillimetersToSquareMeters, PanelUnitConversion.CubicMillimetersToCubicMeters };
        var toMillimeters = new[] { PanelUnitConversion.MetersToMillimeters,
            PanelUnitConversion.SquareMetersToSquareMillimeters, PanelUnitConversion.CubicMetersToCubicMillimeters };
        const double value = 1234.56789;
        foreach (PanelUnitConversion mode in Enum.GetValues(typeof(PanelUnitConversion)))
        foreach (string units in new[] { "mm", "m", "ft", "MM", "" })
        for (int power = 0; power <= 4; power++)
        {
            double expected = value;
            if (power >= 1 && power <= 3)
            {
                double scale = Math.Pow(1000, power);
                if (units == "mm" && mode == toMeters[power - 1]) expected = value / scale;
                if (units == "m" && mode == toMillimeters[power - 1]) expected = value * scale;
            }
            SameBits(expected, PanelUnits.ConvertGeometry(value, power, units, mode));
        }
        Check(PanelUnits.FormatGeometry(double.MaxValue, 3, "m", PanelUnitConversion.CubicMetersToCubicMillimeters, -1) == "unavailable",
            "Geometry conversion overflow remains unavailable instead of displaying infinity");
    }

    private static void UnknownModes()
    {
        foreach (int id in new[] { -1, 9, int.MinValue, int.MaxValue })
        {
            var mode = (PanelUnitConversion)id;
            Check(PanelUnits.Label(mode) == "No conversion" && PanelUnits.Symbol(mode) == string.Empty,
                "Unknown modes retain no-conversion presentation");
            foreach (double value in new[] { -0.0, 0.1, double.NaN, double.PositiveInfinity })
                SameBits(value, PanelUnits.Convert(value, mode));
            SameBits(1.25, PanelUnits.ConvertGeometry(1.25, 1, "mm", mode));
            Check(PanelUnits.FormatGeometry(1.25, 2, "mm", mode, 2) == "1.25 mm²",
                "Unknown modes retain model geometry units");
        }
    }

    private static void SameBits(double expected, double actual) =>
        Check(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            "Expected exact floating-point value " + expected + "; got " + actual);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
