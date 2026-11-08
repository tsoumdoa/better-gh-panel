using Xunit;
using BetterGhPanel.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// This links the production queue, rather than a model of its state machine.
[Collection("NurbsCandidateLimits")]
public class GeometryQueueTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void MillionPositionDemandStopsAtCancellationInsideBoundedRegistration(int cancelAt)
    {
        var ui = new Queue<Action>(); var delays = new Queue<Action>();
        var snapshot = new object(); int checks = 0;
        using var cancellation = new CancellationTokenSource();
        using var queue = new GeometryMeasurementQueue<object, object>(ui.Enqueue, ui.Enqueue,
            (_, action) => delays.Enqueue(action), _ => throw new InvalidOperationException("No geometry should prepare"), () => { },
            (_, position) => position >= 0 && position < 1000000, (_, __) => false, (_, __) => true,
            _ => null, () => { }, (_, __) => { if (++checks == cancelAt) cancellation.Cancel(); return true; });
        queue.Attach(snapshot, false);
        var work = queue.MeasureAllAsync(snapshot, 1000000, cancellation.Token);
        ui.Dequeue()();
        Assert.InRange(checks, 1, GeometryMeasurementQueue<object, object>.DemandChunkSize);
        while (!work.IsCompleted) { Assert.NotEmpty(delays); delays.Dequeue()(); }
        while (ui.Count > 0) ui.Dequeue()();
        Assert.False(work.Result); Assert.Equal(cancelAt, checks);
        Assert.InRange(delays.Count, 0, 1); Assert.Equal(0, queue.EntryCountForTesting);
    }

    [Fact]
    public void MeasurementMapFreezeYieldsAndCancelsBeforeScanningAMillionPositions()
    {
        using var fixture = new Fixture();
        var measured = fixture.Measure(CancellationToken.None, 0, 1, 2);
        fixture.Ui();
        while (!measured.IsCompleted) { if (fixture.Delays.Count > 0) fixture.Delay(); if (!fixture.Workers.IsEmpty) fixture.Worker(); fixture.Ui(); }
        Assert.True(measured.Result);
        using var cancellation = new CancellationTokenSource();
        fixture.Queue.DiagnosticsForTesting = true; fixture.Queue.ResetDiagnosticsForTesting();
        var frozen = fixture.Queue.CaptureCompletedAsync(fixture.Current, 1000000, cancellation.Token);
        fixture.Ui();
        Assert.False(frozen.IsCompleted); Assert.Single(fixture.Delays);
        cancellation.Cancel(); fixture.Delay(); fixture.Ui();
        Assert.Null(frozen.Result);
        Assert.InRange(fixture.Queue.SourceChecksForTesting, 1, 3);
        var complete = fixture.Queue.CaptureCompletedAsync(fixture.Current, 3, CancellationToken.None); fixture.Ui();
        Assert.Equal(3, complete.Result!.Count);
        fixture.Attach(new Snapshot(new Source(99)), false);
        Assert.Equal(3, complete.Result.Count); // the projection owns immutable values, not the mutable cache
    }

    [Fact]
    public void SessionAdmissionGuardsCrossQueueReentryAndPrioritizesDThenColumns()
    {
        var posts = new Queue<Action>();
        using var scheduler = new GeometryMeasurementScheduler(posts.Enqueue);
        void Dispatch() { while (posts.Count != 0) posts.Dequeue()(); }
        using var data = new Fixture(scheduler, 0);
        using var left = new Fixture(scheduler, 1, false);
        using var right = new Fixture(scheduler, 2, false);
        var seed = data.Measure(CancellationToken.None, 0);
        data.Ui(); data.Delay(); Dispatch(); data.Worker(); data.Ui(); Dispatch();
        Assert.True(seed.Result);
        var order = new List<string>();
        Task<bool>? d = null, r = null;
        data.AfterCopy = () => order.Add("D");
        right.AfterCopy = () => order.Add("right");
        left.AfterCopy = () =>
        {
            order.Add("left");
            // Even synchronous unit-retention proofs must not enter preparation
            // while another queue's duplication callback owns the session slot.
            data.Attach(new Snapshot(data.Current.Rows[0], data.Current.Rows[1]), true);
            Assert.Equal(1, data.PreparedPositions.Count);
            Assert.False(data.Queue.Has(data.Current, 0));
            r = right.Measure(CancellationToken.None, 0); right.Ui(); right.Delay();
            d = data.Measure(CancellationToken.None, 0, 1); data.Ui(); data.Delay();
            Dispatch();
            Assert.Equal(new[] { "left" }, order);
            Assert.Empty(data.Workers); Assert.Empty(right.Workers);
        };
        var l = left.Measure(CancellationToken.None, 0);
        left.Ui(); left.Delay(); Dispatch();
        Assert.Equal(2, scheduler.PendingCount);
        // Pending D work wins when the active column completes; its second row
        // also wins before the right-hand column's already pending request.
        left.Worker(); left.Ui(); Dispatch();
        Assert.Equal(new[] { "left", "D" }, order);
        data.Worker(); data.Ui(); Dispatch();
        Assert.Equal(new[] { "left", "D", "D" }, order);
        data.Worker(); data.Ui(); Dispatch();
        Assert.Equal(new[] { "left", "D", "D", "right" }, order);
        right.Worker(); right.Ui(); Dispatch();
        Assert.True(l.Result && d!.Result && r!.Result);
        Assert.All(left.Resources, resource => Assert.Equal(1, resource.Disposals));
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void ReentrantQueueRetirementKeepsAdmissionUntilPreparationUnwinds()
    {
        var posts = new Queue<Action>();
        using var scheduler = new GeometryMeasurementScheduler(posts.Enqueue);
        void Dispatch() { while (posts.Count != 0) posts.Dequeue()(); }
        using var column = new Fixture(scheduler, 1, false);
        using var data = new Fixture(scheduler, 0);
        Task<bool>? replacement = null;
        column.AfterCopy = () =>
        {
            column.Queue.Dispose();
            replacement = data.Measure(CancellationToken.None, 0);
            data.Ui(); data.Delay(); Dispatch();
            Assert.Empty(data.PreparedPositions);
        };
        var old = column.Measure(CancellationToken.None, 0);
        column.Ui(); column.Delay(); Dispatch();
        Assert.False(old.Result);
        Assert.Single(column.Resources); Assert.Equal(1, column.Resources[0].Disposals);
        Assert.Empty(column.Workers);
        Assert.Single(data.PreparedPositions);
        data.Worker(); data.Ui(); Dispatch();
        Assert.True(replacement!.Result);
    }

    [Fact]
    public void PendingAdmissionIsBoundedAndRemovedOnCancellationReplacementAndDisposal()
    {
        var posts = new Queue<Action>();
        using var scheduler = new GeometryMeasurementScheduler(posts.Enqueue);
        void Dispatch() { while (posts.Count != 0) posts.Dequeue()(); }
        using var active = new Fixture(scheduler, 0);
        using var cancellation = new CancellationTokenSource();
        var first = active.Measure(cancellation.Token, 0);
        active.Ui(); active.Delay(); Dispatch();
        cancellation.Cancel(); active.Ui(); Dispatch();
        Assert.False(first.Result);
        Assert.Equal(1, active.Resources[0].Disposals);
        for (int i = 0; i < 256; i++)
        {
            using var retired = new Fixture(scheduler, 1, false);
            retired.Queue.RequestVisible(retired.Current, new[] { 0 }); retired.Delay();
            retired.Queue.RequestVisible(retired.Current, new[] { 1 }); retired.Delay();
            Assert.Equal(1, scheduler.PendingCount); // replace, never append
            Assert.Empty(retired.PreparedPositions);
            retired.Attach(new Snapshot(new Source(i)), false);
            Assert.Equal(0, scheduler.PendingCount);
            using var cancelPending = new CancellationTokenSource();
            var pending = retired.Measure(cancelPending.Token, 0);
            retired.Ui(); retired.Delay();
            Assert.Equal(1, scheduler.PendingCount);
            cancelPending.Cancel(); retired.Ui();
            Assert.False(pending.Result); Assert.Equal(0, scheduler.PendingCount);
            var hidden = retired.Measure(CancellationToken.None, 0); retired.Ui(); retired.Delay();
            Assert.Equal(1, scheduler.PendingCount);
            retired.Queue.CancelRequests();
            Assert.False(hidden.Result); Assert.Equal(0, scheduler.PendingCount);
            retired.Queue.RequestVisible(retired.Current, new[] { 0 }); retired.Delay();
            retired.Queue.Dispose(); Assert.Equal(0, scheduler.PendingCount);
        }
        using var newest = new Fixture(scheduler, 0);
        var next = newest.Measure(CancellationToken.None, 0);
        newest.Ui(); newest.Delay(); Dispatch();
        Assert.Empty(newest.PreparedPositions);
        Assert.Single(active.Workers); // at most one accepted stale callback
        active.Worker(); active.Ui(); Dispatch();
        Assert.Single(newest.PreparedPositions);
        newest.Worker(); newest.Ui(); Dispatch();
        Assert.True(next.Result); Assert.Equal(0, active.Computations);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void ColumnQueuesNeverRetainWitnessesOrConsumeTheProcessRetentionCap()
    {
        for (int i = 0; i < 64; i++)
        {
            using var column = new Fixture(retainWitnesses: false);
            var measured = column.Measure(CancellationToken.None, 0);
            column.Ui(); column.Delay(); column.Worker(); column.Ui();
            Assert.True(measured.Result);
            Assert.True(MeasurementRetentionBudget.TryAcquire(MeasurementRetentionBudget.MaxBytes));
            try { Assert.False(MeasurementRetentionBudget.TryAcquire(1)); }
            finally { MeasurementRetentionBudget.Release(MeasurementRetentionBudget.MaxBytes); }
            column.Attach(new Snapshot(column.Current.Rows[0]), true);
            Assert.Single(column.PreparedPositions); // no retention proof call
            Assert.False(column.Queue.Has(column.Current, 0));
        }
    }

    [Fact]
    public async Task ClaimedColumnKeepsSessionAdmissionUntilOwnedWorkerDisposalCompletes()
    {
        var posts = new Queue<Action>();
        using var scheduler = new GeometryMeasurementScheduler(posts.Enqueue);
        void Dispatch() { while (posts.Count != 0) posts.Dequeue()(); }
        using var column = new Fixture(scheduler, 1, false);
        using var data = new Fixture(scheduler, 0);
        using var cancel = new CancellationTokenSource();
        using var gate = new ManualResetEventSlim(false);
        column.Gate = gate;
        var canceled = column.Measure(cancel.Token, 0);
        column.Ui(); column.Delay(); Dispatch();
        var worker = Task.Run(column.Worker);
        try
        {
            Assert.True(column.Started.Wait(TimeSpan.FromSeconds(5)));
            cancel.Cancel(); column.Ui();
            Assert.True(canceled.IsCompleted); Assert.False(await canceled);
            var pending = data.Measure(CancellationToken.None, 0);
            data.Ui(); data.Delay(); Dispatch();
            Assert.Empty(data.PreparedPositions); Assert.Equal(0, column.Resources[0].Disposals);
            gate.Set();
            Assert.True(SpinWait.SpinUntil(() => worker.IsCompleted, TimeSpan.FromSeconds(5)));
            column.Ui(); Dispatch();
            Assert.Equal(1, column.Resources[0].Disposals); Assert.Single(data.PreparedPositions);
            data.Worker(); data.Ui(); Dispatch();
            Assert.True(pending.IsCompleted); Assert.True(await pending);
        }
        finally { gate.Set(); await worker; }
    }



    [Fact]
    public void SnapshotAttachmentAndScanCounts()
    {
        const int count = 4096;
        using var fixture = new Fixture { OmitWitness = true };
        var sources = new Source[count]; var positions = new int[count];
        for (int i = 0; i < count; i++) { sources[i] = new Source(i); positions[i] = i; }
        fixture.Attach(new Snapshot(sources), false);
        int sourceChecks = 0;
        fixture.SourceCurrent = (_, __) => { sourceChecks++; return true; };
        var batch = fixture.Measure(CancellationToken.None, positions);
        fixture.Ui();
        while (!batch.IsCompleted) { if (fixture.Delays.Count > 0) fixture.Delay(); if (!fixture.Workers.IsEmpty) fixture.Worker(); fixture.Ui(); }
        Assert.True(batch.Result && fixture.Computations == count && fixture.Queue.EntryCountForTesting == count,
            "scan-count control populates every entry through the production worker queue");
        var queue = fixture.Queue;
        queue.DiagnosticsForTesting = true;
        queue.ResetDiagnosticsForTesting(); sourceChecks = 0;
        Assert.True(queue.SourcesCurrent(fixture.Current), "unchanged populated sources remain current");
        Assert.True(queue.EntryCopiesForTesting == count && queue.EntryVisitsForTesting == 4 * count &&
            queue.SourceChecksForTesting == count && sourceChecks == count && queue.SourceScansForTesting == 1,
            "source scan counters include the copy, enumeration and both entry lookups");
        Console.WriteLine($"  {count:N0}-entry production source scan: copied entries={queue.EntryCopiesForTesting}, " +
            $"entry reads={queue.EntryVisitsForTesting}, source checks={queue.SourceChecksForTesting}.");
        queue.ResetDiagnosticsForTesting(); sourceChecks = 0;
        Assert.True(queue.IsAttachedTo(fixture.Current), "the publication fence keeps queue ownership");
        queue.Attach(fixture.Current, false);
        Assert.True(queue.IsAttachedTo(fixture.Current) && queue.EntryCountForTesting == count && fixture.Computations == count &&
            queue.EntryVisitsForTesting == 0 && queue.EntryCopiesForTesting == 0 &&
            queue.SourceChecksForTesting == 0 && sourceChecks == 0 && queue.SourceScansForTesting == 0,
            "same-snapshot output-only ownership checks never scan, allocate an entry copy or remeasure");
        queue.Get(fixture.Current, count - 1);
        Assert.True(queue.EntryVisitsForTesting == 3 && queue.EntryCopiesForTesting == 0 && queue.SourceChecksForTesting == 1,
            "diagnostics include ordinary display/detail reads outside a full source scan");
        // Ownership alone deliberately says nothing about live source freshness.
        fixture.SourceCurrent = (_, __) => false;
        Assert.True(queue.IsAttachedTo(fixture.Current) && !queue.SourcesCurrent(fixture.Current) &&
            queue.Get(fixture.Current, 0) == null, "ownership cannot replace the full source freshness fence");
        fixture.SourceCurrent = (_, __) => true;
        var original = fixture.Current;
        bool reentered = false;
        fixture.SourceCurrent = (_, __) =>
        {
            if (!reentered) { reentered = true; fixture.Attach(new Snapshot(new Source(99)), false); }
            return true;
        };
        Assert.True(!queue.SourcesCurrent(original) && !queue.IsAttachedTo(original) && queue.IsAttachedTo(fixture.Current),
            "reentrant attachment invalidates both the old source scan and its ownership fence");
        queue.Suspend(true);
        Assert.True(!queue.IsAttachedTo(fixture.Current), "suspension detaches even retained measurements");
        queue.Attach(fixture.Current, false); queue.Dispose();
        Assert.True(!queue.IsAttachedTo(fixture.Current), "disposal cannot validate an output snapshot");
    }

    [Fact]
    public void ReentrantPreparationReplacement()
    {
        using var fixture = new Fixture();
        Task<bool>? replacement = null;
        fixture.AfterCopy = () =>
        {
            fixture.AfterCopy = null;
            fixture.Attach(new Snapshot(new Source(9)), false);
            replacement = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui();
            Assert.True(fixture.Workers.Count == 0 && fixture.Delays.Count == 0,
                "reentrant replacement cannot start another worker during preparation");
        };
        var obsolete = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        Assert.True(obsolete.IsCompleted && !obsolete.Result && fixture.Resources.Count == 1 && fixture.Resources[0].Disposals == 1 &&
            fixture.Workers.Count == 0 && !fixture.Queue.Has(fixture.Current, 0),
            "a copy returned after reentrant replacement is abandoned without publication");
        fixture.Delay(); fixture.Worker(); fixture.Ui();
        Assert.True(replacement != null && replacement.IsCompleted && replacement.Result && fixture.Queue.Get(fixture.Current, 0)?.Value == 9 &&
            fixture.Resources.Count == 2 && fixture.Resources.TrueForAll(resource => resource.Disposals == 1),
            "the replacement runs once after preparation exits and owns its own copy");
    }

    [Fact]
    public void WitnesslessRemeasurement()
    {
        using var fixture = new Fixture { OmitWitness = true };
        var first = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Assert.True(first.Result && fixture.Queue.Has(fixture.Current, 0), "witnessless work completes in its original snapshot");
        fixture.Queue.Attach(fixture.Current, false);
        Assert.True(fixture.Queue.Has(fixture.Current, 0) && fixture.Computations == 1,
            "attaching the same snapshot for output preserves its witnessless measurement");
        fixture.Attach(new Snapshot(fixture.Current.Rows[0]), true);
        Assert.True(!fixture.Queue.Has(fixture.Current, 0) && fixture.PreparedPositions.Count == 1,
            "unit-only attachment cannot retain or prepare a witnessless result for comparison");
        var second = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Assert.True(second.Result && fixture.Computations == 2 && fixture.Resources.TrueForAll(resource => resource.Disposals == 1),
            "a later request remeasures and releases a new witnessless copy");
    }

    [Fact]
    public void ReentrantPreparationCancellation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.InlineCancellation = true;
        fixture.AfterCopy = () => { fixture.AfterCopy = null; cancellation.Cancel(); };
        var canceled = fixture.Measure(cancellation.Token, 0);
        fixture.Ui(); fixture.Delay();
        Assert.True(canceled.IsCompleted && !canceled.Result && fixture.Workers.Count == 0 &&
            fixture.Resources.Count == 1 && fixture.Resources[0].Disposals == 1 && !fixture.Queue.Has(fixture.Current, 0),
            "cancellation during reentrant preparation abandons the copy without submitting it");
    }

    [Fact]
    public void LegacyAdmissionRecovery()
    {
        var held = new List<IDisposable>();
        try
        {
            for (int i = 0; i < LegacyMeasurementAdmission.MaxConcurrent; i++)
                held.Add(LegacyMeasurementAdmission.TryAcquire(out _) ?? throw new InvalidOperationException("admission unavailable"));
            Assert.True(LegacyMeasurementAdmission.TryAcquire(out var released) == null && !released.IsCompleted,
                "legacy admission is shared and exposes a release signal at capacity");
            using var first = new Fixture { UseLegacyAdmission = true };
            using var second = new Fixture { UseLegacyAdmission = true };
            using var cancellation = new CancellationTokenSource();
            var canceled = first.Measure(cancellation.Token, 0);
            var waiting = second.Measure(CancellationToken.None, 0);
            first.Ui(); second.Ui(); first.Delay(); second.Delay();
            cancellation.Cancel(); first.Ui();
            Assert.True(canceled.IsCompleted && !canceled.Result && !waiting.IsCompleted && first.Resources.Count == 0 && second.Resources.Count == 0,
                "capacity waits do not duplicate and a canceled panel stops waiting");
            held[0].Dispose();
            Assert.True(SpinWait.SpinUntil(() => { second.Ui(); return second.Delays.Count != 0; }, TimeSpan.FromSeconds(5)),
                "released legacy admission resumes a waiting panel without polling");
            second.Delay(); second.Worker(); second.Ui(); first.Ui();
            Assert.True(waiting.Result && second.Resources.Count == 1 && second.Resources[0].Disposals == 1 && first.Resources.Count == 0,
                "the resumed copy runs once and cancellation does not restart the other panel");
        }
        finally { foreach (var reservation in held) reservation.Dispose(); }
    }

    [Fact]
    public void ClaimedLegacyAdmissionSurvivesCancellation()
    {
        var held = new List<IDisposable>();
        try
        {
            for (int i = 0; i < LegacyMeasurementAdmission.MaxConcurrent - 1; i++)
                held.Add(LegacyMeasurementAdmission.TryAcquire(out _) ?? throw new InvalidOperationException("admission unavailable"));
            using var first = new Fixture { UseLegacyAdmission = true, OmitWitness = true };
            using var second = new Fixture { UseLegacyAdmission = true, OmitWitness = true };
            using var cancel = new CancellationTokenSource();
            using var gate = new ManualResetEventSlim(false);
            first.Gate = gate;
            var canceled = first.Measure(cancel.Token, 0);
            first.Ui(); first.Delay();
            var worker = Task.Run(first.Worker);
            Assert.True(first.Started.Wait(TimeSpan.FromSeconds(5)), "legacy worker claims its reserved copy");
            cancel.Cancel(); first.Ui();
            var waiting = second.Measure(CancellationToken.None, 0);
            second.Ui(); second.Delay();
            Assert.True(canceled.IsCompleted && !canceled.Result && !waiting.IsCompleted && first.Resources[0].Disposals == 0 &&
                second.Resources.Count == 0, "a canceled claimed native operation keeps admission until its worker completes");
            gate.Set();
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "claimed worker finishes");
            first.Ui();
            Assert.True(SpinWait.SpinUntil(() => { second.Ui(); return second.Delays.Count != 0; }, TimeSpan.FromSeconds(5)),
                "worker completion releases admission to another panel");
            second.Delay(); second.Worker(); second.Ui();
            Assert.True(waiting.Result && first.Resources[0].Disposals == 1 && second.Resources[0].Disposals == 1,
                "both claimed copies and their permits are released exactly once");
        }
        finally { foreach (var reservation in held) reservation.Dispose(); }
    }

    [Fact]
    public void ReentrantCrossPanelAdmission()
    {
        var held = new List<IDisposable>();
        try
        {
            for (int i = 0; i < LegacyMeasurementAdmission.MaxConcurrent - 1; i++)
                held.Add(LegacyMeasurementAdmission.TryAcquire(out _) ?? throw new InvalidOperationException("admission unavailable"));
            using var first = new Fixture { UseLegacyAdmission = true, OmitWitness = true };
            using var second = new Fixture { UseLegacyAdmission = true, OmitWitness = true };
            using var cancelFirst = new CancellationTokenSource();
            using var cancelSecond = new CancellationTokenSource();
            Task<bool>? remaining = null, canceledShared = null;
            first.AfterCopy = () =>
            {
                first.AfterCopy = null;
                remaining = second.Measure(CancellationToken.None, 0);
                canceledShared = second.Measure(cancelSecond.Token, 0);
                second.Ui(); second.Delay();
                Assert.True(!remaining.IsCompleted && !canceledShared.IsCompleted && second.Resources.Count == 0 && second.Workers.Count == 0,
                    "a reentrant panel returns immediately and waits asynchronously while the last permit is held");
                cancelSecond.Cancel(); second.Ui();
                Assert.True(canceledShared.IsCompleted && !canceledShared.Result && !remaining.IsCompleted,
                    "canceling one reentrant consumer keeps the shared waiting demand");
            };
            var abandoned = first.Measure(cancelFirst.Token, 0);
            first.Ui(); first.Delay();
            Assert.True(first.Resources.Count == 1 && first.Resources[0].Disposals == 0,
                "the first panel retains admission for its unclaimed copy");
            cancelFirst.Cancel(); first.Ui();
            Assert.True(abandoned.IsCompleted && !abandoned.Result && first.Resources[0].Disposals == 1,
                "abandoning unclaimed work disposes the copy before releasing its permit");
            Assert.True(SpinWait.SpinUntil(() => { second.Ui(); return second.Delays.Count != 0; }, TimeSpan.FromSeconds(5)),
                "the other panel wakes once the abandoned permit is released");
            second.Delay(); second.Worker(); second.Ui();
            Assert.True(remaining != null && remaining.IsCompleted && remaining.Result && second.Resources[0].Disposals == 1,
                "the remaining reentrant consumer completes with exactly-once disposal");
            first.Worker(); first.Ui();
            Assert.True(first.Computations == 0 && first.Resources[0].Disposals == 1, "the abandoned callback cannot claim or release twice");
        }
        finally { foreach (var reservation in held) reservation.Dispose(); }
    }

    [Fact]
    public void ReentrantNotificationAndRead()
    {
        using var fixture = new Fixture { OmitWitness = true };
        fixture.AfterNotify = () =>
        {
            fixture.AfterNotify = null;
            fixture.Attach(new Snapshot(new Source(9)), false);
        };
        var obsolete = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        Assert.True(obsolete.IsCompleted && !obsolete.Result && fixture.Workers.Count == 0 && fixture.Resources[0].Disposals == 1,
            "replacement during notification cannot submit the obsolete prepared copy");
        var current = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Assert.True(current.Result, "replacement can measure after reentrant notification");
        var old = fixture.Current;
        fixture.SourceCurrent = (_, __) =>
        {
            fixture.SourceCurrent = null;
            fixture.Attach(new Snapshot(new Source(11)), false);
            return true;
        };
        Assert.True(fixture.Queue.Get(old, 0) == null && !fixture.Queue.Has(old, 0),
            "a reentrant source check cannot return a value from an invalidated snapshot");
    }

    [Fact]
    public void ReentrantRetentionPreparation()
    {
        using var fixture = new Fixture();
        var original = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Assert.True(original.Result, "retention fixture has a witnessed value");
        Task<bool>? replacement = null;
        fixture.AfterCopy = () =>
        {
            fixture.AfterCopy = null;
            fixture.Attach(new Snapshot(new Source(9)), false);
            replacement = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui();
            Assert.True(fixture.Delays.Count == 0 && fixture.Workers.Count == 0,
                "nested replacement cannot start a worker during retention proof preparation");
        };
        fixture.Attach(new Snapshot(fixture.Current.Rows[0]), true);
        Assert.True(fixture.Resources.Count == 2 && fixture.Resources[1].Disposals == 1 && !fixture.Queue.Has(fixture.Current, 0),
            "a proof returned after reentrant replacement is discarded exactly once");
        fixture.Delay(); fixture.Worker(); fixture.Ui();
        Assert.True(replacement != null && replacement.Result && fixture.Queue.Get(fixture.Current, 0)?.Value == 9 &&
            fixture.Resources.TrueForAll(resource => resource.Disposals == 1),
            "the newer snapshot starts only after retention preparation and disposal finish");
    }

    [Fact]
    public void MeasurementContextChanges()
    {
        var original = new MeasurementContextStamp(1, 2, 0.001, "mm");
        Assert.True(!original.Equals(new MeasurementContextStamp(2, 2, 0.001, "mm")) &&
            !original.Equals(new MeasurementContextStamp(1, 3, 0.001, "mm")) &&
            !original.Equals(new MeasurementContextStamp(1, 2, 0.002, "mm")) &&
            !original.Equals(new MeasurementContextStamp(1, 2, 0.001, "cm")),
            "document, unit system, tolerance and unit label participate in the geometry context");
        using (var fixture = new Fixture { UseLegacyAdmission = true, OmitWitness = true })
        {
            var live = original;
            fixture.SourceCurrent = (_, __) => original.Equals(live);
            fixture.AfterCopy = () => { fixture.AfterCopy = null; live = new MeasurementContextStamp(1, 2, 0.002, "mm"); };
            var stale = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui(); fixture.Delay(); fixture.Ui();
            Assert.True(stale.IsCompleted && !stale.Result && fixture.Workers.Count == 0 && fixture.Resources[0].Disposals == 1,
                "a context change during preparation abandons its copy before dispatch");
        }
        using (var fixture = new Fixture { UseLegacyAdmission = true, OmitWitness = true })
        using (var gate = new ManualResetEventSlim(false))
        {
            var live = original;
            fixture.SourceCurrent = (_, __) => original.Equals(live);
            fixture.Gate = gate;
            var stale = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui(); fixture.Delay();
            var worker = Task.Run(fixture.Worker);
            Assert.True(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "context-change worker is gated after claiming");
            live = new MeasurementContextStamp(2, 2, 0.001, "mm");
            gate.Set(); Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "context-change worker finishes"); fixture.Ui();
            Assert.True(stale.IsCompleted && !stale.Result && !fixture.Queue.Has(fixture.Current, 0) && fixture.Resources[0].Disposals == 1,
                "a document change during worker execution rejects its result and releases the copy");
        }
        using (var fixture = new Fixture { UseLegacyAdmission = true, OmitWitness = true })
        {
            var live = original;
            fixture.SourceCurrent = (_, __) => original.Equals(live);
            var measured = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
            Assert.True(measured.Result && fixture.Queue.Has(fixture.Current, 0), "unchanged context admits a measurement");
            live = new MeasurementContextStamp(1, 3, 0.001, "mm");
            fixture.Queue.Attach(fixture.Current, false);
            Assert.True(!fixture.Queue.Has(fixture.Current, 0) && fixture.Queue.Get(fixture.Current, 0) == null,
                "a model-unit change after completion hides the old value even on same-snapshot reuse");
        }
    }

    [Fact]
    public void CapacityRecoveryAndCancellation()
    {
        var held = new List<IDisposable>();
        try
        {
            IDisposable? reservation;
            while ((reservation = NurbsCandidateLimits.TryReserve(1)) != null) held.Add(reservation);
            using var first = new Fixture { UseReservations = true };
            using var second = new Fixture { UseReservations = true };
            using var cancel = new CancellationTokenSource();
            var canceled = first.Measure(cancel.Token, 0);
            var waiting = second.Measure(CancellationToken.None, 0);
            first.Ui(); second.Ui(); first.Delay(); second.Delay();
            Assert.True(!canceled.IsCompleted && !waiting.IsCompleted && first.PreparedPositions.Count == 1 && second.PreparedPositions.Count == 1 &&
                first.Delays.Count == 0 && second.Delays.Count == 0 && first.Resources.Count == 0 && second.Resources.Count == 0,
                "two queues wait for shared capacity without allocating or spinning");
            cancel.Cancel(); first.Ui();
            Assert.True(canceled.IsCompleted && !canceled.Result, "capacity waiting honors cancellation");
            held[0].Dispose();
            held[1].Dispose();
            Assert.True(SpinWait.SpinUntil(() => { second.Ui(); return second.Delays.Count != 0; }, TimeSpan.FromSeconds(5)),
                "capacity release posts an asynchronous retry to the waiting queue");
            second.Delay(); second.Worker(); second.Ui(); first.Ui();
            Assert.True(waiting.IsCompleted && waiting.Result && second.PreparedPositions.Count == 2 && first.PreparedPositions.Count == 1,
                "one waiting panel recovers and a canceled panel does not restart");

            // Fill the returned slot again, then replace the waiting generation.
            while ((reservation = NurbsCandidateLimits.TryReserve(1)) != null) held.Add(reservation);
            using var replaced = new Fixture { UseReservations = true };
            var old = replaced.Measure(CancellationToken.None, 0);
            replaced.Ui(); replaced.Delay();
            Assert.True(!old.IsCompleted, "replacement case is waiting on the shared budget");
            replaced.Attach(new Snapshot(new Source(99)), false);
            Assert.True(old.IsCompleted && !old.Result, "replacement cancels the old waiting generation");
            held[2].Dispose();
            replaced.Ui();
            Assert.True(replaced.PreparedPositions.Count == 1 && replaced.Delays.Count == 0,
                "capacity release does not revive the replaced request");
        }
        finally { foreach (var item in held) item.Dispose(); }
    }

    [Fact]
    public void SourceReplacement()
    {
        using var fixture = new Fixture();
        bool current = true;
        fixture.SourceCurrent = (_, __) => current;
        var batch = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        current = false;
        fixture.Worker(); fixture.Ui();
        Assert.True(batch.IsCompleted && !batch.Result && !fixture.Queue.Has(fixture.Current, 0) &&
            fixture.Queue.Get(fixture.Current, 0) == null && fixture.Resources[0].Disposals == 1,
            "replacement after preparation rejects the result and releases its worker copy");
        current = true;
        var later = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Assert.True(later.Result && fixture.Queue.Has(fixture.Current, 0), "current source can be measured");
        current = false;
        Assert.True(!fixture.Queue.Has(fixture.Current, 0) && fixture.Queue.Get(fixture.Current, 0) == null &&
            !fixture.Queue.SourcesCurrent(fixture.Current),
            "cached results are hidden immediately when their source changes");
    }

    [Fact]
    public void GlobalRetentionBudget()
    {
        Assert.True(MeasurementRetentionBudget.TryAcquire(MeasurementRetentionBudget.MaxBytes), "process-wide witness capacity can be reserved");
        try
        {
            using var fixture = new Fixture();
            var measured = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
            Assert.True(measured.Result && fixture.Queue.Has(fixture.Current, 0), "global budget exhaustion does not prevent measurement");
            fixture.Attach(new Snapshot(fixture.Current.Rows[0]), true);
            Assert.True(!fixture.Queue.Has(fixture.Current, 0), "a witness that cannot be globally retained is not reused");
        }
        finally { MeasurementRetentionBudget.Release(MeasurementRetentionBudget.MaxBytes); }
        using var afterRelease = new Fixture();
        var later = afterRelease.Measure(CancellationToken.None, 0);
        afterRelease.Ui(); afterRelease.Delay(); afterRelease.Worker(); afterRelease.Ui();
        afterRelease.Attach(new Snapshot(afterRelease.Current.Rows[0]), true);
        Assert.True(later.Result && afterRelease.Queue.Has(afterRelease.Current, 0), "released global capacity allows retention again");
    }

    [Fact]
    public void CancellationRegistration()
    {
        using var fixture = new Fixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var already = fixture.Measure(canceled.Token, 0);
        fixture.Ui();
        Assert.True(already.IsCompleted && !already.Result && fixture.Resources.Count == 0, "already canceled registration");

        using var racing = new CancellationTokenSource();
        fixture.InlineCancellation = true;
        fixture.OnValidate = () => { fixture.OnValidate = null; racing.Cancel(); };
        var raced = fixture.Measure(racing.Token, 0);
        fixture.Ui();
        Assert.True(raced.IsCompleted && !raced.Result && fixture.Resources.Count == 0 && fixture.Delays.Count == 0,
            "synchronous cancellation during registration is safe");
    }

    [Fact]
    public void SharedBeforeClaim()
    {
        using var fixture = new Fixture();
        using var firstCancellation = new CancellationTokenSource();
        var first = fixture.Measure(firstCancellation.Token, 0);
        var second = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        var resource = fixture.Resources[0];
        firstCancellation.Cancel(); fixture.Ui();
        Assert.True(first.IsCompleted && !first.Result && !second.IsCompleted && resource.Disposals == 0,
            "one pre-claim cancellation leaves shared work alive");
        fixture.Worker(); fixture.Ui();
        Assert.True(second.IsCompleted && second.Result && fixture.Queue.Get(fixture.Current, 0)?.Value == 1 &&
            fixture.Resources.Count == 1 && resource.Disposals == 1 && fixture.Computations == 1,
            "remaining consumer receives the shared result and one disposal");
    }

    [Fact]
    public void SharedAfterClaim()
    {
        using var fixture = new Fixture();
        using var firstCancellation = new CancellationTokenSource();
        using var gate = new ManualResetEventSlim(false);
        fixture.Gate = gate;
        var first = fixture.Measure(firstCancellation.Token, 0);
        var second = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        var worker = Task.Run(fixture.Worker);
        Assert.True(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "worker claimed copy");
        firstCancellation.Cancel(); fixture.Ui();
        Assert.True(first.IsCompleted && !first.Result && !second.IsCompleted && fixture.Resources[0].Disposals == 0,
            "claimed copy stays alive after a shared consumer cancels");
        gate.Set(); Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "gated worker finished"); fixture.Ui();
        Assert.True(second.IsCompleted && second.Result && fixture.Resources[0].Disposals == 1,
            "worker owns claimed copy until completion");
    }

    [Fact]
    public void AbandonedLateWorker()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var task = fixture.Measure(cancellation.Token, 0);
        fixture.Ui(); fixture.Delay();
        cancellation.Cancel(); fixture.Ui();
        Assert.True(task.IsCompleted && !task.Result && fixture.Resources[0].Disposals == 1, "unclaimed work abandoned");
        fixture.Worker(); fixture.Ui();
        Assert.True(fixture.Computations == 0 && fixture.Resources[0].Disposals == 1 && !fixture.Queue.Has(fixture.Current, 0),
            "late worker cannot claim or dispose abandoned work");
    }

    [Fact]
    public void DelayedAcceptedWorkerDoesNotBlock()
    {
        using var fixture = new Fixture();
        fixture.Attach(new Snapshot(new Source(1), new Source(2), new Source(3), new Source(4)), false);
        using var cancellation = new CancellationTokenSource();
        var obsolete = fixture.Measure(cancellation.Token, 0);
        fixture.Ui(); fixture.Delay();
        var lateCallback = fixture.TakeWorker(); // accepted, but not yet claimed
        cancellation.Cancel(); fixture.Ui();
        Assert.True(obsolete.IsCompleted && !obsolete.Result && fixture.Resources[0].Disposals == 1,
            "canceling the sole consumer abandons the accepted unclaimed copy");

        var next = fixture.Measure(CancellationToken.None, 1);
        fixture.Ui();
        Assert.True(fixture.Delays.Count == 1, "a delayed obsolete callback does not occupy the worker slot");
        fixture.Delay(); fixture.Worker(); fixture.Ui();
        Assert.True(next.IsCompleted && next.Result && fixture.Queue.Get(fixture.Current, 1)?.Value == 2 &&
            fixture.Computations == 1, "a different item completes before the obsolete callback executes");

        using var sharedCancellation = new CancellationTokenSource();
        using var gate = new ManualResetEventSlim(false);
        fixture.Gate = gate;
        fixture.Started.Reset();
        var canceledShared = fixture.Measure(sharedCancellation.Token, 2);
        var shared = fixture.Measure(CancellationToken.None, 2);
        fixture.Ui(); fixture.Delay();
        var claimedWorker = Task.Run(fixture.Worker);
        Assert.True(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "shared work has been claimed by its worker");
        try
        {
            sharedCancellation.Cancel(); fixture.Ui();
            var following = fixture.Measure(CancellationToken.None, 3);
            fixture.Ui();
            Assert.True(canceledShared.IsCompleted && !canceledShared.Result && !shared.IsCompleted &&
                fixture.Resources[2].Disposals == 0 && fixture.Delays.Count == 0 && fixture.Workers.Count == 0,
                "a shared consumer keeps claimed work alive and a following item waits");

            fixture.RunWorker(lateCallback); fixture.Ui();
            Assert.True(fixture.Resources[0].Disposals == 1 && fixture.Computations == 2 &&
                !fixture.Queue.Has(fixture.Current, 0) && !shared.IsCompleted && !following.IsCompleted &&
                fixture.Delays.Count == 0 && fixture.Workers.Count == 0,
                "late abandoned callback neither disposes twice nor publishes or releases a claimed worker slot");

            gate.Set();
            Assert.True(claimedWorker.Wait(TimeSpan.FromSeconds(5)), "shared claimed worker completed");
            fixture.Ui();
            Assert.True(shared.IsCompleted && shared.Result && fixture.Queue.Get(fixture.Current, 2)?.Value == 3 &&
                fixture.Resources[2].Disposals == 1 && fixture.Delays.Count == 1,
                "the remaining shared consumer receives exactly one result before the next worker starts");
            fixture.Gate = null;
            fixture.Delay(); fixture.Worker(); fixture.Ui();
            Assert.True(following.IsCompleted && following.Result && fixture.Queue.Get(fixture.Current, 3)?.Value == 4 &&
                fixture.Computations == 3 && fixture.Resources.TrueForAll(resource => resource.Disposals == 1) &&
                !fixture.Queue.Has(fixture.Current, 0), "later work completes with exactly-once disposal and no stale result");
        }
        finally { gate.Set(); }
    }

    [Fact]
    public void DelayedWorkerAfterReplacementAndVisibilityChange()
    {
        foreach (bool retain in new[] { false, true })
        {
            using var fixture = new Fixture();
            var old = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui(); fixture.Delay();
            var late = fixture.TakeWorker();
            fixture.Queue.Suspend(retain);
            fixture.Attach(new Snapshot(new Source(42)), false);
            Assert.True(old.IsCompleted && !old.Result && fixture.Resources[0].Disposals == 1,
                "suspending an unclaimed worker releases it whether retained entries are requested or not");
            var replacement = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui();
            Assert.True(fixture.Delays.Count == 1, "replacement generation schedules before the old callback");
            fixture.Delay();
            fixture.RunWorker(late); fixture.Ui();
            Assert.True(!replacement.IsCompleted && fixture.Resources[1].Disposals == 0 && !fixture.Queue.Has(fixture.Current, 0),
                "the old callback cannot release or publish over the new entry at the same position");
            fixture.Worker(); fixture.Ui();
            Assert.True(replacement.Result && fixture.Queue.Get(fixture.Current, 0)?.Value == 42 &&
                fixture.Resources.TrueForAll(resource => resource.Disposals == 1),
                "replacement computes its own value with exactly-once disposal");
        }

        using var visible = new Fixture();
        visible.Queue.RequestVisible(visible.Current, new[] { 0 });
        visible.Delay();
        var oldVisible = visible.TakeWorker();
        visible.Queue.RequestVisible(visible.Current, new[] { 1 });
        Assert.True(visible.Resources[0].Disposals == 1 && visible.Delays.Count == 1,
            "changing visible demand releases an unclaimed worker without waiting for its callback");
        visible.Delay(); visible.Worker(); visible.Ui();
        visible.RunWorker(oldVisible); visible.Ui();
        Assert.True(!visible.Queue.Has(visible.Current, 0) && visible.Queue.Get(visible.Current, 1)?.Value == 2 &&
            visible.Resources.TrueForAll(resource => resource.Disposals == 1), "a late visible callback cannot publish stale work");
    }

    [Fact]
    public void AbandonReleasesPreparedReferences()
    {
        var resource = new Resource();
        var work = new PreparedMeasurement<Result>(() => new Result(1), resource,
            new MeasurementWitness("test", new byte[1024]));
        Assert.True(work.Witness != null && work.TryAbandon() && work.Witness == null && resource.Disposals == 1,
            "abandonment releases the prepared proof and resource before a delayed callback");
        Assert.True(!work.TryRun(out _) && !work.TryAbandon() && resource.Disposals == 1,
            "late claims and repeated abandonment cannot run or dispose the work again");
    }

    [Fact]
    public void RejectedWorker()
    {
        using var fixture = new Fixture { RejectWorker = true };
        var task = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        Assert.True(task.IsCompleted && task.Result && fixture.Resources[0].Disposals == 1 &&
            fixture.Queue.Has(fixture.Current, 0) && fixture.Queue.Get(fixture.Current, 0) == null,
            "rejected submission disposes and records an unavailable result");
    }

    [Fact]
    public void ThrowingWorkerSubmission()
    {
        using var fixture = new Fixture { ThrowSubmit = true };
        var task = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        Assert.True(task.IsCompleted && task.Result && fixture.Resources[0].Disposals == 1 && fixture.Queue.Has(fixture.Current, 0),
            "throwing worker submission releases unclaimed work");
    }

    [Fact]
    public void ReplacementAndDisposal()
    {
        using var fixture = new Fixture();
        using var gate = new ManualResetEventSlim(false);
        fixture.Gate = gate;
        var old = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        var worker = Task.Run(fixture.Worker);
        Assert.True(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "old worker running");
        fixture.Attach(new Snapshot(new Source(22)), false);
        Assert.True(old.IsCompleted && !old.Result && fixture.Resources[0].Disposals == 0, "replacement never disposes a claimed copy");
        var current = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui();
        Assert.True(fixture.Workers.Count == 0, "replacement waits for the existing worker");
        gate.Set(); Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "old worker finished"); fixture.Ui();
        Assert.True(!fixture.Queue.Has(fixture.Current, 0), "stale successful result is rejected");
        fixture.Gate = null; fixture.Delay(); fixture.Worker(); fixture.Ui();
        Assert.True(current.IsCompleted && current.Result && fixture.Queue.Get(fixture.Current, 0)?.Value == 22 &&
            fixture.Resources[0].Disposals == 1 && fixture.Resources[1].Disposals == 1, "new source is measured after stale success");

        using var other = new Fixture();
        using var gate2 = new ManualResetEventSlim(false);
        other.Gate = gate2;
        var pending = other.Measure(CancellationToken.None, 0);
        other.Ui(); other.Delay();
        var running = Task.Run(other.Worker);
        Assert.True(other.Started.Wait(TimeSpan.FromSeconds(5)), "disposal worker running");
        other.Queue.Dispose();
        Assert.True(pending.IsCompleted && !pending.Result && other.Resources[0].Disposals == 0, "session disposal leaves claimed work alive");
        gate2.Set(); Assert.True(running.Wait(TimeSpan.FromSeconds(5)), "disposal worker finished"); other.Ui();
        Assert.True(other.Resources[0].Disposals == 1 && !other.Queue.Has(other.Current, 0), "disposed session rejects publication");
    }

    [Fact]
    public void StaleException()
    {
        using var fixture = new Fixture();
        using var gate = new ManualResetEventSlim(false);
        fixture.Gate = gate; fixture.FailCompute = true;
        var old = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        var worker = Task.Run(fixture.Worker);
        Assert.True(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "exception worker running");
        fixture.Attach(new Snapshot(new Source(8)), false);
        gate.Set(); Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "exception worker finished"); fixture.Ui();
        Assert.True(old.IsCompleted && !old.Result && fixture.Resources[0].Disposals == 1 && !fixture.Queue.Has(fixture.Current, 0),
            "stale exception disposes without poisoning replacement");
    }

    [Fact]
    public void ValueAndContextRetention()
    {
        using var fixture = new Fixture();
        var source = fixture.Current.Rows[0];
        var first = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Assert.True(first.Result, "initial measurement");
        fixture.Attach(new Snapshot(source), true);
        Assert.True(fixture.Queue.Has(fixture.Current, 0) && fixture.Queue.Get(fixture.Current, 0)?.Value == 1 &&
            fixture.Resources[1].Disposals == 1, "same full value and context retain completed result");
        source.Value = 2; // same holder identity with a different value
        fixture.Attach(new Snapshot(source), true);
        Assert.True(!fixture.Queue.Has(fixture.Current, 0), "reused identity with changed value is not retained");
        var second = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Assert.True(second.Result && fixture.Queue.Get(fixture.Current, 0)?.Value == 2, "changed value remeasured");
        source.Role = "surface";
        fixture.Attach(new Snapshot(source), true);
        Assert.True(!fixture.Queue.Has(fixture.Current, 0), "role change invalidates identical geometry");
    }

    [Fact]
    public void WitnessIsImmutable()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var original = new MeasurementWitness("curve|mm|tolerance|length", bytes);
        bytes[0] = 9;
        Assert.True(original.Equals(new MeasurementWitness("curve|mm|tolerance|length", new byte[] { 1, 2, 3 })) &&
            !original.Equals(new MeasurementWitness("surface|mm|tolerance|area", new byte[] { 1, 2, 3 })) &&
            !original.Equals(new MeasurementWitness("curve|cm|tolerance|length", new byte[] { 1, 2, 3 })) &&
            !original.Equals(new MeasurementWitness("curve|mm|other-tolerance|length", new byte[] { 1, 2, 3 })),
            "immutable full witness includes geometry role, metric, units and tolerance");
    }

    [Fact]
    public void RetainedProofBudget()
    {
        using var fixture = new Fixture();
        var sources = new Source[130];
        var positions = new int[sources.Length];
        for (int i = 0; i < sources.Length; i++) { sources[i] = new Source(i); positions[i] = i; }
        fixture.Attach(new Snapshot(sources), false);
        var batch = fixture.Measure(CancellationToken.None, positions);
        fixture.Ui();
        while (!batch.IsCompleted) { if (fixture.Delays.Count > 0) fixture.Delay(); if (!fixture.Workers.IsEmpty) fixture.Worker(); fixture.Ui(); }
        Assert.True(batch.Result, "large batch completes");
        int preparedBefore = fixture.PreparedPositions.Count;
        fixture.Attach(new Snapshot(sources), true);
        int retained = 0;
        for (int i = 0; i < sources.Length; i++) if (fixture.Queue.Has(fixture.Current, i)) retained++;
        Assert.True(retained == 128 && fixture.PreparedPositions.Count - preparedBefore == 128,
            "retention limits both proof count and current-value preparation work");
    }

    [Fact]
    public void CopyBeforeWitness()
    {
        using var fixture = new Fixture();
        using var gate = new ManualResetEventSlim(false);
        var source = fixture.Current.Rows[0];
        fixture.Gate = gate;
        fixture.AfterCopy = () => { fixture.AfterCopy = null; source.Value = 9; };
        var task = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        var worker = Task.Run(fixture.Worker);
        Assert.True(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "in-flight copy made before source mutation");
        fixture.Attach(new Snapshot(source), true);
        Assert.True(task.IsCompleted && !task.Result && fixture.Resources[0].Disposals == 0, "different current witness rejects in-flight copy");
        gate.Set(); Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "in-flight worker finished"); fixture.Ui();
        Assert.True(!fixture.Queue.Has(fixture.Current, 0) && fixture.Resources[0].Disposals == 1,
            "witness reflects the worker copy, not the later live source");

        using var completed = new Fixture();
        var live = completed.Current.Rows[0];
        completed.AfterCopy = () => { completed.AfterCopy = null; live.Value = 7; };
        var measured = completed.Measure(CancellationToken.None, 0);
        completed.Ui(); completed.Delay(); completed.Worker(); completed.Ui();
        Assert.True(measured.Result && completed.Queue.Get(completed.Current, 0)?.Value == 1, "completed result uses old copy");
        completed.Attach(new Snapshot(live), true);
        Assert.True(!completed.Queue.Has(completed.Current, 0), "completed result cannot retain newer live witness");
    }

    [Fact]
    public void InFlightRetentionAndRoleSwitch()
    {
        using var fixture = new Fixture();
        using var gate = new ManualResetEventSlim(false);
        fixture.Gate = gate;
        var source = fixture.Current.Rows[0];
        var original = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        var worker = Task.Run(fixture.Worker);
        Assert.True(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "retained worker claimed");
        fixture.Attach(new Snapshot(source), true);
        Assert.True(original.IsCompleted && !original.Result && fixture.Resources[0].Disposals == 0 && fixture.Resources[1].Disposals == 1,
            "same in-flight proof is compared without touching the worker copy");
        gate.Set(); Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "retained worker finished"); fixture.Ui();
        Assert.True(fixture.Queue.Has(fixture.Current, 0) && fixture.Queue.Get(fixture.Current, 0)?.Value == 1 &&
            fixture.Resources[0].Disposals == 1 && fixture.Computations == 1, "unchanged in-flight value retained");

        using var changed = new Fixture();
        using var changedGate = new ManualResetEventSlim(false);
        changed.Gate = changedGate;
        var live = changed.Current.Rows[0];
        changed.Measure(CancellationToken.None, 0);
        changed.Ui(); changed.Delay();
        var changedWorker = Task.Run(changed.Worker);
        Assert.True(changed.Started.Wait(TimeSpan.FromSeconds(5)), "role-switch worker claimed");
        live.Role = "surface";
        changed.Attach(new Snapshot(live), true);
        changedGate.Set(); Assert.True(changedWorker.Wait(TimeSpan.FromSeconds(5)), "role-switch worker finished"); changed.Ui();
        Assert.True(!changed.Queue.Has(changed.Current, 0) && changed.Resources[0].Disposals == 1,
            "in-flight result is rejected when the measurement role changes");
    }

    [Fact]
    public void SchedulingAndAffinity()
    {
        using var fixture = new Fixture();
        fixture.Queue.RequestVisible(fixture.Current, new[] { 1, 0 });
        Assert.True(fixture.Delays.Count == 1 && fixture.Delays.Peek().delay == 40 && fixture.Resources.Count == 0,
            "viewport preparation is deferred");
        var explicitRequest = fixture.Measure(CancellationToken.None, 2);
        fixture.Ui(); fixture.Delay();
        Assert.True(fixture.PreparedPositions.Count == 1 && fixture.PreparedPositions[0] == 2 && fixture.Workers.Count == 1,
            "explicit work has priority over delayed visible work");
        fixture.Queue.RequestVisible(fixture.Current, new[] { 0 });
        fixture.Worker(); fixture.Ui();
        Assert.True(explicitRequest.IsCompleted && explicitRequest.Result && fixture.Delays.Count == 1 && fixture.Delays.Peek().delay == 0,
            "single worker resumes on UI with no added delay");
        fixture.Delay(); fixture.Worker(); fixture.Ui();
        Assert.True(fixture.PreparedPositions.Count == 2 && fixture.PreparedPositions[1] == 0 &&
            fixture.Computations == 2 && fixture.UiNotifications > 0 && fixture.WrongThreadCalls == 0,
            "viewport replacement, UI affinity and worker boundary are respected");
    }



    private sealed class Source
    {
        internal Source(int value) { Value = value; }
        internal int Value;
        internal string Role = "curve";
    }
    private sealed class Snapshot
    {
        internal Snapshot(params Source[] rows) { Rows = rows; }
        internal Source[] Rows;
    }
    private sealed class Result
    {
        internal Result(int value) { Value = value; }
        internal int Value;
    }
    private sealed class Resource : IDisposable
    {
        private readonly IDisposable? _reservation;
        internal Resource(IDisposable? reservation = null) { _reservation = reservation; }
        internal int Disposals;
        public void Dispose()
        {
            if (Interlocked.Increment(ref Disposals) != 1) throw new InvalidOperationException("double dispose");
            _reservation?.Dispose();
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly int _uiThread = Environment.CurrentManagedThreadId;
        private readonly ThreadLocal<bool> _onWorker = new ThreadLocal<bool>(() => false);
        private readonly ConcurrentQueue<Action> _ui = new ConcurrentQueue<Action>();
        internal readonly Queue<(int delay, Action action)> Delays = new Queue<(int, Action)>();
        internal readonly ConcurrentQueue<Action> Workers = new ConcurrentQueue<Action>();
        internal readonly List<Resource> Resources = new List<Resource>();
        internal readonly List<int> PreparedPositions = new List<int>();
        internal readonly ManualResetEventSlim Started = new ManualResetEventSlim(false);
        internal readonly GeometryMeasurementQueue<Snapshot, Result> Queue;
        internal Snapshot Current = new Snapshot(new Source(1), new Source(2), new Source(3));
        internal ManualResetEventSlim? Gate;
        internal Action? AfterCopy, OnValidate, AfterNotify;
        internal Func<Snapshot, int, bool>? SourceCurrent;
        internal bool InlineCancellation, RejectWorker, ThrowSubmit, FailCompute, UseReservations, UseLegacyAdmission, OmitWitness;
        internal int Computations, UiNotifications, WrongThreadCalls;

        internal Fixture(GeometryMeasurementScheduler? scheduler = null, int priority = 0, bool retainWitnesses = true)
        {
            Queue = new GeometryMeasurementQueue<Snapshot, Result>(
                action => _ui.Enqueue(action),
                action => { if (InlineCancellation) action(); else _ui.Enqueue(action); },
                (delay, action) => Delays.Enqueue((delay, action)),
                action => { if (ThrowSubmit) throw new InvalidOperationException("submission rejected"); if (RejectWorker) return false; Workers.Enqueue(action); return true; },
                VerifyUi,
                (snapshot, position) => { OnValidate?.Invoke(); return position >= 0 && position < snapshot.Rows.Length; },
                (snapshot, position) => position >= 0 && position < snapshot.Rows.Length,
                (snapshot, position) => position >= 0 && position < snapshot.Rows.Length,
                Prepare, () => { VerifyUi(); UiNotifications++; AfterNotify?.Invoke(); },
                (snapshot, position) => SourceCurrent?.Invoke(snapshot, position) ?? true,
                scheduler: scheduler, priority: priority, retainWitnesses: retainWitnesses);
            Queue.Attach(Current, false);
        }
        private void VerifyUi()
        {
            if (Environment.CurrentManagedThreadId != _uiThread || _onWorker.Value)
            { WrongThreadCalls++; throw new InvalidOperationException("not on UI"); }
        }
        private PreparedMeasurement<Result> Prepare(int position)
        {
            VerifyUi(); PreparedPositions.Add(position);
            IDisposable? reservation = null;
            if (UseReservations)
            {
                reservation = NurbsCandidateLimits.TryReserve(1024, out var released);
                if (reservation == null) throw new MeasurementCapacityException(released!);
            }
            if (UseLegacyAdmission)
            {
                reservation = LegacyMeasurementAdmission.TryAcquire(out var released);
                if (reservation == null) throw new MeasurementCapacityException(released);
            }
            var source = Current.Rows[position];
            int copiedValue = source.Value;
            string copiedRole = source.Role;
            AfterCopy?.Invoke();
            var resource = new Resource(reservation); Resources.Add(resource);
            var witness = OmitWitness ? null : new MeasurementWitness(copiedRole + "|mm|0.001|length", BitConverter.GetBytes(copiedValue));
            var gate = Gate; bool fail = FailCompute;
            return new PreparedMeasurement<Result>(() =>
            {
                if (!_onWorker.Value) { WrongThreadCalls++; throw new InvalidOperationException("not on worker"); }
                Interlocked.Increment(ref Computations); Started.Set();
                if (gate != null && !gate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                if (fail) throw new InvalidOperationException("measurement failed");
                return new Result(copiedValue);
            }, resource, witness);
        }
        internal Task<bool> Measure(CancellationToken token, params int[] positions) => Queue.MeasureAsync(Current, positions, token);
        internal void Attach(Snapshot snapshot, bool retain) { Current = snapshot; Queue.Attach(snapshot, retain); }
        internal void Ui() { while (_ui.TryDequeue(out var action)) action(); }
        internal void Delay() { var next = Delays.Dequeue(); next.action(); }
        internal void Worker()
        {
            RunWorker(TakeWorker());
        }
        internal Action TakeWorker()
        {
            if (!Workers.TryDequeue(out var action)) throw new InvalidOperationException("no worker");
            return action;
        }
        internal void RunWorker(Action action)
        {
            _onWorker.Value = true;
            try { action(); } finally { _onWorker.Value = false; }
        }
        public void Dispose() { Queue.Dispose(); Started.Dispose(); _onWorker.Dispose(); }
    }
}
