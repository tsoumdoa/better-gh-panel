using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Xunit;

public class BranchStatsTests
{
    private static PanelSnapshot Numbers(params double[] values) => new PanelSnapshot(values.Select((value, index) =>
        new PanelRow(index, "{0}", "Number", "", value)).ToArray(), new[] { new PanelBranch("{0}", values.Length) });
    private static PanelQueryResult Stats(PanelSnapshot snapshot, PanelQueryOptions? options = null)
    {
        options ??= new PanelQueryOptions(); options.BranchStats = true;
        return PanelQueryEngine.Execute(snapshot, options);
    }

    [Theory]
    [InlineData(new double[] { 9, 1, 3 }, 3, 13, 8)]
    [InlineData(new double[] { 9, 1, 3, 7 }, 5, 20, 8)]
    [InlineData(new double[] { -5 }, -5, -5, 0)]
    public void MedianRangeSumAndPopulationDeviation(double[] values, double median, double sum, double range)
    {
        var stats = Stats(Numbers(values)).BranchStats!["{0}"];
        Assert.Equal(median, stats.Value(PanelStatKinds.Median));
        Assert.Equal(sum, stats.Value(PanelStatKinds.Sum));
        Assert.Equal(range, stats.Value(PanelStatKinds.Range));
        Assert.Equal(values.Length, stats.Value(PanelStatKinds.Count));
        double mean = values.Average();
        Assert.Equal(PanelNumber.GeneralValue(Math.Sqrt(values.Average(value => Math.Pow(value - mean, 2))), 12), stats.Value(PanelStatKinds.StdDev));
        Assert.Equal(PanelNumber.GeneralValue(mean, 12), stats.Value(PanelStatKinds.Avg));
    }

    [Fact]
    public void SkippedReasonsEmptyAndNullOutputSlots()
    {
        var snapshot = new PanelSnapshot(new[] {
            new PanelRow(0, "{0}", "Number", "", 3),
            new PanelRow(1, "{0}", "Text", "hello", null, stringValue: "hello"),
            new PanelRow(2, "{0}", "Null", "", null, isNull: true),
            new PanelRow(3, "{0}", "Number", "", 10, isValid: false),
            new PanelRow(4, "{0}", "Number", "", 20, PanelValidity.Unverified),
            new PanelRow(0, "{1}", "Text", "only text", null, stringValue: "only text")
        }, new[] { new PanelBranch("{0}", 5), new PanelBranch("{1}", 1), new PanelBranch("{2}", 0) });
        var result = Stats(snapshot);
        Assert.Equal(new[] { "{0}", "{1}", "{2}" }, result.BranchPaths);
        var stats = result.BranchStats!["{0}"];
        Assert.Equal(4, stats.Skipped);
        foreach (string reason in new[] { "1 text", "1 null", "1 invalid", "1 unverified" }) Assert.Contains(reason, stats.Details);
        Assert.Equal(3, stats.Value(PanelStatKinds.Sum));
        foreach (string path in result.BranchPaths)
        {
            var output = result.BranchStats[path].Output(PanelBranchStats.All).ToArray();
            Assert.Equal(8, output.Length);
            if (path != "{0}")
            {
                Assert.Equal(0, output[0].Number);
                Assert.All(output.Skip(1), stat => Assert.Null(stat.Number));
                Assert.Equal("—", result.BranchStats[path].Format(PanelStatKinds.Sum, 2));
            }
        }
    }

    private static PanelRow Geometry(int index, string path, int power, double? value, bool measurable = false) =>
        new PanelRow(index, path, "Geometry", "", null, geometryInfo: new PanelGeometryInfo("Geometry", value,
            power: power, units: "mm", metricName: power == 2 ? "Area" : "Length", metricLabel: power == 2 ? "A" : "L", canMeasure: measurable));

