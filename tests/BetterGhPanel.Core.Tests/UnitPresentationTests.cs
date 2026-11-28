using Xunit;
using BetterGhPanel.Core;

public class UnitPresentationTests
{

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
    public void AngularConversionRetainsItsSerializedOrdinal()
    {
        Assert.Equal(5, (int)PanelUnitConversion.RadiansToDegrees);
    }

}
