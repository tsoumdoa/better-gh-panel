using Xunit;
using BetterGhPanel.Core;

public class InvalidNumberTests
{
    [Fact]
    public void EmptyAndNonfiniteNumbers()
    {
        var empty = PanelQueryEngine.Execute(PanelSnapshot.Empty, new PanelQueryOptions());
        Assert.Equal(0, empty.RowPositions.Count);
        Assert.Equal(0, empty.Statistics.BranchCount);
        Assert.Equal<double?>(null, empty.Statistics.Mean);
        Assert.Equal<double?>(null, empty.Statistics.Minimum);
        Assert.Equal<double?>(null, empty.Statistics.Maximum);
        var snapshot = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "Number", "NaN", double.NaN),
            new PanelRow(1, "{0}", "Number", "Infinity", double.PositiveInfinity),
            new PanelRow(2, "{0}", "Number", "null", 5, true),
            new PanelRow(3, "{0}", "Number", "3", 3),
            new PanelRow(4, "{0}", "Number", "3", 3)
        }, new[] { new PanelBranch("{0}", 5) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Assert.Equal(2, result.Statistics.NumericCount);
        Assert.Equal(3.0, result.Statistics.Mean!.Value);
        Assert.Equal(new[] { 3, 4 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = ">0" }).RowPositions);
    }

    [Fact]
    public void InvalidFiniteNumbersRemainVisibleButAreExcludedFromStatistics()
    {
        var snapshot = CreateUnsetValueSnapshot();
        var all = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Assert.Equal(new[] { 0, 1, 2 }, all.RowPositions);
        Assert.Equal(2, all.Statistics.InvalidCount);
        Assert.Equal(1, all.Statistics.NumericCount);
        Assert.Equal<double?>(10, all.Statistics.Minimum);
        Assert.Equal<double?>(10, all.Statistics.Maximum);
        Assert.Equal<double?>(10, all.Statistics.Mean);
    }

    [Theory]
    [InlineData(PanelSearchMode.Text)]
    [InlineData(PanelSearchMode.Value)]
    public void NumericSearchExcludesInvalidFiniteValues(PanelSearchMode mode)
    {
        var snapshot = CreateUnsetValueSnapshot();
        Assert.Equal(0, PanelQueryEngine.Execute(snapshot,
            new PanelQueryOptions { SearchMode = mode, Search = "< 0" }).RowPositions.Count);
        Assert.Equal(0, PanelQueryEngine.Execute(snapshot,
            new PanelQueryOptions { SearchMode = mode, Search = "> 20" }).RowPositions.Count);
        Assert.Equal(new[] { 1 }, PanelQueryEngine.Execute(snapshot,
            new PanelQueryOptions { SearchMode = mode, Search = "< 20" }).RowPositions);
    }

    [Fact]
    public void SortingRetainsInvalidFiniteRows()
    {
        var snapshot = CreateUnsetValueSnapshot();
        Assert.Equal(new[] { 1, 0, 2 }, PanelQueryEngine.Execute(snapshot,
            new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending }).RowPositions);
        Assert.Equal(new[] { 1, 2, 0 }, PanelQueryEngine.Execute(snapshot,
            new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending }).RowPositions);
    }

    [Fact]
    public void InvalidOnlyResultsHaveNoNumericStatistics()
    {
        var snapshot = CreateUnsetValueSnapshot();
        var invalid = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { OnlyInvalid = true });
        Assert.Equal(new[] { 0, 2 }, invalid.RowPositions);
        Assert.Equal(0, invalid.Statistics.NumericCount);
        Assert.Equal<double?>(null, invalid.Statistics.Mean);
    }

    private static PanelSnapshot CreateUnsetValueSnapshot()
    {
        // RhinoMath.UnsetValue is finite, but a native GH_Number carrying it is invalid.
        const double unsetValue = -1.23432101234321e308;
        var snapshot = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "Number", "-1.23432101234E+308", unsetValue, isValid: false),
            new PanelRow(1, "{0}", "Number", "10", 10),
            new PanelRow(2, "{0}", "Number", "42", 42, isValid: false)
        }, new[] { new PanelBranch("{0}", 3) });
        return snapshot;
    }
}
