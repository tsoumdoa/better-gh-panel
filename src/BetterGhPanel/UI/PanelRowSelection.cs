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
        private readonly HashSet<string> _emptyBranches = new HashSet<string>(StringComparer.Ordinal);
        private IReadOnlyList<string>? _branchPaths;
        private PanelQueryResult? _result;
        private int _anchor = -1;

        internal int Count => _positions.Count + _emptyBranches.Count;
        internal bool Contains(int position) => _positions.Contains(position);
        internal event EventHandler? Changed;
        private void NotifyChanged() { _branchPaths = null; Changed?.Invoke(this, EventArgs.Empty); }

        internal void Clear()
        {
            bool changed = Count > 0;
            _positions.Clear();
            _emptyBranches.Clear();
            _anchor = -1;
            _result = null;
            if (changed) NotifyChanged();
        }

        internal void Synchronize(PanelQueryResult result)
        {
            if (ReferenceEquals(_result, result)) return;
            int previousCount = Count;
            if (_result == null || !ReferenceEquals(_result.Snapshot, result.Snapshot))
            {
                _positions.Clear();
                _emptyBranches.Clear();
                _anchor = -1;
            }
            else if (_positions.Count > 0 || _anchor >= 0)
            {
                var visible = new HashSet<int>(result.RowPositions);
                _positions.IntersectWith(visible);
                if (!visible.Contains(_anchor)) _anchor = -1;
            }
            if (result.DisplaySet?.IsBranchSet == true) _emptyBranches.IntersectWith(result.BranchPaths);
            else _emptyBranches.Clear();
            _result = result;
            if (Count != previousCount) NotifyChanged();
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
                    if (added) NotifyChanged();
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
            if (changed) NotifyChanged();
        }

        internal IReadOnlyList<int> OrderedPositions(PanelQueryResult result)
        {
            Synchronize(result);
            return _positions.Count == 0 ? Array.Empty<int>() : result.RowPositions.Where(_positions.Contains).ToArray();
        }

        internal IReadOnlyList<string> BranchPaths(PanelQueryResult result)
        {
            Synchronize(result);
            return _branchPaths ??= _positions.Select(position => result.Snapshot.Rows[position].Path)
                .Concat(_emptyBranches).Distinct(StringComparer.Ordinal).ToArray();
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

        internal void SelectAll(PanelQueryResult result, string? branchPath = null, bool includeEmptyBranches = false)
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
            var empty = includeEmptyBranches && result.DisplaySet?.IsBranchSet == true
                ? new HashSet<string>(result.BranchPaths.Where(path => (branchPath == null || branchPath == path) && result.Snapshot.BranchItemCounts[path] == 0)) : null;
            Replace(next, anchor, empty);
        }

        private void Replace(HashSet<int> next, int anchor, HashSet<string>? emptyBranches = null)
        {
            bool changed = !_positions.SetEquals(next) || (emptyBranches == null ? _emptyBranches.Count > 0 : !_emptyBranches.SetEquals(emptyBranches));
            if (changed)
            {
                _positions.Clear();
                _positions.UnionWith(next);
                _emptyBranches.Clear();
                if (emptyBranches != null) _emptyBranches.UnionWith(emptyBranches);
            }
            _anchor = anchor;
            if (changed) NotifyChanged();
        }

        private static int IndexOf(IReadOnlyList<int> positions, int position)
        {
            for (int i = 0; i < positions.Count; i++)
                if (positions[i] == position) return i;
            return -1;
        }
    }
}
