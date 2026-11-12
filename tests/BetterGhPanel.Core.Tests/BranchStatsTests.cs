using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BetterGhPanel.Core;
using Xunit;

public class BranchStatsTests
{
    private static PanelSnapshot Numbers(params double[] values) => new PanelSnapshot(values.Select((value, index) =>
        new PanelRow(index, "{0}", "Number", "", value)).ToArray(), new[] { new PanelBranch("{0}", values.Length) });
    private static PanelQueryResult Stats(PanelSnapshot snapshot, PanelQueryOptions? options = null)
    {
        options ??= new PanelQueryOptions { StatKinds = PanelBranchStats.All }; options.BranchStats = true;
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
        foreach (string reason in new[] { "1 not numbers", "1 null", "1 invalid", "1 unverified" }) Assert.Contains(reason, stats.Details);
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
        Assert.Contains("Length, Area or Volume", mixed.Details);
        Assert.Equal(0, mixed.Value(PanelStatKinds.Count));
        Assert.Null(mixed.Value(PanelStatKinds.Sum));
        Assert.Equal(-1, mixed.Source(PanelStatKinds.Min));
        var length = Stats(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.Length, DisplayFilterValue = true }).BranchStats!["{0}"];
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
    public void FilteredStatsKeepAllBranchPaths()
    {
        var separate = new PanelSnapshot(new[] { new PanelRow(0, "{0;0}", "Number", "", 1), new PanelRow(0, "{0;1}", "Number", "", 3) },
            new[] { new PanelBranch("{0;0}", 1), new PanelBranch("{0;1}", 1), new PanelBranch("{0;2}", 0) });
        var result = Stats(separate, new PanelQueryOptions { Search = "> 2" });
        Assert.Equal(3, result.BranchStats!.Count);
        Assert.Null(result.BranchStats["{0;0}"].Value(PanelStatKinds.Sum));
        Assert.Equal(3, result.BranchStats["{0;1}"].Value(PanelStatKinds.Sum));
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
    public void PropertyFilterWithoutShowValuesUsesTheShownMetric()
    {
        var curve = new PanelGeometryInfo("NURBS curve", 2500, power: 1, units: "mm", metricName: "Length", metricLabel: "L").WithProperties(
            new Dictionary<PanelSearchMode, PanelPropertyValue?> { [PanelSearchMode.Degree] = PanelPropertyValue.Numeric(3) });
        var snapshot = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Curve", "", null, geometryInfo: curve) }, new[] { new PanelBranch("{0}", 1) });
        var options = new PanelQueryOptions { BranchStats = true, SearchMode = PanelSearchMode.Degree, Search = "= 3" };
        Assert.Equal(2500, PanelQueryEngine.Execute(snapshot, options).BranchStats!["{0}"].Value(PanelStatKinds.Sum));
    }

    [Fact]
    public void StatsUseCapturedPropertyWithoutMeasuringPrimaryMetric()
    {
        var pending = new PanelGeometryInfo("NURBS curve", canMeasure: true).WithProperties(
            new Dictionary<PanelSearchMode, PanelPropertyValue?> { [PanelSearchMode.Degree] = PanelPropertyValue.Numeric(3) });
        var row = new PanelRow(0, "{0}", "Curve", "", null, geometryInfo: pending);
        var snapshot = new PanelSnapshot(new[] { row }, new[] { new PanelBranch("{0}", 1) });
        var options = new PanelQueryOptions { BranchStats = true, SearchMode = PanelSearchMode.Degree, DisplayFilterValue = true };
        Assert.Empty(PanelQueryEngine.GeometryCandidates(snapshot, options));
        var result = PanelQueryEngine.Execute(snapshot, options, measuredGeometry:
            new Dictionary<int, PanelGeometryInfo> { [0] = new PanelGeometryInfo("NURBS curve", 12).WithProperties(pending.Properties) });
        Assert.False(result.BranchStats!["{0}"].Measuring);
        Assert.Equal(3, result.BranchStats["{0}"].Value(PanelStatKinds.Sum));
    }

    [Fact]
    public void FailedColumnMeasurementIsSkippedRatherThanPending()
    {
        var data = Stats(Numbers(1));
        var snapshot = new PanelSnapshot(new[] { Geometry(0, "{0}", 1, null, true) }, new[] { new PanelBranch("{0}", 1) });
        var columns = PanelBranchStats.Column(data, snapshot, position => position, new PanelQueryOptions(),
            new Dictionary<int, PanelGeometryInfo?> { [0] = null }, CancellationToken.None);
        var stats = columns["{0}"];
        Assert.False(stats.Measuring); Assert.Equal(1, stats.Skipped);
        Assert.Equal(0, stats.Value(PanelStatKinds.Count)); Assert.Null(stats.Value(PanelStatKinds.Sum));
        Assert.Equal("—", stats.Format(PanelStatKinds.Sum, -1));
    }

    [Fact]
    public void AutomaticAggregatesDisplayedInputsRatherThanHiddenDigits()
    {
        var snapshot = new PanelSnapshot(new[] { Geometry(0, "{0}", 1, 1.234564), Geometry(1, "{0}", 1, 1.234564) },
            new[] { new PanelBranch("{0}", 2) });
        Assert.All(snapshot.Rows, row => Assert.EndsWith("1.23456 mm", PanelValuePresentation.FormatDisplay(row, PanelUnitConversion.None, -1)));
        Assert.Equal(2.46912, Stats(snapshot).BranchStats!["{0}"].Value(PanelStatKinds.Sum));
        var scalars = Numbers(1.234560000004, -1.234559999996);
        Assert.Equal("1.23456", PanelValuePresentation.FormatDisplay(scalars.Rows[0], PanelUnitConversion.None, -1));
        Assert.Equal("-1.23456", PanelValuePresentation.FormatDisplay(scalars.Rows[1], PanelUnitConversion.None, -1));
        Assert.Equal(0, Stats(scalars).BranchStats!["{0}"].Value(PanelStatKinds.Sum));
    }

    [Fact]
    public void ColumnsUseTheirOwnPrimaryMetricDespiteDsSearchMode()
    {
        var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Length, DisplayFilterValue = true };
        var data = Stats(new PanelSnapshot(new[] { Geometry(0, "{0}", 1, 4) }, new[] { new PanelBranch("{0}", 1) }), options);
        var area = new PanelSnapshot(new[] { Geometry(0, "{0}", 2, 12) }, new[] { new PanelBranch("{0}", 1) });
        var columns = PanelBranchStats.Column(data, area, position => position, options, null, CancellationToken.None);
        Assert.Equal(12, columns["{0}"].Value(PanelStatKinds.Sum));
        Assert.Equal("12 mm²", columns["{0}"].Format(PanelStatKinds.Sum, -1));
        Assert.Equal(0, columns["{0}"].Skipped);
    }

