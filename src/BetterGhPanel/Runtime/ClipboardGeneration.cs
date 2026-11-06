using System.Threading;

namespace BetterGhPanel.Runtime
{
    internal static class ClipboardGeneration
    {
        private static long _generation;
        internal static long Current => Interlocked.Read(ref _generation);
        internal static long Begin() => Interlocked.Increment(ref _generation);
        internal static bool IsCurrent(long generation) => Current == generation;
    }
}
