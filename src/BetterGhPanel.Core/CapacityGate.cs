using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGhPanel.Core
{
    internal sealed class CapacityGate
    {
        private readonly object _gate = new object();
        private readonly int _capacity;
        private int _inUse;
        private TaskCompletionSource<bool> _released = NewSignal();
        internal CapacityGate(int capacity) { _capacity = capacity; }
        internal IDisposable? TryAcquire(int amount, out Task released)
        {
            lock (_gate)
            {
                released = _released.Task;
                if (_inUse > _capacity - amount) return null;
                var reservation = new Reservation(this, amount);
                _inUse += amount;
                return reservation;
            }
        }
        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private sealed class Reservation : IDisposable
        {
            private readonly CapacityGate _owner;
            private int _amount;
            internal Reservation(CapacityGate owner, int amount) { _owner = owner; _amount = amount; }
            public void Dispose()
            {
                int amount = Interlocked.Exchange(ref _amount, 0);
                if (amount == 0) return;
                TaskCompletionSource<bool> signal;
                lock (_owner._gate)
                {
                    _owner._inUse -= amount;
                    signal = _owner._released;
                    _owner._released = NewSignal();
                }
                signal.TrySetResult(true);
            }
        }
    }
}
