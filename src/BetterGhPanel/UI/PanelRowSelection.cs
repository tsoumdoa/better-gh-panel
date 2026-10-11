using System;
using System.Collections.Generic;
using System.Linq;
using BetterGhPanel.Core;

namespace BetterGhPanel.UI
{
    // Source positions identify rows only within the snapshot that supplied them.
    internal sealed class PanelRowSelection
    {
        private readonly HashSet<int> _positions = new HashSet<int>();
        private PanelQueryResult? _result;
        private int _anchor = -1;

        internal int Count => _positions.Count;
        internal bool Contains(int position) => _positions.Contains(position);

        internal void Clear()
        {
            _positions.Clear();
            _anchor = -1;
            _result = null;
        }

        internal void Synchronize(PanelQueryResult result)
        {
            if (ReferenceEquals(_result, result)) return;
            if (_result == null || !ReferenceEquals(_result.Snapshot, result.Snapshot))
            {
                _positions.Clear();
                _anchor = -1;
            }
            else if (_positions.Count > 0 || _anchor >= 0)
            {
                var visible = new HashSet<int>(result.RowPositions);
                _positions.IntersectWith(visible);
                if (!visible.Contains(_anchor)) _anchor = -1;
            }
            _result = result;
        }

        internal void Select(PanelQueryResult result, int position, bool toggle = false,
            bool range = false, bool preserveSelected = false)
        {
            Synchronize(result);
            int index = IndexOf(result.RowPositions, position);
            if (position < 0 || position >= result.Snapshot.Rows.Count || index < 0) return;
            int anchorIndex = range ? IndexOf(result.RowPositions, _anchor) : -1;
            if (range && anchorIndex >= 0)
            {
                if (!toggle) _positions.Clear();
                for (int i = Math.Min(index, anchorIndex); i <= Math.Max(index, anchorIndex); i++)
                    _positions.Add(result.RowPositions[i]);
                return;
            }
            if (toggle)
            {
                if (!_positions.Remove(position)) _positions.Add(position);
            }
            else if (!preserveSelected || !_positions.Contains(position))
            {
                _positions.Clear();
                _positions.Add(position);
            }
            _anchor = position;
        }

        internal IReadOnlyList<int> OrderedPositions(PanelQueryResult result)
        {
            Synchronize(result);
            return result.RowPositions.Where(_positions.Contains).ToArray();
        }

        private static int IndexOf(IReadOnlyList<int> positions, int position)
        {
            for (int i = 0; i < positions.Count; i++)
                if (positions[i] == position) return i;
            return -1;
        }
    }
}
