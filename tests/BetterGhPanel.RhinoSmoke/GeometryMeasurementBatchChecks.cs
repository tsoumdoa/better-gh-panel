using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;

internal static class GeometryMeasurementBatchChecks
{
    internal static async Task VerifyPriority(Assembly assembly)
    {
        using var fixture = new Fixture(assembly);
        using var gate = new ManualResetEventSlim(false);
        var prepared = new List<int>();
        int active = 0, maximumActive = 0, uiThread = Environment.CurrentManagedThreadId;
        bool preparationOnUi = true, computationOnWorker = true;
        fixture.SetResolver(position =>
        {
            preparationOnUi &= Environment.CurrentManagedThreadId == uiThread;
            prepared.Add(position);
            return () =>
            {
                computationOnWorker &= Environment.CurrentManagedThreadId != uiThread;
                maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
                try
                {
                    if (position == 0 && !gate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
                    return new PanelGeometryInfo("Measured", position + 1);
                }
                finally { Interlocked.Decrement(ref active); }
            };
        });
        try
        {
            fixture.RequestVisible(0, 1);
            await WaitUntil(() => prepared.Count == 1);
            var batch = fixture.Measure(CancellationToken.None, 4, 2, 4, 5, 6);
            fixture.RequestVisible(1);
            Check(!batch.IsCompleted && fixture.Pending(4), "Selected offscreen metrics stay pending while the viewport worker finishes");
            gate.Set();
            Check(await Result(batch), "Explicit multirow measurement completes");
            await WaitUntil(() => fixture.Has(1));
            Check(prepared.SequenceEqual(new[] { 0, 4, 2, 1 }) && fixture.Details(4)?.Value == 5,
                "Offscreen selected rows precede viewport work, deduplicate positions and skip existing analytic values");
            Check(maximumActive == 1 && preparationOnUi && computationOnWorker,
                "Explicit and visible measurements share one worker with all geometry preparation on the UI thread");
        }
        finally { gate.Set(); }
    }

    internal static async Task VerifyCancellation(Assembly assembly)
    {
        using var fixture = new Fixture(assembly);
        using var gate = new ManualResetEventSlim(false);
        using var cancellation = new CancellationTokenSource();
        var prepared = new List<int>();
        fixture.SetResolver(position =>
        {
            prepared.Add(position);
            return () =>
            {
                if (position == 0 && !gate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
                return new PanelGeometryInfo("Measured", position);
            };
        });
        try
        {
            var first = fixture.Measure(cancellation.Token, 0, 1);
            await WaitUntil(() => prepared.Count == 1);
            var overlapping = fixture.Measure(CancellationToken.None, 0, 2);
            await Task.Run(cancellation.Cancel);
            Check(!await Result(first) && !overlapping.IsCompleted && !fixture.Pending(1),
                "Worker-thread cancellation resolves its batch immediately and drops only unwanted queued rows");
            gate.Set();
            Check(await Result(overlapping) && fixture.Has(0) && fixture.Has(2),
                "Canceling an in-flight batch retains native work needed by another selection");
            await Task.Delay(100);
            Check(prepared.SequenceEqual(new[] { 0, 2 }) && !fixture.Has(1),
                "Cancellation never starts the abandoned offscreen row or duplicates a shared calculation");

            using var alreadyCanceled = new CancellationTokenSource();
            alreadyCanceled.Cancel();
            Check(!await Result(fixture.Measure(alreadyCanceled.Token, 3)) && !fixture.Pending(3),
                "Already canceled selections cannot queue geometry");
            using var immediate = new CancellationTokenSource();
            var queued = fixture.Measure(immediate.Token, 3, 4);
            immediate.Cancel();
            Check(!await Result(queued), "Cancellation before the deferred pump resolves the selection");
            await Task.Delay(100);
            Check(prepared.SequenceEqual(new[] { 0, 2 }), "Canceling before preparation prevents all queued native copies");
        }
        finally { gate.Set(); }
    }

    internal static async Task VerifyInvalidation(Assembly assembly)
    {
        using var fixture = new Fixture(assembly);
        using var gate = new ManualResetEventSlim(false);
        var prepared = new List<int>();
        int active = 0;
        fixture.SetResolver(position =>
        {
            prepared.Add(position);
            return () =>
            {
                Interlocked.Increment(ref active);
                try
                {
                    if (!gate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
                    return new PanelGeometryInfo("Measured", position);
                }
                finally { Interlocked.Decrement(ref active); }
            };
        });
        try
        {
            var original = fixture.Snapshot;
            var convertedBatch = fixture.Measure(CancellationToken.None, 0, 1);
            await WaitUntil(() => prepared.Count == 1);
            fixture.Attach(new PanelSnapshot(original.Rows, original.Branches, PanelUnitConversion.MillimetersToMeters), true);
            Check(!await Result(convertedBatch), "Even unit-only snapshot changes reject pending copy completion");
            gate.Set();
            await WaitUntil(() => fixture.Has(0));
            Check(prepared.SequenceEqual(new[] { 0 }) && fixture.Details(0)?.Value == 0,
                "Unit-only invalidation retains allowed in-flight metrics without continuing the abandoned selection");

            gate.Reset();
            var replaced = fixture.Measure(CancellationToken.None, 2, 3);
            await WaitUntil(() => prepared.Count == 2);
            fixture.Attach(new PanelSnapshot(original.Rows, original.Branches));
            Check(!await Result(replaced), "New solutions resolve every old selected-row waiter before native work completes");
            var current = fixture.Measure(CancellationToken.None, 4);
            gate.Set();
            Check(await Result(current) && !fixture.Has(2) && prepared.SequenceEqual(new[] { 0, 2, 4 }),
                "New solution requests wait for the old worker, reject stale results and skip stale queued rows");

            gate.Reset();
            var disposed = fixture.Measure(CancellationToken.None, 0, 1);
            await WaitUntil(() => prepared.Count == 4);
            fixture.Dispose();
            Check(!await Result(disposed), "Disposal resolves selected-row waiters while native work is still running");
            gate.Set();
            await WaitUntil(() => Volatile.Read(ref active) == 0);
            await Task.Delay(100);
            Check(!await Result(fixture.Measure(CancellationToken.None, 3)) && prepared.Count == 4,
                "Disposed sessions cannot publish or prepare any further selected-row measurements");
        }
        finally { gate.Set(); }
    }

    internal static async Task VerifyFailures(Assembly assembly)
    {
        using var fixture = new Fixture(assembly);
        var prepared = new List<int>();
        fixture.SetResolver(position =>
        {
            prepared.Add(position);
            if (position == 0) throw new InvalidOperationException("Preparation failed");
            if (position == 1) return null;
            if (position == 2) return () => throw new InvalidOperationException("Calculation failed");
            return () => null;
        });
        Check(await Result(fixture.Measure(CancellationToken.None, 0, 1, 2, 3)) &&
            Enumerable.Range(0, 4).All(position => fixture.Has(position) && fixture.Details(position) == null),
            "Preparation and calculation failures finish selected-row batches and cache unavailable metrics");
        Check(await Result(fixture.Measure(CancellationToken.None, 0, 1, 2, 3)) && prepared.Count == 4,
            "Repeated copies reuse cached measurement failures without retry loops");
        Check(!await Result(fixture.Measure(CancellationToken.None, -1)) &&
            !await Result(fixture.Measure(CancellationToken.None, fixture.Snapshot.Rows.Count)),
            "Invalid selected positions reject the batch without indexing outside the snapshot");
        Check(await Result(fixture.Measure(CancellationToken.None)), "Empty measurement selections complete without work");
    }

    private static async Task<bool> Result(Task<bool> task)
    {
        await WaitUntil(() => task.IsCompleted);
        return await task;
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Explicit geometry measurement did not finish");
            await Task.Delay(10);
        }
    }

    private static void Check(bool condition, string description) => SmokeAssertions.Check(condition, description);

    private sealed class Fixture : IDisposable
    {
        private readonly object _session;
        private readonly Type _type;

        internal Fixture(Assembly assembly)
        {
            _type = assembly.GetType("BetterGhPanel.Runtime.PanelSession", true)!;
            _session = Activator.CreateInstance(_type, new object[] { (Action)(() => { }) })!;
            var rows = Enumerable.Range(0, 5).Select(position => new PanelRow(position, "{0}", "Curve", "Curve", null,
                geometryInfo: new PanelGeometryInfo("Curve", canMeasure: true))).ToList();
            rows.Add(new PanelRow(5, "{0}", "Number", "12", 12));
            rows.Add(new PanelRow(6, "{0}", "Curve", "Curve", null,
                geometryInfo: new PanelGeometryInfo("Curve", 12, canMeasure: true)));
            Snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", rows.Count) });
            Attach(Snapshot);
        }

        internal PanelSnapshot Snapshot { get; private set; }
        private object? Invoke(string name, params object[] arguments) =>
            _type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(_session, arguments);
        internal void SetResolver(Func<int, Func<PanelGeometryInfo?>?> resolver) => Invoke("SetGeometryDetailResolver", resolver);
        internal Task<bool> Measure(CancellationToken cancellation, params int[] positions) =>
            (Task<bool>)Invoke("MeasureGeometryAsync", Snapshot, positions, cancellation)!;
        internal void RequestVisible(params int[] positions) => Invoke("RequestGeometryMeasurements", Snapshot, positions);
        internal bool Has(int position) => (bool)Invoke("HasGeometryMeasurement", Snapshot, position)!;
        internal bool Pending(int position) => (bool)Invoke("IsGeometryPending", Snapshot, position)!;
        internal PanelGeometryInfo? Details(int position) => (PanelGeometryInfo?)Invoke("GetGeometryDetails", Snapshot, position);
        internal void Attach(PanelSnapshot snapshot, bool retain = false)
        {
            Snapshot = snapshot;
            if (retain) Invoke("AttachConvertedSnapshot", snapshot, (Func<int, bool>)(_ => true));
            else Invoke("AttachSnapshot", snapshot);
        }
        public void Dispose() => ((IDisposable)_session).Dispose();
    }
}
