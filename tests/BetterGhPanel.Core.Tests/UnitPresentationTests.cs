using Xunit;
using System;
using System.Globalization;
using BetterGhPanel.Core;

public class UnitPresentationTests
{
    [Fact]
    public void UnitConversionsAndFormatting()
    {
        Assert.True(Math.Abs((1.23456789) - (PanelUnits.Convert(1234.56789, PanelUnitConversion.MillimetersToMeters))) <= Math.Max(1, Math.Abs(1.23456789)) * 1e-12);
        Assert.True(Math.Abs((1234.56789) - (PanelUnits.Convert(1.23456789, PanelUnitConversion.MetersToMillimeters))) <= Math.Max(1, Math.Abs(1234.56789)) * 1e-12);
        Assert.True(Math.Abs((1) - (PanelUnits.Convert(1000000, PanelUnitConversion.SquareMillimetersToSquareMeters))) <= Math.Max(1, Math.Abs(1)) * 1e-12);
        Assert.True(Math.Abs((1000000) - (PanelUnits.Convert(1, PanelUnitConversion.SquareMetersToSquareMillimeters))) <= Math.Max(1, Math.Abs(1000000)) * 1e-12);
        Assert.True(Math.Abs((180) - (PanelUnits.Convert(Math.PI, PanelUnitConversion.RadiansToDegrees))) <= Math.Max(1, Math.Abs(180)) * 1e-12);
        Assert.True(Math.Abs((Math.PI) - (PanelUnits.Convert(180, PanelUnitConversion.DegreesToRadians))) <= Math.Max(1, Math.Abs(Math.PI)) * 1e-12);
        foreach (var pair in new[] { PanelUnitConversion.MillimetersToMeters, PanelUnitConversion.SquareMillimetersToSquareMeters, PanelUnitConversion.RadiansToDegrees })
            Assert.True(Math.Abs((-1234.5) - (PanelUnits.Convert(PanelUnits.Convert(-1234.5, pair), (PanelUnitConversion)((int)pair + 1)))) <= Math.Max(1, Math.Abs(-1234.5)) * 1e-12);
        Assert.Equal("1.23 m", PanelUnits.Format(1.23456789, PanelUnitConversion.MillimetersToMeters, 2));
        Assert.Equal("1.23456789 m", PanelUnits.Format(1.23456789, PanelUnitConversion.MillimetersToMeters, -1));
        Assert.Equal("1 m²", PanelUnits.Format(1, PanelUnitConversion.SquareMillimetersToSquareMeters, 0));
        Assert.Equal("180.00 °", PanelUnits.Format(180, PanelUnitConversion.RadiansToDegrees, 2));
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1.23 m", PanelUnits.Format(1.234, PanelUnitConversion.MillimetersToMeters, 2));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        Assert.Equal("1.000000000000", PanelUnits.Format(1, PanelUnitConversion.None, 100));
        Assert.Equal(true, double.IsNaN(PanelUnits.Convert(double.NaN, PanelUnitConversion.RadiansToDegrees)));
        Assert.Equal(double.PositiveInfinity, PanelUnits.Convert(double.PositiveInfinity, PanelUnitConversion.MillimetersToMeters));
    }

    [Fact]
    public void GeometryUnitsConvertCompatibleDimensionsInBothDirections()
    {
        Assert.Equal("1 m", PanelUnits.FormatGeometry(1000, 1, "mm", PanelUnitConversion.MillimetersToMeters, -1));
        Assert.Equal("1 m²", PanelUnits.FormatGeometry(1000000, 2, "mm", PanelUnitConversion.SquareMillimetersToSquareMeters, -1));
        Assert.Equal("1 m³", PanelUnits.FormatGeometry(1000000000, 3, "mm", PanelUnitConversion.CubicMillimetersToCubicMeters, -1));
        Assert.Equal("1000 mm", PanelUnits.FormatGeometry(1, 1, "m", PanelUnitConversion.MetersToMillimeters, -1));
        Assert.Equal("1e+06 mm²", PanelUnits.FormatGeometry(1, 2, "m", PanelUnitConversion.SquareMetersToSquareMillimeters, -1).ToLowerInvariant());
        Assert.Equal("1e+09 mm³", PanelUnits.FormatGeometry(1, 3, "m", PanelUnitConversion.CubicMetersToCubicMillimeters, -1).ToLowerInvariant());
    }

