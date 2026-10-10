using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

// Grasshopper's canvas drawing contract is supplied by Rhino on Windows and Mac.
#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed class BetterPanelAttributes : GH_ComponentAttributes
    {
        private const float RowHeight = 17, BranchHeight = 15, HeaderHeight = CanvasPanelSize.HeaderHeight;
        private static readonly Color Ink = Color.FromArgb(49, 55, 53);
        private static readonly Color Muted = Color.FromArgb(120, 127, 123);
        private static readonly Color Accent = Color.FromArgb(77, 115, 94);
        private RectangleF _panel, _search, _type, _sort, _branch, _jump, _reset, _resize, _list, _track, _footer;
        private RectangleF _trim, _simplify, _flatten, _reverse, _units;
        private RectangleF _display;
        private readonly List<RowHit> _rowHits = new List<RowHit>();
        private int _offset, _shown, _selectedPosition = -1;
        private PanelQueryResult? _lastResult;
        private PanelSnapshot? _selectedSnapshot;
        private readonly CanvasMenu _menu = new CanvasMenu();
        private bool _resizing, _scrolling;
        private PointF _dragStart;
        private float _startWidth, _startHeight, _thumbGrab;
        private DateTime _copiedUntil;
        private BetterPanelComponent PanelOwner => (BetterPanelComponent)Owner;
        private int Count => PanelOwner.Session.Result.RowPositions.Count;
        private int MaximumOffset => Math.Max(0, Count - Math.Max(1, _shown));

        internal BetterPanelAttributes(BetterPanelComponent owner) : base(owner) { }

        internal void ResetViewState()
        {
            _offset = 0;
            _selectedPosition = -1;
            _selectedSnapshot = null;
            _copiedUntil = default;
            _scrolling = false;
            _menu.Close();
            _rowHits.Clear();
        }

        protected override void Layout()
        {
            _panel = new RectangleF(Pivot.X,
                Pivot.Y + CanvasPanelSize.DisplayTopOffset(PanelOwner.CanvasHeight, PanelOwner.CanvasDisplay),
                PanelOwner.CanvasWidth, PanelOwner.DisplayHeight);
            _rowHits.Clear();
            m_innerBounds = _panel;
            float x = _panel.X + 4;
            _search = new RectangleF(x + 2, _panel.Y + 25, _panel.Width - 212, 21);
            _type = new RectangleF(_search.Right + 3, _search.Y, 76, 21);
            _sort = new RectangleF(_type.Right + 3, _search.Y, 43, 21);
            _branch = new RectangleF(_sort.Right + 3, _search.Y, 72, 21);
            _display = new RectangleF(_panel.Right - 26, _panel.Y + 2, 20, 18);
            _jump = new RectangleF(_display.Left - 48, _panel.Y + 2, 20, 18);
            _reset = new RectangleF(_jump.Right + 4, _jump.Y, 20, 18);
            _footer = new RectangleF(x + 2, _panel.Bottom - 36, _panel.Width - 12, 32);
            _units = new RectangleF(_footer.Right - 85, _footer.Y + 15, 76, 17);
            _trim = new RectangleF(_units.Left - 101, _footer.Y + 14, 22, 20);
            _simplify = new RectangleF(_trim.Right + 2, _trim.Y, 22, 20);
            _flatten = new RectangleF(_simplify.Right + 2, _trim.Y, 22, 20);
            _reverse = new RectangleF(_flatten.Right + 2, _trim.Y, 22, 20);
            _list = new RectangleF(x, _panel.Y + 52, _panel.Width - 15, Math.Max(18, _panel.Height - 90));
            _track = new RectangleF(_panel.Right - 8, _list.Y, 4, _list.Height);
            _resize = new RectangleF(_panel.Right - 13, _panel.Bottom - 13, 13, 13);
            // Linked parameter attributes keep Grasshopper's wire/tooltip behavior, but
            // expose actual wire grips at our rectangle instead of a hidden default capsule.
            PositionPorts(Owner.Params.Input, true);
            PositionPorts(Owner.Params.Output, false);
            Bounds = new RectangleF(_panel.X - 6, _panel.Y, _panel.Width + 12, _panel.Height);
            LayoutMenu();
        }

        private void PositionPorts(IList<IGH_Param> parameters, bool input)
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                var port = parameters[i].Attributes as PanelPortAttributes;
                if (port == null)
                {
                    port = new PanelPortAttributes(parameters[i], this, input);
                    parameters[i].Attributes = port;
                }
                // Folding changes only the visible rectangle, never the wire endpoints.
                var point = new PointF(input ? _panel.Left : _panel.Right,
                    Pivot.Y + HeaderHeight + (PanelOwner.CanvasHeight - HeaderHeight) * (i + 1) / (parameters.Count + 1));
                port.Place(point);
            }
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            if (channel == GH_CanvasChannel.Wires) { base.Render(canvas, graphics, channel); return; }
            if (channel != GH_CanvasChannel.Objects) return;
            CanvasTextInput.Maintain(canvas);
            var session = PanelOwner.Session;
            var visibleRegion = canvas.Viewport.VisibleRegion;
            // Grasshopper also renders components outside the canvas viewport.
            if (!Bounds.IntersectsWith(visibleRegion))
            {
                _rowHits.Clear(); _shown = 0; _menu.Close();
                session.RequestGeometryMeasurements(session.Snapshot, Array.Empty<int>());
                return;
            }
            var result = session.Result;
            if (!ReferenceEquals(result, _lastResult))
            {
                if (_selectedPosition >= 0 && (_selectedSnapshot != result.Snapshot || !result.RowPositions.Contains(_selectedPosition))) _selectedPosition = -1;
                _lastResult = result;
                _offset = 0;
                _menu.Close();
            }
            if (!_menu.IsCurrent(session.Snapshot)) _menu.Close();
            // Layout is measured in canvas units. Pixel fonts follow the canvas
            // transform without adding the display DPI a second time.
            using (var font = new Font("Arial", 11f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var small = new Font("Arial", 10f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var bold = new Font("Arial", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var mono = new Font(FontFamily.GenericMonospace, 10f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var trim = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
            {
                Fill(graphics, Color.White, _panel);
                Fill(graphics, Accent, new RectangleF(_panel.X, _panel.Y, 3, HeaderHeight));
                Text(graphics, "P+", bold, Accent, new RectangleF(_panel.X + 8, _panel.Y + 1, 28, 20), trim);
                var itemLabel = result.Statistics.ItemCount == 1 ? "item" : "items";
                var branchLabel = result.Statistics.BranchCount == 1 ? "branch" : "branches";
                Text(graphics, $"{result.Statistics.ItemCount:N0} {itemLabel}  ·  {result.Statistics.BranchCount:N0} {branchLabel}", small, Muted, new RectangleF(_panel.X + 39, _panel.Y + 1, _panel.Width - 117, 20), trim);
                if (canvas.Viewport.Zoom >= 0.5f)
                    DrawDisplayAction(graphics);
                if (canvas.Viewport.Zoom >= 0.5f)
                {
                    var options = session.Options;
                    Control(graphics, _search, string.IsNullOrEmpty(options.Search) ? "Search / > 800…" : options.Search, small, trim, !string.IsNullOrEmpty(options.Search));
                    DropdownControl(graphics, _type, CanvasMenu.TypeHint(result, options), small, trim, options.TypeName != null || options.OnlyInvalid);
                    DropdownControl(graphics, _sort, options.SortMode == PanelSortMode.SourceOrder ? "Src" : options.SortMode == PanelSortMode.ValueAscending ? "↑" : "↓", small, trim, options.SortMode != PanelSortMode.SourceOrder);
                    DropdownControl(graphics, _branch, Short(options.BranchPath ?? "Paths", 80), mono, trim, options.BranchPath != null);
                    DrawViewAction(graphics, _jump, false);
                    DrawViewAction(graphics, _reset, true);
                    RenderRows(graphics, result, visibleRegion, font, small, mono, trim);
                    RenderFooter(graphics, result, small, mono, trim);
                    if (_menu.Kind != CanvasMenuKind.None) RenderMenu(graphics, small, mono, trim);
                    using (var gripPen = new Pen(Color.FromArgb(165, 173, 166), 1))
                    {
                        graphics.DrawLine(gripPen, _panel.Right - 10, _panel.Bottom - 4, _panel.Right - 4, _panel.Bottom - 10);
                        graphics.DrawLine(gripPen, _panel.Right - 6, _panel.Bottom - 4, _panel.Right - 4, _panel.Bottom - 6);
                    }
                }
                else
                {
                    _rowHits.Clear(); _shown = 0; _menu.Close();
                    session.RequestGeometryMeasurements(session.Snapshot, Array.Empty<int>());
                }
                using (var border = new Pen(Selected ? Accent : Color.FromArgb(164, 171, 164), Selected ? 1.5f : 1))
                    graphics.DrawRectangle(border, _panel.X, _panel.Y, _panel.Width, _panel.Height);
                foreach (var parameter in Owner.Params.Input)
                    GH_CapsuleRenderEngine.RenderInputGrip(graphics, canvas.Viewport.Zoom, parameter.Attributes.InputGrip, false);
                foreach (var parameter in Owner.Params.Output)
                    GH_CapsuleRenderEngine.RenderOutputGrip(graphics, canvas.Viewport.Zoom, parameter.Attributes.OutputGrip, false);
            }
        }

        private void RenderRows(Graphics g, PanelQueryResult result, RectangleF visibleRegion, Font font, Font small, Font mono, StringFormat trim)
        {
            _rowHits.Clear();
            _shown = 0;
            float y = _list.Y;
            string? previousPath = null;
            // A half-height panel at the minimum saved size still has room for a
            // value, even when a separate branch heading would consume that row.
            bool grouped = PanelOwner.Session.Options.SortMode == PanelSortMode.SourceOrder &&
                _list.Height >= BranchHeight + RowHeight;
            bool showTypes = result.Statistics.TypeCounts.Count > 1 || PanelOwner.Session.Options.OnlyInvalid;
            using var typeFormat = new StringFormat(trim) { Alignment = StringAlignment.Far };
            _offset = Math.Max(0, Math.Min(Math.Max(0, result.RowPositions.Count - 1), _offset));
            var clip = g.Save();
            try
            {
                g.SetClip(_list, CombineMode.Intersect);
                for (int i = _offset; i < result.RowPositions.Count; i++)
                {
                    int sourcePosition = result.RowPositions[i];
                    var row = result.Snapshot.Rows[sourcePosition];
                    if (grouped && row.Path != previousPath)
                    {
                        if (y + BranchHeight + RowHeight > _list.Bottom) break;
                        Fill(g, Color.FromArgb(236, 241, 235), new RectangleF(_list.X, y, _list.Width, BranchHeight));
                        Text(g, row.Path, mono, Accent, new RectangleF(_list.X + 6, y, _list.Width - 12, BranchHeight), trim);
                        y += BranchHeight;
                        previousPath = row.Path;
                    }
                    if (y + RowHeight > _list.Bottom) break;
                    var bounds = new RectangleF(_list.X, y, _list.Width, RowHeight);
                    Fill(g, sourcePosition == _selectedPosition ? Color.FromArgb(216, 232, 219) : (_shown & 1) == 0 ? Color.White : Color.FromArgb(247, 249, 246), bounds);
                    if (!row.IsValid) Fill(g, Color.FromArgb(180, 106, 67), new RectangleF(bounds.X, bounds.Y + 3, 2, bounds.Height - 6));
                    Text(g, row.OriginalIndex.ToString(CultureInfo.InvariantCulture), mono, Muted, new RectangleF(bounds.X + 6, y, 29, RowHeight), trim);
                    float valueStart = 39;
                    if (!grouped)
                    {
                        Text(g, row.Path, mono, Accent, new RectangleF(bounds.X + 44, y, 68, RowHeight), trim);
                        valueStart = 116;
                    }
                    // Geometry summaries already name their type; reserve the extra
                    // label only for values whose text does not identify their type.
                    bool showRowType = showTypes && row.GeometryInfo == null;
                    Text(g, PanelOwner.DisplayValue(result.Snapshot, row, sourcePosition), font, Ink,
                        new RectangleF(bounds.X + valueStart, y, bounds.Width - valueStart - (showRowType ? 70 : 4), RowHeight), trim);
                    if (showRowType)
                        Text(g, CanvasMenu.ShortTypeName(row.TypeName), small, Muted, new RectangleF(bounds.Right - 64, y, 60, RowHeight), typeFormat);
                    _rowHits.Add(new RowHit(bounds, sourcePosition));
                    _shown++;
                    y += RowHeight;
                }
                if (result.RowPositions.Count == 0)
                {
                    Text(g, result.Snapshot.Rows.Count == 0 ? "Connect a tree to start exploring." : "No matching items. Clear a filter or Reset.", small, Muted, new RectangleF(_list.X + 6, _list.Y + 12, _list.Width - 12, 22), trim);
                }
            }
            finally { g.Restore(clip); }
            PanelOwner.Session.RequestGeometryMeasurements(result.Snapshot,
                _rowHits.Where(hit => hit.Bounds.IntersectsWith(visibleRegion)).Select(hit => hit.SourcePosition));
            if (Count > _shown)
            {
                Fill(g, Color.FromArgb(232, 236, 229), _track);
                Fill(g, Color.FromArgb(153, 174, 156), Thumb());
            }
        }

        private void RenderFooter(Graphics g, PanelQueryResult result, Font small, Font mono, StringFormat trim)
        {
            var stats = result.Statistics;
            string metrics = stats.NumericCount > 0
                ? $"min {Number(stats.Minimum, result.Snapshot)}   max {Number(stats.Maximum, result.Snapshot)}   avg {Number(stats.Mean, result.Snapshot)}"
                : $"{stats.TypeCounts.Count:N0} types   ·   {stats.NullCount:N0} null   ·   {stats.InvalidCount:N0} invalid";
            Text(g, metrics, mono, Ink, new RectangleF(_footer.X, _footer.Y, _footer.Width - 5, 14), trim);
            var session = PanelOwner.Session;
            string output = PanelOwner.HasTreeOperations || PanelOwner.UnitConversion != PanelUnitConversion.None ? "out: tools" : "out: source";
            string status = session.Error != null ? "Query failed: " + Short(session.Error, 150) : session.IsBusy ? "Updating…" : DateTime.UtcNow < _copiedUntil ? "Value copied" : $"{(Count == 0 ? 0 : _offset + 1):N0}–{Math.Min(Count, _offset + _shown):N0}/{Count:N0} · {output}";
            Text(g, status, small, Muted, new RectangleF(_footer.X, _footer.Y + 15, _trim.Left - _footer.X - 6, 14), trim);
            TreeToolIcons.Draw(g, _trim, TreeToolIcons.Trim, PanelOwner.TrimDepth > 0);
            TreeToolIcons.Draw(g, _simplify, TreeToolIcons.Simplify, PanelOwner.SimplifyTree);
            TreeToolIcons.Draw(g, _flatten, TreeToolIcons.Flatten, PanelOwner.FlattenTree);
            TreeToolIcons.Draw(g, _reverse, TreeToolIcons.Reverse, PanelOwner.ReverseTree);
            string unitLabel = PanelOwner.UnitConversion == PanelUnitConversion.None ? "Units" : PanelUnits.Label(PanelOwner.UnitConversion).Replace(" → ", "→");
            string precision = PanelOwner.DecimalPlaces < 0 ? "" : $" · {PanelOwner.DecimalPlaces}dp";
            DropdownControl(g, _units, unitLabel + precision, small, trim,
                PanelOwner.UnitConversion != PanelUnitConversion.None || PanelOwner.DecimalPlaces >= 0);
        }

        private RectangleF Thumb()
        {
            float height = Math.Min(_track.Height, Math.Max(20, _track.Height * Math.Max(1, _shown) / Math.Max(1, Count)));
            float y = _track.Y + (_track.Height - height) * _offset / Math.Max(1, MaximumOffset);
            return new RectangleF(_track.X, y, _track.Width, height);
        }

        private void SetOffset(GH_Canvas canvas, int value)
        {
            _offset = Math.Max(0, Math.Min(MaximumOffset, value));
            canvas.Invalidate();
        }

        private void LayoutMenu() => _menu.Layout(_panel, _type, _sort, _branch, _units);

        private void RenderMenu(Graphics g, Font small, Font mono, StringFormat trim)
        {
            LayoutMenu();
            Fill(g, Color.FromArgb(32, 49, 55, 53), new RectangleF(_menu.Bounds.X + 1, _menu.Bounds.Y + 2, _menu.Bounds.Width, _menu.Bounds.Height));
            Fill(g, Color.White, _menu.Bounds);
            int visible = _menu.Visible;
            var options = PanelOwner.Session.Options;
            for (int i = 0; i < visible && i + _menu.Offset < _menu.Count; i++)
            {
                int index = i + _menu.Offset;
                var row = new RectangleF(_menu.Bounds.X + 1, _menu.Bounds.Y + i * 20, _menu.Bounds.Width - 2, 20);
                bool selected = _menu.IsSelected(index, options, PanelOwner.UnitConversion, PanelOwner.DecimalPlaces);
                if (index == _menu.HoveredIndex || selected)
                    Fill(g, index == _menu.HoveredIndex ? Color.FromArgb(226, 236, 226) : Color.FromArgb(242, 247, 241), row);
                if (_menu.Kind == CanvasMenuKind.Units && index == PanelUnits.ConversionCount)
                    using (var divider = new Pen(Color.FromArgb(225, 231, 224)))
                        g.DrawLine(divider, row.X + 7, row.Y, row.Right - 7, row.Y);
                if (selected)
                    using (var check = new Pen(Accent, 1.3f))
                        g.DrawLines(check, new[] { new PointF(row.X + 7, row.Y + 10), new PointF(row.X + 10, row.Y + 13), new PointF(row.X + 16, row.Y + 7) });
                Text(g, _menu.Label(index, PanelOwner.DecimalPlaces), _menu.Kind == CanvasMenuKind.Branch ? mono : small,
                    selected ? Accent : Ink, new RectangleF(row.X + 23, row.Y, row.Width - 30, 20), trim);
            }
            if (_menu.Count > visible)
            {
                float y = _menu.Bounds.Bottom - 20;
                Fill(g, Color.FromArgb(235, 241, 232), new RectangleF(_menu.Bounds.X, y, _menu.Bounds.Width, 20));
                Text(g, "‹ Previous", small, Accent, new RectangleF(_menu.Bounds.X + 7, y, _menu.Bounds.Width / 2 - 7, 20), trim);
                Text(g, "Next ›", small, Accent, new RectangleF(_menu.Bounds.X + _menu.Bounds.Width / 2, y, _menu.Bounds.Width / 2 - 7, 20), trim);
            }
            using (var pen = new Pen(Color.FromArgb(194, 205, 194))) g.DrawRectangle(pen, _menu.Bounds.X, _menu.Bounds.Y, _menu.Bounds.Width, _menu.Bounds.Height);
        }

        private void OpenMenu(CanvasMenuKind kind, GH_Canvas canvas)
        {
            var session = PanelOwner.Session;
            _menu.Toggle(kind, session.Snapshot, session.Options);
            LayoutMenu();
            canvas.Invalidate();
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (sender.Viewport.Zoom < 0.5f) return base.RespondToMouseDown(sender, e);
            if (e.Button != MouseButtons.Left) return base.RespondToMouseDown(sender, e);
            var point = e.CanvasLocation;
            if (_display.Contains(point))
            {
                _menu.Close();
                PanelOwner.SetCanvasDisplay(CanvasPanelSize.NextDisplay(PanelOwner.CanvasDisplay));
                PerformLayout();
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            if (_reset.Contains(point))
            {
                PanelOwner.ResetPanel();
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            if (_menu.Kind != CanvasMenuKind.None)
            {
                if (!_menu.IsCurrent(PanelOwner.Session.Snapshot))
                {
                    _menu.Close();
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
                if (_menu.TrySelect(point, out var index)) ChooseMenu(index, sender);
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            if (_resize.Contains(point))
            {
                PanelOwner.RecordUndoEvent("Resize Panel+");
                _resizing = true; _dragStart = point; _startWidth = _panel.Width; _startHeight = _panel.Height;
                return GH_ObjectResponse.Capture;
            }
            if (_track.Contains(point) && Count > _shown)
            {
                var thumb = Thumb();
                if (thumb.Contains(point)) { _scrolling = true; _thumbGrab = point.Y - thumb.Y; return GH_ObjectResponse.Capture; }
                SetOffset(sender, _offset + (point.Y < thumb.Y ? -Math.Max(1, _shown) : Math.Max(1, _shown)));
                return GH_ObjectResponse.Handled;
            }
            if (_trim.Contains(point))
            {
                CanvasTextInput.Show(sender, PanelOwner, _trim, PanelOwner.TrimDepth.ToString(), text =>
                {
                    if (int.TryParse(text, out var depth) && depth >= 0)
                        PanelOwner.SetTreeOperations(depth, PanelOwner.FlattenTree, PanelOwner.ReverseTree, PanelOwner.SimplifyTree);
                });
                return GH_ObjectResponse.Handled;
            }
            if (_simplify.Contains(point) || _flatten.Contains(point) || _reverse.Contains(point))
            {
                PanelOwner.SetTreeOperations(PanelOwner.TrimDepth,
                    _flatten.Contains(point) ? !PanelOwner.FlattenTree : PanelOwner.FlattenTree,
                    _reverse.Contains(point) ? !PanelOwner.ReverseTree : PanelOwner.ReverseTree,
                    _simplify.Contains(point) ? !PanelOwner.SimplifyTree : PanelOwner.SimplifyTree);
                return GH_ObjectResponse.Handled;
            }
            if (_search.Contains(point)) { EditSearch(sender); return GH_ObjectResponse.Handled; }
            if (_units.Contains(point)) { OpenMenu(CanvasMenuKind.Units, sender); return GH_ObjectResponse.Handled; }
            if (_type.Contains(point)) { OpenMenu(CanvasMenuKind.Type, sender); return GH_ObjectResponse.Handled; }
            if (_sort.Contains(point)) { OpenMenu(CanvasMenuKind.Sort, sender); return GH_ObjectResponse.Handled; }
            if (_branch.Contains(point)) { OpenMenu(CanvasMenuKind.Branch, sender); return GH_ObjectResponse.Handled; }
            if (_jump.Contains(point))
            {
                CanvasTextInput.Show(sender, PanelOwner, _jump, (_offset + 1).ToString(), text =>
                {
                    if (!int.TryParse(text, out var row)) return;
                    // Put the requested item first; grouped branch headings can change
                    // page capacity, so previous-page size must not hide the final item.
                    _offset = Math.Max(0, Math.Min(Math.Max(0, Count - 1), row <= 1 ? 0 : row - 1));
                    sender.Invalidate();
                });
                return GH_ObjectResponse.Handled;
            }
            var currentResult = PanelOwner.Session.Result;
            if (_list.Contains(point) && !ReferenceEquals(_lastResult, currentResult))
            {
                _rowHits.Clear();
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            foreach (var hit in _rowHits)
                if (hit.Bounds.Contains(point) && hit.SourcePosition >= 0 && hit.SourcePosition < currentResult.Snapshot.Rows.Count)
                { _selectedPosition = hit.SourcePosition; _selectedSnapshot = currentResult.Snapshot; sender.Invalidate(); return GH_ObjectResponse.Handled; }
            return base.RespondToMouseDown(sender, e);
        }

        private void ChooseMenu(int index, GH_Canvas canvas)
        {
            if (index < 0 || index >= _menu.Count) return;
            var kind = _menu.Kind;
            _menu.Close();
            if (kind == CanvasMenuKind.Units)
            {
                if (index < PanelUnits.ConversionCount)
                    PanelOwner.SetUnits((PanelUnitConversion)index, PanelOwner.DecimalPlaces);
                else if (index == PanelUnits.ConversionCount)
                    PanelOwner.SetUnits(PanelOwner.UnitConversion, PanelUnits.AutomaticDecimals);
                else CanvasTextInput.Show(canvas, PanelOwner, _units, Math.Max(0, PanelOwner.DecimalPlaces).ToString(), text =>
                {
                    if (int.TryParse(text, out var decimals) && decimals >= 0 && decimals <= PanelUnits.MaximumDecimals)
                        PanelOwner.SetUnits(PanelOwner.UnitConversion, decimals);
                });
                canvas.Invalidate();
                return;
            }
            var options = PanelOwner.Session.Options;
            if (kind == CanvasMenuKind.Type)
            {
                if (index == 1) options.OnlyInvalid = !options.OnlyInvalid;
                else { options.TypeName = index == 0 ? null : _menu.TypeNameAt(index); if (index == 0) options.OnlyInvalid = false; }
            }
            else if (kind == CanvasMenuKind.Sort) options.SortMode = (PanelSortMode)index;
            else if (index == 1)
            {
                CanvasTextInput.Show(canvas, PanelOwner, _branch, options.BranchPath ?? "{0}", text => { var query = PanelOwner.Session.Options; query.BranchPath = string.IsNullOrWhiteSpace(text) ? null : text.Trim(); query.BranchPrefix = false; PanelOwner.Session.UpdateQuery(query); });
                canvas.Invalidate();
                return;
            }
            else { options.BranchPath = index == 0 ? null : _menu.BranchPathAt(index); options.BranchPrefix = false; }
            PanelOwner.Session.UpdateQuery(options);
            canvas.Invalidate();
        }

        private void EditSearch(GH_Canvas canvas) => CanvasTextInput.Show(canvas, PanelOwner, _search, PanelOwner.Session.Options.Search, text => { var options = PanelOwner.Session.Options; options.Search = text; PanelOwner.Session.UpdateQuery(options); });

        public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (_menu.Hover(e.CanvasLocation)) sender.Invalidate();
            if (_resizing)
            {
                // Half's top offset also changes with the saved height. Account
                // for that so the visible corner follows the cursor exactly.
                float height = _startHeight + (e.CanvasY - _dragStart.Y) /
                    (PanelOwner.CanvasDisplay == CanvasPanelDisplay.Half ? 1.5f : 1);
                PanelOwner.SetCanvasSize(_startWidth + e.CanvasX - _dragStart.X,
                    PanelOwner.CanvasDisplay == CanvasPanelDisplay.Half ? height * 2 : height);
                PerformLayout();
                sender.Invalidate();
                return GH_ObjectResponse.Capture;
            }
            if (_scrolling)
            {
                float travel = _track.Height - Thumb().Height;
                if (travel > 0) SetOffset(sender, (int)Math.Round((e.CanvasY - _track.Y - _thumbGrab) / travel * MaximumOffset));
                return GH_ObjectResponse.Capture;
            }
            return base.RespondToMouseMove(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (_resizing || _scrolling) { _resizing = false; _scrolling = false; return GH_ObjectResponse.Release; }
            return base.RespondToMouseUp(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (_display.Contains(e.CanvasLocation))
                return GH_ObjectResponse.Handled;
            if (sender.Viewport.Zoom < 0.5f) return base.RespondToMouseDoubleClick(sender, e);
            var result = PanelOwner.Session.Result;
            if (_list.Contains(e.CanvasLocation) && !ReferenceEquals(_lastResult, result))
            {
                _rowHits.Clear();
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            foreach (var hit in _rowHits)
                if (hit.Bounds.Contains(e.CanvasLocation) && hit.SourcePosition >= 0 && hit.SourcePosition < result.Snapshot.Rows.Count)
                {
                    using (var clipboard = new Eto.Forms.Clipboard()) clipboard.Text = PanelOwner.CopyValue(result.Snapshot, hit.SourcePosition);
                    _copiedUntil = DateTime.UtcNow.AddSeconds(3);
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
            if (_search.Contains(e.CanvasLocation) || _list.Contains(e.CanvasLocation)) { EditSearch(sender); return GH_ObjectResponse.Handled; }
            return base.RespondToMouseDoubleClick(sender, e);
        }

        public override void SetupTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            base.SetupTooltip(point, e);
            if (_display.Contains(point))
            {
                e.Title = PanelOwner.CanvasDisplay == CanvasPanelDisplay.Full ? "Show half panel" : "Show full panel";
                e.Text = "Full ↔ Half";
                e.Description = "Click to toggle panel sizes. Full restores your saved height. Half shows half that height. Wire connections and view settings are retained.";
                return;
            }
            if (_units.Contains(point))
            {
                e.Title = "Units and decimals";
                e.Text = "Numeric output conversion · geometry metric display";
                e.Description = "Choose length (mm ↔ m), area (mm² ↔ m²), volume (mm³ ↔ m³), or angle (rad ↔ deg). Geometry metrics convert only matching model units and dimensions; the geometry itself is unchanged. Decimal places (0–12 or automatic) format metrics, numeric rows, copied numeric values and statistics without changing output precision.";
            }
            else if (_trim.Contains(point) || _simplify.Contains(point) || _flatten.Contains(point) || _reverse.Contains(point))
            {
                e.Title = _trim.Contains(point) ? "Trim tree" : _simplify.Contains(point) ? "Simplify tree" : _flatten.Contains(point) ? "Flatten tree" : "Reverse tree";
                e.Text = (_trim.Contains(point) ? "Depth: " + PanelOwner.TrimDepth + " · " : "") + "Changes output · Simplify → Trim → Flatten → Reverse";
                e.Description = _trim.Contains(point) ? "Enter the number of trailing path levels to remove. Matching branches merge in source order. Fully trimmed paths become {0}. Enter 0 to disable." :
                    _simplify.Contains(point) ? "Toggle removal of path indices shared by all branches." :
                    _flatten.Contains(point) ? "Toggle merging all branches into {0}." : "Toggle reversing items within each output branch.";
            }
            else if (_type.Contains(point) || _sort.Contains(point) || _branch.Contains(point))
            {
                var options = PanelOwner.Session.Options;
                e.Title = _type.Contains(point) ? "Filter by type" : _sort.Contains(point) ? "Sort this view" : "Filter by branch";
                e.Text = _type.Contains(point) ? CanvasMenu.TypeHint(PanelOwner.Session.Result, options) :
                    _sort.Contains(point) ? options.SortMode.ToString() : options.BranchPath ?? "All branches";
                e.Description = "Changes the panel view. Output retains the original tree unless a tree operation or unit conversion is active.";
                if (_type.Contains(point))
                    e.Description = (PanelOwner.Session.Result.RowPositions.Count > 0 ? "Visible types: " : "Source types: ") +
                        CanvasMenu.TypeDetails(PanelOwner.Session.Result) + "\n\n" + e.Description;
            }
            else if (_reset.Contains(point))
            {
                e.Title = "Reset panel";
                e.Description = "Restore the original incoming data: disable all tree tools and unit conversion, restore automatic decimals, clear search, filters, sorting and selection, and scroll to the top. Undo restores the previous settings.";
            }
            else if (_search.Contains(point))
            {
                e.Title = "Search this view";
                e.Text = "Press Enter to apply";
                e.Description = "Search values, paths and types, or use numeric comparisons such as > 800 or <= 25.";
            }
            else if (_jump.Contains(point))
            {
                e.Title = "Jump to a row";
                e.Text = "Position in the current filtered and sorted view";
                e.Description = "Enter a row number starting at 1. The index beside each value remains its original index within its branch.";
            }
            else if (_resize.Contains(point))
            {
                e.Title = "Resize Panel+";
                e.Text = "Drag this corner";
                e.Description = "The panel size is saved with the Grasshopper definition.";
            }
            else if (ReferenceEquals(_lastResult, PanelOwner.Session.Result))
            {
                foreach (var hit in _rowHits)
                {
                    if (!hit.Bounds.Contains(point)) continue;
                    var row = PanelOwner.Session.Result.Snapshot.Rows[hit.SourcePosition];
                    e.Title = row.Path + " [" + row.OriginalIndex + "]";
                    e.Text = (row.IsValid ? "" : "Invalid ") + row.TypeName;
                    var snapshot = PanelOwner.Session.Result.Snapshot;
                    var value = PanelOwner.DisplayValue(snapshot, row, hit.SourcePosition);
                    var geometry = PanelOwner.Session.GetGeometryDetails(snapshot, hit.SourcePosition) ?? row.GeometryInfo;
                    string details;
                    if (!row.IsValid)
                        details = value + "\n" + (string.IsNullOrWhiteSpace(row.InvalidReason) ? "Geometry or value is invalid." : row.InvalidReason);
                    else if (geometry != null)
                    {
                        details = geometry.FormatDetails(snapshot.UnitConversion, PanelOwner.DecimalPlaces);
                        if (PanelOwner.Session.IsGeometryPending(snapshot, hit.SourcePosition)) details += "\nCalculating measurement…";
                        else if (PanelOwner.Session.HasGeometryMeasurement(snapshot, hit.SourcePosition) && !geometry.Value.HasValue)
                            details += "\nMeasurement unavailable.";
                    }
                    else details = value.Length > 1000 ? value.Substring(0, 997) + "…" : value;
                    e.Description = details + "\n\nDouble-click to copy the full value.";
                    break;
                }
            }
        }

        private static void Fill(Graphics g, Color color, RectangleF bounds) { using (var brush = new SolidBrush(color)) g.FillRectangle(brush, bounds); }
        private static void Text(Graphics g, string value, Font font, Color color, RectangleF bounds, StringFormat trim)
        {
            value = value ?? string.Empty;
            if (value.Length > 256) value = value.Substring(0, 253) + "…";
            value = value.Replace('\r', ' ').Replace('\n', ' ');
            using (var brush = new SolidBrush(color)) g.DrawString(value, font, brush, bounds, trim);
        }
        private static void Control(Graphics g, RectangleF bounds, string value, Font font, StringFormat trim, bool active)
        {
            if (active) Fill(g, Color.FromArgb(227, 237, 223), bounds);
            using (var pen = new Pen(active ? Accent : Color.FromArgb(224, 229, 222)))
                g.DrawLine(pen, bounds.X, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
            Text(g, value, font, active ? Accent : Muted, new RectangleF(bounds.X + 3, bounds.Y, bounds.Width - 6, bounds.Height), trim);
        }
        private static void DropdownControl(Graphics g, RectangleF bounds, string value, Font font, StringFormat trim, bool active)
        {
            Control(g, bounds, "", font, trim, active);
            Text(g, value, font, active ? Accent : Muted, new RectangleF(bounds.X + 4, bounds.Y, bounds.Width - 17, bounds.Height), trim);
            float x = bounds.Right - 10, y = bounds.Y + bounds.Height / 2 - 1;
            using (var pen = new Pen(active ? Accent : Muted, 1))
                g.DrawLines(pen, new[] { new PointF(x, y), new PointF(x + 3, y + 3), new PointF(x + 6, y) });
        }
        private void DrawDisplayAction(Graphics g)
        {
            float x = _display.X + 4, y = _display.Y + 5;
            bool expand = PanelOwner.CanvasDisplay == CanvasPanelDisplay.Half;
            using (var pen = new Pen(Muted, 1.2f))
            {
                g.DrawLines(pen, expand
                    ? new[] { new PointF(x, y), new PointF(x + 6, y + 6), new PointF(x + 12, y) }
                    : new[] { new PointF(x, y + 6), new PointF(x + 6, y), new PointF(x + 12, y + 6) });
            }
        }

        private static void DrawViewAction(Graphics g, RectangleF bounds, bool reset)
        {
            float x = bounds.X + 5, y = bounds.Y + 4;
            using (var pen = new Pen(Muted, 1.2f))
            {
                if (reset)
                {
                    g.DrawArc(pen, x, y, 10, 10, 25, 295);
                    g.DrawLine(pen, x + 9, y + 2, x + 9, y - 1);
                    g.DrawLine(pen, x + 9, y + 2, x + 6, y + 2);
                }
                else
                {
                    g.DrawLine(pen, x + 5, y, x + 5, y + 9);
                    g.DrawLine(pen, x + 5, y + 9, x + 2, y + 6);
                    g.DrawLine(pen, x + 5, y + 9, x + 8, y + 6);
                    g.DrawLine(pen, x, y + 11, x + 10, y + 11);
                }
            }
        }
        private string Number(double? value, PanelSnapshot snapshot) => value.HasValue
            ? PanelUnits.Format(value.Value, snapshot.UnitConversion, PanelOwner.DecimalPlaces, automaticFormat: "G5") : "—";
        private static string Short(string text, int maximum) => text.Length <= maximum ? text : text.Substring(0, maximum - 1) + "…";
        private readonly struct RowHit
        {
            internal RowHit(RectangleF bounds, int sourcePosition) { Bounds = bounds; SourcePosition = sourcePosition; }
            internal RectangleF Bounds { get; }
            internal int SourcePosition { get; }
        }

        private sealed class PanelPortAttributes : GH_LinkedParamAttributes
        {
            private PointF _grip;
            private readonly bool _input;
            internal PanelPortAttributes(IGH_Param owner, IGH_Attributes parent, bool input) : base(owner, parent) { _input = input; }
            internal void Place(PointF grip) { _grip = grip; Pivot = grip; Bounds = new RectangleF(grip.X - 6, grip.Y - 9, 12, 18); }
            protected override void Layout() => Bounds = new RectangleF(_grip.X - 6, _grip.Y - 9, 12, 18);
            public override PointF InputGrip => _input ? _grip : base.InputGrip;
            public override PointF OutputGrip => !_input ? _grip : base.OutputGrip;
        }
    }
}
#pragma warning restore CA1416
