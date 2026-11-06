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
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

// Grasshopper's canvas drawing contract is supplied by Rhino on Windows and Mac.
#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes : GH_ComponentAttributes
    {
        private sealed class PaneView
        {
            internal int Offset, Shown, PreviewOffset, PaintedOffset;
            // The visible scroll track stays slim; its grab target is wider.
            internal RectangleF List, Track, TrackHit, Caption;
            internal bool Scrolling;
        }

        private readonly PaneView _d = new PaneView(), _f = new PaneView();

        private const float RowHeight = 17, BranchHeight = 15, HeaderHeight = CanvasPanelSize.HeaderHeight;
        private const float TypeColumnWidth = 44, CellGap = 10;
        private static Color Ink => PanelTheme.Ink;
        private static Color Muted => PanelTheme.Muted;
        private static Color Accent => PanelTheme.Accent;
        private const float OutputControlWidth = 92;
        private bool _singleLineFooter;
        private bool SearchClearVisible
        {
            get
            {
                var options = PanelOwner.Session.Options;
                return !_layout.SearchClear.IsEmpty && (!string.IsNullOrEmpty(options.Search) || options.DisplayFilterValue || CanvasMenu.IsComparisonMode(options.SearchMode));
            }
        }
        private bool BranchClearVisible => !_layout.BranchClear.IsEmpty && PanelOwner.Session.Options.BranchPath != null;
        private readonly List<RowHit> _rowHits = new List<RowHit>();
        private readonly List<BranchHit> _branchHits = new List<BranchHit>();
        private PanelSearchMode _editingSearchMode;
        private CanvasTextInput? _queryEditor;
        private PanelSnapshot? _queryEditorSnapshot;
        private long _queryEditorGeneration;
        private PanelQueryOptions? _previewOriginal;
        private object? _previewOwner;
        private int _firstShownItem, _lastShownItem;
        private PanelQueryResult? _lastResult;
        private PanelQueryResult? _paintedResult;
        private GH_Canvas? _paintedCanvas;
        private GH_Document? _paintedDocument;
        private RectangleF _paintedVisibleRegion, _paintedPanel, _paintedList;
        private RectangleF _paintedDetails, _staleList, _staleDetails, _staleRowMenu;
        private RectangleF _renderVisibleRegion;
        private bool _paintedListBackground, _preservePaintedTargets;
        private PointF _paintedPivot;
        private float _paintedZoom;
        private PanelRowSelection _selection;
        private GH_Canvas? _selectionCanvas;
        private readonly CanvasMenu _menu = new CanvasMenu();
        private bool _resizing;
        private PointF _dragStart;
        private float _startWidth, _startHeight, _thumbGrab;
        private BetterPanelComponent PanelOwner => (BetterPanelComponent)Owner;
        private bool DisplayListLengths => PanelOwner.Session.Options.DisplayFilterValue && PanelOwner.Session.Options.SearchMode == PanelSearchMode.ListLength;
        private int Count => PanelOwner.Session.HasCurrentResult ? DisplayListLengths ? PanelOwner.Session.Result.BranchPaths.Count : ViewCount(PanelOwner.Session.Result) : 0;
        private int MaximumOffset
        {
            get
            {
                int count = Count;
                if (count == 0) return 0;
                if (DisplayListLengths)
                {
                    float header = _d.List.Height >= BranchHeight + RowHeight ? BranchHeight : 0;
                    return Math.Max(0, count - Math.Max(1, (int)((_d.List.Height - header) / RowHeight)));
                }
                var result = PanelOwner.Session.Result;
                bool grouped = _d.List.Height >= BranchHeight + RowHeight;
                float used = 0;
                string? followingPath = null;
                int first = count;
                // At most one viewport of entries is visited, even for huge trees.
                for (int index = count - 1; index >= 0; index--)
                {
                    ViewEntry(result, index, out _, out string? emptyPath, out int extra, out _);
                    string path = ViewPath(result, index);
                    float height = extra >= 0 ? RowHeight + (grouped && path != followingPath && result.Snapshot.BranchItemCounts[path] > 0 ? BranchHeight : 0) : emptyPath != null ? (grouped ? BranchHeight : RowHeight) :
                        RowHeight + (grouped && path != followingPath ? BranchHeight : 0);
                    if (used + height > _d.List.Height) break;
                    used += height;
                    first = index;
                    followingPath = emptyPath == null ? path : null;
                }
                return Math.Min(count - 1, first);
            }
        }

        internal BetterPanelAttributes(BetterPanelComponent owner) : base(owner) { _selection = owner.RowSelection; }

        internal void UseRowSelection(PanelRowSelection selection) => _selection = selection;

        internal void ResetViewState(bool keepViewRestore = false)
        {
            // An unconfirmed live search never survives a reset or a new input.
            EndQueryPreview(_previewOwner);
            if (!keepViewRestore) { _viewRestore = null; _selectionRestore = null; _savedSelection = null; }
            ClearHover();
            _keyboardCursor = -1;
            CanvasTextInput.CloseActive(PanelOwner);
            CancelQueryEditor();
            _setDetails.Close();
            InvalidatePaintedHits();
            _setCountResult = null;
            _setControlsVisible = false;
            LayoutSetDetails();
            _d.Offset = _f.Offset = 0;
            _lastResult = null;
            ClearPaneSummary();
            if (keepViewRestore) _selection.Clear();
            else ClearRowSelection();
            PanelOwner.ClearHighlight();
            _d.Scrolling = _f.Scrolling = false;
            _rowContextHandled = false;
            _menu.Close();
            _rowActionDismissedUntil = default;
        }

        internal void ClearRowSelection()
        {
            _keyboardCursor = -1;
            _selectionRestore = null;
            _savedSelection = null;
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
            canvas.DocumentChanged += SelectionCanvasDocumentChanged;
            canvas.Disposed += SelectionCanvasDisposed;
        }

        private void DetachSelectionCanvas()
        {
            if (_selectionCanvas == null) return;
            _selectionCanvas.DocumentChanged -= SelectionCanvasDocumentChanged;
            _selectionCanvas.Disposed -= SelectionCanvasDisposed;
            _selectionCanvas = null;
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
            CancelQueryEditor();
            InvalidatePaintedHits();
            _menu.Close();
            CloseRowMenu();
            _resizing = _d.Scrolling = _f.Scrolling = _rowContextHandled = false;
        }

        internal void InvalidatePaintedHits(bool closeRowMenu = true)
        {
            if (_paintedResult != null)
            {
                _staleList = _paintedList;
                _staleDetails = _paintedDetails;
                if (_rowMenuResult != null && _rowMenuPainted) _staleRowMenu = _rowMenuBounds;
            }
            _paintedResult = null;
            _paintedCanvas = null;
            _paintedDocument = null;
            _rowHits.Clear();
            _branchHits.Clear();
            _warningHits.Clear();
            _setHits.Clear();
            _paintedDetailTargets.Clear();
            _paintedListBackground = false;
            _menu.InvalidatePaint();
            if (closeRowMenu) CloseRowMenu();
        }

        internal void OnQueryStarted()
        {
            ClearPaneSummary();
            InvalidatePaintedHits();
        }

        internal void OnQueryCompleted()
        {
            var session = PanelOwner.Session;
            if (_queryEditorSnapshot != null && !ReferenceEquals(_queryEditorSnapshot, session.Snapshot)) CancelQueryEditor();
            if (!StatisticsAreCurrent()) InvalidateStatistics();
            if (session.IsBusy) { InvalidatePaintedHits(); PanelOwner.ClearHighlight(); return; }
            var result = session.Result;
            if (!session.HasCurrentResult)
            {
                InvalidatePaintedHits();
                _selection.Clear();
                if (_selectionRestore == null) DetachSelectionCanvas();
                PanelOwner.ClearHighlight();
                LayoutSetDetails();
                return;
            }
            if (!ReferenceEquals(result, _lastResult))
            {
                InvalidatePaintedHits();
                InvalidateCopyStatus();
                _selection.Synchronize(result);
                _keyboardCursor = -1;
                _lastResult = result;
                _d.Offset = _f.Offset = 0;
                _menu.Close();
                ClearHover();
                RestoreView(result);
            }
            LayoutSetDetails();
            PanelOwner.HighlightRows(result.Snapshot, _selection.OrderedPositions(result));
            if (_selection.Count == 0) DetachSelectionCanvas();
            else if (Grasshopper.Instances.ActiveCanvas is GH_Canvas canvas) TrackSelectionCanvas(canvas);
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
            try { options.BranchPath = PanelQueryEngine.NormalizeBranchPath(options.BranchPath); }
            catch (ArgumentException) { }
            if (PanelQueryPolicy.SameQuery(session.Options, options)) return;
            PanelOwner.RecordUndoEvent("Panel branch filter");
            ClearRowSelection();
            var previousResult = session.Result;
            session.UpdateQuery(options);
            PanelOwner.OnPingDocument()?.Modified();
            if (ReferenceEquals(previousResult, session.Result)) OnQueryCompleted();
        }

        protected override void Layout()
        {
            InvalidatePaintedHits();
            _layout = ComputePanelLayout();
            m_innerBounds = _layout.Panel;
            if (PanelOwner.PanelMinimized)
            {
                ClosePresentationControls();
                ReleaseKeyboardFocus();
                PositionPorts(Owner.Params.Input, true);
                PositionPorts(Owner.Params.Output, false);
                Bounds = new RectangleF(_layout.Panel.X - 22, _layout.Panel.Y, _layout.Panel.Width + 44, _layout.Panel.Height);
                LayoutSetDetails();
                return;
            }
            if (PanelOwner.Session.HasCurrentResult)
            {
                _d.Offset = Math.Min(_d.Offset, MaximumOffset);
                _f.Offset = _scrollLocked ? _d.Offset : Math.Min(_f.Offset, MaximumOffset);
            }
            // Linked parameter attributes retain native wire and tooltip behavior.
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
                float top = PanelOwner.PanelMinimized ? _layout.Panel.Top + 12 : Pivot.Y + PanelOwner.CanvasHeight / 4 + 12;
                float span = PanelOwner.PanelMinimized ? _layout.Panel.Height - 24 : PanelOwner.CanvasHeight / 2 - 48;
                // Native wire picking searches within 12 canvas units in reverse
                // parameter order. Keep neighbours beyond that radius even at
                // the vertical edge of a grip's target; reserve the resize corner.
                float offset = parameters.Count == 1 ? span / 2 : span * i / (parameters.Count - 1);
                var point = new PointF(input ? _layout.Panel.Left : _layout.Panel.Right, top + offset);
                port.Place(point);
            }
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            if (channel == GH_CanvasChannel.Wires) { base.Render(canvas, graphics, channel); return; }
            if (channel != GH_CanvasChannel.Objects) return;
            _setControlsVisible = canvas.Viewport.Zoom >= 0.5f;
            LayoutSetDetails();
            CanvasTextInput.Maintain(canvas);
            var session = PanelOwner.Session;
            _preservePaintedTargets = ReferenceEquals(_paintedCanvas, canvas) && PaintedViewIsCurrent() &&
                ReferenceEquals(_paintedResult, session.Result) && ResultIsCurrent(session.Result);
            if (!_preservePaintedTargets)
            {
                bool canvasChanged = _paintedCanvas != null && !ReferenceEquals(_paintedCanvas, canvas);
                InvalidatePaintedHits(closeRowMenu: canvasChanged);
                _rowMenuPainted = false;
            }
            var visibleRegion = canvas.Viewport.VisibleRegion;
            _renderVisibleRegion = visibleRegion;
            session.AnimateBusyState(!PanelOwner.Locked && Bounds.IntersectsWith(visibleRegion) && canvas.Viewport.Zoom >= 0.5f);
            // Grasshopper also renders components outside the canvas viewport.
            if (!Bounds.IntersectsWith(visibleRegion))
            {
                _rowHits.Clear(); _branchHits.Clear(); _d.Shown = 0; _menu.Close(); CloseRowMenu();
                session.RequestGeometryMeasurements(session.Snapshot, Array.Empty<int>());
                return;
            }
            var result = session.Result;
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
                // Minimum Half split needs a one-line footer to fit a value row below the pane captions.
                bool singleLine = PanelOwner.PanelSplit && _layout.Panel.Height < 140 || _layout.Panel.Width >= 500 &&
                    graphics.MeasureString(CanvasStatistics.Summary(result) + " · " + OutputLabel(result), small).Width <=
                        _layout.Panel.Width - 357 - (PanelOwner.CanOutputValues ? OutputControlWidth + 4 : 0);
                if (singleLine != _singleLineFooter || PanelOwner.CanOutputValues == _layout.OutLabel.IsEmpty ||
                    _warningsHeight != WarningStripHeight(singleLine ? 20 : 36))
                {
                    _singleLineFooter = singleLine;
                    Layout();
                    _preservePaintedTargets = false;
                }
                Fill(graphics, PanelTheme.Background, _layout.Panel);
                Fill(graphics, Accent, new RectangleF(_layout.Panel.X, _layout.Panel.Y, 3, HeaderHeight));
                using (var grip = new SolidBrush(Muted))
                    for (int column = 0; column < 2; column++)
                        for (int row = 0; row < 3; row++)
                            graphics.FillEllipse(grip, _layout.Move.X + 5 + column * 4, _layout.Move.Y + 5 + row * 4, 1.5f, 1.5f);
                Text(graphics, "P+", bold, Accent, new RectangleF(_layout.Move.X + 18, _layout.Move.Y, _layout.Move.Width - 20, 20), trim);
                if (canvas.Viewport.Zoom >= 0.5f)
                {
                    DrawDisplayAction(graphics);
                    DrawCloseAction(graphics);
                    DrawSplitActions(graphics, small, trim);
                }
                if (canvas.Viewport.Zoom >= 0.5f)
                {
                    var options = session.Options;
                    DropdownControl(graphics, _layout.Preview, "Preview: " + CanvasMenu.PreviewLabel(PanelOwner.PreviewMode), small, trim,
                        PanelOwner.PreviewMode != PanelPreviewMode.Off);
                    bool searchClear = SearchClearVisible, branchClear = BranchClearVisible;
                    DropdownControl(graphics, _layout.Search, CanvasMenu.SearchHint(options), small, trim,
                        options.SearchMode != PanelSearchMode.Text || !string.IsNullOrEmpty(options.Search), searchClear ? _layout.SearchClear.Width : 0);
                    if (searchClear) DrawClear(graphics, _layout.SearchClear);
                    string typeHint = session.HasCurrentResult ? CanvasMenu.TypeHint(result, options) : "Types";
                    DropdownControl(graphics, _layout.Type, typeHint, small, trim, options.TypeName != null || options.OnlyInvalid,
                        warning: PanelQueryPolicy.InputDiagnostic(session.Snapshot, options).Length > 0);
                    DropdownControl(graphics, _layout.Sort, options.SortMode == PanelSortMode.SourceOrder ? "Sort" : options.SortMode == PanelSortMode.ValueAscending ? "Asc" : "Desc", small, trim, options.SortMode != PanelSortMode.SourceOrder);
                    DropdownControl(graphics, _layout.Branch, Short(options.BranchPath ?? "Paths", 80), mono, trim, options.BranchPath != null, branchClear ? _layout.BranchClear.Width : 0);
                    if (branchClear) DrawClear(graphics, _layout.BranchClear);
                    DrawJumpAction(graphics);
                    DrawResetAction(graphics);
                    RenderWarnings(graphics, result, small);
                    RenderRows(graphics, result, visibleRegion, font, small, mono, trim);
                    if (_scrollLocked) _f.Offset = _d.Offset;
                    if (PanelOwner.PanelSplit) RenderRows(graphics, result, visibleRegion, font, small, mono, trim, paired: true);
                    RenderFooter(graphics, result, small, trim);
                    RenderSetDetails(graphics, result, small, mono, trim);
                    RenderHover(graphics);
                    if (_menu.Kind != CanvasMenuKind.None) RenderMenu(graphics, small, mono, trim);
                    if (_rowMenuResult != null) RenderRowMenu(graphics, small, trim);
                    using (var gripPen = new Pen(PanelTheme.Rgb(165, 173, 166), 1))
                    {
                        for (int inset = 8; inset <= 18; inset += 5)
                            graphics.DrawLine(gripPen, _layout.Panel.Right - inset, _layout.Panel.Bottom - 4, _layout.Panel.Right - 4, _layout.Panel.Bottom - inset);
                    }
                    _paintedResult = ResultIsCurrent(result) ? result : null;
                    _paintedCanvas = canvas;
                    _paintedDocument = canvas.Document;
                    _paintedVisibleRegion = visibleRegion;
                    _paintedZoom = canvas.Viewport.Zoom;
                    _paintedPanel = _layout.Panel;
                    _paintedList = ListBounds;
                    _paintedDetails = _setDetails.Bounds;
                    _paintedPivot = Pivot;
                    _d.PaintedOffset = _d.Offset;
                    _f.PaintedOffset = _f.Offset;
                    _staleList = _staleDetails = _staleRowMenu = RectangleF.Empty;
                }
                else
                {
                    _rowHits.Clear(); _branchHits.Clear(); _d.Shown = 0; _menu.Close(); CloseRowMenu();
                    ReleaseKeyboardFocus();
                    session.RequestGeometryMeasurements(session.Snapshot, Array.Empty<int>());
                    RenderOverview(graphics);
                }
                using (var border = new Pen(Selected ? Accent : PanelTheme.Rgb(164, 171, 164), Selected ? 1.5f : 1))
                    graphics.DrawRectangle(border, _layout.Panel.X, _layout.Panel.Y, _layout.Panel.Width, _layout.Panel.Height);
                // Keyboard and wheel input belong to this panel only while the ring shows.
                if (KeyboardFocused)
                    using (var ring = new Pen(PanelTheme.Rgb(110, 160, 128), 2))
                        graphics.DrawRectangle(ring, _layout.Panel.X - 3, _layout.Panel.Y - 3, _layout.Panel.Width + 6, _layout.Panel.Height + 6);
                RenderPorts(graphics, canvas, small, trim);
            }
        }

        // Below half zoom rows are unreadable; show the capsule summary at overview size.
        private void RenderOverview(Graphics g)
        {
            var body = new RectangleF(_layout.Panel.X + 10, _layout.Panel.Y + HeaderHeight + 6, _layout.Panel.Width - 20, _layout.Panel.Height - HeaderHeight - 12);
            if (body.Height < 20) return;
            using var large = new Font("Arial", Math.Min(28f, Math.Max(14f, body.Height / 5)), FontStyle.Regular, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
            using var brush = new SolidBrush(Muted);
            g.DrawString(CapsuleSummary().Replace(" · ", "\n"), large, brush, body, format);
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
            using (var fill = new SolidBrush(PanelTheme.Background))
            using (var border = new Pen(Selected ? Accent : PanelTheme.Rgb(164, 171, 164), Selected ? 1.5f : 1))
            {
                const float corner = 12;
                shape.AddArc(_layout.Panel.X, _layout.Panel.Y, corner, corner, 180, 90);
                shape.AddArc(_layout.Panel.Right - corner, _layout.Panel.Y, corner, corner, 270, 90);
                shape.AddArc(_layout.Panel.Right - corner, _layout.Panel.Bottom - corner, corner, corner, 0, 90);
                shape.AddArc(_layout.Panel.X, _layout.Panel.Bottom - corner, corner, corner, 90, 90);
                shape.CloseFigure();
                g.FillPath(fill, shape);
                g.DrawPath(border, shape);
            }
            float textTop = _layout.Panel.Y + (_layout.Panel.Height - 22) / 2;
            Text(g, "P+", bold, Accent, new RectangleF(_layout.Panel.X + 8, textTop, 27, 22), trim);
            Text(g, CapsuleSummary(), small, Muted,
                new RectangleF(_layout.Panel.X + 36, textTop, _layout.Restore.Left - _layout.Panel.X - 40, 22), trim);
            if (!_hoverBounds.IsEmpty && _hoverBounds == _layout.Restore) Fill(g, PanelTheme.Hover, _layout.Restore);
            // A window outline reads as "restore", not as "open in a new window".
            using (var pen = new Pen(Accent, 1.2f))
            {
                float x = _layout.Restore.X + 5, y = _layout.Restore.Y + 6;
                g.DrawRectangle(pen, x, y, 12, 10);
                g.DrawLine(pen, x, y + 2.5f, x + 12, y + 2.5f);
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
                    Text(g, OutputPortLabel(parameter), font, Muted,
                        new RectangleF(grip.X + 8, grip.Y - 8, 12, 16), trim);
            }
        }

        private void RenderRows(Graphics g, PanelQueryResult result, RectangleF visibleRegion, Font font, Font small, Font mono, StringFormat trim, bool paired = false)
        {
            if (!paired && !_preservePaintedTargets) { _setHits.Clear(); _rowHits.Clear(); _branchHits.Clear(); }
            var pane = paired ? _f : _d;
            var list = pane.List;
            var track = pane.Track;
            if (PanelOwner.PanelSplit && _scrollLocked) track = paired ? RectangleF.Empty : _f.Track;
            ref int offset = ref pane.Offset;
            ref int shown = ref pane.Shown;
            int firstShownItem = -1, lastShownItem = -1;
            if (!paired) _firstShownItem = _lastShownItem = -1;
            shown = 0;
            _paintedListBackground |= TargetPainted(g, list);
            if (paired && (PanelOwner.PairedError != null || !PanelOwner.CanSplit))
            {
                DrawPairedDiagnostic(g, small);
                return;
            }
            if (PanelOwner.Locked)
            {
                Text(g, "Disabled. Enable the component to recompute.", small, Muted,
                    new RectangleF(list.X + 6, list.Y + 12, list.Width - 12, 22), trim);
                if (!paired) PanelOwner.Session.RequestGeometryMeasurements(PanelOwner.Session.Snapshot, Array.Empty<int>());
                return;
            }
            if (PanelOwner.Session.IsBusy)
            {
                // Clear both rows and their hit regions while the current view is
                // being computed; never mix old statistics/values with new input.
                float busyY = list.Y + Math.Max(0, (list.Height - 22) / 2);
                var ring = new RectangleF(list.X + 9, busyY + 5, 12, 12);
                using (var ringTrack = new Pen(PanelTheme.Rgb(225, 231, 224), 2))
                    g.DrawEllipse(ringTrack, ring);
                using (var progress = new Pen(Accent, 2))
                    g.DrawArc(progress, ring, (float)(DateTime.UtcNow.TimeOfDay.TotalMilliseconds % 900 / 900 * 360), 250);
                Text(g, "Recomputing…", small, Muted,
                    new RectangleF(list.X + 29, busyY, list.Width - 35, 22), trim);
                if (!paired) PanelOwner.Session.RequestGeometryMeasurements(PanelOwner.Session.Snapshot, Array.Empty<int>());
                return;
            }
            if (PanelOwner.Session.Error != null)
            {
                Text(g, "Edit the filter or Clear filters.", small, Muted,
                    new RectangleF(list.X + 6, list.Y, list.Width - 12, 22), trim);
                if (!paired) PanelOwner.Session.RequestGeometryMeasurements(result.Snapshot, Array.Empty<int>());
                return;
            }
            if (!PanelOwner.Session.HasCurrentResult) return;
            if (DisplayListLengths)
            {
                _setDetails.Close();
                RenderListLengths(g, result, font, small, mono, trim, paired);
                if (!paired) PanelOwner.Session.RequestGeometryMeasurements(result.Snapshot, Array.Empty<int>());
                return;
            }
            float y = list.Y;
            string? previousPath = null;
            // A half-height panel at the minimum saved size still has room for a
            // value, even when a separate branch heading would consume that row.
            bool sorted = PanelOwner.Session.Options.SortMode != PanelSortMode.SourceOrder;
            bool grouped = list.Height >= BranchHeight + RowHeight;
            bool displayMetric = !paired && (PanelOwner.Session.Options.DisplayFilterValue || result.DisplaySet != null);
            bool numericMetric = displayMetric && (!PanelProperties.IsProperty(PanelOwner.Session.Options.SearchMode) ||
                PanelProperties.Kind(PanelOwner.Session.Options.SearchMode) == PanelPropertyKind.Number);
            bool showTypes = paired ? PanelOwner.PairedSnapshot?.TypeCounts.Count > 1 : displayMetric || result.Statistics.TypeCounts.Count > 1 || PanelOwner.Session.Options.OnlyInvalid;
            using var rowRenderer = new CanvasRowRenderer(g, PanelOwner.PanelSplit ? Math.Max(result.Snapshot.Rows.Count, PanelOwner.PairedSnapshot?.Rows.Count ?? 0) : result.Snapshot.Rows.Count, sorted);
            if (!paired && PanelOwner.CreateSetTree) SizeSetColumn(g, rowRenderer, result);
            float setWidth = !paired && PanelOwner.CreateSetTree ? _setCountWidth : 0;
            // Grouped rows already show their path in the branch heading.
            float pathWidth = grouped ? 0 : Math.Min(80, Math.Max(0, list.Width - rowRenderer.GutterWidth -
                (showTypes ? TypeColumnWidth + CellGap : 12) - 40 - setWidth));
            float valueStart = rowRenderer.GutterWidth + pathWidth;
            var measurements = new List<int>();
            offset = Math.Max(0, Math.Min(Math.Max(0, Count - 1), offset));
            var clip = g.Save();
            try
            {
                g.SetClip(list, CombineMode.Intersect);
                for (int scrollIndex = offset; scrollIndex < ViewCount(result); scrollIndex++)
                {
                    ViewEntry(result, scrollIndex, out int i, out string? emptyPath, out int extra, out string missingLabel);
                    if (extra >= 0)
                    {
                        string extraPath = ViewPath(result, scrollIndex);
                        if (grouped && extraPath != previousPath && result.Snapshot.BranchItemCounts[extraPath] > 0)
                        {
                            if (y + BranchHeight + RowHeight > list.Bottom) break;
                            var headingBounds = new RectangleF(list.X, y, list.Width, BranchHeight);
                            Fill(g, PanelTheme.Rgb(236, 241, 235), headingBounds);
                            if (paired) DrawPairedHeading(g, extraPath, headingBounds, small, mono, trim);
                            else
                            {
                                var count = CanvasStatistics.BranchCount(result, extraPath);
                                float countWidth = Math.Min(list.Width / 2, g.MeasureString(count, small).Width + 6);
                                Text(g, extraPath, mono, Accent, new RectangleF(list.X + 6, y, list.Width - countWidth - 36, BranchHeight), trim);
                                Text(g, count, small, Muted, new RectangleF(list.Right - countWidth - 6, y, countWidth, BranchHeight), trim);
                            }
                            if (!paired) DrawBranchExtras(g, result, headingBounds, extraPath, mono, small, trim, countText: CanvasStatistics.BranchCount(result, extraPath));
                            y += BranchHeight;
                            previousPath = extraPath;
                        }
                        if (y + RowHeight > list.Bottom) break;
                        var spacer = new RectangleF(list.X, y, list.Width, RowHeight);
                        Fill(g, PanelTheme.Background, spacer);
                        if (paired)
                        {
                            using var italic = new Font(small, FontStyle.Italic);
                            var extraRow = PanelOwner.PairedSnapshot!.Rows[extra];
                            rowRenderer.DrawAddress(g, spacer, scrollIndex + 1, extraRow.OriginalIndex, sorted, false);
                            float noteWidth = missingLabel.Length == 0 ? 0 : g.MeasureString(missingLabel, small).Width + 10;
                            rowRenderer.DrawCell(g, PairedDisplayValue(extraRow), italic, Muted,
                                new RectangleF(list.X + valueStart + 3, y, Math.Max(0, list.Width - valueStart - 9 - noteWidth), RowHeight));
                            if (noteWidth > 0) rowRenderer.DrawCell(g, missingLabel, small, Muted,
                                new RectangleF(list.Right - noteWidth - 3, y, noteWidth, RowHeight), true);
                        }
                        shown++; y += RowHeight;
                        continue;
                    }
                    if (emptyPath != null)
                    {
                        float height = grouped ? BranchHeight : RowHeight;
                        if (y + height > list.Bottom) break;
                        var emptyBounds = new RectangleF(list.X, y, list.Width, height);
                        Fill(g, PanelTheme.Rgb(236, 241, 235), emptyBounds);
                        if (grouped)
                        {
                            var count = CanvasStatistics.BranchCount(result, emptyPath);
                            float countWidth = Math.Min(list.Width / 2, g.MeasureString(count, small).Width + 6);
                            if (paired) DrawPairedHeading(g, emptyPath, emptyBounds, small, mono, trim);
                            else Text(g, emptyPath, mono, Accent, new RectangleF(list.X + 6, y, list.Width - countWidth - 36, height), trim);
                            if (!paired) Text(g, count, small, Muted, new RectangleF(list.Right - countWidth - 6, y, countWidth, height), trim);
                        }
                        else rowRenderer.DrawCell(g, emptyPath, mono, Accent,
                            new RectangleF(list.X + 6, y, list.Width - 12, height));
                        if (!paired || !grouped) DrawBranchExtras(g, result, emptyBounds, emptyPath, mono, small, trim, countText: CanvasStatistics.BranchCount(result, emptyPath), paired: paired);
                        shown++;
                        y += height;
                        previousPath = null;
                        continue;
                    }
                    int sourcePosition = result.RowPositions[i];
                    var dataRow = result.Snapshot.Rows[sourcePosition];
                    var pairedRow = paired ? PanelOwner.PairedRow(sourcePosition) : null;
                    var row = pairedRow ?? dataRow;
                    if (grouped && dataRow.Path != previousPath)
                    {
                        if (y + BranchHeight + RowHeight > list.Bottom) break;
                        var branchBounds = new RectangleF(list.X, y, list.Width, BranchHeight);
                        Fill(g, PanelTheme.Rgb(236, 241, 235), branchBounds);
                        var count = CanvasStatistics.BranchCount(result, dataRow.Path);
                        float countWidth = Math.Min(list.Width / 2, g.MeasureString(count, small).Width + 6);
                        if (paired) DrawPairedHeading(g, dataRow.Path, branchBounds, small, mono, trim);
                        else
                        {
                            Text(g, dataRow.Path, mono, Accent, new RectangleF(list.X + 6, y, list.Width - countWidth - 36, BranchHeight), trim);
                            Text(g, count, small, Muted, new RectangleF(list.Right - countWidth - 6, y, countWidth, BranchHeight), trim);
                        }
                        if (!paired) DrawBranchExtras(g, result, branchBounds, dataRow.Path, mono, small, trim, countText: count);
                        y += BranchHeight;
                        previousPath = dataRow.Path;
                    }
                    if (y + RowHeight > list.Bottom) break;
                    var bounds = new RectangleF(list.X, y, list.Width, RowHeight);
                    bool selected = _selection.Contains(sourcePosition);
                    Fill(g, selected ? PanelTheme.Rgb(216, 232, 219) : (shown & 1) == 0 ? PanelTheme.Background : PanelTheme.Rgb(247, 249, 246), bounds);
                    if (paired && pairedRow == null && result.DisplaySet == null)
                    {
                        Fill(g, selected ? PanelTheme.Rgb(216, 232, 219) : PanelTheme.Background, bounds);
                        if (missingLabel.Length > 0)
                        {
                            using var italic = new Font(small, FontStyle.Italic);
                            Text(g, missingLabel, italic, Muted, new RectangleF(bounds.X + valueStart + 3, y, Math.Max(0, bounds.Width - valueStart - 9), RowHeight), trim);
                        }
                        if (TargetPainted(g, bounds) && !_rowHits.Any(hit => hit.Bounds == bounds && hit.SourcePosition == sourcePosition))
                            _rowHits.Add(new RowHit(bounds, sourcePosition, i + 1));
                        if (firstShownItem < 0) firstShownItem = i;
                        lastShownItem = i;
                        shown++; y += RowHeight;
                        continue;
                    }
                    var address = RowAddress(result, sourcePosition, paired);
                    if (address.Index >= 0) rowRenderer.DrawAddress(g, bounds, i + 1, address.Index, address.Sorted, selected);
                    var sourceStatuses = paired ? new PanelSourceStatusCounts(0,
                        row.Validity == PanelValidity.VerifiedInvalid ? 1 : 0, row.Validity == PanelValidity.Unverified ? 1 : 0) :
                        CanvasStatistics.RowSourceStatuses(result, sourcePosition);
                    if (sourceStatuses.VerifiedInvalid > 0)
                        Fill(g, PanelTheme.Rgb(180, 106, 67), new RectangleF(bounds.X, bounds.Y + 3, 2, bounds.Height - 6));
                    if (sourceStatuses.Unverified > 0)
                        Fill(g, PanelTheme.Rgb(103, 112, 166), new RectangleF(bounds.X + (sourceStatuses.VerifiedInvalid > 0 ? 2 : 0), bounds.Y + 3, 2, bounds.Height - 6));
                    if (!grouped)
                        rowRenderer.DrawCell(g, dataRow.Path, mono, Accent,
                            new RectangleF(bounds.X + rowRenderer.GutterWidth + 9, y, Math.Max(0, pathWidth - 12), RowHeight));
                    // Analytic summaries name their type. Native metadata summaries
                    // (for example dot text/location) use the catalog's separate label.
                    bool showRowType = showTypes && !(paired && result.DisplaySet != null) && (displayMetric || row.GeometryInfo == null || StockTypeLabels.SeparateLabel(row.TypeName));
                    float valueRight = bounds.Right - setWidth - (showRowType ? TypeColumnWidth + CellGap : 12);
                    var projectedType = !paired && result.DisplaySet != null ? result.DisplaySet.Values[sourcePosition] : null;
                    // A glyph as well as the colour bar, so validity never relies on colour alone.
                    float marker = 0;
                    if (sourceStatuses.VerifiedInvalid > 0 || sourceStatuses.Unverified > 0)
                    {
                        DrawStatusBadge(g, new PointF(bounds.X + valueStart + 8, y + RowHeight / 2),
                            sourceStatuses.VerifiedInvalid > 0 ? "!" : "?", sourceStatuses.VerifiedInvalid > 0 ? PanelTheme.Warning : PanelTheme.Unverified, small);
                        marker = 12;
                    }
                    rowRenderer.DrawValue(g, PanelValuePresentation.Visualize(paired ? result.DisplaySet != null ? PairedSetValue(sourcePosition) : PairedDisplayValue(row) : PanelOwner.DisplayValue(result.Snapshot, row, sourcePosition)), font, Ink,
                        new RectangleF(bounds.X + valueStart + 3 + marker, y, Math.Max(0, valueRight - bounds.X - valueStart - 3 - marker), RowHeight),
                        projectedType != null ? projectedType.Number.HasValue : !(paired && result.DisplaySet != null) && (numericMetric || !displayMetric && row.NumericValue.HasValue));
                    if (showRowType)
                        rowRenderer.DrawCell(g, projectedType != null
                                ? projectedType.Number.HasValue ? "Num" : "Text" : CanvasMenu.ShortTypeName(row.TypeName), small, Muted,
                            new RectangleF(bounds.Right - setWidth - TypeColumnWidth, y, TypeColumnWidth - CellGap, RowHeight), true);
                    if (!paired) DrawSetCount(g, rowRenderer, bounds, sourcePosition, i + 1, small);
                    if (TargetPainted(g, bounds) && !_rowHits.Any(hit => hit.Bounds == bounds && hit.SourcePosition == sourcePosition))
                        _rowHits.Add(new RowHit(bounds, sourcePosition, i + 1));
                    if (!paired && bounds.IntersectsWith(visibleRegion)) measurements.Add(sourcePosition);
                    if (firstShownItem < 0) firstShownItem = i;
                    lastShownItem = i;
                    shown++;
                    y += RowHeight;
                }
                if (ViewCount(result) == 0)
                {
                    Text(g, result.Snapshot.Branches.Count == 0 ? "Connect a tree to start exploring." : "No results. Clear a filter or Reset all.",
                        small, Muted, new RectangleF(list.X + 6, list.Y + 12, list.Width - 12, 22), trim);
                }
            }
            finally { g.Restore(clip); }
            if (!paired)
            {
                _firstShownItem = firstShownItem;
                _lastShownItem = lastShownItem;
                PanelOwner.Session.RequestGeometryMeasurements(result.Snapshot, measurements);
            }
            if (Count > shown && !track.IsEmpty)
            {
                Fill(g, PanelTheme.Rgb(232, 236, 229), track);
                Fill(g, PanelTheme.Rgb(153, 174, 156), Thumb(paired));
            }
        }

        private static void DrawStatusBadge(Graphics g, PointF center, string glyph, Color color, Font font)
        {
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(color)) g.FillEllipse(brush, center.X - 5, center.Y - 5, 10, 10);
            g.SmoothingMode = mode;
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var bold = new Font(font, FontStyle.Bold);
            using var text = new SolidBrush(PanelTheme.Background);
            g.DrawString(glyph, bold, text, new RectangleF(center.X - 6, center.Y - 6, 12, 12), format);
        }

        private int ActiveFilterCount()
        {
            var options = PanelOwner.Session.Options;
            return (!string.IsNullOrEmpty(options.Search) ? 1 : 0) + (options.TypeName != null || options.OnlyInvalid ? 1 : 0) +
                (options.BranchPath != null ? 1 : 0);
        }

        private string OutputLabel(PanelQueryResult result)
        {
            int filters = ActiveFilterCount();
            if (filters > 0) return filters == 1 ? "1 filter on" : filters + " filters on";
            if (result.RowPositions.Count != result.Snapshot.Rows.Count || result.Statistics.BranchCount != result.Snapshot.Branches.Count)
                return "filtered";
            return PanelOwner.HasTreeOperations || PanelOwner.UnitConversion != PanelUnitConversion.None || PanelOwner.RoundUpFactor > 0 || PanelOwner.DecimalPlaces >= 0
                ? "tools on" : PanelOwner.OutputsValues ? "values out" : "output = input";
        }

        private void RenderFooter(Graphics g, PanelQueryResult result, Font small, StringFormat trim)
        {
            var session = PanelOwner.Session;
            bool current = !PanelOwner.Locked && session.HasCurrentResult;
            if (current && !_singleLineFooter)
                Text(g, CanvasStatistics.Summary(result), small, Ink,
                    new RectangleF(_layout.Footer.X, _layout.Footer.Y, (!_layout.OutLabel.IsEmpty ? _layout.OutLabel.Left - 4 : _layout.SelectAll.Left - 5) - _layout.Footer.X, _layout.Statistics.Height), trim);
            int selected = !current ? 0 : DisplayListLengths ? _selection.BranchPaths(result).Count : _selection.Count;
            int selectableCount = DisplayListLengths ? result.BranchPaths.Count : result.RowPositions.Count;
            Control(g, _layout.SelectAll, "Select all", small, trim, current && selectableCount > 0 && selected == selectableCount);
            Control(g, _layout.Statistics, "Stats", small, trim, current && _menu.Kind == CanvasMenuKind.Statistics);
            string status = FooterStatus(result, _singleLineFooter);
            float statusRight = _singleLineFooter ? (!_layout.OutLabel.IsEmpty ? _layout.OutLabel.Left - 4 : _layout.SelectAll.Left - 6) : _layout.Trim.Left - 6;
            if (!_layout.OutLabel.IsEmpty) DrawOutputControl(g, small, trim);
            if (statusRight - _layout.Footer.X >= g.MeasureString("000…", small).Width)
                Text(g, status, small, Muted, new RectangleF(_layout.Footer.X, _layout.Units.Y, statusRight - _layout.Footer.X, _layout.Units.Height), trim);
            TreeToolIcons.Draw(g, _layout.Trim, TreeToolIcon.Trim, PanelOwner.TrimDepth > 0);
            TreeToolIcons.Draw(g, _layout.Simplify, TreeToolIcon.Simplify, PanelOwner.SimplifyTree);
            TreeToolIcons.Draw(g, _layout.Flatten, TreeToolIcon.Flatten, PanelOwner.FlattenTree);
            TreeToolIcons.Draw(g, _layout.Reverse, TreeToolIcon.Reverse, PanelOwner.ReverseTree);
            TreeToolIcons.Draw(g, _layout.CreateSet, TreeToolIcon.CreateSet, PanelOwner.CreateSetTree);
            string unitLabel = PanelOwner.UnitConversion == PanelUnitConversion.None ? "Units" : PanelUnits.Label(PanelOwner.UnitConversion).Replace(" → ", "→");
            string precision = (PanelOwner.RoundUpFactor > 0 ? "↑" + PanelUnits.FormatFactor(PanelOwner.RoundUpFactor, "G4") : "") +
                (PanelOwner.DecimalPlaces < 0 ? "" : (PanelOwner.RoundUpFactor > 0 ? " · " : "") + PanelOwner.DecimalPlaces + "dp");
            DropdownControl(g, _layout.Units, precision.Length == 0 ? unitLabel : unitLabel == "Units" ? precision : unitLabel + " · " + precision, small, trim,
                PanelOwner.UnitConversion != PanelUnitConversion.None || PanelOwner.DecimalPlaces >= 0 || PanelOwner.RoundUpFactor > 0);
        }

        private void DrawOutputControl(Graphics g, Font small, StringFormat trim)
        {
            var bounds = RectangleF.Union(_layout.OutItems, _layout.OutValues);
            bounds.Height -= 1;
            const float corner = 6;
            using var shape = new GraphicsPath();
            shape.AddArc(bounds.X, bounds.Y, corner, corner, 180, 90);
            shape.AddArc(bounds.Right - corner, bounds.Y, corner, corner, 270, 90);
            shape.AddArc(bounds.Right - corner, bounds.Bottom - corner, corner, corner, 0, 90);
            shape.AddArc(bounds.X, bounds.Bottom - corner, corner, corner, 90, 90);
            shape.CloseFigure();
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.SetClip(shape, CombineMode.Intersect);
            Fill(g, PanelTheme.Rgb(250, 251, 249), bounds);
            Fill(g, PanelTheme.Rgb(227, 237, 223), PanelOwner.OutputsValues ? _layout.OutValues : _layout.OutItems);
            g.Restore(state);
            using var border = new Pen(PanelTheme.Rgb(210, 223, 203));
            g.DrawPath(border, shape);
            g.DrawLine(border, _layout.OutValues.Left, bounds.Y + 1, _layout.OutValues.Left, bounds.Bottom - 1);
            Text(g, "Out", small, Muted, _layout.OutLabel, trim);
            using var centered = (StringFormat)trim.Clone();
            centered.Alignment = StringAlignment.Center;
            Text(g, "Items", small, PanelOwner.OutputsValues ? Muted : Accent, _layout.OutItems, centered);
            Text(g, "Values", small, PanelOwner.OutputsValues ? Accent : Muted, _layout.OutValues, centered);

        }

        private string FooterStatus(PanelQueryResult result, bool singleLine)
        {
            var session = PanelOwner.Session;
            int selected = PanelOwner.Locked || !session.HasCurrentResult ? 0 : DisplayListLengths ? _selection.BranchPaths(result).Count : _selection.Count;
            string output = OutputLabel(result);
            return PanelOwner.Locked ? "Disabled" : session.Error != null ? "Query failed: " + Short(session.Error, 150) : session.IsBusy ? "Recomputing…" :
                _copyPending || DateTime.UtcNow < _copiedUntil ? _actionStatus :
                !string.IsNullOrEmpty(PanelOwner.PreviewStatus) ? PanelOwner.PreviewStatus :
                selected > 0 ? System.FormattableString.Invariant($"{selected:N0} selected") + (DisplayListLengths ? " branches" : " · right-click to copy") :
                singleLine ? CanvasStatistics.Summary(result) + " · " + output :
                DisplayListLengths ? System.FormattableString.Invariant($"{(Count == 0 ? 0 : _d.Offset + 1):N0}–{Math.Min(Count, _d.Offset + _d.Shown):N0}/{Count:N0} branches · {output}") :
                _firstShownItem < 0 ? System.FormattableString.Invariant($"{result.RowPositions.Count:N0} items · {output}") :
                System.FormattableString.Invariant($"{_firstShownItem + 1:N0}–{_lastShownItem + 1:N0}/{result.RowPositions.Count:N0} · {output}");
        }

        private RectangleF Thumb(bool paired)
        {
            var pane = paired ? _f : _d;
            var track = paired || PanelOwner.PanelSplit && _scrollLocked ? _f.Track : pane.Track;
            int shown = pane.Shown, offset = pane.Offset;
            float minimum = Math.Min(20, track.Height / 2);
            float height = Math.Min(track.Height, Math.Max(minimum, track.Height * Math.Max(1, shown) / Math.Max(1, Count)));
            int maximum = MaximumOffset;
            float y = track.Y + (track.Height - height) * Math.Min(offset, maximum) / Math.Max(1, maximum);
            return new RectangleF(track.X, y, track.Width, height);
        }

        private void SetOffset(GH_Canvas canvas, int value) => SetPaneOffset(canvas, value, false);

        private void SetPaneOffset(GH_Canvas canvas, int value, bool paired)
        {
            int offset = Math.Max(0, Math.Min(MaximumOffset, value));
            if (_scrollLocked) _d.Offset = _f.Offset = offset;
            else (paired ? _f : _d).Offset = offset;
            InvalidatePaintedHits();
            canvas.Invalidate();
        }

        private void LayoutMenu() => _menu.Layout(_layout.Panel, _layout.Type, _layout.Sort, _layout.Branch, _layout.Units, _layout.Search, _layout.Preview, _layout.Statistics);

        private void RenderMenu(Graphics g, Font small, Font mono, StringFormat trim)
        {
            LayoutMenu();
            _menu.BeginPaint(_preservePaintedTargets);
            Fill(g, PanelTheme.Shadow, new RectangleF(_menu.Bounds.X + 1, _menu.Bounds.Y + 2, _menu.Bounds.Width, _menu.Bounds.Height));
            Fill(g, PanelTheme.Background, _menu.Bounds);
            int visible = _menu.Visible;
            var options = PanelOwner.Session.Options;
            for (int i = 0; i < visible && i + _menu.Offset < _menu.Count; i++)
            {
                int index = i + _menu.Offset;
                var row = new RectangleF(_menu.Bounds.X + 1, _menu.Bounds.Y + i * 20, _menu.Bounds.Width - 2, 20);
                bool selected = _menu.IsSelected(index, options, PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.PreviewMode, PanelOwner.ShowItemOrder, PanelOwner.RoundUpFactor);
                if (index == _menu.HoveredIndex || selected)
                    Fill(g, index == _menu.HoveredIndex ? PanelTheme.Rgb(226, 236, 226) : PanelTheme.Rgb(242, 247, 241), row);
                if (_menu.IsSectionStart(index))
                    using (var divider = new Pen(PanelTheme.Rgb(225, 231, 224)))
                        g.DrawLine(divider, row.X + 7, row.Y, row.Right - 7, row.Y);
                if (selected)
                    using (var check = new Pen(Accent, 1.3f))
                        g.DrawLines(check, new[] { new PointF(row.X + 7, row.Y + 10), new PointF(row.X + 10, row.Y + 13), new PointF(row.X + 16, row.Y + 7) });
                float inset = _menu.Kind == CanvasMenuKind.Statistics ? 7 : 23;
                Text(g, _menu.Label(index, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor), _menu.Kind == CanvasMenuKind.Branch ? mono : small,
                    selected ? Accent : Ink, new RectangleF(row.X + inset, row.Y, row.Width - inset - 7, 20), trim);
                var target = new RectangleF(_menu.Bounds.X, row.Y, _menu.Bounds.Width, row.Height);
                if (TargetPainted(g, target)) _menu.MarkRowPainted(i);
            }
            if (_menu.Count > visible)
            {
                float y = _menu.Bounds.Bottom - 20;
                Fill(g, PanelTheme.Rgb(235, 241, 232), new RectangleF(_menu.Bounds.X, y, _menu.Bounds.Width, 20));
                Text(g, "‹ Previous", small, Accent, new RectangleF(_menu.Bounds.X + 7, y, _menu.Bounds.Width / 2 - 7, 20), trim);
                Text(g, "Next ›", small, Accent, new RectangleF(_menu.Bounds.X + _menu.Bounds.Width / 2, y, _menu.Bounds.Width / 2 - 7, 20), trim);
                if (TargetPainted(g, new RectangleF(_menu.Bounds.X, y, _menu.Bounds.Width / 2, 20))) _menu.MarkPagePainted(true);
                if (TargetPainted(g, new RectangleF(_menu.Bounds.X + _menu.Bounds.Width / 2, y, _menu.Bounds.Width / 2, 20))) _menu.MarkPagePainted(false);
            }
            using (var pen = new Pen(PanelTheme.Rgb(194, 205, 194))) g.DrawRectangle(pen, _menu.Bounds.X, _menu.Bounds.Y, _menu.Bounds.Width, _menu.Bounds.Height);
        }

        private void OpenMenu(CanvasMenuKind kind, GH_Canvas canvas)
        {
            CloseRowMenu();
            var session = PanelOwner.Session;
            if (kind == CanvasMenuKind.Statistics && (!session.HasCurrentResult || PanelOwner.Locked)) return;
            var modes = kind == CanvasMenuKind.SearchMode
                ? Enum.GetValues(typeof(PanelSearchMode)).Cast<PanelSearchMode>().Where(session.Snapshot.SupportsSearchMode)
                : null;
            _menu.Toggle(kind, session.Snapshot, session.Options, session.Result, modes, _editingSearchMode);
            InvalidatePaintedHits();
            LayoutMenu();
            canvas.Invalidate();
        }

        private bool ResultIsCurrent(PanelQueryResult result) =>
            ReferenceEquals(_lastResult, result) && ReferenceEquals(result, PanelOwner.Session.Result) &&
            !PanelOwner.Locked && PanelOwner.Session.HasCurrentResult;

        private bool PaintedViewIsCurrent()
        {
            var canvas = _paintedCanvas;
            if (canvas == null || canvas.IsDisposed) return false;
            if (!ReferenceEquals(canvas.Document, _paintedDocument) || canvas.Viewport.Zoom != _paintedZoom || canvas.Viewport.Zoom < 0.5f ||
                !canvas.Viewport.VisibleRegion.Equals(_paintedVisibleRegion) || !_layout.Panel.Equals(_paintedPanel) ||
                !ListBounds.Equals(_paintedList) || !_setDetails.Bounds.Equals(_paintedDetails) ||
                !Pivot.Equals(_paintedPivot) || _d.Offset != _d.PaintedOffset || _f.Offset != _f.PaintedOffset || PanelOwner.PanelMinimized)
            {
                InvalidatePaintedHits();
                return false;
            }
            return true;
        }

        private bool RowsAreCurrent(PanelQueryResult result) => PaintedViewIsCurrent() &&
            ReferenceEquals(_paintedResult, result) && ResultIsCurrent(result);

        private bool TargetPainted(Graphics graphics, RectangleF bounds)
        {
            // Offscreen pixels need no paint, but the entire visible portion must
            // be covered by the graphics clip before publishing an interaction.
            var visible = RectangleF.Intersect(bounds, _renderVisibleRegion);
            return visible.Width > 0 && visible.Height > 0 && FullyVisible(graphics, visible);
        }

        private void AddBranchHit(RectangleF bounds, string path, RectangleF copyBounds = default)
        {
            int index = _branchHits.FindIndex(hit => hit.Bounds == bounds && hit.Path == path);
            var target = new BranchHit(bounds, path, copyBounds);
            if (index < 0) _branchHits.Add(target); else _branchHits[index] = target;
        }

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
            if (e.Button == MouseButtons.Left && _layout.Panel.Contains(e.CanvasLocation))
            {
                if (_layout.Reset.Contains(e.CanvasLocation)) CanvasTextInput.CloseActive(PanelOwner);
                CanvasTextInput.CompletePending();
                CancelQueryEditor();
            }
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) _keyboardCursor = -1;
            if (_paintedCanvas != null && !ReferenceEquals(_paintedCanvas, sender)) InvalidatePaintedHits();
            if (PanelOwner.PanelMinimized)
            {
                if (e.Button == MouseButtons.Left && _layout.Restore.Contains(e.CanvasLocation))
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
            // Clicking into the panel focuses it; the move grip and header stay
            // ordinary component gestures, so Ctrl+C there copies the component.
            // The 12-unit edge strips belong to wire grips, which stay wire gestures.
            if ((e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) &&
                (_layout.Panel.Contains(e.CanvasLocation) || _setDetails.Bounds.Contains(e.CanvasLocation)))
            {
                if (_layout.Move.Contains(e.CanvasLocation)) ReleaseKeyboardFocus();
                else if (_setDetails.Bounds.Contains(e.CanvasLocation) ||
                    e.CanvasLocation.X > _layout.Panel.Left + 12 && e.CanvasLocation.X < _layout.Panel.Right - 12) FocusPanel(sender);
            }
            if (_menu.Kind == CanvasMenuKind.None && _rowMenuResult == null && !RowsAreCurrent(PanelOwner.Session.Result) &&
                (ListContains(e.CanvasLocation) || _setDetails.Bounds.Contains(e.CanvasLocation) ||
                 _staleList.Contains(e.CanvasLocation) || _staleDetails.Contains(e.CanvasLocation) || _staleRowMenu.Contains(e.CanvasLocation)))
            {
                if (e.Button == MouseButtons.Right) _rowContextHandled = true;
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            if (e.Button == MouseButtons.Right && _setDetails.Bounds.Contains(e.CanvasLocation))
            {
                _rowContextHandled = true;
                return GH_ObjectResponse.Handled;
            }
            // A click on an open menu, or a suppressed trailing click, belongs to the
            // gesture it finishes, not to the pane underneath it.
            void TrackKeyboardPane()
            {
                if (_menu.Kind == CanvasMenuKind.None && _rowMenuResult == null &&
                    (ListContains(e.CanvasLocation) || _d.TrackHit.Contains(e.CanvasLocation) || _f.TrackHit.Contains(e.CanvasLocation)))
                    _keyboardPaired = _f.List.Contains(e.CanvasLocation) || _f.TrackHit.Contains(e.CanvasLocation);
            }
            if (e.Button == MouseButtons.Right) { TrackKeyboardPane(); return RespondToRowContextMenu(sender, e); }
            if (e.Button != MouseButtons.Left) return base.RespondToMouseDown(sender, e);
            var point = e.CanvasLocation;
            // A fresh gesture may start elsewhere before the prior menu's
            // double-click interval expires. Only its trailing click is blocked.
            if (e.Clicks == 1) _rowActionDismissedUntil = default;
            if (e.Clicks > 1 && DateTime.UtcNow < _rowActionDismissedUntil) return GH_ObjectResponse.Handled;
            TrackKeyboardPane();
            if (_rowMenuResult != null)
            {
                var menuResult = _rowMenuResult;
                if (!_rowMenuBounds.Contains(point))
                {
                    CloseRowMenu();
                    SuppressTrailingDoubleClick();
                    InvalidatePaintedHits();
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
                if (!_rowMenuPainted || !RowsAreCurrent(menuResult) || !RowMenuTargetPainted(point))
                {
                    // An unpainted action consumes the gesture and dismisses the
                    // menu, as it did before per-target clipping was tracked.
                    CloseRowMenu();
                    SuppressTrailingDoubleClick();
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
                int index = _rowMenuBounds.Contains(point) ? RowMenuIndex(point) : -1;
                // Capture identity before closing releases the menu choices.
                PanelRowAction? action = index >= 0 && index < _rowMenuActions.Length ? _rowMenuActions[index].Action : (PanelRowAction?)null;
                string? disabled = index >= 0 && index < _rowMenuActions.Length ? _rowMenuActions[index].DisabledReason : null;
                if (disabled != null) return GH_ObjectResponse.Handled; // Greyed-out actions keep the menu open.
                bool current = RowsAreCurrent(menuResult);
                CloseRowMenu();
                SuppressTrailingDoubleClick();
                InvalidatePaintedHits();
                if (current && action.HasValue) ChooseRowAction(action.Value, menuResult, sender);
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            if (_menu.Kind != CanvasMenuKind.None)
            {
                if (!_menu.Bounds.Contains(point))
                {
                    _menu.Close();
                    InvalidatePaintedHits();
                    SuppressTrailingDoubleClick();
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
                if (!PaintedViewIsCurrent() || !_menu.IsCurrent(PanelOwner.Session.Snapshot) || _menu.Kind == CanvasMenuKind.Statistics && !StatisticsAreCurrent())
                {
                    // Keep a newly opened or paged menu alive until it is painted.
                    if (!_menu.IsCurrent(PanelOwner.Session.Snapshot) || _menu.Kind == CanvasMenuKind.Statistics && !StatisticsAreCurrent())
                    { _menu.Close(); SuppressTrailingDoubleClick(); }
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
                if (_menu.TrySelect(point, out var index)) ChooseMenu(index, sender);
                if (_menu.Kind == CanvasMenuKind.None)
                {
                    InvalidatePaintedHits();
                    SuppressTrailingDoubleClick();
                }
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            if (_layout.Close.Contains(point))
            {
                SuppressTrailingDoubleClick();
                PanelOwner.SetPanelMinimized(true);
                PerformLayout();
                CanvasDoubleClickGuard.Start(sender, e);
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            if (_layout.Display.Contains(point))
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
            if (HandleSplitClick(sender, e)) return GH_ObjectResponse.Handled;
            if (_layout.Reset.Contains(point))
            {
                bool recordedUndo = PanelOwner.ResetAll();
                ShowNotice(sender, recordedUndo ? "Reset all · Undo restores the previous settings" : "Reset all");
                return GH_ObjectResponse.Handled;
            }
            if (HandleSetDetailsClick(sender, e)) return GH_ObjectResponse.Handled;
            if (_layout.Resize.Contains(point))
            {
                PanelOwner.RecordUndoEvent("Resize Panel+");
                _resizing = true; _dragStart = point; _startWidth = _layout.Panel.Width; _startHeight = _layout.Panel.Height;
                return GH_ObjectResponse.Capture;
            }
            if ((_d.TrackHit.Contains(point) && Count > _d.Shown) ||
                (_f.TrackHit.Contains(point) && Count > (_scrollLocked ? _d.Shown : _f.Shown) && (_scrollLocked || PanelOwner.CanSplit)))
            {
                _dragPaired = _f.TrackHit.Contains(point) && !_scrollLocked;
                var thumb = Thumb(_dragPaired);
                if (point.Y >= thumb.Top && point.Y <= thumb.Bottom) { (_dragPaired ? _f : _d).Scrolling = true; _thumbGrab = point.Y - thumb.Y; return GH_ObjectResponse.Capture; }
                var pane = _dragPaired ? _f : _d;
                int offset = pane.Offset, shown = pane.Shown;
                SetPaneOffset(sender, offset + (point.Y < thumb.Y ? -Math.Max(1, shown) : Math.Max(1, shown)), _dragPaired);
                return GH_ObjectResponse.Handled;
            }
            if (PanelOwner.CanOutputValues && (_layout.OutItems.Contains(point) || _layout.OutValues.Contains(point)))
            {
                PanelOwner.SetOutputValues(_layout.OutValues.Contains(point) ? PanelOutputValues.Values : PanelOutputValues.Items);
                return GH_ObjectResponse.Handled;
            }
            if (_layout.Trim.Contains(point))
            {
                CanvasTextInput.Show(sender, PanelOwner, _layout.Trim, PanelOwner.TrimDepth.ToString(), text =>
                {
                    if (int.TryParse(text.Trim(), out var depth) && depth >= 0)
                        PanelOwner.SetTreeOperations(depth, PanelOwner.FlattenTree, PanelOwner.ReverseTree, PanelOwner.SimplifyTree, PanelOwner.CreateSetTree);
                    else ShowNotice(sender, "Trim depth must be a whole number, 0 or more (0 turns Trim off)");
                }, valid: text => int.TryParse(text.Trim(), out int depth) && depth >= 0);
                return GH_ObjectResponse.Handled;
            }
            if (_layout.Simplify.Contains(point) || _layout.Flatten.Contains(point) || _layout.Reverse.Contains(point) || _layout.CreateSet.Contains(point))
            {
                PanelOwner.SetTreeOperations(PanelOwner.TrimDepth,
                    _layout.Flatten.Contains(point) ? !PanelOwner.FlattenTree : PanelOwner.FlattenTree,
                    _layout.Reverse.Contains(point) ? !PanelOwner.ReverseTree : PanelOwner.ReverseTree,
                    _layout.Simplify.Contains(point) ? !PanelOwner.SimplifyTree : PanelOwner.SimplifyTree,
                    _layout.CreateSet.Contains(point) ? !PanelOwner.CreateSetTree : PanelOwner.CreateSetTree);
                return GH_ObjectResponse.Handled;
            }
            if (_layout.SelectAll.Contains(point))
            {
                var result = PanelOwner.Session.Result;
                if (RowsAreCurrent(result)) SelectAllRows(sender, result);
                return GH_ObjectResponse.Handled;
            }
            if (_layout.Preview.Contains(point)) { OpenMenu(CanvasMenuKind.Preview, sender); return GH_ObjectResponse.Handled; }
            if (_layout.Statistics.Contains(point)) { OpenMenu(CanvasMenuKind.Statistics, sender); return GH_ObjectResponse.Handled; }
            if (SearchClearVisible && _layout.SearchClear.Contains(point)) { ClearSearch(sender); return GH_ObjectResponse.Handled; }
            if (BranchClearVisible && _layout.BranchClear.Contains(point)) { SetBranchFilter(null); sender.Invalidate(); return GH_ObjectResponse.Handled; }
            if (_layout.Filter.Contains(point)) { OpenMenu(CanvasMenuKind.SearchMode, sender); return GH_ObjectResponse.Handled; }
            if (_layout.Search.Contains(point)) { EditSearch(sender); return GH_ObjectResponse.Handled; }
            if (_layout.Units.Contains(point)) { OpenMenu(CanvasMenuKind.Units, sender); return GH_ObjectResponse.Handled; }
            if (_layout.Type.Contains(point)) { OpenMenu(CanvasMenuKind.Type, sender); return GH_ObjectResponse.Handled; }
            if (_layout.Sort.Contains(point)) { OpenMenu(CanvasMenuKind.Sort, sender); return GH_ObjectResponse.Handled; }
            if (_layout.Branch.Contains(point)) { OpenMenu(CanvasMenuKind.Branch, sender); return GH_ObjectResponse.Handled; }
            if (_layout.Jump.Contains(point))
            {
                var jumpResult = PanelOwner.Session.Result;
                bool branchView = DisplayListLengths;
                int firstItem = branchView ? _d.Offset : ViewItemAtOrAfter(jumpResult, _d.Offset);
                CanvasTextInput.Show(sender, PanelOwner, _layout.Jump, (Math.Min(firstItem, Math.Max(0, (branchView ? jumpResult.BranchPaths.Count : jumpResult.RowPositions.Count) - 1)) + 1).ToString(), text =>
                {
                    if (!ResultIsCurrent(jumpResult) || DisplayListLengths != branchView) return;
                    int itemCount = branchView ? jumpResult.BranchPaths.Count : jumpResult.RowPositions.Count;
                    if (!int.TryParse(text.Trim(), out var row))
                    {
                        ShowNotice(sender, System.FormattableString.Invariant($"Enter a row number from 1 to {Math.Max(1, itemCount):N0}"));
                        return;
                    }
                    // Put the requested item first; grouped branch headings can change
                    // page capacity, so previous-page size must not hide the final item.
                    int index = Math.Max(0, Math.Min(Math.Max(0, itemCount - 1), row <= 1 ? 0 : row - 1));
                    int offset = itemCount == 0 ? 0 : branchView ? index : ViewOffset(jumpResult, index);
                    bool changed = _d.Offset != offset;
                    _d.Offset = offset;
                    if (itemCount > 0 && (row < 1 || row > itemCount))
                        ShowNotice(sender, System.FormattableString.Invariant($"Row {row:N0} is out of range · showing row {index + 1:N0} of {itemCount:N0}"));
                    if (changed) InvalidatePaintedHits();
                    sender.Invalidate();
                }, valid: text => int.TryParse(text.Trim(), out _));
                return GH_ObjectResponse.Handled;
            }
            // Moving the panel is a presentation control, so keep its row selection.
            if (_layout.Move.Contains(point)) return base.RespondToMouseDown(sender, e);
            var currentResult = PanelOwner.Session.Result;
            if (ListContains(point) && !RowsAreCurrent(currentResult))
            {
                _rowHits.Clear();
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            if (TryWarningClick(point, sender)) return GH_ObjectResponse.Handled;
            foreach (var hit in _branchHits)
                if (hit.Bounds.Contains(point))
                {
                    if (hit.CopyBounds.Contains(point))
                    {
                        CopySelectedValues(currentResult, sender, _f.List.Contains(point), PanelRowAction.CopyPath, hit.Path);
                        return GH_ObjectResponse.Handled;
                    }
                    InvalidateCopyStatus();
                    // Only an accepted row or heading selection chooses which pane Ctrl+C copies.
                    _copyPaired = _f.List.Contains(point);
                    _selection.SelectAll(currentResult, hit.Path, includeEmptyBranches: DisplayListLengths);
                    RevealPartner(sender, -1, hit.Path, point);
                    PanelOwner.HighlightRows(currentResult.Snapshot, _selection.OrderedPositions(currentResult));
                    TrackSelectionCanvas(sender);
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
            foreach (var hit in _rowHits)
                if (hit.Bounds.Contains(point) && hit.SourcePosition >= 0 && hit.SourcePosition < currentResult.Snapshot.Rows.Count)
                {
                    _copyPaired = _f.List.Contains(point);
                    RevealPartner(sender, hit.SourcePosition, null, point);
                    var modifiers = RowModifiers();
                    if (modifiers.AcrossBranches)
                    {
                        SelectAcrossBranches(currentResult, new[] { currentResult.DisplaySet?.MemberIndices.TryGetValue(hit.SourcePosition, out int k) == true ? k : currentResult.Snapshot.Rows[hit.SourcePosition].OriginalIndex }, sender);
                        sender.Invalidate();
                        return GH_ObjectResponse.Handled;
                    }
                    InvalidateCopyStatus();
                    _selection.SelectAt(currentResult, hit.SourcePosition, hit.ViewNumber - 1, modifiers.Toggle, modifiers.Range, preserveSelected: true);
                    var positions = _selection.OrderedPositions(currentResult);
                    PanelOwner.HighlightRows(currentResult.Snapshot, positions);
                    TrackSelectionCanvas(sender);
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
            if (ListContains(point))
            {
                // Empty panel space retains selection.
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            // The host selects and drags the component; empty space keeps rows.
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
            var selectedSearch = kind == CanvasMenuKind.SearchMode ? _menu.SearchModeAt(index) : (PanelSearchMode?)null;
            var selectedSort = kind == CanvasMenuKind.Sort ? _menu.SortModeAt(index) : (PanelSortMode?)null;
            var selectedPreview = kind == CanvasMenuKind.Preview ? _menu.PreviewChoiceAt(index) : (CanvasPreviewChoice?)null;
            var selectedUnit = kind == CanvasMenuKind.Units ? _menu.UnitChoiceAt(index) : (CanvasUnitChoice?)null;
            var selectedComparison = kind == CanvasMenuKind.Comparison ? _menu.ComparisonChoiceAt(index) : (CanvasComparisonChoice?)null;
            var branchFilter = kind == CanvasMenuKind.Branch ? _menu.BranchFilter : string.Empty;
            _menu.Close();
            if (kind == CanvasMenuKind.Preview)
            {
                if (selectedPreview!.Value.IsItemOrder) PanelOwner.SetShowItemOrder(!PanelOwner.ShowItemOrder);
                else PanelOwner.SetPreviewMode(selectedPreview.Value.Mode!.Value);
                canvas.Invalidate();
                return;
            }
            if (kind == CanvasMenuKind.Units)
            {
                if (selectedUnit!.Value.Conversion.HasValue)
                    PanelOwner.SetUnits(selectedUnit.Value.Conversion!.Value, PanelOwner.DecimalPlaces);
                else if (selectedUnit.Value.Setting == CanvasNumberSetting.Automatic)
                    PanelOwner.SetUnits(PanelOwner.UnitConversion, PanelUnits.AutomaticDecimals);
                else if (selectedUnit.Value.Setting == CanvasNumberSetting.RoundingOff)
                    PanelOwner.SetUnits(PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, 0);
                else if (selectedUnit.Value.Setting == CanvasNumberSetting.RoundingFactor)
                    CanvasTextInput.Show(canvas, PanelOwner, _layout.Units,
                        PanelUnits.FormatFactor(PanelOwner.RoundUpFactor > 0 ? PanelOwner.RoundUpFactor : 1, "R"), text =>
                    {
                        if (double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double factor) &&
                            PanelUnits.IsRoundingFactor(factor))
                            PanelOwner.SetUnits(PanelOwner.UnitConversion, PanelOwner.DecimalPlaces, factor);
                        else ShowNotice(canvas, "Rounding factor must be a positive number, using . for decimals (for example 5 or 0.25)");
                    }, valid: text => double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double factor) && PanelUnits.IsRoundingFactor(factor));
                else CanvasTextInput.Show(canvas, PanelOwner, _layout.Units, Math.Max(0, PanelOwner.DecimalPlaces).ToString(), text =>
                {
                    if (int.TryParse(text.Trim(), out var decimals) && decimals >= 0 && decimals <= PanelUnits.MaximumDecimals)
                        PanelOwner.SetUnits(PanelOwner.UnitConversion, decimals);
                    else ShowNotice(canvas, $"Decimal places must be a whole number from 0 to {PanelUnits.MaximumDecimals}");
                }, valid: text => int.TryParse(text.Trim(), out int decimals) && decimals >= 0 && decimals <= PanelUnits.MaximumDecimals);
                canvas.Invalidate();
                return;
            }
            var options = PanelOwner.Session.Options;
            var oldOptions = options.Clone();
            if (kind == CanvasMenuKind.SearchMode)
            {
                _editingSearchMode = selectedSearch!.Value;
                if (CanvasMenu.IsComparisonMode(_editingSearchMode))
                {
                    if (options.SearchMode == _editingSearchMode && (options.DisplayFilterValue || !string.IsNullOrEmpty(options.Search)))
                    { EditSearch(canvas); return; }
                    SetSearch(canvas, _editingSearchMode, string.Empty, true);
                    EditSearch(canvas);
                }
                else EditSearch(canvas, _editingSearchMode, options.SearchMode == _editingSearchMode ? options.Search : string.Empty);
                return;
            }
            if (kind == CanvasMenuKind.Comparison)
            {
                if (selectedComparison!.Value.IsDisplayValue)
                {
                    SetSearch(canvas, _editingSearchMode, string.Empty, true);
                    return;
                }
                if (selectedComparison.Value.IsValue)
                {
                    SetSearch(canvas, _editingSearchMode, "= " + selectedComparison.Value.Value, false);
                    return;
                }
                return;
            }
            if (kind == CanvasMenuKind.Type)
            {
                if (index == 1) options.OnlyInvalid = !options.OnlyInvalid;
                else { options.TypeName = selectedType; if (index == 0) options.OnlyInvalid = false; }
            }
            else if (kind == CanvasMenuKind.Sort) options.SortMode = selectedSort!.Value;
            else if (index == 1)
            {
                // Digits typed into the open menu jump straight to that path.
                if (branchFilter.Length > 0) SetBranchFilter(branchFilter);
                else ShowQueryEditor(canvas, _layout.Branch, options.BranchPath ?? "{0}", SetBranchFilter, valid: text =>
                {
                    var query = PanelOwner.Session.Options;
                    query.BranchPath = text;
                    return PanelQueryEngine.IsValidSearch(query);
                });
                canvas.Invalidate();
                return;
            }
            else
            {
                SetBranchFilter(selectedPath);
                canvas.Invalidate();
                return;
            }
            if (PanelQueryPolicy.SameQuery(oldOptions, options)) { canvas.Invalidate(); return; }
            PanelOwner.RecordUndoEvent(kind == CanvasMenuKind.Sort ? "Panel sort" : "Panel type filter");
            PanelOwner.Session.UpdateQuery(options);
            PanelOwner.OnPingDocument()?.Modified();
            canvas.Invalidate();
        }

        private void SetSearch(GH_Canvas canvas, PanelSearchMode mode, string search, bool showValues)
        {
            var options = PanelOwner.Session.Options;
            var before = options.Clone();
            options.SearchMode = mode;
            options.Search = search;
            options.DisplayFilterValue = showValues;
            if (PanelQueryPolicy.SameQuery(before, options)) { canvas.Invalidate(); return; }
            PanelOwner.RecordQueryUndo(showValues ? "Show Panel+ values" : "Panel+ filter", before, options);
            _d.Offset = _f.Offset = 0;
            PanelOwner.Session.UpdateQuery(options);
            canvas.Invalidate();
        }

        private void EditSearch(GH_Canvas canvas)
        {
            var options = PanelOwner.Session.Options;
            if (CanvasMenu.HasValueChoices(options.SearchMode))
            {
                _editingSearchMode = options.SearchMode;
                OpenMenu(CanvasMenuKind.Comparison, canvas);
                SuppressTrailingDoubleClick();
            }
            else EditSearch(canvas, options.SearchMode, options.Search);
        }

        private void EditSearch(GH_Canvas canvas, PanelSearchMode mode, string current) =>
            ShowLiveQueryEditor(canvas, current, "Panel search", text =>
            {
                var options = PanelOwner.Session.Options;
                options.DisplayFilterValue = CanvasMenu.IsComparisonMode(mode) && string.IsNullOrWhiteSpace(text);
                options.SearchMode = mode;
                options.Search = options.DisplayFilterValue ? string.Empty : text;
                return options;
            });

        private void ClearSearch(GH_Canvas canvas)
        {
            CancelQueryEditor();
            var options = PanelOwner.Session.Options;
            PanelOwner.RecordUndoEvent("Clear Panel+ search");
            options.SearchMode = PanelSearchMode.Text;
            options.DisplayFilterValue = false;
            options.Search = string.Empty;
            PanelOwner.Session.UpdateQuery(options);
            canvas.Invalidate();
        }

        // Rows follow the search while typing. The preview is view-only: Enter
        // or focus loss commits it as one undo step and publishes outputs; Escape
        // restores the previous query, leaving outputs and undo untouched.
        private void ShowLiveQueryEditor(GH_Canvas canvas, string current, string undoName, Func<string, PanelQueryOptions> build)
        {
            object? token = null;
            ShowQueryEditor(canvas, _layout.Search, current, text => CommitQuery(token, undoName, build(text)),
                text => PreviewQuery(token!, build(text)), () => EndQueryPreview(token), text => PanelQueryEngine.IsValidSearch(build(text)));
            token = _queryEditor;
        }

        private void PreviewQuery(object token, PanelQueryOptions options)
        {
            var session = PanelOwner.Session;
            if (!ReferenceEquals(token, _queryEditor) || !ReferenceEquals(_queryEditorSnapshot, session.Snapshot) ||
                !PanelQueryEngine.IsValidSearch(options) || PanelQueryPolicy.SameQuery(session.Options, options)) return;
            if (_previewOriginal == null)
            {
                _previewOriginal = session.Options;
                _previewOwner = token;
                _d.PreviewOffset = _d.Offset;
                _f.PreviewOffset = _f.Offset;
                PanelOwner.BeginQueryPreview();
            }
            session.UpdateQuery(options);
        }

        private void CommitQuery(object? token, string undoName, PanelQueryOptions options)
        {
            var session = PanelOwner.Session;
            if (_previewOriginal == null || !ReferenceEquals(_previewOwner, token))
            {
                if (PanelQueryPolicy.SameQuery(session.Options, options)) return;
                PanelOwner.RecordQueryUndo(undoName, session.Options, options);
                session.UpdateQuery(options);
                return;
            }
            var original = _previewOriginal;
            if (PanelQueryPolicy.SameQuery(original, options))
            {
                EndQueryPreview(token);
                return;
            }
            PanelOwner.RecordQueryUndo(undoName, original, options);
            ClearPreviewState();
            session.UpdateQuery(options);
            PanelOwner.EndQueryPreview(committed: true);
        }

        private void EndQueryPreview(object? token)
        {
            if (_previewOriginal == null || !ReferenceEquals(_previewOwner, token)) return;
            var original = _previewOriginal;
            _viewRestore = new ViewRestore(PanelOwner.Session.Snapshot, _d.PreviewOffset, _f.PreviewOffset,
                original.DisplayFilterValue && original.SearchMode == PanelSearchMode.ListLength);
            ClearPreviewState();
            PanelOwner.Session.UpdateQuery(original);
            PanelOwner.EndQueryPreview(committed: false);
            Grasshopper.Instances.ActiveCanvas?.Invalidate();
        }

        private void ClearPreviewState()
        {
            _previewOriginal = null;
            _previewOwner = null;
            _d.PreviewOffset = _f.PreviewOffset = 0;
        }

        private void ShowQueryEditor(GH_Canvas canvas, RectangleF bounds, string current, Action<string> accepted,
            Action<string>? changed = null, Action? abandoned = null, Func<string, bool>? valid = null)
        {
            CancelQueryEditor();
            var session = PanelOwner.Session;
            var snapshot = session.Snapshot;
            var generation = _queryEditorGeneration;
            _queryEditorSnapshot = snapshot;
            CanvasTextInput? editor = null;
            editor = CanvasTextInput.Show(canvas, PanelOwner, bounds, current, text =>
            {
                if (generation != _queryEditorGeneration || !ReferenceEquals(_queryEditor, editor) || !CanvasTextInput.IsCurrent(editor))
                {
                    abandoned?.Invoke();
                    return;
                }
                _queryEditor = null;
                _queryEditorSnapshot = null;
                ++_queryEditorGeneration;
                if (PanelOwner.Locked || canvas.IsDisposed || !canvas.Enabled || canvas.Document?.Enabled != true || !ReferenceEquals(canvas.Document, PanelOwner.OnPingDocument()) ||
                    !ReferenceEquals(session, PanelOwner.Session) || !ReferenceEquals(snapshot, session.Snapshot))
                {
                    abandoned?.Invoke();
                    return;
                }
                accepted(text);
                canvas.Invalidate();
            }, changed, abandoned, valid);
            _queryEditor = editor;
        }

        internal void CancelQueryEditor()
        {
            CanvasTextInput.CompletePending();
            ++_queryEditorGeneration;
            EndQueryPreview(_previewOwner);
            CanvasTextInput.Close(_queryEditor);
            _queryEditor = null;
            _queryEditorSnapshot = null;
        }

        public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (_paintedCanvas != null && !ReferenceEquals(_paintedCanvas, sender)) InvalidatePaintedHits();
            if (PanelOwner.PanelMinimized)
            {
                if (sender.Viewport.Zoom >= 0.5f && UpdateHover(sender, e.CanvasLocation))
                {
                    sender.Cursor = Cursors.Hand;
                    return GH_ObjectResponse.Handled;
                }
                return base.RespondToMouseMove(sender, e);
            }
            // Hidden controls must not claim a cursor that their mouse-down path
            // cannot act on. An already captured gesture still needs its moves.
            if (!_resizing && !(_d.Scrolling || _f.Scrolling) && sender.Viewport.Zoom < 0.5f)
                return base.RespondToMouseMove(sender, e);
            bool setControl = SetControlAt(e.CanvasLocation);
            bool hoverControl = UpdateHover(sender, e.CanvasLocation);
            setControl |= hoverControl;
            sender.Cursor = _resizing || _layout.Resize.Contains(e.CanvasLocation) ? Cursors.SizeNWSE :
                _layout.Move.Contains(e.CanvasLocation) ? Cursors.SizeAll : setControl ? Cursors.Hand : Cursors.Default;
            if (PaintedViewIsCurrent() && _menu.Hover(e.CanvasLocation)) sender.Invalidate();
            int rowMenuHovered = _rowMenuResult != null && RowsAreCurrent(_rowMenuResult) && RowMenuTargetPainted(e.CanvasLocation)
                ? RowMenuIndex(e.CanvasLocation) : -1;
            if (rowMenuHovered != _rowMenuHovered) { _rowMenuHovered = rowMenuHovered; sender.Invalidate(); }
            if (_resizing)
            {
                // Half's top offset also changes with the saved height. Account
                // for that so the visible corner follows the cursor exactly.
                float height = _startHeight + (e.CanvasY - _dragStart.Y) /
                    (PanelOwner.CanvasDisplay == CanvasPanelDisplay.Half ? 1.5f : 1);
                PanelOwner.SetCanvasSize((_startWidth + e.CanvasX - _dragStart.X) / (PanelOwner.PanelSplit ? 2 : 1),
                    PanelOwner.CanvasDisplay == CanvasPanelDisplay.Half ? height * 2 : height);
                PerformLayout();
                sender.Invalidate();
                return GH_ObjectResponse.Capture;
            }
            if (_d.Scrolling || _f.Scrolling)
            {
                var track = _dragPaired || PanelOwner.PanelSplit && _scrollLocked ? _f.Track : _d.Track;
                float travel = track.Height - Thumb(_dragPaired).Height;
                if (travel > 0) SetPaneOffset(sender, (int)Math.Round((e.CanvasY - track.Y - _thumbGrab) / travel * MaximumOffset), _dragPaired);
                return GH_ObjectResponse.Capture;
            }
            // Grasshopper replaces the cursor after an ignored move event.
            if (_layout.Move.Contains(e.CanvasLocation) || _layout.Resize.Contains(e.CanvasLocation) || setControl) return GH_ObjectResponse.Handled;
            return base.RespondToMouseMove(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == MouseButtons.Right && _rowContextHandled)
            {
                _rowContextHandled = false;
                return GH_ObjectResponse.Handled;
            }
            if (_resizing || (_d.Scrolling || _f.Scrolling)) { _resizing = false; _d.Scrolling = _f.Scrolling = false; return GH_ObjectResponse.Release; }
            return base.RespondToMouseUp(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button != MouseButtons.Left) return base.RespondToMouseDoubleClick(sender, e);
            if (DateTime.UtcNow < _rowActionDismissedUntil) return GH_ObjectResponse.Handled;
            if (PanelOwner.PanelMinimized || _layout.Close.Contains(e.CanvasLocation) || _layout.Display.Contains(e.CanvasLocation) || _layout.Split.Contains(e.CanvasLocation) || _layout.ScrollLock.Contains(e.CanvasLocation) || _layout.SelectAll.Contains(e.CanvasLocation))
                return GH_ObjectResponse.Handled;
            if (sender.Viewport.Zoom < 0.5f) return base.RespondToMouseDoubleClick(sender, e);
            if (!RowsAreCurrent(PanelOwner.Session.Result) &&
                (ListContains(e.CanvasLocation) || _setDetails.Bounds.Contains(e.CanvasLocation) ||
                 _staleList.Contains(e.CanvasLocation) || _staleDetails.Contains(e.CanvasLocation) || _staleRowMenu.Contains(e.CanvasLocation))) return GH_ObjectResponse.Handled;
            if (_rowMenuResult != null || _menu.Kind != CanvasMenuKind.None) return GH_ObjectResponse.Handled;
            if (_setDetails.Bounds.Contains(e.CanvasLocation) || _setHits.Any(hit => hit.Bounds.Contains(e.CanvasLocation)))
                return GH_ObjectResponse.Handled;
            if (ListContains(e.CanvasLocation))
            {
                if (RowsAreCurrent(PanelOwner.Session.Result) && (_paintedListBackground ||
                    _rowHits.Any(hit => hit.Bounds.Contains(e.CanvasLocation)) || _branchHits.Any(hit => hit.Bounds.Contains(e.CanvasLocation))))
                    ClearRowSelection(sender);
                return GH_ObjectResponse.Handled;
            }
            if (_layout.Filter.Contains(e.CanvasLocation)) return GH_ObjectResponse.Handled;
            if (_layout.Search.Contains(e.CanvasLocation)) { EditSearch(sender); return GH_ObjectResponse.Handled; }
            return base.RespondToMouseDoubleClick(sender, e);
        }

        public override void SetupTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            base.SetupTooltip(point, e);
            if (_menu.Kind == CanvasMenuKind.None && _rowMenuResult == null && SplitTooltip(point, e)) return;
            if (e.Control is GH_Canvas canvas && _paintedCanvas != null && !ReferenceEquals(_paintedCanvas, canvas)) InvalidatePaintedHits();
            if (_menu.Kind != CanvasMenuKind.None && _menu.Bounds.Contains(point) &&
                (!PaintedViewIsCurrent() || !_menu.IsPaintedAt(point))) return;
            if (!PanelOwner.PanelMinimized && _menu.Kind == CanvasMenuKind.None && _rowMenuResult == null && !RowsAreCurrent(PanelOwner.Session.Result) &&
                (ListContains(point) || _setDetails.Bounds.Contains(point) || _staleList.Contains(point) || _staleDetails.Contains(point) || _staleRowMenu.Contains(point))) return;
            if (_rowMenuResult != null && _rowMenuBounds.Contains(point) &&
                (!RowsAreCurrent(_rowMenuResult) || !RowMenuTargetPainted(point))) return;
            if (!PanelOwner.PanelMinimized && _menu.Kind == CanvasMenuKind.None && _rowMenuResult == null && SetDetailsTooltip(point, e)) return;
            if (_menu.Kind == CanvasMenuKind.None && _rowMenuResult == null && _setDetails.Bounds.Contains(point)) return;
            if (_menu.Kind == CanvasMenuKind.Preview && _menu.Bounds.Contains(point))
            {
                int row = (int)((point.Y - _menu.Bounds.Y) / 20);
                if (row >= 0 && row < _menu.Visible && _menu.Offset + row < _menu.Count && _menu.PreviewChoiceAt(_menu.Offset + row).IsItemOrder)
                {
                    e.Title = "Show item order";
                    e.Text = PanelOwner.ShowItemOrder ? "On" : "Off";
                    e.Description = CanvasMenu.ItemOrderDescription;
                    return;
                }
            }
            if (PanelOwner.PanelMinimized)
            {
                e.Title = _layout.Restore.Contains(point) ? "Restore panel" : "Panel+ capsule";
                e.Text = CapsuleSummary();
                e.Description = "Click the window icon to reopen the " + PanelOwner.CanvasDisplay + " panel exactly as you left it.";
                return;
            }
            if (_layout.Close.Contains(point))
            {
                e.Title = "Minimize to capsule";
                e.Text = "Nothing is deleted";
                e.Description = "Shrink to a compact summary on the canvas. Restore reopens the same view.";
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
                    e.Description = index == 6 || index == 7 || index >= CanvasStatistics.OverviewRows ? CanvasStatistics.UniqueScope :
                        index == 8 ? "Source counts after filters are measured before Create set removes duplicate occurrences. Valid, invalid and unverified are distinct." : CanvasStatistics.Scope;
                }
                else e.Description = "Use Previous and Next to inspect all values.";
                return;
            }
            if (_layout.Move.Contains(point))
            {
                e.Title = "Move Panel+";
                e.Text = "Drag this header";
                e.Description = "Drag to move the component. Clicks here are ordinary Grasshopper gestures, so Ctrl+C copies the component.";
                return;
            }
            if (RowActionTooltip(point, e)) return;
            if (_layout.Display.Contains(point))
            {
                e.Title = PanelOwner.CanvasDisplay == CanvasPanelDisplay.Full ? "Show half panel" : "Show full panel";
                e.Text = "Full ↔ Half";
                e.Description = "Toggle between your saved height and half of it. Wires stay connected.";
                return;
            }
            if (_layout.SelectAll.Contains(point))
            {
                e.Title = "Select all rows";
                e.Text = !PanelOwner.Locked && PanelOwner.Session.HasCurrentResult
                    ? System.FormattableString.Invariant($"{PanelOwner.Session.Result.RowPositions.Count:N0} results")
                    : PanelOwner.Locked ? "Disabled" : PanelOwner.Session.IsBusy ? "Recomputing…" : "Selection unavailable";
                e.Description = "Select every result, including offscreen rows. Shortcut while focused: Ctrl+A.";
                return;
            }
            if (_layout.Statistics.Contains(point) || _layout.Footer.Contains(point) && point.Y < _layout.Units.Y &&
                !_layout.OutLabel.Contains(point) && !_layout.OutItems.Contains(point) && !_layout.OutValues.Contains(point))
            {
                e.Title = "Tree statistics";
                e.Text = !PanelOwner.Locked && PanelOwner.Session.HasCurrentResult
                    ? CanvasStatistics.Summary(PanelOwner.Session.Result)
                    : PanelOwner.Locked ? "Disabled" : PanelOwner.Session.IsBusy ? "Recomputing…" : "Statistics unavailable";
                e.Description = "Minimum, maximum, average, counts and every unique value with its frequency.";
                return;
            }
            if (SearchClearVisible && _layout.SearchClear.Contains(point))
            {
                e.Title = "Clear search";
                e.Description = CanvasMenu.IsComparisonMode(PanelOwner.Session.Options.SearchMode)
                    ? "Clear the filter to show values, then clear values to return to Text search. Undo restores each step."
                    : "Remove the search filter. Undo restores it.";
                return;
            }
            if (BranchClearVisible && _layout.BranchClear.Contains(point))
            {
                e.Title = "Show all branches";
                e.Description = "Remove the branch filter. Undo restores it.";
                return;
            }
            if (_layout.Preview.Contains(point))
            {
                e.Title = "Geometry preview";
                e.Text = CanvasMenu.PreviewLabel(PanelOwner.PreviewMode);
                e.Description = "Selection: draw selected rows only. Results: draw every result, highlighting selected rows. Off: no Panel+ preview. Item order labels: " +
                    (PanelOwner.ShowItemOrder ? "on" : "off") + ".";
                if (!string.IsNullOrEmpty(PanelOwner.PreviewStatus)) e.Description += " " + PanelOwner.PreviewStatus +
                    " Oversized geometry, unsupported nested types, custom UserData, or mesh limits can prevent part of the preview.";
            }
            else if (_layout.OutLabel.Contains(point) || _layout.OutItems.Contains(point) || _layout.OutValues.Contains(point))
            {
                e.Title = "Output: Items / Values";
                e.Description = _layout.OutValues.Contains(point) ? (PanelOwner.CreateSetTree ? "Sends the displayed set values." : PanelOwner.DecimalPlaces >= 0 ? "Sends the values exactly as shown." : PanelOwner.RoundUpFactor != 0 ? "Sends the rounded values at full precision; rows may be abbreviated." : "Sends full-precision values; rows may be abbreviated.") : "Items sends the items; numbers use the shown rounding (full precision in Automatic). Values sends the displayed property. Sets group all member items by set index; unavailable values are null.";
            }
            else if (_layout.Units.Contains(point))
            {
                e.Title = "Units and decimals";
                e.Text = "Changes numeric output";
                e.Description = "Convert mm ↔ m, mm² ↔ m², mm³ ↔ m³ or rad ↔ deg; set explicit decimals (0–12), shared with numeric output; or round output up to a factor (factor 5: 12.1 → 15).";
            }
            else if (_layout.Trim.Contains(point) || _layout.Simplify.Contains(point) || _layout.Flatten.Contains(point) || _layout.Reverse.Contains(point) || _layout.CreateSet.Contains(point))
            {
                e.Title = _layout.Trim.Contains(point) ? "Trim tree" : _layout.Simplify.Contains(point) ? "Simplify tree" : _layout.Flatten.Contains(point) ? "Flatten tree" : _layout.Reverse.Contains(point) ? "Reverse tree" : "Create set";
                e.Text = (_layout.Trim.Contains(point) ? "Depth: " + PanelOwner.TrimDepth + " · " : "") + "Changes output · Tree tools → Units → Filters → Create set";
                e.Description = _layout.Trim.Contains(point) ? "Remove this many trailing path levels; matching branches merge. 0 turns Trim off." :
                    _layout.Simplify.Contains(point) ? "Toggle removal of path indices shared by all branches." :
                    _layout.Flatten.Contains(point) ? "Toggle merging all branches into {0}." :
                    _layout.Reverse.Contains(point) ? "Toggle reversing items within each output branch." :
                    "Group equal displayed values per branch; add Flatten for one set. Out Items sends every member in its set branch; Out Values sends each distinct value once.";
            }
            else if (_layout.Type.Contains(point) || _layout.Sort.Contains(point) || _layout.Branch.Contains(point))
            {
                var options = PanelOwner.Session.Options;
                e.Title = _layout.Type.Contains(point) ? "Filter by type" : _layout.Sort.Contains(point) ? "Sort this view" : "Filter by branch";
                e.Text = _layout.Type.Contains(point) ? PanelOwner.Session.HasCurrentResult ? CanvasMenu.TypeHint(PanelOwner.Session.Result, options) : "Types" :
                    _layout.Sort.Contains(point) ? options.SortMode.ToString() : options.BranchPath ?? "All branches";
                e.Description = _layout.Sort.Contains(point)
                    ? "Asc/Desc sorts items within each branch. Sorting changes the view only; output keeps its tree order."
                    : "Filters change both the view and the output wire.";
                if (_layout.Type.Contains(point) && PanelOwner.Session.HasCurrentResult)
                    e.Description = (PanelOwner.Session.Result.RowPositions.Count > 0 ? "Visible types: " : "Source types: ") +
                        CanvasMenu.TypeDetails(PanelOwner.Session.Result) + "\n\n" + e.Description;
                else if (_layout.Branch.Contains(point))
                {
                    e.Description = "Show one branch. While the menu is open, type digits and ; to find a path, then press Enter. " + e.Description;
                    if (PanelOwner.Session.Error != null) e.Description = PanelOwner.Session.Error + "\n\n" + e.Description;
                }
                string inputDiagnostic = PanelQueryPolicy.InputDiagnostic(PanelOwner.Session.Snapshot, options);
                if (_layout.Type.Contains(point) && inputDiagnostic.Length > 0) e.Description += "\n\n" + inputDiagnostic;
            }
            else if (_layout.Reset.Contains(point))
            {
                e.Title = "Reset all";
                e.Description = "Clears filters, sorting and selection, turns off tree tools and units, and sets Out to Items and B + F to Results. Keeps Preview, item order, D|F and size. Undo restores the settings.";
            }
            else if (_warningHits.Any(hit => hit.Bounds.Contains(point)))
            {
                e.Title = "Data checks";
                e.Description = "Counts the input before filters. Replaces the current filters; Undo restores them.";
            }
            else if (_layout.Search.Contains(point))
            {
                var options = PanelOwner.Session.Options;
                e.Title = CanvasMenu.SearchModeLabel(options.SearchMode);
                e.Text = options.DisplayFilterValue ? "Showing values · click to filter" : string.IsNullOrEmpty(options.Search) ? "Click to search · arrow chooses the mode" : options.Search;
                e.Description = options.SearchMode == PanelSearchMode.Text
                    ? "Search displayed values, paths and types; > 800 or <= 25 compares numbers."
                    : options.SearchMode == PanelSearchMode.Regex
                    ? "Case-insensitive regular expression on displayed text, e.g. ^Wall or beam-[0-9]+."
                    : options.SearchMode == PanelSearchMode.ListLength
                    ? "Compare each branch's item count with >, >=, <, <=, = or != (a bare number means =)."
                    : PanelProperties.IsProperty(options.SearchMode) ? CanvasMenu.PropertyHelp(options.SearchMode)
                    : "Compare with >, >=, <, <=, = or != (a bare number means =) against the shown number. Rows without this value are excluded.";
                if (options.DisplayFilterValue)
                    e.Description = "Shows each item's value; n/a means none. Click the search field to filter by it.";
                else if (!CanvasMenu.HasValueChoices(options.SearchMode)) e.Description += " Rows update as you type; Enter applies, Esc cancels.";
                if (PanelOwner.Session.Error != null) e.Description = PanelOwner.Session.Error + "\n\n" + e.Description;
            }
            else if (_layout.Jump.Contains(point))
            {
                e.Title = "Jump to a row";
                e.Text = DisplayListLengths ? "Branch row in the list-length view" : "Row in the current view";
                e.Description = DisplayListLengths ? "Enter a branch row number, starting at 1." : "Enter a row number, starting at 1 for the first row of the current view.";
            }
            else if (_layout.Resize.Contains(point))
            {
                e.Title = "Resize Panel+";
                e.Text = "Drag this corner";
                e.Description = "The panel size is saved with the Grasshopper definition.";
            }
            else if (_layout.Footer.Contains(point))
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
                    e.Description = PanelOwner.Session.Error + "\n\nEdit the filter or use Clear filters (right-click menu).";
                    return;
                }
                e.Title = "Row selection and copy";
                e.Text = _copyPending || DateTime.UtcNow < _copiedUntil ? _actionStatus : System.FormattableString.Invariant($"{_selection.Count:N0} selected");
                if (PanelOwner.Session.HasCurrentResult) e.Text += " · " + CanvasStatistics.Summary(PanelOwner.Session.Result);
                e.Description = FooterStatus(PanelOwner.Session.Result, _singleLineFooter) + "\n\nClick to select; Ctrl/Cmd toggles, Shift extends, Alt/Option picks the same index in every branch. Right-click to copy or frame.\n\n" + KeyboardHint;
            }
            else if (RowsAreCurrent(PanelOwner.Session.Result))
            {
                foreach (var hit in _branchHits)
                    if (hit.Bounds.Contains(point))
                    {
                        e.Title = "Select branch " + hit.Path;
                        e.Text = CanvasStatistics.BranchCount(PanelOwner.Session.Result, hit.Path);
                        e.Description = hit.CopyBounds.Contains(point) ? "Copy " + (_f.List.Contains(point) ? string.Join(", ", PanelOwner.PairedPaths(hit.Path)) : hit.Path) : "Select this branch's results. Double-click to clear. Right-click for branch actions.";
                        if (!_f.List.Contains(point) && PanelOwner.Session.Result.Warnings.IsOutlier(PanelOwner.Session.Result.Snapshot, hit.Path))
                            e.Description += "\nMost branches have " + PanelOwner.Session.Result.Warnings.MajorityLength + " items";
                        if (PanelOwner.Session.Result.DisplaySet?.BranchOccurrences.TryGetValue(hit.Path, out var occurrences) == true)
                        {
                            e.Text = "List length: " + PanelOwner.Session.Snapshot.BranchItemCounts[hit.Path] + " · " + occurrences.Count + " branches";
                            e.Description = "Matching branches with this displayed count: " + string.Join(", ", occurrences) +
                                ".\nSource items after filters: " + CanvasStatistics.SourceStatuses(PanelOwner.Session.Result.DisplaySet.BranchSourceStatuses[hit.Path]) +
                                ". Out Items groups all matching branches; Out Values sends the count. B, F and geometry actions include every member. Click to select this count; double-click to clear selection.";
                        }
                        return;
                    }
                foreach (var hit in _rowHits)
                {
                    if (!hit.Bounds.Contains(point)) continue;
                    var row = PanelOwner.Session.Result.Snapshot.Rows[hit.SourcePosition];
                    PanelItemOrder.TryGetNumber(PanelOwner.Session.Result, hit.SourcePosition, out int rowNumber);
                    if (_f.List.Contains(point) && PanelOwner.PairedSnapshot != null)
                    {
                        var pairedRow = PanelOwner.PairedRow(hit.SourcePosition);
                        e.Title = "F · " + (pairedRow?.Path ?? PanelOwner.PairedData!.BranchAt(hit.SourcePosition).Paired.Path) + (pairedRow == null ? "" : " [" + pairedRow.OriginalIndex + "]");
                        e.Text = PanelOwner.Session.Result.DisplaySet != null ? "F set values" : pairedRow?.TypeName ?? "No F item";
                        e.Description = PanelTextPreview.ForTooltip(PanelOwner.Session.Result.DisplaySet != null ? PairedSetValue(hit.SourcePosition) : pairedRow == null ? "" : PairedDisplayValue(pairedRow)) + "\nClick to select the shared D / F row · double-click to clear";
                        return;
                    }
                    e.Title = (PanelOwner.ShowItemOrder || PanelOwner.Session.Options.SortMode == PanelSortMode.SourceOrder
                        ? "" : "Row " + hit.ViewNumber + " · ") + row.Path + " [" + rowNumber + "]";
                    var setOccurrences = PanelOwner.Session.Result.SetOccurrencesAt(hit.SourcePosition);
                    e.Text = setOccurrences != null ? "Set value · " + row.TypeName :
                        (row.Validity == PanelValidity.VerifiedInvalid ? "Invalid " : row.Validity == PanelValidity.Unverified ? "Unverified " : "") + row.TypeName;
                    var snapshot = PanelOwner.Session.Result.Snapshot;
                    var value = PanelOwner.DisplayValue(snapshot, row, hit.SourcePosition);
                    var geometry = PanelOwner.Session.GetGeometryDetails(snapshot, hit.SourcePosition) ?? row.GeometryInfo;
                    string details;
                    if (row.Validity == PanelValidity.VerifiedInvalid)
                        details = value + "\n" + (string.IsNullOrWhiteSpace(row.InvalidReason) ? "Geometry or value is invalid." : row.InvalidReason);
                    else if (row.Validity == PanelValidity.Unverified)
                        details = value + "\n" + (string.IsNullOrWhiteSpace(row.InvalidReason) ? "Validity could not be verified." : row.InvalidReason);
                    else if (geometry != null)
                    {
                        details = geometry.FormatDetails(snapshot.UnitConversion, PanelOwner.DecimalPlaces, PanelOwner.RoundUpFactor);
                        if (PanelOwner.Session.IsGeometryPending(snapshot, hit.SourcePosition)) details += "\nCalculating measurement…";
                        else if (PanelOwner.Session.HasGeometryMeasurement(snapshot, hit.SourcePosition) && !geometry.Value.HasValue)
                            details += "\nMeasurement unavailable.";
                    }
                    else details = value;
                    string rounding = PanelFilterValue.RoundingDescription(snapshot, row, PanelOwner.Session.Options, geometry);
                    // The rounding line already describes the shown metric. Retain
                    // the labeled geometry details without repeating its compact summaries.
                    if (PanelOwner.Session.Options.DisplayFilterValue && !(rounding.Length > 0 && geometry != null))
                        details = CanvasMenu.SearchModeLabel(PanelOwner.Session.Options.SearchMode) + ": " + value + "\n" +
                            PanelValuePresentation.FormatDisplay(row, snapshot.UnitConversion, PanelOwner.DecimalPlaces, geometry, roundUpFactor: PanelOwner.RoundUpFactor) + "\n" + details;
                    if (rounding.Length > 0) details = rounding + "\n" + details;
                    if (setOccurrences != null)
                    {
                        details += "\nSource occurrences after filters: " + CanvasStatistics.SourceStatuses(setOccurrences.SourceStatuses);
                        if (PanelOwner.OutputValues == PanelOutputValues.Items)
                            details += "\nD output branch: " + PanelItemOrder.Address(PanelOwner.Session.Result, hit.SourcePosition, true, PanelOwner.FlattenTree);
                    }
                    e.Description = PanelTextPreview.ForTooltip(details) + "\n\nClick to select · Ctrl/Cmd toggles · Shift extends · Alt/Option same index · right-click for more";
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
            if (active) Fill(g, PanelTheme.Rgb(227, 237, 223), bounds);
            using (var pen = new Pen(active ? Accent : PanelTheme.Rgb(224, 229, 222)))
                g.DrawLine(pen, bounds.X, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
            Text(g, value, font, active ? Accent : Muted, new RectangleF(bounds.X + 3, bounds.Y, bounds.Width - 6, bounds.Height), trim);
        }
        private static void DropdownControl(Graphics g, RectangleF bounds, string value, Font font, StringFormat trim, bool active,
            float reserve = 0, bool warning = false)
        {
            Control(g, bounds, "", font, trim, active);
            float inset = 4;
            if (warning)
            {
                // An input diagnostic is flagged with a drawn triangle rather than a bare "!".
                DrawWarningTriangle(g, new PointF(bounds.X + 4, bounds.Y + bounds.Height / 2));
                inset += 12;
            }
            Text(g, value, font, active ? Accent : Muted, new RectangleF(bounds.X + inset, bounds.Y, bounds.Width - 13 - inset - reserve, bounds.Height), trim);
            float x = bounds.Right - 10, y = bounds.Y + bounds.Height / 2 - 1;
            using (var pen = new Pen(active ? Accent : Muted, 1))
                g.DrawLines(pen, new[] { new PointF(x, y), new PointF(x + 3, y + 3), new PointF(x + 6, y) });
        }

        private static void DrawWarningTriangle(Graphics g, PointF origin)
        {
            float tx = origin.X, ty = origin.Y;
            using (var brush = new SolidBrush(PanelTheme.Warning))
                g.FillPolygon(brush, new[] { new PointF(tx + 4.5f, ty - 5), new PointF(tx + 9, ty + 4), new PointF(tx, ty + 4) });
            using (var pen = new Pen(PanelTheme.Background, 1.2f))
                g.DrawLine(pen, tx + 4.5f, ty - 2, tx + 4.5f, ty + 1.5f);
        }

        private static void DrawClear(Graphics g, RectangleF bounds)
        {
            float cx = bounds.X + bounds.Width / 2, cy = bounds.Y + bounds.Height / 2;
            using (var pen = new Pen(Muted, 1.2f))
            {
                g.DrawLine(pen, cx - 3, cy - 3, cx + 3, cy + 3);
                g.DrawLine(pen, cx + 3, cy - 3, cx - 3, cy + 3);
            }
        }

        // Minimize, not close: the panel stays on the canvas as a capsule.
        private void DrawCloseAction(Graphics g)
        {
            float x = _layout.Close.X + 6, y = _layout.Close.Y + 5;
            using (var pen = new Pen(Muted, 1.4f))
                g.DrawLine(pen, x, y + 8, x + 9, y + 8);
        }

        private void DrawDisplayAction(Graphics g)
        {
            float x = _layout.Display.X + 4, y = _layout.Display.Y + 5;
            bool expand = PanelOwner.CanvasDisplay == CanvasPanelDisplay.Half;
            using (var pen = new Pen(Muted, 1.2f))
            {
                g.DrawLines(pen, expand
                    ? new[] { new PointF(x, y), new PointF(x + 6, y + 6), new PointF(x + 12, y) }
                    : new[] { new PointF(x, y + 6), new PointF(x + 6, y), new PointF(x + 12, y + 6) });
            }
        }

        // An arrow pointing into list rows: go to a row number.
        private void DrawJumpAction(Graphics g)
        {
            float x = _layout.Jump.X + 4, y = _layout.Jump.Y + 4;
            using (var pen = new Pen(Muted, 1.2f))
            {
                for (int line = 0; line < 3; line++) g.DrawLine(pen, x + 7, y + 1 + line * 4.5f, x + 12, y + 1 + line * 4.5f);
                g.DrawLine(pen, x, y + 5.5f, x + 5, y + 5.5f);
                g.DrawLines(pen, new[] { new PointF(x + 3, y + 3.5f), new PointF(x + 5, y + 5.5f), new PointF(x + 3, y + 7.5f) });
            }
        }

        // Counter-clockwise, as for reset/undo. A clockwise arrow reads as "refresh".
        private void DrawResetAction(Graphics g)
        {
            float x = _layout.Reset.X + 5, y = _layout.Reset.Y + 4;
            using (var pen = new Pen(Muted, 1.2f))
            {
                g.DrawArc(pen, x, y, 10, 10, 155, -295);
                g.DrawLine(pen, x + 1, y + 2, x + 1, y - 1);
                g.DrawLine(pen, x + 1, y + 2, x + 4, y + 2);
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
            internal BranchHit(RectangleF bounds, string path, RectangleF copyBounds) { Bounds = bounds; Path = path; CopyBounds = copyBounds; }
            internal RectangleF CopyBounds { get; }
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
