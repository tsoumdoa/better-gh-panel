using System;
using System.Threading.Tasks;

namespace BetterGhPanel.Core
{
    // Limits concurrently prepared or running compatibility copies across panels.
    // It does not bound the cost or allocation of any individual native call.
    internal static class LegacyMeasurementAdmission
    {
        internal const int MaxConcurrent = 2;
        private static readonly CapacityGate Gate = new CapacityGate(MaxConcurrent);
        internal static IDisposable? TryAcquire(out Task released) => Gate.TryAcquire(1, out released);
    }
}
