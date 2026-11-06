using Xunit;
using BetterGhPanel.Core;
using System;
using System.Collections.Generic;

[Collection("NurbsCandidateLimits")]
public class NurbsCandidateLimitTests
{
    [Fact]
    public void CandidateLimitsBoundPayloadsAndReleaseReservations()
    {
        Assert.True(NurbsCandidateLimits.Curve(2, 2, 2, 2, out int small) && small == 144, "valid 2D curve payload");
        Assert.True(NurbsCandidateLimits.Curve(3, 12, 1024, 1034, out int largest) && largest == 41104, "largest curve payload");
        Assert.True(!NurbsCandidateLimits.Curve(4, 2, 2, 2, out _) && !NurbsCandidateLimits.Curve(3, 13, 13, 24, out _) &&
            !NurbsCandidateLimits.Curve(3, 3, 2, 3, out _) && !NurbsCandidateLimits.Curve(3, 2, 1025, 1025, out _) &&
            !NurbsCandidateLimits.Curve(3, 2, 2, int.MaxValue, out _), "unsupported curves rejected before allocation");
        Assert.True(NurbsCandidateLimits.Polyline(3, 2048, out int polyline) && polyline == 65540 &&
            !NurbsCandidateLimits.Polyline(3, 2049, out _) && !NurbsCandidateLimits.Polyline(2, 2, out _),
            "polyline point count and dimension are bounded before allocation");
        Assert.True(NurbsCandidateLimits.Surface(3, 2, 2, 64, 64, 64, 64, out int surface) && surface == 132192,
            "largest permitted surface point grid payload");
        Assert.True(!NurbsCandidateLimits.Surface(2, 2, 2, 2, 2, 2, 2, out _) &&
            !NurbsCandidateLimits.Surface(3, 2, 2, 65, 64, 65, 64, out _) &&
            !NurbsCandidateLimits.Surface(3, 2, 2, int.MaxValue, int.MaxValue, 0, 0, out _) &&
            !NurbsCandidateLimits.Surface(3, 2, 2, 2, 2, 2, 3, out _), "surface dimensions, products and knots are bounded");

        var reservations = new List<IDisposable>();
        try
        {
            IDisposable? item;
            while ((item = NurbsCandidateLimits.TryReserve(surface)) != null) reservations.Add(item);
            Assert.True(reservations.Count > 0 && reservations.Count * (surface * 4L + 65536) <= NurbsCandidateLimits.MaxInFlightBytes,
                "process-wide reservations stop before the in-flight value budget");
            reservations[0].Dispose();
            reservations[0].Dispose();
            using var replacement = NurbsCandidateLimits.TryReserve(surface);
            Assert.True(replacement != null && NurbsCandidateLimits.TryReserve(surface) == null,
                "release is idempotent and returns only the reserved capacity");
            Assert.True(NurbsCandidateLimits.TryReserve(surface, out var signal) == null && signal != null && !signal.IsCompleted,
                "temporary capacity exhaustion provides a pending release signal");
            replacement!.Dispose();
            Assert.True(signal!.IsCompleted, "a release wakes waiters without polling");
            Assert.True(NurbsCandidateLimits.TryReserve(-1) == null && NurbsCandidateLimits.TryReserve(int.MaxValue) == null,
                "invalid and overflowing reservations fail");
        }
        finally { foreach (var reservation in reservations) reservation.Dispose(); }
        using var restored = NurbsCandidateLimits.TryReserve(surface);
        Assert.True(restored != null, "all reservations released");
    }

}
