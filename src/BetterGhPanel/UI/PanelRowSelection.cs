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
        private IndexNode? _index;
        private long _version, _singleFrom = -1;
        private int _singlePosition;
        private bool _singleSelected;
        private int _anchor = -1;
        private bool _lastBulkValid;
        private string? _lastBulkPath;
        private bool _lastBulkIncludesEmpty;
        private int _lastBulkAnchor;

        // The optional observer counts selection-path row visits in managed tests.
        internal PanelRowSelection(Action? rowVisited = null) => _rowVisited = rowVisited;

        internal int Count => _positions.Count + _emptyBranches.Count;
        internal bool Contains(int position) => _positions.Contains(position);
        internal long Version => _version;
        internal event EventHandler? Changed;

        private void NotifyChanged(int? position = null, bool selected = false)
        {
            _singleFrom = position.HasValue ? _version : -1;
            if (position.HasValue) { _singlePosition = position.Value; _singleSelected = selected; }
            _version++;
            _orderedPositions = null;
            _branchPaths = null;
            _lastBulkValid = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        internal bool TryGetSingleChange(PanelQueryResult result, long from, out int position, out bool selected)
        {
            position = _singlePosition; selected = _singleSelected;
            return ReferenceEquals(_result, result) && from == _singleFrom && _version == from + 1;
        }

        internal void Clear()
        {
            bool changed = Count > 0;
            _positions.Clear();
            _index = null;
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
            RebuildIndex(result);
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
                        if (_positions.Add(result.RowPositions[i]))
                        { added = true; _index = IndexNode.Set(_index, 0, result.RowPositions.Count, i, true); }
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
            bool single = false, nowSelected = false;
            if (toggle)
            {
                nowSelected = !_positions.Remove(position);
                if (nowSelected) _positions.Add(position);
                _index = IndexNode.Set(_index, 0, result.RowPositions.Count, index, nowSelected);
                single = true;
                changed = true;
            }
            else if (!preserveSelected || !_positions.Contains(position))
            {
                changed = _positions.Count != 1 || !_positions.Contains(position);
                single = _positions.Count == 0;
                nowSelected = true;
                _positions.Clear();
                _positions.Add(position);
                _index = IndexNode.Set(null, 0, result.RowPositions.Count, index, true);
            }
            _anchor = position;
            if (changed) NotifyChanged(single ? position : (int?)null, nowSelected);
        }

        internal IReadOnlyList<int> OrderedPositions(PanelQueryResult result)
        {
            Synchronize(result);
            if (_orderedPositions != null) return _orderedPositions;
            if (_positions.Count == 0) return _orderedPositions = Array.Empty<int>();
            if (_positions.Count == result.RowPositions.Count) return _orderedPositions = result.RowPositions;
            return _orderedPositions = new OrderedSelection(result.RowPositions, _index!, _positions.Count, _rowVisited);
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
                RebuildIndex(_result!);
                _emptyBranches.Clear();
                if (emptyBranches != null) _emptyBranches.UnionWith(emptyBranches);
            }
            _anchor = anchor;
            if (changed) NotifyChanged();
        }

        private void RebuildIndex(PanelQueryResult result)
        {
            int size = result.RowPositions.Count;
            if (_positions.Count == 0) { _index = null; return; }
            if (_positions.Count == size) { _index = IndexNode.Full; return; }
            if (_positions.Count > size / 4)
            {
                _index = IndexNode.Build(0, size, i => { _rowVisited?.Invoke(); return _positions.Contains(result.RowPositions[i]); });
                return;
            }
            _index = null;
            foreach (int position in _positions)
            {
                _rowVisited?.Invoke();
                if (result.TryGetViewIndex(position, out int index)) _index = IndexNode.Set(_index, 0, size, index, true);
            }
        }

        // Immutable compressed binary index. Full subtrees are shared; a single
        // toggle creates O(log result size) nodes and old action lists stay stable.
        private sealed class IndexNode
        {
            internal static readonly IndexNode Full = new IndexNode(-1, null, null);
            private IndexNode(int count, IndexNode? left, IndexNode? right) { Count = count; Left = left; Right = right; }
            private int Count { get; }
            private IndexNode? Left { get; }
            private IndexNode? Right { get; }
            private static int Size(IndexNode? node, int width) => node == null ? 0 : ReferenceEquals(node, Full) ? width : node.Count;
            private static IndexNode? Join(IndexNode? left, IndexNode? right, int leftWidth, int rightWidth)
            {
                if (left == null && right == null) return null;
                if (ReferenceEquals(left, Full) && ReferenceEquals(right, Full)) return Full;
                return new IndexNode(Size(left, leftWidth) + Size(right, rightWidth), left, right);
            }
            internal static IndexNode? Set(IndexNode? node, int start, int end, int index, bool selected)
            {
                if ((node == null && !selected) || (ReferenceEquals(node, Full) && selected)) return node;
                if (end - start == 1) return selected ? Full : null;
                int mid = start + (end - start) / 2;
                var left = ReferenceEquals(node, Full) ? Full : node?.Left;
                var right = ReferenceEquals(node, Full) ? Full : node?.Right;
                if (index < mid) left = Set(left, start, mid, index, selected);
                else right = Set(right, mid, end, index, selected);
                return Join(left, right, mid - start, end - mid);
            }
            internal static IndexNode? Build(int start, int end, Func<int, bool> selected)
            {
                if (end == start) return null;
                if (end - start == 1) return selected(start) ? Full : null;
                int mid = start + (end - start) / 2;
                return Join(Build(start, mid, selected), Build(mid, end, selected), mid - start, end - mid);
            }
            internal static int At(IndexNode node, int start, int end, int rank)
            {
                if (ReferenceEquals(node, Full)) return start + rank;
                int mid = start + (end - start) / 2;
                int leftCount = Size(node.Left, mid - start);
                return rank < leftCount ? At(node.Left!, start, mid, rank) : At(node.Right!, mid, end, rank - leftCount);
            }
            internal static IEnumerable<int> Enumerate(IndexNode? node, int start, int end)
            {
                if (node == null) yield break;
                if (ReferenceEquals(node, Full)) { for (int i = start; i < end; i++) yield return i; yield break; }
                int mid = start + (end - start) / 2;
                foreach (int i in Enumerate(node.Left, start, mid)) yield return i;
                foreach (int i in Enumerate(node.Right, mid, end)) yield return i;
            }
        }

        private sealed class OrderedSelection : IReadOnlyList<int>, IList<int>
        {
            private readonly IReadOnlyList<int> _rows;
            private readonly IndexNode _index;
            private readonly Action? _visit;
            internal OrderedSelection(IReadOnlyList<int> rows, IndexNode index, int count, Action? visit)
            { _rows = rows; _index = index; Count = count; _visit = visit; }
            public int Count { get; }
            public int this[int index] => index < 0 || index >= Count ? throw new ArgumentOutOfRangeException(nameof(index)) :
                _rows[IndexNode.At(_index, 0, _rows.Count, index)];
            int IList<int>.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
            bool ICollection<int>.IsReadOnly => true;
            void ICollection<int>.Add(int value) => throw new NotSupportedException();
            void ICollection<int>.Clear() => throw new NotSupportedException();
            bool ICollection<int>.Remove(int value) => throw new NotSupportedException();
            void IList<int>.Insert(int index, int value) => throw new NotSupportedException();
            void IList<int>.RemoveAt(int index) => throw new NotSupportedException();
            int IList<int>.IndexOf(int value) { int index = 0; foreach (int item in this) { if (item == value) return index; index++; } return -1; }
            bool ICollection<int>.Contains(int value) => ((IList<int>)this).IndexOf(value) >= 0;
            void ICollection<int>.CopyTo(int[] array, int index) { foreach (int item in this) array[index++] = item; }
            public IEnumerator<int> GetEnumerator()
            {
                foreach (int i in IndexNode.Enumerate(_index, 0, _rows.Count)) { _visit?.Invoke(); yield return _rows[i]; }
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
