using System;
using Xunit;

internal static class NumericAssertions
{
    internal static void AssertNear(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= Math.Max(1, Math.Abs(expected)) * 1e-12,
            $"Expected approximately {expected:R}; got {actual:R}.");
}
