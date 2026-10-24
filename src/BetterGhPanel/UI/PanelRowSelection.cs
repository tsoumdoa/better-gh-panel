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
        internal event EventHandler? Changed;

        internal void Clear()
        {
            bool changed = _positions.Count > 0;
            _positions.Clear();
            _anchor = -1;
            _result = null;
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }

        internal void Synchronize(PanelQueryResult result)
        {
            if (ReferenceEquals(_result, result)) return;
            int previousCount = _positions.Count;
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
            if (_positions.Count != previousCount) Changed?.Invoke(this, EventArgs.Empty);
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
                if (toggle)
                {
                    bool added = false;
                    for (int i = Math.Min(index, anchorIndex); i <= Math.Max(index, anchorIndex); i++)
                        added |= _positions.Add(result.RowPositions[i]);
                    if (added) Changed?.Invoke(this, EventArgs.Empty);
                    return;
                }
                var next = new HashSet<int>();
                for (int i = Math.Min(index, anchorIndex); i <= Math.Max(index, anchorIndex); i++)
                    next.Add(result.RowPositions[i]);
                Replace(next, _anchor);
                return;
            }
            bool changed = false;
            if (toggle)
            {
                if (!_positions.Remove(position)) _positions.Add(position);
                changed = true;
            }
            else if (!preserveSelected || !_positions.Contains(position))
            {
                changed = _positions.Count != 1 || !_positions.Contains(position);
                _positions.Clear();
                _positions.Add(position);
            }
            _anchor = position;
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }

        internal IReadOnlyList<int> OrderedPositions(PanelQueryResult result)
        {
            Synchronize(result);
            return _positions.Count == 0 ? Array.Empty<int>() : result.RowPositions.Where(_positions.Contains).ToArray();
        }

        internal int[] Indices(PanelQueryResult result)
        {
            Synchronize(result);
            return _positions.Select(position => result.Snapshot.Rows[position].OriginalIndex).Distinct().OrderBy(index => index).ToArray();
        }

        internal void SelectIndices(PanelQueryResult result, IEnumerable<int> indices)
        {
            Synchronize(result);
            var wanted = new HashSet<int>(indices);
            var next = new HashSet<int>();
            int anchor = -1;
            foreach (int position in result.RowPositions)
            {
                if (!wanted.Contains(result.Snapshot.Rows[position].OriginalIndex)) continue;
                next.Add(position);
                if (anchor < 0) anchor = position;
            }
            Replace(next, anchor);
        }

        internal void SelectAll(PanelQueryResult result, string? branchPath = null)
        {
            Synchronize(result);
            var next = new HashSet<int>();
            int anchor = -1;
            foreach (int position in result.RowPositions)
            {
                if (branchPath != null && result.Snapshot.Rows[position].Path != branchPath) continue;
                next.Add(position);
                if (anchor < 0) anchor = position;
            }
            Replace(next, anchor);
        }

        private void Replace(HashSet<int> next, int anchor)
        {
            bool changed = !_positions.SetEquals(next);
            if (changed)
            {
                _positions.Clear();
                _positions.UnionWith(next);
            }
            _anchor = anchor;
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }

        private static int IndexOf(IReadOnlyList<int> positions, int position)
        {
            for (int i = 0; i < positions.Count; i++)
                if (positions[i] == position) return i;
            return -1;
        }
    }
}
