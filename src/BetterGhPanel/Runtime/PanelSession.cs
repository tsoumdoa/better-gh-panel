using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Rhino;

namespace BetterGhPanel.Runtime
{
    /// <summary>Owns one component's immutable view and cancelable background queries.</summary>
    public sealed class PanelSession : IDisposable
    {
        private readonly Action _invalidate;
        private CancellationTokenSource? _queryCancellation;
        private PanelQueryOptions _options = new PanelQueryOptions();
        private Func<PanelRow, string>? _detailResolver;
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
        public long QueryRevision { get; private set; }
        public bool IsBusy { get; private set; }
        public string? Error { get; private set; }
        public event EventHandler? Changed;
        public event EventHandler? QueryChanged;

        public void AttachSnapshot(PanelSnapshot snapshot)
        {
            if (_disposed) return;
            Snapshot = snapshot ?? PanelSnapshot.Empty;
            QueueQuery();
        }

        public void SetDetailResolver(Func<PanelRow, string> resolver) => _detailResolver = resolver;

        public string GetDisplayValue(PanelRow? row)
        {
            if (row == null) return string.Empty;
            // An obsolete view may still be visible while the next solution is indexed.
            if (row.Id < 0 || row.Id >= Snapshot.Rows.Count || !ReferenceEquals(Snapshot.Rows[row.Id], row))
                return row.DisplayValue;
            try { return _detailResolver?.Invoke(row) ?? row.DisplayValue; }
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
            ++QueryRevision;
            if (changed) QueryChanged?.Invoke(this, EventArgs.Empty);
            QueueQuery();
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
                Changed?.Invoke(this, EventArgs.Empty);
                _invalidate?.Invoke();
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
            Changed = null;
            QueryChanged = null;
            _detailResolver = null;
            Snapshot = PanelSnapshot.Empty;
        }
    }
}
