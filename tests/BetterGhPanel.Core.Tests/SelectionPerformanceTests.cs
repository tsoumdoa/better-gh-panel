using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using BetterGhPanel.Core;

public class SelectionPerformanceTests
{
    private const int count = 80000;
    private readonly PanelSnapshot snapshot;
    private readonly PanelQueryResult result;
    private int visits, notifications;
    private readonly Action visit;
    private readonly PanelRowSelection selection;

    public SelectionPerformanceTests()
    {
        snapshot = new PanelSnapshot(Enumerable.Range(0, count)
            .Select(i => new PanelRow(i % (count / 2), i < count / 2 ? "{0}" : "{1}", "Number", i.ToString(), i)).ToArray(),
            new[] { new PanelBranch("{0}", count / 2), new PanelBranch("{1}", count / 2), new PanelBranch("{2}", 0) });
        result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        visit = () => visits++;
        selection = new PanelRowSelection(visit);
        selection.Changed += (_, __) => notifications++;
    }

    [Fact]
    public void SparseSelectionActionsAndLabelsVisitOnlySelectedRows()
    {
        selection.SelectAt(result, 39000, 39000, preserveSelected: true);
        var first = selection.OrderedPositions(result);
        Assert.True(PanelActionPositions.TryGet(result, count, selection, first, out var trusted, visit) && ReferenceEquals(first, trusted),
            "A current immutable selection is accepted without revalidation");
        Assert.True(PanelItemOrder.TryGetNumber(result, 39000, out int label, visit) && label == 39000,
            "The label uses the captured original item index");
        Assert.True(selection.Indices(result).SequenceEqual(new[] { 39000 }), "The context action uses selected original indices");
        Assert.True(first.SequenceEqual(new[] { 39000 }) && visits <= 6, "The first sparse gesture visits only its row");
    }

    [Fact]
    public void RepeatedSparseGesturesReuseOrderWithoutNotification()
    {
        selection.SelectAt(result, 39000, 39000, preserveSelected: true);
        var first = selection.OrderedPositions(result);
        int firstNotifications = notifications;
        visits = 0;
        selection.SelectAt(result, 39000, 39000, preserveSelected: true);
        var repeated = selection.OrderedPositions(result);
        Assert.True(PanelActionPositions.TryGet(result, count, selection, repeated, out _, visit), "Repeated row action remains valid");
        Assert.True(PanelItemOrder.TryGetNumber(result, 39000, out _, visit), "Repeated label lookup remains valid");
        Assert.True(selection.Indices(result).Length == 1 && ReferenceEquals(first, repeated) && visits <= 3 && notifications == firstNotifications,
            "Repeated unchanged gestures reuse ordering and do not notify");
    }

    [Fact]
    public void MembershipChangesInvalidateCachedOrderAndOldActionsAreValidated()
    {
        selection.SelectAt(result, 39000, 39000, preserveSelected: true);
        var first = selection.OrderedPositions(result);
        visits = 0;
        selection.SelectAt(result, 12, 12, toggle: true);
        var changed = selection.OrderedPositions(result);
        Assert.True(changed.SequenceEqual(new[] { 12, 39000 }) && !ReferenceEquals(first, changed) &&
            !selection.IsCurrentOrderedPositions(result, first) && visits <= 5,
            "Membership changes invalidate cached order and preserve view order");
        int actionVisits = 0;
        Assert.True(PanelActionPositions.TryGet(result, count, selection, first, out var oldOrder, () => actionVisits++) &&
            oldOrder.SequenceEqual(new[] { 39000 }) && actionVisits == 2,
            "An old selection is validated as an arbitrary action input");
        bool readOnly = false;
        try { ((IList<int>)changed)[0] = 100; } catch (NotSupportedException) { readOnly = true; }
        Assert.True(readOnly, "A cached selection cannot be mutated by an action consumer");
    }

    [Fact]
    public void MismatchedPaintedHitsLeaveSelectionUnchanged()
    {
        selection.SelectAt(result, 39000, 39000, preserveSelected: true);
        _ = selection.OrderedPositions(result);
        selection.SelectAt(result, 12, 12, toggle: true);
        _ = selection.OrderedPositions(result);
        visits = 0;
        int beforeBadHit = notifications;
        selection.SelectAt(result, 39000, 12);
        selection.SelectAt(result, 12, -1);
        Assert.True(selection.OrderedPositions(result).SequenceEqual(new[] { 12, 39000 }) && notifications == beforeBadHit,
            "Mismatched and out-of-range painted hits cannot change selection");
    }

