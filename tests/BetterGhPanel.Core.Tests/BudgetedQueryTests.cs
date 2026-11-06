using Xunit;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;

public class BudgetedQueryTests
{


    [Fact]
    public void NoRowOrBranchCutoff()
    {
        var snapshot = Numbers(4096);
        using var work = PanelQueryEngine.Start(snapshot, new PanelQueryOptions());
        Assert.True(!work.TryComplete(TimeSpan.Zero) && work.Result == null, "An exhausted budget yields without publishing partial data");
        Assert.True(work.TryComplete(() => false) && work.Result!.RowPositions.Count == 4096,
            "An available budget can finish a tree larger than the old row limit");
        Assert.True(work.TryComplete(TimeSpan.Zero) && ReferenceEquals(work.Result, work.Complete()), "Completed work reuses its result");

        var emptyBranches = new PanelSnapshot(Array.Empty<PanelRow>(),
            Enumerable.Range(0, 4096).Select(i => new PanelBranch("{" + i + "}", 0)).ToArray());
        using var branches = PanelQueryEngine.Start(emptyBranches, new PanelQueryOptions());
        int checks = 0;
        Assert.True(!branches.TryComplete(() => ++checks > 10) && branches.Result == null, "The deadline is checked during empty-branch scanning");
        Assert.True(branches.Complete().BranchPaths.Count == 4096, "Branch work resumes without dropping or duplicating paths");
    }

    [Fact]
    public async Task ResumeScanOnWorker()
    {
        await Task.Factory.StartNew(ResumeScanOnWorkerCore, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static void ResumeScanOnWorkerCore()
    {
        var snapshot = Numbers(80);
        var reads = new MeasurementReads(snapshot.Rows.Count);
        var options = new PanelQueryOptions();
        using var work = PanelQueryEngine.Start(snapshot, options, measuredGeometry: reads);
        options.Search = "> 1000"; // Deferred work must retain its captured options.
        int checks = 0;
        Assert.True(!work.TryComplete(() => ++checks > 17), "Query yields partway through its scan");
        Assert.True(reads.Reads.Sum() > 0 && reads.Reads.Sum() < snapshot.Rows.Count && work.Result == null,
            "Partial scan has done work but has no public result");
        int uiThread = Environment.CurrentManagedThreadId;
        var result = Task.Run(() =>
        {
            Assert.True(Environment.CurrentManagedThreadId != uiThread, "Continuation runs on a worker thread");
            return work.Complete();
        }).GetAwaiter().GetResult();
        Assert.True(reads.Reads.All(count => count == 1), "Handoff resumes the scan instead of evaluating earlier rows again");
        Assert.True(result.RowPositions.SequenceEqual(Enumerable.Range(0, 80)) && result.Statistics.NumericCount == 80,
            "Worker continuation preserves options, source order and accumulated statistics");
    }

    [Fact]
    public void YieldThroughSorting()
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
            Assert.True(work.Result == null, "A yielded query does not expose partial results");
            if (reads.Reads.All(count => count == 1)) sortingSlices++;
            if (++slices > 1000) throw new InvalidOperationException("Query made no progress across slices");
        }
        var result = work.Result!;
        var expected = Enumerable.Range(0, 24).OrderByDescending(i => i % 7).ThenBy(i => i);
        Assert.True(sortingSlices > 24 && result.RowPositions.SequenceEqual(expected),
            "Sort comparisons yield and resume with stable ordering, including equal values");
        Assert.True(result.RowPositions.Select((position, index) => result.TryGetViewIndex(position, out int viewIndex) && viewIndex == index).All(found => found),
            "The reverse index is complete and agrees with every row after cooperative handoff");
        Assert.True(reads.Reads.All(count => count == 1) && result.Statistics.ItemCount == 24 &&
            result.Statistics.BranchCount == snapshot.Branches.Count && result.Statistics.Minimum == 0 &&
            result.Statistics.Maximum == 6 && Math.Abs(result.Statistics.Mean!.Value - Enumerable.Range(0, 24).Average(i => i % 7)) < 1e-12,
            "Repeated yields preserve rows, empty branches and full statistics");
    }

