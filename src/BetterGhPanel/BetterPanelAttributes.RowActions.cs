using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;
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
        private int _rowMenuSourcePosition = -1;
        private string? _rowMenuPath;
        private string? _rowMenuColumnPath;
        private DateTime _copiedUntil;
        private DateTime _rowActionDismissedUntil;
        // The row and pane of the latest single click, which a double-click's second click repeats.
        private int _clickedPosition = -1;
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
            Fill(g, PanelTheme.Shadow, new RectangleF(_rowMenuBounds.X + 1, _rowMenuBounds.Y + 2, _rowMenuBounds.Width, _rowMenuBounds.Height));
            Fill(g, PanelTheme.Background, _rowMenuBounds);
            var actions = _rowMenuPendingActions;
            if (!_preservePaintedTargets)
                _rowMenuActions = Enumerable.Repeat(new CanvasRowAction(PanelRowAction.Unpainted, "", ""), actions.Length).ToArray();
            float top = _rowMenuBounds.Y;
            for (int i = 0; i < actions.Length; i++)
            {
                var bounds = new RectangleF(_rowMenuBounds.X + 1, top,
                    _rowMenuBounds.Width - 2, actions[i].Action == PanelRowAction.Separator ? 5 : CanvasRowActions.RowHeight);
                top += bounds.Height;
                if (actions[i].Action == PanelRowAction.Separator)
                {
                    using var separator = new Pen(PanelTheme.Rgb(224, 229, 222));
                    g.DrawLine(separator, bounds.Left + 8, bounds.Y + bounds.Height / 2, bounds.Right - 8, bounds.Y + bounds.Height / 2);
                    continue;
                }
                if (i == _rowMenuHovered) Fill(g, PanelTheme.Rgb(226, 236, 226), bounds);
                string label = actions[i].Label;
                int pathStart = actions[i].Action == PanelRowAction.CopyPath ? label.IndexOf("  {", StringComparison.Ordinal) : -1;
                if (pathStart >= 0)
                {
                    using var mono = new Font(FontFamily.GenericMonospace, font.Size, font.Style, font.Unit);
                    using var right = (StringFormat)trim.Clone();
                    right.Alignment = StringAlignment.Far;
                    Text(g, label.Substring(pathStart + 2), mono, Muted, new RectangleF(bounds.X + 80, bounds.Y, bounds.Width - 88, bounds.Height), right);
                    label = label.Substring(0, pathStart);
                }
                Text(g, label, font, actions[i].Enabled ? Ink : PanelTheme.Disabled,
                    new RectangleF(bounds.X + 8, bounds.Y, pathStart >= 0 ? 72 : bounds.Width - 16, bounds.Height), trim);
                // Partial invalidations must not activate a row that has not
                // actually been painted at its new position.
                var target = new RectangleF(_rowMenuBounds.X, bounds.Y, _rowMenuBounds.Width, bounds.Height);
                if (TargetPainted(g, target)) _rowMenuActions[i] = actions[i];
            }
            using (var pen = new Pen(PanelTheme.Rgb(194, 205, 194)))
                g.DrawRectangle(pen, _rowMenuBounds.X, _rowMenuBounds.Y, _rowMenuBounds.Width, _rowMenuBounds.Height);
            _rowMenuPainted = _rowMenuActions.Any(action => action.Action != PanelRowAction.Unpainted);
        }

        private int RowMenuIndex(PointF point)
        {
            float top = _rowMenuBounds.Y;
            for (int i = 0; i < _rowMenuPendingActions.Length; i++)
            {
                float height = _rowMenuPendingActions[i].Action == PanelRowAction.Separator ? 5 : CanvasRowActions.RowHeight;
                if (point.Y >= top && point.Y < top + height) return _rowMenuPendingActions[i].Action == PanelRowAction.Separator ? -1 : i;
                top += height;
            }
            return -1;
        }

        private bool RowMenuTargetPainted(PointF point)
        {
            int index = RowMenuIndex(point);
            return _rowMenuPainted && _rowMenuBounds.Contains(point) && index >= 0 && index < _rowMenuActions.Length &&
                _rowMenuActions[index].Action != PanelRowAction.Unpainted;
        }

        private static bool FullyVisible(Graphics graphics, RectangleF bounds)
        {
            if (!graphics.IsVisible(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)) return false;
            // Canvas projection is scale/translation. GDI+ can round a clip edge
            // inward when converting it back to canvas units at fractional zoom.
            // Test the interior with one device pixel of tolerance, retaining the
            // region check so real partial paints (including clip holes) stay inert.
            var pixel = new[] { PointF.Empty, new PointF(1, 1) };
            graphics.TransformPoints(CoordinateSpace.World, CoordinateSpace.Device, pixel);
            float dx = Math.Abs(pixel[1].X - pixel[0].X), dy = Math.Abs(pixel[1].Y - pixel[0].Y);
            // Tiny viewport slivers must retain their full coverage requirement.
            bounds.Inflate(bounds.Width > 2 * dx ? -dx : 0, bounds.Height > 2 * dy ? -dy : 0);
            using (var unpainted = new Region(bounds))
            using (var clip = graphics.Clip)
            {
                clip.Intersect(graphics.VisibleClipBounds);
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
            _rowMenuColumnPath = null;
            foreach (var heading in _branchHits)
            {
                if (!heading.Bounds.Contains(e.CanvasLocation)) continue;
                _rowContextHandled = true;
                _activeTableColumn = CellColumnAt(e.CanvasLocation);
                if (DisplayListLengths && EffectiveTableVisible && _activeTableColumn >= 0)
                {
                    InvalidateCopyStatus();
                    _selection.SelectAll(result, heading.Path, includeEmptyBranches: true);
                    TrackSelectionCanvas(canvas);
                    _rowMenuPath = null;
                    _rowMenuColumnPath = heading.Path;
                    ShowRowMenu(canvas, result, new[] {
                        new CanvasRowAction(PanelRowAction.CopyValues, "Copy value", "Copy this cell's summary."),
                        new CanvasRowAction(PanelRowAction.ViewFullValue, "View full value", "View all values represented by this cell.")
                    }, e.CanvasLocation);
                    canvas.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
                _rowMenuPath = heading.Path;
                string copyPath = heading.Path;
                var choices = new System.Collections.Generic.List<CanvasRowAction>
                {
                    new CanvasRowAction(PanelRowAction.CopyPath, "Copy path  " + copyPath, "Copy the branch path."),
                    new CanvasRowAction(PanelRowAction.CopyPathBare, "Copy path as " + copyPath.Trim('{', '}'), "Copy without braces.")
                };
                if (_selection.Count > 0) choices.Add(new CanvasRowAction(PanelRowAction.CopyPaths, "Copy paths of selected rows", "Unique paths in tree order, one per line."));
                choices.Add(new CanvasRowAction(PanelRowAction.Separator, "", ""));
                choices.Add(new CanvasRowAction(PanelRowAction.ShowBranch, "Show only this branch", "Filter Paths to this branch."));
                ShowRowMenu(canvas, result, choices.ToArray(), e.CanvasLocation);
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
                _rowMenuPath = null;
                _rowMenuSourcePosition = hit.SourcePosition;
                ShowRowMenu(canvas, result, CanvasRowActions.Create(_selection.Count, _selection.Indices(result), filtered,
                    PanelOwner.SelectionHasGeometry(_selection.OrderedPositions(result)), !Rhino.Runtime.HostUtils.RunningOnOSX,
                    result.DisplaySet != null && !result.DisplaySet.IsBranchSet && PanelOwner.OutputValues == PanelOutputValues.Items),
                    e.CanvasLocation);
                canvas.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            return base.RespondToMouseDown(canvas, e);
        }

        private void ShowRowMenu(GH_Canvas canvas, PanelQueryResult result, CanvasRowAction[] actions, PointF anchor)
        {
            _rowMenuResult = result;
            _rowMenuPainted = false;
            _rowMenuHovered = -1;
            _rowMenuPendingActions = actions;
            _rowMenuActions = Enumerable.Repeat(new CanvasRowAction(PanelRowAction.Unpainted, "", ""), actions.Length).ToArray();
            float width = Math.Min(300, _layout.Panel.Width - 28);
            float height = actions.Sum(action => action.Action == PanelRowAction.Separator ? 5 : CanvasRowActions.RowHeight);
            var viewport = canvas.Viewport.VisibleRegion;
            _rowMenuBounds = new RectangleF(Math.Max(_layout.Panel.Left + 14, Math.Min(anchor.X, _layout.Panel.Right - width - 14)),
                CanvasRowActions.MenuTop(anchor.Y, height, viewport.Top, viewport.Bottom), width, height);
            RouteMenuInput(canvas);
        }

        private void ChooseRowAction(PanelRowAction action, PanelQueryResult result, GH_Canvas canvas)
        {
            var positions = _selection.OrderedPositions(result);
            if (action == PanelRowAction.ShowBranch && _rowMenuPath != null)
            {
                SetBranchFilter(_rowMenuPath);
                canvas.Invalidate();
                return;
            }
            if (action == PanelRowAction.CopyPath || action == PanelRowAction.CopyPathBare || action == PanelRowAction.CopyPaths ||
                action == PanelRowAction.CopyAddresses || action == PanelRowAction.CopyTable)
            { CopySelectedValues(result, canvas, action, action == PanelRowAction.CopyPath || action == PanelRowAction.CopyPathBare ? _rowMenuPath : null); return; }
            if (_rowMenuColumnPath != null)
            {
                if (action == PanelRowAction.CopyValues) CopySelectedValues(result, canvas, columnBranch: _rowMenuColumnPath);
                else if (action == PanelRowAction.ViewFullValue) ViewFullBranchCell(result, _rowMenuColumnPath);
                return;
            }
            if (positions.Count == 0) return;
            if (action == PanelRowAction.ViewFullValue) ViewFullValue(result, _rowMenuSourcePosition);
            else if (action == PanelRowAction.FrameSelection)
            {
                InvalidateCopyStatus();
                long before = PanelGeometryActions.FrameGeneration;
                long actionGeneration = _actionGeneration;
                void Completed(string value)
                {
                    if (actionGeneration != _actionGeneration || PanelGeometryActions.FrameGeneration != before + 1 || !ResultIsCurrent(result)) return;
                    _actionStatus = value; _copiedUntil = DateTime.UtcNow.AddSeconds(5); canvas.Invalidate();
                }
                string? status = PanelOwner.FrameRowsWithCompletion(result.Snapshot, positions, Completed);
                if (PanelGeometryActions.FrameGeneration <= before + 1)
                { _actionStatus = status ?? "Selection framed"; _copiedUntil = DateTime.UtcNow.AddSeconds(5); }
            }
            else if (action == PanelRowAction.CopyValues) CopySelectedValues(result, canvas);
            else if (action == PanelRowAction.CopyGeometry)
            {
                InvalidateCopyStatus();
                long before = ClipboardGeneration.Current;
                long actionGeneration = _actionGeneration;
                void Completed(string value)
                {
                    if (actionGeneration != _actionGeneration || ClipboardGeneration.Current != before + 1 || !ResultIsCurrent(result)) return;
                    _actionStatus = value; _copiedUntil = DateTime.UtcNow.AddSeconds(5); canvas.Invalidate();
                }
                PanelOwner.TryCopyGeometryWithCompletion(result.Snapshot, positions, Completed, out var status);
                if (ClipboardGeneration.Current <= before + 1)
                { _actionStatus = status; _copiedUntil = DateTime.UtcNow.AddSeconds(5); }
            }
            else if (action == PanelRowAction.SelectIndices) SelectAcrossBranches(result, _selection.Indices(result), canvas);
        }

        private const string FullValueHint = "\nDouble-click to view the full value.";

        // Shared by the row menu and row double-click; the row must be selected.
        // A column cell without an item has no value to show.
        private void ViewFullValue(PanelQueryResult result, int position)
        {
            if (!PanelOwner.CapturedSnapshotCurrent(result.Snapshot)) return;
            if (position < 0 || position >= result.Snapshot.Rows.Count || !_selection.Contains(position)) return;
            var row = result.Snapshot.Rows[position];
            bool projected = result.DisplaySet?.Values.ContainsKey(position) == true;
            string? columnValue = null;
            if (EffectiveTableVisible && _activeTableColumn >= 0 && _activeTableColumn < PanelOwner.TableColumns.Count)
            {
                var column = PanelOwner.TableColumns[_activeTableColumn];
                if (result.DisplaySet != null && !column.SummaryCurrent(result)) return;
                if (column.Row(position) == null && result.DisplaySet == null) return;
                columnValue = column.Cell(result, position, PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor, raw: true);
            }
            string value = !projected && !PanelOwner.Session.Options.DisplayFilterValue && row.StringValue != null
                ? row.StringValue : PanelOwner.DisplayValue(result.Snapshot, row, position);
            try { PanelFullValueWindow.Show(columnValue ?? value); }
            catch (Exception)
            {
                _actionStatus = "Could not open the full value.";
                _copiedUntil = DateTime.UtcNow.AddSeconds(5);
            }
        }

        private void ViewFullBranchCell(PanelQueryResult result, string path)
        {
            if (!EffectiveTableVisible || _activeTableColumn < 0 || _activeTableColumn >= PanelOwner.TableColumns.Count ||
                !result.ContainsBranchPath(path) || !PanelOwner.CapturedSnapshotCurrent(result.Snapshot)) return;
            var column = PanelOwner.TableColumns[_activeTableColumn];
            if (!column.SummaryCurrent(result)) return;
            try { PanelFullValueWindow.Show(column.BranchCell(result, path, PanelOwner.UnitConversion,
                PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor, raw: true)); }
            catch (Exception) { _actionStatus = "Could not open the full value."; _copiedUntil = DateTime.UtcNow.AddSeconds(5); }
        }

        private void SelectAcrossBranches(PanelQueryResult result, int[] indices, GH_Canvas canvas)
        {
            InvalidateCopyStatus();
            _selection.SelectIndices(result, indices);
            var positions = _selection.OrderedPositions(result);
            PanelOwner.HighlightRows(result.Snapshot, positions);
            TrackSelectionCanvas(canvas);
            int branches = positions.Select(position => result.Snapshot.Rows[position].Path).Distinct().Count();
            _actionStatus = System.FormattableString.Invariant($"{CanvasRowActions.IndexLabel(indices)} · {_selection.Count:N0} selected · {branches:N0} branches");
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

        private async void CopySelectedValues(PanelQueryResult result, GH_Canvas canvas, PanelRowAction action = PanelRowAction.CopyValues, string? path = null, string? columnBranch = null)
        {
            var positions = _selection.OrderedPositions(result);
            long selectionVersion = _selection.Version;
            if (result.BranchStats == null && positions.Count == 0 && path == null && action != PanelRowAction.CopyPaths &&
                !((DisplayListLengths || result.DisplaySet?.IsBranchSet == true) && _selection.BranchPaths(result).Count > 0)) return;
            InvalidateCopyStatus();
            long generation = _actionGeneration;
            _copyPending = true;
            _actionStatus = "Copying values…";
            canvas.Invalidate();
            long before = ClipboardGeneration.Current;
            string status;
            try { status = await (EffectiveTableVisible && _activeTableColumn >= 0 && action == PanelRowAction.CopyValues ? PanelOwner.CopyColumnValuesAsync(result.Snapshot, positions, _activeTableColumn, columnBranch) : action != PanelRowAction.CopyValues ? PanelOwner.CopyAddressesAsync(result.Snapshot, positions, action, path) : PanelOwner.CopyValuesAsync(result.Snapshot, positions)); }
            catch (Exception) { status = "Could not copy values. Try again."; }
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (generation != _actionGeneration || canvas.IsDisposed || !ResultIsCurrent(result) ||
                    selectionVersion != _selection.Version || !positions.SequenceEqual(_selection.OrderedPositions(result))) return;
                _copyPending = false;
                if (ClipboardGeneration.Current > before + 1) { _actionStatus = string.Empty; canvas.Invalidate(); return; }
                _actionStatus = status;
                _copiedUntil = DateTime.UtcNow.AddSeconds(5);
                canvas.Invalidate();
            }));
        }

        private void RowActionTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            if (_rowMenuResult == null || !_rowMenuPainted || !_rowMenuBounds.Contains(point)) return;
            int index = RowMenuIndex(point);
            if (RowsAreCurrent(_rowMenuResult) && index >= 0 && index < _rowMenuActions.Length &&
                _rowMenuActions[index].Action != PanelRowAction.Unpainted)
            {
                var action = _rowMenuActions[index];
                e.Title = action.Label;
                e.Text = action.Action == PanelRowAction.ViewFullValue ? "The row you right-clicked" : System.FormattableString.Invariant($"{_selection.Count:N0} selected");
                e.Description = PanelTextPreview.ForTooltip(action.Enabled ? action.Description : "Unavailable: " + action.DisabledReason);
            }
        }
    }
}
#pragma warning restore CA1416
