using Xunit;
using BetterGhPanel.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// This links the production queue, rather than a model of its state machine.
public class GeometryQueueTests
{



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



    private sealed class Source
    {
        internal Source(int value) { Value = value; }
        internal int Value;
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
        public void Dispose()
        {
            if (Interlocked.Increment(ref Disposals) != 1) throw new InvalidOperationException("double dispose");
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
        internal readonly ManualResetEventSlim Started = new ManualResetEventSlim(false);
        internal readonly GeometryMeasurementQueue<Snapshot, Result> Queue;
        internal Snapshot Current = new Snapshot(new Source(1), new Source(2), new Source(3));
        internal ManualResetEventSlim? Gate;
        internal Action? OnValidate;
        internal bool InlineCancellation, RejectWorker, ThrowSubmit, FailCompute;
        internal int Computations;

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
                Prepare, VerifyUi, (_, __) => true);
            Queue.Attach(Current, false);
        }
        private void VerifyUi()
        {
            if (Environment.CurrentManagedThreadId != _uiThread || _onWorker.Value)
                throw new InvalidOperationException("not on UI");
        }
        private PreparedMeasurement<Result> Prepare(int position)
        {
            VerifyUi();
            int copiedValue = Current.Rows[position].Value;
            var resource = new Resource(); Resources.Add(resource);
            var witness = new MeasurementWitness("curve|mm|0.001|length", BitConverter.GetBytes(copiedValue));
            var gate = Gate; bool fail = FailCompute;
            return new PreparedMeasurement<Result>(() =>
            {
                if (!_onWorker.Value) throw new InvalidOperationException("not on worker");
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
