using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGhPanel.Runtime
{
    // Payload bounds and one process-wide reservation for both curve providers.
    // These do not bound Rhino's allocator or mass-property working memory.
    internal static class NurbsCandidateLimits
    {
        internal const int MaxOrder = 12;
        internal const int MaxCurvePoints = 1024;
        internal const int MaxSurfaceAxisPoints = 128;
        internal const int MaxSurfacePoints = 4096;
        internal const int MaxInFlightBytes = 4 * 1024 * 1024;
        internal const int MaxPolylinePoints = 2048;
        private static readonly object Gate = new object();
        private static int _inFlightBytes;
        private static TaskCompletionSource<bool> _released = NewReleaseSignal();

        internal static bool Polyline(int dimension, int count, out int payload)
        {
            payload = 0;
            if (dimension != 3 || count < 2 || count > MaxPolylinePoints) return false;
            payload = checked(sizeof(int) + count * 4 * sizeof(double));
            return true;
        }

        internal static bool Curve(int dimension, int order, int count, int knots, out int payload)
        {
            payload = 0;
            if ((dimension != 2 && dimension != 3) || order < 2 || order > MaxOrder ||
                count < order || count > MaxCurvePoints || knots != count + order - 2) return false;
            payload = checked(32 * count + 8 * knots + 64);
            return true;
        }

        internal static bool Surface(int dimension, int orderU, int orderV, int countU, int countV,
            int knotsU, int knotsV, out int payload)
        {
            payload = 0;
            if (dimension != 3 || orderU < 2 || orderU > MaxOrder || orderV < 2 || orderV > MaxOrder ||
                countU < orderU || countV < orderV || countU > MaxSurfaceAxisPoints || countV > MaxSurfaceAxisPoints ||
                (long)countU * countV > MaxSurfacePoints || knotsU != countU + orderU - 2 || knotsV != countV + orderV - 2)
                return false;
            payload = checked(32 * countU * countV + 8 * (knotsU + knotsV) + 96);
            return true;
        }

        // Reserve before any value arrays or worker geometry are allocated. Four
        // payloads cover the two value buffers, witness clone and owned NURBS
        // payload, plus a fixed allowance for small managed/native structures.
        internal static IDisposable? TryReserve(int payload) => TryReserve(payload, out _);

        // The returned task is completed only when capacity is released. Taking
        // it while holding Gate avoids missing a release between failure and wait.
        internal static IDisposable? TryReserve(int payload, out Task? released)
        {
            released = null;
            if (payload <= 0 || payload > (MaxInFlightBytes - 65536) / 4) return null;
            int bytes = checked(payload * 4 + 65536);
            lock (Gate)
            {
                if (_inFlightBytes > MaxInFlightBytes - bytes)
                {
                    released = _released.Task;
                    return null;
                }
                var reservation = new Reservation(bytes);
                _inFlightBytes += bytes;
                return reservation;
            }
        }

        private static TaskCompletionSource<bool> NewReleaseSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class Reservation : IDisposable
        {
            private int _bytes;
            internal Reservation(int bytes) { _bytes = bytes; }
            public void Dispose()
            {
                int bytes = Interlocked.Exchange(ref _bytes, 0);
                if (bytes == 0) return;
                TaskCompletionSource<bool> signal;
                lock (Gate)
                {
                    _inFlightBytes -= bytes;
                    signal = _released;
                    _released = NewReleaseSignal();
                }
                signal.TrySetResult(true);
            }
        }
    }

    // Full capacity is temporary, unlike an unsupported or invalid candidate.
    internal sealed class MeasurementCapacityException : Exception
    {
        internal MeasurementCapacityException(Task released) { Released = released; }
        internal Task Released { get; }
    }
}
