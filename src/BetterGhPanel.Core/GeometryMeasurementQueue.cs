using System;
using System.Collections.Generic;
using System.Collections;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGhPanel.Core
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
        private readonly GeometryMeasurementScheduler? _scheduler;
        private readonly int _priority;
        private readonly bool _retainWitnesses;
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

        // Counts actual cache slot reads (successful lookups, snapshot copies and
        // enumeration), including work outside SourcesCurrent. Disabled normally.
        internal bool DiagnosticsForTesting { get; set; }
        internal long EntryVisitsForTesting { get; private set; }
        internal long EntryCopiesForTesting { get; private set; }
        internal long SourceChecksForTesting { get; private set; }
        internal long SourceScansForTesting { get; private set; }
        internal int EntryCountForTesting => _entries.Count;
        internal void ResetDiagnosticsForTesting()
            => EntryVisitsForTesting = EntryCopiesForTesting = SourceChecksForTesting = SourceScansForTesting = 0;

        // An O(1), callback-free ownership fence, deliberately not a source proof.
        internal bool IsAttachedTo(TSnapshot snapshot) => !_disposed && ReferenceEquals(snapshot, _snapshot);

        private bool TryGetEntry(int position, out Entry entry)
        {
            bool found = _entries.TryGetValue(position, out entry!);
            if (found) CountEntryVisit();
            return found;
        }
        private void CountEntryVisit() { if (DiagnosticsForTesting) EntryVisitsForTesting++; }
        private List<KeyValuePair<int, Entry>> CopyEntries()
        {
            CountEntryCopy();
            return new List<KeyValuePair<int, Entry>>(_entries);
        }
        private void CountEntryCopy()
        {
            if (DiagnosticsForTesting) { EntryCopiesForTesting += _entries.Count; EntryVisitsForTesting += _entries.Count; }
        }
        private bool SourceCurrent(TSnapshot snapshot, int position)
        {
            if (DiagnosticsForTesting) SourceChecksForTesting++;
            return _sourceCurrent(snapshot, position);
        }

        internal GeometryMeasurementQueue(Action<Action> postUi, Action<Action> postCancellation,
            Action<int, Action> delayOnUi, Func<Action, bool> submitWorker, Action verifyUi,
            Func<TSnapshot, int, bool> validPosition, Func<TSnapshot, int, bool> canMeasure, Func<TSnapshot, int, bool> needsMeasurement,
            Func<int, PreparedMeasurement<TResult>?> prepare, Action changed, Func<TSnapshot, int, bool>? sourceCurrent = null,
            Func<int, PreparedMeasurement<TResult>?>? prepareForRetention = null,
            GeometryMeasurementScheduler? scheduler = null, int priority = 0, bool retainWitnesses = true)
        {
            _scheduler = scheduler; _priority = priority; _retainWitnesses = retainWitnesses;
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
            _scheduler?.Cancel(this);
            StopCapacityWait();
            CancelBatches();
            _visible.Clear();
            _snapshot = snapshot;
            CountEntryCopy();
            foreach (var position in new List<int>(_entries.Keys))
            {
                CountEntryVisit();
                if (!ReferenceEquals(_snapshot, snapshot) || _disposed) { PumpLater(); return; }
                if (!TryGetEntry(position, out var entry)) continue;
                bool match = false;
                try
                {
                    if (_retainWitnesses && retain && entry.Witness != null)
                    {
                        var admission = _scheduler?.TryAcquire(this, snapshot);
                        try
                        {
                            if ((_scheduler == null || admission != null) && _canMeasure(snapshot, position) && (allowed?.Invoke(position) ?? true))
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
                        finally { admission?.Dispose(); }
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
            _scheduler?.Cancel(this);
            StopCapacityWait();
            CancelBatches();
            _visible.Clear();
            _snapshot = null;
            if (!retain) ClearEntries();
            AbandonUndemanded();
        }

        private bool IsCurrentEntry(TSnapshot snapshot, int position, Entry entry) =>
            ReferenceEquals(snapshot, _snapshot) && TryGetEntry(position, out var current) && ReferenceEquals(current, entry) &&
            SourceCurrent(snapshot, position) && ReferenceEquals(snapshot, _snapshot) &&
            TryGetEntry(position, out current) && ReferenceEquals(current, entry);

        internal TResult? Get(TSnapshot snapshot, int position) =>
            ReferenceEquals(snapshot, _snapshot) && TryGetEntry(position, out var entry) && IsCurrentEntry(snapshot, position, entry) ? entry.Value : null;
        internal bool Has(TSnapshot snapshot, int position) =>
            ReferenceEquals(snapshot, _snapshot) && TryGetEntry(position, out var entry) && entry.Complete && IsCurrentEntry(snapshot, position, entry);
        internal bool Pending(TSnapshot snapshot, int position) => ReferenceEquals(snapshot, _snapshot) &&
            ((TryGetEntry(position, out var entry) && !entry.Complete) || _visible.Contains(position) ||
             _batches.Exists(batch => !batch.Token.IsCancellationRequested && batch.Pending.Contains(position)));
        internal IEnumerable<KeyValuePair<int, TResult?>> Completed(TSnapshot snapshot)
        {
            if (!ReferenceEquals(snapshot, _snapshot)) yield break;
            foreach (var pair in CopyEntries())
            {
                CountEntryVisit();
                if (pair.Value.Complete && IsCurrentEntry(snapshot, pair.Key, pair.Value)) yield return new KeyValuePair<int, TResult?>(pair.Key, pair.Value.Value);
            }
        }

        internal bool SourcesCurrent(TSnapshot snapshot)
        {
            if (DiagnosticsForTesting) SourceScansForTesting++;
            if (!ReferenceEquals(snapshot, _snapshot)) return false;
            foreach (var pair in CopyEntries())
            {
                CountEntryVisit();
                if (!IsCurrentEntry(snapshot, pair.Key, pair.Value)) return false;
            }
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

        internal const int DemandChunkSize = 256;

        // Position lists are immutable request inputs; the queue retains them only
        // until registration ends. Whole-column callers use the allocation-free range.
        internal Task<bool> MeasureAllAsync(TSnapshot snapshot, int count, CancellationToken token) =>
            MeasureAsync(snapshot, new SequentialPositions(count), token);

        internal Task<bool> MeasureAsync(TSnapshot snapshot, IReadOnlyList<int> positions, CancellationToken token)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _postUi(() =>
            {
                _verifyUi();
                if (_disposed || !ReferenceEquals(snapshot, _snapshot) || token.IsCancellationRequested)
                { completion.TrySetResult(false); return; }
                var batch = new Batch(snapshot, positions, token, completion);
                _batches.Add(batch);
                var registration = token.Register(() => _postCancellation(() =>
                {
                    _verifyUi();
                    CompleteBatch(batch, false);
                    AbandonUndemanded();
                    PumpLater();
                }));
                batch.Registration = registration;
                if (!_batches.Contains(batch)) registration.Dispose();
                RegisterDemand(batch);
            });
            return completion.Task;
        }

        private void RegisterDemand(Batch batch)
        {
            _verifyUi();
            bool Current() => !_disposed && !batch.Token.IsCancellationRequested &&
                ReferenceEquals(batch.Snapshot, _snapshot) && _batches.Contains(batch);
            if (!Current()) { CompleteBatch(batch, false); return; }
            int end = Math.Min(batch.Requested!.Count, batch.Next + DemandChunkSize);
            for (; batch.Next < end; batch.Next++)
            {
                if (!Current()) { CompleteBatch(batch, false); return; }
                int position = batch.Requested[batch.Next];
                if (!_validPosition(batch.Snapshot, position)) { CompleteBatch(batch, false); return; }
                if (!_needsMeasurement(batch.Snapshot, position)) continue;
                if (!SourceCurrent(batch.Snapshot, position) || !Current()) { CompleteBatch(batch, false); return; }
                // The source was just fenced; avoid CanStart/Has repeating the same
                // proof for every registered position. Admission checks it again.
                if (_canMeasure(batch.Snapshot, position) &&
                    (!TryGetEntry(position, out var entry) || !entry.Complete) && batch.Pending.Add(position))
                    batch.Positions.Enqueue(position);
            }
            if (!Current()) { CompleteBatch(batch, false); return; }
            if (batch.Next == batch.Requested!.Count) { batch.Requested = null; batch.Registered = true; }
            else _postCancellation(() => RegisterDemand(batch)); // asynchronous UI post, no timer floor
            PumpLater();
        }

        internal Task<IReadOnlyDictionary<int, TResult?>?> CaptureCompletedAsync(TSnapshot snapshot, int count, CancellationToken token)
        {
            var completion = new TaskCompletionSource<IReadOnlyDictionary<int, TResult?>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var values = new Dictionary<int, TResult?>();
            int next = 0;
            void Step()
            {
                _verifyUi();
                bool Current() => !_disposed && !token.IsCancellationRequested && ReferenceEquals(snapshot, _snapshot);
                if (!Current()) { completion.TrySetResult(null); return; }
                int end = Math.Min(count, next + DemandChunkSize);
                for (; next < end; next++)
                {
                    if (!Current()) { completion.TrySetResult(null); return; }
                    if (!TryGetEntry(next, out var entry) || !entry.Complete) continue;
                    if (!IsCurrentEntry(snapshot, next, entry) || !Current()) { completion.TrySetResult(null); return; }
                    values.Add(next, entry.Value);
                }
                if (next < count) _postCancellation(Step);
                else completion.TrySetResult(new ReadOnlyDictionary<int, TResult?>(values));
            }
            _postUi(Step);
            return completion.Task;
        }

        private sealed class SequentialPositions : IReadOnlyList<int>
        {
            internal SequentialPositions(int count) { Count = count; }
            public int Count { get; }
            public int this[int index] => index;
            public IEnumerator<int> GetEnumerator() { for (int i = 0; i < Count; i++) yield return i; }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private bool CanStart(TSnapshot snapshot, int position) =>
            _canMeasure(snapshot, position) && SourceCurrent(snapshot, position) && !Has(snapshot, position);

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
                    if (batch.Positions.Count == 0) { if (batch.Registered) CompleteBatch(batch, true); }
                    else if (!SourceCurrent(batch.Snapshot, batch.Positions.Peek())) CompleteBatch(batch, false);
                }
            }
        }

        private void CompleteBatch(Batch batch, bool success)
        {
            if (!_batches.Remove(batch)) return;
            batch.Registration.Dispose();
            batch.Requested = null; batch.Positions.Clear(); batch.Pending.Clear();
            batch.Completion.TrySetResult(success && !_disposed && !batch.Token.IsCancellationRequested && ReferenceEquals(batch.Snapshot, _snapshot));
        }
        private void CancelBatches()
        {
            while (_batches.Count != 0) CompleteBatch(_batches[_batches.Count - 1], false);
        }

        private void PumpLater(bool continuing = false)
        {
            FinishBatches();
            if (_disposed || NextPosition() < 0) { _scheduler?.Cancel(this); StopCapacityWait(); return; }
            if (_running != null || _preparingDepth != 0 || _scheduled || _capacityWait != null) return;
            if (continuing && _scheduler != null) { RequestAdmission(); return; }
            _scheduled = true;
            int delay = !continuing && _batches.Count == 0 ? 40 : 0;
            try
            {
                _delayOnUi(delay, () => { _verifyUi(); _scheduled = false; RequestAdmission(); });
            }
            catch (Exception) { _scheduled = false; FailPending(); }
        }

        private void RequestAdmission()
        {
            var snapshot = _snapshot;
            if (_scheduler == null) { Pump(); return; }
            if (snapshot == null || _disposed) { _scheduler.Cancel(this); return; }
            _scheduler.RequestWork(this, snapshot, _priority,
                () => !_disposed && ReferenceEquals(snapshot, _snapshot) && _running == null &&
                    _preparingDepth == 0 && _capacityWait == null && NextPosition() >= 0,
                admission => Pump(admission));
        }

        // Cancellation callbacks run on the UI and can prune demand immediately,
        // without waiting for the posted registration callback or a running worker.
        internal void PruneDemand()
        {
            _verifyUi();
            AbandonUndemanded();
            PumpLater(true);
        }

        internal void CancelRequests()
        {
            _verifyUi();
            CancelBatches(); _visible.Clear();
            AbandonUndemanded();
            _scheduler?.Cancel(this);
            StopCapacityWait();
        }

        private void Pump(GeometryMeasurementScheduler.Lease? admission = null)
        {
            bool submitted = false;
            try
            {
                FinishBatches();
                if (_disposed || _running != null || _preparingDepth != 0 || _snapshot == null ||
                    admission != null && (admission.Canceled || !ReferenceEquals(admission.Owner, this) || !ReferenceEquals(admission.Snapshot, _snapshot))) return;
                var snapshot = _snapshot;
                int position = NextPosition();
                if (position < 0) return;
                var entry = new Entry(position) { Admission = admission };
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
                bool current = ReferenceEquals(_snapshot, snapshot) && TryGetEntry(position, out var existing) &&
                    ReferenceEquals(existing, entry) && IsDemanded(entry) && SourceCurrent(snapshot, position);
                if (!current || !ReferenceEquals(_snapshot, snapshot) || !TryGetEntry(position, out existing) ||
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
                if (_retainWitnesses && witness != null && _witnessCount < MaxRetainedWitnesses && witness.ByteCount <= MaxRetainedWitnessBytes - _witnessBytes &&
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
                    submitted = true;
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
            finally { if (!submitted) admission?.Dispose(); }
        }

        private void WorkerFinished(Entry entry, bool claimed, TResult? value)
        {
            _verifyUi();
            try
            {
                if (!ReferenceEquals(_running, entry)) return;
                _running = null;
                entry.Work = null; // release unretained proof bytes as soon as the worker ends
                if (!_disposed && _snapshot != null && TryGetEntry(entry.Position, out var current) && ReferenceEquals(current, entry))
                {
                    var snapshot = _snapshot;
                    if (!SourceCurrent(snapshot, entry.Position)) RemoveEntry(entry.Position, entry);
                    else if (!ReferenceEquals(snapshot, _snapshot) || !TryGetEntry(entry.Position, out current) || !ReferenceEquals(current, entry)) { }
                    else if (claimed) { entry.Value = value; entry.Complete = true; _visible.Remove(entry.Position); _changed(); }
                    else RemoveEntry(entry.Position, entry);
                }
                PumpLater(true);
            }
            finally { entry.Admission?.Dispose(); entry.Admission = null; }
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
            if (!TryGetEntry(position, out var current) || !ReferenceEquals(current, entry)) return;
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
            CountEntryCopy();
            foreach (var entry in new List<Entry>(_entries.Values)) { CountEntryVisit(); RemoveEntry(entry.Position, entry); }
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
            _scheduler?.Cancel(this);
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
            internal GeometryMeasurementScheduler.Lease? Admission;
        }
        private sealed class Batch
        {
            internal Batch(TSnapshot snapshot, IReadOnlyList<int> positions, CancellationToken token, TaskCompletionSource<bool> completion)
            { Snapshot = snapshot; Requested = positions; Token = token; Completion = completion; }
            internal IReadOnlyList<int>? Requested;
            internal int Next;
            internal bool Registered;
            internal readonly TSnapshot Snapshot;
            internal readonly Queue<int> Positions = new Queue<int>();
            internal readonly HashSet<int> Pending = new HashSet<int>();
            internal readonly CancellationToken Token;
            internal readonly TaskCompletionSource<bool> Completion;
            internal CancellationTokenRegistration Registration;
        }
    }
}
