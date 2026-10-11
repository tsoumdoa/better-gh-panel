using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Rhino;

namespace BetterGhPanel.Runtime
{
    /// <summary>Owns one component's view, background queries and lazy geometry measurements.</summary>
    public sealed class PanelSession : IDisposable
    {
        // Allow queries 120 ms to finish on the UI thread before handing off.
        // This is a soft deadline checked between operations.
        private static readonly TimeSpan ImmediateQueryBudget = TimeSpan.FromMilliseconds(120);
        private readonly Action _invalidate;
        private CancellationTokenSource? _queryCancellation;
        private PanelQueryOptions _options = new PanelQueryOptions();
        private Func<int, string?>? _detailResolver;
        private Func<int, Func<PanelGeometryInfo?>?>? _geometryDetailResolver;
        private readonly Dictionary<int, GeometryMeasurement> _geometryDetails = new Dictionary<int, GeometryMeasurement>();
        private readonly List<int> _visibleGeometry = new List<int>();
        private readonly List<GeometryBatch> _geometryBatches = new List<GeometryBatch>();
        private bool _geometryScheduled, _geometryRunning;
        private bool _awaitingSnapshot, _measurementNotifyScheduled;
        private System.Windows.Forms.Timer? _busyAnimation;
        private long _request;
        private bool _disposed;

        public PanelSession(Action invalidate)
        {
            _invalidate = invalidate;
            Snapshot = PanelSnapshot.Empty;
            Result = PanelQueryEngine.Execute(Snapshot, _options, CancellationToken.None);
        }

        public PanelSnapshot Snapshot { get; private set; }
        public PanelQueryResult Result { get; private set; }
        public PanelQueryOptions Options => _options.Clone();
        public bool IsBusy { get; private set; }
        public bool HasCurrentResult => !_disposed && !IsBusy && Error == null && ReferenceEquals(Result.Snapshot, Snapshot);
        public string? Error { get; private set; }
        public event EventHandler? QueryChanged;
        public event EventHandler? ResultChanged;

        public void AttachSnapshot(PanelSnapshot snapshot)
            => AttachSnapshotCore(snapshot, null);

        internal void AttachConvertedSnapshot(PanelSnapshot snapshot, Func<int, bool> canRetainMeasurement)
            => AttachSnapshotCore(snapshot, canRetainMeasurement);

        private void AttachSnapshotCore(PanelSnapshot snapshot, Func<int, bool>? canRetainMeasurement)
        {
            if (_disposed) return;
            CancelGeometryBatches();
            Snapshot = snapshot ?? PanelSnapshot.Empty;
            _awaitingSnapshot = false;
            _visibleGeometry.Clear();
            if (canRetainMeasurement == null) _geometryDetails.Clear();
            else
                foreach (var position in new List<int>(_geometryDetails.Keys))
                    if (position >= Snapshot.Rows.Count || !Snapshot.Rows[position].IsValid || !canRetainMeasurement(position))
                        _geometryDetails.Remove(position);
            QueueQuery();
        }

        internal void InvalidateInput(bool retainMeasurements)
        {
            if (_disposed) return;
            ++_request;
            _queryCancellation?.Cancel();
            CancelGeometryBatches();
            _visibleGeometry.Clear();
            if (!retainMeasurements) _geometryDetails.Clear();
            Snapshot = PanelSnapshot.Empty;
            _awaitingSnapshot = true;
            IsBusy = true;
            Error = null;
            Notify();
        }

        internal void AnimateBusyState(bool visible)
        {
            if (_disposed) return;
            if (!visible || !IsBusy)
            {
                _busyAnimation?.Stop();
                return;
            }
            if (_busyAnimation == null)
            {
                _busyAnimation = new System.Windows.Forms.Timer { Interval = 80 };
                _busyAnimation.Tick += (_, __) => Notify();
            }
            _busyAnimation.Start();
        }

        public void SetDetailResolver(Func<int, string?> resolver) => _detailResolver = resolver;

        internal void SetGeometryDetailResolver(Func<int, Func<PanelGeometryInfo?>?> resolver) => _geometryDetailResolver = resolver;

        internal PanelGeometryInfo? GetGeometryDetails(PanelSnapshot snapshot, int sourcePosition) =>
            ReferenceEquals(snapshot, Snapshot) && _geometryDetails.TryGetValue(sourcePosition, out var measurement) ? measurement.Details : null;

        internal bool HasGeometryMeasurement(PanelSnapshot snapshot, int sourcePosition) =>
            ReferenceEquals(snapshot, Snapshot) && _geometryDetails.TryGetValue(sourcePosition, out var measurement) && measurement.Complete;

