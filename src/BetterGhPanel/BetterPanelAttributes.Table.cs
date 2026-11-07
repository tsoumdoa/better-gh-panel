using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;
using BetterGhPanel.UI;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;

#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private RectangleF _tableHeader, _horizontalTrack, _headerMenuAnchor;
        private readonly List<RectangleF> _columnRects = new List<RectangleF>();
        private float _horizontalOffset, _horizontalMaximum, _frozenWidth;
        private Guid? _resizingColumn;
        private float _columnDragStart, _columnDragWidth, _columnPreviewWidth;
        private bool _horizontalDragging;
        private float _horizontalGrab;
        private int _activeTableColumn = -1;
        private Guid _headerSortColumn;
        private bool TableVisible => !_tableHeader.IsEmpty;
        private RectangleF ListBounds => _d.List;
        private bool ListContains(PointF point) => _d.List.Contains(point);
        private string OutputPortLabel(IGH_Param parameter) => parameter.NickName;

        internal static string PairedDisplayValue(PanelRow row) =>
            PanelValuePresentation.FormatDisplay(row, PanelUnitConversion.None, PanelUnits.AutomaticDecimals);

        internal void ClearPaneSummary() { _tableViewResult = null; _tableView = null; }

        private void LayoutTable()
        {
            _tableHeader = _horizontalTrack = RectangleF.Empty;
            _columnRects.Clear();
            if (!PanelOwner.PanelSplit || _d.List.Height < 3 * RowHeight + 24) return;
            _tableHeader = new RectangleF(_d.List.X, _d.List.Y, _d.List.Width, RowHeight);
            _horizontalTrack = new RectangleF(_d.List.X, _d.List.Bottom - 6, _d.List.Width, 4);
            _d.List = new RectangleF(_d.List.X, _d.List.Y + RowHeight, _d.List.Width, _d.List.Height - RowHeight - 8);
            _d.Track.Y = _d.TrackHit.Y = _d.List.Y;
            _d.Track.Height = _d.TrackHit.Height = _d.List.Height;
        }

        private void DrawSplitActions(Graphics g, Font small, StringFormat trim)
        {
            if (_layout.Split.IsEmpty) return;
            bool available = PanelOwner.CanSplit || PanelOwner.PanelSplit;
            Control(g, _layout.Split, available ? "Table" : "", small, trim, PanelOwner.PanelSplit);
            if (!available) Text(g, "Table", small, PanelTheme.Disabled,
                new RectangleF(_layout.Split.X + 3, _layout.Split.Y, _layout.Split.Width - 6, _layout.Split.Height), trim);
        }

        private bool HandleSplitClick(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            if (!_layout.Split.Contains(e.CanvasLocation)) return false;
            if (PanelOwner.CanSplit || PanelOwner.PanelSplit)
            {
                SuppressTrailingDoubleClick();
                PanelOwner.SetPanelSplit(!PanelOwner.PanelSplit);
                PerformLayout(); CanvasDoubleClickGuard.Start(canvas, e); canvas.Invalidate();
            }
            return true;
        }

        private Guid ColumnId(int index) => index == 0 ? PanelOwner.Params.Input[0].InstanceGuid : PanelOwner.TableColumns[index - 1].Input.InstanceGuid;
        private string ColumnTitle(int index)
        {
            string title = index == 0 ? PanelOwner.Params.Input[0].NickName : PanelOwner.TableColumns[index - 1].Input.NickName;
            bool numeric = index == 0 ? PanelOwner.Session.Snapshot.Rows.Any() && PanelOwner.Session.Result.Statistics.NumericCount > 0 :
                PanelOwner.TableColumns[index - 1].Snapshot?.TypeCounts.Keys.Any(type => type == "Number" || type == "Integer") == true;
            string units = numeric ? PanelUnits.Symbol(PanelOwner.UnitConversion) : "";
            if (units.Length > 0) title += " · " + units;
            var options = PanelOwner.Session.Options;
            if (options.SortMode != PanelSortMode.SourceOrder && (options.SortColumn == ColumnId(index) || index == 0 && options.SortColumn == Guid.Empty))
                title += options.SortMode == PanelSortMode.ValueAscending ? " ▲" : " ▼";
            if (index > 0)
            {
                var column = PanelOwner.TableColumns[index - 1];
                if (!column.Present) title += " — not wired";
                else if (column.Pair == null) title += " !";
            }
            return title;
        }

        private void PrepareTable(Graphics g, PanelQueryResult result, Font font, Font small, float gutter)
        {
            using var number = CanvasRowRenderer.MonospaceFont(11);
            float Measure(string value, bool numeric = false) => TightWidth(g, Short(value, 256).Replace('\r', ' ').Replace('\n', ' '), numeric ? number : font);
            var widths = new float[PanelOwner.TableColumns.Count + 1];
            for (int index = 0; index < widths.Length; index++) widths[index] = Math.Max(48, TightWidth(g, ColumnTitle(index), small) + 16);
            if (DisplayListLengths)
            {
                int end = Math.Min(result.BranchPaths.Count, _d.Offset + (int)(_d.List.Height / RowHeight));
                for (int offset = _d.Offset; offset < end; offset++)
                {
                    if (!new RectangleF(_d.List.X, _d.List.Y + (offset - _d.Offset) * RowHeight, _d.List.Width, RowHeight).IntersectsWith(_renderVisibleRegion)) continue;
                    string path = result.BranchPaths[offset];
                    widths[0] = Math.Max(widths[0], Measure(path + " " + result.Snapshot.BranchItemCounts[path] + " items") + 24);
                    for (int index = 1; index < widths.Length; index++) widths[index] = Math.Max(widths[index], Measure(ColumnBranchCell(path, index - 1)) + 16);
                }
            }
            else
            {
                // Measure only a viewport of rows, never offscreen tree values.
                float bottom = _d.List.Y;
                string? previousPath = null;
                for (int offset = _d.Offset; offset < ViewCount(result); offset++)
                {
                    string path = ViewPath(result, offset);
                    ViewEntry(result, offset, out _, out string? empty, out _, out _);
                    if (path != previousPath)
                    {
                        if (bottom + BranchHeight + (empty == null ? RowHeight : 0) > _d.List.Bottom) break;
                        bottom += BranchHeight; previousPath = path;
                    }
                    if (empty != null) continue;
                    if (bottom + RowHeight > _d.List.Bottom) break;
                    var visibleRow = new RectangleF(_d.List.X, bottom, _d.List.Width, RowHeight);
                    bottom += RowHeight;
                    if (!visibleRow.IntersectsWith(_renderVisibleRegion)) continue;
                    ViewEntry(result, offset, out int item, out _, out int extra, out _);
                    if (item < 0)
                    {
                        if (extra >= 0)
                            for (int column = 0; column < PanelOwner.TableColumns.Count; column++)
                            {
                                int cell = TableIndex(result).Entries[extra].Cells[column];
                                var source = PanelOwner.TableColumns[column];
                                if (cell >= 0 && source.Snapshot != null)
                                    widths[column + 1] = Math.Max(widths[column + 1], Measure(source.Value(source.Snapshot.Rows[cell], PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor), source.Snapshot.Rows[cell].NumericValue.HasValue) + 60);
                            }
                        continue;
                    }
                    int position = result.RowPositions[item];
                    widths[0] = Math.Max(widths[0], Measure(PanelOwner.DisplayValue(result.Snapshot, result.Snapshot.Rows[position], position), result.Snapshot.Rows[position].NumericValue.HasValue) + 60);
                    for (int index = 1; index < widths.Length; index++)
                        widths[index] = Math.Max(widths[index], Measure(ColumnCell(result, position, index - 1), PanelOwner.TableColumns[index - 1].Row(position)?.NumericValue.HasValue == true) + 60);
                }
            }
            for (int index = 0; index < widths.Length; index++)
            {
                var id = ColumnId(index);
                widths[index] = _resizingColumn == id ? _columnPreviewWidth : PanelOwner.ColumnWidths.TryGetValue(id, out float width) ? width : Math.Min(320, widths[index]);
            }
            _frozenWidth = gutter + Math.Min(widths[0], Math.Max(24, _d.List.Width * 0.45f - gutter));
            float available = Math.Max(1, _d.List.Width - _frozenWidth);
            _horizontalMaximum = Math.Max(0, widths.Skip(1).Sum() - available);
            _horizontalOffset = Math.Max(0, Math.Min(_horizontalMaximum, _horizontalOffset));
            _horizontalTrack.X = _d.List.X + _frozenWidth;
            _horizontalTrack.Width = available;
            _columnRects.Clear();
            _columnRects.Add(new RectangleF(_d.List.X + gutter, _tableHeader.Y, _frozenWidth - gutter, RowHeight));
            float x = _d.List.X + _frozenWidth - _horizontalOffset;
            for (int index = 1; index < widths.Length; index++)
            { _columnRects.Add(new RectangleF(x, _tableHeader.Y, widths[index], RowHeight)); x += widths[index]; }
        }

        private string ColumnCell(PanelQueryResult result, int position, int column) =>
            PanelOwner.TableColumns[column].Cell(result, position, PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor);

        private RectangleF ColumnClip(int index, float y, float height) => index == 0 ?
            new RectangleF(_d.List.X, y, _frozenWidth, height) : new RectangleF(_d.List.X + _frozenWidth, y, Math.Max(0, _d.List.Width - _frozenWidth), height);

        private void PaintColumn(Graphics g, int index, float y, float height, Action<RectangleF> paint)
        {
            if (index >= _columnRects.Count) return;
            var bounds = new RectangleF(_columnRects[index].X, y, _columnRects[index].Width, height);
            var visible = RectangleF.Intersect(bounds, ColumnClip(index, y, height));
            if (visible.Width <= 0) return;
            var state = g.Save();
            g.SetClip(visible, CombineMode.Intersect);
            paint(bounds);
            using (var pen = new Pen(PanelTheme.Rgb(224, 229, 222))) g.DrawLine(pen, bounds.Right - 0.5f, y, bounds.Right - 0.5f, y + height);
            g.Restore(state);
        }

        private void DrawTableHeader(Graphics g, Font small, StringFormat trim, float gutter)
        {
            Fill(g, PanelTheme.Rgb(242, 247, 241), _tableHeader);
            Text(g, "#", small, Muted, new RectangleF(_tableHeader.X + 4, _tableHeader.Y, gutter - 8, RowHeight), trim);
            using var bold = new Font(small, FontStyle.Bold);
            for (int index = 0; index < _columnRects.Count; index++)
            {
                int columnIndex = index;
                PaintColumn(g, index, _tableHeader.Y, RowHeight, bounds =>
                {
                    var column = columnIndex == 0 ? null : PanelOwner.TableColumns[columnIndex - 1];
                    Text(g, ColumnTitle(columnIndex), bold, columnIndex == 0 ? Accent : column?.Present == false ? Muted : column?.Pair == null ? PanelTheme.Warning : Ink,
                        new RectangleF(bounds.X + 6, bounds.Y, Math.Max(0, bounds.Width - 12), bounds.Height), trim);
                });
            }
            using var line = new Pen(PanelTheme.Rgb(193, 205, 191));
            g.DrawLine(line, _tableHeader.X, _tableHeader.Bottom - 1, _tableHeader.Right, _tableHeader.Bottom - 1);
        }

        private void DrawTableCell(Graphics g, CanvasRowRenderer renderer, PanelRow? row, string value, RectangleF bounds, Font font, Font small, bool muted = false)
        {
            bool showType = row != null && (row.GeometryInfo == null || StockTypeLabels.SeparateLabel(row.TypeName));
            float typeWidth = showType && bounds.Width >= 90 ? Math.Min(44, bounds.Width / 3) : 0;
            renderer.DrawValue(g, PanelValuePresentation.Visualize(value), font, muted ? Muted : Ink,
                new RectangleF(bounds.X + 6, bounds.Y, Math.Max(0, bounds.Width - typeWidth - 12), bounds.Height), row?.NumericValue.HasValue == true);
            if (typeWidth > 0) renderer.DrawCell(g, CanvasMenu.ShortTypeName(row!.TypeName), small, Muted,
                new RectangleF(bounds.Right - typeWidth - 4, bounds.Y, typeWidth - 4, bounds.Height), true);
        }

        private void DrawTableCells(Graphics g, CanvasRowRenderer renderer, PanelQueryResult result, int position, int offset, RectangleF bounds, Font font, Font small)
        {
            for (int index = 0; index < PanelOwner.TableColumns.Count; index++)
            {
                int columnIndex = index;
                var column = PanelOwner.TableColumns[index];
                PaintColumn(g, index + 1, bounds.Y, RowHeight, cell =>
                {
                    var row = column.Row(position);
                    if (row == null && result.DisplaySet == null)
                    {
                        bool firstMissing = true;
                        if (offset > 0)
                        {
                            ViewEntry(result, offset - 1, out int previous, out _, out _, out _);
                            firstMissing = previous < 0 || result.Snapshot.Rows[result.RowPositions[previous]].Path != result.Snapshot.Rows[position].Path || column.Row(result.RowPositions[previous]) != null;
                        }
                        if (column.Pair != null && firstMissing) renderer.DrawCell(g, "—", small, Muted, new RectangleF(cell.X + 6, cell.Y, cell.Width - 12, RowHeight));
                    }
                    else DrawTableCell(g, renderer, result.DisplaySet == null ? row : null, ColumnCell(result, position, columnIndex), cell, font, small);
                });
            }
        }

        private void DrawTableExtra(Graphics g, CanvasRowRenderer renderer, PanelQueryResult result, int offset, RectangleF bounds, Font font, Font small)
        {
            var entry = TableIndex(result).Entries[offset];
            for (int index = 0; index < entry.Cells.Count; index++)
            {
                int item = entry.Cells[index];
                var column = PanelOwner.TableColumns[index];
                if (item < 0 || column.Snapshot == null || column.Pair == null) continue;
                var pair = column.Pair.PairAtPaired(item);
                bool only = item - pair.PairedOffset >= pair.Data.ItemCount;
                bool first = offset == 0 || !TableIndex(result).Entries[offset - 1].Extra || TableIndex(result).Entries[offset - 1].Cells[index] < 0 ||
                    !ReferenceEquals(pair, column.Pair.PairAtPaired(TableIndex(result).Entries[offset - 1].Cells[index]));
                string value = column.Value(column.Snapshot.Rows[item], PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor);
                if (first) value += only ? " · only in " + column.Input.NickName : " · paired with hidden D rows";
                PaintColumn(g, index + 1, bounds.Y, RowHeight, cell => DrawTableCell(g, renderer, null, value, cell, font, small, muted: true));
            }
        }

        private string[] LengthWarnings(string path) => PanelOwner.TableColumns.Where(column => column.Pair != null)
            .Select(column => (Column: column, Heading: column.Pair!.Heading(path, PanelPairedData.EmptyPartnersInScope(PanelOwner.Session.Options))))
            .Where(value => value.Heading.Unequal).Select(value => value.Column.Input.NickName + " " + value.Heading.Total + " / " + value.Heading.DataCount).ToArray();

        private void DrawTableBadge(Graphics g, string path, RectangleF bounds, Font small, StringFormat trim)
        {
            var warnings = LengthWarnings(path);
            if (warnings.Length == 0) return;
            float width = Math.Min(bounds.Width - _frozenWidth - 12, TightWidth(g, warnings[0], small) + 10);
            if (width < 30) return;
            var badge = new RectangleF(bounds.X + _frozenWidth + 6, bounds.Y + 1, width, bounds.Height - 2);
            using var pen = new Pen(PanelTheme.PairingWarning, 0.75f);
            g.DrawRectangle(pen, badge.X, badge.Y, badge.Width, badge.Height);
            Text(g, warnings[0], small, PanelTheme.PairingWarning, new RectangleF(badge.X + 4, badge.Y, badge.Width - 8, badge.Height), trim);
        }

        private void RenderTableRows(Graphics g, PanelQueryResult result, RectangleF visibleRegion, Font font, Font small, Font mono, StringFormat trim)
        {
            bool sorted = PanelOwner.Session.Options.SortMode != PanelSortMode.SourceOrder;
            using var renderer = new CanvasRowRenderer(g, result.Snapshot.Rows.Count, sorted);
            if (PanelOwner.CreateSetTree) SizeSetColumn(g, renderer, result);
            PrepareTable(g, result, font, small, renderer.GutterWidth);
            DrawTableHeader(g, small, trim, renderer.GutterWidth);
            float y = _d.List.Y;
            string? previous = null;
            var measurements = new List<int>();
            var clip = g.Save();
            g.SetClip(_d.List, CombineMode.Intersect);
            for (int offset = _d.Offset; offset < ViewCount(result); offset++)
            {
                ViewEntry(result, offset, out int item, out string? empty, out int extra, out _);
                string path = ViewPath(result, offset);
                if (path != previous)
                {
                    if (y + BranchHeight + (empty == null ? RowHeight : 0) > _d.List.Bottom) break;
                    var heading = new RectangleF(_d.List.X, y, _d.List.Width, BranchHeight);
                    Fill(g, PanelTheme.Rgb(236, 241, 235), heading);
                    string count = CanvasStatistics.BranchCount(result, path);
                    float countWidth = Math.Min(_frozenWidth / 2, TightWidth(g, count, small) + 6);
                    Text(g, path, mono, Accent, new RectangleF(heading.X + 6, y, Math.Max(0, _frozenWidth - countWidth - 12), BranchHeight), trim);
                    Text(g, count, small, Muted, new RectangleF(heading.X + _frozenWidth - countWidth - 6, y, countWidth, BranchHeight), trim);
                    DrawBranchExtras(g, result, heading, path, mono, small, trim, labelBounds: new RectangleF(heading.X + 6, y, Math.Max(0, _frozenWidth - countWidth - 12), BranchHeight));
                    DrawTableBadge(g, path, heading, small, trim);
                    y += BranchHeight; previous = path;
                }
                if (empty != null) { _d.Shown++; continue; }
                if (y + RowHeight > _d.List.Bottom) break;
                var bounds = new RectangleF(_d.List.X, y, _d.List.Width, RowHeight);
                if (extra >= 0) { Fill(g, PanelTheme.Background, bounds); DrawTableExtra(g, renderer, result, offset, bounds, font, small); }
                else
                {
                    int position = result.RowPositions[item];
                    var row = result.Snapshot.Rows[position];
                    bool selected = _selection.Contains(position);
                    Fill(g, selected ? PanelTheme.Rgb(216, 232, 219) : (_d.Shown & 1) == 0 ? PanelTheme.Background : PanelTheme.Rgb(247, 249, 246), bounds);
                    var address = RowAddress(result, position);
                    renderer.DrawAddress(g, new RectangleF(bounds.X, y, _frozenWidth, RowHeight), item + 1, address.Index, address.Sorted, selected);
                    if (selected) Fill(g, Accent, new RectangleF(bounds.X, y, 2, RowHeight));
                    PaintColumn(g, 0, y, RowHeight, cell =>
                    {
                        float setWidth = PanelOwner.CreateSetTree ? _setCountWidth : 0;
                        DrawTableCell(g, renderer, result.DisplaySet == null ? row : null, PanelOwner.DisplayValue(result.Snapshot, row, position),
                            new RectangleF(cell.X, cell.Y, Math.Max(0, cell.Width - setWidth), cell.Height), font, small);
                        DrawSetCount(g, renderer, new RectangleF(bounds.X, y, _frozenWidth, RowHeight), position, item + 1, small);
                    });
                    var statuses = CanvasStatistics.RowSourceStatuses(result, position);
                    if (statuses.VerifiedInvalid > 0) Fill(g, PanelTheme.Rgb(180, 106, 67), new RectangleF(bounds.X + 2, y + 3, 2, RowHeight - 6));
                    if (statuses.Unverified > 0) Fill(g, PanelTheme.Rgb(103, 112, 166), new RectangleF(bounds.X + (statuses.VerifiedInvalid > 0 ? 4 : 2), y + 3, 2, RowHeight - 6));
                    DrawTableCells(g, renderer, result, position, offset, bounds, font, small);
                    if (TargetPainted(g, bounds) && !_rowHits.Any(hit => hit.Bounds == bounds && hit.SourcePosition == position)) _rowHits.Add(new RowHit(bounds, position, item + 1));
                    if (bounds.IntersectsWith(visibleRegion)) measurements.Add(position);
                    if (_firstShownItem < 0) _firstShownItem = item;
                    _lastShownItem = item;
                }
                _d.Shown++; y += RowHeight;
            }
            g.Restore(clip);
            PanelOwner.Session.RequestGeometryMeasurements(result.Snapshot, measurements);
            if (Count > _d.Shown) { Fill(g, PanelTheme.Rgb(232, 236, 229), _d.Track); Fill(g, PanelTheme.Rgb(153, 174, 156), Thumb()); }
            if (_horizontalMaximum > 0) { Fill(g, PanelTheme.Rgb(232, 236, 229), _horizontalTrack); Fill(g, PanelTheme.Rgb(153, 174, 156), HorizontalThumb()); }
        }

        private RectangleF HorizontalThumb()
        {
            float width = Math.Max(20, _horizontalTrack.Width * _horizontalTrack.Width / (_horizontalTrack.Width + _horizontalMaximum));
            return new RectangleF(_horizontalTrack.X + (_horizontalTrack.Width - width) * _horizontalOffset / Math.Max(1, _horizontalMaximum), _horizontalTrack.Y, width, _horizontalTrack.Height);
        }
        private void ScrollHorizontal(GH_Canvas canvas, float value)
        { _horizontalOffset = Math.Max(0, Math.Min(_horizontalMaximum, value)); InvalidatePaintedHits(); canvas.Invalidate(); }

        private int HeaderColumnAt(PointF point, bool border = false)
        {
            if (!TableVisible || !_tableHeader.Contains(point)) return -1;
            for (int index = 0; index < _columnRects.Count; index++)
            {
                var rect = RectangleF.Intersect(_columnRects[index], ColumnClip(index, _tableHeader.Y, RowHeight));
                if (rect.Width <= 0 || !rect.Contains(point)) continue;
                if (!border || Math.Abs(point.X - _columnRects[index].Right) <= 4) return index;
            }
            return -1;
        }
        private string ColumnBranchCell(string path, int column) => PanelOwner.TableColumns[column].BranchCell(path, PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor);

        private int CellColumnAt(PointF point)
        {
            if (!TableVisible || point.X < _d.List.X + _frozenWidth) return -1;
            for (int index = 1; index < _columnRects.Count; index++)
                if (point.X >= _columnRects[index].X && point.X < _columnRects[index].Right) return index - 1;
            return -1;
        }
        private GH_ObjectResponse? ClickTableHeader(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            int index = HeaderColumnAt(e.CanvasLocation);
            if (index < 0) return GH_ObjectResponse.Handled;
            if (HeaderColumnAt(e.CanvasLocation, true) >= 0)
            {
                if (e.Clicks > 1) { PanelOwner.SetColumnWidth(ColumnId(index), null); canvas.Invalidate(); return GH_ObjectResponse.Handled; }
                _resizingColumn = ColumnId(index); _columnDragStart = e.CanvasX;
                _columnDragWidth = _columnPreviewWidth = _columnRects[index].Width;
                return GH_ObjectResponse.Capture;
            }
            var options = PanelOwner.Session.Options;
            var id = index == 0 ? Guid.Empty : ColumnId(index);
            var next = options.SortColumn != id || options.SortMode == PanelSortMode.SourceOrder ? PanelSortMode.ValueAscending :
                options.SortMode == PanelSortMode.ValueAscending ? PanelSortMode.ValueDescending : PanelSortMode.SourceOrder;
            PanelOwner.SetColumnSort(id, next); canvas.Invalidate(); return GH_ObjectResponse.Handled;
        }
        private bool HeaderContext(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            if (!RowsAreCurrent(PanelOwner.Session.Result)) return false;
            int index = HeaderColumnAt(e.CanvasLocation);
            if (index < 0) return false;
            _headerSortColumn = index == 0 ? Guid.Empty : ColumnId(index);
            _headerMenuAnchor = RectangleF.Intersect(_columnRects[index], ColumnClip(index, _tableHeader.Y, RowHeight));
            OpenMenu(CanvasMenuKind.HeaderSort, canvas);
            _rowContextHandled = true;
            return true;
        }
        private void TooltipTableHeader(PointF point, GH_TooltipDisplayEventArgs e)
        {
            int index = HeaderColumnAt(point);
            if (index < 0) return;
            e.Title = ColumnTitle(index); e.Text = "";
            e.Description = "Click to sort ascending, descending, then tree order. Drag a border to resize; double-click it to auto-fit.";
            if (index > 0)
            {
                var column = PanelOwner.TableColumns[index - 1];
                if (!column.Present) e.Description = column.Input.NickName + " is not wired.";
                else if (column.Pair == null) e.Description = column.Tree == null || column.Tree.DataCount == 0 ? column.Input.NickName + " has no usable data." :
                    column.Input.NickName + " has " + column.Tree.PathCount + " branches; D has " + PanelOwner.InputBranchCount + ".";
            }
        }
        private bool SplitTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            if (PanelOwner.PanelMinimized || !_layout.Split.Contains(point)) return false;
            e.Title = "Table"; e.Text = PanelOwner.PanelSplit ? "On" : "Off";
            e.Description = "Show the wired columns beside D. Hiding them keeps the wires.";
            return true;
        }
    }
}
#pragma warning restore CA1416
