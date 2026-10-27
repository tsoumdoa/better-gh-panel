using System;
using System.Collections.Generic;
using BetterGhPanel.Runtime;

internal static class NurbsCandidateLimitChecks
{
    internal static void Run()
    {
        Check(NurbsCandidateLimits.Curve(2, 2, 2, 2, out int small) && small == 144, "valid 2D curve payload");
        Check(NurbsCandidateLimits.Curve(3, 12, 1024, 1034, out int largest) && largest == 41104, "largest curve payload");
        Check(!NurbsCandidateLimits.Curve(4, 2, 2, 2, out _) && !NurbsCandidateLimits.Curve(3, 13, 13, 24, out _) &&
            !NurbsCandidateLimits.Curve(3, 3, 2, 3, out _) && !NurbsCandidateLimits.Curve(3, 2, 1025, 1025, out _) &&
            !NurbsCandidateLimits.Curve(3, 2, 2, int.MaxValue, out _), "unsupported curves rejected before allocation");
        Check(NurbsCandidateLimits.Surface(3, 2, 2, 64, 64, 64, 64, out int surface) && surface == 132192,
            "largest permitted surface point grid payload");
        Check(!NurbsCandidateLimits.Surface(2, 2, 2, 2, 2, 2, 2, out _) &&
            !NurbsCandidateLimits.Surface(3, 2, 2, 65, 64, 65, 64, out _) &&
            !NurbsCandidateLimits.Surface(3, 2, 2, int.MaxValue, int.MaxValue, 0, 0, out _) &&
            !NurbsCandidateLimits.Surface(3, 2, 2, 2, 2, 2, 3, out _), "surface dimensions, products and knots are bounded");

        var reservations = new List<IDisposable>();
        try
        {
            IDisposable? item;
            while ((item = NurbsCandidateLimits.TryReserve(surface)) != null) reservations.Add(item);
            Check(reservations.Count > 0 && reservations.Count * (surface * 4L + 65536) <= NurbsCandidateLimits.MaxInFlightBytes,
                "process-wide reservations stop before the in-flight value budget");
            reservations[0].Dispose();
            reservations[0].Dispose();
            using var replacement = NurbsCandidateLimits.TryReserve(surface);
            Check(replacement != null && NurbsCandidateLimits.TryReserve(surface) == null,
                "release is idempotent and returns only the reserved capacity");
            Check(NurbsCandidateLimits.TryReserve(-1) == null && NurbsCandidateLimits.TryReserve(int.MaxValue) == null,
                "invalid and overflowing reservations fail");
        }
        finally { foreach (var reservation in reservations) reservation.Dispose(); }
        using var restored = NurbsCandidateLimits.TryReserve(surface);
        Check(restored != null, "all reservations released");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
