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
    }

    private static PanelSnapshot Sample()
    {
        var values = new[] { 40, 10, 30, 20, 50 };
        return new PanelSnapshot(values.Select((value, index) =>
            new PanelRow(index % 3, index < 3 ? "{0}" : "{1}", "Number", value.ToString(), value)).ToArray(),
            new[] { new PanelBranch("{0}", 3), new PanelBranch("{1}", 2) });
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
