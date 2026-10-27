using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;

#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private PanelQueryResult? _rowMenuResult;
        private RectangleF _rowMenuBounds;
        private int _rowMenuHovered = -1;
        private CanvasRowAction[] _rowMenuActions = Array.Empty<CanvasRowAction>();
        private CanvasRowAction[] _rowMenuPendingActions = Array.Empty<CanvasRowAction>();
        private CanvasRowAction[] _rowMenuAllActions = Array.Empty<CanvasRowAction>();
        private PointF _rowMenuAnchor;
        private int _rowMenuSourcePosition = -1;
        private bool _rowMenuPaged;
        private DateTime _copiedUntil;
        private DateTime _rowActionDismissedUntil;
        private string _actionStatus = string.Empty;
        private bool _copyPending;
        private long _actionGeneration;
        private bool _rowContextHandled;
        private bool _rowMenuPainted;

        private void CloseRowMenu()
        {
            bool coveredRows = _rowMenuResult != null && _rowMenuPainted;
            if (coveredRows) _staleRowMenu = _rowMenuBounds;
            _rowMenuResult = null;
            _rowMenuHovered = -1;
            _rowMenuActions = Array.Empty<CanvasRowAction>();
            _rowMenuPendingActions = Array.Empty<CanvasRowAction>();
            _rowMenuPainted = false;
            if (coveredRows) InvalidatePaintedHits(closeRowMenu: false);
        }

        private void SuppressTrailingDoubleClick() =>
            _rowActionDismissedUntil = DateTime.UtcNow.AddMilliseconds(SystemInformation.DoubleClickTime);

        private void RenderRowMenu(Graphics g, Font font, StringFormat trim)
        {
            if (_rowMenuResult == null || !ResultIsCurrent(_rowMenuResult)) return;
            Fill(g, Color.FromArgb(32, 49, 55, 53), new RectangleF(_rowMenuBounds.X + 1, _rowMenuBounds.Y + 2, _rowMenuBounds.Width, _rowMenuBounds.Height));
            Fill(g, Color.White, _rowMenuBounds);
            var actions = _rowMenuPendingActions;
            if (!_preservePaintedTargets)
                _rowMenuActions = Enumerable.Repeat(new CanvasRowAction(PanelRowAction.Unpainted, "", ""), actions.Length).ToArray();
            for (int i = 0; i < actions.Length; i++)
            {
                var bounds = new RectangleF(_rowMenuBounds.X + 1, _rowMenuBounds.Y + i * CanvasRowActions.RowHeight,
                    _rowMenuBounds.Width - 2, CanvasRowActions.RowHeight);
                if (i == _rowMenuHovered) Fill(g, Color.FromArgb(226, 236, 226), bounds);
                Text(g, actions[i].Label, font, Ink, new RectangleF(bounds.X + 8, bounds.Y, bounds.Width - 16, bounds.Height), trim);
                // Partial invalidations must not activate a row that has not
                // actually been painted at its new position.
                var target = new RectangleF(_rowMenuBounds.X, bounds.Y, _rowMenuBounds.Width, bounds.Height);
                if (TargetPainted(g, target)) _rowMenuActions[i] = actions[i];
            }
            using (var pen = new Pen(Color.FromArgb(194, 205, 194)))
                g.DrawRectangle(pen, _rowMenuBounds.X, _rowMenuBounds.Y, _rowMenuBounds.Width, _rowMenuBounds.Height);
            _rowMenuPainted = _rowMenuActions.Any(action => action.Action != PanelRowAction.Unpainted);
        }

        private bool RowMenuTargetPainted(PointF point)
        {
            int index = (int)((point.Y - _rowMenuBounds.Y) / CanvasRowActions.RowHeight);
            return _rowMenuPainted && _rowMenuBounds.Contains(point) && index >= 0 && index < _rowMenuActions.Length &&
                _rowMenuActions[index].Action != PanelRowAction.Unpainted;
        }

        private static bool FullyVisible(Graphics graphics, RectangleF bounds)
        {
            if (!graphics.VisibleClipBounds.Contains(bounds)) return false;
            using (var unpainted = new Region(bounds))
            using (var clip = graphics.Clip)
            {
                unpainted.Exclude(clip);
                return unpainted.IsEmpty(graphics);
            }
        }

        private GH_ObjectResponse RespondToRowContextMenu(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            _rowContextHandled = false;
            if (_rowMenuResult != null || _menu.Kind != CanvasMenuKind.None)
            {
                _rowContextHandled = true;
                CloseRowMenu();
                _menu.Close();
                InvalidatePaintedHits();
                canvas.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            var result = PanelOwner.Session.Result;
            if (_list.Contains(e.CanvasLocation) && !RowsAreCurrent(result))
            {
                _rowContextHandled = true;
                _rowHits.Clear();
                canvas.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            foreach (var hit in _rowHits)
            {
                if (!hit.Bounds.Contains(e.CanvasLocation)) continue;
                _rowContextHandled = true;
                InvalidateCopyStatus();
                _selection.SelectAt(result, hit.SourcePosition, hit.ViewNumber - 1, preserveSelected: true);
                PanelOwner.HighlightRows(result.Snapshot, _selection.OrderedPositions(result));
                TrackSelectionCanvas(canvas);
                var options = PanelOwner.Session.Options;
                bool filtered = options.Search.Length > 0 || options.TypeName != null || options.OnlyInvalid || options.BranchPath != null;
                _rowMenuAllActions = CanvasRowActions.Create(_selection.Count, _selection.Indices(result), filtered);
                _rowMenuAnchor = e.CanvasLocation;
                _rowMenuSourcePosition = hit.SourcePosition;
                _rowMenuPaged = CanvasRowActions.NeedsPages(_panel.Height - HeaderHeight - 6);
                ShowRowMenu(result, 0);
                canvas.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            return base.RespondToMouseDown(canvas, e);
        }

        private void ShowRowMenu(PanelQueryResult result, int page)
        {
            _rowMenuResult = result;
            _rowMenuPainted = false;
            _rowMenuHovered = -1;
            _rowMenuPendingActions = _rowMenuPaged ? CanvasRowActions.Page(_rowMenuAllActions, page) : _rowMenuAllActions;
            _rowMenuActions = Enumerable.Repeat(new CanvasRowAction(PanelRowAction.Unpainted, "", ""), _rowMenuPendingActions.Length).ToArray();
            float width = Math.Min(300, _panel.Width - 28);
            float height = _rowMenuPendingActions.Length * CanvasRowActions.RowHeight;
            _rowMenuBounds = new RectangleF(
                Math.Max(_panel.Left + 14, Math.Min(_rowMenuAnchor.X, _panel.Right - width - 14)),
                Math.Max(_panel.Top + HeaderHeight, Math.Min(_rowMenuAnchor.Y, _panel.Bottom - height - 6)), width, height);
        }

        private void ChooseRowAction(PanelRowAction action, PanelQueryResult result, GH_Canvas canvas)
        {
            var positions = _selection.OrderedPositions(result);
            if (positions.Count == 0) return;
            if (action == PanelRowAction.NextPage || action == PanelRowAction.PreviousPage)
            {
                if (_rowMenuPaged) ShowRowMenu(result, action == PanelRowAction.NextPage ? 1 : 0);
            }
            else if (action == PanelRowAction.ViewFullValue)
            {
                int position = _rowMenuSourcePosition;
                if (position < 0 || position >= result.Snapshot.Rows.Count || !positions.Contains(position)) return;
                var row = result.Snapshot.Rows[position];
                bool projected = result.DisplaySet?.Values.ContainsKey(position) == true;
                string value = !projected && !PanelOwner.Session.Options.DisplayFilterValue && row.StringValue != null
                    ? row.StringValue : PanelOwner.DisplayValue(result.Snapshot, row, position);
                try { PanelFullValueWindow.Show(value); }
                catch (Exception)
                {
                    _actionStatus = "Could not open the full value.";
                    _copiedUntil = DateTime.UtcNow.AddSeconds(5);
                }
            }
            else if (action == PanelRowAction.FrameSelection)
            {
                InvalidateCopyStatus();
                _actionStatus = PanelOwner.FrameRows(result.Snapshot, positions) ?? "Selection framed";
                _copiedUntil = DateTime.UtcNow.AddSeconds(5);
            }
            else if (action == PanelRowAction.CopyValues) CopySelectedValues(result, canvas);
            else if (action == PanelRowAction.CopyGeometry)
            {
                InvalidateCopyStatus();
                PanelOwner.TryCopyGeometry(result.Snapshot, positions, out var status);
                _actionStatus = status;
                _copiedUntil = DateTime.UtcNow.AddSeconds(5);
            }
            else if (action == PanelRowAction.SelectIndices) SelectAcrossBranches(result, _selection.Indices(result), canvas);
        }

        private void SelectAcrossBranches(PanelQueryResult result, int[] indices, GH_Canvas canvas)
        {
            InvalidateCopyStatus();
            _selection.SelectIndices(result, indices);
            var positions = _selection.OrderedPositions(result);
            PanelOwner.HighlightRows(result.Snapshot, positions);
            TrackSelectionCanvas(canvas);
            int branches = positions.Select(position => result.Snapshot.Rows[position].Path).Distinct().Count();
            _actionStatus = $"{CanvasRowActions.IndexLabel(indices)} · {_selection.Count:N0} selected · {branches:N0} branches";
            _copiedUntil = DateTime.UtcNow.AddSeconds(5);
        }

        private void InvalidateCopyStatus()
        {
            _actionGeneration++;
            PanelOwner.CancelPendingCopy();
            _copyPending = false;
            _copiedUntil = default;
            _actionStatus = string.Empty;
        }

        private async void CopySelectedValues(PanelQueryResult result, GH_Canvas canvas)
        {
            var positions = _selection.OrderedPositions(result);
            if (positions.Count == 0) return;
            InvalidateCopyStatus();
            long generation = _actionGeneration;
            _copyPending = true;
            _actionStatus = "Copying values…";
            canvas.Invalidate();
            string status;
            try { status = await PanelOwner.CopyValuesAsync(result.Snapshot, positions); }
            catch (Exception) { status = "Could not copy values. Try again."; }
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (generation != _actionGeneration || canvas.IsDisposed || !ResultIsCurrent(result) ||
                    !positions.SequenceEqual(_selection.OrderedPositions(result))) return;
                _copyPending = false;
                _actionStatus = status;
                _copiedUntil = DateTime.UtcNow.AddSeconds(5);
                canvas.Invalidate();
            }));
        }

        private bool RowActionTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            if (_rowMenuResult == null || !_rowMenuPainted || !_rowMenuBounds.Contains(point)) return false;
            int index = (int)((point.Y - _rowMenuBounds.Y) / CanvasRowActions.RowHeight);
            if (RowsAreCurrent(_rowMenuResult) && index >= 0 && index < _rowMenuActions.Length &&
                _rowMenuActions[index].Action != PanelRowAction.Unpainted)
            {
                var action = _rowMenuActions[index];
                e.Title = action.Label;
                e.Text = action.Action == PanelRowAction.ViewFullValue ? "The row you right-clicked" : $"{_selection.Count:N0} selected";
                e.Description = PanelTextPreview.ForTooltip(action.Description);
            }
            return true;
        }
    }
}
#pragma warning restore CA1416
