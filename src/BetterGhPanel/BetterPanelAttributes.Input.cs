using System;
using System.Linq;
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
        private const string KeyboardHint = "Click into the panel to focus it (green ring). While focused: ↑/↓ move the selection (Shift extends). " +
            "PgUp/PgDn/Home/End scroll. Ctrl/Cmd+C copies selected values. Ctrl/Cmd+A selects all. Ctrl/Cmd+F or / searches. Ctrl/Cmd+G jumps to a row. " +
            "The wheel scrolls rows (hold any modifier to zoom). Esc clears the selection, then releases focus. Everything else stays with Grasshopper.";
        private RectangleF _hoverBounds;
        private bool _hoverIsControl;
        private GH_Canvas? _hoverCanvas;
        private int _wheelRemainder;
        private int _keyboardCursor = -1;
        private bool _keyboardPaired, _copyPaired;
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

        // A forwarded menu click keeps focus only while its router is still visible,
        // so a stale flag cannot claim an unrelated later click.
        bool IPanelInputTarget.ContainsCanvasPoint(PointF point) => _menuClickForwarded && _menuRouter?.Visible == true ||
            _layout.Panel.Contains(point) || _setDetails.Bounds.Contains(point) || MenuContains(point);

        // Open menus may extend beyond the panel rectangle.
        private bool MenuContains(PointF point) => _menu.Kind != CanvasMenuKind.None && _menu.Bounds.Contains(point) ||
            _rowMenuResult != null && _rowMenuBounds.Contains(point);

        bool IPanelInputTarget.HandleWheel(GH_Canvas canvas, PointF point, int delta)
        {
            if (_menu.Kind != CanvasMenuKind.None && _menu.Bounds.Contains(point))
            {
                if (_menu.Scroll(-WheelRows(delta))) canvas.Invalidate();
                return true;
            }
            if (_rowMenuResult != null && _rowMenuBounds.Contains(point))
            {
                // A row menu taller than the viewport moves under the wheel, so every action stays reachable.
                var viewport = canvas.Viewport.VisibleRegion;
                float top = CanvasRowActions.ScrollMenu(_rowMenuBounds.Y, WheelRows(delta) * CanvasRowActions.RowHeight,
                    _rowMenuBounds.Height, viewport.Top, viewport.Bottom);
                if (top != _rowMenuBounds.Y)
                {
                    InvalidatePaintedHits(closeRowMenu: false);
                    _rowMenuPainted = false;
                    _rowMenuBounds.Y = top;
                    canvas.Invalidate();
                }
                return true;
            }
            if (!_layout.Panel.Contains(point)) return false;
            // A list without overflow lets the wheel zoom the canvas as usual.
            if (!ListContains(point) && !_d.TrackHit.Contains(point) && !_f.TrackHit.Contains(point) || MaximumOffset == 0)
            {
                _wheelRemainder = 0;
                return false;
            }
            _keyboardPaired = _f.List.Contains(point) || _f.TrackHit.Contains(point);
            bool paired = !_scrollLocked && _keyboardPaired;
            var pane = paired ? _f : _d;
            if (paired && !PanelOwner.CanSplit) return false;
            int rows = WheelRows(delta);
            if (rows != 0) SetPaneOffset(canvas, pane.Offset - rows, paired);
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
            bool paired = PanelOwner.PanelSplit && !_scrollLocked && PanelOwner.CanSplit && _keyboardPaired;
            var pane = paired ? _f : _d;
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
                    SetPaneOffset(canvas, pane.Offset + (key == Keys.PageUp ? -1 : 1) *
                        Math.Max(1, pane.Shown - 1), paired);
                    return true;
                case Keys.Home when none || control:
                    SetPaneOffset(canvas, 0, paired);
                    return true;
                case Keys.End when none || control:
                    SetPaneOffset(canvas, MaximumOffset, paired);
                    return true;
                case Keys.C when modifiers == (Keys.Control | Keys.Shift):
                    if (!current || _selection.Count == 0) return false;
                    CopySelectedValues(result, canvas, PanelOwner.PanelSplit && PanelOwner.CanSplit && _copyPaired, PanelRowAction.CopyPaths);
                    return true;
                case Keys.C when control:
                    // With no rows selected, Ctrl+C keeps copying Grasshopper objects.
                    if (!current || _selection.Count == 0) return false;
                    // Copy from the pane whose row was last clicked, whether or not scrolling is linked.
                    CopySelectedValues(result, canvas, PanelOwner.PanelSplit && PanelOwner.CanSplit && _copyPaired);
                    return true;
                case Keys.A when control:
                    if (current) SelectAllRows(canvas, result);
                    return true;
                case Keys.G when control:
                    // Nothing to jump in: leave Ctrl/Cmd+G to Grasshopper's Group.
                    if (current) ShowJumpEditor(canvas);
                    return current;
                case Keys.F when control:
                case Keys.OemQuestion when none:
                case Keys.Divide when none:
                    var options = PanelOwner.Session.Options;
                    if (CanvasMenu.HasValueChoices(options.SearchMode)) EditSearch(canvas);
                    else EditSearch(canvas, options.SearchMode, options.Search);
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
            bool paired = PanelOwner.PanelSplit && !_scrollLocked && PanelOwner.CanSplit && _keyboardPaired;
            var pane = paired ? _f : _d;
            if (DisplayListLengths) { SetPaneOffset(canvas, pane.Offset + step, paired); return; }
            int count = result.RowPositions.Count;
            if (count == 0) return;
            int index;
            if (_keyboardCursor >= 0 && result.TryGetViewIndex(_keyboardCursor, out int cursor)) index = cursor + step;
            else if (_selection.Anchor >= 0 && result.TryGetViewIndex(_selection.Anchor, out int anchor)) index = anchor + step;
            else index = paired ? ViewItemAtOrAfter(result, _f.Offset) :
                _firstShownItem >= 0 ? _firstShownItem : ViewItemAtOrAfter(result, _d.Offset);
            index = Math.Max(0, Math.Min(count - 1, index));
            int position = result.RowPositions[index];
            InvalidateCopyStatus();
            _selection.SelectAt(result, position, index, range: extend);
            _keyboardCursor = position;
            PanelOwner.HighlightRows(result.Snapshot, _selection.OrderedPositions(result));
            TrackSelectionCanvas(canvas);
            int offset = ViewOffset(result, index);
            // Read both panes' visibility before scrolling either: scrolling clears painted hits.
            bool shownInData = RowShownIn(_d.List, position), shownInPaired = RowShownIn(_f.List, position);
            if (paired) { if (!shownInPaired) RevealPartner(canvas, position, null, _d.List.Location); canvas.Invalidate(); }
            else if (_firstShownItem < 0 || index < _firstShownItem) SetOffset(canvas, offset);
            else if (index > _lastShownItem) SetOffset(canvas, offset - Math.Max(0, _d.Shown - 3));
            else canvas.Invalidate();
            if (paired ? !shownInData : !shownInPaired)
                RevealPartner(canvas, position, null, paired ? _f.List.Location : _d.List.Location);
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
            foreach (var entry in ControlHits.Where(entry => entry.HoverStep == ControlStep.Header))
                if (entry.Visible(ControlPath.Hover)) yield return entry.Rect(_layout);
            if (!_layout.Split.IsEmpty) yield return _layout.Split;
            if (PanelOwner.PanelSplit) yield return _layout.ScrollLock;
            foreach (var entry in ControlHits.Where(entry => entry.HoverStep == ControlStep.Main))
            {
                if (!entry.Visible(ControlPath.Hover)) continue;
                if (entry.HoverRects != null)
                    foreach (var bounds in entry.HoverRects()) yield return bounds;
                else yield return entry.Rect(_layout);
            }
        }

        private RectangleF HoverTargetAt(PointF point, out bool control)
        {
            control = true;
            if (PanelOwner.PanelMinimized) return ControlAt(ControlPath.Hover, point, ControlStep.Capsule)?.Rect(_layout) ?? RectangleF.Empty;
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

        // Upstream recomputes keep the scroll position when the new tree has the
        // same paths and branch lengths; otherwise the view starts at the top.
        // Selection is re-bound explicitly only after the same structure check.
        private PanelQueryResult? _selectionRestore;
        private PanelRowSelection? _savedSelection;

        internal void StashViewForRestore()
        {
            EndQueryPreview(_previewOwner);
            var result = _lastResult;
            // A second expiry before the next result keeps the original stash.
            if (result == null) return;
            _selectionRestore = result;
            _savedSelection = PanelOwner.CreateSetTree ? null : PanelOwner.CommittedSelection.CopyForPreview();
            _viewRestore = _d.Offset == 0 && _f.Offset == 0 ? null : new ViewRestore(result.Snapshot, _d.Offset, _f.Offset, DisplayListLengths);
        }

        private void RestoreView(PanelQueryResult result)
        {
            // A cancelled live edit can republish the stashed (old) snapshot; only the
            // replacement input may consume an upstream-recompute stash.
            if (_selectionRestore != null && ReferenceEquals(result.Snapshot, _selectionRestore.Snapshot)) return;
            bool? sameStructure = null;
            if (_selectionRestore != null)
            {
                sameStructure = PanelRowSelection.SameStructure(_selectionRestore.Snapshot, result.Snapshot);
                if (sameStructure == true && _savedSelection != null && !PanelOwner.CreateSetTree)
                    _selection.Rebind(_savedSelection, result);
                _selectionRestore = null;
                _savedSelection = null;
            }
            var restore = _viewRestore;
            if (restore == null) return;
            _viewRestore = null;
            if (restore.ListLengths != DisplayListLengths ||
                !(sameStructure ?? PanelRowSelection.SameStructure(restore.Snapshot, result.Snapshot))) return;
            _d.Offset = Math.Max(0, Math.Min(MaximumOffset, restore.Offset));
            _f.Offset = _scrollLocked ? _d.Offset : Math.Max(0, Math.Min(MaximumOffset, restore.PairedOffset));
        }

        private sealed class ViewRestore
        {
            internal ViewRestore(PanelSnapshot snapshot, int offset, int pairedOffset, bool listLengths)
            {
                Snapshot = snapshot; Offset = offset; PairedOffset = pairedOffset; ListLengths = listLengths;
            }
            internal PanelSnapshot Snapshot { get; }
            internal int Offset { get; }
            internal int PairedOffset { get; }
            internal bool ListLengths { get; }
        }
    }
}
#pragma warning restore CA1416