    [Fact]
    public void ColumnMeasurementDemandUsesOnlyPairedFilteredMembers()
    {
        var data = Stats(Numbers(1, 2, 3, 4, 5), new PanelQueryOptions { Search = "> 3" });
        var visited = new List<int>();
        var items = PanelBranchStats.ColumnItems(data, position =>
        {
            visited.Add(position); return position == 3 ? 7 : (int?)null;
        });
        Assert.Equal(new[] { 3, 4 }, visited);
        Assert.Equal(new[] { 7 }, items);
    }

    [Fact]
    public void PlainNumbersAndGeometryAreDifferentKinds()
    {
        var snapshot = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Number", "5", 5), Geometry(1, "{0}", 1, 10) }, new[] { new PanelBranch("{0}", 2) });
        var stats = Stats(snapshot).BranchStats!["{0}"];
        Assert.Equal("mixed N / L", stats.MixedLabel);
        Assert.Null(stats.Value(PanelStatKinds.Sum));
        Assert.Equal(0, stats.Value(PanelStatKinds.Count));
    }

    [Fact]
    public void ShownCoordinatesAndNonnumericPropertiesDoNotMeasureGeometry()
    {
        var pending = new PanelGeometryInfo("Curve", canMeasure: true).WithProperties(new Dictionary<PanelSearchMode, PanelPropertyValue?> {
            [PanelSearchMode.Closed] = null, [PanelSearchMode.Degree] = PanelPropertyValue.Integer(3) });
        var snapshot = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Point", "", null, PanelValidity.VerifiedValid,
            components: new PanelComponents(2500, 0, 0, "mm"), geometryInfo: pending) }, new[] { new PanelBranch("{0}", 1) }, PanelUnitConversion.MillimetersToMeters);
        var options = new PanelQueryOptions { SearchMode = PanelSearchMode.X, DisplayFilterValue = true, BranchStats = true };
        Assert.Empty(PanelQueryEngine.GeometryCandidates(snapshot, options));
        var x = Stats(snapshot, options).BranchStats!["{0}"];
        Assert.Equal(2.5, x.Value(PanelStatKinds.Sum)); Assert.Equal("2.5 m", x.Format(PanelStatKinds.Sum, -1));
        options.SearchMode = PanelSearchMode.Closed;
        Assert.Empty(PanelQueryEngine.GeometryCandidates(snapshot, options));
        var boolean = Stats(snapshot, options).BranchStats!["{0}"];
        Assert.False(boolean.Measuring); Assert.Equal(1, boolean.Skipped);
        Assert.Contains("not numbers", boolean.Details); Assert.DoesNotContain("unavailable measurements", boolean.Details);
        var noMetric = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Point", "", null,
            geometryInfo: new PanelGeometryInfo("Point", 0, metricName: "", metricLabel: "")) }, new[] { new PanelBranch("{0}", 1) });
        var skipped = Stats(noMetric).BranchStats!["{0}"];
        Assert.Equal(1, skipped.Skipped); Assert.Contains("not numbers", skipped.Details); Assert.DoesNotContain("unavailable measurements", skipped.Details);
    }

    [Fact]
    public void ScalarFiltersExcludeGeometryFromStatsDemand()
    {
        var snapshot = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Number", "1", 1), Geometry(1, "{0}", 1, null, true) }, new[] { new PanelBranch("{0}", 2) });
        foreach (var mode in new[] { PanelSearchMode.Value, PanelSearchMode.Text })
            Assert.Empty(PanelQueryEngine.GeometryCandidates(snapshot, new PanelQueryOptions { BranchStats = true, SearchMode = mode, Search = "> 0" }));
    }

    [Fact]
    public void SelectionIsTrimmedWhenAResultChangesTheExtremumOrPicks()
    {
        var snapshot = Numbers(1, 2, 3);
        var options = new PanelQueryOptions { StatKinds = PanelStatKinds.Max };
        var first = Stats(snapshot, options);
        var selection = new PanelRowSelection(); selection.Select(first, 2);
        Assert.True(selection.Contains(2));
        PanelFormula.TryCompile("-x", out var negative, out _); options.Formula = negative;
        var changed = Stats(snapshot, options);
        Assert.Empty(selection.OrderedPositions(changed)); Assert.Equal(0, changed.BranchStats!["{0}"].Source(PanelStatKinds.Max));
        selection.SelectIndices(changed, new[] { 1 }); Assert.Empty(selection.OrderedPositions(changed));
        selection.Select(changed, 0); options.StatKinds = PanelStatKinds.Sum;
        Assert.Empty(selection.OrderedPositions(Stats(snapshot, options)));
    }

    [Fact]
    public void OnlyMedianRetainsValuesAndCompletionHonoursCancellation()
    {
        var snapshot = Numbers(1, 2, 3); var query = new PanelQueryOptions { StatKinds = PanelStatKinds.Sum };
        var stats = new PanelBranchStats(query.StatKinds);
        for (int i = 0; i < 3; i++) stats.Add(snapshot, snapshot.Rows[i], i, query, null);
        var field = typeof(PanelBranchStats).GetField("_median", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Null(field.GetValue(stats)); stats.Complete(query);
        Assert.Equal(6, stats.Value(PanelStatKinds.Sum)); Assert.Null(stats.Value(PanelStatKinds.Avg)); Assert.Null(stats.Value(PanelStatKinds.StdDev));
        query.StatKinds = PanelStatKinds.Median; stats = new PanelBranchStats(query.StatKinds);
        for (int i = 0; i < 3; i++) stats.Add(snapshot, snapshot.Rows[i], i, query, null);
        Assert.NotNull(field.GetValue(stats));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => stats.Complete(query, cancellation.Token));
    }
}