    [Fact]
    public void ArbitraryActionsValidateDeduplicateAndRejectInvalidPositions()
    {
        selection.SelectAt(result, 39000, 39000, preserveSelected: true);
        var first = selection.OrderedPositions(result);
        selection.SelectAt(result, 12, 12, toggle: true);
        var changed = selection.OrderedPositions(result);
        int actionVisits = 0;
        Assert.True(PanelActionPositions.TryGet(result, count, selection, new[] { 39000, 12, 39000 }, out var arbitrary, () => actionVisits++) &&
            arbitrary.SequenceEqual(new[] { 12, 39000 }) && actionVisits == 5,
            "Arbitrary action inputs are validated, deduplicated and ordered without a result scan");
        Assert.True(!PanelActionPositions.TryGet(result, count, selection, new[] { -1 }, out _) &&
            !PanelActionPositions.TryGet(result, count, selection, new[] { count }, out _) &&
            !PanelActionPositions.TryGet(result, 10, selection, changed, out _) &&
            !PanelActionPositions.TryGet(result, count, selection, Array.Empty<int>(), out _),
            "Arbitrary, out-of-bounds, source-mismatched and empty action inputs are rejected");
    }

    [Fact]
    public void DisabledOrMissingLabelsDoNotInspectRows()
    {
        Assert.True(!PanelItemOrder.TryGetNumber(null, 12, out _, visit) && !PanelItemOrder.TryGetNumber(result, -1, out _, visit),
            "Labels disabled or absent from the result cannot create an entry");
        int disabledLabelVisits = 0;
        Assert.True(!PanelItemOrder.TryGetNumber(null, 12, out _, () => disabledLabelVisits++) && disabledLabelVisits == 0,
            "Disabled item labels do not inspect rows");
    }

