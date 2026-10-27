using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGhPanel.Runtime
{
    // Limits concurrently prepared or running compatibility copies across panels.
    // It does not bound the cost or allocation of any individual native call.
    internal static class LegacyMeasurementAdmission
    {
        internal const int MaxConcurrent = 2;
        private static readonly object Gate = new object();
        private static int _inUse;
        private static TaskCompletionSource<bool> _released = NewSignal();

        internal static IDisposable? TryAcquire(out Task released)
        {
            lock (Gate)
            {
                released = _released.Task;
                if (_inUse >= MaxConcurrent) return null;
                _inUse++;
                return new Reservation();
            }
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class Reservation : IDisposable
        {
            private int _releasedOnce;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _releasedOnce, 1) != 0) return;
                TaskCompletionSource<bool> signal;
                lock (Gate)
                {
                    _inUse--;
                    signal = _released;
                    _released = NewSignal();
                }
                signal.TrySetResult(true);
            }
        }
    }
}
