using System;
using System.Threading;

// Only checks made while the selected fixture is executing count toward its report.
internal static class SmokeAssertions
{
    private static int _active;
    private static int _count;

    internal static int Count => Volatile.Read(ref _count);
    internal static void Begin()
    {
        Volatile.Write(ref _count, 0);
        Volatile.Write(ref _active, 1);
    }
    internal static void End() => Volatile.Write(ref _active, 0);

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        if (Volatile.Read(ref _active) != 0) Interlocked.Increment(ref _count);
        Console.WriteLine("PASS: " + message);
    }
}
