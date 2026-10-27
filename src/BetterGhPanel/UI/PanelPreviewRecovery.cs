using System;
using System.Collections.Generic;

namespace BetterGhPanel.UI
{
    // Retain the ordered selection, rather than dropping its unvisited suffix
    // at the per-turn budget. Sparse toggles do not restart or scan that suffix.
    internal sealed class PanelPreviewRecovery
    {
        internal enum Admission { Complete, Retry, Blocked }
        private IReadOnlyList<int>? _positions;
        private int _next;
        private int _retryFrom = -1;
        private long _version;
        internal bool Pending => _positions != null && _next < _positions.Count;

        internal void Reset(IReadOnlyList<int> positions) { _version++; _positions = positions; _next = 0; _retryFrom = -1; }
        internal void Clear() { _version++; _positions = null; _next = 0; _retryFrom = -1; }

        // False means capacity/preparation blocked, no work remains, or a
        // reentrant reset replaced this batch. Retry blocked work only when
        // selection/capacity changes; never spin on an inadmissible root.
        internal bool Pump(int maxRows, Func<int, bool> selected, Func<int, bool> settled, Func<int, Admission> admit)
        {
            long version = _version;
            int visited = 0;
            while (Pending && visited++ < maxRows)
            {
                int position = _positions![_next];
                if (selected(position) && !settled(position))
                {
                    var admitted = admit(position);
                    if (version != _version || admitted == Admission.Blocked) return false;
                    // A root too expensive for this turn must not hide later
                    // small roots. Retain the earliest retry without allocating
                    // a second queue; sleep after one pass until capacity changes.
                    if (admitted == Admission.Retry && _retryFrom < 0) _retryFrom = _next;
                }
                if (version != _version) return false;
                _next++;
            }
            if (!Pending)
            {
                if (_retryFrom >= 0) { _next = _retryFrom; _retryFrom = -1; return false; }
                _positions = null; _next = 0; return false;
            }
            return true;
        }
    }
}
