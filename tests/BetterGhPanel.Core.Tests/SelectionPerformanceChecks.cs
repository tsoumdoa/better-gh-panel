using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

internal static class SelectionPerformanceChecks
{
    internal static void Run()
    {
        const int count = 80000;
        var snapshot = new PanelSnapshot(Enumerable.Range(0, count)
            .Select(i => new PanelRow(i % (count / 2), i < count / 2 ? "{0}" : "{1}", "Number", i.ToString(), i)).ToArray(),
            new[] { new PanelBranch("{0}", count / 2), new PanelBranch("{1}", count / 2), new PanelBranch("{2}", 0) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        int visits = 0, notifications = 0;
        Action visit = () => visits++;
        var selection = new PanelRowSelection(visit);
        selection.Changed += (_, __) => notifications++;

        // The selection and action/label preparation executed by a sparse click
        // must not walk the result. Preview drawing and output construction are
        // separate work, and filtered-preview classification is not tested here.
        var clock = Stopwatch.StartNew();
        selection.SelectAt(result, 39000, 39000, preserveSelected: true);
        var first = selection.OrderedPositions(result);
        Check(PanelActionPositions.TryGet(result, count, selection, first, out var trusted, visit) && ReferenceEquals(first, trusted),
            "A current immutable selection is accepted without revalidation");
        Check(PanelItemOrder.TryGetNumber(result, 39000, out int label, visit) && label == 39000,
            "The label uses the captured original item index");
        Check(selection.Indices(result).SequenceEqual(new[] { 39000 }), "The context action uses selected original indices");
        Check(first.SequenceEqual(new[] { 39000 }) && visits <= 6, "The first sparse gesture visits only its row");
        var firstElapsed = clock.Elapsed;
        int firstVisits = visits;
        int firstNotifications = notifications;
        visits = 0;
        clock.Restart();
        selection.SelectAt(result, 39000, 39000, preserveSelected: true);
        var repeated = selection.OrderedPositions(result);
        Check(PanelActionPositions.TryGet(result, count, selection, repeated, out _, visit), "Repeated row action remains valid");
        Check(PanelItemOrder.TryGetNumber(result, 39000, out _, visit), "Repeated label lookup remains valid");
        Check(selection.Indices(result).Length == 1 && ReferenceEquals(first, repeated) && visits <= 3 && notifications == firstNotifications,
            "Repeated unchanged gestures reuse ordering and do not notify");
        var repeatedElapsed = clock.Elapsed;
        Console.WriteLine($"  80,000-row sparse selection/action/label: first {firstElapsed.TotalMilliseconds:F3} ms, repeated {repeatedElapsed.TotalMilliseconds:F3} ms; visits {firstVisits} / {visits}.");

        visits = 0;
        selection.SelectAt(result, 12, 12, toggle: true);
        var changed = selection.OrderedPositions(result);
        Check(changed.SequenceEqual(new[] { 12, 39000 }) && !ReferenceEquals(first, changed) &&
            !selection.IsCurrentOrderedPositions(result, first) && visits <= 5,
            "Membership changes invalidate cached order and preserve view order");
        int actionVisits = 0;
        Check(PanelActionPositions.TryGet(result, count, selection, first, out var oldOrder, () => actionVisits++) &&
            oldOrder.SequenceEqual(new[] { 39000 }) && actionVisits == 2,
            "An old selection is validated as an arbitrary action input");
        bool readOnly = false;
        try { ((IList<int>)changed)[0] = 100; } catch (NotSupportedException) { readOnly = true; }
        Check(readOnly, "A cached selection cannot be mutated by an action consumer");

        visits = 0;
        int beforeBadHit = notifications;
        selection.SelectAt(result, 39000, 12);
        selection.SelectAt(result, 12, -1);
        Check(selection.OrderedPositions(result).SequenceEqual(new[] { 12, 39000 }) && notifications == beforeBadHit,
            "Mismatched and out-of-range painted hits cannot change selection");
        actionVisits = 0;
        Check(PanelActionPositions.TryGet(result, count, selection, new[] { 39000, 12, 39000 }, out var arbitrary, () => actionVisits++) &&
            arbitrary.SequenceEqual(new[] { 12, 39000 }) && actionVisits == 5,
            "Arbitrary action inputs are validated, deduplicated and ordered without a result scan");
        Check(!PanelActionPositions.TryGet(result, count, selection, new[] { -1 }, out _) &&
            !PanelActionPositions.TryGet(result, count, selection, new[] { count }, out _) &&
            !PanelActionPositions.TryGet(result, 10, selection, changed, out _) &&
            !PanelActionPositions.TryGet(result, count, selection, Array.Empty<int>(), out _),
            "Arbitrary, out-of-bounds, source-mismatched and empty action inputs are rejected");
        Check(!PanelItemOrder.TryGetNumber(null, 12, out _, visit) && !PanelItemOrder.TryGetNumber(result, -1, out _, visit),
            "Labels disabled or absent from the result cannot create an entry");
        int disabledLabelVisits = 0;
        Check(!PanelItemOrder.TryGetNumber(null, 12, out _, () => disabledLabelVisits++) && disabledLabelVisits == 0,
            "Disabled item labels do not inspect rows");
        Check(new[] { 0, 4095, 4096, 8191, count - 1 }.All(position =>
            result.TryGetViewIndex(position, out int index) && index == position),
            "Dense reverse lookup crosses allocation-page boundaries");
        Check(PanelItemOrder.TryGetNumber(result, 60001, out int secondBranchLabel) && secondBranchLabel == 20001,
            "Item labels retain branch-local source indices rather than display row numbers");
        var sparseResult = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "> 79990" });
        Check(sparseResult.RowPositions.Count == 9 && sparseResult.TryGetViewIndex(79991, out int sparseIndex) && sparseIndex == 0 &&
            sparseResult.TryGetViewIndex(79999, out sparseIndex) && sparseIndex == 8 && !sparseResult.TryGetViewIndex(0, out _),
            "Sparse reverse lookup contains only matching rows in view order");

        var descending = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        visits = 0;
        int beforeSort = notifications;
        var sorted = selection.OrderedPositions(descending);
        Check(sorted.SequenceEqual(new[] { 39000, 12 }) && !selection.IsCurrentOrderedPositions(descending, changed) &&
            !ReferenceEquals(sorted, changed) && visits <= 7 && notifications == beforeSort,
            "A new result reorders surviving membership without a scan or notification");
        Check(descending.TryGetViewIndex(39000, out int sortedIndex) && sortedIndex == 999 &&
            !descending.TryGetViewIndex(-1, out _) && !descending.TryGetViewIndex(count, out _),
            "The result index reflects sorted order and rejects invalid positions");
        selection.SelectAt(descending, 10, 39989, range: true);
        Check(selection.OrderedPositions(descending).SequenceEqual(Enumerable.Range(10, 3).Reverse()),
            "Range selection retains its anchor across sorting");

        var filtered = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "> 39000" });
        visits = 0;
        int beforeFilter = notifications;
        Check(selection.OrderedPositions(filtered).Count == 0 && notifications == beforeFilter + 1 && visits <= 4,
            "Filtering prunes only the old selected rows and notifies once");
        Check(!PanelActionPositions.TryGet(filtered, count, selection, new[] { 39000 }, out _) &&
            !filtered.TryGetViewIndex(39000, out _) && filtered.TryGetViewIndex(39001, out int filteredIndex) && filteredIndex == 0,
            "Filtered membership is enforced for actions and indexed lookups");
        selection.SelectAt(filtered, 39001, 0, range: true);
        Check(selection.OrderedPositions(filtered).SequenceEqual(new[] { 39001 }), "A filtered-out anchor cannot form a stale range");
        var sameOrderNewResult = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "> 39000" });
        var oldFiltered = selection.OrderedPositions(filtered);
        visits = 0;
        var newFiltered = selection.OrderedPositions(sameOrderNewResult);
        Check(newFiltered.SequenceEqual(oldFiltered) && !ReferenceEquals(oldFiltered, newFiltered) &&
            !selection.IsCurrentOrderedPositions(sameOrderNewResult, oldFiltered) && visits <= 4,
            "Result identity invalidates an ordered list even if membership and ordering are identical");
        var replacement = PanelQueryEngine.Execute(new PanelSnapshot(snapshot.Rows, snapshot.Branches), new PanelQueryOptions());
        Check(selection.OrderedPositions(replacement).Count == 0, "A new snapshot never reuses source positions");

        selection.Clear();
        selection.SelectAt(result, 0, 0);
        visits = 0;
        clock.Restart();
        selection.SelectAt(result, 59999, 59999, range: true);
        var dense = selection.OrderedPositions(result);
        Check(PanelActionPositions.TryGet(result, count, selection, dense, out var denseAction, visit) && ReferenceEquals(dense, denseAction),
            "Dense selection actions reuse the current ordered membership");
        Check(dense.Count == 60000 && dense[0] == 0 && dense[59999] == 59999 && visits <= 3 * count,
            "A dense range preserves view order with bounded linear work");
        var denseElapsed = clock.Elapsed;
        int denseVisits = visits;
        int denseNotifications = notifications;
        visits = 0;
        clock.Restart();
        selection.SelectAt(result, 59999, 59999, range: true);
        Check(ReferenceEquals(dense, selection.OrderedPositions(result)) &&
            PanelActionPositions.TryGet(result, count, selection, dense, out _, visit) && notifications == denseNotifications && visits <= 2 * 60000 + 2,
            "Repeating a dense range reuses its ordered list without another result scan");
        var denseRepeatElapsed = clock.Elapsed;
        int denseRepeatVisits = visits;
        visits = 0;
        clock.Restart();
        selection.SelectAll(result);
        var all = selection.OrderedPositions(result);
        Check(PanelActionPositions.TryGet(result, count, selection, all, out var allAction, visit) && ReferenceEquals(all, allAction),
            "Select-all actions use the already validated result order");
        Check(ReferenceEquals(all, result.RowPositions) && all.Count == count && visits <= 2 * count,
            "Select all makes one membership pass and reuses the immutable result order");
        var allElapsed = clock.Elapsed;
        int allVisits = visits;
        int allNotifications = notifications;
        visits = 0;
        clock.Restart();
        selection.SelectAll(result);
        Check(ReferenceEquals(all, selection.OrderedPositions(result)) &&
            PanelActionPositions.TryGet(result, count, selection, all, out _, visit) && visits == 0 && notifications == allNotifications,
            "Repeated select all performs no row visits or notification");
        var allRepeatElapsed = clock.Elapsed;
        Console.WriteLine($"  Dense range: {denseElapsed.TotalMilliseconds:F3} / {denseRepeatElapsed.TotalMilliseconds:F3} ms; visits {denseVisits} / {denseRepeatVisits}. " +
            $"Select all: {allElapsed.TotalMilliseconds:F3} / {allRepeatElapsed.TotalMilliseconds:F3} ms; visits {allVisits} / {visits}.");
        selection.SelectAt(result, 3, 3, range: true);
        Check(selection.OrderedPositions(result).SequenceEqual(new[] { 0, 1, 2, 3 }), "Select all anchors a later range at the first visible row");
        visits = 0;
        selection.SelectAll(result);
        Check(selection.OrderedPositions(result).Count == count && visits >= count,
            "Membership changes invalidate the repeated select-all shortcut");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
