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

#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private const float MetricLaneWidth = 56, MetricUnitWidth = 40;
        private RectangleF _tableHeader, _horizontalTrack, _horizontalTrackHit, _headerMenuAnchor;
        private readonly List<RectangleF> _columnRects = new List<RectangleF>();
        private float _horizontalOffset, _horizontalMaximum, _frozenWidth;
        private Guid? _resizingColumn;
        private float _columnDragStart, _columnDragWidth, _columnPreviewWidth;
        private bool _horizontalDragging;
        private float _horizontalGrab;
        private int _activeTableColumn = -1;
        private Guid _headerSortColumn;
        private readonly Dictionary<Guid, float> _autoWidths = new Dictionary<Guid, float>();
        private PanelQueryResult? _autoWidthResult;
        internal void ClearTableWidths() { _autoWidths.Clear(); _autoWidthResult = null; _setCountResult = null; }
        private RectangleF HeaderTarget(int index, bool border)
        {
            var rect = _columnRects[index];
            return border ? new RectangleF(rect.Right - 4, rect.Y, 8, rect.Height) :
                RectangleF.Intersect(rect, ColumnClip(index, _tableHeader.Y, RowHeight));
        }
        private bool HeaderTargetPainted(GH_Canvas canvas, int index, bool border = false) => index >= 0 && index < _columnRects.Count &&
            ReferenceEquals(_paintedCanvas, canvas) && RowsAreCurrent(PanelOwner.Session.Result) &&
            (border ? _paintedHeaderBorders : _paintedHeaders).TryGetValue(ColumnId(index), out var painted) && painted == HeaderTarget(index, border);

        private bool _fitTablePending;
        internal void RequestTableFit() => _fitTablePending = true;
        internal void CancelTableFit() => _fitTablePending = false;
        private void FitTableOnPaint(Graphics g, PanelQueryResult result, Font font)
        {
            if (!_fitTablePending || !TableVisible || !PanelOwner.Session.HasCurrentResult ||
                !PanelOwner.TableSummariesCurrent) return;
            using var renderer = new CanvasRowRenderer(g, result.Snapshot.Rows.Count, PanelOwner.Session.Options.SortMode != PanelSortMode.SourceOrder);
            if (PanelOwner.CreateSetTree) SizeSetColumn(g, renderer, result);
            PrepareTable(g, result, font, renderer.GutterWidth);
            float desired = renderer.GutterWidth + _autoWidths.Values.Sum(width => Math.Min(PanelTableText.MaximumColumnWidth, width)) + 36;
            _fitTablePending = false;
            PanelOwner.FitTableWidth(desired);
            Layout();
            _preservePaintedTargets = false;
        }
        private string? SortedColumnName(PanelQueryOptions options) => options.SortColumn == Guid.Empty ? null :
            PanelOwner.TableColumns.Select((column, index) => (column, index)).Where(value => value.column.Input.InstanceGuid == options.SortColumn)
                .Select(value => PanelOwner.TableHeaderTitle(value.index + 1)).FirstOrDefault();
        private string SortChipLabel(PanelQueryOptions options) => options.SortMode == PanelSortMode.SourceOrder ? "Sort" :
            SortedColumnName(options) is string name ? name + (options.SortMode == PanelSortMode.ValueAscending ? " ▲" : " ▼") :
            options.SortMode == PanelSortMode.ValueAscending ? "Asc" : "Desc";
        private bool TableVisible => !_tableHeader.IsEmpty;
        internal bool EffectiveTableVisible => TableVisible && !PanelOwner.PanelMinimized && (_paintedCanvas?.Viewport.Zoom ?? 1f) >= 0.5f;

        private void LayoutTable()
        {
            _tableHeader = _horizontalTrack = _horizontalTrackHit = RectangleF.Empty;
            _columnRects.Clear();
            if (!PanelOwner.PanelSplit || _list.Height < 3 * RowHeight + 24) return;
            _tableHeader = new RectangleF(_list.X, _list.Y, _list.Width, RowHeight);
            _list = new RectangleF(_list.X, _list.Y + RowHeight, _list.Width, _list.Height - RowHeight);
            if (_horizontalMaximum > 0) _list.Height -= 8;
            LayoutHorizontalTrack();
            _track.Y = _trackHit.Y = _list.Y;
            _track.Height = _trackHit.Height = _list.Height;
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
            string title = PanelOwner.TableHeaderTitle(index);
            bool numeric = index > 0 && PanelOwner.TableColumns[index - 1].Snapshot?.TypeCounts.Keys.Any(type => type == "Number" || type == "Integer") == true;
            string units = index == 0 ? DataHeaderUnit() : PanelOwner.TableColumns[index - 1].HasGeometry
                ? PanelOwner.TableColumns[index - 1].HeaderUnit(PanelOwner.UnitConversion) : numeric ? PanelUnits.Symbol(PanelOwner.UnitConversion) : "";
            if (index > 0 && PanelOwner.TableColumns[index - 1].HomogeneousMetric)
                title += PanelOwner.TableColumns[index - 1].HeaderSuffix(PanelOwner.UnitConversion);
            else if (units.Length > 0) title += " \u00b7 " + units;
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

        private string DataHeaderUnit()
        {
            var session = PanelOwner.Session;
            var result = session.Result;
            var options = session.Options;
            if (DisplayListLengths || PanelOwner.UnitConversion == PanelUnitConversion.None) return "";
            int first = ViewItemAtOrAfter(result, _offset);
            int end = Math.Min(result.RowPositions.Count, first + Math.Max(1, (int)(_list.Height / RowHeight)));
            for (int index = first; index < end; index++)
            {
                int position = result.RowPositions[index];
                var row = result.Snapshot.Rows[position];
                var geometry = session.GetGeometryDetails(result.Snapshot, position) ?? row.GeometryInfo;
                if (options.DisplayFilterValue)
                {
                    var value = PanelFilterValue.OutputValue(result.Snapshot, row, options.SearchMode, geometry, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor);
                    if (value?.Kind == PanelPropertyKind.Number)
                    {
                        string symbol = PanelFilterValue.UnitSymbol(result.Snapshot, row, options.SearchMode, geometry);
                        if (symbol.Length > 0) return symbol;
                    }
                }
                else if (row.IsValid && !row.IsNull && row.NumericValue.HasValue) return PanelUnits.Symbol(PanelOwner.UnitConversion);
            }
            return "";
        }

        private void PrepareTable(Graphics g, PanelQueryResult result, Font font, float gutter)
        {
            PanelOwner.ResolveColumnWidths();
            using var number = CanvasRowRenderer.MonospaceFont(11);
            using var pathFont = CanvasRowRenderer.MonospaceFont(10);
            float Measure(string value, bool numeric = false) => TightWidth(g, Short(value, 256).Replace('\r', ' ').Replace('\n', ' '), numeric ? number : font);
            float CellWidth(PanelTableColumn column, PanelRow row, string text)
            {
                string lane = column.MetricLane(row);
                return lane.Length > 0 && text.StartsWith(lane + " ", StringComparison.Ordinal)
                    ? MetricLaneWidth + Measure(text.Substring(lane.Length + 1).Split(' ')[0], true) + MetricUnitWidth + 6
                    : Measure(text, column.CellIsNumeric(row)) + column.CellPadding(row);
            }
            using var headerFont = new Font(font, FontStyle.Bold);
            var widths = new float[PanelOwner.TableColumns.Count + 1];
            for (int index = 0; index < widths.Length; index++) widths[index] = Math.Max(PanelTableText.MinimumColumnWidth, TightWidth(g, ColumnTitle(index), headerFont) + 24);
            if (DisplayListLengths)
            {
                int end = Math.Min(result.BranchPaths.Count, _offset + (int)(_list.Height / RowHeight));
                for (int offset = _offset; offset < end; offset++)
                {
                    if (!new RectangleF(_list.X, _list.Y + (offset - _offset) * RowHeight, _list.Width, RowHeight).IntersectsWith(_renderVisibleRegion)) continue;
                    string path = result.BranchPaths[offset];
                    widths[0] = Math.Max(widths[0], Measure(path + " " + result.Snapshot.BranchItemCounts[path] + " items") + 24);
                    if (result.DisplaySet?.IsBranchSet == true)
                    {
                        string label = result.DisplaySet.BranchIndices[path] + " · " + path;
                        widths[0] = Math.Max(widths[0], TightWidth(g, label, pathFont) + 76 + 40 + 12 - gutter);
                    }
                    for (int index = 1; index < widths.Length; index++)
                    {
                        string text = PanelOwner.TableColumns[index - 1].BranchCell(result, path, PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor);
                        if (text != "measuring…") widths[index] = Math.Max(widths[index], Measure(text) + 16);
                    }
                }
            }
            else
            {
                // Measure only a viewport of rows, never offscreen tree values.
                float bottom = _list.Y;
                string? previousPath = null;
                for (int offset = _offset; offset < ViewCount(result); offset++)
                {
                    string path = ViewPath(result, offset);
                    ViewEntry(result, offset, out _, out string? empty, out _);
                    if (path != previousPath)
                    {
                        if (bottom + BranchHeight + (empty == null ? RowHeight : 0) > _list.Bottom) break;
                        if (new RectangleF(_list.X, bottom, _list.Width, BranchHeight).IntersectsWith(_renderVisibleRegion))
                            for (int column = 0; column < PanelOwner.TableColumns.Count; column++)
                                if (ColumnHeading(path, column) is PanelPairedData.PairHeading heading && heading.Unequal)
                                    widths[column + 1] = Math.Max(widths[column + 1], Measure(PanelTableText.LengthBadge(heading.Total, heading.DataTotal)) + 20);
                        bottom += BranchHeight; previousPath = path;
                    }
                    if (empty != null) continue;
                    if (bottom + RowHeight > _list.Bottom) break;
                    var visibleRow = new RectangleF(_list.X, bottom, _list.Width, RowHeight);
                    bottom += RowHeight;
                    if (!visibleRow.IntersectsWith(_renderVisibleRegion)) continue;
                    ViewEntry(result, offset, out int item, out _, out int extra);
                    if (item < 0)
                    {
                        if (extra >= 0)
                        {
                            using var noteFont = new Font(font.FontFamily, 10, FontStyle.Regular, GraphicsUnit.Pixel);
                            widths[0] = Math.Max(widths[0], TightWidth(g, ExtraNote(result, extra), noteFont) + 16 - gutter);
                            for (int column = 0; column < PanelOwner.TableColumns.Count; column++)
                            {
                                int cell = TableIndex(result).Entries[extra].Cells[column];
                                var source = PanelOwner.TableColumns[column];
                                if (cell >= 0 && source.Snapshot != null && !source.Measuring(cell))
                                    widths[column + 1] = Math.Max(widths[column + 1], CellWidth(source, source.Snapshot.Rows[cell], source.ItemValue(cell, PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor)));
                            }
                        }
                        continue;
                    }
                    int position = result.RowPositions[item];
                    var dataRow = result.Snapshot.Rows[position];
                    bool metric = PanelOwner.Session.Options.DisplayFilterValue || result.DisplaySet != null;
                    bool showType = ShowDataTypes(result) && (metric || dataRow.GeometryInfo == null || StockTypeLabels.SeparateLabel(dataRow.TypeName));
                    bool numeric = result.DisplaySet != null ? result.DisplaySet.Values[position].Number.HasValue :
                        metric ? !PanelProperties.IsProperty(PanelOwner.Session.Options.SearchMode) || PanelProperties.Kind(PanelOwner.Session.Options.SearchMode) == PanelPropertyKind.Number : dataRow.NumericValue.HasValue;
                    var statuses = CanvasStatistics.RowSourceStatuses(result, position);
                    float reserved = (PanelOwner.CreateSetTree ? _setCountWidth : 0) + (showType ? TypeColumnWidth + CellGap : 12) + 6 +
                        (statuses.VerifiedInvalid > 0 || statuses.Unverified > 0 ? 12 : 0);
                    widths[0] = Math.Max(widths[0], Measure(PanelOwner.DisplayValue(result.Snapshot, dataRow, position), numeric) + reserved);
                    for (int index = 1; index < widths.Length; index++)
                    {
                        var column = PanelOwner.TableColumns[index - 1];
                        var cell = result.DisplaySet == null ? column.Row(position) : null;
                        string text = column.Cell(result, position, PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor);
                        if (text == "measuring…") continue;
                        widths[index] = Math.Max(widths[index], cell == null ? Measure(text) + column.CellPadding(cell) : CellWidth(column, cell, text));
                    }
                }
            }
            // Auto-fit only grows while the result stays the same, so scrolling never makes columns jump.
            if (!ReferenceEquals(_autoWidthResult, result)) { _autoWidths.Clear(); _autoWidthResult = result; }
            for (int index = 0; index < widths.Length; index++)
            {
                var id = ColumnId(index);
                if (_autoWidths.TryGetValue(id, out float seen)) widths[index] = Math.Max(widths[index], seen);
                _autoWidths[id] = widths[index];
                widths[index] = _resizingColumn == id ? _columnPreviewWidth : PanelOwner.ColumnWidths.TryGetValue(id, out float width) ? width : Math.Min(PanelTableText.MaximumColumnWidth, widths[index]);
            }
            bool manualD = _resizingColumn == ColumnId(0) || PanelOwner.ColumnWidths.ContainsKey(ColumnId(0));
            _frozenWidth = manualD ? Math.Min(gutter + widths[0], _list.Width - PanelTableText.MinimumColumnWidth) :
                gutter + Math.Min(widths[0], Math.Max(24, _list.Width * 0.45f - gutter));
            float available = Math.Max(1, _list.Width - _frozenWidth);
            _horizontalMaximum = Math.Max(0, widths.Skip(1).Sum() - available);
            _horizontalOffset = Math.Max(0, Math.Min(_horizontalMaximum, _horizontalOffset));
            bool barReserved = !_horizontalTrack.IsEmpty;
            bool overflow = _horizontalMaximum > 0;
            if (barReserved != overflow)
            {
                _list.Height += overflow ? -8 : 8;
                _track.Height = _trackHit.Height = _list.Height;
            }
            LayoutHorizontalTrack();
            _columnRects.Clear();
            _columnRects.Add(new RectangleF(_list.X + gutter, _tableHeader.Y, _frozenWidth - gutter, RowHeight));
            float x = _list.X + _frozenWidth - _horizontalOffset;
            for (int index = 1; index < widths.Length; index++)
            { _columnRects.Add(new RectangleF(x, _tableHeader.Y, widths[index], RowHeight)); x += widths[index]; }
        }

        private string ColumnCell(PanelQueryResult result, int position, int column) =>
            PanelOwner.TableColumns[column].Cell(result, position, PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor);

        private RectangleF ColumnClip(int index, float y, float height) => index == 0 ?
            new RectangleF(_list.X, y, _frozenWidth, height) : new RectangleF(_list.X + _frozenWidth, y, Math.Max(0, _list.Width - _frozenWidth), height);

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

        private void DrawTableHeader(Graphics g, Font font, StringFormat trim, float gutter)
        {
            Fill(g, PanelTheme.Rgb(242, 247, 241), _tableHeader);
            Text(g, "#", font, Muted, new RectangleF(_tableHeader.X + 4, _tableHeader.Y, gutter - 8, RowHeight), trim);
            using var bold = new Font(font, FontStyle.Bold);
            for (int index = 0; index < _columnRects.Count; index++)
            {
                int columnIndex = index;
                PaintColumn(g, index, _tableHeader.Y, RowHeight, bounds =>
                {
                    var column = columnIndex == 0 ? null : PanelOwner.TableColumns[columnIndex - 1];
                    if (column?.Present == true && column.Pair == null) Fill(g, PanelTheme.Rgb(255, 246, 222), bounds);
                    float left = columnIndex == 0 ? bounds.X : Math.Max(bounds.X, _list.X + _frozenWidth);
                    Text(g, ColumnTitle(columnIndex), bold, columnIndex == 0 ? Accent : column?.Present == false ? Muted : column?.Pair == null ? PanelTheme.PairingWarning : Ink,
                        new RectangleF(left + 6, bounds.Y, Math.Max(0, bounds.Right - left - 12), bounds.Height), trim);
                });
                var target = HeaderTarget(index, false);
                if (target.Width > 0 && TargetPainted(g, target)) _paintedHeaders[ColumnId(index)] = target;
                float edge = _columnRects[index].Right;
                var clip = ColumnClip(index, _tableHeader.Y, RowHeight);
                var border = HeaderTarget(index, true);
                if (edge >= clip.Left && edge <= clip.Right && TargetPainted(g, border)) _paintedHeaderBorders[ColumnId(index)] = border;
            }
            using var line = new Pen(PanelTheme.Rgb(193, 205, 191));
            g.DrawLine(line, _tableHeader.X, _tableHeader.Bottom - 1, _tableHeader.Right, _tableHeader.Bottom - 1);
        }

        private void DrawTableCell(Graphics g, CanvasRowRenderer renderer, PanelRow? row, string value, RectangleF bounds, Font font, Font small, bool muted = false, bool showType = false, string note = "", bool numeric = false, string metricLane = "")
        {
            if (note.Length > 0)
            {
                float noteWidth = Math.Min(bounds.Width * 0.65f, TightWidth(g, note, small) + 12);
                DrawTableCell(g, renderer, row, value, new RectangleF(bounds.X, bounds.Y, bounds.Width - noteWidth, bounds.Height), font, small, muted, showType, numeric: numeric);
                renderer.DrawCell(g, note, small, Muted, new RectangleF(bounds.Right - noteWidth, bounds.Y, noteWidth - 6, bounds.Height));
                return;
            }
            showType = showType && row != null;
            float marker = 0;
            if (row?.Validity == PanelValidity.VerifiedInvalid || row?.Validity == PanelValidity.Unverified)
            {
                bool invalid = row.Validity == PanelValidity.VerifiedInvalid;
                float badgeX = numeric && row.GeometryInfo != null
                    ? Math.Max(bounds.X + 10, bounds.Right - 14 - renderer.MeasureNumber(g, value)) : bounds.X + 10;
                DrawStatusBadge(g, new PointF(badgeX, bounds.Y + RowHeight / 2), invalid ? "!" : "?",
                    invalid ? PanelTheme.Warning : PanelTheme.Unverified, small);
                marker = 12;
            }
            if (metricLane.Length > 0 && value.StartsWith(metricLane + " ", StringComparison.Ordinal))
            {
                string metric = value.Substring(metricLane.Length + 1);
                int unitAt = metric.LastIndexOf(' ');
                string number = unitAt < 0 ? metric : metric.Substring(0, unitAt);
                string unit = unitAt < 0 ? "" : metric.Substring(unitAt + 1);
                if (bounds.Width >= MetricLaneWidth + renderer.MeasureNumber(g, number) + MetricUnitWidth + 6 &&
                    renderer.MeasureNumber(g, unit) <= MetricUnitWidth - 6)
                {
                    renderer.DrawCell(g, metricLane, small, Muted, new RectangleF(bounds.X + 6, bounds.Y, Math.Max(0, Math.Min(MetricLaneWidth - 6, bounds.Width - 12)), bounds.Height));
                    renderer.DrawValue(g, number, font, muted ? Muted : Ink,
                        new RectangleF(bounds.X + MetricLaneWidth, bounds.Y, Math.Max(0, bounds.Width - MetricLaneWidth - MetricUnitWidth - 6), bounds.Height), true);
                    renderer.DrawNumber(g, unit, muted ? Muted : Ink,
                        new RectangleF(bounds.Right - MetricUnitWidth, bounds.Y, MetricUnitWidth - 6, bounds.Height), rightAligned: false);
                    return;
                }
            }
            float typeWidth = showType ? Math.Min(44, Math.Max(0, bounds.Width - 12)) : 0;
            renderer.DrawValue(g, PanelValuePresentation.Visualize(value), font, muted ? Muted : Ink,
                new RectangleF(bounds.X + 6 + marker, bounds.Y, Math.Max(0, bounds.Width - typeWidth - 12 - marker), bounds.Height), numeric);
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
                        bool firstMissing = offset == _offset;
                        if (!firstMissing && offset > 0)
                        {
                            ViewEntry(result, offset - 1, out int previous, out _, out _);
                            firstMissing = previous < 0 || result.Snapshot.Rows[result.RowPositions[previous]].Path != result.Snapshot.Rows[position].Path || column.Row(result.RowPositions[previous]) != null;
                        }
                        float typeWidth = !column.HomogeneousMetric && column.ShowCellTypes ? Math.Min(44, Math.Max(0, cell.Width - 12)) : 0;
                        if (column.Pair != null && firstMissing) renderer.DrawCell(g, "—", small, Muted,
                            new RectangleF(cell.X + 6, cell.Y, Math.Max(0, cell.Width - 12 - typeWidth), RowHeight), column.Numeric || column.HomogeneousMetric);
                    }
                    else if (column.Present && column.Pair != null && column.Snapshot != null) DrawTableCell(g, renderer, result.DisplaySet == null ? row : null, ColumnCell(result, position, columnIndex), cell, font, small,
                        showType: result.DisplaySet == null && column.CellShowsType(row), numeric: result.DisplaySet == null && column.CellIsNumeric(row),
                        metricLane: result.DisplaySet == null ? column.MetricLane(row) : "",
                        muted: result.DisplaySet == null && column.HomogeneousMetric && row?.GeometryInfo == null);
                });
            }
        }

        private string ExtraNote(PanelQueryResult result, int offset)
        {
            var entry = TableIndex(result).Entries[offset];
            var notes = new List<string>();
            bool hidden = false;
            for (int index = 0; index < entry.Cells.Count; index++)
            {
                int item = entry.Cells[index];
                var column = PanelOwner.TableColumns[index];
                if (item < 0 || column.Pair == null) continue;
                var pair = column.Pair.PairAtPaired(item);
                bool only = item - pair.PairedOffset >= pair.Data.ItemCount;
                bool first = offset == 0 || !TableIndex(result).Entries[offset - 1].Extra || TableIndex(result).Entries[offset - 1].Cells[index] < 0 ||
                    !ReferenceEquals(pair, column.Pair.PairAtPaired(TableIndex(result).Entries[offset - 1].Cells[index])) ||
                    (TableIndex(result).Entries[offset - 1].Cells[index] - pair.PairedOffset >= pair.Data.ItemCount) != only;
                if (first) { if (only) notes.Add(PanelOwner.TableHeaderTitle(index + 1)); else hidden = true; }
            }
            return (hidden ? "paired with hidden " + PanelOwner.TableHeaderTitle(0) + " rows" : "") +
                (hidden && notes.Count > 0 ? "; " : "") + (notes.Count > 0 ? "only in " + string.Join(", ", notes) : "");
        }

        private void DrawTableExtra(Graphics g, CanvasRowRenderer renderer, PanelQueryResult result, int offset, RectangleF bounds, Font font, Font small)
        {
            var entry = TableIndex(result).Entries[offset];
            for (int index = 0; index < entry.Cells.Count; index++)
            {
                int item = entry.Cells[index];
                var column = PanelOwner.TableColumns[index];
                PaintColumn(g, index + 1, bounds.Y, RowHeight, cell =>
                {
                    if (item < 0 || column.Snapshot == null || column.Pair == null) return;
                    string value = column.ItemValue(item, PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor);
                    DrawTableCell(g, renderer, column.Snapshot.Rows[item], value, cell, font, small, muted: true, showType: column.CellShowsType(column.Snapshot.Rows[item]), numeric: column.CellIsNumeric(column.Snapshot.Rows[item]), metricLane: column.MetricLane(column.Snapshot.Rows[item]));
                });
            }
            string note = ExtraNote(result, offset);
            var state = g.Save();
            g.SetClip(new RectangleF(bounds.X, bounds.Y, _frozenWidth, bounds.Height), CombineMode.Intersect);
            renderer.DrawCell(g, note, small, Muted, new RectangleF(bounds.X + 6, bounds.Y, Math.Max(0, _frozenWidth - 12), bounds.Height));
            g.Restore(state);
        }

        private PanelPairedData.PairHeading? ColumnHeading(string path, int column) =>
            PanelOwner.TableColumns[column].Pair?.Heading(path, PanelPairedData.EmptyPartnersInScope(PanelOwner.Session.Options));
        private string? ColumnLengthWarning(string path, int column) => ColumnHeading(path, column) is PanelPairedData.PairHeading heading && heading.Unequal
            ? PanelOwner.TableHeaderTitle(column + 1) + " has " + heading.Total + " items; " + PanelOwner.TableHeaderTitle(0) + " has " + heading.DataTotal + "." : null;
        private string[] LengthWarnings(string path) => Enumerable.Range(0, PanelOwner.TableColumns.Count)
            .Select(column => ColumnLengthWarning(path, column)).Where(value => value != null).Cast<string>().ToArray();

        private void DrawTableBadge(Graphics g, string path, RectangleF bounds, Font small, StringFormat trim)
        {
            for (int column = 0; column < PanelOwner.TableColumns.Count; column++)
            {
                if (!(ColumnHeading(path, column) is PanelPairedData.PairHeading heading) || !heading.Unequal) continue;
                string text = PanelTableText.LengthBadge(heading.Total, heading.DataTotal);
                float width = TightWidth(g, text, small) + 10;
                PaintColumn(g, column + 1, bounds.Y, bounds.Height, cell =>
                {
                    var badge = new RectangleF(cell.X + 6, bounds.Y + 1, width, bounds.Height - 2);
                    using var pen = new Pen(PanelTheme.PairingWarning, 0.75f);
                    g.DrawRectangle(pen, badge.X, badge.Y, badge.Width, badge.Height);
                    using var untrimmed = (StringFormat)trim.Clone();
                    untrimmed.Trimming = StringTrimming.None;
                    Text(g, text, small, PanelTheme.PairingWarning, new RectangleF(badge.X + 4, badge.Y, badge.Width - 8, badge.Height), untrimmed);
                });
            }
        }

        private void RenderTableRows(Graphics g, PanelQueryResult result, RectangleF visibleRegion, Font font, Font small, Font mono, StringFormat trim)
        {
            bool sorted = PanelOwner.Session.Options.SortMode != PanelSortMode.SourceOrder;
            using var renderer = new CanvasRowRenderer(g, result.Snapshot.Rows.Count, sorted);
            if (PanelOwner.CreateSetTree) SizeSetColumn(g, renderer, result);
            PrepareTable(g, result, font, renderer.GutterWidth);
            DrawTableHeader(g, font, trim, renderer.GutterWidth);
            float y = _list.Y;
            string? previous = null;
            var measurements = new List<int>();
            var extras = new List<int>();
            var clip = g.Save();
            g.SetClip(_list, CombineMode.Intersect);
            for (int offset = _offset; offset < ViewCount(result); offset++)
            {
                ViewEntry(result, offset, out int item, out string? empty, out int extra);
                string path = ViewPath(result, offset);
                if (path != previous)
                {
                    if (y + BranchHeight + (empty == null ? RowHeight : 0) > _list.Bottom) break;
                    var heading = new RectangleF(_list.X, y, _list.Width, BranchHeight);
                    Fill(g, PanelTheme.Rgb(236, 241, 235), heading);
                    string count = CanvasStatistics.BranchCount(result, path);
                    float countWidth = Math.Min(_frozenWidth / 2, TightWidth(g, count, small) + 6);
                    Text(g, path, mono, Accent, new RectangleF(heading.X + 6, y, Math.Max(0, _frozenWidth - countWidth - 12), BranchHeight), trim);
                    Text(g, count, small, Muted, new RectangleF(heading.X + _frozenWidth - countWidth - 6, y, countWidth, BranchHeight), trim);
                    DrawBranchExtras(g, result, heading, path, mono, small, trim, labelBounds: new RectangleF(heading.X + 6, y, Math.Max(0, _frozenWidth - countWidth - 12), BranchHeight));
                    DrawTableBadge(g, path, heading, small, trim);
                    y += BranchHeight; previous = path;
                }
                if (empty != null) { _shown++; continue; }
                if (y + RowHeight > _list.Bottom) break;
                var bounds = new RectangleF(_list.X, y, _list.Width, RowHeight);
                if (extra >= 0) { Fill(g, PanelTheme.Background, bounds); DrawTableExtra(g, renderer, result, offset, bounds, font, small); if (bounds.IntersectsWith(visibleRegion)) extras.Add(offset); }
                else
                {
                    int position = result.RowPositions[item];
                    var row = result.Snapshot.Rows[position];
                    bool selected = _selection.Contains(position);
                    Fill(g, selected ? PanelTheme.Rgb(216, 232, 219) : (_shown & 1) == 0 ? PanelTheme.Background : PanelTheme.Rgb(247, 249, 246), bounds);
                    var address = RowAddress(result, position);
                    renderer.DrawAddress(g, new RectangleF(bounds.X, y, _frozenWidth, RowHeight), item + 1, address.Index, address.Sorted, selected);
                    if (selected) Fill(g, Accent, new RectangleF(bounds.X, y, 2, RowHeight));
                    var frozen = new RectangleF(bounds.X, y, _frozenWidth, RowHeight);
                    var dataClip = g.Save(); g.SetClip(frozen, CombineMode.Intersect);
                    DrawDataCell(g, renderer, result, position, item + 1, frozen, renderer.GutterWidth,
                        PanelOwner.CreateSetTree ? _setCountWidth : 0, font, small, mono);
                    g.Restore(dataClip);
                    using (var separator = new Pen(PanelTheme.Rgb(224, 229, 222))) g.DrawLine(separator, frozen.Right - 0.5f, y, frozen.Right - 0.5f, y + RowHeight);
                    DrawTableCells(g, renderer, result, position, offset, bounds, font, small);
                    if (TargetPainted(g, bounds) && !_rowHits.Any(hit => hit.Bounds == bounds && hit.SourcePosition == position)) _rowHits.Add(new RowHit(bounds, position, item + 1));
                    if (bounds.IntersectsWith(visibleRegion)) measurements.Add(position);
                    if (_firstShownItem < 0) _firstShownItem = item;
                    _lastShownItem = item;
                }
                _shown++; y += RowHeight;
            }
            g.Restore(clip);
            DrawFrozenEdge(g, y);
            RequestVisibleTableGeometry(result, measurements, extras, Array.Empty<string>());
            if (Count > _shown) { Fill(g, PanelTheme.Rgb(232, 236, 229), _track); Fill(g, PanelTheme.Rgb(153, 174, 156), Thumb()); }
            if (_horizontalMaximum > 0) { Fill(g, PanelTheme.Rgb(232, 236, 229), _horizontalTrack); Fill(g, PanelTheme.Rgb(153, 174, 156), HorizontalThumb()); }
        }

        private void RequestVisibleTableGeometry(PanelQueryResult result, IReadOnlyList<int> data,
            IReadOnlyList<int> extras, IReadOnlyList<string> branches)
        {
            var requests = new List<int[]>();
            for (int index = 0; index < PanelOwner.TableColumns.Count; index++)
            {
                var column = PanelOwner.TableColumns[index];
                var visible = index + 1 < _columnRects.Count
                    ? RectangleF.Intersect(new RectangleF(_columnRects[index + 1].X, _list.Y, _columnRects[index + 1].Width, _list.Height),
                        ColumnClip(index + 1, _list.Y, _list.Height)) : RectangleF.Empty;
                if (visible.Width <= 0 || !visible.IntersectsWith(_renderVisibleRegion)) { requests.Add(Array.Empty<int>()); continue; }
                var items = data.SelectMany(position => column.CellItems(result, position))
                    .Concat(branches.SelectMany(path => column.BranchItems(result, path)))
                    .Concat(extras.Select(offset => TableIndex(result).Entries[offset].Cells[index]).Where(item => item >= 0));
                requests.Add(items.Distinct().ToArray());
            }
            PanelOwner.Session.RequestTableMeasurements(result.Snapshot, data, requests);
        }

        private RectangleF HorizontalThumb()
        {
            float width = Math.Max(20, _horizontalTrack.Width * _horizontalTrack.Width / (_horizontalTrack.Width + _horizontalMaximum));
            return new RectangleF(_horizontalTrack.X + (_horizontalTrack.Width - width) * _horizontalOffset / Math.Max(1, _horizontalMaximum), _horizontalTrack.Y, width, _horizontalTrack.Height);
        }
        private void ScrollHorizontal(GH_Canvas canvas, float value)
        { _horizontalOffset = Math.Max(0, Math.Min(_horizontalMaximum, value)); ClearHover(); InvalidatePaintedHits(); canvas.Invalidate(); }

        private void LayoutHorizontalTrack()
        {
            _horizontalTrack = _horizontalTrackHit = RectangleF.Empty;
            if (_horizontalMaximum <= 0 || !TableVisible) return;
            _horizontalTrack = new RectangleF(_list.X + _frozenWidth, _list.Bottom + 2, Math.Max(0, _list.Width - _frozenWidth), 4);
            _horizontalTrackHit = new RectangleF(_horizontalTrack.X, _list.Bottom, _horizontalTrack.Width, 10);
        }
        private void DrawFrozenEdge(Graphics g, float bottom)
        {
            float height = Math.Min(_list.Bottom, bottom) - _list.Y;
            if (height <= 0) return;
            float edge = _list.X + _frozenWidth;
            if (_horizontalOffset > 0)
                using (var shadow = new LinearGradientBrush(new RectangleF(edge, _list.Y, 2, height),
                    PanelTheme.Rgb(60, 80, 90, 75), Color.Transparent, LinearGradientMode.Horizontal))
                    g.FillRectangle(shadow, edge, _list.Y, 2, height);
            using var line = new Pen(PanelTheme.Rgb(193, 205, 191));
            g.DrawLine(line, edge, _list.Y, edge, _list.Y + height);
        }
        private IEnumerable<RectangleF> HeaderHoverRects()
        {
            for (int index = 0; index < _columnRects.Count; index++)
            {
                if (_paintedCanvas == null || !HeaderTargetPainted(_paintedCanvas, index)) continue;
                var rect = RectangleF.Intersect(_columnRects[index], ColumnClip(index, _tableHeader.Y, RowHeight));
                rect.X += 4; rect.Width = Math.Max(0, rect.Width - 8);
                yield return rect;
            }
        }
        private int HeaderColumnAt(PointF point, bool border = false)
        {
            if (!TableVisible || point.Y < _tableHeader.Top || point.Y >= _tableHeader.Bottom || !border && !_tableHeader.Contains(point)) return -1;
            if (border)
            {
                int nearest = -1; float distance = 4.01f;
                for (int index = 0; index < _columnRects.Count; index++)
                {
                    float edge = _columnRects[index].Right;
                    var clip = ColumnClip(index, _tableHeader.Y, RowHeight);
                    if (edge < clip.Left || edge > clip.Right) continue;
                    float delta = Math.Abs(point.X - edge);
                    if (delta <= 4 && delta < distance) { nearest = index; distance = delta; }
                }
                return nearest;
            }
            for (int index = 0; index < _columnRects.Count; index++)
                if (RectangleF.Intersect(_columnRects[index], ColumnClip(index, _tableHeader.Y, RowHeight)).Contains(point)) return index;
            return -1;
        }
        internal float DColumnWidthLimit => Math.Max(PanelTableText.MinimumColumnWidth,
            (_list.Width > 0 ? _list.Width : PanelOwner.DisplayWidth - 33) - PanelTableText.MinimumColumnWidth -
            (_columnRects.Count > 0 ? _frozenWidth - _columnRects[0].Width : 24));
        private bool HeaderCanSort(int index) => index == 0 || index > 0 && index <= PanelOwner.TableColumns.Count &&
            PanelOwner.TableColumns[index - 1].Present && PanelOwner.TableColumns[index - 1].Pair != null;
        private bool HeaderSortEnabled
        {
            get
            {
                int index = _headerSortColumn == Guid.Empty ? 0 : -1;
                if (_headerSortColumn != Guid.Empty)
                    for (int column = 0; column < PanelOwner.TableColumns.Count; column++)
                        if (PanelOwner.TableColumns[column].Input.InstanceGuid == _headerSortColumn) { index = column + 1; break; }
                return _paintedCanvas != null && HeaderTargetPainted(_paintedCanvas, index) && HeaderCanSort(index);
            }
        }

        private string ColumnBranchCell(string path, int column) => PanelOwner.TableColumns[column].BranchCell(PanelOwner.Session.Result, path, PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor);

        private int CellColumnAt(PointF point)
        {
            if (!TableVisible || point.X < _list.X + _frozenWidth) return -1;
            for (int index = 1; index < _columnRects.Count; index++)
                if (point.X >= _columnRects[index].X && point.X < _columnRects[index].Right) return index - 1;
            return -1;
        }
        private GH_ObjectResponse? ClickTableHeader(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            int border = HeaderColumnAt(e.CanvasLocation, true);
            int index = border >= 0 ? border : HeaderColumnAt(e.CanvasLocation);
            if (!HeaderTargetPainted(canvas, index, border >= 0)) return GH_ObjectResponse.Handled;
            if (border >= 0)
            {
                if (e.Clicks > 1) return GH_ObjectResponse.Handled;
                _resizingColumn = ColumnId(index); _columnDragStart = e.CanvasX;
                _columnDragWidth = _columnPreviewWidth = _columnRects[index].Width;
                return GH_ObjectResponse.Capture;
            }
            if (!HeaderCanSort(index)) return GH_ObjectResponse.Handled;
            var options = PanelOwner.Session.Options;
            var id = index == 0 ? Guid.Empty : ColumnId(index);
            var next = options.SortColumn != id || options.SortMode == PanelSortMode.SourceOrder ? PanelSortMode.ValueAscending :
                options.SortMode == PanelSortMode.ValueAscending ? PanelSortMode.ValueDescending : PanelSortMode.SourceOrder;
            PanelOwner.SetColumnSort(id, next); canvas.Invalidate(); return GH_ObjectResponse.Handled;
        }
        private bool HeaderContext(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            int index = HeaderColumnAt(e.CanvasLocation);
            if (!HeaderTargetPainted(canvas, index)) return false;
            if (!HeaderCanSort(index)) return true;
            _headerSortColumn = index == 0 ? Guid.Empty : ColumnId(index);
            _headerMenuAnchor = RectangleF.Intersect(_columnRects[index], ColumnClip(index, _tableHeader.Y, RowHeight));
            _menu.HeaderSortColumn = _headerSortColumn;
            OpenMenu(CanvasMenuKind.HeaderSort, canvas);
            _rowContextHandled = true;
            return true;
        }
        private void TooltipTableHeader(PointF point, GH_TooltipDisplayEventArgs e)
        {
            int index = HeaderColumnAt(point);
            if (_paintedCanvas == null || !HeaderTargetPainted(_paintedCanvas, index)) return;
            e.Title = ColumnTitle(index); e.Text = "";
            e.Description = "Click to sort ascending, descending, then tree order. Drag a border to resize; double-click it to auto-fit.";
            if (index > 0)
            {
                var column = PanelOwner.TableColumns[index - 1];
                if (!column.ShowCellTypes && column.Snapshot?.TypeCounts.Count == 1)
                    e.Text = column.Snapshot.TypeCounts.Keys.First();
                if (column.HomogeneousMetric) e.Text = column.MetricTooltip(PanelOwner.UnitConversion);
                if (!column.Present) e.Description = PanelOwner.TableHeaderTitle(index) + " is not wired. Sorting is unavailable.";
                else if (column.Pair == null) e.Description = column.Tree == null || column.Tree.DataCount == 0 ? PanelOwner.TableHeaderTitle(index) + " has no usable data. Sorting is unavailable." :
                    PanelOwner.TableHeaderTitle(index) + " has " + column.Tree.PathCount + " branches; " + PanelOwner.TableHeaderTitle(0) + " has " + PanelOwner.InputBranchCount + ". Sorting is unavailable.";
            }
            if (PanelOwner.HasHeaderTitle(index))
                e.Description += "\nInput: " + (index == 0 ? PanelOwner.Params.Input[0].NickName : PanelOwner.TableColumns[index - 1].Input.NickName);
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
