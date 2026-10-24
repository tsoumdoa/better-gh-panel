using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Canvas.Interaction;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

// Grasshopper's canvas drawing contract is supplied by Rhino on Windows and Mac.
#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes : GH_ComponentAttributes
    {
        private const float RowHeight = 17, BranchHeight = 15, HeaderHeight = CanvasPanelSize.HeaderHeight;
        private const float TypeColumnWidth = 44, CellGap = 10;
        private static readonly Color Ink = Color.FromArgb(49, 55, 53);
        private static readonly Color Muted = Color.FromArgb(120, 127, 123);
        private static readonly Color Accent = Color.FromArgb(77, 115, 94);
        private RectangleF _panel, _search, _filter, _type, _sort, _branch, _jump, _reset, _resize, _list, _track, _footer;
        private RectangleF _trim, _simplify, _flatten, _reverse, _createSet, _units;
        private RectangleF _display, _preview, _move, _statistics, _selectAll, _close, _restore;
        private readonly List<RowHit> _rowHits = new List<RowHit>();
        private readonly List<BranchHit> _branchHits = new List<BranchHit>();
        private PanelSearchMode _editingSearchMode;
        private int _offset, _shown;
        private PanelQueryResult? _lastResult;
        private readonly PanelRowSelection _selection;
        private GH_Canvas? _selectionCanvas;
        private readonly CanvasMenu _menu = new CanvasMenu();
        private bool _resizing, _scrolling;
        private PointF _dragStart;
        private float _startWidth, _startHeight, _thumbGrab;
        private BetterPanelComponent PanelOwner => (BetterPanelComponent)Owner;
        private int Count => PanelOwner.Session.HasCurrentResult ? PanelOwner.Session.Result.RowPositions.Count : 0;
        private int MaximumOffset => Math.Max(0, Count - Math.Max(1, _shown));

        internal BetterPanelAttributes(BetterPanelComponent owner) : base(owner) { _selection = owner.RowSelection; }

        internal void ResetViewState()
        {
            _setDetails.Close();
            _setHits.Clear();
            _setCountSnapshot = null;
            _setControlsVisible = false;
            LayoutSetDetails();
            _offset = 0;
            _lastResult = null;
            ClearRowSelection();
            PanelOwner.ClearHighlight();
            _scrolling = false;
            _rowContextHandled = false;
            _menu.Close();
            _rowActionDismissedUntil = default;
            _rowHits.Clear();
            _branchHits.Clear();
        }

        internal void ClearRowSelection()
        {
            _selection.Clear();
            DetachSelectionCanvas();
            InvalidateCopyStatus();
            CloseRowMenu();
        }

        private void ClearRowSelection(GH_Canvas canvas)
        {
            ClearRowSelection();
            OnQueryCompleted();
            canvas.Invalidate();
        }

        private void TrackSelectionCanvas(GH_Canvas canvas)
        {
            if (_selection.Count == 0) { DetachSelectionCanvas(); return; }
            if (ReferenceEquals(_selectionCanvas, canvas)) return;
            DetachSelectionCanvas();
            _selectionCanvas = canvas;
            canvas.MouseDown += SelectionCanvasMouseDown;
            canvas.DocumentChanged += SelectionCanvasDocumentChanged;
            canvas.Disposed += SelectionCanvasDisposed;
        }

        private void DetachSelectionCanvas()
        {
            if (_selectionCanvas == null) return;
            _selectionCanvas.MouseDown -= SelectionCanvasMouseDown;
            _selectionCanvas.DocumentChanged -= SelectionCanvasDocumentChanged;
            _selectionCanvas.Disposed -= SelectionCanvasDisposed;
            _selectionCanvas = null;
        }

        private void SelectionCanvasMouseDown(object? sender, MouseEventArgs e)
        {
            if (sender is not GH_Canvas canvas || e.Button != MouseButtons.Left ||
                !ReferenceEquals(canvas.Document, Owner.OnPingDocument()) || canvas.Document == null) return;
            if (e.Clicks > 1 && DateTime.UtcNow < _rowActionDismissedUntil) return;
            // The host handles this event first. Only its ordinary blank-canvas
            // selection gesture clears rows; wire, widget, pan and drag gestures do not.
            if (canvas.ActiveInteraction is not GH_WindowSelectInteraction) return;
            var modifiers = RowModifiers();
            if (modifiers.Toggle || modifiers.Range || modifiers.AcrossBranches) return;
            var point = canvas.Viewport.UnprojectPoint(e.Location);
            if (canvas.Document.RelevantObjectAtPoint(point) != null) return;
            ClearRowSelection(canvas);
        }

        private void SelectionCanvasDocumentChanged(GH_Canvas canvas, GH_CanvasDocumentChangedEventArgs e)
        {
            if (ReferenceEquals(e.NewDocument, Owner.OnPingDocument())) return;
            ClearRowSelection();
            PanelOwner.ClearHighlight();
        }

        private void SelectionCanvasDisposed(object? sender, EventArgs e)
        {
            ClearRowSelection();
            PanelOwner.ClearHighlight();
        }

        internal void ClosePresentationControls()
        {
            _setHits.Clear();
            _menu.Close();
            CloseRowMenu();
            _resizing = _scrolling = _rowContextHandled = false;
            _rowHits.Clear();
            _branchHits.Clear();
        }

        internal void OnQueryCompleted()
        {
            LayoutSetDetails();
            var session = PanelOwner.Session;
            if (!StatisticsAreCurrent()) InvalidateStatistics();
            if (session.IsBusy) { PanelOwner.ClearHighlight(); return; }
            var result = session.Result;
            if (!session.HasCurrentResult)
            {
                ClearRowSelection();
                PanelOwner.ClearHighlight();
                return;
            }
            PanelOwner.HighlightRows(result.Snapshot, _selection.OrderedPositions(result));
            if (_selection.Count == 0) DetachSelectionCanvas();
        }

        internal void InvalidateStatistics()
        {
            if (_menu.Kind == CanvasMenuKind.Statistics) _menu.Close();
        }

        private bool StatisticsAreCurrent() => PanelOwner.Session.HasCurrentResult &&
            !PanelOwner.Locked && _menu.IsCurrent(PanelOwner.Session.Result);

        private void SetBranchFilter(string? path)
        {
            var session = PanelOwner.Session;
            var options = session.Options;
            options.BranchPath = string.IsNullOrWhiteSpace(path) ? null : path!.Trim();
            options.BranchPrefix = false;
            ClearRowSelection();
            var previousResult = session.Result;
            session.UpdateQuery(options);
            if (ReferenceEquals(previousResult, session.Result)) OnQueryCompleted();
        }

        protected override void Layout()
        {
            CloseRowMenu();
            if (PanelOwner.PanelMinimized)
            {
                _panel = new RectangleF(Pivot.X, Pivot.Y, CanvasPanelSize.CapsuleWidth, CanvasPanelSize.CapsuleHeight);
                m_innerBounds = _panel;
                ClosePresentationControls();
                _search = _filter = _type = _sort = _branch = _jump = _reset = _resize = _list = _track = _footer = RectangleF.Empty;
                _trim = _simplify = _flatten = _reverse = _createSet = _units = _display = _preview = _close = _move = _statistics = _selectAll = RectangleF.Empty;
                _restore = new RectangleF(_panel.Right - 36, _panel.Y + (_panel.Height - 22) / 2, 22, 22);
                PositionPorts(Owner.Params.Input, true);
                PositionPorts(Owner.Params.Output, false);
                Bounds = new RectangleF(_panel.X - 22, _panel.Y, _panel.Width + 44, _panel.Height);
                LayoutSetDetails();
                return;
            }
            _restore = RectangleF.Empty;
            _panel = new RectangleF(Pivot.X,
                Pivot.Y + CanvasPanelSize.DisplayTopOffset(PanelOwner.CanvasHeight, PanelOwner.CanvasDisplay),
                PanelOwner.CanvasWidth, PanelOwner.DisplayHeight);
            _rowHits.Clear();
            _branchHits.Clear();
            m_innerBounds = _panel;
            float x = _panel.X + 4;
            _search = new RectangleF(x + 2, _panel.Y + 25, _panel.Width - 212, 21);
            _filter = new RectangleF(_search.Right - 18, _search.Y, 18, _search.Height);
            _type = new RectangleF(_search.Right + 3, _search.Y, 76, 21);
            _sort = new RectangleF(_type.Right + 3, _search.Y, 43, 21);
            _branch = new RectangleF(_sort.Right + 3, _search.Y, 64, 21);
            _close = new RectangleF(_panel.Right - 34, _panel.Y + 2, 20, 18);
            _display = new RectangleF(_close.Left - 22, _close.Y, 20, 18);
            _jump = new RectangleF(_display.Left - 48, _panel.Y + 2, 20, 18);
            _reset = new RectangleF(_jump.Right + 4, _jump.Y, 20, 18);
            _preview = new RectangleF(_jump.Left - 108, _jump.Y, 104, 18);
            _move = new RectangleF(_panel.X + 14, _panel.Y + 1, _preview.Left - _panel.X - 18, 23);
            // Reserve the corner for resizing rather than overlaying footer controls.
            _footer = new RectangleF(x + 2, _panel.Bottom - 38, _panel.Width - 34, 36);
            _statistics = new RectangleF(_footer.Right - 46, _footer.Y, 46, 16);
            _selectAll = new RectangleF(_statistics.Left - 62, _footer.Y, 58, 16);
            // Share one row so status text, tool glyphs and Units have the same center.
            _units = new RectangleF(_footer.Right - 76, _footer.Y + 16, 76, 20);
            _trim = new RectangleF(_units.Left - 125, _units.Y, 22, _units.Height);
            _simplify = new RectangleF(_trim.Right + 2, _trim.Y, 22, 20);
            _flatten = new RectangleF(_simplify.Right + 2, _trim.Y, 22, 20);
            _reverse = new RectangleF(_flatten.Right + 2, _trim.Y, 22, 20);
            _createSet = new RectangleF(_reverse.Right + 2, _trim.Y, 22, 20);
            // Keep interactive content beyond native grip picking's 12-unit
            // radius on both edges, including rows and the scroll track.
            _list = new RectangleF(_panel.X + 14, _panel.Y + 52, _panel.Width - 33, Math.Max(RowHeight, _panel.Height - 92));
            _track = new RectangleF(_panel.Right - 18, _list.Y, 4, _list.Height);
            _resize = new RectangleF(_panel.Right - 24, _panel.Bottom - 24, 24, 24);
            // Linked parameter attributes keep Grasshopper's wire/tooltip behavior, but
            // expose actual wire grips at our rectangle instead of a hidden default capsule.
            PositionPorts(Owner.Params.Input, true);
            PositionPorts(Owner.Params.Output, false);
            LayoutSetDetails();
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
                // Canonical Full/Half grips all fit inside the Half rectangle.
                // A capsule has enough height for four distinct output targets.
                float top = PanelOwner.PanelMinimized ? _panel.Top + 12 : Pivot.Y + PanelOwner.CanvasHeight / 4 + 12;
                float span = PanelOwner.PanelMinimized ? _panel.Height - 24 : PanelOwner.CanvasHeight / 2 - 48;
                // Native wire picking searches within 12 canvas units in reverse
                // parameter order. Keep neighbours beyond that radius even at
                // the vertical edge of a grip's target; reserve the resize corner.
                float offset = parameters.Count == 1 ? span / 2 : span * i / (parameters.Count - 1);
                var point = new PointF(input ? _panel.Left : _panel.Right, top + offset);
                port.Place(point);
            }
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            if (channel == GH_CanvasChannel.Wires) { base.Render(canvas, graphics, channel); return; }
            if (channel != GH_CanvasChannel.Objects) return;
            _setControlsVisible = canvas.Viewport.Zoom >= 0.5f;
            _setHits.Clear();
            LayoutSetDetails();
            CanvasTextInput.Maintain(canvas);
            var session = PanelOwner.Session;
            var visibleRegion = canvas.Viewport.VisibleRegion;
            session.AnimateBusyState(!PanelOwner.Locked && Bounds.IntersectsWith(visibleRegion) && canvas.Viewport.Zoom >= 0.5f);
            // Grasshopper also renders components outside the canvas viewport.
            if (!Bounds.IntersectsWith(visibleRegion))
            {
                _rowHits.Clear(); _branchHits.Clear(); _shown = 0; _menu.Close(); CloseRowMenu();
                session.RequestGeometryMeasurements(session.Snapshot, Array.Empty<int>());
                return;
            }
            var result = session.Result;
            if (!ReferenceEquals(result, _lastResult))
            {
                _selection.Synchronize(result);
                InvalidateCopyStatus();
                _lastResult = result;
                _offset = 0;
                _menu.Close();
                CloseRowMenu();
            }
            if (!ReferenceEquals(result.Snapshot, session.Snapshot) && (_selection.Count > 0 || _copyPending))
            {
                _selection.Clear();
                InvalidateCopyStatus();
            }
            if (session.IsBusy || !ReferenceEquals(result.Snapshot, session.Snapshot)) CloseRowMenu();
            if (!_menu.IsCurrent(session.Snapshot)) _menu.Close();
            TrackSelectionCanvas(canvas);
            if (!StatisticsAreCurrent()) InvalidateStatistics();
            // Layout is measured in canvas units. Pixel fonts follow the canvas
            // transform without adding the display DPI a second time.
            using (var font = new Font("Arial", 11f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var small = new Font("Arial", 10f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var bold = new Font("Arial", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var mono = CanvasRowRenderer.MonospaceFont(10))
            using (var trim = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
            {
                if (PanelOwner.PanelMinimized)
                {
                    RenderCapsule(canvas, graphics, bold, small, trim);
                    session.RequestGeometryMeasurements(session.Snapshot, Array.Empty<int>());
                    return;
                }
                Fill(graphics, Color.White, _panel);
                Fill(graphics, Accent, new RectangleF(_panel.X, _panel.Y, 3, HeaderHeight));
                using (var grip = new SolidBrush(Muted))
                    for (int column = 0; column < 2; column++)
                        for (int row = 0; row < 3; row++)
                            graphics.FillEllipse(grip, _move.X + 5 + column * 4, _move.Y + 5 + row * 4, 1.5f, 1.5f);
                Text(graphics, "P+", bold, Accent, new RectangleF(_move.X + 18, _move.Y, _move.Width - 20, 20), trim);
                if (canvas.Viewport.Zoom >= 0.5f)
                {
                    DrawDisplayAction(graphics);
                    DrawCloseAction(graphics);
                }
                if (canvas.Viewport.Zoom >= 0.5f)
                {
                    var options = session.Options;
                    DropdownControl(graphics, _preview, "Preview: " + (PanelOwner.PreviewMode == PanelPreviewMode.FilteredResults ? "Results" : CanvasMenu.PreviewLabel(PanelOwner.PreviewMode)), small, trim,
                        PanelOwner.PreviewMode != PanelPreviewMode.Off);
                    DropdownControl(graphics, _search, CanvasMenu.SearchHint(options), small, trim,
                        options.SearchMode != PanelSearchMode.Text || !string.IsNullOrEmpty(options.Search));
                    DropdownControl(graphics, _type, session.HasCurrentResult ? CanvasMenu.TypeHint(result, options) : "Types", small, trim, options.TypeName != null || options.OnlyInvalid);
                    DropdownControl(graphics, _sort, options.SortMode == PanelSortMode.SourceOrder ? "Src" : options.SortMode == PanelSortMode.ValueAscending ? "↑" : "↓", small, trim, options.SortMode != PanelSortMode.SourceOrder);
                    DropdownControl(graphics, _branch, Short(options.BranchPath ?? "Paths", 80), mono, trim, options.BranchPath != null);
                    DrawViewAction(graphics, _jump, false);
                    DrawViewAction(graphics, _reset, true);
                    RenderRows(graphics, result, visibleRegion, font, small, mono, trim);
                    RenderFooter(graphics, result, small, trim);
                    RenderSetDetails(graphics, result, small, mono, trim);
                    if (_menu.Kind != CanvasMenuKind.None) RenderMenu(graphics, small, mono, trim);
                    if (_rowMenuResult != null) RenderRowMenu(graphics, small, trim);
                    using (var gripPen = new Pen(Color.FromArgb(165, 173, 166), 1))
                    {
                        for (int inset = 8; inset <= 18; inset += 5)
                            graphics.DrawLine(gripPen, _panel.Right - inset, _panel.Bottom - 4, _panel.Right - 4, _panel.Bottom - inset);
                    }
                }
                else
                {
                    _rowHits.Clear(); _branchHits.Clear(); _shown = 0; _menu.Close(); CloseRowMenu();
                    session.RequestGeometryMeasurements(session.Snapshot, Array.Empty<int>());
                }
                using (var border = new Pen(Selected ? Accent : Color.FromArgb(164, 171, 164), Selected ? 1.5f : 1))
                    graphics.DrawRectangle(border, _panel.X, _panel.Y, _panel.Width, _panel.Height);
                RenderPorts(graphics, canvas, small, trim);
            }
        }

        private string CapsuleSummary()
        {
            var session = PanelOwner.Session;
            if (PanelOwner.Locked) return "Disabled";
            if (!session.HasCurrentResult) return session.IsBusy ? "Recomputing…" : "Filter needs attention";
            return CanvasStatistics.Summary(session.Result);
        }

        private void RenderCapsule(GH_Canvas canvas, Graphics g, Font bold, Font small, StringFormat trim)
        {
            using (var shape = new GraphicsPath())
            using (var fill = new SolidBrush(Color.White))
            using (var border = new Pen(Selected ? Accent : Color.FromArgb(164, 171, 164), Selected ? 1.5f : 1))
            {
                const float corner = 12;
                shape.AddArc(_panel.X, _panel.Y, corner, corner, 180, 90);
                shape.AddArc(_panel.Right - corner, _panel.Y, corner, corner, 270, 90);
                shape.AddArc(_panel.Right - corner, _panel.Bottom - corner, corner, corner, 0, 90);
                shape.AddArc(_panel.X, _panel.Bottom - corner, corner, corner, 90, 90);
                shape.CloseFigure();
                g.FillPath(fill, shape);
                g.DrawPath(border, shape);
            }
            float textTop = _panel.Y + (_panel.Height - 22) / 2;
            Text(g, "P+", bold, Accent, new RectangleF(_panel.X + 8, textTop, 27, 22), trim);
            Text(g, CapsuleSummary(), small, Muted,
                new RectangleF(_panel.X + 36, textTop, _restore.Left - _panel.X - 40, 22), trim);
            using (var pen = new Pen(Accent, 1.2f))
            {
                float x = _restore.X + 5, y = _restore.Y + 5;
                g.DrawRectangle(pen, x, y + 3, 8, 8);
                g.DrawLine(pen, x + 5, y + 6, x + 11, y);
                g.DrawLines(pen, new[] { new PointF(x + 6, y), new PointF(x + 11, y), new PointF(x + 11, y + 5) });
            }
            RenderPorts(g, canvas, small, trim);
        }

        private void RenderPorts(Graphics g, GH_Canvas canvas, Font font, StringFormat trim)
        {
            foreach (var parameter in Owner.Params.Input)
            {
                var grip = parameter.Attributes.InputGrip;
                GH_CapsuleRenderEngine.RenderInputGrip(g, canvas.Viewport.Zoom, grip, false);
                if (canvas.Viewport.Zoom >= 0.5f)
                    Text(g, parameter.NickName, font, Muted, new RectangleF(grip.X - 20, grip.Y - 8, 12, 16), trim);
            }
            foreach (var parameter in Owner.Params.Output)
            {
                var grip = parameter.Attributes.OutputGrip;
                GH_CapsuleRenderEngine.RenderOutputGrip(g, canvas.Viewport.Zoom, grip, false);
                if (canvas.Viewport.Zoom >= 0.5f)
                    Text(g, parameter.NickName, font, Muted, new RectangleF(grip.X + 8, grip.Y - 8, 12, 16), trim);
            }
        }

        private void RenderRows(Graphics g, PanelQueryResult result, RectangleF visibleRegion, Font font, Font small, Font mono, StringFormat trim)
        {
            _setHits.Clear();
            _rowHits.Clear();
            _branchHits.Clear();
            _shown = 0;
            if (PanelOwner.Locked)
            {
                Text(g, "Disabled. Enable the component to recompute.", small, Muted,
                    new RectangleF(_list.X + 6, _list.Y + 12, _list.Width - 12, 22), trim);
                PanelOwner.Session.RequestGeometryMeasurements(PanelOwner.Session.Snapshot, Array.Empty<int>());
                return;
            }
            if (PanelOwner.Session.IsBusy)
            {
                // Clear both rows and their hit regions while the current view is
                // being computed; never mix old statistics/values with new input.
                float busyY = _list.Y + Math.Max(0, (_list.Height - 22) / 2);
                var ring = new RectangleF(_list.X + 9, busyY + 5, 12, 12);
                using (var track = new Pen(Color.FromArgb(225, 231, 224), 2))
                    g.DrawEllipse(track, ring);
                using (var progress = new Pen(Accent, 2))
                    g.DrawArc(progress, ring, (float)(DateTime.UtcNow.TimeOfDay.TotalMilliseconds % 900 / 900 * 360), 250);
                Text(g, "Recomputing…", small, Muted,
                    new RectangleF(_list.X + 29, busyY, _list.Width - 35, 22), trim);
                PanelOwner.Session.RequestGeometryMeasurements(PanelOwner.Session.Snapshot, Array.Empty<int>());
                return;
            }
            if (PanelOwner.Session.Error != null)
            {
                Text(g, "Filter needs attention. Edit filter or Reset.", small, Muted,
                    new RectangleF(_list.X + 6, _list.Y, _list.Width - 12, 22), trim);
                PanelOwner.Session.RequestGeometryMeasurements(result.Snapshot, Array.Empty<int>());
                return;
            }
            if (!PanelOwner.Session.HasCurrentResult) return;
            float y = _list.Y;
            string? previousPath = null;
            // A half-height panel at the minimum saved size still has room for a
            // value, even when a separate branch heading would consume that row.
            bool sorted = PanelOwner.Session.Options.SortMode != PanelSortMode.SourceOrder;
            bool grouped = _list.Height >= BranchHeight + RowHeight;
            bool showTypes = result.Statistics.TypeCounts.Count > 1 || PanelOwner.Session.Options.OnlyInvalid;
            using var rowRenderer = new CanvasRowRenderer(g, result.Snapshot.Rows.Count, sorted);
            if (PanelOwner.CreateSetTree) SizeSetColumn(g, rowRenderer, result.Snapshot);
            float setWidth = PanelOwner.CreateSetTree ? _setCountWidth : 0;
            // Grouped rows already show their path in the branch heading.
            float pathWidth = grouped ? 0 : Math.Min(80, Math.Max(0, _list.Width - rowRenderer.GutterWidth -
                (showTypes ? TypeColumnWidth + CellGap : 12) - 40 - setWidth));
            float valueStart = rowRenderer.GutterWidth + pathWidth;
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
                        var branchBounds = new RectangleF(_list.X, y, _list.Width, BranchHeight);
                        Fill(g, Color.FromArgb(236, 241, 235), branchBounds);
                        var count = CanvasStatistics.BranchCount(result, row.Path);
                        float countWidth = Math.Min(_list.Width / 2, g.MeasureString(count, small).Width + 6);
                        Text(g, row.Path, mono, Accent, new RectangleF(_list.X + 6, y, _list.Width - countWidth - 18, BranchHeight), trim);
                        Text(g, count, small, Muted, new RectangleF(_list.Right - countWidth - 6, y, countWidth, BranchHeight), trim);
                        _branchHits.Add(new BranchHit(branchBounds, row.Path));
                        y += BranchHeight;
                        previousPath = row.Path;
                    }
                    if (y + RowHeight > _list.Bottom) break;
                    var bounds = new RectangleF(_list.X, y, _list.Width, RowHeight);
                    bool selected = _selection.Contains(sourcePosition);
                    Fill(g, selected ? Color.FromArgb(216, 232, 219) : (_shown & 1) == 0 ? Color.White : Color.FromArgb(247, 249, 246), bounds);
                    rowRenderer.DrawAddress(g, bounds, i + 1, row.OriginalIndex, sorted, selected);
                    if (!row.IsValid) Fill(g, Color.FromArgb(180, 106, 67), new RectangleF(bounds.X, bounds.Y + 3, 2, bounds.Height - 6));
                    if (!grouped)
                        rowRenderer.DrawCell(g, row.Path, mono, Accent,
                            new RectangleF(bounds.X + rowRenderer.GutterWidth + 9, y, Math.Max(0, pathWidth - 12), RowHeight));
                    // Geometry summaries already name their type; reserve the extra
                    // label only for values whose text does not identify their type.
                    bool showRowType = showTypes && row.GeometryInfo == null;
                    float valueRight = bounds.Right - setWidth - (showRowType ? TypeColumnWidth + CellGap : 12);
                    rowRenderer.DrawValue(g, PanelOwner.DisplayValue(result.Snapshot, row, sourcePosition), font, Ink,
                        new RectangleF(bounds.X + valueStart + 3, y, Math.Max(0, valueRight - bounds.X - valueStart - 3), RowHeight), row.NumericValue.HasValue);
                    if (showRowType)
                        rowRenderer.DrawCell(g, CanvasMenu.ShortTypeName(row.TypeName), small, Muted,
                            new RectangleF(bounds.Right - setWidth - TypeColumnWidth, y, TypeColumnWidth - CellGap, RowHeight), true);
                    DrawSetCount(g, rowRenderer, row, bounds, sourcePosition, i + 1, small);
                    _rowHits.Add(new RowHit(bounds, sourcePosition, i + 1));
                    _shown++;
                    y += RowHeight;
                }
                if (result.RowPositions.Count == 0)
                {
                    Text(g, result.BranchPaths.Count > 0 ? "Matching branches are empty." :
                        result.Snapshot.Branches.Count == 0 ? "Connect a tree to start exploring." : "No matching items. Clear a filter or Reset.",
                        small, Muted, new RectangleF(_list.X + 6, _list.Y + 12, _list.Width - 12, 22), trim);
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

        private void RenderFooter(Graphics g, PanelQueryResult result, Font small, StringFormat trim)
        {
            var session = PanelOwner.Session;
            bool current = !PanelOwner.Locked && session.HasCurrentResult;
            if (current)
                Text(g, CanvasStatistics.Summary(result), small, Ink,
                    new RectangleF(_footer.X, _footer.Y, _selectAll.Left - _footer.X - 5, _statistics.Height), trim);
            Control(g, _selectAll, "Select all", small, trim, current && result.RowPositions.Count > 0 && _selection.Count == result.RowPositions.Count);
            Control(g, _statistics, "Stats", small, trim, current && _menu.Kind == CanvasMenuKind.Statistics);
            string output = result.RowPositions.Count != result.Snapshot.Rows.Count || result.Statistics.BranchCount != result.Snapshot.Branches.Count
                ? "out: filtered" : PanelOwner.HasTreeOperations || PanelOwner.UnitConversion != PanelUnitConversion.None ? "out: tools" : "out: source";
            string status = PanelOwner.Locked ? "Disabled" : session.Error != null ? "Query failed: " + Short(session.Error, 150) : session.IsBusy ? "Recomputing…" :
                _copyPending || DateTime.UtcNow < _copiedUntil ? _actionStatus :
                _selection.Count > 0 ? $"{_selection.Count:N0} selected · right-click to copy" :
                $"{(Count == 0 ? 0 : _offset + 1):N0}–{Math.Min(Count, _offset + _shown):N0}/{Count:N0} · {output}";
            Text(g, status, small, Muted, new RectangleF(_footer.X, _units.Y, _trim.Left - _footer.X - 6, _units.Height), trim);
            TreeToolIcons.Draw(g, _trim, TreeToolIcon.Trim, PanelOwner.TrimDepth > 0);
            TreeToolIcons.Draw(g, _simplify, TreeToolIcon.Simplify, PanelOwner.SimplifyTree);
            TreeToolIcons.Draw(g, _flatten, TreeToolIcon.Flatten, PanelOwner.FlattenTree);
            TreeToolIcons.Draw(g, _reverse, TreeToolIcon.Reverse, PanelOwner.ReverseTree);
            TreeToolIcons.Draw(g, _createSet, TreeToolIcon.CreateSet, PanelOwner.CreateSetTree);
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
            _rowHits.Clear();
            _branchHits.Clear();
            CloseRowMenu();
            canvas.Invalidate();
        }

        private void LayoutMenu() => _menu.Layout(_panel, _type, _sort, _branch, _units, _search, _preview, _statistics);

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
                bool selected = _menu.IsSelected(index, options, PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.PreviewMode, PanelOwner.ShowItemOrder);
                if (index == _menu.HoveredIndex || selected)
                    Fill(g, index == _menu.HoveredIndex ? Color.FromArgb(226, 236, 226) : Color.FromArgb(242, 247, 241), row);
                if (_menu.Kind == CanvasMenuKind.Units && index == PanelUnits.ConversionCount ||
                    _menu.Kind == CanvasMenuKind.Statistics && index == CanvasStatistics.OverviewRows ||
                    _menu.Kind == CanvasMenuKind.Preview && index == CanvasMenu.ItemOrderIndex)
                    using (var divider = new Pen(Color.FromArgb(225, 231, 224)))
                        g.DrawLine(divider, row.X + 7, row.Y, row.Right - 7, row.Y);
                if (selected)
                    using (var check = new Pen(Accent, 1.3f))
                        g.DrawLines(check, new[] { new PointF(row.X + 7, row.Y + 10), new PointF(row.X + 10, row.Y + 13), new PointF(row.X + 16, row.Y + 7) });
                float inset = _menu.Kind == CanvasMenuKind.Statistics ? 7 : 23;
                Text(g, _menu.Label(index, PanelOwner.DecimalPlaces), _menu.Kind == CanvasMenuKind.Branch ? mono : small,
                    selected ? Accent : Ink, new RectangleF(row.X + inset, row.Y, row.Width - inset - 7, 20), trim);
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
            CloseRowMenu();
            var session = PanelOwner.Session;
            if (kind == CanvasMenuKind.Statistics && (!session.HasCurrentResult || PanelOwner.Locked)) return;
            _menu.Toggle(kind, session.Snapshot, session.Options, session.Result);
            LayoutMenu();
            canvas.Invalidate();
        }

        private bool RowsAreCurrent(PanelQueryResult result) =>
            ReferenceEquals(_lastResult, result) && ReferenceEquals(result, PanelOwner.Session.Result) &&
            !PanelOwner.Locked && PanelOwner.Session.HasCurrentResult;

        private static (bool Toggle, bool Range, bool AcrossBranches) RowModifiers()
        {
            // Eto reports Command on Mac; WinForms supplies the fallback used by
            // Grasshopper hosts without an initialized Eto application.
            if (Eto.Forms.Application.Instance != null)
            {
                var keys = Eto.Forms.Keyboard.Modifiers;
                return ((keys & (Eto.Forms.Keys.Control | Eto.Forms.Keys.Application)) != 0,
                    (keys & Eto.Forms.Keys.Shift) != 0, (keys & Eto.Forms.Keys.Alt) != 0);
            }
            var modifiers = System.Windows.Forms.Control.ModifierKeys;
            return ((modifiers & Keys.Control) != 0, (modifiers & Keys.Shift) != 0, (modifiers & Keys.Alt) != 0);
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (PanelOwner.PanelMinimized)
            {
                if (e.Button == MouseButtons.Left && _restore.Contains(e.CanvasLocation))
                {
                    if (e.Clicks > 1 && DateTime.UtcNow < _rowActionDismissedUntil) return GH_ObjectResponse.Handled;
                    SuppressTrailingDoubleClick();
                    PanelOwner.SetPanelMinimized(false);
                    PerformLayout();
                    CanvasDoubleClickGuard.Start(sender, e);
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
                return base.RespondToMouseDown(sender, e);
            }
            if (sender.Viewport.Zoom < 0.5f) return base.RespondToMouseDown(sender, e);
            LayoutSetDetails();
            if (e.Button == MouseButtons.Right && _setDetails.Bounds.Contains(e.CanvasLocation))
            {
                _rowContextHandled = true;
                return GH_ObjectResponse.Handled;
            }
            if (e.Button == MouseButtons.Right) return RespondToRowContextMenu(sender, e);
            if (e.Button != MouseButtons.Left) return base.RespondToMouseDown(sender, e);
            var point = e.CanvasLocation;
            // A fresh gesture may start elsewhere before the prior menu's
            // double-click interval expires. Only its trailing click is blocked.
            if (e.Clicks == 1) _rowActionDismissedUntil = default;
            if (e.Clicks > 1 && DateTime.UtcNow < _rowActionDismissedUntil) return GH_ObjectResponse.Handled;
            if (_close.Contains(point))
            {
                SuppressTrailingDoubleClick();
                PanelOwner.SetPanelMinimized(true);
                PerformLayout();
                CanvasDoubleClickGuard.Start(sender, e);
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            if (_display.Contains(point))
            {
                // Expanding Half moves this header away from the pointer and can
                // expose a row beneath the second click of the same gesture.
                SuppressTrailingDoubleClick();
                _menu.Close();
                CloseRowMenu();
                PanelOwner.SetCanvasDisplay(CanvasPanelSize.NextDisplay(PanelOwner.CanvasDisplay));
                PerformLayout();
                CanvasDoubleClickGuard.Start(sender, e);
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            if (_reset.Contains(point))
            {
                PanelOwner.ResetPanel();
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            if (_rowMenuResult != null)
            {
                var menuResult = _rowMenuResult;
                int index = _rowMenuBounds.Contains(point) ? (int)((point.Y - _rowMenuBounds.Y) / 20) : -1;
                // Capture identity before closing releases the menu choices.
                PanelRowAction? action = index >= 0 && index < _rowMenuActions.Length ? _rowMenuActions[index].Action : (PanelRowAction?)null;
                CloseRowMenu();
                SuppressTrailingDoubleClick();
                if (RowsAreCurrent(menuResult) && action.HasValue) ChooseRowAction(action.Value, menuResult, sender);
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            if (_menu.Kind != CanvasMenuKind.None)
            {
                if (!_menu.IsCurrent(PanelOwner.Session.Snapshot) || _menu.Kind == CanvasMenuKind.Statistics && !StatisticsAreCurrent())
                {
                    _menu.Close();
                    SuppressTrailingDoubleClick();
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
                if (_menu.TrySelect(point, out var index)) ChooseMenu(index, sender);
                // A no-op choice (such as the already active sort order) does
                // not replace the result, so its exposed rows remain hittable.
                if (_menu.Kind == CanvasMenuKind.None) SuppressTrailingDoubleClick();
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            if (HandleSetDetailsClick(sender, e)) return GH_ObjectResponse.Handled;
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
                        PanelOwner.SetTreeOperations(depth, PanelOwner.FlattenTree, PanelOwner.ReverseTree, PanelOwner.SimplifyTree, PanelOwner.CreateSetTree);
                });
                return GH_ObjectResponse.Handled;
            }
            if (_simplify.Contains(point) || _flatten.Contains(point) || _reverse.Contains(point) || _createSet.Contains(point))
            {
                PanelOwner.SetTreeOperations(PanelOwner.TrimDepth,
                    _flatten.Contains(point) ? !PanelOwner.FlattenTree : PanelOwner.FlattenTree,
                    _reverse.Contains(point) ? !PanelOwner.ReverseTree : PanelOwner.ReverseTree,
                    _simplify.Contains(point) ? !PanelOwner.SimplifyTree : PanelOwner.SimplifyTree,
                    _createSet.Contains(point) ? !PanelOwner.CreateSetTree : PanelOwner.CreateSetTree);
                return GH_ObjectResponse.Handled;
            }
            if (_selectAll.Contains(point))
            {
                var result = PanelOwner.Session.Result;
                if (RowsAreCurrent(result))
                {
                    InvalidateCopyStatus();
                    _selection.SelectAll(result);
                    PanelOwner.HighlightRows(result.Snapshot, _selection.OrderedPositions(result));
                    TrackSelectionCanvas(sender);
                    sender.Invalidate();
                }
                return GH_ObjectResponse.Handled;
            }
            if (_preview.Contains(point)) { OpenMenu(CanvasMenuKind.Preview, sender); return GH_ObjectResponse.Handled; }
            if (_statistics.Contains(point)) { OpenMenu(CanvasMenuKind.Statistics, sender); return GH_ObjectResponse.Handled; }
            if (_filter.Contains(point)) { OpenMenu(CanvasMenuKind.SearchMode, sender); return GH_ObjectResponse.Handled; }
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
                    _rowHits.Clear();
                    _branchHits.Clear();
                    sender.Invalidate();
                });
                return GH_ObjectResponse.Handled;
            }
            // Moving the panel is a presentation control, so keep its row selection.
            if (_move.Contains(point)) return base.RespondToMouseDown(sender, e);
            var currentResult = PanelOwner.Session.Result;
            if (_list.Contains(point) && !RowsAreCurrent(currentResult))
            {
                _rowHits.Clear();
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            foreach (var hit in _branchHits)
                if (hit.Bounds.Contains(point))
                {
                    InvalidateCopyStatus();
                    _selection.SelectAll(currentResult, hit.Path);
                    PanelOwner.HighlightRows(currentResult.Snapshot, _selection.OrderedPositions(currentResult));
                    TrackSelectionCanvas(sender);
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
            foreach (var hit in _rowHits)
                if (hit.Bounds.Contains(point) && hit.SourcePosition >= 0 && hit.SourcePosition < currentResult.Snapshot.Rows.Count)
                {
                    var modifiers = RowModifiers();
                    if (modifiers.AcrossBranches)
                    {
                        SelectAcrossBranches(currentResult, new[] { currentResult.Snapshot.Rows[hit.SourcePosition].OriginalIndex }, sender);
                        sender.Invalidate();
                        return GH_ObjectResponse.Handled;
                    }
                    InvalidateCopyStatus();
                    _selection.Select(currentResult, hit.SourcePosition, modifiers.Toggle, modifiers.Range, preserveSelected: true);
                    var positions = _selection.OrderedPositions(currentResult);
                    PanelOwner.HighlightRows(currentResult.Snapshot, positions);
                    TrackSelectionCanvas(sender);
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
            if (_list.Contains(point))
            {
                ClearRowSelection(sender);
                return GH_ObjectResponse.Handled;
            }
            // Empty panel margins and its header also clear rows, while the host
            // remains responsible for selecting and dragging the component itself.
            if (_panel.Contains(point)) ClearRowSelection(sender);
            return base.RespondToMouseDown(sender, e);
        }

        private void ChooseMenu(int index, GH_Canvas canvas)
        {
            if (index < 0 || index >= _menu.Count) return;
            var kind = _menu.Kind;
            // Statistics are inspection only. A click must not select rows or change the output.
            if (kind == CanvasMenuKind.Statistics) return;
            // Resolve choices before closing releases the captured menu data.
            var selectedType = kind == CanvasMenuKind.Type && index > 1 ? _menu.TypeNameAt(index) : null;
            var selectedPath = kind == CanvasMenuKind.Branch && index > 1 ? _menu.BranchPathAt(index) : null;
            _menu.Close();
            if (kind == CanvasMenuKind.Preview)
            {
                if (index == CanvasMenu.ItemOrderIndex) PanelOwner.SetShowItemOrder(!PanelOwner.ShowItemOrder);
                else PanelOwner.SetPreviewMode((PanelPreviewMode)index);
                canvas.Invalidate();
                return;
            }
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
            if (kind == CanvasMenuKind.SearchMode)
            {
                _editingSearchMode = (PanelSearchMode)index;
                if (_editingSearchMode >= PanelSearchMode.Value)
                {
                    OpenMenu(CanvasMenuKind.Comparison, canvas);
                    SuppressTrailingDoubleClick();
                }
                else EditSearch(canvas, _editingSearchMode, options.SearchMode == _editingSearchMode ? options.Search : string.Empty);
                return;
            }
            if (kind == CanvasMenuKind.Comparison)
            {
                string comparison = CanvasMenu.ComparisonAt(index);
                string value = CanvasMenu.WithComparison(options.Search, index).Substring(comparison.Length).Trim();
                var mode = _editingSearchMode;
                CanvasTextInput.Show(canvas, PanelOwner, _search, value, text =>
                {
                    var query = PanelOwner.Session.Options;
                    query.SearchMode = mode;
                    query.Search = CanvasMenu.ApplyComparison(text, comparison);
                    PanelOwner.Session.UpdateQuery(query);
                });
                return;
            }
            if (kind == CanvasMenuKind.Type)
            {
                if (index == 1) options.OnlyInvalid = !options.OnlyInvalid;
                else { options.TypeName = selectedType; if (index == 0) options.OnlyInvalid = false; }
            }
            else if (kind == CanvasMenuKind.Sort) options.SortMode = (PanelSortMode)index;
            else if (index == 1)
            {
                CanvasTextInput.Show(canvas, PanelOwner, _branch, options.BranchPath ?? "{0}", SetBranchFilter);
                canvas.Invalidate();
                return;
            }
            else
            {
                SetBranchFilter(selectedPath);
                canvas.Invalidate();
                return;
            }
            PanelOwner.Session.UpdateQuery(options);
            canvas.Invalidate();
        }

        private void EditSearch(GH_Canvas canvas)
        {
            var options = PanelOwner.Session.Options;
            EditSearch(canvas, options.SearchMode, options.Search);
        }

        private void EditSearch(GH_Canvas canvas, PanelSearchMode mode, string current) =>
            CanvasTextInput.Show(canvas, PanelOwner, _search, current, text =>
            {
                var options = PanelOwner.Session.Options;
                options.SearchMode = mode;
                options.Search = text;
                PanelOwner.Session.UpdateQuery(options);
            });

        public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (PanelOwner.PanelMinimized) return base.RespondToMouseMove(sender, e);
            // Hidden controls must not claim a cursor that their mouse-down path
            // cannot act on. An already captured gesture still needs its moves.
            if (!_resizing && !_scrolling && sender.Viewport.Zoom < 0.5f)
                return base.RespondToMouseMove(sender, e);
            bool setControl = SetControlAt(e.CanvasLocation);
            sender.Cursor = _resizing || _resize.Contains(e.CanvasLocation) ? Cursors.SizeNWSE :
                _move.Contains(e.CanvasLocation) ? Cursors.SizeAll : setControl ? Cursors.Hand : Cursors.Default;
            if (_menu.Hover(e.CanvasLocation)) sender.Invalidate();
            int rowMenuHovered = _rowMenuResult != null && _rowMenuBounds.Contains(e.CanvasLocation)
                ? (int)((e.CanvasY - _rowMenuBounds.Y) / 20) : -1;
            if (rowMenuHovered != _rowMenuHovered) { _rowMenuHovered = rowMenuHovered; sender.Invalidate(); }
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
            // Grasshopper replaces the cursor after an ignored move event.
            if (_move.Contains(e.CanvasLocation) || _resize.Contains(e.CanvasLocation) || setControl) return GH_ObjectResponse.Handled;
            return base.RespondToMouseMove(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == MouseButtons.Right && _rowContextHandled)
            {
                _rowContextHandled = false;
                return GH_ObjectResponse.Handled;
            }
            if (_resizing || _scrolling) { _resizing = false; _scrolling = false; return GH_ObjectResponse.Release; }
            return base.RespondToMouseUp(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button != MouseButtons.Left) return base.RespondToMouseDoubleClick(sender, e);
            if (DateTime.UtcNow < _rowActionDismissedUntil) return GH_ObjectResponse.Handled;
            if (PanelOwner.PanelMinimized || _close.Contains(e.CanvasLocation) || _display.Contains(e.CanvasLocation) || _selectAll.Contains(e.CanvasLocation))
                return GH_ObjectResponse.Handled;
            if (sender.Viewport.Zoom < 0.5f) return base.RespondToMouseDoubleClick(sender, e);
            if (_rowMenuResult != null || _menu.Kind != CanvasMenuKind.None) return GH_ObjectResponse.Handled;
            if (_setDetails.Bounds.Contains(e.CanvasLocation) || _setHits.Any(hit => hit.Bounds.Contains(e.CanvasLocation)))
                return GH_ObjectResponse.Handled;
            if (_list.Contains(e.CanvasLocation))
            {
                ClearRowSelection(sender);
                return GH_ObjectResponse.Handled;
            }
            if (_filter.Contains(e.CanvasLocation)) return GH_ObjectResponse.Handled;
            if (_search.Contains(e.CanvasLocation)) { EditSearch(sender); return GH_ObjectResponse.Handled; }
            return base.RespondToMouseDoubleClick(sender, e);
        }

        public override void SetupTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            base.SetupTooltip(point, e);
            if (!PanelOwner.PanelMinimized && _menu.Kind == CanvasMenuKind.None && _rowMenuResult == null && SetDetailsTooltip(point, e)) return;
            if (_menu.Kind == CanvasMenuKind.Preview && _menu.Bounds.Contains(point))
            {
                int row = (int)((point.Y - _menu.Bounds.Y) / 20);
                if (row < _menu.Visible && _menu.Offset + row == CanvasMenu.ItemOrderIndex)
                {
                    e.Title = "Show item order";
                    e.Text = PanelOwner.ShowItemOrder ? "On" : "Off";
                    e.Description = CanvasMenu.ItemOrderDescription;
                    return;
                }
            }
            if (PanelOwner.PanelMinimized)
            {
                e.Title = _restore.Contains(point) ? "Restore panel" : "Panel+ capsule";
                e.Text = CapsuleSummary();
                e.Description = "Click the restore arrow to return to your " + PanelOwner.CanvasDisplay +
                    " panel. Filters, selection, scrolling and geometry preview are retained.";
                return;
            }
            if (_close.Contains(point))
            {
                e.Title = "Minimize to capsule";
                e.Text = "Keep the panel on the canvas";
                e.Description = "Shrink width and height to a compact summary. Restore reopens the same Full or Half view with your saved size, filters, selection and scrolling.";
                return;
            }
            if (_menu.Kind == CanvasMenuKind.Statistics && _menu.Bounds.Contains(point))
            {
                if (!StatisticsAreCurrent()) { InvalidateStatistics(); return; }
                int row = (int)((point.Y - _menu.Bounds.Y) / 20);
                int index = _menu.Offset + row;
                e.Title = "Tree statistics";
                if (row < _menu.Visible && index < _menu.Count)
                {
                    e.Text = _menu.StatisticsDetails(index, PanelOwner.DecimalPlaces);
                    e.Description = index >= 6 ? CanvasStatistics.UniqueScope : CanvasStatistics.Scope;
                }
                else e.Description = "Use Previous and Next to inspect all values.";
                return;
            }
            if (_move.Contains(point))
            {
                e.Title = "Move Panel+";
                e.Text = "Drag this header";
                e.Description = "The dotted grip and the space beside Panel+ move the component on the Grasshopper canvas.";
                return;
            }
            if (RowActionTooltip(point, e)) return;
            if (_display.Contains(point))
            {
                e.Title = PanelOwner.CanvasDisplay == CanvasPanelDisplay.Full ? "Show half panel" : "Show full panel";
                e.Text = "Full ↔ Half";
                e.Description = "Click to toggle panel sizes. Full restores your saved height. Half shows half that height. Wire connections and view settings are retained.";
                return;
            }
            if (_selectAll.Contains(point))
            {
                e.Title = "Select all rows";
                e.Text = !PanelOwner.Locked && PanelOwner.Session.HasCurrentResult
                    ? $"{PanelOwner.Session.Result.RowPositions.Count:N0} matching items"
                    : PanelOwner.Locked ? "Disabled" : PanelOwner.Session.IsBusy ? "Recomputing…" : "Selection unavailable";
                e.Description = "Select every row in the current results across all branches, including offscreen rows. Search, type, invalid and Paths filters are respected. Selection keeps the camera and output unchanged.";
                return;
            }
            if (_statistics.Contains(point) || _footer.Contains(point) && point.Y < _units.Y)
            {
                e.Title = "Tree statistics";
                e.Text = !PanelOwner.Locked && PanelOwner.Session.HasCurrentResult
                    ? CanvasStatistics.Summary(PanelOwner.Session.Result)
                    : PanelOwner.Locked ? "Disabled" : PanelOwner.Session.IsBusy ? "Recomputing…" : "Statistics unavailable";
                e.Description = "Click Stats for numeric summaries and the unique set of numeric/text values with their frequencies. " + CanvasStatistics.Scope;
                return;
            }
            if (_preview.Contains(point))
            {
                e.Title = "Geometry preview";
                e.Text = CanvasMenu.PreviewLabel(PanelOwner.PreviewMode);
                e.Description = "Selected previews only selected rows; an empty selection draws nothing. Filtered results previews all matching geometry, including offscreen rows, with selected rows highlighted. Off disables Panel+ preview. Show item order adds numbered labels in Rhino (" +
                    (PanelOwner.ShowItemOrder ? "on" : "off") + "). Other components keep their own previews. Right-click a row to frame the selection.";
            }
            else if (_units.Contains(point))
            {
                e.Title = "Units and decimals";
                e.Text = "Numeric output conversion · geometry metric display";
                e.Description = "Choose length (mm ↔ m), area (mm² ↔ m²), volume (mm³ ↔ m³), or angle (rad ↔ deg). Geometry metrics convert only matching model units and dimensions; the geometry itself is unchanged. Decimal places (0–12 or automatic) format metrics, numeric rows, copied numeric values and statistics without changing output precision.";
            }
            else if (_trim.Contains(point) || _simplify.Contains(point) || _flatten.Contains(point) || _reverse.Contains(point) || _createSet.Contains(point))
            {
                e.Title = _trim.Contains(point) ? "Trim tree" : _simplify.Contains(point) ? "Simplify tree" : _flatten.Contains(point) ? "Flatten tree" : _reverse.Contains(point) ? "Reverse tree" : "Create set";
                e.Text = (_trim.Contains(point) ? "Depth: " + PanelOwner.TrimDepth + " · " : "") + "Changes output · Simplify → Trim → Flatten → Reverse → Create set";
                e.Description = _trim.Contains(point) ? "Enter the number of trailing path levels to remove. Matching branches merge in source order. Fully trimmed paths become {0}. Enter 0 to disable." :
                    _simplify.Contains(point) ? "Toggle removal of path indices shared by all branches." :
                    _flatten.Contains(point) ? "Toggle merging all branches into {0}." :
                    _reverse.Contains(point) ? "Toggle reversing items within each output branch." :
                    "Keep the first of each exact number, text or primitive value per branch. Use Flatten for one set across the tree. Text is case-sensitive; decimal formatting is ignored. Repeated nulls become one; invalid values, complex geometry and custom data pass through unchanged.";
            }
            else if (_type.Contains(point) || _sort.Contains(point) || _branch.Contains(point))
            {
                var options = PanelOwner.Session.Options;
                e.Title = _type.Contains(point) ? "Filter by type" : _sort.Contains(point) ? "Sort this view" : "Filter by branch";
                e.Text = _type.Contains(point) ? PanelOwner.Session.HasCurrentResult ? CanvasMenu.TypeHint(PanelOwner.Session.Result, options) : "Types" :
                    _sort.Contains(point) ? options.SortMode.ToString() : options.BranchPath ?? "All branches";
                e.Description = "Filters keep matching items on the output wire after tree tools and unit conversion. Asc/Desc sorts items within each branch, keeping branch order. Sorting changes only the view; output keeps its tree order.";
                if (_type.Contains(point) && PanelOwner.Session.HasCurrentResult)
                    e.Description = (PanelOwner.Session.Result.RowPositions.Count > 0 ? "Visible types: " : "Source types: ") +
                        CanvasMenu.TypeDetails(PanelOwner.Session.Result) + "\n\n" + e.Description;
                else if (_branch.Contains(point))
                {
                    e.Description = "Choose a branch to filter the rows without moving the camera. Jump to an exact path using 0;0;1 or {0;0;1}; leave it empty for all branches. Click a branch heading to select its matching rows.\n\n" + e.Description;
                    if (PanelOwner.Session.Error != null) e.Description = PanelOwner.Session.Error + "\n\n" + e.Description;
                }
            }
            else if (_reset.Contains(point))
            {
                e.Title = "Reset panel";
                e.Description = "Restore the original incoming data: disable all tree tools and unit conversion, restore automatic decimals, clear search, filters, sorting and selection, and scroll to the top. Undo restores the previous settings.";
            }
            else if (_search.Contains(point))
            {
                var options = PanelOwner.Session.Options;
                e.Title = CanvasMenu.SearchModeLabel(options.SearchMode);
                e.Text = string.IsNullOrEmpty(options.Search) ? "Press Enter to apply · arrow chooses filter mode" : options.Search;
                e.Description = options.SearchMode == PanelSearchMode.Text
                    ? "Search displayed values, paths and types. Text matches use the selected units and decimal formatting; numeric comparisons such as > 800 or <= 25 use full precision. Use the arrow for text regex, measured geometry or per-branch list length filters."
                    : options.SearchMode == PanelSearchMode.Regex
                    ? "Match displayed row text with a case-insensitive regular expression, such as ^Wall or beam-[0-9]+. Matches use the selected units and decimal formatting. Empty text clears the filter."
                    : options.SearchMode == PanelSearchMode.ListLength
                    ? "Compare the number of items in each branch using >, >=, <, <=, = or !=. A number alone means equal. Counts include all items after tree tools and before type or invalid-only filters. Matching branches keep their items subject to those filters; = 0 keeps empty branches when no item filters are active. Empty text clears the filter."
                    : "Compare full-precision values using >, >=, <, <=, = or !=. A number alone means equal. Geometry uses the selected display units. Length: curves, including circle circumference. Area: rectangles, surfaces and open Breps. Volume: boxes and closed Breps. Rows without that measurement are excluded. Empty text clears the filter.";
                if (PanelOwner.Session.Error != null) e.Description = PanelOwner.Session.Error + "\n\n" + e.Description;
            }
            else if (_jump.Contains(point))
            {
                e.Title = "Jump to a row";
                e.Text = "Position in the current filtered and sorted view";
                e.Description = "Enter a row number starting at 1 across the complete filtered view. In Asc/Desc, branches retain their order and items sort within each branch. The large number is the continuous view position across branches; the smaller bracketed number is the original zero-based item index within its branch. Source order shows only the original index.";
            }
            else if (_resize.Contains(point))
            {
                e.Title = "Resize Panel+";
                e.Text = "Drag this corner";
                e.Description = "The panel size is saved with the Grasshopper definition.";
            }
            else if (_footer.Contains(point))
            {
                if (PanelOwner.Locked)
                {
                    e.Title = "Disabled";
                    e.Description = "Enable the component to recompute its input.";
                    return;
                }
                if (PanelOwner.Session.IsBusy)
                {
                    e.Title = "Recomputing…";
                    e.Description = "Rows and statistics will appear when the current data is ready.";
                    return;
                }
                if (PanelOwner.Session.Error != null)
                {
                    e.Title = "Filter needs attention";
                    e.Text = CanvasMenu.SearchModeLabel(PanelOwner.Session.Options.SearchMode);
                    e.Description = PanelOwner.Session.Error + "\n\nEdit the filter or use Reset to clear it.";
                    return;
                }
                e.Title = "Row selection and copy";
                e.Text = _copyPending || DateTime.UtcNow < _copiedUntil ? _actionStatus : $"{_selection.Count:N0} selected";
                e.Description = "Click a row to select it without moving the camera. Ctrl/Cmd-click toggles rows; Shift-click selects a range. Alt/Option-click selects the same index across filtered branches. Double-click a row or branch heading, or click empty table, panel or canvas space, to clear selection. Right-click to select indices across branches, frame the selection or copy values or geometry.";
            }
            else if (RowsAreCurrent(PanelOwner.Session.Result))
            {
                foreach (var hit in _branchHits)
                    if (hit.Bounds.Contains(point))
                    {
                        e.Title = "Select branch " + hit.Path;
                        e.Text = CanvasStatistics.BranchCount(PanelOwner.Session.Result, hit.Path);
                        e.Description = "Select every matching row in this branch, including rows outside the panel's visible area. Other active filters are respected. Double-click to clear selection.";
                        return;
                    }
                foreach (var hit in _rowHits)
                {
                    if (!hit.Bounds.Contains(point)) continue;
                    var row = PanelOwner.Session.Result.Snapshot.Rows[hit.SourcePosition];
                    e.Title = (PanelOwner.ShowItemOrder || PanelOwner.Session.Options.SortMode == PanelSortMode.SourceOrder
                        ? "" : "Row " + hit.ViewNumber + " · ") + row.Path + " [" + row.OriginalIndex + "]";
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
                    e.Description = details + "\n\nClick to select without moving the camera. Ctrl/Cmd-click toggles rows; Shift-click selects a range. Alt/Option-click selects the same index across filtered branches. Double-click to clear selection. Right-click to select indices across branches, frame the selection or copy values or geometry.";
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
        private void DrawCloseAction(Graphics g)
        {
            float x = _close.X + 6, y = _close.Y + 5;
            using (var pen = new Pen(Muted, 1.2f))
            {
                g.DrawLine(pen, x, y, x + 8, y + 8);
                g.DrawLine(pen, x + 8, y, x, y + 8);
            }
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
        private static string Short(string text, int maximum) => text.Length <= maximum ? text : text.Substring(0, maximum - 1) + "…";
        private readonly struct RowHit
        {
            internal RowHit(RectangleF bounds, int sourcePosition, int viewNumber) { Bounds = bounds; SourcePosition = sourcePosition; ViewNumber = viewNumber; }
            internal RectangleF Bounds { get; }
            internal int SourcePosition { get; }
            internal int ViewNumber { get; }
        }

        private readonly struct BranchHit
        {
            internal BranchHit(RectangleF bounds, string path) { Bounds = bounds; Path = path; }
            internal RectangleF Bounds { get; }
            internal string Path { get; }
        }

        private sealed class PanelPortAttributes : GH_LinkedParamAttributes
        {
            private PointF _grip;
            private readonly bool _input;
            internal PanelPortAttributes(IGH_Param owner, IGH_Attributes parent, bool input) : base(owner, parent) { _input = input; }
            internal void Place(PointF grip) { _grip = grip; Pivot = grip; Layout(); }
            protected override void Layout() => Bounds = new RectangleF(_grip.X - (_input ? 22 : 6), _grip.Y - 7, 28, 14);
            public override PointF InputGrip => _input ? _grip : base.InputGrip;
            public override PointF OutputGrip => !_input ? _grip : base.OutputGrip;
        }
    }
}
#pragma warning restore CA1416
