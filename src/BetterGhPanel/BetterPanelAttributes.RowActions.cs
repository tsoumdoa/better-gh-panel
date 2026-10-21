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
        private DateTime _copiedUntil;
        private DateTime _rowActionDismissedUntil;
        private string _actionStatus = string.Empty;
        private bool _copyPending;
        private long _actionGeneration;
        private bool _rowContextHandled;

        private void CloseRowMenu()
        {
            _rowMenuResult = null;
            _rowMenuHovered = -1;
            _rowMenuActions = Array.Empty<CanvasRowAction>();
        }

        private void SuppressTrailingDoubleClick() =>
            _rowActionDismissedUntil = DateTime.UtcNow.AddMilliseconds(SystemInformation.DoubleClickTime);

        private void RenderRowMenu(Graphics g, Font font, StringFormat trim)
        {
            Fill(g, Color.FromArgb(32, 49, 55, 53), new RectangleF(_rowMenuBounds.X + 1, _rowMenuBounds.Y + 2, _rowMenuBounds.Width, _rowMenuBounds.Height));
            Fill(g, Color.White, _rowMenuBounds);
            var actions = _rowMenuActions;
            for (int i = 0; i < actions.Length; i++)
            {
                var bounds = new RectangleF(_rowMenuBounds.X + 1, _rowMenuBounds.Y + i * 20, _rowMenuBounds.Width - 2, 20);
                if (i == _rowMenuHovered) Fill(g, Color.FromArgb(226, 236, 226), bounds);
                Text(g, actions[i].Label, font, Ink, new RectangleF(bounds.X + 8, bounds.Y, bounds.Width - 16, bounds.Height), trim);
            }
            using (var pen = new Pen(Color.FromArgb(194, 205, 194)))
                g.DrawRectangle(pen, _rowMenuBounds.X, _rowMenuBounds.Y, _rowMenuBounds.Width, _rowMenuBounds.Height);
        }

        private GH_ObjectResponse RespondToRowContextMenu(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            _rowContextHandled = false;
            if (_rowMenuResult != null || _menu.Kind != CanvasMenuKind.None)
            {
                _rowContextHandled = true;
                CloseRowMenu();
                _menu.Close();
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
                _selection.Select(result, hit.SourcePosition, preserveSelected: true);
                PanelOwner.HighlightRows(result.Snapshot, _selection.OrderedPositions(result));
                TrackSelectionCanvas(canvas);
                _rowMenuResult = result;
                var options = PanelOwner.Session.Options;
                bool filtered = options.Search.Length > 0 || options.TypeName != null || options.OnlyInvalid || options.BranchPath != null;
                _rowMenuActions = CanvasRowActions.Create(_selection.Count, _selection.Indices(result), filtered);
                float width = Math.Min(300, _panel.Width - 12);
                float height = _rowMenuActions.Length * 20;
                _rowMenuBounds = new RectangleF(
                    Math.Max(_panel.Left + 6, Math.Min(e.CanvasX, _panel.Right - width - 6)),
                    Math.Max(_panel.Top + HeaderHeight, Math.Min(e.CanvasY, _panel.Bottom - height - 6)), width, height);
                canvas.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            return base.RespondToMouseDown(canvas, e);
        }

        private void ChooseRowAction(PanelRowAction action, PanelQueryResult result, GH_Canvas canvas)
        {
            var positions = _selection.OrderedPositions(result);
            if (positions.Count == 0) return;
            if (action == PanelRowAction.FrameSelection)
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
                if (generation != _actionGeneration || canvas.IsDisposed || !RowsAreCurrent(result) ||
                    !positions.SequenceEqual(_selection.OrderedPositions(result))) return;
                _copyPending = false;
                _actionStatus = status;
                _copiedUntil = DateTime.UtcNow.AddSeconds(5);
                canvas.Invalidate();
            }));
        }

        private bool RowActionTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            if (_rowMenuResult == null || !_rowMenuBounds.Contains(point)) return false;
            int index = (int)((point.Y - _rowMenuBounds.Y) / 20);
            if (index >= 0 && index < _rowMenuActions.Length)
            {
                var action = _rowMenuActions[index];
                e.Title = action.Label;
                e.Text = $"{_selection.Count:N0} selected";
                e.Description = action.Description;
            }
            return true;
        }
    }
}
#pragma warning restore CA1416
