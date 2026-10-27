using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGhPanel.Runtime
{
    // A witness is a complete value representation, not a hash or a live source.
    // Its producer is responsible for proving that the bytes describe the exact
    // owned geometry to be measured and that the context covers its result.
    internal sealed class MeasurementWitness : IEquatable<MeasurementWitness>
    {
        private readonly byte[] _value;
        internal MeasurementWitness(string context, byte[] value)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            _value = (byte[])(value ?? throw new ArgumentNullException(nameof(value))).Clone();
        }
        internal string Context { get; }
        internal int ByteCount
        {
            get
            {
                long size = (long)_value.Length + (long)Context.Length * sizeof(char);
                return size > int.MaxValue ? int.MaxValue : (int)size;
            }
        }
        public bool Equals(MeasurementWitness? other)
        {
            if (other == null || Context != other.Context || _value.Length != other._value.Length) return false;
            for (int i = 0; i < _value.Length; i++) if (_value[i] != other._value[i]) return false;
            return true;
        }
        public override bool Equals(object? obj) => Equals(obj as MeasurementWitness);
        public override int GetHashCode() => Context.GetHashCode();
    }

    // The existing per-session cap does not bound an arbitrary number of panels.
    // This cap accounts for retained witness payloads across all queues. It does
    // not count managed object headers or native geometry/mass-property allocations.
    internal static class MeasurementRetentionBudget
    {
        internal const int MaxBytes = 8 * 1024 * 1024;
        private static int _bytes;
        internal static bool TryAcquire(int bytes)
        {
            if (bytes <= 0 || bytes > MaxBytes) return false;
            while (true)
            {
                int current = Volatile.Read(ref _bytes);
                if (current > MaxBytes - bytes) return false;
                if (Interlocked.CompareExchange(ref _bytes, current + bytes, current) == current) return true;
            }
        }
        internal static void Release(int bytes) => Interlocked.Add(ref _bytes, -bytes);
    }

    // The UI can abandon only unclaimed work. Once the worker wins the claim,
    // it alone executes and disposes the resource, including on exception.
    internal sealed class PreparedMeasurement<TResult> : IDisposable where TResult : class
    {
        private Func<TResult?>? _compute;
        private IDisposable? _resource;
        private int _state; // 0 unclaimed, 1 claimed, 2 abandoned/completed

        internal PreparedMeasurement(Func<TResult?> compute, IDisposable? resource, MeasurementWitness? witness)
        {
            _compute = compute ?? throw new ArgumentNullException(nameof(compute));
            _resource = resource;
            Witness = witness;
        }
        internal MeasurementWitness? Witness { get; private set; }
        internal bool TryRun(out TResult? result)
        {
            result = null;
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
            try { result = _compute!(); }
            finally
            {
                try { _resource?.Dispose(); }
                finally
                {
                    _compute = null; _resource = null; Witness = null;
                    Volatile.Write(ref _state, 2);
                }
            }
            return true;
        }
        internal bool TryAbandon()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return false;
            try { _resource?.Dispose(); }
            catch (Exception) { /* The resource was still claimed for disposal exactly once. */ }
            finally { _compute = null; _resource = null; Witness = null; }
            return true;
        }
        public void Dispose() => TryAbandon();
    }

    // All state other than the atomic prepared-work claim belongs to the UI.
    // The injected boundaries let production scheduling be exercised without a
    // Rhino host, a real clock, or races against the thread pool.
    internal sealed class GeometryMeasurementQueue<TSnapshot, TResult> : IDisposable
        where TSnapshot : class where TResult : class
    {
        private const int MaxRetainedWitnessBytes = 2 * 1024 * 1024;
        private const int MaxRetainedWitnesses = 128;
        private readonly Action<Action> _postUi;
        private readonly Action<Action> _postCancellation;
        private readonly Action<int, Action> _delayOnUi;
        private readonly Func<Action, bool> _submitWorker;
        private readonly Action _verifyUi;
        private readonly Func<TSnapshot, int, bool> _validPosition;
        private readonly Func<TSnapshot, int, bool> _canMeasure;
        private readonly Func<TSnapshot, int, bool> _needsMeasurement;
        private readonly Func<TSnapshot, int, bool> _sourceCurrent;
        private readonly Func<int, PreparedMeasurement<TResult>?> _prepare;
        private readonly Func<int, PreparedMeasurement<TResult>?> _prepareForRetention;
        private readonly Action _changed;
        private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        private readonly List<int> _visible = new List<int>();
        private readonly List<Batch> _batches = new List<Batch>();
        private TSnapshot? _snapshot;
        private Entry? _running;
        private bool _scheduled, _disposed;
        private int _preparingDepth;
        private CancellationTokenSource? _capacityWait;
        private int _witnessBytes, _witnessCount;

        internal GeometryMeasurementQueue(Action<Action> postUi, Action<Action> postCancellation,
            Action<int, Action> delayOnUi, Func<Action, bool> submitWorker, Action verifyUi,
            Func<TSnapshot, int, bool> validPosition, Func<TSnapshot, int, bool> canMeasure, Func<TSnapshot, int, bool> needsMeasurement,
            Func<int, PreparedMeasurement<TResult>?> prepare, Action changed, Func<TSnapshot, int, bool>? sourceCurrent = null,
            Func<int, PreparedMeasurement<TResult>?>? prepareForRetention = null)
        {
            _postUi = postUi; _postCancellation = postCancellation; _delayOnUi = delayOnUi;
            _submitWorker = submitWorker; _verifyUi = verifyUi; _validPosition = validPosition; _canMeasure = canMeasure;
            _needsMeasurement = needsMeasurement; _prepare = prepare; _changed = changed;
            _sourceCurrent = sourceCurrent ?? ((_, __) => true);
            _prepareForRetention = prepareForRetention ?? prepare;
        }

        internal void Attach(TSnapshot snapshot, bool retain, Func<int, bool>? allowed = null)
        {
            _verifyUi();
            if (_disposed) return;
            if (ReferenceEquals(_snapshot, snapshot)) return;
            StopCapacityWait();
            CancelBatches();
            _visible.Clear();
            _snapshot = snapshot;
            foreach (var position in new List<int>(_entries.Keys))
            {
                if (!ReferenceEquals(_snapshot, snapshot) || _disposed) { PumpLater(); return; }
                if (!_entries.TryGetValue(position, out var entry)) continue;
                bool match = false;
                try
                {
                    if (retain && entry.Witness != null && _canMeasure(snapshot, position) && (allowed?.Invoke(position) ?? true))
                    {
                        _preparingDepth++;
                        PreparedMeasurement<TResult>? current = null;
                        try
                        {
                            current = _prepareForRetention(position);
                            match = current?.Witness != null && entry.Witness.Equals(current.Witness);
                        }
                        finally { try { current?.Dispose(); } finally { _preparingDepth--; } }
                    }
                }
                catch (Exception) { match = false; }
                if (!ReferenceEquals(_snapshot, snapshot) || _disposed) { PumpLater(); return; }
                if (!match) RemoveEntry(position, entry);
            }
            AbandonUndemanded();
            PumpLater();
        }

        internal void Suspend(bool retain)
        {
            _verifyUi();
            if (_disposed) return;
            StopCapacityWait();
            CancelBatches();
            _visible.Clear();
            _snapshot = null;
            if (!retain) ClearEntries();
            AbandonUndemanded();
        }

        private bool IsCurrentEntry(TSnapshot snapshot, int position, Entry entry) =>
            ReferenceEquals(snapshot, _snapshot) && _entries.TryGetValue(position, out var current) && ReferenceEquals(current, entry) &&
            _sourceCurrent(snapshot, position) && ReferenceEquals(snapshot, _snapshot) &&
            _entries.TryGetValue(position, out current) && ReferenceEquals(current, entry);

        internal TResult? Get(TSnapshot snapshot, int position) =>
            ReferenceEquals(snapshot, _snapshot) && _entries.TryGetValue(position, out var entry) && IsCurrentEntry(snapshot, position, entry) ? entry.Value : null;
        internal bool Has(TSnapshot snapshot, int position) =>
            ReferenceEquals(snapshot, _snapshot) && _entries.TryGetValue(position, out var entry) && entry.Complete && IsCurrentEntry(snapshot, position, entry);
        internal bool Pending(TSnapshot snapshot, int position) => ReferenceEquals(snapshot, _snapshot) &&
            ((_entries.TryGetValue(position, out var entry) && !entry.Complete) || _visible.Contains(position) ||
             _batches.Exists(batch => !batch.Token.IsCancellationRequested && batch.Pending.Contains(position)));
        internal IEnumerable<KeyValuePair<int, TResult?>> Completed(TSnapshot snapshot)
        {
            if (!ReferenceEquals(snapshot, _snapshot)) yield break;
            foreach (var pair in new List<KeyValuePair<int, Entry>>(_entries))
                if (pair.Value.Complete && IsCurrentEntry(snapshot, pair.Key, pair.Value)) yield return new KeyValuePair<int, TResult?>(pair.Key, pair.Value.Value);
        }

        internal bool SourcesCurrent(TSnapshot snapshot)
        {
            if (!ReferenceEquals(snapshot, _snapshot)) return false;
            foreach (var pair in new List<KeyValuePair<int, Entry>>(_entries))
                if (!IsCurrentEntry(snapshot, pair.Key, pair.Value)) return false;
            return ReferenceEquals(snapshot, _snapshot);
        }

        internal void RequestVisible(TSnapshot snapshot, IEnumerable<int> positions)
        {
            _verifyUi();
            if (_disposed || !ReferenceEquals(snapshot, _snapshot)) return;
            _visible.Clear();
            foreach (var position in positions)
                if (CanStart(snapshot, position) && !_visible.Contains(position)) _visible.Add(position);
            AbandonUndemanded();
            PumpLater();
        }

        internal Task<bool> MeasureAsync(TSnapshot snapshot, IReadOnlyList<int> positions, CancellationToken token)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requested = new int[positions.Count];
            for (int i = 0; i < positions.Count; i++) requested[i] = positions[i];
            _postUi(() =>
            {
                _verifyUi();
                if (_disposed || !ReferenceEquals(snapshot, _snapshot) || token.IsCancellationRequested)
                { completion.TrySetResult(false); return; }
                var pending = new List<int>();
                var seen = new HashSet<int>();
                foreach (int position in requested)
                {
                    // A negative position is always invalid; the supplied predicate
                    // rejects positions beyond the snapshot before indexing it.
                    if (!_validPosition(snapshot, position))
                    { completion.TrySetResult(false); return; }
                    if (_needsMeasurement(snapshot, position) && !_sourceCurrent(snapshot, position))
                    { completion.TrySetResult(false); return; }
                    if (_needsMeasurement(snapshot, position) && CanStart(snapshot, position) && seen.Add(position)) pending.Add(position);
                }
                if (pending.Count == 0) { completion.TrySetResult(!token.IsCancellationRequested); return; }
                var batch = new Batch(snapshot, pending, token, completion);
                _batches.Add(batch);
                // Register may invoke synchronously. The cancellation callback only
                // posts and never waits for UI dispatch or registration disposal.
                var registration = token.Register(() => _postCancellation(() =>
                {
                    _verifyUi();
                    CompleteBatch(batch, false);
                    AbandonUndemanded();
                    PumpLater();
                }));
                batch.Registration = registration;
                if (!_batches.Contains(batch)) registration.Dispose();
                PumpLater();
            });
            return completion.Task;
        }

        private bool CanStart(TSnapshot snapshot, int position) =>
            _canMeasure(snapshot, position) && _sourceCurrent(snapshot, position) && !Has(snapshot, position);

        private int NextPosition()
        {
            if (_snapshot == null) return -1;
            foreach (var batch in _batches)
                if (!batch.Token.IsCancellationRequested && batch.Positions.Count > 0) return batch.Positions.Peek();
            foreach (int position in _visible) if (CanStart(_snapshot, position)) return position;
            return -1;
        }

        private void FinishBatches()
        {
            for (int i = _batches.Count - 1; i >= 0; i--)
            {
                var batch = _batches[i];
                if (batch.Token.IsCancellationRequested || !ReferenceEquals(batch.Snapshot, _snapshot)) CompleteBatch(batch, false);
                else
                {
                    while (batch.Positions.Count > 0 && Has(batch.Snapshot, batch.Positions.Peek()))
                        batch.Pending.Remove(batch.Positions.Dequeue());
                    if (batch.Positions.Count == 0) CompleteBatch(batch, true);
                    else if (!_sourceCurrent(batch.Snapshot, batch.Positions.Peek())) CompleteBatch(batch, false);
                }
            }
        }

        private void CompleteBatch(Batch batch, bool success)
        {
            if (!_batches.Remove(batch)) return;
            batch.Registration.Dispose();
            batch.Completion.TrySetResult(success && !_disposed && !batch.Token.IsCancellationRequested && ReferenceEquals(batch.Snapshot, _snapshot));
        }
        private void CancelBatches()
        {
            while (_batches.Count != 0) CompleteBatch(_batches[_batches.Count - 1], false);
        }

        private void PumpLater(bool continuing = false)
        {
            FinishBatches();
            if (_disposed || NextPosition() < 0) { StopCapacityWait(); return; }
            if (_running != null || _preparingDepth != 0 || _scheduled || _capacityWait != null) return;
            _scheduled = true;
            int delay = !continuing && _batches.Count == 0 ? 40 : 0;
            try
            {
                _delayOnUi(delay, () => { _verifyUi(); _scheduled = false; Pump(); });
            }
            catch (Exception) { _scheduled = false; FailPending(); }
        }

        private void Pump()
        {
            FinishBatches();
            if (_disposed || _running != null || _preparingDepth != 0 || _snapshot == null) return;
            var snapshot = _snapshot;
            int position = NextPosition();
            if (position < 0) return;
            var entry = new Entry(position);
            _entries[position] = entry;
            // Preparation can call installed plug-in code while duplicating. Hold
            // the worker slot even if that code reenters the host message loop.
            _running = entry;
            try
            {
                _preparingDepth++;
                try { entry.Work = _prepare(position); }
                finally { _preparingDepth--; }
            }
            catch (MeasurementCapacityException capacity)
            {
                if (ReferenceEquals(_running, entry)) _running = null;
                RemoveEntry(position, entry);
                if (ReferenceEquals(_snapshot, snapshot) && IsDemanded(entry)) WaitForCapacity(capacity.Released);
                else PumpLater(true);
                return;
            }
            catch (Exception) { entry.Work = null; }
            bool current = ReferenceEquals(_snapshot, snapshot) && _entries.TryGetValue(position, out var existing) &&
                ReferenceEquals(existing, entry) && IsDemanded(entry) && _sourceCurrent(snapshot, position);
            if (!current || !ReferenceEquals(_snapshot, snapshot) || !_entries.TryGetValue(position, out existing) ||
                !ReferenceEquals(existing, entry) || !IsDemanded(entry))
            {
                entry.Work?.TryAbandon();
                if (ReferenceEquals(_running, entry)) _running = null;
                RemoveEntry(position, entry);
                PumpLater(true);
                return;
            }
            if (entry.Work == null)
            {
                _running = null;
                entry.Complete = true;
                _visible.Remove(position);
                _changed();
                PumpLater(true);
                return;
            }
            var witness = entry.Work.Witness;
            if (witness != null && _witnessCount < MaxRetainedWitnesses && witness.ByteCount <= MaxRetainedWitnessBytes - _witnessBytes &&
                MeasurementRetentionBudget.TryAcquire(witness.ByteCount))
            { entry.Witness = witness; _witnessBytes += witness.ByteCount; _witnessCount++; }
            _changed();
            if (!IsCurrentEntry(snapshot, position, entry) || !IsDemanded(entry))
            {
                entry.Work.TryAbandon();
                if (ReferenceEquals(_running, entry)) _running = null;
                RemoveEntry(position, entry);
                PumpLater(true);
                return;
            }
            try
            {
                if (!_submitWorker(() =>
                {
                    TResult? value = null;
                    bool claimed = false;
                    try { claimed = entry.Work.TryRun(out value); }
                    catch (Exception) { claimed = true; }
                    _postUi(() => WorkerFinished(entry, claimed, value));
                })) throw new InvalidOperationException("Worker rejected measurement");
            }
            catch (Exception)
            {
                entry.Work.TryAbandon();
                WorkerFinished(entry, true, null);
            }
        }

        private void WorkerFinished(Entry entry, bool claimed, TResult? value)
        {
            _verifyUi();
            if (!ReferenceEquals(_running, entry)) return;
            _running = null;
            entry.Work = null; // release unretained proof bytes as soon as the worker ends
            if (!_disposed && _snapshot != null && _entries.TryGetValue(entry.Position, out var current) && ReferenceEquals(current, entry))
            {
                var snapshot = _snapshot;
                if (!_sourceCurrent(snapshot, entry.Position)) RemoveEntry(entry.Position, entry);
                else if (!ReferenceEquals(snapshot, _snapshot) || !_entries.TryGetValue(entry.Position, out current) || !ReferenceEquals(current, entry)) { }
                else if (claimed) { entry.Value = value; entry.Complete = true; _visible.Remove(entry.Position); _changed(); }
                else RemoveEntry(entry.Position, entry);
            }
            PumpLater(true);
        }

        private void WaitForCapacity(Task released)
        {
            StopCapacityWait();
            var wait = new CancellationTokenSource();
            _capacityWait = wait;
            _ = ResumeWhenCapacityReleased(released, wait);
        }

        private async Task ResumeWhenCapacityReleased(Task released, CancellationTokenSource wait)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (wait.Token.Register(() => canceled.TrySetResult(true)))
            {
                if (await Task.WhenAny(released, canceled.Task).ConfigureAwait(false) != released || wait.IsCancellationRequested) return;
                try
                {
                    _postUi(() =>
                    {
                        _verifyUi();
                        if (!ReferenceEquals(_capacityWait, wait)) return;
                        _capacityWait = null;
                        wait.Dispose();
                        PumpLater(true);
                    });
                }
                catch (Exception) { /* A disposed host cannot dispatch a retry. */ }
            }
        }

        private void StopCapacityWait()
        {
            var wait = _capacityWait;
            _capacityWait = null;
            if (wait == null) return;
            wait.Cancel();
            wait.Dispose();
        }

        private void AbandonUndemanded()
        {
            var entry = _running;
            if (entry?.Work == null) return;
            if (!IsDemanded(entry) && TryAbandon(entry)) RemoveEntry(entry.Position, entry);
        }

        private bool IsDemanded(Entry entry) => _snapshot != null && (_visible.Contains(entry.Position) ||
            _batches.Exists(batch => ReferenceEquals(batch.Snapshot, _snapshot) && !batch.Token.IsCancellationRequested && batch.Pending.Contains(entry.Position)));

        // Only a successful atomic abandonment proves that no worker claimed
        // this copy. Its accepted callback may arrive much later and is stale.
        private bool TryAbandon(Entry entry)
        {
            if (entry.Work?.TryAbandon() != true) return false;
            if (ReferenceEquals(_running, entry)) _running = null;
            return true;
        }

        private void RemoveEntry(int position, Entry entry)
        {
            if (!_entries.TryGetValue(position, out var current) || !ReferenceEquals(current, entry)) return;
            _entries.Remove(position);
            if (entry.Witness != null)
            {
                _witnessBytes -= entry.Witness.ByteCount; _witnessCount--;
                MeasurementRetentionBudget.Release(entry.Witness.ByteCount);
                entry.Witness = null;
            }
            entry.Value = null;
            TryAbandon(entry);
        }
        private void ClearEntries()
        {
            foreach (var entry in new List<Entry>(_entries.Values)) RemoveEntry(entry.Position, entry);
        }
        private void FailPending()
        {
            while (_batches.Count != 0) CompleteBatch(_batches[_batches.Count - 1], false);
            _visible.Clear();
        }
        public void Dispose()
        {
            _verifyUi();
            if (_disposed) return;
            _disposed = true;
            StopCapacityWait();
            CancelBatches(); _visible.Clear(); ClearEntries(); _snapshot = null;
        }

        private sealed class Entry
        {
            internal Entry(int position) { Position = position; }
            internal readonly int Position;
            internal PreparedMeasurement<TResult>? Work;
            internal MeasurementWitness? Witness;
            internal TResult? Value;
            internal bool Complete;
        }
        private sealed class Batch
        {
            internal Batch(TSnapshot snapshot, List<int> positions, CancellationToken token, TaskCompletionSource<bool> completion)
            { Snapshot = snapshot; Positions = new Queue<int>(positions); Pending = new HashSet<int>(positions); Token = token; Completion = completion; }
            internal readonly TSnapshot Snapshot;
            internal readonly Queue<int> Positions;
            internal readonly HashSet<int> Pending;
            internal readonly CancellationToken Token;
            internal readonly TaskCompletionSource<bool> Completion;
            internal CancellationTokenRegistration Registration;
        }
    }
}
