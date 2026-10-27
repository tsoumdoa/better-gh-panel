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
        private readonly Action? _rowVisited;
        private IReadOnlyList<int>? _orderedPositions;
        private IReadOnlyList<string>? _branchPaths;
        private PanelQueryResult? _result;
        private int _anchor = -1;
        private bool _lastBulkValid;
        private string? _lastBulkPath;
        private bool _lastBulkIncludesEmpty;
        private int _lastBulkAnchor;

        // The optional observer counts selection-path row visits in managed tests.
        internal PanelRowSelection(Action? rowVisited = null) => _rowVisited = rowVisited;

        internal int Count => _positions.Count + _emptyBranches.Count;
        internal bool Contains(int position) => _positions.Contains(position);
        internal event EventHandler? Changed;

        private void NotifyChanged()
        {
            _orderedPositions = null;
            _branchPaths = null;
            _lastBulkValid = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        internal void Clear()
        {
            bool changed = Count > 0;
            _positions.Clear();
            _emptyBranches.Clear();
            _anchor = -1;
            _result = null;
            _orderedPositions = null;
            _branchPaths = null;
            _lastBulkValid = false;
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
            else
            {
                // Test only selected positions; a sparse selection must not scan a
                // newly sorted or filtered result just to discover its order.
                _positions.RemoveWhere(position =>
                {
                    _rowVisited?.Invoke();
                    return !result.TryGetViewIndex(position, out _);
                });
                if (_anchor >= 0)
                {
                    _rowVisited?.Invoke();
                    if (!result.TryGetViewIndex(_anchor, out _)) _anchor = -1;
                }
            }
            if (result.DisplaySet?.IsBranchSet == true)
                _emptyBranches.RemoveWhere(path => !result.ContainsBranchPath(path));
            else _emptyBranches.Clear();
            _result = result;
            // Even unchanged membership may have a different order in a new result.
            _orderedPositions = null;
            _branchPaths = null;
            _lastBulkValid = false;
            if (Count != previousCount) NotifyChanged();
        }

        internal void Select(PanelQueryResult result, int position, bool toggle = false,
            bool range = false, bool preserveSelected = false)
        {
            Synchronize(result);
            _rowVisited?.Invoke();
            if (!result.TryGetViewIndex(position, out int index)) return;
            SelectAtIndex(result, position, index, toggle, range, preserveSelected);
        }

        // Painted hits carry their view index. Verify the pair in constant time so
        // stale or fabricated hits cannot select a different row.
        internal void SelectAt(PanelQueryResult result, int position, int index, bool toggle = false,
            bool range = false, bool preserveSelected = false)
        {
            Synchronize(result);
            _rowVisited?.Invoke();
            if (index < 0 || index >= result.RowPositions.Count || result.RowPositions[index] != position) return;
            SelectAtIndex(result, position, index, toggle, range, preserveSelected);
        }

        private void SelectAtIndex(PanelQueryResult result, int position, int index, bool toggle, bool range, bool preserveSelected)
        {
            int anchorIndex = -1;
            if (range && _anchor >= 0)
            {
                _rowVisited?.Invoke();
                if (!result.TryGetViewIndex(_anchor, out anchorIndex)) anchorIndex = -1;
            }
            if (range && anchorIndex >= 0)
            {
                if (toggle)
                {
                    bool added = false;
                    for (int i = Math.Min(index, anchorIndex); i <= Math.Max(index, anchorIndex); i++)
                    {
                        _rowVisited?.Invoke();
                        added |= _positions.Add(result.RowPositions[i]);
                    }
                    if (added) NotifyChanged();
                    return;
                }
                var next = new HashSet<int>();
                for (int i = Math.Min(index, anchorIndex); i <= Math.Max(index, anchorIndex); i++)
                {
                    _rowVisited?.Invoke();
                    next.Add(result.RowPositions[i]);
                }
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
            if (_orderedPositions != null) return _orderedPositions;
            if (_positions.Count == 0) return _orderedPositions = Array.Empty<int>();
            if (_positions.Count == result.RowPositions.Count) return _orderedPositions = result.RowPositions;

            var ordered = new List<int>(_positions.Count);
            if (_positions.Count > result.RowPositions.Count / 2)
            {
                // For dense membership, one linear pass is cheaper than sorting it.
                foreach (int position in result.RowPositions)
                {
                    _rowVisited?.Invoke();
                    if (_positions.Contains(position)) ordered.Add(position);
                }
            }
            else
            {
                var indices = new List<int>(_positions.Count);
                foreach (int position in _positions)
                {
                    _rowVisited?.Invoke();
                    if (result.TryGetViewIndex(position, out int index)) indices.Add(index);
                }
                indices.Sort();
                foreach (int index in indices)
                {
                    _rowVisited?.Invoke();
                    ordered.Add(result.RowPositions[index]);
                }
            }
            return _orderedPositions = Array.AsReadOnly(ordered.ToArray());
        }

        // The immutable list is trusted only while both its result and membership
        // still match. Old lists continue to be safe to read, but must be validated.
        internal bool IsCurrentOrderedPositions(PanelQueryResult result, IReadOnlyList<int> positions) =>
            ReferenceEquals(_result, result) && ReferenceEquals(_orderedPositions, positions);

        internal IReadOnlyList<string> BranchPaths(PanelQueryResult result)
        {
            Synchronize(result);
            return _branchPaths ??= _positions.Select(position =>
                {
                    _rowVisited?.Invoke();
                    return result.Snapshot.Rows[position].Path;
                }).Concat(_emptyBranches).Distinct(StringComparer.Ordinal).ToArray();
        }

        internal int[] Indices(PanelQueryResult result)
        {
            Synchronize(result);
            return _positions.Select(position =>
                {
                    _rowVisited?.Invoke();
                    return result.Snapshot.Rows[position].OriginalIndex;
                }).Distinct().OrderBy(index => index).ToArray();
        }

        internal void SelectIndices(PanelQueryResult result, IEnumerable<int> indices)
        {
            Synchronize(result);
            var wanted = new HashSet<int>(indices);
            var next = new HashSet<int>();
            int anchor = -1;
            foreach (int position in result.RowPositions)
            {
                _rowVisited?.Invoke();
                if (!wanted.Contains(result.Snapshot.Rows[position].OriginalIndex)) continue;
                next.Add(position);
                if (anchor < 0) anchor = position;
            }
            Replace(next, anchor);
        }

        internal void SelectAll(PanelQueryResult result, string? branchPath = null, bool includeEmptyBranches = false)
        {
            Synchronize(result);
            if (_lastBulkValid && _lastBulkPath == branchPath && _lastBulkIncludesEmpty == includeEmptyBranches)
            {
                _anchor = _lastBulkAnchor;
                return;
            }
            var next = new HashSet<int>();
            int anchor = -1;
            foreach (int position in result.RowPositions)
            {
                _rowVisited?.Invoke();
                if (branchPath != null && result.Snapshot.Rows[position].Path != branchPath) continue;
                next.Add(position);
                if (anchor < 0) anchor = position;
            }
            var empty = includeEmptyBranches && result.DisplaySet?.IsBranchSet == true
                ? new HashSet<string>(result.BranchPaths.Where(path => (branchPath == null || branchPath == path) && result.Snapshot.BranchItemCounts[path] == 0)) : null;
            Replace(next, anchor, empty);
            _lastBulkValid = true;
            _lastBulkPath = branchPath;
            _lastBulkIncludesEmpty = includeEmptyBranches;
            _lastBulkAnchor = anchor;
        }

        private void Replace(HashSet<int> next, int anchor, HashSet<string>? emptyBranches = null)
        {
            bool samePositions = _positions.Count == next.Count;
            if (samePositions)
                foreach (int position in next)
                {
                    _rowVisited?.Invoke();
                    if (!_positions.Contains(position)) { samePositions = false; break; }
                }
            bool changed = !samePositions || (emptyBranches == null ? _emptyBranches.Count > 0 : !_emptyBranches.SetEquals(emptyBranches));
            if (changed)
            {
                _positions.Clear();
                foreach (int position in next)
                {
                    _rowVisited?.Invoke();
                    _positions.Add(position);
                }
                _emptyBranches.Clear();
                if (emptyBranches != null) _emptyBranches.UnionWith(emptyBranches);
            }
            _anchor = anchor;
            if (changed) NotifyChanged();
        }
    }
}
