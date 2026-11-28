using System.Globalization;
using BetterGhPanel.Core;
using Xunit;

public sealed class PresentedNumberTests
{
    [Theory]
    [InlineData(1, 1d / 3, -1, 1.3333333333333333)]
    [InlineData(2 * System.Math.PI, System.Math.PI, -1, 2 * System.Math.PI)]
    [InlineData(1000000000000001, 1, -1, 1000000000000001)]
    [InlineData(9007199254740991, 1, -1, 9007199254740991)]

    [InlineData(79595447042768.58, .25, 8, 79595447042768.75)]
    [InlineData(3e-30, 1e-30, 2, 0)] // Tiny-value guard retains base rounding.
    [InlineData(.6424555553321054, .1, 1, .7)]
    [InlineData(135.63838467730133, .01, 2, 135.64)]
    [InlineData(8680.175, .07, 2, 8680.21)]
    [InlineData(8680.175, .07, 11, 8680.21)]
    [InlineData(12345678901234.56, .07, 2, 12345678901234.56)]
    [InlineData(1000000.3, 1e-12, 2, 1000000.3)]
    [InlineData(820337036301.54, 1e-6, 2, 820337036301.54)]
    [InlineData(45920837976001.6, .001, 1, 45920837976001.6)]
    [InlineData(651429.77, 1e-20, 3, 651429.77)]
    [InlineData(9623.300000000001, .05, 2, 9623.35)]
    [InlineData(.30000000000000004, .1, -1, .4)]
    [InlineData(12.1, .25, 0, 13)]
    [InlineData(-12.1, .25, 0, -12)]
    [InlineData(2.67, .01, 2, 2.67)]
    [InlineData(2.675, .001, 2, 2.68)]
    public void FactorDecimalPlacesCeil(double value, double factor, int places, double expected) =>
        Assert.Equal(expected, PanelUnits.Transform(value, PanelUnitConversion.None, factor, places));

    [Fact]
    public void PresentedMetricsAgreeWithOutputStatisticsAndFormatting()
    {
        var rows = new[] {
            new PanelRow(0, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", 4.4999999, units: "mm")),
            new PanelRow(1, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", 4.501, units: "mm"))
        };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 2) });
        var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Length, DisplayFilterValue = true, OutputValues = true, DecimalPlaces = 2 };
        var result = PanelQueryEngine.Execute(snapshot, options);
        Assert.Equal(4.5, result.OutputValues![0]!.Number);

        Assert.Equal("4.50 mm", PanelFilterValue.Format(snapshot, rows[0], options.SearchMode, 2));
    }

    [Fact]
    public void MetricComparisonsUsePresentedRoundingAndRoundUpFactors()
    {
        var rows = new[] {
            new PanelRow(0, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", 4.4999999, units: "mm")),
            new PanelRow(1, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", 4.501, units: "mm"))
        };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 2) });
        var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Length, DisplayFilterValue = true, OutputValues = true, DecimalPlaces = 2 };
        options.DisplayFilterValue = false; options.Search = "= 4.5";
        Assert.Equal(new[] { 0, 1 }, PanelQueryEngine.Execute(snapshot, options).RowPositions);
        options.RoundUpFactor = 5;
        Assert.Empty(PanelQueryEngine.Execute(snapshot, options).RowPositions);
        Assert.Equal("5.00 mm", PanelFilterValue.Format(snapshot, rows[0], options.SearchMode, 2, roundUpFactor: 5));
    }

    [Fact]
    public void PresentedScalarsAgreeWithComparisonsAndStatistics()
    {
        var options = new PanelQueryOptions { DisplayFilterValue = false, OutputValues = true, DecimalPlaces = 2, Search = "= 4.5" };
        var scalar = new PanelRow(0, "{0}", "Number", "captured", 4.4999999);
        var snapshot = new PanelSnapshot(new[] { scalar }, new[] { new PanelBranch("{0}", 1) });
        options.SearchMode = PanelSearchMode.Value;
        Assert.Single(PanelQueryEngine.Execute(snapshot, options).RowPositions);
    }

    [Fact]
    public void ScalarDisplayUsesInvariantCulture()
    {
        var scalar = new PanelRow(0, "{0}", "Number", "captured", 4.4999999);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("4.50", PanelValuePresentation.FormatDisplay(scalar, PanelUnitConversion.None, 2));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

}
