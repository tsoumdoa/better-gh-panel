using Xunit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using BetterGhPanel.Core;
using static PanelQueryTestData;

public class QueryExecutionTests
{
    [Fact]
    public void SearchMatchesValuePathAndType()
    {
        Assert.Equal(new[] { 2 }, Query(new PanelQueryOptions { Search = "ALpHa" }).RowPositions);
        Assert.Equal(new[] { 0, 1, 3 }, Query(new PanelQueryOptions { Search = "nUmBeR" }).RowPositions);
        Assert.Equal(new[] { 2 }, Query(new PanelQueryOptions { Search = "{0;1;2}" }).RowPositions);
        Assert.Equal(0, Query(new PanelQueryOptions { Search = "absent" }).Statistics.BranchCount);
    }

    [Fact]
    public void NumericComparisonsUseInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert.Equal(new[] { 0, 3 }, Query(new PanelQueryOptions { Search = "> 2.5" }).RowPositions);
            Assert.Equal(new[] { 0, 1, 3 }, Query(new PanelQueryOptions { Search = ">= 2" }).RowPositions);
            Assert.Equal(new[] { 1 }, Query(new PanelQueryOptions { Search = "< 10" }).RowPositions);
            Assert.Equal(new[] { 1 }, Query(new PanelQueryOptions { Search = "<= 2" }).RowPositions);
            Assert.Equal(new[] { 0, 3 }, Query(new PanelQueryOptions { Search = "= 1e1" }).RowPositions);
            Assert.Equal(new[] { 0, 3 }, Query(new PanelQueryOptions { Search = "==10" }).RowPositions);
            Assert.Equal(0, Query(new PanelQueryOptions { Search = "> nonsense" }).RowPositions.Count);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void BranchSelectionPreservesTreeSemantics()
    {
        Assert.Equal(new[] { 0, 1 }, Query(new PanelQueryOptions { BranchPath = "{0;1}" }).RowPositions);
        var descendants = Query(new PanelQueryOptions { BranchPath = "{0;1}", BranchPrefix = true });
        Assert.Equal(new[] { 0, 1, 2 }, descendants.RowPositions);
        Assert.Equal(2, descendants.Statistics.BranchCount);
        Assert.Equal(1, Query(new PanelQueryOptions { BranchPath = "{9}" }).Statistics.BranchCount);
        Assert.Equal(0, Query(new PanelQueryOptions { BranchPath = "{9}", Search = "hello" }).Statistics.BranchCount);
        Assert.Equal(0, Query(new PanelQueryOptions { BranchPath = "{404}" }).Statistics.BranchCount);
    }

    [Fact]
    public void TypeAndInvalidFilters()
    {
        Assert.Equal(new[] { 0, 1, 3 }, Query(new PanelQueryOptions { TypeName = "number" }).RowPositions);
        Assert.Equal(new[] { 4, 5 }, Query(new PanelQueryOptions { OnlyInvalid = true }).RowPositions);
        Assert.Equal(new[] { 5 }, Query(new PanelQueryOptions { OnlyInvalid = true, TypeName = "text" }).RowPositions);
    }

    [Fact]
    public void StableMixedSorting()
    {
        var ascending = Query(new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
        Assert.Equal(new[] { 1, 0, 2, 3, 5, 4 }, ascending.RowPositions);
        var descending = Query(new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        Assert.Equal(new[] { 0, 1, 2, 3, 5, 4 }, descending.RowPositions);
        var first = descending.Snapshot.Rows[descending.RowPositions[0]];
        Assert.Equal(0, first.OriginalIndex);
        Assert.Equal("{0;1}", first.Path);
        var mixed = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "ZType", "A", null),
            new PanelRow(1, "{0}", "AType", "Z", null),
            new PanelRow(2, "{0}", "AType", "A", null),
            new PanelRow(3, "{0}", "AType", "a", null)
        }, new[] { new PanelBranch("{0}", 4) });
        Assert.Equal(new[] { 1, 2, 3, 0 }, PanelQueryEngine.Execute(mixed,
            new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending }).RowPositions);
    }

    [Fact]
    public void ImmutableSnapshotAndView()
    {
        var rows = new List<PanelRow>(Sample().Rows);
        var originalRows = rows.ToArray();
        var branches = new List<PanelBranch>(Sample().Branches);
        var snapshot = new PanelSnapshot(rows, branches);
        rows.Clear(); branches.Clear();
        Assert.Equal(6, snapshot.Rows.Count);
        Assert.Equal(4, snapshot.Branches.Count);
        Assert.Equal(3, snapshot.TypeCounts["number"]);
        PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions()).RowPositions);
        Assert.Equal(originalRows, snapshot.Rows);
        Assert.ThrowsAny<NotSupportedException>(() => ((IList<PanelRow>)snapshot.Rows)[0] = snapshot.Rows[1]);
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Assert.ThrowsAny<NotSupportedException>(() => ((IList<int>)result.RowPositions)[0] = 5);
    }

    [Fact]
    public void OddLengthSorting()
    {
        foreach (var length in new[] { 1, 3, 7, 17, 257 })
        {
            var rows = Enumerable.Range(0, length).Select(index =>
                new PanelRow(index, "{0}", "Number", string.Empty, (index * 7) % 11)).ToArray();
            var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", length) });
            var ascending = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
            var descending = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
            Assert.Equal(rows.OrderBy(row => row.NumericValue), ascending.RowPositions.Select(position => snapshot.Rows[position]));
            Assert.Equal(rows.OrderByDescending(row => row.NumericValue), descending.RowPositions.Select(position => snapshot.Rows[position]));
        }
    }

    [Fact]
    public void Cancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PanelQueryEngine.Execute(Sample(), new PanelQueryOptions(), source.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => PanelQueryEngine.Execute(PanelSnapshot.Empty, new PanelQueryOptions(), source.Token));
    }

    [Fact]
    public void LargeData()
    {
        const int count = 262144;
        const int branchSize = 1024;
        var rows = new PanelRow[count];
        var branches = new PanelBranch[count / branchSize];
        for (var branch = 0; branch < branches.Length; branch++)
        {
            var path = "{" + branch.ToString(CultureInfo.InvariantCulture) + "}";
            branches[branch] = new PanelBranch(path, branchSize);
            for (var index = 0; index < branchSize; index++)
            {
                var position = branch * branchSize + index;
                rows[position] = new PanelRow(index, path, "Number",
                    position.ToString(CultureInfo.InvariantCulture), position);
            }
        }
        var snapshot = new PanelSnapshot(rows, branches);
        var watch = Stopwatch.StartNew();
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        watch.Stop();
        Assert.Equal(count, result.Statistics.ItemCount);
        Assert.Equal(count / branchSize, result.Statistics.BranchCount);

        Assert.Equal(Enumerable.Range(0, branches.Length).SelectMany(branch =>
            Enumerable.Range(branch * branchSize, branchSize).Reverse()), result.RowPositions);
        Console.WriteLine($"  {count:N0} rows filtered, aggregated and sorted in {watch.ElapsedMilliseconds} ms (local measurement).");
    }
}
