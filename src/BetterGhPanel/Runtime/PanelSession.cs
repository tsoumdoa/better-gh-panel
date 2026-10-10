using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Rhino;

namespace BetterGhPanel.Runtime
{
    /// <summary>Owns one component's view, background queries and lazy geometry measurements.</summary>
    public sealed class PanelSession : IDisposable
    {
        private readonly Action _invalidate;
        private CancellationTokenSource? _queryCancellation;
        private PanelQueryOptions _options = new PanelQueryOptions();
        private Func<int, string?>? _detailResolver;
        private Func<int, Func<PanelGeometryInfo?>?>? _geometryDetailResolver;
        private readonly Dictionary<int, GeometryMeasurement> _geometryDetails = new Dictionary<int, GeometryMeasurement>();
        private readonly List<int> _visibleGeometry = new List<int>();
        private bool _geometryScheduled, _geometryRunning;
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
        public string? Error { get; private set; }
        public event EventHandler? QueryChanged;

        public void AttachSnapshot(PanelSnapshot snapshot)
            => AttachSnapshotCore(snapshot, null);

        internal void AttachConvertedSnapshot(PanelSnapshot snapshot, Func<int, bool> canRetainMeasurement)
            => AttachSnapshotCore(snapshot, canRetainMeasurement);

        private void AttachSnapshotCore(PanelSnapshot snapshot, Func<int, bool>? canRetainMeasurement)
        {
            if (_disposed) return;
            Snapshot = snapshot ?? PanelSnapshot.Empty;
            _visibleGeometry.Clear();
            if (canRetainMeasurement == null) _geometryDetails.Clear();
            else
                foreach (var position in new List<int>(_geometryDetails.Keys))
                    if (position >= Snapshot.Rows.Count || !Snapshot.Rows[position].IsValid || !canRetainMeasurement(position))
                        _geometryDetails.Remove(position);
            QueueQuery();
        }

        public void SetDetailResolver(Func<int, string?> resolver) => _detailResolver = resolver;

        internal void SetGeometryDetailResolver(Func<int, Func<PanelGeometryInfo?>?> resolver) => _geometryDetailResolver = resolver;

        internal PanelGeometryInfo? GetGeometryDetails(PanelSnapshot snapshot, int sourcePosition) =>
            ReferenceEquals(snapshot, Snapshot) && _geometryDetails.TryGetValue(sourcePosition, out var measurement) ? measurement.Details : null;

        internal bool HasGeometryMeasurement(PanelSnapshot snapshot, int sourcePosition) =>
            ReferenceEquals(snapshot, Snapshot) && _geometryDetails.TryGetValue(sourcePosition, out var measurement) && measurement.Complete;

        internal bool IsGeometryPending(PanelSnapshot snapshot, int sourcePosition) => ReferenceEquals(snapshot, Snapshot) &&
            ((_geometryDetails.TryGetValue(sourcePosition, out var measurement) && !measurement.Complete) || _visibleGeometry.Contains(sourcePosition));

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

        private void QueueGeometryPump()
        {
            if (_disposed || _geometryRunning || _geometryScheduled || _visibleGeometry.Count == 0) return;
            _geometryScheduled = true;
            // Yield until painting and rapid scrolling finish before copying any geometry.
            Task.Run(async () =>
            {
                await Task.Delay(40).ConfigureAwait(false);
                RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    _geometryScheduled = false;
                    PumpGeometry();
                }));
            });
        }

        private void PumpGeometry()
        {
            if (_disposed || _geometryRunning) return;
            int position = _visibleGeometry.FindIndex(candidate => CanMeasureGeometry(Snapshot, candidate));
            if (position < 0) return;
            position = _visibleGeometry[position];
            var measurement = new GeometryMeasurement();
            _geometryDetails[position] = measurement;
            Func<PanelGeometryInfo?>? compute;
            try { compute = _geometryDetailResolver?.Invoke(position); }
            catch (Exception) { compute = null; }
            if (compute == null)
            {
                measurement.Complete = true;
                _visibleGeometry.Remove(position);
                Notify();
                QueueGeometryPump();
                return;
            }
            _geometryRunning = true;
            Notify();
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
                        Notify();
                    }
                    QueueGeometryPump();
                }));
            });
        }

        private sealed class GeometryMeasurement
        {
            internal PanelGeometryInfo? Details;
            internal bool Complete;
        }

        public string GetDisplayValue(PanelSnapshot snapshot, int sourcePosition)
        {
            if (sourcePosition < 0 || sourcePosition >= snapshot.Rows.Count) return string.Empty;
            var row = snapshot.Rows[sourcePosition];
            // An obsolete view may still be visible while the next solution is indexed.
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
                _options.OnlyInvalid != next.OnlyInvalid || _options.SortMode != next.SortMode;
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
            Notify();
            // Only plain immutable managed snapshot values cross this boundary.
            Task.Run(() =>
            {
                try
                {
                    var result = PanelQueryEngine.Execute(snapshot, options, token);
                    RhinoApp.InvokeOnUiThread((Action)(() =>
                    {
                        if (_disposed || request != _request || token.IsCancellationRequested) return;
                        Result = result;
                        IsBusy = false;
                        Notify();
                    }));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    RhinoApp.InvokeOnUiThread((Action)(() =>
                    {
                        if (_disposed || request != _request) return;
                        Error = ex.Message;
                        IsBusy = false;
                        Notify();
                    }));
                }
            });
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
            ++_request;
            _queryCancellation?.Cancel();
            _queryCancellation?.Dispose();
            _queryCancellation = null;
            QueryChanged = null;
            _detailResolver = null;
            _geometryDetailResolver = null;
            _geometryDetails.Clear();
            _visibleGeometry.Clear();
            Snapshot = PanelSnapshot.Empty;
        }
    }
}