        internal bool IsGeometryPending(PanelSnapshot snapshot, int sourcePosition) => ReferenceEquals(snapshot, Snapshot) &&
            ((_geometryDetails.TryGetValue(sourcePosition, out var measurement) && !measurement.Complete) || _visibleGeometry.Contains(sourcePosition) ||
             (!HasGeometryMeasurement(snapshot, sourcePosition) &&
              _geometryBatches.Exists(batch => !batch.Cancellation.IsCancellationRequested && batch.Pending.Contains(sourcePosition))));

        internal bool CanMeasureGeometry(PanelSnapshot snapshot, int sourcePosition) => !_disposed && ReferenceEquals(snapshot, Snapshot) &&
            sourcePosition >= 0 && sourcePosition < snapshot.Rows.Count && snapshot.Rows[sourcePosition].IsValid &&
            snapshot.Rows[sourcePosition].GeometryInfo?.CanMeasure == true && !HasGeometryMeasurement(snapshot, sourcePosition);

        internal void RequestGeometryMeasurements(PanelSnapshot snapshot, IEnumerable<int> visiblePositions)
        {
            if (_disposed || !ReferenceEquals(snapshot, Snapshot)) return;
            // Replace the pending viewport, so scrolling never accumulates offscreen work.
            _visibleGeometry.Clear();
            foreach (var position in visiblePositions)
                if (CanMeasureGeometry(snapshot, position) && !_visibleGeometry.Contains(position)) _visibleGeometry.Add(position);
            QueueGeometryPump();
        }