    [Fact]
    public void ReverseLookupIndexesDenseAndSparseViewsAndBranchLocalLabels()
    {
        Assert.True(new[] { 0, 4095, 4096, 8191, count - 1 }.All(position =>
            result.TryGetViewIndex(position, out int index) && index == position),
            "Dense reverse lookup crosses allocation-page boundaries");
        Assert.True(PanelItemOrder.TryGetNumber(result, 60001, out int secondBranchLabel) && secondBranchLabel == 20001,
            "Item labels retain branch-local source indices rather than display row numbers");
        var sparseResult = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "> 79990" });
        Assert.True(sparseResult.RowPositions.Count == 9 && sparseResult.TryGetViewIndex(79991, out int sparseIndex) && sparseIndex == 0 &&
            sparseResult.TryGetViewIndex(79999, out sparseIndex) && sparseIndex == 8 && !sparseResult.TryGetViewIndex(0, out _),
            "Sparse reverse lookup contains only matching rows in view order");
    }

    [Fact]
    public void SortingReordersSelectionAndPreservesTheRangeAnchor()
    {
        selection.SelectAt(result, 39000, 39000, preserveSelected: true);
        _ = selection.OrderedPositions(result);
        selection.SelectAt(result, 12, 12, toggle: true);
        var changed = selection.OrderedPositions(result);
        var descending = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        visits = 0;
        int beforeSort = notifications;
        var sorted = selection.OrderedPositions(descending);
        Assert.True(sorted.SequenceEqual(new[] { 39000, 12 }) && !selection.IsCurrentOrderedPositions(descending, changed) &&
            !ReferenceEquals(sorted, changed) && visits <= 7 && notifications == beforeSort,
            "A new result reorders surviving membership without a scan or notification");
        Assert.True(descending.TryGetViewIndex(39000, out int sortedIndex) && sortedIndex == 999 &&
            !descending.TryGetViewIndex(-1, out _) && !descending.TryGetViewIndex(count, out _),
            "The result index reflects sorted order and rejects invalid positions");
        selection.SelectAt(descending, 10, 39989, range: true);
        Assert.True(selection.OrderedPositions(descending).SequenceEqual(Enumerable.Range(10, 3).Reverse()),
            "Range selection retains its anchor across sorting");
    }

    [Fact]
    public void FilteringPrunesSelectionAndInvalidatesOldResultIdentity()
    {
        selection.SelectAt(result, 39000, 39000, preserveSelected: true);
        var first = selection.OrderedPositions(result);
        selection.SelectAt(result, 12, 12, toggle: true);
        var changed = selection.OrderedPositions(result);
        var descending = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        selection.OrderedPositions(descending);
        selection.SelectAt(descending, 10, 39989, range: true);
        var filtered = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "> 39000" });
        visits = 0;
        int beforeFilter = notifications;
        Assert.True(selection.OrderedPositions(filtered).Count == 0 && notifications == beforeFilter + 1 && visits <= 4,
            "Filtering prunes only the old selected rows and notifies once");
        Assert.True(!PanelActionPositions.TryGet(filtered, count, selection, new[] { 39000 }, out _) &&
            !filtered.TryGetViewIndex(39000, out _) && filtered.TryGetViewIndex(39001, out int filteredIndex) && filteredIndex == 0,
            "Filtered membership is enforced for actions and indexed lookups");
        selection.SelectAt(filtered, 39001, 0, range: true);
        Assert.True(selection.OrderedPositions(filtered).SequenceEqual(new[] { 39001 }), "A filtered-out anchor cannot form a stale range");
        var sameOrderNewResult = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "> 39000" });
        var oldFiltered = selection.OrderedPositions(filtered);
        visits = 0;
        var newFiltered = selection.OrderedPositions(sameOrderNewResult);
        int identityVisits = visits;
        Assert.True(newFiltered.SequenceEqual(oldFiltered) && !ReferenceEquals(oldFiltered, newFiltered) &&
            !selection.IsCurrentOrderedPositions(sameOrderNewResult, oldFiltered) && identityVisits <= 4,
            "Result identity invalidates an ordered list even if membership and ordering are identical");
    }

    [Fact]
    public void ReplacementSnapshotsDiscardSelectedSourcePositions()
    {
        var filtered = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "> 39000" });
        selection.SelectAt(filtered, 39001, 0, range: true);
        Assert.Equal(new[] { 39001 }, selection.OrderedPositions(filtered));
        var replacement = PanelQueryEngine.Execute(new PanelSnapshot(snapshot.Rows, snapshot.Branches), new PanelQueryOptions());
        Assert.True(selection.OrderedPositions(replacement).Count == 0, "A new snapshot never reuses source positions");
    }

    [Fact]
    public void DenseRangesReuseCurrentActionMembershipWithBoundedWork()
    {
        selection.Clear();
        selection.SelectAt(result, 0, 0);
        visits = 0;
        selection.SelectAt(result, 59999, 59999, range: true);
        var dense = selection.OrderedPositions(result);
        Assert.True(PanelActionPositions.TryGet(result, count, selection, dense, out var denseAction, visit) && ReferenceEquals(dense, denseAction),
            "Dense selection actions reuse the current ordered membership");
        Assert.True(dense.Count == 60000 && dense[0] == 0 && dense[59999] == 59999 && visits <= 3 * count,
            "A dense range preserves view order with bounded linear work");
    }

    [Fact]
    public void RepeatedDenseRangesReuseOrderWithoutNotification()
    {
        selection.SelectAt(result, 0, 0);
        selection.SelectAt(result, 59999, 59999, range: true);
        var dense = selection.OrderedPositions(result);
        int denseNotifications = notifications;
        visits = 0;
        selection.SelectAt(result, 59999, 59999, range: true);
        Assert.True(ReferenceEquals(dense, selection.OrderedPositions(result)) &&
            PanelActionPositions.TryGet(result, count, selection, dense, out _, visit) && notifications == denseNotifications && visits <= 2 * 60000 + 2,
            "Repeating a dense range reuses its ordered list without another result scan");
    }

    [Fact]
    public void SelectAllReusesImmutableResultOrderWithBoundedWork()
    {
        selection.SelectAt(result, 0, 0);
        selection.SelectAt(result, 59999, 59999, range: true);
        selection.OrderedPositions(result);
        visits = 0;
        selection.SelectAll(result);
        var all = selection.OrderedPositions(result);
        Assert.True(PanelActionPositions.TryGet(result, count, selection, all, out var allAction, visit) && ReferenceEquals(all, allAction),
            "Select-all actions use the already validated result order");
        Assert.True(ReferenceEquals(all, result.RowPositions) && all.Count == count && visits <= 2 * count,
            "Select all makes one membership pass and reuses the immutable result order");
    }

    [Fact]
    public void RepeatedSelectAllDoesNoRowVisitsOrNotification()
    {
        selection.SelectAll(result);
        var all = selection.OrderedPositions(result);
        int allNotifications = notifications;
        visits = 0;
        selection.SelectAll(result);
        Assert.True(ReferenceEquals(all, selection.OrderedPositions(result)) &&
            PanelActionPositions.TryGet(result, count, selection, all, out _, visit) && visits == 0 && notifications == allNotifications,
            "Repeated select all performs no row visits or notification");
    }

    [Fact]
    public void SelectAllAnchorsLaterRangesAndMembershipChangesInvalidateItsShortcut()
    {
        selection.SelectAll(result);
        _ = selection.OrderedPositions(result);
        selection.SelectAt(result, 3, 3, range: true);
        Assert.True(selection.OrderedPositions(result).SequenceEqual(new[] { 0, 1, 2, 3 }), "Select all anchors a later range at the first visible row");
        visits = 0;
        selection.SelectAll(result);
        Assert.True(selection.OrderedPositions(result).Count == count && visits >= count,
            "Membership changes invalidate the repeated select-all shortcut");
    }

}
