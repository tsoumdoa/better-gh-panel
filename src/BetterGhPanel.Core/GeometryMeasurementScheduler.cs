using System;
using System.Collections.Generic;

namespace BetterGhPanel.Core
{
    // UI-owned admission for one session. Only the active lease can retain a
    // submitted worker; pending owners have one replaceable, removable request.
    internal sealed class GeometryMeasurementScheduler : IDisposable
    {
        private readonly Action<Action> _postUi;
        private readonly Dictionary<object, Request> _pending = new Dictionary<object, Request>();
        private Lease? _active;
        private bool _scheduled, _disposed;

        internal GeometryMeasurementScheduler(Action<Action> postUi) => _postUi = postUi;
        internal int PendingCount => _pending.Count;

        internal void RequestWork(object owner, object snapshot, int priority, Func<bool> current, Action<Lease> start)
        {
            if (_disposed) return;
            _pending[owner] = new Request(owner, snapshot, priority, current, start);
            Schedule();
        }

        internal void Cancel(object owner)
        {
            _pending.Remove(owner);
            if (ReferenceEquals(_active?.Owner, owner)) _active!.Canceled = true;
        }

        // Retention proofs are synchronous. At capacity, discard the old proof
        // and remeasure later rather than preparing outside the session slot.
        internal Lease? TryAcquire(object owner, object snapshot)
        {
            if (_disposed || _active != null || _pending.Count != 0) return null;
            return _active = new Lease(this, owner, snapshot);
        }

        private void Schedule()
        {
            if (_disposed || _scheduled || _active != null || _pending.Count == 0) return;
            _scheduled = true;
            _postUi(Pump); // Never capture a retired queue in the posted callback.
        }

        private void Pump()
        {
            _scheduled = false;
            if (_disposed || _active != null) return;
            Request? next = null;
            foreach (var request in _pending.Values)
                if (next == null || request.Priority < next.Priority) next = request;
            if (next == null) return;
            _pending.Remove(next.Owner);
            var lease = _active = new Lease(this, next.Owner, next.Snapshot);
            try
            {
                // Source checks can also pump UI events. Admission already owns
                // the slot, and cancellation cannot release it from inside them.
                if (next.Current() && !lease.Canceled && !_disposed) next.Start(lease);
                else lease.Dispose();
            }
            catch { lease.Dispose(); throw; }
        }

        private void Release(Lease lease)
        {
            if (!ReferenceEquals(_active, lease)) return;
            _active = null;
            Schedule();
        }

        public void Dispose()
        {
            _disposed = true;
            _pending.Clear();
            if (_active != null) _active.Canceled = true;
        }

        internal sealed class Lease : IDisposable
        {
            private GeometryMeasurementScheduler? _scheduler;
            internal readonly object Owner, Snapshot;
            internal bool Canceled;
            internal Lease(GeometryMeasurementScheduler scheduler, object owner, object snapshot)
            { _scheduler = scheduler; Owner = owner; Snapshot = snapshot; }
            public void Dispose()
            {
                var scheduler = _scheduler; _scheduler = null;
                scheduler?.Release(this);
            }
        }

        private sealed class Request
        {
            internal readonly object Owner, Snapshot;
            internal readonly int Priority;
            internal readonly Func<bool> Current;
            internal readonly Action<Lease> Start;
            internal Request(object owner, object snapshot, int priority, Func<bool> current, Action<Lease> start)
            { Owner = owner; Snapshot = snapshot; Priority = priority; Current = current; Start = start; }
        }
    }
}