    [Fact]
    public void CancellationAndDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        using var work = PanelQueryEngine.Start(Numbers(80), new PanelQueryOptions(), cancellation.Token);
        int checks = 0;
        Assert.True(!work.TryComplete(() => ++checks > 17), "Cancellation fixture has a suspended scan");
        cancellation.Cancel();
        try { work.Complete(); throw new InvalidOperationException("Canceled work completed"); }
        catch (OperationCanceledException) { }
        Assert.True(work.Result == null, "Cancellation after handoff cannot publish a result");
        using var disposed = PanelQueryEngine.Start(Numbers(8), new PanelQueryOptions());
        disposed.Dispose();
        try { disposed.Complete(); throw new InvalidOperationException("Disposed work completed"); }
        catch (ObjectDisposedException) { }
    }

    [Fact]
    public void YieldAndCancelThroughResultIndex()
    {
        var snapshot = Numbers(512);
        var reads = new MeasurementReads(snapshot.Rows.Count);
        using var cancellation = new CancellationTokenSource();
        using var work = PanelQueryEngine.Start(snapshot, new PanelQueryOptions(), cancellation.Token, reads);
        int afterScan = 0;
        // Seven distinct values precede indexing. Sixty yield checks after the
        // scan therefore suspend inside the result-local position-index build.
        Assert.True(!work.TryComplete(() => reads.Reads.All(count => count == 1) && ++afterScan > 60) && work.Result == null,
            "Position-index construction yields without publishing a partial result");
        cancellation.Cancel();
        try { work.Complete(); throw new InvalidOperationException("Canceled position index completed"); }
        catch (OperationCanceledException) { }
        Assert.True(work.Result == null, "Cancellation during indexing cannot publish incomplete lookup state");
    }

    [Fact]
    public void YieldAndCancelThroughBranchSorting()
    {
        var snapshot = new PanelSnapshot(Enumerable.Range(0, 96).Select(index =>
            new PanelRow(index, "{" + index % 4 + "}", "Number", (index % 7).ToString(), index % 7)).ToArray(),
            new[] { 2, 0, 3, 1, 9 }.Select(index => new PanelBranch("{" + index + "}", index == 9 ? 0 : 24)).ToArray());
        foreach (var direction in new[] { PanelSortMode.ValueAscending, PanelSortMode.ValueDescending })
        {
            using var work = PanelQueryEngine.Start(snapshot, new PanelQueryOptions { SortMode = direction });
            var slices = 0;
            while (true)
            {
                int checks = 0;
                if (work.TryComplete(() => ++checks > 1)) break;
                Assert.True(work.Result == null, "Branch grouping and merge slices never publish partial results");
                Assert.True(++slices < 3000, "Branch sorting advances across tiny cooperative budgets");
            }
            var expected = snapshot.Branches.SelectMany(branch =>
            {
                var positions = Enumerable.Range(0, snapshot.Rows.Count).Where(index => snapshot.Rows[index].Path == branch.Path);
                return direction == PanelSortMode.ValueAscending
                    ? positions.OrderBy(index => snapshot.Rows[index].NumericValue)
                    : positions.OrderByDescending(index => snapshot.Rows[index].NumericValue);
            });
            Assert.True(work.Result!.RowPositions.SequenceEqual(expected) && work.Result.Statistics.BranchCount == 5 &&
                work.Result.Statistics.ItemCount == 96, "Yielded sorting preserves tree order, branch-local stability and empty branches");
        }

        // Suspend after filtering at successively later points, exercising metadata
        // ordering, grouping, merge passes and copy-back rather than just the scan.
        foreach (int laterChecks in new[] { 2, 20, 150, 300, 700 })
        {
            using var cancellation = new CancellationTokenSource();
            var reads = new MeasurementReads(snapshot.Rows.Count);
            using var work = PanelQueryEngine.Start(snapshot,
                new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending }, cancellation.Token, reads);
            int checks = 0;
            Assert.True(!work.TryComplete(() => reads.Reads.All(count => count == 1) && ++checks > laterChecks),
                "Cancellation fixture suspends after the complete item scan");
            cancellation.Cancel();
            try { work.Complete(); throw new InvalidOperationException("Canceled branch sort completed"); }
            catch (OperationCanceledException) { }
            Assert.True(work.Result == null, "Cancellation during branch sorting cannot publish a partial result");
        }
        Assert.True(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions()).RowPositions.SequenceEqual(Enumerable.Range(0, 96)),
            "Canceled branch sorting never changes source order");
    }

    private static PanelSnapshot Numbers(int count) => new PanelSnapshot(
        Enumerable.Range(0, count).Select(i => new PanelRow(i, "{0}", "Number", (i % 7).ToString(), i % 7)).ToArray(),
        new[] { new PanelBranch("{0}", count), new PanelBranch("{1}", 0) });



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
