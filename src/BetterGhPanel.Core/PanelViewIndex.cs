using System;
using System.Collections.Generic;

namespace BetterGhPanel.Core
{
    /// <summary>Scroll coordinates interleave retained empty branches with item rows.
    /// Item indices always refer to the unchanged filtered and sorted result.</summary>
    public sealed class PanelViewIndex
    {
        private readonly IReadOnlyList<int> _emptyItemOffsets;
        private readonly IReadOnlyList<string> _emptyPaths;

        internal PanelViewIndex(int itemCount, List<int> emptyItemOffsets, List<string> emptyPaths)
        {
            ItemCount = itemCount;
            _emptyItemOffsets = emptyItemOffsets;
            _emptyPaths = emptyPaths;
        }

        public int ItemCount { get; }
        public int Count => ItemCount + _emptyPaths.Count;

        /// <summary>Returns either an item index or the path of an empty branch.</summary>
        public bool TryGet(int offset, out int itemIndex, out string? emptyPath)
        {
            itemIndex = -1;
            emptyPath = null;
            if (offset < 0 || offset >= Count) return false;
            int low = 0, high = _emptyPaths.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (_emptyItemOffsets[middle] + middle <= offset) low = middle + 1;
                else high = middle;
            }
            int last = low - 1;
            if (last >= 0 && _emptyItemOffsets[last] + last == offset)
                emptyPath = _emptyPaths[last];
            else itemIndex = offset - low;
            return true;
        }

        /// <summary>Locate an item by its zero-based position, ignoring empty branches.</summary>
        public int OffsetOfItem(int itemIndex)
        {
            if (itemIndex < 0 || itemIndex >= ItemCount) throw new ArgumentOutOfRangeException(nameof(itemIndex));
            int low = 0, high = _emptyPaths.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (_emptyItemOffsets[middle] <= itemIndex) low = middle + 1;
                else high = middle;
            }
            return itemIndex + low;
        }

        /// <summary>Returns the item at or after a scroll coordinate, or ItemCount at the end.</summary>
        public int ItemAtOrAfter(int offset)
        {
            if (offset >= Count) return ItemCount;
            if (offset < 0) return 0;
            TryGet(offset, out int item, out _);
            if (item >= 0) return item;
            int low = 0, high = ItemCount;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (OffsetOfItem(middle) < offset) low = middle + 1;
                else high = middle;
            }
            return low;
        }
    }
}
