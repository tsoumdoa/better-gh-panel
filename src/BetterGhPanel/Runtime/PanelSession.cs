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
        private static readonly PanelQueryResult EmptyResult =
            PanelQueryEngine.Execute(PanelSnapshot.Empty, new PanelQueryOptions(), CancellationToken.None);
        private readonly Action _invalidate;
        private CancellationTokenSource? _queryCancellation;
        private PanelQueryOptions _options = new PanelQueryOptions();
        private Func<int>? _decimalPlacesResolver;
        private Func<bool>? _createSetResolver;
        private Func<int, Func<PanelGeometryInfo?>?>? _geometryDetailResolver;
        private Func<int, PreparedMeasurement<PanelGeometryInfo>?>? _geometryPreparationResolver;
        private readonly GeometryMeasurementQueue<PanelSnapshot, PanelGeometryInfo> _geometry;
        private bool _awaitingSnapshot, _inputSuspended, _measurementNotifyScheduled;
        private System.Windows.Forms.Timer? _busyAnimation;
        private long _request;
        private bool _disposed;

        public PanelSession(Action invalidate)
        {
            _invalidate = invalidate;
            _geometry = new GeometryMeasurementQueue<PanelSnapshot, PanelGeometryInfo>(
                action => RhinoApp.InvokeOnUiThread(action),
                action => { _ = Task.Run(() => RhinoApp.InvokeOnUiThread(action)); },
                (delay, action) => { _ = Task.Run(async () =>
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    RhinoApp.InvokeOnUiThread(action);
                }); },
                action => { _ = Task.Run(action); return true; }, () => { },
                (snapshot, position) => position >= 0 && position < snapshot.Rows.Count,
                (snapshot, position) => position >= 0 && position < snapshot.Rows.Count &&
                    snapshot.Rows[position].IsValid && snapshot.Rows[position].GeometryInfo?.CanMeasure == true,
                (snapshot, position) => position >= 0 && position < snapshot.Rows.Count &&
                    snapshot.Rows[position].GeometryInfo?.Value == null,
                PrepareGeometry, NotifyMeasurement);
            Snapshot = PanelSnapshot.Empty;
            Result = EmptyResult;
        }

        public PanelSnapshot Snapshot { get; private set; }
        public PanelQueryResult Result { get; private set; }
        public PanelQueryOptions Options => _options.Clone();
        public bool IsBusy { get; private set; }
        public bool HasCurrentResult => !_disposed && !_awaitingSnapshot && !IsBusy && Error == null && ReferenceEquals(Result.Snapshot, Snapshot);
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
            Snapshot = snapshot ?? PanelSnapshot.Empty;
            _awaitingSnapshot = false;
            _inputSuspended = false;
            _geometry.Attach(Snapshot, canRetainMeasurement != null, canRetainMeasurement);
            QueueQuery();
        }

        internal void InvalidateInput(bool retainMeasurements, bool suspended = false)
        {
            if (_disposed) return;
            ++_request;
            _queryCancellation?.Cancel();
            _geometry.Suspend(retainMeasurements);
            Snapshot = PanelSnapshot.Empty;
            Result = EmptyResult;
            _awaitingSnapshot = true;
            _inputSuspended = suspended;
            IsBusy = !suspended;
            _busyAnimation?.Stop();
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

        internal void SetDecimalPlacesResolver(Func<int> resolver) => _decimalPlacesResolver = resolver;
        internal void SetCreateSetResolver(Func<bool> resolver) => _createSetResolver = resolver;

        // Legacy delegate seam is used by the host fixtures. It deliberately has no
        // retention proof; production uses the owned preparation provider below.
        internal void SetGeometryDetailResolver(Func<int, Func<PanelGeometryInfo?>?> resolver)
        { _geometryDetailResolver = resolver; _geometryPreparationResolver = null; }
        internal void SetGeometryPreparationResolver(Func<int, PreparedMeasurement<PanelGeometryInfo>?> resolver)
        { _geometryPreparationResolver = resolver; _geometryDetailResolver = null; }

        private PreparedMeasurement<PanelGeometryInfo>? PrepareGeometry(int position)
        {
            if (_geometryPreparationResolver != null)
            {
                PreparedMeasurement<PanelGeometryInfo>? prepared;
                try { prepared = _geometryPreparationResolver(position); }
                catch (Exception) { prepared = null; }
                if (prepared != null) return prepared;
                var summary = position >= 0 && position < Snapshot.Rows.Count ? Snapshot.Rows[position].GeometryInfo : null;
                if (summary == null) return null;
                var unavailable = new PanelGeometryInfo(summary.Description, power: summary.Power, units: summary.Units,
                    metricName: summary.MetricName, metricLabel: summary.MetricLabel,
                    details: summary.Details + "\nMeasurement unavailable: no verified, bounded geometry snapshot.",
                    canMeasure: summary.CanMeasure, measurementUnavailable: true);
                return new PreparedMeasurement<PanelGeometryInfo>(() => unavailable, null, null);
            }
            var operation = _geometryDetailResolver?.Invoke(position);
            return operation == null ? null : new PreparedMeasurement<PanelGeometryInfo>(operation, null, null);
        }

        internal PanelGeometryInfo? GetGeometryDetails(PanelSnapshot snapshot, int sourcePosition) => _geometry.Get(snapshot, sourcePosition);
        internal bool HasGeometryMeasurement(PanelSnapshot snapshot, int sourcePosition) => _geometry.Has(snapshot, sourcePosition);
        internal bool IsGeometryPending(PanelSnapshot snapshot, int sourcePosition) => _geometry.Pending(snapshot, sourcePosition);
        internal bool CanMeasureGeometry(PanelSnapshot snapshot, int sourcePosition) => !_disposed && ReferenceEquals(snapshot, Snapshot) &&
            sourcePosition >= 0 && sourcePosition < snapshot.Rows.Count && snapshot.Rows[sourcePosition].IsValid &&
            snapshot.Rows[sourcePosition].GeometryInfo?.CanMeasure == true && !HasGeometryMeasurement(snapshot, sourcePosition);
        internal void RequestGeometryMeasurements(PanelSnapshot snapshot, IEnumerable<int> visiblePositions) => _geometry.RequestVisible(snapshot, visiblePositions);
        internal Task<bool> MeasureGeometryAsync(PanelSnapshot snapshot, IReadOnlyList<int> positions, CancellationToken cancellationToken) =>
            _geometry.MeasureAsync(snapshot, positions, cancellationToken);

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

        public void UpdateQuery(PanelQueryOptions options)
        {
            if (_disposed) return;
            var next = (options ?? new PanelQueryOptions()).Clone();
            if (next.SearchMode < PanelSearchMode.Value) next.DisplayFilterValue = false;
            if (next.DisplayFilterValue) next.Search = string.Empty;
            // A component owns its formatting setting. Direct query updates and
            // ResetView cannot silently revert the filter's presentation precision.
            if (_decimalPlacesResolver != null) next.DecimalPlaces = _decimalPlacesResolver();
            if (_createSetResolver != null) next.CreateSet = _createSetResolver();
            next.DecimalPlaces = Math.Max(PanelUnits.AutomaticDecimals, Math.Min(PanelUnits.MaximumDecimals, next.DecimalPlaces));
            // Canonical paths keep direct input, saved options and menu checkmarks
            // consistent. Malformed input stays in the query so it reports the same
            // error state as any other invalid filter, without throwing from UI edits.
            try { next.BranchPath = PanelQueryEngine.NormalizeBranchPath(next.BranchPath); }
            catch (ArgumentException) { }
            var changed = !PanelQueryPolicy.SameQuery(_options, next);
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
            bool measureGeometry = snapshot.HasMeasurableGeometry && PanelQueryEngine.RequiresGeometryMeasurements(options);
            var request = ++_request;
            IsBusy = !_inputSuspended;
            Error = null;
            _geometry.RequestVisible(snapshot, Array.Empty<int>());
            if (_awaitingSnapshot) { Notify(); return; }
            PanelQueryWork? work = null;
            // Regex and native calls cannot yield midway through one operation.
            // All other queries get the same time budget regardless of tree size.
            if (options.SearchMode != PanelSearchMode.Regex && !measureGeometry)
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
                    if (measureGeometry)
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
                foreach (var entry in _geometry.Completed(snapshot))
                {
                    var geometry = entry.Value ?? snapshot.Rows[entry.Key].GeometryInfo?.WithUnavailableMeasurement();
                    if (geometry != null) values.Add(entry.Key, geometry);
                }
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
            _geometry.Dispose();
            ++_request;
            _queryCancellation?.Cancel();
            _queryCancellation?.Dispose();
            _queryCancellation = null;
            QueryChanged = null;
            ResultChanged = null;
            _decimalPlacesResolver = null;
            _geometryDetailResolver = null;
            _geometryPreparationResolver = null;
            Snapshot = PanelSnapshot.Empty;
            Result = EmptyResult;
        }
    }
}