        internal Task<bool> MeasureGeometryAsync(PanelSnapshot snapshot, IReadOnlyList<int> positions, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Capture the selection before dispatching; callers can change their selection while waiting.
            var requested = new int[positions.Count];
            for (int index = 0; index < requested.Length; index++) requested[index] = positions[index];
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (_disposed || _awaitingSnapshot || !ReferenceEquals(snapshot, Snapshot) || cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetResult(false);
                    return;
                }
                var pending = new List<int>();
                var seen = new HashSet<int>();
                foreach (var position in requested)
                {
                    if (position < 0 || position >= snapshot.Rows.Count)
                    {
                        completion.TrySetResult(false);
                        return;
                    }
                    var geometry = snapshot.Rows[position].GeometryInfo;
                    if (geometry?.Value == null && CanMeasureGeometry(snapshot, position) && seen.Add(position)) pending.Add(position);
                }
                if (pending.Count == 0)
                {
                    completion.TrySetResult(!cancellationToken.IsCancellationRequested);
                    return;
                }
                var batch = new GeometryBatch(snapshot, pending, cancellationToken, completion);
                _geometryBatches.Add(batch);
                // A token can be canceled from a worker. Keep session state on the UI
                // thread, and never block cancellation/registration disposal on UI dispatch.
                batch.Registration = cancellationToken.Register(() =>
                {
                    _ = Task.Run(() => RhinoApp.InvokeOnUiThread((Action)(() =>
                    {
                        CompleteGeometryBatch(batch, false);
                        QueueGeometryPump();
                    })));
                });
                QueueGeometryPump();
            }));
            return completion.Task;
        }

        private int NextGeometryPosition()
        {
            // Explicit copy and filter requests include offscreen rows and take priority over paint work.
            foreach (var batch in _geometryBatches)
                if (!batch.Cancellation.IsCancellationRequested && batch.Positions.Count > 0) return batch.Positions.Peek();
            foreach (var position in _visibleGeometry)
                if (CanMeasureGeometry(Snapshot, position)) return position;
            return -1;
        }

        private void CompleteGeometryBatches()
        {
            for (int index = _geometryBatches.Count - 1; index >= 0; index--)
            {
                var batch = _geometryBatches[index];
                if (batch.Cancellation.IsCancellationRequested || !ReferenceEquals(batch.Snapshot, Snapshot))
                    CompleteGeometryBatch(batch, false);
                else
                {
                    while (batch.Positions.Count > 0 && HasGeometryMeasurement(batch.Snapshot, batch.Positions.Peek()))
                        batch.Pending.Remove(batch.Positions.Dequeue());
                    if (batch.Positions.Count == 0) CompleteGeometryBatch(batch, true);
                }
            }
        }

        private void CompleteGeometryBatch(GeometryBatch batch, bool complete)
        {
            if (!_geometryBatches.Remove(batch)) return;
            batch.Registration.Dispose();
            batch.Completion.TrySetResult(complete && !_disposed && !batch.Cancellation.IsCancellationRequested && ReferenceEquals(batch.Snapshot, Snapshot));
        }

        private void CancelGeometryBatches()
        {
            while (_geometryBatches.Count > 0) CompleteGeometryBatch(_geometryBatches[_geometryBatches.Count - 1], false);
        }

        private void QueueGeometryPump(bool continuing = false)
        {
            CompleteGeometryBatches();
            if (_disposed || _geometryRunning || _geometryScheduled || NextGeometryPosition() < 0) return;
            _geometryScheduled = true;
            // Paint work waits for rapid scrolling to settle. Explicit selections still
            // yield before preparation, without adding a scrolling delay for every row.
            int delay = !continuing && _geometryBatches.Count == 0 ? 40 : 0;
            Task.Run(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    _geometryScheduled = false;
                    PumpGeometry();
                }));
            });
        }

        private void PumpGeometry()
        {
            CompleteGeometryBatches();
            if (_disposed || _geometryRunning) return;
            int position = NextGeometryPosition();
            if (position < 0) return;
            var measurement = new GeometryMeasurement();
            _geometryDetails[position] = measurement;
            Func<PanelGeometryInfo?>? compute;
            try { compute = _geometryDetailResolver?.Invoke(position); }
            catch (Exception) { compute = null; }
            if (compute == null)
            {
                measurement.Complete = true;
                _visibleGeometry.Remove(position);
                NotifyMeasurement();
                QueueGeometryPump(continuing: true);
                return;
            }
            _geometryRunning = true;
            NotifyMeasurement();
            // One operation per session bounds CPU and native-copy memory. It touches
            // only its owned geometry, never source goo or the Rhino document.
            Task.Run(() =>
            {
                PanelGeometryInfo? details;
                try { details = compute(); }
                catch (Exception) { details = null; }
                RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    _geometryRunning = false;
                    if (_disposed) return;
                    // Entry identity also preserves in-flight work across unit-only
                    // snapshots while rejecting new solutions and reordered sources.
                    if (_geometryDetails.TryGetValue(position, out var current) && ReferenceEquals(current, measurement))
                    {
                        measurement.Details = details;
                        measurement.Complete = true;
                        _visibleGeometry.Remove(position);
                        NotifyMeasurement();
                    }
                    QueueGeometryPump(continuing: true);
                }));
            });
        }

        private void NotifyMeasurement()
        {
            // Fast rows share a repaint instead of streaming two redraws per row.
            // Slow native calls still yield and expose their pending status.
            if (_measurementNotifyScheduled || _disposed) return;
            _measurementNotifyScheduled = true;
            Task.Run(async () =>
            {
                await Task.Delay(60).ConfigureAwait(false);
                RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    _measurementNotifyScheduled = false;
                    if (!_disposed && !IsBusy) Notify();
                }));
            });
        }

        private sealed class GeometryMeasurement
        {
            internal PanelGeometryInfo? Details;
            internal bool Complete;
        }

        private sealed class GeometryBatch
        {
            internal GeometryBatch(PanelSnapshot snapshot, List<int> positions, CancellationToken cancellation, TaskCompletionSource<bool> completion)
            {
                Snapshot = snapshot;
                Positions = new Queue<int>(positions);
                Pending = new HashSet<int>(positions);
                Cancellation = cancellation;
                Completion = completion;
            }

            internal readonly PanelSnapshot Snapshot;
            internal readonly Queue<int> Positions;
            internal readonly HashSet<int> Pending;
            internal readonly CancellationToken Cancellation;
            internal readonly TaskCompletionSource<bool> Completion;
            internal CancellationTokenRegistration Registration;
        }

        public string GetDisplayValue(PanelSnapshot snapshot, int sourcePosition)
        {
            if (sourcePosition < 0 || sourcePosition >= snapshot.Rows.Count) return string.Empty;
            var row = snapshot.Rows[sourcePosition];
            // Never resolve a saved address against another solution's live sources.
            if (!ReferenceEquals(snapshot, Snapshot)) return row.DisplayValue;
            try { return _detailResolver?.Invoke(sourcePosition) ?? row.DisplayValue; }
            catch (Exception ex) { return row.DisplayValue + "\nValue formatting failed: " + ex.Message; }
        }

        public void UpdateQuery(PanelQueryOptions options)
        {
            if (_disposed) return;
            var next = (options ?? new PanelQueryOptions()).Clone();
            var changed = _options.Search != next.Search || _options.TypeName != next.TypeName ||
                _options.BranchPath != next.BranchPath || _options.BranchPrefix != next.BranchPrefix ||
                _options.OnlyInvalid != next.OnlyInvalid || _options.SortMode != next.SortMode ||
                _options.SearchMode != next.SearchMode;
            _options = next;
            if (changed)
            {
                QueryChanged?.Invoke(this, EventArgs.Empty);
                QueueQuery();
            }
            else Notify(); // View resets can clear transient state even when options are unchanged.
        }

        public void ResetView() => UpdateQuery(new PanelQueryOptions());

        private void QueueQuery()
        {
            _queryCancellation?.Cancel();
            _queryCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _queryCancellation = cancellation;
            var token = cancellation.Token;
            var snapshot = Snapshot;
            var options = _options.Clone();
            var request = ++_request;
            IsBusy = true;
            Error = null;
            _visibleGeometry.Clear();
            if (_awaitingSnapshot) { Notify(); return; }
            PanelQueryWork? work = null;
            // Regex and native calls cannot yield midway through one operation.
            // All other queries get the same time budget regardless of tree size.
            if (options.SearchMode != PanelSearchMode.Regex && !PanelQueryEngine.RequiresGeometryMeasurements(options))
            {
                try
                {
                    work = PanelQueryEngine.Start(snapshot, options, token);
                    if (work.TryComplete(ImmediateQueryBudget))
                    {
                        PublishQuery(work.Result, null);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    work?.Dispose();
                    PublishQuery(null, ex.Message);
                    return;
                }
            }
            Notify();
            // Only plain immutable managed snapshot values cross this boundary.
            Task.Run(async () =>
            {
                try
                {
                    IReadOnlyDictionary<int, PanelGeometryInfo>? measurements = null;
                    if (PanelQueryEngine.RequiresGeometryMeasurements(options))
                    {
                        // Validate and narrow by branch/type before preparing any native
                        // geometry. Filters include offscreen rows through the same bounded
                        // worker used by visible rows and explicit copy requests.
                        var candidates = PanelQueryEngine.GeometryCandidates(snapshot, options, token);
                        if (!await MeasureGeometryAsync(snapshot, candidates, token).ConfigureAwait(false)) return;
                        measurements = await CaptureGeometryMeasurements(snapshot, request, token).ConfigureAwait(false);
                        if (measurements == null) return;
                    }
                    // Resume the same scan, statistics and sort state. Work already
                    // performed within the UI budget is never restarted.
                    var result = work?.Complete() ?? PanelQueryEngine.Execute(snapshot, options, token, measurements);
                    RhinoApp.InvokeOnUiThread((Action)(() =>
                    {
                        if (_disposed || request != _request || token.IsCancellationRequested) return;
                        PublishQuery(result, null);
                    }));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    RhinoApp.InvokeOnUiThread((Action)(() =>
                    {
                        if (_disposed || request != _request || token.IsCancellationRequested) return;
                        PublishQuery(null, ex.Message);
                    }));
                }
                finally { work?.Dispose(); }
            });
        }

        private void PublishQuery(PanelQueryResult? result, string? error)
        {
            if (result != null) Result = result;
            Error = error;
            IsBusy = false;
            _busyAnimation?.Stop();
            ResultChanged?.Invoke(this, EventArgs.Empty);
            Notify();
        }

        private Task<IReadOnlyDictionary<int, PanelGeometryInfo>?> CaptureGeometryMeasurements(
            PanelSnapshot snapshot, long request, CancellationToken token)
        {
            var completion = new TaskCompletionSource<IReadOnlyDictionary<int, PanelGeometryInfo>?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (_disposed || request != _request || token.IsCancellationRequested || !ReferenceEquals(snapshot, Snapshot))
                {
                    completion.TrySetResult(null);
                    return;
                }
                // Cache entries change only on the UI thread. The worker receives its
                // own immutable projection, never the live cache or live Rhino objects.
                var values = new Dictionary<int, PanelGeometryInfo>();
                foreach (var entry in _geometryDetails)
                    if (entry.Value.Complete && entry.Value.Details != null) values.Add(entry.Key, entry.Value.Details);
                completion.TrySetResult(new ReadOnlyDictionary<int, PanelGeometryInfo>(values));
            }));
            return completion.Task;
        }

        private void Notify()
        {
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (_disposed) return;
                _invalidate();
            }));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _busyAnimation?.Dispose();
            _busyAnimation = null;
            CancelGeometryBatches();
            ++_request;
            _queryCancellation?.Cancel();
            _queryCancellation?.Dispose();
            _queryCancellation = null;
            QueryChanged = null;
            ResultChanged = null;
            _detailResolver = null;
            _geometryDetailResolver = null;
            _geometryDetails.Clear();
            _visibleGeometry.Clear();
            Snapshot = PanelSnapshot.Empty;
        }
    }
}
