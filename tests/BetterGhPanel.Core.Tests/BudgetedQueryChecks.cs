using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;

internal static class BudgetedQueryChecks
{
    internal static void Run()
    {
        NoRowOrBranchCutoff();
        ResumeScanOnWorker();
        YieldThroughSorting();
        CancellationAndDisposal();
    }

    private static void NoRowOrBranchCutoff()
    {
        var snapshot = Numbers(4096);
        using var work = PanelQueryEngine.Start(snapshot, new PanelQueryOptions());
        Check(!work.TryComplete(TimeSpan.Zero) && work.Result == null, "An exhausted budget yields without publishing partial data");
        Check(work.TryComplete(() => false) && work.Result!.RowPositions.Count == 4096,
            "An available budget can finish a tree larger than the old row limit");
        Check(work.TryComplete(TimeSpan.Zero) && ReferenceEquals(work.Result, work.Complete()), "Completed work reuses its result");

        var emptyBranches = new PanelSnapshot(Array.Empty<PanelRow>(),
            Enumerable.Range(0, 4096).Select(i => new PanelBranch("{" + i + "}", 0)).ToArray());
        using var branches = PanelQueryEngine.Start(emptyBranches, new PanelQueryOptions());
        int checks = 0;
        Check(!branches.TryComplete(() => ++checks > 10) && branches.Result == null, "The deadline is checked during empty-branch scanning");
        Check(branches.Complete().BranchPaths.Count == 4096, "Branch work resumes without dropping or duplicating paths");
    }

    private static void ResumeScanOnWorker()
    {
        var snapshot = Numbers(80);
        var reads = new MeasurementReads(snapshot.Rows.Count);
        var options = new PanelQueryOptions();
        using var work = PanelQueryEngine.Start(snapshot, options, measuredGeometry: reads);
        options.Search = "> 1000"; // Deferred work must retain its captured options.
        int checks = 0;
        Check(!work.TryComplete(() => ++checks > 17), "Query yields partway through its scan");
        Check(reads.Reads.Sum() > 0 && reads.Reads.Sum() < snapshot.Rows.Count && work.Result == null,
            "Partial scan has done work but has no public result");
        int uiThread = Environment.CurrentManagedThreadId;
        var result = Task.Run(() =>
        {
            Check(Environment.CurrentManagedThreadId != uiThread, "Continuation runs on a worker thread");
            return work.Complete();
        }).GetAwaiter().GetResult();
        Check(reads.Reads.All(count => count == 1), "Handoff resumes the scan instead of evaluating earlier rows again");
        Check(result.RowPositions.SequenceEqual(Enumerable.Range(0, 80)) && result.Statistics.NumericCount == 80,
            "Worker continuation preserves options, source order and accumulated statistics");
    }

    private static void YieldThroughSorting()
    {
        var snapshot = Numbers(24);
        var reads = new MeasurementReads(snapshot.Rows.Count);
        using var work = PanelQueryEngine.Start(snapshot,
            new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending }, measuredGeometry: reads);
        int slices = 0, sortingSlices = 0;
        while (true)
        {
            int checks = 0;
            bool complete = work.TryComplete(() => ++checks > 1);
            if (complete) break;
            Check(work.Result == null, "A yielded query does not expose partial results");
            if (reads.Reads.All(count => count == 1)) sortingSlices++;
            if (++slices > 1000) throw new InvalidOperationException("Query made no progress across slices");
        }
        var result = work.Result!;
        var expected = Enumerable.Range(0, 24).OrderByDescending(i => i % 7).ThenBy(i => i);
        Check(sortingSlices > 24 && result.RowPositions.SequenceEqual(expected),
            "Sort comparisons yield and resume with stable ordering, including equal values");
        Check(reads.Reads.All(count => count == 1) && result.Statistics.ItemCount == 24 &&
            result.Statistics.BranchCount == snapshot.Branches.Count && result.Statistics.Minimum == 0 &&
            result.Statistics.Maximum == 6 && Math.Abs(result.Statistics.Mean!.Value - Enumerable.Range(0, 24).Average(i => i % 7)) < 1e-12,
            "Repeated yields preserve rows, empty branches and full statistics");
    }

    private static void CancellationAndDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        using var work = PanelQueryEngine.Start(Numbers(80), new PanelQueryOptions(), cancellation.Token);
        int checks = 0;
        Check(!work.TryComplete(() => ++checks > 17), "Cancellation fixture has a suspended scan");
        cancellation.Cancel();
        try { work.Complete(); throw new InvalidOperationException("Canceled work completed"); }
        catch (OperationCanceledException) { }
        Check(work.Result == null, "Cancellation after handoff cannot publish a result");
        using var disposed = PanelQueryEngine.Start(Numbers(8), new PanelQueryOptions());
        disposed.Dispose();
        try { disposed.Complete(); throw new InvalidOperationException("Disposed work completed"); }
        catch (ObjectDisposedException) { }
    }

    private static PanelSnapshot Numbers(int count) => new PanelSnapshot(
        Enumerable.Range(0, count).Select(i => new PanelRow(i, "{0}", "Number", (i % 7).ToString(), i % 7)).ToArray(),
        new[] { new PanelBranch("{0}", count), new PanelBranch("{1}", 0) });

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class MeasurementReads : IReadOnlyDictionary<int, PanelGeometryInfo>
    {
        internal readonly int[] Reads;
        internal MeasurementReads(int count) => Reads = new int[count];
        public bool TryGetValue(int key, out PanelGeometryInfo value)
        {
            Reads[key]++;
            value = null!;
            return false;
        }
        public int Count => 0;
        public IEnumerable<int> Keys => Array.Empty<int>();
        public IEnumerable<PanelGeometryInfo> Values => Array.Empty<PanelGeometryInfo>();
        public PanelGeometryInfo this[int key] => throw new KeyNotFoundException();
        public bool ContainsKey(int key) => false;
        public IEnumerator<KeyValuePair<int, PanelGeometryInfo>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<int, PanelGeometryInfo>>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
