using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper.GUI.Canvas;

// Grasshopper's canvas drawing and input contract is supplied by Rhino on Windows and Mac.
#pragma warning disable CA1416
namespace BetterGhPanel
{
    // Keyboard focus, wheel scrolling, hover feedback, transient notices and the
    // view restored after an upstream recompute. Focus is explicit: clicking into
    // the panel takes it and draws a focus ring; Esc, a click elsewhere, minimizing
    // or zooming out releases it. Without focus every key and wheel event belongs
    // to Grasshopper, exactly as for any other component.
    internal sealed partial class BetterPanelAttributes : IPanelInputTarget
    {
        private const int WheelUnitsPerRow = 40; // 120 per notch → three rows.
        private const string KeyboardHint = "Click into the panel to focus it (green ring). While focused: ↑/↓ move the selection (Shift extends), " +
            "PgUp/PgDn/Home/End scroll, Ctrl/Cmd+C copies selected values, Ctrl/Cmd+A selects all, Ctrl/Cmd+F or / searches, the wheel scrolls rows " +
            "(hold any modifier to zoom), Esc releases. Everything else stays with Grasshopper.";
        private RectangleF _hoverBounds;
        private bool _hoverIsControl;
        private GH_Canvas? _hoverCanvas;
        private int _wheelRemainder;
        private int _keyboardCursor = -1;
        private ViewRestore? _viewRestore;

        private bool KeyboardFocused => PanelCanvasInput.IsFocused(this);

        private void FocusPanel(GH_Canvas canvas)
        {
            if (PanelOwner.PanelMinimized || PanelOwner.Locked || KeyboardFocused) return;
            PanelCanvasInput.Focus(this, canvas);
            canvas.Invalidate();
        }

        internal void ReleaseKeyboardFocus() => PanelCanvasInput.Release(this);

        void IPanelInputTarget.FocusReleased()
        {
            _keyboardCursor = -1;
            _wheelRemainder = 0;
            if (_menu.Kind == CanvasMenuKind.Branch && _menu.BranchFilter.Length > 0) _menu.SetBranchFilter(string.Empty);
            Grasshopper.Instances.ActiveCanvas?.Invalidate();
        }

        bool IPanelInputTarget.OwnsCanvas(GH_Canvas canvas) => canvas.Document != null &&
            ReferenceEquals(canvas.Document, Owner.OnPingDocument()) && !PanelOwner.PanelMinimized && !PanelOwner.Locked &&
            canvas.Viewport.Zoom >= 0.5f;

        bool IPanelInputTarget.ContainsCanvasPoint(PointF point) => _panel.Contains(point) || _setDetails.Bounds.Contains(point);

        bool IPanelInputTarget.HandleWheel(GH_Canvas canvas, PointF point, int delta)
        {
            if (!_panel.Contains(point)) return false;
            if (_menu.Kind != CanvasMenuKind.None && _menu.Bounds.Contains(point))
            {
                if (_menu.Scroll(-WheelRows(delta))) canvas.Invalidate();
                return true;
            }
            if (_rowMenuResult != null && _rowMenuBounds.Contains(point)) return true;
            // A list without overflow lets the wheel zoom the canvas as usual.
            if (!_list.Contains(point) && !_trackHit.Contains(point) || MaximumOffset == 0)
            {
                _wheelRemainder = 0;
                return false;
            }
            int rows = WheelRows(delta);
            if (rows != 0) SetOffset(canvas, _offset - rows);
            return true;
        }

        private int WheelRows(int delta)
        {
            _wheelRemainder += delta;
            int rows = _wheelRemainder / WheelUnitsPerRow;
            _wheelRemainder -= rows * WheelUnitsPerRow;
            return rows;
        }

        bool IPanelInputTarget.HandleKey(GH_Canvas canvas, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            var modifiers = keyData & Keys.Modifiers;
            bool none = modifiers == Keys.None, shift = modifiers == Keys.Shift, control = modifiers == Keys.Control;
            if (_rowMenuResult != null)
            {
                if (key != Keys.Escape || !none) return false;
                CloseRowMenu();
                InvalidatePaintedHits();
                canvas.Invalidate();
                return true;
            }
            if (_menu.Kind != CanvasMenuKind.None) return HandleMenuKey(canvas, key, none);
            var result = PanelOwner.Session.Result;
            bool current = ResultIsCurrent(result);
            switch (key)
            {
                case Keys.Escape when none:
                    if (_selection.Count > 0) ClearRowSelection(canvas);
                    else ReleaseKeyboardFocus();
                    return true;
                case Keys.Up when none || shift:
                case Keys.Down when none || shift:
                    if (current) MoveKeyboardCursor(canvas, result, key == Keys.Up ? -1 : 1, shift);
                    return true;
                case Keys.PageUp when none:
                case Keys.PageDown when none:
                    SetOffset(canvas, _offset + (key == Keys.PageUp ? -1 : 1) * Math.Max(1, _shown - 1));
                    return true;
                case Keys.Home when none || control:
                    SetOffset(canvas, 0);
                    return true;
                case Keys.End when none || control:
                    SetOffset(canvas, MaximumOffset);
                    return true;
                case Keys.C when control:
                    // With no rows selected, Ctrl+C keeps copying Grasshopper objects.
                    if (!current || _selection.Count == 0 || DisplayListLengths) return false;
                    CopySelectedValues(result, canvas);
                    return true;
                case Keys.A when control:
                    if (current) SelectAllRows(canvas, result);
                    return true;
                case Keys.F when control:
                case Keys.OemQuestion when none:
                case Keys.Divide when none:
                    EditSearch(canvas);
                    return true;
            }
            return false;
        }