    [Fact]
    public void GeometryUnitsPreserveIncompatibleDimensionsAndSourceUnits()
    {
        Assert.Equal("1000 mm²", PanelUnits.FormatGeometry(1000, 2, "mm", PanelUnitConversion.MillimetersToMeters, -1));
        Assert.Equal("1 m", PanelUnits.FormatGeometry(1, 1, "m", PanelUnitConversion.MillimetersToMeters, -1));
        Assert.Equal("2 ft³", PanelUnits.FormatGeometry(2, 3, "ft", PanelUnitConversion.CubicMillimetersToCubicMeters, -1));
    }

    [Fact]
    public void GeometryFormattingAppliesPrecisionAndIgnoresAngularConversion()
    {
        Assert.Equal("1.235 m", PanelUnits.FormatGeometry(1234.56789, 1, "mm", PanelUnitConversion.MillimetersToMeters, 3));
        Assert.Equal("1000.00 mm", PanelUnits.FormatGeometry(1000, 1, "mm", PanelUnitConversion.RadiansToDegrees, 2));
    }

    [Fact]
    public void UnavailableGeometryValuesUseTheUnavailableLabel()
    {
        Assert.Equal("unavailable", PanelUnits.FormatGeometry(double.NaN, 2, "mm", PanelUnitConversion.None, -1));
        Assert.Equal("unavailable", PanelUnits.FormatGeometry(double.MaxValue, 3, "m", PanelUnitConversion.CubicMetersToCubicMillimeters, -1));
    }

    [Fact]
    public void CubicConversionsWorkInBothDirections()
    {
        Assert.True(Math.Abs((1) - (PanelUnits.Convert(1000000000, PanelUnitConversion.CubicMillimetersToCubicMeters))) <= Math.Max(1, Math.Abs(1)) * 1e-12);
        Assert.True(Math.Abs((1000000000) - (PanelUnits.Convert(1, PanelUnitConversion.CubicMetersToCubicMillimeters))) <= Math.Max(1, Math.Abs(1000000000)) * 1e-12);
    }

    [Fact]
    public void AngularConversionRetainsItsSerializedOrdinal()
    {
        Assert.Equal(5, (int)PanelUnitConversion.RadiansToDegrees);
    }

    [Fact]
    public void CompactVolumeIncludesConvertedValue()
    {
        var info = new PanelGeometryInfo("Brep · closed", 1000000000, 3, "mm", "Volume", "V");
        Assert.Equal("Brep · closed · V 1 m³", info.FormatCompact(PanelUnitConversion.CubicMillimetersToCubicMeters, -1));
        Assert.Equal(1000000000d, info.Value!.Value);
    }

    [Fact]
    public void SurfaceDetailsIncludeConvertedAreaAndCapturedProperties()
    {
        var surface = new PanelGeometryInfo("Srf · untrimmed · planar", 1000000, 2, "mm", "Area", "A",
            details: "Trimming: Untrimmed\nPlanarity: Planar\nDegree (U/V): 1/1");
        Assert.Equal("Srf · untrimmed · planar · A 1.00 m²", surface.FormatCompact(PanelUnitConversion.SquareMillimetersToSquareMeters, 2));
        Assert.Equal("Area: 1.00 m²\nTrimming: Untrimmed\nPlanarity: Planar\nDegree (U/V): 1/1",
            surface.FormatDetails(PanelUnitConversion.SquareMillimetersToSquareMeters, 2));
    }

    [Fact]
    public void UnmeasuredGeometryRetainsCapturedDetails()
    {
        Assert.Equal("Trimming: Untrimmed", new PanelGeometryInfo("Srf · untrimmed", canMeasure: true,
            details: "Trimming: Untrimmed").FormatDetails(PanelUnitConversion.None, -1));
    }

    [Fact]
    public void GeometryFormattingCanOmitUnitSuffixes()
    {
        Assert.Equal("1.00", PanelUnits.FormatGeometry(1000000, 2, "mm", PanelUnitConversion.SquareMillimetersToSquareMeters, 2, false));
        Assert.Equal("1E+06", PanelUnits.FormatGeometry(1000000, 2, "mm", PanelUnitConversion.MillimetersToMeters, -1, false));
        Assert.Equal("1", PanelUnits.FormatGeometry(1000000000, 3, "mm", PanelUnitConversion.CubicMillimetersToCubicMeters, -1, false));
        Assert.Equal("unavailable", PanelUnits.FormatGeometry(double.MaxValue, 3, "m", PanelUnitConversion.CubicMetersToCubicMillimeters, -1, false));
    }
}
