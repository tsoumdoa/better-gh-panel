using System;

namespace BetterGhPanel.Runtime
{
    // A claimed worker owns both the copy and its process-wide reservation.
    internal sealed class OwnedMeasurementResource : IDisposable
    {
        private readonly IDisposable _geometry;
        private readonly IDisposable _reservation;
        internal OwnedMeasurementResource(IDisposable geometry, IDisposable reservation)
        { _geometry = geometry; _reservation = reservation; }
        public void Dispose()
        {
            try { _geometry.Dispose(); }
            finally { _reservation.Dispose(); }
        }
    }
}
