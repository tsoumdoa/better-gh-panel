using Xunit;
using System;
using System.Linq;
using BetterGhPanel.Core;

public class PreviewMembershipTests
{
    private const int total = 80000;
    private readonly PanelQueryResult result;
    private int visits, changes;
    private readonly PanelRowSelection selection;
    private readonly PanelPreviewMembership classification;

    public PreviewMembershipTests()
    {
        var snapshot = new PanelSnapshot(Enumerable.Range(0, total)
        .Select(i => new PanelRow(i, "{0}", "Point", i.ToString(), null)).ToArray(),
        new[] { new PanelBranch("{0}", total) });
        result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        selection = new PanelRowSelection(() => visits++);
        classification = new PanelPreviewMembership(() => visits++);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SparseClassificationVisitsOnlySelectedRows(bool labels)
    {
        selection.SelectAt(result, 40000, 40000);
        var ordered = selection.OrderedPositions(result);
        Assert.True(PanelActionPositions.TryGet(result, total, selection, ordered, out _), "current selection is accepted");
        classification.UpdateSelection(selection, result, ordered, (_, __) => changes++);
        if (labels) Assert.True(PanelItemOrder.TryGetNumber(result, 40000, out _, () => visits++), "selected label lookup is indexed");
        Assert.True(classification.Contains(40000) && changes == 1 && visits <= 8,
            "first sparse selection and classification visit only selected rows");
    }

    [Fact]
    public void RepeatedSparseSelectionDoesNotReclassifyRows()
    {
        selection.SelectAt(result, 40000, 40000);
        classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
        visits = 0;
        selection.SelectAt(result, 40000, 40000, preserveSelected: true);
        classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
        Assert.True(visits <= 4 && changes == 1, "repeated selection does not classify every result row");
    }

    [Fact]
    public void SparseTogglesClassifyOnlyChangedMembership()
    {
        selection.SelectAt(result, 40000, 40000);
        classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
        visits = 0;
        selection.SelectAt(result, 12, 12, toggle: true);
        classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
        Assert.True(classification.Contains(12) && classification.Contains(40000) && visits <= 11 && changes == 2,
            "sparse toggle updates only changed selection membership");
    }

    [Fact]
    public void SelectAllClassificationIsBoundedBySelectionSize()
    {
        visits = 0;
        selection.SelectAll(result);
        var all = selection.OrderedPositions(result);
        classification.UpdateSelection(selection, result, all, (_, __) => changes++);
        Assert.True(classification.Count == total && visits <= total * 5, "dense select-all classification is bounded by selection size");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingOneSelectedRowDoesNotScanDenseMembership(bool labels)
    {
        selection.SelectAll(result);
        var all = selection.OrderedPositions(result);
        classification.UpdateSelection(selection, result, all, (_, __) => changes++);
        visits = 0;
        selection.SelectAt(result, 3, 3, toggle: true);
        var without = selection.OrderedPositions(result);
        Assert.True(PanelActionPositions.TryGet(result, total, selection, without, out var action) && ReferenceEquals(without, action),
            "dense toggle keeps the action list result-bound");
        var removed = classification.UpdateSelection(selection, result, without, (_, __) => changes++);
        if (labels) Assert.True(PanelItemOrder.TryGetNumber(result, 40000, out _, () => visits++), "dense label lookup is indexed");
        Assert.True(!removed.ScanSelection && removed.AddedPosition == -1 && visits <= 5 && without.Count == total - 1 &&
            without[2] == 2 && without[3] == 4 && all[3] == 3 && !classification.Contains(3),
            "one toggle after select-all visits only the changed row and preserves the old ordered list");
    }

    [Fact]
    public void RestoringOneSelectedRowUpdatesOneClassification()
    {
        selection.SelectAll(result);
        var all = selection.OrderedPositions(result);
        classification.UpdateSelection(selection, result, all, (_, __) => changes++);
        selection.SelectAt(result, 3, 3, toggle: true);
        var without = selection.OrderedPositions(result);
        classification.UpdateSelection(selection, result, without, (_, __) => changes++);
        visits = 0;
        selection.SelectAt(result, 3, 3, toggle: true);
        var restored = selection.OrderedPositions(result);
        var added = classification.UpdateSelection(selection, result, restored, (_, __) => changes++);
        Assert.True(!added.ScanSelection && added.AddedPosition == 3 && visits <= 4 && restored.Count == total &&
            without[3] == 4 && classification.Contains(3), "repeated dense toggle updates one classification");
    }

    [Fact]
    public void RepeatedDenseGesturesDoNotRescanSelection()
    {
        selection.SelectAll(result);
        var all = selection.OrderedPositions(result);
        classification.UpdateSelection(selection, result, all, (_, __) => changes++);
        selection.SelectAt(result, 3, 3, toggle: true);
        var without = selection.OrderedPositions(result);
        classification.UpdateSelection(selection, result, without, (_, __) => changes++);
        selection.SelectAt(result, 3, 3, toggle: true);
        classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
        visits = 0;
        selection.SelectAt(result, 3, 3, preserveSelected: true);
        var same = classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
        Assert.True(!same.ScanSelection && visits <= 2, "repeated dense gesture does not rescan selection");
    }

    [Fact]
    public void DenseRangeTogglesDoNotScanSelectedRows()
    {
        selection.Clear();
        selection.SelectAt(result, 10000, 10000);
        selection.SelectAt(result, 69999, 69999, range: true);
        classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
        visits = 0;
        selection.SelectAt(result, 40000, 40000, toggle: true);
        var dense = selection.OrderedPositions(result);
        var denseChange = classification.UpdateSelection(selection, result, dense, (_, __) => changes++);
        Assert.True(!denseChange.ScanSelection && visits <= 4 && dense.Count == 59999 && !classification.Contains(40000),
            "one toggle after a dense range avoids scanning the selected rows");
    }

    [Fact]
    public void ReplacingDenseSelectionRemovesStaleClassifications()
    {
        selection.SelectAt(result, 10000, 10000);
        selection.SelectAt(result, 69999, 69999, range: true);
        classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
        visits = 0;
        selection.SelectAt(result, 3, 3);
        classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
        Assert.True(classification.Count == 1 && classification.Contains(3) && visits <= total + 5,
            "replacing a dense selection removes stale classifications");
    }

    [Fact]
    public void SeededTogglesPreserveOrderAndPreviouslyReturnedLists()
    {
        var smallSnapshot = new PanelSnapshot(Enumerable.Range(0, 257)
            .Select(i => new PanelRow(i, "{0}", "Point", i.ToString(), null)).ToArray(), new[] { new PanelBranch("{0}", 257) });
        var small = PanelQueryEngine.Execute(smallSnapshot, new PanelQueryOptions());
        var randomSelection = new PanelRowSelection();
        var expected = new System.Collections.Generic.HashSet<int>();
        var random = new Random(8372);
        for (int i = 0; i < 2000; i++)
        {
            var before = randomSelection.OrderedPositions(small);
            int[] prior = i % 31 == 0 ? before.ToArray() : Array.Empty<int>();
            int position = random.Next(257);
            randomSelection.SelectAt(small, position, position, toggle: true);
            if (!expected.Remove(position)) expected.Add(position);
            if (i % 31 == 0)
            {
                Assert.True(before.SequenceEqual(prior), "older result-bound selection lists remain immutable after later toggles");
                Assert.True(randomSelection.OrderedPositions(small).SequenceEqual(expected.OrderBy(value => value)),
                    "the compressed selection index preserves order across sparse and dense toggles");
            }
        }
    }

}
