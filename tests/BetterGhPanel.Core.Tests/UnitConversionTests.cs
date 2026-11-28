using Xunit;
using System;
using BetterGhPanel.Core;

public class UnitConversionTests
{


    [Fact]
    public void PersistedModesAndPresentation()
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
        Assert.True(PanelUnits.ConversionCount == modes.Length && Enum.GetValues(typeof(PanelUnitConversion)).Length == modes.Length,
            "Every persisted conversion is exposed by the unit menu");
        for (int index = 0; index < modes.Length; index++)
        {
            Assert.True((int)modes[index] == index, "Existing conversion IDs remain stable");
            Assert.True(PanelUnits.Label(modes[index]) == labels[index] && PanelUnits.Symbol(modes[index]) == symbols[index],
                "Menu labels and output suffixes retain their meaning for saved conversion " + index);
        }
    }

    [Fact]
    public void ExactScalarArithmetic()
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

    [Fact]
    public void UnknownModes()
    {
        foreach (int id in new[] { -1, 9, int.MinValue, int.MaxValue })
        {
            var mode = (PanelUnitConversion)id;
            Assert.True(PanelUnits.Label(mode) == "No conversion" && PanelUnits.Symbol(mode) == string.Empty,
                "Unknown modes retain no-conversion presentation");
            foreach (double value in new[] { -0.0, 0.1, double.NaN, double.PositiveInfinity })
                SameBits(value, PanelUnits.Convert(value, mode));
            SameBits(1.25, PanelUnits.ConvertGeometry(1.25, 1, "mm", mode));
            Assert.True(PanelUnits.FormatGeometry(1.25, 2, "mm", mode, 2) == "1.25 mm²",
                "Unknown modes retain model geometry units");
        }
    }

    private static void SameBits(double expected, double actual) =>
        Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            "Expected exact floating-point value " + expected + "; got " + actual);


}
