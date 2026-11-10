using System;

namespace BetterGhPanel.Core
{
    // UI-owned accounting. A resource keeps its lease through retirement and
    // releases it only after native disposal, including across generation clears.
    internal sealed class PanelPreviewMemory
    {
        private readonly long _limit;
        private readonly Func<long> _otherBytes;
        internal PanelPreviewMemory(long limit, Func<long> otherBytes)
        { _limit = limit; _otherBytes = otherBytes; }
        internal long UsedBytes { get; private set; }
        internal bool CanReserve(long bytes)
        {
            long other = _otherBytes();
            return bytes >= 0 && other >= 0 && other <= _limit && UsedBytes <= _limit - other &&
                bytes <= _limit - other - UsedBytes;
        }
        // Pending retirement covers a shortfall for eviction planning, but does not
        // make capacity reservable until the corresponding leases are disposed.
        internal long RequiredRelease(long bytes, long retiringBytes) =>
            Math.Max(0, _otherBytes() + UsedBytes + bytes - _limit - retiringBytes);
        internal Lease? TryReserve(long bytes)
        {
            if (!CanReserve(bytes)) return null;
            UsedBytes += bytes;
            return new Lease(this, bytes);
        }
        internal sealed class Lease : IDisposable
        {
            private PanelPreviewMemory? _owner;
            internal Lease(PanelPreviewMemory owner, long bytes) { _owner = owner; Bytes = bytes; }
            internal long Bytes { get; }
            public void Dispose()
            {
                var owner = _owner; _owner = null;
                if (owner != null) owner.UsedBytes -= Bytes;
            }
        }
    }
}
