using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Runtime;

// This links the production queue, rather than a model of its state machine.
internal static class GeometryQueueChecks
{
    internal static void Run()
    {
        CancellationRegistration();
        SharedBeforeClaim();
        SharedAfterClaim();
        AbandonedLateWorker();
        DelayedAcceptedWorkerDoesNotBlock();
        DelayedWorkerAfterReplacementAndVisibilityChange();
        AbandonReleasesPreparedReferences();
        RejectedWorker();
        ThrowingWorkerSubmission();
        ReplacementAndDisposal();
        StaleException();
        ValueAndContextRetention();
        WitnessIsImmutable();
        RetainedProofBudget();
        InFlightRetentionAndRoleSwitch();
        CopyBeforeWitness();
        SchedulingAndAffinity();
        GlobalRetentionBudget();
        CapacityRecoveryAndCancellation();
        SourceReplacement();
        ReentrantPreparationReplacement();
        ReentrantPreparationCancellation();
        WitnesslessRemeasurement();
        LegacyAdmissionRecovery();
        ClaimedLegacyAdmissionSurvivesCancellation();
        ReentrantCrossPanelAdmission();
        ReentrantNotificationAndRead();
        ReentrantRetentionPreparation();
        MeasurementContextChanges();
        SnapshotAttachmentAndScanCounts();
    }

