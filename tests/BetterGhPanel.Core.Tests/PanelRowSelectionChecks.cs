using System;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

internal static class PanelRowSelectionChecks
{
    internal static void Run()
    {
        var snapshot = Sample();
        var source = Query(snapshot);
        var selection = new PanelRowSelection();

        selection.Select(source, 1);
        selection.Select(source, 4, toggle: true);
        Expect(selection, source, 1, 4);
        selection.Select(source, 1, toggle: true);
        Expect(selection, source, 4);
        selection.Select(source, 2);
        Expect(selection, source, 2);
        selection.Select(source, 4, toggle: true);
        selection.Select(source, 2, preserveSelected: true);
        Expect(selection, source, 2, 4); // right-click retains the selected group
        selection.Select(source, 0, preserveSelected: true);
        Expect(selection, source, 0); // an unselected row starts a new group

        selection.Select(source, 3, range: true);
        Expect(selection, source, 0, 1, 2, 3);
        selection.Select(source, 1, range: true);
        Expect(selection, source, 0, 1); // repeated Shift-click retains the original anchor
        selection.Select(source, 4, range: true, toggle: true);
        Expect(selection, source, 0, 1, 2, 3, 4);

        selection.Select(source, 0);
        selection.Select(source, 1, toggle: true);
        var ascending = Query(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
        Expect(selection, ascending, 1, 0); // copy order is display order, independent of click order
        selection.Select(ascending, 0, range: true);
        Expect(selection, ascending, 1, 3, 2, 0);

        var filtered = Query(snapshot, new PanelQueryOptions { Search = "> 15", SortMode = PanelSortMode.ValueAscending });
        Expect(selection, filtered, 3, 2, 0); // hidden rows are pruned
        selection.Select(filtered, 4, range: true);
        Expect(selection, filtered, 4); // a filtered-out anchor cannot select an unintended range
        Expect(selection, source, 4); // removing the filter never resurrects discarded rows

        selection.Select(source, -1, toggle: true);
        selection.Select(source, 99);
        Expect(selection, source, 4);
        var empty = Query(snapshot, new PanelQueryOptions { Search = "no such value" });
        Expect(selection, empty);
        selection.Select(empty, 0);
        Expect(selection, empty);

        selection.Select(source, 2);
        var solvedAgain = Query(Sample());
        Expect(selection, solvedAgain); // reusing a source position in a new solution is unsafe
        selection.Select(solvedAgain, 1);
        selection.Clear();
        selection.Select(solvedAgain, 3, range: true);
        Expect(selection, solvedAgain, 3); // reset also discards the old range anchor
        selection.Select(solvedAgain, 3, toggle: true);
        Expect(selection, solvedAgain);

        AcrossBranches();
    }

    private static void AcrossBranches()
    {
        var snapshot = Sample();
        var source = Query(snapshot);
        var selection = new PanelRowSelection();
        selection.SelectIndices(source, new[] { 1 });
        Expect(selection, source, 1, 4);
        selection.SelectIndices(source, new[] { 2 });
        Expect(selection, source, 2); // shorter and empty branches are skipped
        selection.SelectIndices(source, new[] { 0, 1, 0 });
        Expect(selection, source, 0, 1, 3, 4);
        if (!selection.Indices(source).SequenceEqual(new[] { 0, 1 }))
            throw new InvalidOperationException("Selected indices must be distinct and sorted.");

        var ascending = Query(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
        selection.SelectIndices(ascending, new[] { 0 });
        Expect(selection, ascending, 3, 0); // item zero is not displayed row one
        selection.Select(ascending, 1, toggle: true);
        selection.SelectIndices(ascending, selection.Indices(ascending));
        Expect(selection, ascending, 1, 3, 0, 4); // expand each selected index, in view order
        var descending = Query(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        Expect(selection, descending, 4, 0, 3, 1);

        var filtered = Query(snapshot, new PanelQueryOptions { Search = "> 15" });
        selection.SelectIndices(filtered, new[] { 1 });
        Expect(selection, filtered, 4);
        Expect(selection, source, 4); // clearing a filter does not create new selections
        var branch = Query(snapshot, new PanelQueryOptions { BranchPath = "{0}" });
        selection.SelectIndices(branch, new[] { 1 });
        Expect(selection, branch, 1);
        var noMatches = Query(snapshot, new PanelQueryOptions { TypeName = "Point" });
        selection.SelectIndices(noMatches, new[] { 0, 1 });
        Expect(selection, noMatches);

        selection.Select(source, 4);
        selection.SelectIndices(ascending, new[] { 0 });
        selection.Select(ascending, 2, range: true);
        Expect(selection, ascending, 3, 2); // the expansion resets the old range anchor
        selection.SelectIndices(source, Array.Empty<int>());
        Expect(selection, source);
        selection.SelectIndices(source, new[] { -1, 100 });
        Expect(selection, source);
        selection.SelectIndices(source, new[] { 1 });
        Expect(selection, Query(Sample())); // new input invalidates the whole group
    }

    private static PanelSnapshot Sample()
    {
        var values = new[] { 40, 10, 30, 20, 50 };
        return new PanelSnapshot(values.Select((value, index) =>
            new PanelRow(index % 3, index < 3 ? "{0}" : "{1}", "Number", value.ToString(), value)).ToArray(),
            new[] { new PanelBranch("{0}", 3), new PanelBranch("{1}", 2), new PanelBranch("{2}", 0) });
    }

    private static PanelQueryResult Query(PanelSnapshot snapshot, PanelQueryOptions? options = null) =>
        PanelQueryEngine.Execute(snapshot, options ?? new PanelQueryOptions());

    private static void Expect(PanelRowSelection selection, PanelQueryResult result, params int[] expected)
    {
        var actual = selection.OrderedPositions(result);
        if (selection.Count != expected.Length || !actual.SequenceEqual(expected) || expected.Any(position => !selection.Contains(position)))
            throw new InvalidOperationException($"Expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}].");
    }
}
