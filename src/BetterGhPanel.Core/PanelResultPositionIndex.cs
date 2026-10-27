using System;
using System.Collections.Generic;

namespace BetterGhPanel.Core
{
    // Result-local reverse lookup. A dense result uses four bytes per input row;
    // a very sparse one grows with the number of matches instead of the input.
    // The query fills it cooperatively before publishing the immutable result.
    internal sealed class PanelResultPositionIndex
    {
        private const int PageShift = 12;
        private const int PageSize = 1 << PageShift;
        private readonly int _rowCount;
        private readonly int[]?[]? _dense;
        private readonly Dictionary<int, int>? _sparse;

        internal PanelResultPositionIndex(int rowCount, int matchCount)
        {
            _rowCount = rowCount;
            if (rowCount <= (long)matchCount * 4)
                _dense = new int[(int)((rowCount + (long)PageSize - 1) / PageSize)][];
            else
                _sparse = new Dictionary<int, int>();
        }

        internal void Add(int position, int index)
        {
            if (_dense != null)
            {
                int pageIndex = position >> PageShift;
                var page = _dense[pageIndex];
                if (page == null)
                    _dense[pageIndex] = page = new int[Math.Min(PageSize, _rowCount - pageIndex * PageSize)];
                page[position & (PageSize - 1)] = index + 1;
            }
            else _sparse!.Add(position, index);
        }

        internal bool TryGet(int position, out int index)
        {
            if (_dense != null)
            {
                var page = position >= 0 && position < _rowCount ? _dense[position >> PageShift] : null;
                index = page == null ? -1 : page[position & (PageSize - 1)] - 1;
                return index >= 0;
            }
            if (_sparse!.TryGetValue(position, out index)) return true;
            index = -1;
            return false;
        }
    }
}
