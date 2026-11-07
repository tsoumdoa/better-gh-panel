using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BetterGhPanel.Core;
using Xunit;

public class TableColumnsTests
{
    private static PanelSnapshot Data() => new PanelSnapshot(new[]
    {
        new PanelRow(0, "{0}", "Number", "3", 3), new PanelRow(1, "{0}", "Number", "1", 1),
        new PanelRow(2, "{0}", "Number", "2", 2), new PanelRow(0, "{2}", "Number", "4", 4)
    }, new[] { new PanelBranch("{0}", 3), new PanelBranch("{1}", 0), new PanelBranch("{2}", 1) });

    [Fact]
    public void AugmentedIndexAlignsExtrasAcrossColumnsAndKeepsEmptyBranches()
    {
        var result = PanelQueryEngine.Execute(Data(), new PanelQueryOptions());
        var index = new PanelTableIndex(result, 3, new[]
        {
            new PanelTableExtra("{0}", "{0}:4", 0, 4, 4), new PanelTableExtra("{0}", "{0}:3", 0, 3, 3),
            new PanelTableExtra("{0}", "{0}:3", 1, 8, 3), new PanelTableExtra("{0}", "{0}:5", 2, 20, 5),
            new PanelTableExtra("{1}", "{1}:0", 2, 21)
        });
        Assert.Equal(9, index.Entries.Count);
        Assert.Equal(new[] { 3, 8, -1 }, index.Entries[3].Cells);
        Assert.Equal(new[] { 4, -1, -1 }, index.Entries[4].Cells);
        Assert.Equal(new[] { -1, -1, 20 }, index.Entries[5].Cells);
        Assert.Equal("{1}", index.Entries[6].Empty);
        Assert.True(index.Entries[7].Extra);
        Assert.Equal(8, index.OffsetOfItem(3));
        Assert.Equal(3, index.ItemAtOrAfter(3));
        Assert.Equal(4, index.ItemAtOrAfter(99));
    }

    [Theory]
    [InlineData(PanelSortMode.ValueAscending, 2, 0, 1)]
    [InlineData(PanelSortMode.ValueDescending, 0, 2, 1)]
    public void ColumnSortIsBranchLocalMissingLastAndOutputEquivalent(PanelSortMode mode, int first, int second, int third)
    {
        var snapshot = Data();
        var source = new PanelQueryOptions();
        var query = new PanelQueryOptions
        {
            SortMode = mode, SortColumn = Guid.NewGuid(), ColumnSortKeys = new PanelColumnSortKeys(new PanelRow?[]
            { new PanelRow(0, "{9}", "Number", "20", 20), null, new PanelRow(2, "{9}", "Number", "10", 10), null })
        };
        using var work = PanelQueryEngine.Start(snapshot, query);
        int checkpoints = 0;
        while (!work.TryComplete(() => ++checkpoints % 7 == 0)) { }
        Assert.Equal(new[] { first, second, third, 3 }, work.Result!.RowPositions);
        Assert.Equal(new[] { "{0}", "{1}", "{2}" }, work.Result.BranchPaths);
        Assert.True(PanelQueryPolicy.SameOutputFilter(source, query));
        Assert.False(PanelQueryPolicy.SameQuery(source, query));
        Assert.Equal(new[] { 0, 1, 2, 3 }, PanelQueryEngine.Execute(snapshot, source).RowPositions);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => PanelQueryEngine.Execute(snapshot, query, cancelled.Token));
    }

    [Fact]
    public void TextColumnSortKeepsStableTiesAndNullCellsLast()
    {
        var options = new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending, ColumnSortKeys = new PanelColumnSortKeys(new PanelRow?[]
        { new PanelRow(0, "{0}", "Text", "b", null), new PanelRow(1, "{0}", "Text", "A", null), new PanelRow(2, "{0}", "Text", "a", null), null }) };
        Assert.Equal(new[] { 1, 2, 0, 3 }, PanelQueryEngine.Execute(Data(), options).RowPositions);
    }

    [Fact]
    public void SetColumnSortUsesTheBoundedSummaryIncludingNonrepresentativeMembers()
    {
        var snapshot = new PanelSnapshot(new[] { 1, 1, 2, 2, 3 }.Select((value, index) =>
            new PanelRow(index, "{0}", "Number", value.ToString(), value)).ToArray(), new[] { new PanelBranch("{0}", 5) });
        var options = new PanelQueryOptions
        {
            CreateSet = true, SortMode = PanelSortMode.ValueAscending,
            ColumnSortKeys = new PanelColumnSortKeys(new PanelRow?[] { null,
                new PanelRow(1, "{9}", "Text", "z", null), new PanelRow(2, "{9}", "Text", "b", null),
                new PanelRow(3, "{9}", "Text", "a", null), null })
        };
        using var work = PanelQueryEngine.Start(snapshot, options);
        int checkpoints = 0;
        while (!work.TryComplete(() => ++checkpoints % 5 == 0)) { }
        Assert.Equal(new[] { 2, 0, 4 }, work.Result!.RowPositions);
        options.SortMode = PanelSortMode.ValueDescending;
        Assert.Equal(new[] { 0, 2, 4 }, PanelQueryEngine.Execute(snapshot, options).RowPositions);
        options.SortMode = PanelSortMode.ValueAscending;
        options.ColumnSortKeys = new PanelColumnSortKeys(new PanelRow?[] { null,
            new PanelRow(1, "{9}", "Text", "", null), new PanelRow(2, "{9}", "Text", " ", null), null, null });
        Assert.Equal(new[] { 2, 0, 4 }, PanelQueryEngine.Execute(snapshot, options).RowPositions);
    }

    [Fact]
    public void TsvPreservesTitlesMissingCellsAndEmbeddedDelimiters()
    {
        var rows = new (string Path, int Index, IReadOnlyList<string> Values)[]
        { ("{0}", 2, new[] { "a\tb", "", "x\"y\nz" }), ("{2}", 0, new[] { "", "3.20", "" }) };
        Assert.Equal("Path\tIndex\tD\tArea\tName" + Environment.NewLine +
            "{0}\t2\t\"a\tb\"\t\t\"x\"\"y\nz\"" + Environment.NewLine + "{2}\t0\t\t3.20\t" + Environment.NewLine,
            PanelTableTsv.Build(new[] { "D", "Area", "Name" }, rows));
    }
}
