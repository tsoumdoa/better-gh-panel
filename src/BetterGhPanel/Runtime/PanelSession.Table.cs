using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;

namespace BetterGhPanel.Runtime
{
    public sealed partial class PanelSession
    {
        private readonly List<PanelTableColumn> _columns = new List<PanelTableColumn>();
        private CancellationTokenSource? _visibleColumns;
        private PanelSnapshot? _visibleSnapshot;
        private int[][]? _visibleDemand;
        private long _columnGeneration;

        // Test seam for the native fixture: delay submission without replacing
        // production capture, preparation, admission or completion/source fences.
        internal Func<Action, bool>? GeometryWorkerSubmissionForTesting { get; set; }
        private bool SubmitGeometryWorker(Action action)
        {
            if (GeometryWorkerSubmissionForTesting is { } submit) return submit(action);
            _ = Task.Run(action);
            return true;
        }

        internal void ConfigureColumnGeometry(IReadOnlyList<PanelTableColumn> columns,
            Func<PanelTableColumn, int, PreparedMeasurement<PanelGeometryInfo>?> prepare,
            Func<PanelTableColumn, PanelSnapshot, int, bool> current)
        {
            if (_disposed) return;
            var generation = ++_columnGeneration;
            var next = columns.ToArray();
            CancelVisibleColumnMeasurements();
            foreach (var old in _columns.ToArray())
                if (!next.Contains(old))
                {
                    var retired = old.Geometry; old.Geometry = null; retired?.Dispose();
                    if (_disposed || generation != _columnGeneration) return;
                }
            _columns.Clear(); _columns.AddRange(next);
            for (int index = 0; index < next.Length; index++)
            {
                var column = next[index];
                if (column.Snapshot != null && column.Geometry?.IsAttachedTo(column.Snapshot) == true) continue;
                var retired = column.Geometry; column.Geometry = null; retired?.Dispose();
                if (_disposed || generation != _columnGeneration) return;
                if (column.Snapshot == null) continue;
                column.Geometry = CreateGeometryQueue(position =>
                {
                    var prepared = prepare(column, position);
                    if (prepared != null) return prepared;
                    var summary = column.Snapshot?.Rows[position].GeometryInfo;
                    return summary == null ? null : new PreparedMeasurement<PanelGeometryInfo>(() => summary.WithUnavailableMeasurement(), null, null);
                }, (snapshot, position) => current(column, snapshot, position), priority: index + 1, retainWitnesses: false);
                column.Geometry.Attach(column.Snapshot, false);
            }
        }

        private void ClearColumnGeometry()
        {
            ++_columnGeneration;
            CancelVisibleColumnMeasurements();
            var retired = _columns.Select(column => column.Geometry).ToArray();
            foreach (var column in _columns) column.Geometry = null;
            _columns.Clear();
            foreach (var queue in retired) queue?.Dispose();
        }

        internal void CancelVisibleColumnMeasurements()
        {
            var cancellation = _visibleColumns; _visibleColumns = null;
            _visibleSnapshot = null; _visibleDemand = null;
            if (cancellation == null) return;
            cancellation.Cancel(); cancellation.Dispose();
            foreach (var column in _columns.ToArray()) column.Geometry?.PruneDemand();
        }

        internal void RequestTableMeasurements(PanelSnapshot snapshot, IReadOnlyList<int> data, IReadOnlyList<int[]> columns)
        {
            var demand = new[] { data.ToArray() }.Concat(columns).ToArray();
            if (ReferenceEquals(snapshot, _visibleSnapshot) && _visibleDemand != null && demand.Length == _visibleDemand.Length &&
                demand.Select((items, index) => items.SequenceEqual(_visibleDemand[index])).All(same => same)) return;
            CancelVisibleColumnMeasurements();
            if (_disposed || !HasCurrentResult || !ReferenceEquals(snapshot, Snapshot) || !_geometry.IsAttachedTo(snapshot)) return;
            _visibleSnapshot = snapshot; _visibleDemand = demand;
            var cancellation = _visibleColumns = new CancellationTokenSource();
            var requests = _columns.Select((column, index) => (Snapshot: column.Snapshot, Queue: column.Geometry,
                Positions: index < columns.Count ? columns[index] : Array.Empty<int>())).ToArray();
            var token = cancellation.Token;
            _geometry.RequestVisible(snapshot, data);
            _ = MeasureVisible();
            async Task MeasureVisible()
            {
                try
                {
                    if (!await _geometry.MeasureAsync(snapshot, data, token).ConfigureAwait(false)) return;
                    foreach (var request in requests)
                    {
                        if (token.IsCancellationRequested) return;
                        if (request.Snapshot != null && request.Queue != null && request.Positions.Length > 0 &&
                            !await request.Queue.MeasureAsync(request.Snapshot, request.Positions, token).ConfigureAwait(false)) return;
                    }
                }
                catch (OperationCanceledException) { }
            }
        }
    }
}
