using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BetterGhPanel.Core;
using Xunit;

public sealed class PresentedNumberChecks
{
    [Theory]
    [InlineData(1263.4718, 50, -1, 1300)]
    [InlineData(-1263.4718, 50, -1, -1250)]
    [InlineData(1.005, 0, 2, 1.01)]
    [InlineData(-1.005, 0, 2, -1.01)]
    [InlineData(4.4999999, 0, 2, 4.5)]
    [InlineData(0.07, 0.01, 2, 0.07)]
    [InlineData(1.2345678901234567, 0, -1, 1.2345678901234567)]
    [InlineData(0.126, 0.05, 1, 0.2)]
    public void ConversionFactorThenExplicitDecimals(double raw, double factor, int decimals, double expected)
    {
        double value = PanelUnits.Transform(raw, PanelUnitConversion.None, factor, decimals);
        Assert.Equal(expected, value);
        Assert.Equal(value, double.Parse(PanelUnits.Format(value, PanelUnitConversion.None, decimals), CultureInfo.InvariantCulture));
        Assert.Equal(1.3, PanelUnits.Transform(1263.4718, PanelUnitConversion.MillimetersToMeters, 0.05, 2));
        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(PanelUnits.Transform(-0.001, PanelUnitConversion.None, 0, 2)));
        Assert.Equal(double.MaxValue, PanelUnits.Transform(double.MaxValue, PanelUnitConversion.None, 0, 12));
    }

    [Fact]
    public void PresentedMetricsPropertiesComponentsAndScalarsAgreeWithOutputComparisonsAndStats()
    {
        var rows = new[] {
            new PanelRow(0, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", 4.4999999, units: "mm")),
            new PanelRow(1, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", 4.501, units: "mm"))
        };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 2) });
        var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Length, DisplayFilterValue = true, OutputValues = true, DecimalPlaces = 2 };
        var result = PanelQueryEngine.Execute(snapshot, options);
        Assert.Equal(4.5, result.OutputValues![0]!.Number);
        Assert.Equal(4.5, result.Statistics.Minimum);
        Assert.Equal(4.5, result.Statistics.Maximum);
        Assert.Equal(4.5, result.Statistics.Mean);
        Assert.Single(result.Statistics.UniqueValues);
        Assert.Equal(2, result.Statistics.UniqueValues[0].Count);
        Assert.Equal("4.50 mm", PanelFilterValue.Format(snapshot, rows[0], options.SearchMode, 2));
        options.DisplayFilterValue = false; options.Search = "= 4.5";
        Assert.Equal(new[] { 0, 1 }, PanelQueryEngine.Execute(snapshot, options).RowPositions);
        options.RoundUpFactor = 5;
        Assert.Empty(PanelQueryEngine.Execute(snapshot, options).RowPositions);
        Assert.Equal("5.00 mm", PanelFilterValue.Format(snapshot, rows[0], options.SearchMode, 2, roundUpFactor: 5));

        var property = new PanelRow(0, "{0}", "Example", "", null, PanelValidity.VerifiedValid,
            components: new PanelComponents(4.4999999, 0, 0, "mm"),
            properties: new Dictionary<PanelSearchMode, PanelPropertyValue?> { [PanelSearchMode.Radius] = PanelPropertyValue.Numeric(4.4999999) });
        snapshot = new PanelSnapshot(new[] { property }, new[] { new PanelBranch("{0}", 1) });
        foreach (var mode in new[] { PanelSearchMode.Radius, PanelSearchMode.X })
        {
            options.SearchMode = mode; options.RoundUpFactor = 0;
            Assert.Single(PanelQueryEngine.Execute(snapshot, options).RowPositions);
            Assert.Equal(4.5, PanelFilterValue.OutputValue(snapshot, property, mode, null, 2)!.Number);
        }
        var scalar = new PanelRow(0, "{0}", "Number", "captured", 4.4999999);
        snapshot = new PanelSnapshot(new[] { scalar }, new[] { new PanelBranch("{0}", 1) });
        options.SearchMode = PanelSearchMode.Value;
        Assert.Single(PanelQueryEngine.Execute(snapshot, options).RowPositions);
        Assert.Equal(4.5, PanelQueryEngine.Execute(snapshot, options).Statistics.Minimum);
        Assert.Equal("4.50", PanelValuePresentation.FormatDisplay(scalar, PanelUnitConversion.None, 2, culture: CultureInfo.GetCultureInfo("de-DE")));
        Assert.Contains("2 decimal places", PanelFilterValue.RoundingDescription(snapshot, scalar, options));
        options.DecimalPlaces = -1;
        Assert.Equal("", PanelFilterValue.RoundingDescription(snapshot, scalar, options));
        var circle = new PanelRow(0, "{0}", "Circle", "", null, geometryInfo: new PanelGeometryInfo("Circle", 2, metricName: "Radius"));
        Assert.Equal(15, PanelFilterValue.OutputValue(snapshot, circle, PanelSearchMode.Length, null, -1, 5)!.Number);
    }

    [Theory]
    [InlineData(new[] { 12, 12, 12, 0 }, 12, 1)]
    [InlineData(new[] { 12, 12, 12 }, -1, 0)]
    [InlineData(new[] { 12, 12, 11, 0 }, -1, 0)]
    [InlineData(new[] { 12, 12, 12, 12 }, -1, 0)]
    [InlineData(new[] { 0, 0, 0, 1 }, 0, 1)]
    public void WarningsAreConservativeAndIndependentOfFilters(int[] lengths, int majority, int outliers)
    {
        var branches = lengths.Select((count, i) => new PanelBranch("{" + i + "}", count)).ToArray();
        var snapshot = new PanelSnapshot(new[] {
            new PanelRow(0, "{0}", "Null", "null", null, isNull: true, isValid: false),
            new PanelRow(1, "{0}", "Number", "invalid", null, isValid: false),
            new PanelRow(2, "{0}", "Unknown", "unverified", null, PanelValidity.Unverified)
        }, branches);
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "no matches" });
        Assert.Empty(result.RowPositions);
        Assert.Equal(1, result.Warnings.NullCount);
        Assert.Equal(1, result.Warnings.InvalidCount);
        Assert.Equal(1, result.Warnings.UnverifiedCount);
        Assert.Equal(majority < 0 ? (int?)null : majority, result.Warnings.MajorityLength);
        Assert.Equal(outliers, result.Warnings.OutlierBranches);
        Assert.Equal(outliers != 0, result.Warnings.IsOutlier(snapshot, branches.Last().Path));
        Assert.False(PanelQueryEngine.Execute(PanelSnapshot.Empty, new PanelQueryOptions()).Warnings.Any);
    }
}
