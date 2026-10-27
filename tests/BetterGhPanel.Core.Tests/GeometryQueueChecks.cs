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
        internal int Disposals;
        public void Dispose() { if (Interlocked.Increment(ref Disposals) != 1) throw new InvalidOperationException("double dispose"); }
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
        internal Action? AfterCopy, OnValidate;
        internal bool InlineCancellation, RejectWorker, ThrowSubmit, FailCompute;
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
                Prepare, () => { VerifyUi(); UiNotifications++; });
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
            var source = Current.Rows[position];
            int copiedValue = source.Value;
            string copiedRole = source.Role;
            AfterCopy?.Invoke();
            var resource = new Resource(); Resources.Add(resource);
            var witness = new MeasurementWitness(copiedRole + "|mm|0.001|length", BitConverter.GetBytes(copiedValue));
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
            if (!Workers.TryDequeue(out var action)) throw new InvalidOperationException("no worker");
            _onWorker.Value = true;
            try { action(); } finally { _onWorker.Value = false; }
        }
        public void Dispose() { Queue.Dispose(); Started.Dispose(); _onWorker.Dispose(); }
    }
}