    private static void SnapshotAttachmentAndScanCounts()
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
        while (!batch.IsCompleted) { fixture.Delay(); fixture.Worker(); fixture.Ui(); }
        Check(batch.Result && fixture.Computations == count && fixture.Queue.EntryCountForTesting == count,
            "scan-count control populates every entry through the production worker queue");
        var queue = fixture.Queue;
        queue.DiagnosticsForTesting = true;
        queue.ResetDiagnosticsForTesting(); sourceChecks = 0;
        Check(queue.SourcesCurrent(fixture.Current), "unchanged populated sources remain current");
        Check(queue.EntryCopiesForTesting == count && queue.EntryVisitsForTesting == 4 * count &&
            queue.SourceChecksForTesting == count && sourceChecks == count && queue.SourceScansForTesting == 1,
            "source scan counters include the copy, enumeration and both entry lookups");
        Console.WriteLine($"  {count:N0}-entry production source scan: copied entries={queue.EntryCopiesForTesting}, " +
            $"entry reads={queue.EntryVisitsForTesting}, source checks={queue.SourceChecksForTesting}.");
        queue.ResetDiagnosticsForTesting(); sourceChecks = 0;
        Check(queue.IsAttachedTo(fixture.Current), "the publication fence keeps queue ownership");
        queue.Attach(fixture.Current, false);
        Check(queue.IsAttachedTo(fixture.Current) && queue.EntryCountForTesting == count && fixture.Computations == count &&
            queue.EntryVisitsForTesting == 0 && queue.EntryCopiesForTesting == 0 &&
            queue.SourceChecksForTesting == 0 && sourceChecks == 0 && queue.SourceScansForTesting == 0,
            "same-snapshot output-only ownership checks never scan, allocate an entry copy or remeasure");
        queue.Get(fixture.Current, count - 1);
        Check(queue.EntryVisitsForTesting == 3 && queue.EntryCopiesForTesting == 0 && queue.SourceChecksForTesting == 1,
            "diagnostics include ordinary display/detail reads outside a full source scan");
        // Ownership alone deliberately says nothing about live source freshness.
        fixture.SourceCurrent = (_, __) => false;
        Check(queue.IsAttachedTo(fixture.Current) && !queue.SourcesCurrent(fixture.Current) &&
            queue.Get(fixture.Current, 0) == null, "ownership cannot replace the full source freshness fence");
        fixture.SourceCurrent = (_, __) => true;
        var original = fixture.Current;
        bool reentered = false;
        fixture.SourceCurrent = (_, __) =>
        {
            if (!reentered) { reentered = true; fixture.Attach(new Snapshot(new Source(99)), false); }
            return true;
        };
        Check(!queue.SourcesCurrent(original) && !queue.IsAttachedTo(original) && queue.IsAttachedTo(fixture.Current),
            "reentrant attachment invalidates both the old source scan and its ownership fence");
        queue.Suspend(true);
        Check(!queue.IsAttachedTo(fixture.Current), "suspension detaches even retained measurements");
        queue.Attach(fixture.Current, false); queue.Dispose();
        Check(!queue.IsAttachedTo(fixture.Current), "disposal cannot validate an output snapshot");
    }

    private static void ReentrantPreparationReplacement()
    {
        using var fixture = new Fixture();
        Task<bool>? replacement = null;
        fixture.AfterCopy = () =>
        {
            fixture.AfterCopy = null;
            fixture.Attach(new Snapshot(new Source(9)), false);
            replacement = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui();
            Check(fixture.Workers.Count == 0 && fixture.Delays.Count == 0,
                "reentrant replacement cannot start another worker during preparation");
        };
        var obsolete = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        Check(obsolete.IsCompleted && !obsolete.Result && fixture.Resources.Count == 1 && fixture.Resources[0].Disposals == 1 &&
            fixture.Workers.Count == 0 && !fixture.Queue.Has(fixture.Current, 0),
            "a copy returned after reentrant replacement is abandoned without publication");
        fixture.Delay(); fixture.Worker(); fixture.Ui();
        Check(replacement != null && replacement.IsCompleted && replacement.Result && fixture.Queue.Get(fixture.Current, 0)?.Value == 9 &&
            fixture.Resources.Count == 2 && fixture.Resources.TrueForAll(resource => resource.Disposals == 1),
            "the replacement runs once after preparation exits and owns its own copy");
    }

    private static void WitnesslessRemeasurement()
    {
        using var fixture = new Fixture { OmitWitness = true };
        var first = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Check(first.Result && fixture.Queue.Has(fixture.Current, 0), "witnessless work completes in its original snapshot");
        fixture.Queue.Attach(fixture.Current, false);
        Check(fixture.Queue.Has(fixture.Current, 0) && fixture.Computations == 1,
            "attaching the same snapshot for output preserves its witnessless measurement");
        fixture.Attach(new Snapshot(fixture.Current.Rows[0]), true);
        Check(!fixture.Queue.Has(fixture.Current, 0) && fixture.PreparedPositions.Count == 1,
            "unit-only attachment cannot retain or prepare a witnessless result for comparison");
        var second = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Check(second.Result && fixture.Computations == 2 && fixture.Resources.TrueForAll(resource => resource.Disposals == 1),
            "a later request remeasures and releases a new witnessless copy");
    }

    private static void ReentrantPreparationCancellation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.InlineCancellation = true;
        fixture.AfterCopy = () => { fixture.AfterCopy = null; cancellation.Cancel(); };
        var canceled = fixture.Measure(cancellation.Token, 0);
        fixture.Ui(); fixture.Delay();
        Check(canceled.IsCompleted && !canceled.Result && fixture.Workers.Count == 0 &&
            fixture.Resources.Count == 1 && fixture.Resources[0].Disposals == 1 && !fixture.Queue.Has(fixture.Current, 0),
            "cancellation during reentrant preparation abandons the copy without submitting it");
    }

    private static void LegacyAdmissionRecovery()
    {
        var held = new List<IDisposable>();
        try
        {
            for (int i = 0; i < LegacyMeasurementAdmission.MaxConcurrent; i++)
                held.Add(LegacyMeasurementAdmission.TryAcquire(out _) ?? throw new InvalidOperationException("admission unavailable"));
            Check(LegacyMeasurementAdmission.TryAcquire(out var released) == null && !released.IsCompleted,
                "legacy admission is shared and exposes a release signal at capacity");
            using var first = new Fixture { UseLegacyAdmission = true };
            using var second = new Fixture { UseLegacyAdmission = true };
            using var cancellation = new CancellationTokenSource();
            var canceled = first.Measure(cancellation.Token, 0);
            var waiting = second.Measure(CancellationToken.None, 0);
            first.Ui(); second.Ui(); first.Delay(); second.Delay();
            cancellation.Cancel(); first.Ui();
            Check(canceled.IsCompleted && !canceled.Result && !waiting.IsCompleted && first.Resources.Count == 0 && second.Resources.Count == 0,
                "capacity waits do not duplicate and a canceled panel stops waiting");
            held[0].Dispose();
            Check(SpinWait.SpinUntil(() => { second.Ui(); return second.Delays.Count != 0; }, TimeSpan.FromSeconds(5)),
                "released legacy admission resumes a waiting panel without polling");
            second.Delay(); second.Worker(); second.Ui(); first.Ui();
            Check(waiting.Result && second.Resources.Count == 1 && second.Resources[0].Disposals == 1 && first.Resources.Count == 0,
                "the resumed copy runs once and cancellation does not restart the other panel");
        }
        finally { foreach (var reservation in held) reservation.Dispose(); }
    }

    private static void ClaimedLegacyAdmissionSurvivesCancellation()
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
            Check(first.Started.Wait(TimeSpan.FromSeconds(5)), "legacy worker claims its reserved copy");
            cancel.Cancel(); first.Ui();
            var waiting = second.Measure(CancellationToken.None, 0);
            second.Ui(); second.Delay();
            Check(canceled.IsCompleted && !canceled.Result && !waiting.IsCompleted && first.Resources[0].Disposals == 0 &&
                second.Resources.Count == 0, "a canceled claimed native operation keeps admission until its worker completes");
            gate.Set();
            Check(worker.Wait(TimeSpan.FromSeconds(5)), "claimed worker finishes");
            first.Ui();
            Check(SpinWait.SpinUntil(() => { second.Ui(); return second.Delays.Count != 0; }, TimeSpan.FromSeconds(5)),
                "worker completion releases admission to another panel");
            second.Delay(); second.Worker(); second.Ui();
            Check(waiting.Result && first.Resources[0].Disposals == 1 && second.Resources[0].Disposals == 1,
                "both claimed copies and their permits are released exactly once");
        }
        finally { foreach (var reservation in held) reservation.Dispose(); }
    }

    private static void ReentrantCrossPanelAdmission()
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
                Check(!remaining.IsCompleted && !canceledShared.IsCompleted && second.Resources.Count == 0 && second.Workers.Count == 0,
                    "a reentrant panel returns immediately and waits asynchronously while the last permit is held");
                cancelSecond.Cancel(); second.Ui();
                Check(canceledShared.IsCompleted && !canceledShared.Result && !remaining.IsCompleted,
                    "canceling one reentrant consumer keeps the shared waiting demand");
            };
            var abandoned = first.Measure(cancelFirst.Token, 0);
            first.Ui(); first.Delay();
            Check(first.Resources.Count == 1 && first.Resources[0].Disposals == 0,
                "the first panel retains admission for its unclaimed copy");
            cancelFirst.Cancel(); first.Ui();
            Check(abandoned.IsCompleted && !abandoned.Result && first.Resources[0].Disposals == 1,
                "abandoning unclaimed work disposes the copy before releasing its permit");
            Check(SpinWait.SpinUntil(() => { second.Ui(); return second.Delays.Count != 0; }, TimeSpan.FromSeconds(5)),
                "the other panel wakes once the abandoned permit is released");
            second.Delay(); second.Worker(); second.Ui();
            Check(remaining != null && remaining.IsCompleted && remaining.Result && second.Resources[0].Disposals == 1,
                "the remaining reentrant consumer completes with exactly-once disposal");
            first.Worker(); first.Ui();
            Check(first.Computations == 0 && first.Resources[0].Disposals == 1, "the abandoned callback cannot claim or release twice");
        }
        finally { foreach (var reservation in held) reservation.Dispose(); }
    }

    private static void ReentrantNotificationAndRead()
    {
        using var fixture = new Fixture { OmitWitness = true };
        fixture.AfterNotify = () =>
        {
            fixture.AfterNotify = null;
            fixture.Attach(new Snapshot(new Source(9)), false);
        };
        var obsolete = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        Check(obsolete.IsCompleted && !obsolete.Result && fixture.Workers.Count == 0 && fixture.Resources[0].Disposals == 1,
            "replacement during notification cannot submit the obsolete prepared copy");
        var current = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Check(current.Result, "replacement can measure after reentrant notification");
        var old = fixture.Current;
        fixture.SourceCurrent = (_, __) =>
        {
            fixture.SourceCurrent = null;
            fixture.Attach(new Snapshot(new Source(11)), false);
            return true;
        };
        Check(fixture.Queue.Get(old, 0) == null && !fixture.Queue.Has(old, 0),
            "a reentrant source check cannot return a value from an invalidated snapshot");
    }

    private static void ReentrantRetentionPreparation()
    {
        using var fixture = new Fixture();
        var original = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Check(original.Result, "retention fixture has a witnessed value");
        Task<bool>? replacement = null;
        fixture.AfterCopy = () =>
        {
            fixture.AfterCopy = null;
            fixture.Attach(new Snapshot(new Source(9)), false);
            replacement = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui();
            Check(fixture.Delays.Count == 0 && fixture.Workers.Count == 0,
                "nested replacement cannot start a worker during retention proof preparation");
        };
        fixture.Attach(new Snapshot(fixture.Current.Rows[0]), true);
        Check(fixture.Resources.Count == 2 && fixture.Resources[1].Disposals == 1 && !fixture.Queue.Has(fixture.Current, 0),
            "a proof returned after reentrant replacement is discarded exactly once");
        fixture.Delay(); fixture.Worker(); fixture.Ui();
        Check(replacement != null && replacement.Result && fixture.Queue.Get(fixture.Current, 0)?.Value == 9 &&
            fixture.Resources.TrueForAll(resource => resource.Disposals == 1),
            "the newer snapshot starts only after retention preparation and disposal finish");
    }

    private static void MeasurementContextChanges()
    {
        var original = new MeasurementContextStamp(1, 2, 0.001, "mm");
        Check(!original.Equals(new MeasurementContextStamp(2, 2, 0.001, "mm")) &&
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
            Check(stale.IsCompleted && !stale.Result && fixture.Workers.Count == 0 && fixture.Resources[0].Disposals == 1,
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
            Check(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "context-change worker is gated after claiming");
            live = new MeasurementContextStamp(2, 2, 0.001, "mm");
            gate.Set(); Check(worker.Wait(TimeSpan.FromSeconds(5)), "context-change worker finishes"); fixture.Ui();
            Check(stale.IsCompleted && !stale.Result && !fixture.Queue.Has(fixture.Current, 0) && fixture.Resources[0].Disposals == 1,
                "a document change during worker execution rejects its result and releases the copy");
        }
        using (var fixture = new Fixture { UseLegacyAdmission = true, OmitWitness = true })
        {
            var live = original;
            fixture.SourceCurrent = (_, __) => original.Equals(live);
            var measured = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
            Check(measured.Result && fixture.Queue.Has(fixture.Current, 0), "unchanged context admits a measurement");
            live = new MeasurementContextStamp(1, 3, 0.001, "mm");
            fixture.Queue.Attach(fixture.Current, false);
            Check(!fixture.Queue.Has(fixture.Current, 0) && fixture.Queue.Get(fixture.Current, 0) == null,
                "a model-unit change after completion hides the old value even on same-snapshot reuse");
        }
    }

    private static void CapacityRecoveryAndCancellation()
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
            Check(!canceled.IsCompleted && !waiting.IsCompleted && first.PreparedPositions.Count == 1 && second.PreparedPositions.Count == 1 &&
                first.Delays.Count == 0 && second.Delays.Count == 0 && first.Resources.Count == 0 && second.Resources.Count == 0,
                "two queues wait for shared capacity without allocating or spinning");
            cancel.Cancel(); first.Ui();
            Check(canceled.IsCompleted && !canceled.Result, "capacity waiting honors cancellation");
            held[0].Dispose();
            held[1].Dispose();
            Check(SpinWait.SpinUntil(() => { second.Ui(); return second.Delays.Count != 0; }, TimeSpan.FromSeconds(5)),
                "capacity release posts an asynchronous retry to the waiting queue");
            second.Delay(); second.Worker(); second.Ui(); first.Ui();
            Check(waiting.IsCompleted && waiting.Result && second.PreparedPositions.Count == 2 && first.PreparedPositions.Count == 1,
                "one waiting panel recovers and a canceled panel does not restart");

            // Fill the returned slot again, then replace the waiting generation.
            while ((reservation = NurbsCandidateLimits.TryReserve(1)) != null) held.Add(reservation);
            using var replaced = new Fixture { UseReservations = true };
            var old = replaced.Measure(CancellationToken.None, 0);
            replaced.Ui(); replaced.Delay();
            Check(!old.IsCompleted, "replacement case is waiting on the shared budget");
            replaced.Attach(new Snapshot(new Source(99)), false);
            Check(old.IsCompleted && !old.Result, "replacement cancels the old waiting generation");
            held[2].Dispose();
            replaced.Ui();
            Check(replaced.PreparedPositions.Count == 1 && replaced.Delays.Count == 0,
                "capacity release does not revive the replaced request");
        }
        finally { foreach (var item in held) item.Dispose(); }
    }

    private static void SourceReplacement()
    {
        using var fixture = new Fixture();
        bool current = true;
        fixture.SourceCurrent = (_, __) => current;
        var batch = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        current = false;
        fixture.Worker(); fixture.Ui();
        Check(batch.IsCompleted && !batch.Result && !fixture.Queue.Has(fixture.Current, 0) &&
            fixture.Queue.Get(fixture.Current, 0) == null && fixture.Resources[0].Disposals == 1,
            "replacement after preparation rejects the result and releases its worker copy");
        current = true;
        var later = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Check(later.Result && fixture.Queue.Has(fixture.Current, 0), "current source can be measured");
        current = false;
        Check(!fixture.Queue.Has(fixture.Current, 0) && fixture.Queue.Get(fixture.Current, 0) == null &&
            !fixture.Queue.SourcesCurrent(fixture.Current),
            "cached results are hidden immediately when their source changes");
    }

    private static void GlobalRetentionBudget()
    {
        Check(MeasurementRetentionBudget.TryAcquire(MeasurementRetentionBudget.MaxBytes), "process-wide witness capacity can be reserved");
        try
        {
            using var fixture = new Fixture();
            var measured = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
            Check(measured.Result && fixture.Queue.Has(fixture.Current, 0), "global budget exhaustion does not prevent measurement");
            fixture.Attach(new Snapshot(fixture.Current.Rows[0]), true);
            Check(!fixture.Queue.Has(fixture.Current, 0), "a witness that cannot be globally retained is not reused");
        }
        finally { MeasurementRetentionBudget.Release(MeasurementRetentionBudget.MaxBytes); }
        using var afterRelease = new Fixture();
        var later = afterRelease.Measure(CancellationToken.None, 0);
        afterRelease.Ui(); afterRelease.Delay(); afterRelease.Worker(); afterRelease.Ui();
        afterRelease.Attach(new Snapshot(afterRelease.Current.Rows[0]), true);
        Check(later.Result && afterRelease.Queue.Has(afterRelease.Current, 0), "released global capacity allows retention again");
    }

    private static void CancellationRegistration()
    {
        using var fixture = new Fixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var already = fixture.Measure(canceled.Token, 0);
        fixture.Ui();
        Check(already.IsCompleted && !already.Result && fixture.Resources.Count == 0, "already canceled registration");

        using var racing = new CancellationTokenSource();
        fixture.InlineCancellation = true;
        fixture.OnValidate = () => { fixture.OnValidate = null; racing.Cancel(); };
        var raced = fixture.Measure(racing.Token, 0);
        fixture.Ui();
        Check(raced.IsCompleted && !raced.Result && fixture.Resources.Count == 0 && fixture.Delays.Count == 0,
            "synchronous cancellation during registration is safe");
    }

    private static void SharedBeforeClaim()
    {
        using var fixture = new Fixture();
        using var firstCancellation = new CancellationTokenSource();
        var first = fixture.Measure(firstCancellation.Token, 0);
        var second = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        var resource = fixture.Resources[0];
        firstCancellation.Cancel(); fixture.Ui();
        Check(first.IsCompleted && !first.Result && !second.IsCompleted && resource.Disposals == 0,
            "one pre-claim cancellation leaves shared work alive");
        fixture.Worker(); fixture.Ui();
        Check(second.IsCompleted && second.Result && fixture.Queue.Get(fixture.Current, 0)?.Value == 1 &&
            fixture.Resources.Count == 1 && resource.Disposals == 1 && fixture.Computations == 1,
            "remaining consumer receives the shared result and one disposal");
    }

    private static void SharedAfterClaim()
    {
        using var fixture = new Fixture();
        using var firstCancellation = new CancellationTokenSource();
        using var gate = new ManualResetEventSlim(false);
        fixture.Gate = gate;
        var first = fixture.Measure(firstCancellation.Token, 0);
        var second = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        var worker = Task.Run(fixture.Worker);
        Check(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "worker claimed copy");
        firstCancellation.Cancel(); fixture.Ui();
        Check(first.IsCompleted && !first.Result && !second.IsCompleted && fixture.Resources[0].Disposals == 0,
            "claimed copy stays alive after a shared consumer cancels");
        gate.Set(); Check(worker.Wait(TimeSpan.FromSeconds(5)), "gated worker finished"); fixture.Ui();
        Check(second.IsCompleted && second.Result && fixture.Resources[0].Disposals == 1,
            "worker owns claimed copy until completion");
    }

    private static void AbandonedLateWorker()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var task = fixture.Measure(cancellation.Token, 0);
        fixture.Ui(); fixture.Delay();
        cancellation.Cancel(); fixture.Ui();
        Check(task.IsCompleted && !task.Result && fixture.Resources[0].Disposals == 1, "unclaimed work abandoned");
        fixture.Worker(); fixture.Ui();
        Check(fixture.Computations == 0 && fixture.Resources[0].Disposals == 1 && !fixture.Queue.Has(fixture.Current, 0),
            "late worker cannot claim or dispose abandoned work");
    }

    private static void DelayedAcceptedWorkerDoesNotBlock()
    {
        using var fixture = new Fixture();
        fixture.Attach(new Snapshot(new Source(1), new Source(2), new Source(3), new Source(4)), false);
        using var cancellation = new CancellationTokenSource();
        var obsolete = fixture.Measure(cancellation.Token, 0);
        fixture.Ui(); fixture.Delay();
        var lateCallback = fixture.TakeWorker(); // accepted, but not yet claimed
        cancellation.Cancel(); fixture.Ui();
        Check(obsolete.IsCompleted && !obsolete.Result && fixture.Resources[0].Disposals == 1,
            "canceling the sole consumer abandons the accepted unclaimed copy");

        var next = fixture.Measure(CancellationToken.None, 1);
        fixture.Ui();
        Check(fixture.Delays.Count == 1, "a delayed obsolete callback does not occupy the worker slot");
        fixture.Delay(); fixture.Worker(); fixture.Ui();
        Check(next.IsCompleted && next.Result && fixture.Queue.Get(fixture.Current, 1)?.Value == 2 &&
            fixture.Computations == 1, "a different item completes before the obsolete callback executes");

        using var sharedCancellation = new CancellationTokenSource();
        using var gate = new ManualResetEventSlim(false);
        fixture.Gate = gate;
        fixture.Started.Reset();
        var canceledShared = fixture.Measure(sharedCancellation.Token, 2);
        var shared = fixture.Measure(CancellationToken.None, 2);
        fixture.Ui(); fixture.Delay();
        var claimedWorker = Task.Run(fixture.Worker);
        Check(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "shared work has been claimed by its worker");
        try
        {
            sharedCancellation.Cancel(); fixture.Ui();
            var following = fixture.Measure(CancellationToken.None, 3);
            fixture.Ui();
            Check(canceledShared.IsCompleted && !canceledShared.Result && !shared.IsCompleted &&
                fixture.Resources[2].Disposals == 0 && fixture.Delays.Count == 0 && fixture.Workers.Count == 0,
                "a shared consumer keeps claimed work alive and a following item waits");

            fixture.RunWorker(lateCallback); fixture.Ui();
            Check(fixture.Resources[0].Disposals == 1 && fixture.Computations == 2 &&
                !fixture.Queue.Has(fixture.Current, 0) && !shared.IsCompleted && !following.IsCompleted &&
                fixture.Delays.Count == 0 && fixture.Workers.Count == 0,
                "late abandoned callback neither disposes twice nor publishes or releases a claimed worker slot");

            gate.Set();
            Check(claimedWorker.Wait(TimeSpan.FromSeconds(5)), "shared claimed worker completed");
            fixture.Ui();
            Check(shared.IsCompleted && shared.Result && fixture.Queue.Get(fixture.Current, 2)?.Value == 3 &&
                fixture.Resources[2].Disposals == 1 && fixture.Delays.Count == 1,
                "the remaining shared consumer receives exactly one result before the next worker starts");
            fixture.Gate = null;
            fixture.Delay(); fixture.Worker(); fixture.Ui();
            Check(following.IsCompleted && following.Result && fixture.Queue.Get(fixture.Current, 3)?.Value == 4 &&
                fixture.Computations == 3 && fixture.Resources.TrueForAll(resource => resource.Disposals == 1) &&
                !fixture.Queue.Has(fixture.Current, 0), "later work completes with exactly-once disposal and no stale result");
        }
        finally { gate.Set(); }
    }

    private static void DelayedWorkerAfterReplacementAndVisibilityChange()
    {
        foreach (bool retain in new[] { false, true })
        {
            using var fixture = new Fixture();
            var old = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui(); fixture.Delay();
            var late = fixture.TakeWorker();
            fixture.Queue.Suspend(retain);
            fixture.Attach(new Snapshot(new Source(42)), false);
            Check(old.IsCompleted && !old.Result && fixture.Resources[0].Disposals == 1,
                "suspending an unclaimed worker releases it whether retained entries are requested or not");
            var replacement = fixture.Measure(CancellationToken.None, 0);
            fixture.Ui();
            Check(fixture.Delays.Count == 1, "replacement generation schedules before the old callback");
            fixture.Delay();
            fixture.RunWorker(late); fixture.Ui();
            Check(!replacement.IsCompleted && fixture.Resources[1].Disposals == 0 && !fixture.Queue.Has(fixture.Current, 0),
                "the old callback cannot release or publish over the new entry at the same position");
            fixture.Worker(); fixture.Ui();
            Check(replacement.Result && fixture.Queue.Get(fixture.Current, 0)?.Value == 42 &&
                fixture.Resources.TrueForAll(resource => resource.Disposals == 1),
                "replacement computes its own value with exactly-once disposal");
        }

        using var visible = new Fixture();
        visible.Queue.RequestVisible(visible.Current, new[] { 0 });
        visible.Delay();
        var oldVisible = visible.TakeWorker();
        visible.Queue.RequestVisible(visible.Current, new[] { 1 });
        Check(visible.Resources[0].Disposals == 1 && visible.Delays.Count == 1,
            "changing visible demand releases an unclaimed worker without waiting for its callback");
        visible.Delay(); visible.Worker(); visible.Ui();
        visible.RunWorker(oldVisible); visible.Ui();
        Check(!visible.Queue.Has(visible.Current, 0) && visible.Queue.Get(visible.Current, 1)?.Value == 2 &&
            visible.Resources.TrueForAll(resource => resource.Disposals == 1), "a late visible callback cannot publish stale work");
    }

    private static void AbandonReleasesPreparedReferences()
    {
        var resource = new Resource();
        var work = new PreparedMeasurement<Result>(() => new Result(1), resource,
            new MeasurementWitness("test", new byte[1024]));
        Check(work.Witness != null && work.TryAbandon() && work.Witness == null && resource.Disposals == 1,
            "abandonment releases the prepared proof and resource before a delayed callback");
        Check(!work.TryRun(out _) && !work.TryAbandon() && resource.Disposals == 1,
            "late claims and repeated abandonment cannot run or dispose the work again");
    }

    private static void RejectedWorker()
    {
        using var fixture = new Fixture { RejectWorker = true };
        var task = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        Check(task.IsCompleted && task.Result && fixture.Resources[0].Disposals == 1 &&
            fixture.Queue.Has(fixture.Current, 0) && fixture.Queue.Get(fixture.Current, 0) == null,
            "rejected submission disposes and records an unavailable result");
    }

    private static void ThrowingWorkerSubmission()
    {
        using var fixture = new Fixture { ThrowSubmit = true };
        var task = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        Check(task.IsCompleted && task.Result && fixture.Resources[0].Disposals == 1 && fixture.Queue.Has(fixture.Current, 0),
            "throwing worker submission releases unclaimed work");
    }

    private static void ReplacementAndDisposal()
    {
        using var fixture = new Fixture();
        using var gate = new ManualResetEventSlim(false);
        fixture.Gate = gate;
        var old = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        var worker = Task.Run(fixture.Worker);
        Check(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "old worker running");
        fixture.Attach(new Snapshot(new Source(22)), false);
        Check(old.IsCompleted && !old.Result && fixture.Resources[0].Disposals == 0, "replacement never disposes a claimed copy");
        var current = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui();
        Check(fixture.Workers.Count == 0, "replacement waits for the existing worker");
        gate.Set(); Check(worker.Wait(TimeSpan.FromSeconds(5)), "old worker finished"); fixture.Ui();
        Check(!fixture.Queue.Has(fixture.Current, 0), "stale successful result is rejected");
        fixture.Gate = null; fixture.Delay(); fixture.Worker(); fixture.Ui();
        Check(current.IsCompleted && current.Result && fixture.Queue.Get(fixture.Current, 0)?.Value == 22 &&
            fixture.Resources[0].Disposals == 1 && fixture.Resources[1].Disposals == 1, "new source is measured after stale success");

        using var other = new Fixture();
        using var gate2 = new ManualResetEventSlim(false);
        other.Gate = gate2;
        var pending = other.Measure(CancellationToken.None, 0);
        other.Ui(); other.Delay();
        var running = Task.Run(other.Worker);
        Check(other.Started.Wait(TimeSpan.FromSeconds(5)), "disposal worker running");
        other.Queue.Dispose();
        Check(pending.IsCompleted && !pending.Result && other.Resources[0].Disposals == 0, "session disposal leaves claimed work alive");
        gate2.Set(); Check(running.Wait(TimeSpan.FromSeconds(5)), "disposal worker finished"); other.Ui();
        Check(other.Resources[0].Disposals == 1 && !other.Queue.Has(other.Current, 0), "disposed session rejects publication");
    }

    private static void StaleException()
    {
        using var fixture = new Fixture();
        using var gate = new ManualResetEventSlim(false);
        fixture.Gate = gate; fixture.FailCompute = true;
        var old = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        var worker = Task.Run(fixture.Worker);
        Check(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "exception worker running");
        fixture.Attach(new Snapshot(new Source(8)), false);
        gate.Set(); Check(worker.Wait(TimeSpan.FromSeconds(5)), "exception worker finished"); fixture.Ui();
        Check(old.IsCompleted && !old.Result && fixture.Resources[0].Disposals == 1 && !fixture.Queue.Has(fixture.Current, 0),
            "stale exception disposes without poisoning replacement");
    }

    private static void ValueAndContextRetention()
    {
        using var fixture = new Fixture();
        var source = fixture.Current.Rows[0];
        var first = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Check(first.Result, "initial measurement");
        fixture.Attach(new Snapshot(source), true);
        Check(fixture.Queue.Has(fixture.Current, 0) && fixture.Queue.Get(fixture.Current, 0)?.Value == 1 &&
            fixture.Resources[1].Disposals == 1, "same full value and context retain completed result");
        source.Value = 2; // same holder identity with a different value
        fixture.Attach(new Snapshot(source), true);
        Check(!fixture.Queue.Has(fixture.Current, 0), "reused identity with changed value is not retained");
        var second = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay(); fixture.Worker(); fixture.Ui();
        Check(second.Result && fixture.Queue.Get(fixture.Current, 0)?.Value == 2, "changed value remeasured");
        source.Role = "surface";
        fixture.Attach(new Snapshot(source), true);
        Check(!fixture.Queue.Has(fixture.Current, 0), "role change invalidates identical geometry");
    }

    private static void WitnessIsImmutable()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var original = new MeasurementWitness("curve|mm|tolerance|length", bytes);
        bytes[0] = 9;
        Check(original.Equals(new MeasurementWitness("curve|mm|tolerance|length", new byte[] { 1, 2, 3 })) &&
            !original.Equals(new MeasurementWitness("surface|mm|tolerance|area", new byte[] { 1, 2, 3 })) &&
            !original.Equals(new MeasurementWitness("curve|cm|tolerance|length", new byte[] { 1, 2, 3 })) &&
            !original.Equals(new MeasurementWitness("curve|mm|other-tolerance|length", new byte[] { 1, 2, 3 })),
            "immutable full witness includes geometry role, metric, units and tolerance");
    }

    private static void RetainedProofBudget()
    {
        using var fixture = new Fixture();
        var sources = new Source[130];
        var positions = new int[sources.Length];
        for (int i = 0; i < sources.Length; i++) { sources[i] = new Source(i); positions[i] = i; }
        fixture.Attach(new Snapshot(sources), false);
        var batch = fixture.Measure(CancellationToken.None, positions);
        fixture.Ui();
        while (!batch.IsCompleted) { fixture.Delay(); fixture.Worker(); fixture.Ui(); }
        Check(batch.Result, "large batch completes");
        int preparedBefore = fixture.PreparedPositions.Count;
        fixture.Attach(new Snapshot(sources), true);
        int retained = 0;
        for (int i = 0; i < sources.Length; i++) if (fixture.Queue.Has(fixture.Current, i)) retained++;
        Check(retained == 128 && fixture.PreparedPositions.Count - preparedBefore == 128,
            "retention limits both proof count and current-value preparation work");
    }

    private static void CopyBeforeWitness()
    {
        using var fixture = new Fixture();
        using var gate = new ManualResetEventSlim(false);
        var source = fixture.Current.Rows[0];
        fixture.Gate = gate;
        fixture.AfterCopy = () => { fixture.AfterCopy = null; source.Value = 9; };
        var task = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        var worker = Task.Run(fixture.Worker);
        Check(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "in-flight copy made before source mutation");
        fixture.Attach(new Snapshot(source), true);
        Check(task.IsCompleted && !task.Result && fixture.Resources[0].Disposals == 0, "different current witness rejects in-flight copy");
        gate.Set(); Check(worker.Wait(TimeSpan.FromSeconds(5)), "in-flight worker finished"); fixture.Ui();
        Check(!fixture.Queue.Has(fixture.Current, 0) && fixture.Resources[0].Disposals == 1,
            "witness reflects the worker copy, not the later live source");

        using var completed = new Fixture();
        var live = completed.Current.Rows[0];
        completed.AfterCopy = () => { completed.AfterCopy = null; live.Value = 7; };
        var measured = completed.Measure(CancellationToken.None, 0);
        completed.Ui(); completed.Delay(); completed.Worker(); completed.Ui();
        Check(measured.Result && completed.Queue.Get(completed.Current, 0)?.Value == 1, "completed result uses old copy");
        completed.Attach(new Snapshot(live), true);
        Check(!completed.Queue.Has(completed.Current, 0), "completed result cannot retain newer live witness");
    }

    private static void InFlightRetentionAndRoleSwitch()
    {
        using var fixture = new Fixture();
        using var gate = new ManualResetEventSlim(false);
        fixture.Gate = gate;
        var source = fixture.Current.Rows[0];
        var original = fixture.Measure(CancellationToken.None, 0);
        fixture.Ui(); fixture.Delay();
        var worker = Task.Run(fixture.Worker);
        Check(fixture.Started.Wait(TimeSpan.FromSeconds(5)), "retained worker claimed");
        fixture.Attach(new Snapshot(source), true);
        Check(original.IsCompleted && !original.Result && fixture.Resources[0].Disposals == 0 && fixture.Resources[1].Disposals == 1,
            "same in-flight proof is compared without touching the worker copy");
        gate.Set(); Check(worker.Wait(TimeSpan.FromSeconds(5)), "retained worker finished"); fixture.Ui();
        Check(fixture.Queue.Has(fixture.Current, 0) && fixture.Queue.Get(fixture.Current, 0)?.Value == 1 &&
            fixture.Resources[0].Disposals == 1 && fixture.Computations == 1, "unchanged in-flight value retained");

        using var changed = new Fixture();
        using var changedGate = new ManualResetEventSlim(false);
        changed.Gate = changedGate;
        var live = changed.Current.Rows[0];
        changed.Measure(CancellationToken.None, 0);
        changed.Ui(); changed.Delay();
        var changedWorker = Task.Run(changed.Worker);
        Check(changed.Started.Wait(TimeSpan.FromSeconds(5)), "role-switch worker claimed");
        live.Role = "surface";
        changed.Attach(new Snapshot(live), true);
        changedGate.Set(); Check(changedWorker.Wait(TimeSpan.FromSeconds(5)), "role-switch worker finished"); changed.Ui();
        Check(!changed.Queue.Has(changed.Current, 0) && changed.Resources[0].Disposals == 1,
            "in-flight result is rejected when the measurement role changes");
    }

    private static void SchedulingAndAffinity()
    {
        using var fixture = new Fixture();
        fixture.Queue.RequestVisible(fixture.Current, new[] { 1, 0 });
        Check(fixture.Delays.Count == 1 && fixture.Delays.Peek().delay == 40 && fixture.Resources.Count == 0,
            "viewport preparation is deferred");
        var explicitRequest = fixture.Measure(CancellationToken.None, 2);
        fixture.Ui(); fixture.Delay();
        Check(fixture.PreparedPositions.Count == 1 && fixture.PreparedPositions[0] == 2 && fixture.Workers.Count == 1,
            "explicit work has priority over delayed visible work");
        fixture.Queue.RequestVisible(fixture.Current, new[] { 0 });
        fixture.Worker(); fixture.Ui();
        Check(explicitRequest.IsCompleted && explicitRequest.Result && fixture.Delays.Count == 1 && fixture.Delays.Peek().delay == 0,
            "single worker resumes on UI with no added delay");
        fixture.Delay(); fixture.Worker(); fixture.Ui();
        Check(fixture.PreparedPositions.Count == 2 && fixture.PreparedPositions[1] == 0 &&
            fixture.Computations == 2 && fixture.UiNotifications > 0 && fixture.WrongThreadCalls == 0,
            "viewport replacement, UI affinity and worker boundary are respected");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
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

        internal Fixture()
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
                (snapshot, position) => SourceCurrent?.Invoke(snapshot, position) ?? true);
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