        private bool HandleMenuKey(GH_Canvas canvas, Keys key, bool none)
        {
            if (!none) return false;
            switch (key)
            {
                case Keys.Escape:
                    _menu.Close();
                    InvalidatePaintedHits();
                    canvas.Invalidate();
                    return true;
                case Keys.Up:
                case Keys.Down:
                    _menu.MoveHover(key == Keys.Up ? -1 : 1);
                    canvas.Invalidate();
                    return true;
                case Keys.PageUp:
                case Keys.PageDown:
                    _menu.Scroll((key == Keys.PageUp ? -1 : 1) * Math.Max(1, _menu.Visible));
                    canvas.Invalidate();
                    return true;
                case Keys.Enter:
                    if (_menu.HoveredIndex >= 0 && _menu.IsCurrent(PanelOwner.Session.Snapshot))
                    {
                        ChooseMenu(_menu.HoveredIndex, canvas);
                        if (_menu.Kind == CanvasMenuKind.None) InvalidatePaintedHits();
                    }
                    canvas.Invalidate();
                    return true;
            }
            if (_menu.Kind != CanvasMenuKind.Branch) return false;
            string filter = _menu.BranchFilter;
            if (key == Keys.Back)
            {
                if (filter.Length > 0) _menu.SetBranchFilter(filter.Substring(0, filter.Length - 1));
                canvas.Invalidate();
                return true;
            }
            char? character = key >= Keys.D0 && key <= Keys.D9 ? (char)('0' + (key - Keys.D0)) :
                key >= Keys.NumPad0 && key <= Keys.NumPad9 ? (char)('0' + (key - Keys.NumPad0)) :
                key == Keys.OemSemicolon ? ';' : (char?)null;
            if (!character.HasValue) return false;
            _menu.SetBranchFilter(filter + character.Value);
            canvas.Invalidate();
            return true;
        }

        private void MoveKeyboardCursor(GH_Canvas canvas, PanelQueryResult result, int step, bool extend)
        {
            if (DisplayListLengths) { SetOffset(canvas, _offset + step); return; }
            int count = result.RowPositions.Count;
            if (count == 0) return;
            int index;
            if (_keyboardCursor >= 0 && result.TryGetViewIndex(_keyboardCursor, out int cursor)) index = cursor + step;
            else if (_selection.Anchor >= 0 && result.TryGetViewIndex(_selection.Anchor, out int anchor)) index = anchor + step;
            else index = Math.Max(0, _firstShownItem);
            index = Math.Max(0, Math.Min(count - 1, index));
            int position = result.RowPositions[index];
            InvalidateCopyStatus();
            _selection.SelectAt(result, position, index, range: extend);
            _keyboardCursor = position;
            PanelOwner.HighlightRows(result.Snapshot, _selection.OrderedPositions(result));
            TrackSelectionCanvas(canvas);
            int offset = result.ViewIndex.OffsetOfItem(index);
            if (_firstShownItem < 0 || index < _firstShownItem) SetOffset(canvas, offset);
            else if (index > _lastShownItem) SetOffset(canvas, offset - Math.Max(0, _shown - 3));
            else canvas.Invalidate();
        }

        private void SelectAllRows(GH_Canvas canvas, PanelQueryResult result)
        {
            InvalidateCopyStatus();
            _selection.SelectAll(result, includeEmptyBranches: DisplayListLengths);
            PanelOwner.HighlightRows(result.Snapshot, _selection.OrderedPositions(result));
            TrackSelectionCanvas(canvas);
            canvas.Invalidate();
        }

        private void ShowNotice(GH_Canvas canvas, string text)
        {
            InvalidateCopyStatus();
            _actionStatus = text;
            _copiedUntil = DateTime.UtcNow.AddSeconds(6);
            canvas.Invalidate();
        }

        // Hover feedback: a translucent fill on the control or row under the pointer.
        private IEnumerable<RectangleF> HoverControls()
        {
            yield return _close; yield return _display; yield return _jump; yield return _reset; yield return _preview;
            if (SearchClearVisible) yield return _searchClear;
            yield return _filter; yield return _search;
            if (BranchClearVisible) yield return _branchClear;
            yield return _type; yield return _sort; yield return _branch;
            yield return _selectAll; yield return _statistics;
            yield return _trim; yield return _simplify; yield return _flatten; yield return _reverse; yield return _createSet; yield return _units;
        }

