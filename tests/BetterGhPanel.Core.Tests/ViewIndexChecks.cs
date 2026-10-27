using System;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

internal static class ViewIndexChecks
{
    internal static void Run()
    {
        var snapshot = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{1}", "Number", "2", 2),
            new PanelRow(1, "{1}", "Number", "1", 1),
            new PanelRow(0, "{3}", "Number", "3", 3)
        }, Enumerable.Range(0, 5).Select(i => new PanelBranch("{" + i + "}", i == 1 ? 2 : i == 3 ? 1 : 0)).ToArray());
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
        Check(result.RowPositions.SequenceEqual(new[] { 1, 0, 2 }), "Sorting preserves item indices");
        Check(Entries(result).SequenceEqual(new[] { "{0}", "item:0", "item:1", "{2}", "item:2", "{4}" }),
            "Empty paths appear before, between and after item rows");
        Check(Enumerable.Range(0, 3).Select(result.ViewIndex.OffsetOfItem).SequenceEqual(new[] { 1, 2, 4 }),
            "Jump positions count items rather than empty paths");
        Check(Enumerable.Range(0, 7).Select(result.ViewIndex.ItemAtOrAfter).SequenceEqual(new[] { 0, 0, 1, 2, 2, 3, 3 }),
            "The current jump value skips empty paths");
        Check(result.BranchPaths.SequenceEqual(snapshot.Branches.Select(branch => branch.Path)), "Paths remain in tree order");

        var selected = new PanelRowSelection();
        selected.Select(result, result.RowPositions[1]);
        Check(selected.OrderedPositions(result).SequenceEqual(new[] { 0 }), "View indexing does not change selected source positions");
        var filtered = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = ">1", SearchMode = PanelSearchMode.Value });
        Check(Entries(filtered).SequenceEqual(new[] { "item:0", "item:1" }) && filtered.RowPositions.SequenceEqual(new[] { 0, 2 }),
            "Item filters remove empty paths without changing result membership");
        var path = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { BranchPath = "{2}" });
        Check(Entries(path).SequenceEqual(new[] { "{2}" }), "A path filter can retain one empty branch");
        var set = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { CreateSet = true });
        Check(Entries(set).SequenceEqual(new[] { "{0}", "item:0", "item:1", "{2}", "item:2", "{4}" }),
            "Value-set projection retains empty branch scroll coordinates");
        var lengths = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions
        { CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength });
        selected.SelectAll(lengths, "{0}", includeEmptyBranches: true);
        Check(selected.Count == 1 && selected.BranchPaths(lengths).SequenceEqual(new[] { "{0}" }) && selected.OrderedPositions(lengths).Count == 0,
            "Empty branch-set selection stays path-based");

        const int total = 4096;
        var allEmpty = new PanelSnapshot(Array.Empty<PanelRow>(),
            Enumerable.Range(0, total).Select(i => new PanelBranch("{" + i + "}", 0)).ToArray());
        using var work = PanelQueryEngine.Start(allEmpty, new PanelQueryOptions());
        int checks = 0;
        Check(!work.TryComplete(() => ++checks > total + 20) && work.Result == null,
            "Index construction yields after the initial empty-branch scan");
        var complete = work.Complete();
        Check(complete.ViewIndex.Count == total && Entries(complete).First() == "{0}" && Entries(complete).Last() == "{4095}",
            "All-empty trees index every path across a suspended query");
    }

    private static string[] Entries(PanelQueryResult result) => Enumerable.Range(0, result.ViewIndex.Count).Select(index =>
    {
        Check(result.ViewIndex.TryGet(index, out int item, out string? path), "A valid scroll coordinate resolves");
        return path ?? "item:" + item;
    }).ToArray();

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