    [Fact]
    public void MixedMetricsAreSuppressedAndExplicitSearchMetricResolvesThem()
    {
        var snapshot = new PanelSnapshot(new[] { Geometry(0, "{0}", 1, 4), Geometry(1, "{0}", 2, 12) }, new[] { new PanelBranch("{0}", 2) });
        var mixed = Stats(snapshot).BranchStats!["{0}"];
        Assert.True(mixed.Mixed);
        Assert.Equal("mixed L / A", mixed.MixedLabel);
        Assert.Contains("Pick Length, Area or Volume", mixed.Details);
        Assert.Equal(0, mixed.Value(PanelStatKinds.Count));
        Assert.Null(mixed.Value(PanelStatKinds.Sum));
        Assert.Equal(-1, mixed.Source(PanelStatKinds.Min));
        var length = Stats(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.Length }).BranchStats!["{0}"];
        Assert.False(length.Mixed); Assert.Equal(4, length.Value(PanelStatKinds.Sum)); Assert.Equal(1, length.Skipped);
    }

    [Fact]
    public void FiltersUsePresentedFormulaUnitsAndRoundingBeforeStats()
    {
        Assert.True(PanelFormula.TryCompile("x*2", out var formula, out _));
        var snapshot = new PanelSnapshot(new[] { Geometry(0, "{0}", 1, 1050), Geometry(1, "{0}", 1, 2100), Geometry(2, "{0}", 1, 3300) },
            new[] { new PanelBranch("{0}", 3) }, PanelUnitConversion.MillimetersToMeters);
        var result = Stats(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.Length, Search = "> 3", Formula = formula, RoundUpFactor = .5, DecimalPlaces = 1 });
        Assert.Equal(new[] { 1, 2 }, result.RowPositions);
        Assert.Equal("2/3 items", CanvasStatistics.BranchCount(result, "{0}"));
        var stats = result.BranchStats!["{0}"];
        Assert.Equal(11.5, stats.Value(PanelStatKinds.Sum));
        Assert.Equal(5.8, stats.Value(PanelStatKinds.Avg));
        Assert.Equal("11.5 m", stats.Format(PanelStatKinds.Sum, 1));
        Assert.Equal("2", stats.Format(PanelStatKinds.Count, 1));
    }

    [Fact]
    public void FlattenAggregatesAllInputsAndFiltersKeepBranchShape()
    {
        // The host's Flatten transform supplies one {0} branch before query execution.
        var separate = new PanelSnapshot(new[] { new PanelRow(0, "{0;0}", "Number", "", 1), new PanelRow(0, "{0;1}", "Number", "", 3) },
            new[] { new PanelBranch("{0;0}", 1), new PanelBranch("{0;1}", 1), new PanelBranch("{0;2}", 0) });
        var result = Stats(separate, new PanelQueryOptions { Search = "> 2" });
        Assert.Equal(3, result.BranchStats!.Count);
        Assert.Null(result.BranchStats["{0;0}"].Value(PanelStatKinds.Sum));
        Assert.Equal(3, result.BranchStats["{0;1}"].Value(PanelStatKinds.Sum));
        var flat = Stats(Numbers(1, 3)).BranchStats!["{0}"];
        Assert.Equal(4, flat.Value(PanelStatKinds.Sum)); Assert.Equal(2, flat.Value(PanelStatKinds.Avg));
    }

    [Fact]
    public void FixedOutputOrderAndFirstExtremumSourceIgnoreSort()
    {
        var result = Stats(Numbers(9, 1, 1, 9), new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        var stats = result.BranchStats!["{0}"];
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.RowPositions);
        Assert.Equal(1, stats.Source(PanelStatKinds.Min)); Assert.Equal(0, stats.Source(PanelStatKinds.Max));
        Assert.Equal(-1, stats.Source(PanelStatKinds.Avg));
        var selection = new PanelRowSelection(); selection.SelectPositions(result, result.StatSources);
        Assert.Equal(new[] { 0, 1 }, selection.OrderedPositions(result));
        var picks = PanelStatKinds.Max | PanelStatKinds.Sum | PanelStatKinds.Min;
        Assert.Equal(new[] { PanelStatKinds.Sum, PanelStatKinds.Min, PanelStatKinds.Max }, stats.Output(picks).Select(stat => stat.Kind));
        Assert.Empty(stats.Output(picks, new HashSet<int>()));
        Assert.Equal(1, Assert.Single(stats.Output(picks, new HashSet<int> { 1 })).Number);
    }

    [Fact]
    public void PendingOffscreenGeometryUsesExistingMeasurementCandidates()
    {
        var snapshot = new PanelSnapshot(new[] { Geometry(0, "{0}", 1, null, true), Geometry(0, "{1}", 1, null, true) },
            new[] { new PanelBranch("{0}", 1), new PanelBranch("{1}", 1) });
        var options = new PanelQueryOptions { BranchStats = true, BranchPath = "{1}" };
        Assert.True(PanelQueryEngine.RequiresGeometryMeasurements(options));
        Assert.Equal(new[] { 1 }, PanelQueryEngine.GeometryCandidates(snapshot, options));
        var pending = Stats(snapshot, options).BranchStats!["{1}"];
        Assert.True(pending.Measuring); Assert.Empty(pending.Output(PanelBranchStats.Default));
        Assert.Equal("measuring…", pending.Format(PanelStatKinds.Sum, 2));
        var measured = PanelQueryEngine.Execute(snapshot, options, measuredGeometry: new Dictionary<int, PanelGeometryInfo> { [1] = Geometry(0, "{1}", 1, 7).GeometryInfo! });
        Assert.Equal(7, measured.BranchStats!["{1}"].Value(PanelStatKinds.Sum));
    }

    [Fact]
    public void TableUsesFilteredIndexPartnersAndDOnlyFormula()
    {
        Assert.True(PanelFormula.TryCompile("x*2", out var formula, out _));
        var options = new PanelQueryOptions { BranchStats = true, Search = "> 2", Formula = formula, DecimalPlaces = 1 };
        var data = Stats(Numbers(1, 2, 3), options);
        var columns = PanelBranchStats.Column(data, Numbers(10, 20, 30), position => position == 2 ? (int?)null : position,
            options, null, CancellationToken.None);
        Assert.Equal(10, data.BranchStats!["{0}"].Value(PanelStatKinds.Sum));
        Assert.Equal(20, columns["{0}"].Value(PanelStatKinds.Sum));
        Assert.Equal(1, columns["{0}"].Value(PanelStatKinds.Count));
        Assert.Equal(4, columns["{0}"].Output(PanelBranchStats.Default).Count());
    }

    [Fact]
    public void AutomaticStatOutputMatchesCompactShownNumbers()
    {
        var stats = Stats(Numbers(1.23456789012345, 3.98765432109876)).BranchStats!["{0}"];
        foreach (var value in stats.Output(PanelBranchStats.All))
            Assert.Equal(double.Parse(stats.Format(value.Kind, -1, false), System.Globalization.CultureInfo.InvariantCulture), value.Number);
        var snapshot = new PanelSnapshot(new[] { Geometry(0, "{0}", 1, 1.23456789) }, new[] { new PanelBranch("{0}", 1) });
        var metric = Stats(snapshot).BranchStats!["{0}"];
        Assert.Equal("1.23457 mm", metric.Format(PanelStatKinds.Sum, -1));
        Assert.Equal(1.23457, metric.Value(PanelStatKinds.Sum));
    }

    [Fact]
    public void PickerHasFixedOrderAndMutedGuidance()
    {
        var menu = new CanvasMenu(); var options = new PanelQueryOptions { BranchStats = true };
        menu.Toggle(CanvasMenuKind.BranchStats, Numbers(1), options);
        var anchor = new RectangleF(250, 400, 20, 20);
        menu.Layout(new RectangleF(0, 0, 470, 440), default, default, default, branchStats: anchor);
        Assert.Equal(11, menu.Count); Assert.True(menu.Bounds.Bottom < anchor.Top);
        Assert.Equal("Turn off", menu.Label(0));
        Assert.Equal("Rows per branch, in this order", menu.Label(1));
        Assert.False(menu.RowEnabled(1, false, false, PanelValuePalette.Off));
        Assert.Equal(new[] { "Count", "Sum", "Avg", "Median", "Min", "Max", "Range", "Std dev" }, Enumerable.Range(2, 8).Select(index => menu.Label(index)));
        Assert.True(menu.IsSelected(3, options, PanelUnitConversion.None, -1));
        Assert.False(menu.IsSelected(2, options, PanelUnitConversion.None, -1));
        Assert.Equal(PanelBranchStats.Default, PanelBranchStats.Normalize(0));
    }
}
