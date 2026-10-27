using System;
using System.Diagnostics;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

internal static class PreviewMembershipChecks
{
    internal static void Run()
    {
        const int total = 80000;
        var snapshot = new PanelSnapshot(Enumerable.Range(0, total)
            .Select(i => new PanelRow(i, "{0}", "Point", i.ToString(), null)).ToArray(),
            new[] { new PanelBranch("{0}", total) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        foreach (bool labels in new[] { false, true })
        foreach (bool filtered in new[] { false, true })
        {
            int visits = 0, changes = 0;
            var selection = new PanelRowSelection(() => visits++);
            var classification = new PanelPreviewMembership(() => visits++);
            var stopwatch = Stopwatch.StartNew();
            selection.SelectAt(result, 40000, 40000);
            var ordered = selection.OrderedPositions(result);
            Check(PanelActionPositions.TryGet(result, total, selection, ordered, out _), "current selection is accepted");
            classification.UpdateSelection(selection, result, ordered, (_, __) => changes++);
            if (labels) Check(PanelItemOrder.TryGetNumber(result, 40000, out _, () => visits++), "selected label lookup is indexed");
            long firstTicks = stopwatch.ElapsedTicks;
            Check(classification.Contains(40000) && changes == 1 && visits <= 8,
                "first sparse selection and classification visit only selected rows");
            visits = 0; stopwatch.Restart();
            selection.SelectAt(result, 40000, 40000, preserveSelected: true);
            classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
            Check(visits <= 4 && changes == 1, "repeated selection does not classify every result row");
            long repeatTicks = stopwatch.ElapsedTicks;
            visits = 0;
            selection.SelectAt(result, 12, 12, toggle: true);
            classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
            Check(classification.Contains(12) && classification.Contains(40000) && visits <= 11 && changes == 2,
                "sparse toggle updates only changed selection membership");
            visits = 0;
            selection.SelectAll(result);
            var all = selection.OrderedPositions(result);
            classification.UpdateSelection(selection, result, all, (_, __) => changes++);
            Check(classification.Count == total && visits <= total * 5, "dense select-all classification is bounded by selection size");
            visits = 0;
            selection.SelectAt(result, 3, 3, toggle: true);
            var without = selection.OrderedPositions(result);
            Check(PanelActionPositions.TryGet(result, total, selection, without, out var action) && ReferenceEquals(without, action),
                "dense toggle keeps the action list result-bound");
            var removed = classification.UpdateSelection(selection, result, without, (_, __) => changes++);
            if (labels) Check(PanelItemOrder.TryGetNumber(result, 40000, out _, () => visits++), "dense label lookup is indexed");
            Check(!removed.ScanSelection && removed.AddedPosition == -1 && visits <= 5 && without.Count == total - 1 &&
                without[2] == 2 && without[3] == 4 && all[3] == 3 && !classification.Contains(3),
                "one toggle after select-all visits only the changed row and preserves the old ordered list");
            visits = 0;
            selection.SelectAt(result, 3, 3, toggle: true);
            var restored = selection.OrderedPositions(result);
            var added = classification.UpdateSelection(selection, result, restored, (_, __) => changes++);
            Check(!added.ScanSelection && added.AddedPosition == 3 && visits <= 4 && restored.Count == total &&
                without[3] == 4 && classification.Contains(3), "repeated dense toggle updates one classification");
            visits = 0;
            selection.SelectAt(result, 3, 3, preserveSelected: true);
            var same = classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
            Check(!same.ScanSelection && visits <= 2, "repeated dense gesture does not rescan selection");
            selection.Clear();
            selection.SelectAt(result, 10000, 10000);
            selection.SelectAt(result, 69999, 69999, range: true);
            classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
            visits = 0;
            selection.SelectAt(result, 40000, 40000, toggle: true);
            var dense = selection.OrderedPositions(result);
            var denseChange = classification.UpdateSelection(selection, result, dense, (_, __) => changes++);
            Check(!denseChange.ScanSelection && visits <= 4 && dense.Count == 59999 && !classification.Contains(40000),
                "one toggle after a dense range avoids scanning the selected rows");
            visits = 0;
            selection.SelectAt(result, 3, 3);
            classification.UpdateSelection(selection, result, selection.OrderedPositions(result), (_, __) => changes++);
            Check(classification.Count == 1 && classification.Contains(3) && visits <= total + 5,
                "replacing a dense selection removes stale classifications");
            Console.WriteLine($"  {(filtered ? "filtered" : "selected")} preview, labels {labels}: sparse {firstTicks} / {repeatTicks} ticks; no full-result classification.");
        }

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
                Check(before.SequenceEqual(prior), "older result-bound selection lists remain immutable after later toggles");
                Check(randomSelection.OrderedPositions(small).SequenceEqual(expected.OrderBy(value => value)),
                    "the compressed selection index preserves order across sparse and dense toggles");
            }
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
