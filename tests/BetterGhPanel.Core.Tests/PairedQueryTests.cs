using BetterGhPanel.Core;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;

public class PairedQueryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PairedSetDistinguishesNumericAndTextKeysWithIdenticalDisplay(bool reverse)
    {
        var branches = new[] { new PanelBranch("{0}", 2) };
        var keys = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "Number", "10", 10),
            new PanelRow(1, "{0}", "Text", "10", null)
        }, branches);
        var options = new PanelQueryOptions
        {
            CreateSet = true, GroupByPaired = true,
            PairedSource = new PanelPairedQuerySource(keys, position => reverse ? 1 - position : position)
        };
        var set = PanelQueryEngine.Execute(keys, options).DisplaySet!;
        Assert.Equal("10", set.Values[0].Text);
        Assert.Equal("10", set.Values[1].Text);
        Assert.Equal(reverse, set.Values[1].IsNumeric);
        Assert.Equal(!reverse, set.Values[0].IsNumeric);
    }

    [Fact]
    public void PairedGeometryKeysUseCompactMeasuredValuesAndSortNumerically()
    {
        var branches = new[] { new PanelBranch("{0}", 2) };
        PanelRow Curve(int index, double length) => new PanelRow(index, "{0}", "Curve", "Curve", null,
            geometryInfo: new PanelGeometryInfo("Curve", length, units: "m", metricName: "Length", metricLabel: "L"));
        var data = new PanelSnapshot(new[] { Curve(0, 10), Curve(1, 9) }, branches);
        var options = new PanelQueryOptions
        {
            CreateSet = true, GroupByPaired = true, SortPairedKey = true, SortMode = PanelSortMode.ValueAscending,
            PairedSource = new PanelPairedQuerySource(data, position => position)
        };
        var result = PanelQueryEngine.Execute(data, options);
        Assert.Equal(new[] { 1, 0 }, result.RowPositions);
        Assert.Equal("9", result.DisplaySet!.Values[1].Text);
        Assert.Equal("10", result.DisplaySet!.Values[0].Text);
    }

    [Fact]
    public void PairedSetOnlyNeedsDataGeometryWhenSortingOrFilteringByData()
    {
        var curve = new PanelGeometryInfo("Curve", units: "m", metricName: "Length", canMeasure: true);
        var data = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Curve", "Curve", null, geometryInfo: curve) },
            new[] { new PanelBranch("{0}", 1) });
        var options = new PanelQueryOptions { CreateSet = true, GroupByPaired = true, OutputValues = true };
        Assert.False(PanelQueryEngine.RequiresGeometryMeasurements(options));
        Assert.Empty(PanelQueryEngine.GeometryCandidates(data, options));
        options.SortMode = PanelSortMode.ValueAscending;
        options.SortPairedKey = true;
        Assert.False(PanelQueryEngine.RequiresGeometryMeasurements(options));
        Assert.Empty(PanelQueryEngine.GeometryCandidates(data, options));
        options.SortPairedKey = false;
        Assert.True(PanelQueryEngine.RequiresGeometryMeasurements(options));
        Assert.Equal(new[] { 0 }, PanelQueryEngine.GeometryCandidates(data, options));
    }

    [Fact]
    public void ListLengthUsesDataGroupingEvenWithAPreviousPairedPreference()
    {
        var options = new PanelQueryOptions
        {
            CreateSet = true, GroupByPaired = true, SearchMode = PanelSearchMode.ListLength, DisplayFilterValue = true
        };
        var set = PanelQueryEngine.Execute(PanelQueryTestData.Sample(), options).DisplaySet!;
        Assert.True(set.IsBranchSet);
        Assert.False(set.IsPaired);
    }

    [Fact]
    public void PairedSetSortsByItsKeyOrItsFirstDataMember()
    {
        var branch = new[] { new PanelBranch("{0}", 5) };
        PanelSnapshot Snapshot(params double[] numbers) => new PanelSnapshot(
            numbers.Select((number, index) => new PanelRow(index, "{0}", "Number", number.ToString(), number)).ToArray(), branch);
        var data = Snapshot(1, 100, 9, 2, 12);
        var keys = Snapshot(10, 10, 2, 20, 20);
        var options = new PanelQueryOptions
        {
            CreateSet = true, GroupByPaired = true, SortMode = PanelSortMode.ValueAscending,
            PairedSource = new PanelPairedQuerySource(keys, position => position)
        };
        var sortedData = PanelQueryEngine.Execute(data, options);
        Assert.Equal(new[] { 0, 3, 2 }, sortedData.RowPositions);
        Assert.Equal(new[] { 0, 1 }, sortedData.DisplaySet!.Members[0]);
        options.SortPairedKey = true;
        Assert.Equal(new[] { 2, 0, 3 }, PanelQueryEngine.Execute(data, options).RowPositions);
        options.SortMode = PanelSortMode.ValueDescending;
        Assert.Equal(new[] { 3, 0, 2 }, PanelQueryEngine.Execute(data, options).RowPositions);
    }

    [Fact]
    public void CleanKeepsOriginalPairOffsetsAndUsesDataBranchLength()
    {
        var data = new[] { new PanelBranch("{0}", 1), new PanelBranch("{1}", 0), new PanelBranch("{2}", 2) };
        var paired = new[] { new PanelBranch("{2}", 2), new PanelBranch("{4}", 3), new PanelBranch("{5}", 1) };
        var groups = PanelPairing.Match(data, paired).Where(pair => pair.RetainedAfterClean(true)).ToArray();
        Assert.Equal(new[] { "{0}", "{2}" }, groups.Select(pair => pair.Data.Path));
        Assert.Equal(new int?[] { 2, 0 }, groups.Select(pair => pair.ItemPosition(0)));
    }
    [Fact]
    public void GroupingByFUsesFirstSeenKeysAndRetainsUnpairedRows()
    {
        var data = PanelQueryTestData.Sample();
        var paired = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "Text", "Wall", null),
            new PanelRow(1, "{0}", "Text", "—", null),
            new PanelRow(2, "{0}", "Text", "Wall", null)
        }, new[] { new PanelBranch("{0}", 3) });
        int?[] partners = { 0, 2, 0, 0, 1, null };
        var options = new PanelQueryOptions
        {
            CreateSet = true, GroupByPaired = true, StatKinds = PanelStatKinds.Sum,
            PairedSource = new PanelPairedQuerySource(paired, position => partners[position])
        };
        var set = PanelQueryEngine.Execute(data, options).DisplaySet!;
        Assert.Equal(new[] { 0, 1 }, set.Members[0]);
        Assert.Equal("Wall", set.Values[3].Text);
        Assert.Equal("—", set.Values[4].Text);
        Assert.False(set.Values[4].Unavailable);
        Assert.True(set.Values[5].Unavailable);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, set.AllMembers);
        options.BranchStats = true;
        var stats = PanelQueryEngine.Execute(data, options);
        Assert.Equal(new[] { "{0;1;0}", "{0;1;2;0}", "{0;10;0}", "{0;10;1}", "{0;10;2}" }, stats.StatPaths);
        Assert.Equal(12d, stats.BranchStats!["{0;1;0}"].Value(PanelStatKinds.Sum));
        var flat = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "Number", "10", 10),
            new PanelRow(1, "{0}", "Number", "3", 3)
        }, new[] { new PanelBranch("{0}", 2) });
        options.Flattened = true;
        options.PairedSource = new PanelPairedQuerySource(paired, position => position == 0 ? 0 : 2);
        var pooled = PanelQueryEngine.Execute(flat, options);
        Assert.Equal(new[] { "{0}" }, pooled.StatPaths);
        Assert.Equal(13d, pooled.BranchStats!["{0}"].Value(PanelStatKinds.Sum));
    }
    [Fact]
    public void SearchUsesOriginalPartnersAndKeepsDataRestrictions()
    {
        var data = PanelQueryTestData.Sample();
        var f = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0;1}", "Text", "Wall", null),
            new PanelRow(0, "{0;1;2}", "Text", "Wall", null),
            new PanelRow(0, "{0;10}", "Text", "Beam", null),
            new PanelRow(1, "{0;10}", "Text", "Wall", null)
        }, new[] { new PanelBranch("{0;1}", 1), new PanelBranch("{0;1;2}", 1), new PanelBranch("{0;10}", 2), new PanelBranch("{9}", 0) });
        int?[] partner = { 0, null, 1, 2, 3, null };
        var options = new PanelQueryOptions
        {
            SearchPaired = true, Search = "wall", TypeName = "Number", BranchPath = "{0;1}", BranchPrefix = true,
            PairedSource = new PanelPairedQuerySource(f, position => partner[position])
        };
        Assert.Equal(new[] { 0 }, PanelQueryEngine.Execute(data, options).RowPositions);
        options.SearchMode = PanelSearchMode.ListLength;
        options.Search = "= 1";
        Assert.Equal(new[] { 0 }, PanelQueryEngine.Execute(data, options).RowPositions);
        options.PairedSource = null;
        Assert.Empty(PanelQueryEngine.Execute(data, options).RowPositions);
    }

    [Fact]
    public void PairedNumericSearchConvertsAndRoundsWithoutApplyingTheDataFormula()
    {
        var data = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Number", "2", 2) },
            new[] { new PanelBranch("{0}", 1) }, PanelUnitConversion.MillimetersToMeters);
        var paired = new PanelSnapshot(new[] { new PanelRow(0, "{7}", "Number", "1234", 1234) },
            new[] { new PanelBranch("{7}", 1) });
        Assert.True(PanelFormula.TryCompile("x*2", out var formula, out _));
        var options = new PanelQueryOptions
        {
            SearchPaired = true, SearchMode = PanelSearchMode.Value, Search = "= 1.23", DecimalPlaces = 2,
            Formula = formula, PairedSource = new PanelPairedQuerySource(paired, _ => 0)
        };
        Assert.Equal(new[] { 0 }, PanelQueryEngine.Execute(data, options).RowPositions);
        options.SearchMode = PanelSearchMode.Regex;
        options.Search = @"^1\.23$";
        Assert.Equal(new[] { 0 }, PanelQueryEngine.Execute(data, options).RowPositions);
    }

    [Fact]
    public void PairedGeometrySearchMeasuresOnlyPartnersInTheDataScope()
    {
        var data = PanelQueryTestData.Sample();
        var curve = new PanelGeometryInfo("Curve", units: "mm", metricName: "Length", canMeasure: true);
        var paired = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{6}", "Curve", "Curve", null, geometryInfo: curve),
            new PanelRow(1, "{6}", "Curve", "Curve", null, geometryInfo: curve)
        }, new[] { new PanelBranch("{6}", 2) });
        var source = new PanelPairedQuerySource(paired, position => position == 0 ? 1 : position == 2 ? 0 : (int?)null);
        var query = new PanelQueryOptions
        {
            SearchPaired = true, SearchMode = PanelSearchMode.Length, Search = "> 25", TypeName = "Number", PairedSource = source
        };
        Assert.Equal(new[] { 1 }, PanelQueryEngine.PairedGeometryCandidates(data, query, source, CancellationToken.None));
        Assert.Empty(PanelQueryEngine.Execute(data, query).RowPositions);
        source.Measurements = new Dictionary<int, PanelGeometryInfo?> { [1] = new PanelGeometryInfo("Curve", 30, units: "mm", metricName: "Length") };
        Assert.Equal(new[] { 0 }, PanelQueryEngine.Execute(data, query).RowPositions);
    }
}
