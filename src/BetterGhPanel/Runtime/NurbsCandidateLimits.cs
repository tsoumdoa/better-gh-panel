using System;
using System.Threading;

namespace BetterGhPanel.Runtime
{
    // Payload bounds for the experimental, trusted-native NURBS value path.
    // These do not bound Rhino's allocator or mass-property working memory.
    internal static class NurbsCandidateLimits
    {
        internal const int MaxOrder = 12;
        internal const int MaxCurvePoints = 1024;
        internal const int MaxSurfaceAxisPoints = 128;
        internal const int MaxSurfacePoints = 4096;
        internal const int MaxInFlightBytes = 4 * 1024 * 1024;
        private static int _inFlightBytes;

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
        internal static IDisposable? TryReserve(int payload)
        {
            if (payload <= 0 || payload > (MaxInFlightBytes - 65536) / 4) return null;
            int bytes = checked(payload * 4 + 65536);
            while (true)
            {
                int current = Volatile.Read(ref _inFlightBytes);
                if (current > MaxInFlightBytes - bytes) return null;
                if (Interlocked.CompareExchange(ref _inFlightBytes, current + bytes, current) == current)
                {
                    try { return new Reservation(bytes); }
                    catch { Interlocked.Add(ref _inFlightBytes, -bytes); throw; }
                }
            }
        }

        private sealed class Reservation : IDisposable
        {
            private int _bytes;
            internal Reservation(int bytes) { _bytes = bytes; }
            public void Dispose()
            {
                int bytes = Interlocked.Exchange(ref _bytes, 0);
                if (bytes != 0) Interlocked.Add(ref _inFlightBytes, -bytes);
            }
        }
    }
}