        private RectangleF HoverTargetAt(PointF point, out bool control)
        {
            control = true;
            if (PanelOwner.PanelMinimized) return _restore.Contains(point) ? _restore : RectangleF.Empty;
            control = false;
            if (_menu.Kind != CanvasMenuKind.None || _rowMenuResult != null || _resizing || _scrolling) return RectangleF.Empty;
            foreach (var bounds in HoverControls())
                if (!bounds.IsEmpty && bounds.Contains(point)) { control = true; return bounds; }
            if (!RowsAreCurrent(PanelOwner.Session.Result)) return RectangleF.Empty;
            foreach (var hit in _branchHits) if (hit.Bounds.Contains(point)) return hit.Bounds;
            foreach (var hit in _rowHits) if (hit.Bounds.Contains(point)) return hit.Bounds;
            return RectangleF.Empty;
        }

        // Returns true while the pointer is over a clickable control.
        private bool UpdateHover(GH_Canvas canvas, PointF point)
        {
            var bounds = HoverTargetAt(point, out bool control);
            if (bounds != _hoverBounds)
            {
                _hoverBounds = bounds;
                if (!bounds.IsEmpty) TrackHoverCanvas(canvas);
                canvas.Invalidate();
            }
            _hoverIsControl = control && !bounds.IsEmpty;
            return _hoverIsControl;
        }

        private void RenderHover(Graphics g)
        {
            if (_hoverBounds.IsEmpty || _menu.Kind != CanvasMenuKind.None || _rowMenuResult != null) return;
            if (!_hoverIsControl && !RowsAreCurrent(PanelOwner.Session.Result)) return;
            Fill(g, PanelTheme.Hover, _hoverBounds);
        }

        private void TrackHoverCanvas(GH_Canvas canvas)
        {
            if (ReferenceEquals(_hoverCanvas, canvas)) return;
            DetachHoverCanvas();
            _hoverCanvas = canvas;
            canvas.MouseMove += HoverCanvasMouseMove;
            canvas.MouseLeave += HoverCanvasMouseLeave;
        }

        private void DetachHoverCanvas()
        {
            if (_hoverCanvas == null) return;
            _hoverCanvas.MouseMove -= HoverCanvasMouseMove;
            _hoverCanvas.MouseLeave -= HoverCanvasMouseLeave;
            _hoverCanvas = null;
        }

        // The host stops reporting moves once the pointer leaves this component.
        private void HoverCanvasMouseMove(object? sender, MouseEventArgs e)
        {
            if (sender is GH_Canvas canvas && _hoverBounds.Contains(canvas.Viewport.UnprojectPoint(e.Location))) return;
            ClearHover();
        }

        private void HoverCanvasMouseLeave(object? sender, EventArgs e) => ClearHover();

        private void ClearHover()
        {
            var canvas = _hoverCanvas;
            bool had = !_hoverBounds.IsEmpty;
            _hoverBounds = RectangleF.Empty;
            _hoverIsControl = false;
            DetachHoverCanvas();
            if (had) canvas?.Invalidate();
        }

        // Upstream recomputes keep selection and scroll position when the new tree
        // has the same paths and branch lengths; otherwise the view starts fresh.
        internal void StashViewForRestore()
        {
            var result = _lastResult;
            // A second expiry before the next result keeps the original stash.
            if (result == null) return;
            _viewRestore = _selection.Count == 0 && _offset == 0 ? null : new ViewRestore(result.Snapshot,
                _selection.PositionsSnapshot(), _selection.EmptyBranchesSnapshot(), _selection.Anchor, _offset, DisplayListLengths);
        }

        private void RestoreView(PanelQueryResult result)
        {
            var restore = _viewRestore;
            if (restore == null) return;
            _viewRestore = null;
            if (restore.ListLengths != DisplayListLengths || !SameStructure(restore.Snapshot, result.Snapshot)) return;
            _selection.Restore(result, restore.Positions, restore.EmptyBranches, restore.Anchor);
            _offset = restore.Offset;
        }

        private static bool SameStructure(PanelSnapshot before, PanelSnapshot after)
        {
            if (before.Rows.Count != after.Rows.Count || before.Branches.Count != after.Branches.Count) return false;
            for (int i = 0; i < before.Branches.Count; i++)
                if (before.Branches[i].Path != after.Branches[i].Path || before.Branches[i].ItemCount != after.Branches[i].ItemCount) return false;
            return true;
        }

        private sealed class ViewRestore
        {
            internal ViewRestore(PanelSnapshot snapshot, int[] positions, string[] emptyBranches, int anchor, int offset, bool listLengths)
            {
                Snapshot = snapshot; Positions = positions; EmptyBranches = emptyBranches; Anchor = anchor; Offset = offset; ListLengths = listLengths;
            }
            internal PanelSnapshot Snapshot { get; }
            internal int[] Positions { get; }
            internal string[] EmptyBranches { get; }
            internal int Anchor { get; }
            internal int Offset { get; }
            internal bool ListLengths { get; }
        }
    }
}
#pragma warning restore CA1416
