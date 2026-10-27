using System;
using System.Collections.Generic;
using BetterGhPanel.Core;

namespace BetterGhPanel.UI
{
    // Selection classification is independent of the size of the preview
    // result. Updating it only visits old and new selected positions.
    internal sealed class PanelPreviewMembership
    {
        private readonly HashSet<int> _selected = new HashSet<int>();
        private readonly Action? _visit;
        private PanelRowSelection? _selection;
        private PanelQueryResult? _result;
        private long _version;
        internal PanelPreviewMembership(Action? visit = null) { _visit = visit; }
        internal bool Contains(int position) => _selected.Contains(position);
        internal int Count => _selected.Count;

        internal void Update(IReadOnlyList<int> positions, Action<int, bool>? changed = null)
        {
            _selection = null; _result = null;
            var next = new HashSet<int>();
            foreach (int position in positions) { _visit?.Invoke(); next.Add(position); }
            foreach (int position in _selected)
            {
                _visit?.Invoke();
                if (!next.Contains(position)) changed?.Invoke(position, false);
            }
            foreach (int position in next)
            {
                _visit?.Invoke();
                if (!_selected.Contains(position)) changed?.Invoke(position, true);
            }
            _selected.Clear();
            _selected.UnionWith(next);
        }

        internal SelectionUpdate UpdateSelection(PanelRowSelection? selection, PanelQueryResult result,
            IReadOnlyList<int> positions, Action<int, bool>? changed = null)
        {
            if (selection != null && selection.IsCurrentOrderedPositions(result, positions))
            {
                if (ReferenceEquals(_selection, selection) && ReferenceEquals(_result, result))
                {
                    if (_version == selection.Version) return new SelectionUpdate(false, -1);
                    if (selection.TryGetSingleChange(result, _version, out int position, out bool selected))
                    {
                        _visit?.Invoke();
                        if (_selected.Contains(position) != selected)
                        {
                            changed?.Invoke(position, selected);
                            if (selected) _selected.Add(position); else _selected.Remove(position);
                        }
                        _version = selection.Version;
                        return new SelectionUpdate(false, selected ? position : -1);
                    }
                }
                Update(positions, changed);
                _selection = selection; _result = result; _version = selection.Version;
                return new SelectionUpdate(true, -1);
            }
            Update(positions, changed);
            return new SelectionUpdate(true, -1);
        }

        internal readonly struct SelectionUpdate
        {
            internal SelectionUpdate(bool scanSelection, int addedPosition) { ScanSelection = scanSelection; AddedPosition = addedPosition; }
            internal bool ScanSelection { get; }
            internal int AddedPosition { get; }
        }

        internal void Clear() { _selected.Clear(); _selection = null; _result = null; }
    }
}
