using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;
using BetterGhPanel.UI;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Undo;

namespace BetterGhPanel
{
    public sealed partial class BetterPanelComponent : GH_Component
    {
        private PanelSession? _session;
        private readonly PanelGeometryHighlight _highlight = new PanelGeometryHighlight();
        private bool _previewSuspended;
        private IGH_Goo?[] _sourceItems = Array.Empty<IGH_Goo?>();
        private string _geometryUnits = "units";
        private Rhino.UnitSystem _geometryUnitSystem = Rhino.UnitSystem.None;
        private double _geometryTolerance = 0.001;
        private CancellationTokenSource? _copyCancellation;
        private bool _retainGeometryMeasurements;
        private GH_Structure<IGH_Goo>? _queryTree;
        private bool _solving, _preserveQuery, _reuseQuery;
        private long _outputRequest;
        private PanelQueryOptions? _outputOptions;
        private bool _outputPending;
        private PanelQueryOptions _savedOptions = new PanelQueryOptions();
        private bool _restoringView;
        internal int PreviewSize => CanvasPanelSize.PresetIndex(CanvasWidth, CanvasHeight);
        internal float CanvasWidth { get; private set; } = CanvasPanelSize.DefaultWidth;
        internal float CanvasHeight { get; private set; } = CanvasPanelSize.DefaultHeight;
        internal CanvasPanelDisplay CanvasDisplay { get; private set; }
        internal bool PanelMinimized { get; private set; }
        internal PanelPreviewMode PreviewMode { get; private set; }
        internal bool ShowItemOrder { get; private set; }
        internal float DisplayHeight => CanvasPanelSize.DisplayHeight(CanvasHeight, CanvasDisplay);
        internal int TrimDepth { get; private set; }
        internal bool FlattenTree { get; private set; }
        internal bool ReverseTree { get; private set; }
        internal bool SimplifyTree { get; private set; }
        internal bool CreateSetTree { get; private set; }
        internal bool HasTreeOperations => TrimDepth > 0 || FlattenTree || ReverseTree || SimplifyTree || CreateSetTree;
        internal PanelUnitConversion UnitConversion { get; private set; }
        internal int DecimalPlaces { get; private set; } = PanelUnits.AutomaticDecimals;

        public BetterPanelComponent() : base("Panel+", "Panel+",
            "Inspect and manipulate typed data trees on the canvas. Tree tools, unit conversion and filters change the output. Sorting changes view order; decimal formatting never rounds wire values, and text/regex filters use the displayed formatting.",
            "Params", "Util")
        {
            RowSelection.Changed += (_, __) =>
            {
                ++_selectionVersion;
                ScheduleOutput();
            };
        }

        public override Guid ComponentGuid => new Guid("51DC646E-5F72-496A-8B40-628478541F44");
        protected override Bitmap Icon => PanelIcon.Create();
        public override GH_Exposure Exposure => GH_Exposure.primary;

        public override bool IsPreviewCapable => _highlight.PreviewCount > 0 || base.IsPreviewCapable;

        public override Rhino.Geometry.BoundingBox ClippingBox =>
            Hidden || Locked || _previewSuspended || PreviewMode == PanelPreviewMode.Off
                ? Rhino.Geometry.BoundingBox.Empty : _highlight.ClippingBox;

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            if (Hidden || Locked || _previewSuspended || PreviewMode == PanelPreviewMode.Off) return;
            _highlight.DrawWires(args, Attributes.Selected);
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            if (Hidden || Locked || _previewSuspended || PreviewMode == PanelPreviewMode.Off) return;
            _highlight.DrawMeshes(args);
        }

        internal PanelSession Session
        {
            get
            {
                if (_session == null)
                {
                    _session = new PanelSession(() => Instances.ActiveCanvas?.Invalidate());
                    _session.SetDecimalPlacesResolver(() => DecimalPlaces);
                    if (Locked) _session.InvalidateInput(false, suspended: true);
                    _session.SetDetailResolver(position => position < _sourceItems.Length ? _sourceItems[position]?.ToString() ?? "<null>" : null);
                    _session.SetGeometryDetailResolver(position => position < _sourceItems.Length
                        ? GeometryHints.PrepareMeasurement(_sourceItems[position], _geometryUnits) : null);
                    _savedOptions.DecimalPlaces = DecimalPlaces;
                    _session.UpdateQuery(_savedOptions);
                    _session.QueryChanged += (_, __) =>
                    {
                        (Attributes as BetterPanelAttributes)?.InvalidateStatistics();
                        CancelPendingCopy();
                        ClearHighlight();
                        if (!_restoringView)
                        {
                            var document = OnPingDocument();
                            document?.Modified();
                            ScheduleOutput();
                        }
                    };
                    _session.ResultChanged += (_, __) =>
                    {
                        (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
                        if (!_solving && !_restoringView) ScheduleOutput();
                    };
                }
                return _session;
            }
        }

        public override void CreateAttributes() => m_attributes = new BetterPanelAttributes(this);

        internal void ResetPanelView()
        {
            CancelPendingCopy();
            CanvasTextInput.CloseActive(this);
            (Attributes as BetterPanelAttributes)?.ResetViewState();
            RowSelection.Clear();
            var previousResult = Session.Result;
            Session.UpdateQuery(new PanelQueryOptions { DecimalPlaces = DecimalPlaces });
            if (ReferenceEquals(previousResult, Session.Result)) (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
        }

        internal void SetPreviewMode(PanelPreviewMode mode)
        {
            if (!Enum.IsDefined(typeof(PanelPreviewMode), mode) || PreviewMode == mode) return;
            RecordUndoEvent("Panel preview mode");
            PreviewMode = mode;
            (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetShowItemOrder(bool show)
        {
            if (ShowItemOrder == show) return;
            RecordUndoEvent("Panel+ item order", new ItemOrderUndo(InstanceGuid, ShowItemOrder, show));
            ApplyShowItemOrder(show);
        }

        private void ApplyShowItemOrder(bool show)
        {
            ShowItemOrder = show;
            (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        private sealed class ItemOrderUndo : GH_UndoAction
        {
            private readonly Guid _component;
            private readonly bool _before, _after;
            internal ItemOrderUndo(Guid component, bool before, bool after)
            { _component = component; _before = before; _after = after; }
            public override bool ExpiresSolution => false;
            public override bool ExpiresDisplay => false;
            protected override void Internal_Undo(GH_Document doc) => Apply(doc, _before);
            protected override void Internal_Redo(GH_Document doc) => Apply(doc, _after);
            private void Apply(GH_Document doc, bool show) =>
                (doc.FindObject(_component, false) as BetterPanelComponent)?.ApplyShowItemOrder(show);
        }

        internal void ResetPanel()
        {
            var options = Session.Options;
            bool outputChanged = HasTreeOperations || UnitConversion != PanelUnitConversion.None;
            bool settingsChanged = outputChanged || OutputScope != PanelOutputScope.Filtered || DecimalPlaces != PanelUnits.AutomaticDecimals ||
                !string.IsNullOrEmpty(options.Search) || options.SearchMode != PanelSearchMode.Text || options.TypeName != null || options.BranchPath != null ||
                options.BranchPrefix || options.OnlyInvalid || options.SortMode != PanelSortMode.SourceOrder;
            if (settingsChanged) RecordUndoEvent("Reset Panel+");
            TrimDepth = 0;
            FlattenTree = ReverseTree = SimplifyTree = CreateSetTree = false;
            UnitConversion = PanelUnitConversion.None;
            DecimalPlaces = PanelUnits.AutomaticDecimals;
            OutputScope = PanelOutputScope.Filtered;
            ResetPanelView();
            if (settingsChanged) OnPingDocument()?.Modified();
            if (outputChanged) ExpireSolution(true);
            else ScheduleOutput();
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetCanvasSize(float width, float height)
        {
            CanvasWidth = CanvasPanelSize.Width(width);
            CanvasHeight = CanvasPanelSize.Height(height);
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetTreeOperations(int trimDepth, bool flatten, bool reverse, bool simplify, bool createSet)
        {
            trimDepth = Math.Max(0, trimDepth);
            if (TrimDepth == trimDepth && FlattenTree == flatten && ReverseTree == reverse && SimplifyTree == simplify && CreateSetTree == createSet) return;
            bool pathsChanged = TrimDepth != trimDepth || FlattenTree != flatten || SimplifyTree != simplify;
            RecordUndoEvent("Panel+ tree tools");
            TrimDepth = trimDepth;
            FlattenTree = flatten;
            ReverseTree = reverse;
            SimplifyTree = simplify;
            CreateSetTree = createSet;
            if (pathsChanged)
            {
                // Reverse and Create set preserve paths, so branch selection stays valid.
                var options = Session.Options;
                options.BranchPath = null;
                options.BranchPrefix = false;
                Session.UpdateQuery(options);
            }
            OnPingDocument()?.Modified();
            ExpireSolution(true);
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetCanvasDisplay(CanvasPanelDisplay display)
        {
            if (!Enum.IsDefined(typeof(CanvasPanelDisplay), display) || CanvasDisplay == display) return;
            RecordUndoEvent("Panel+ display size");
            CanvasTextInput.CloseActive();
            CanvasDisplay = display;
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetPanelMinimized(bool minimized)
        {
            if (PanelMinimized == minimized) return;
            // A generic component undo reloads the data and clears transient row
            // state. This action changes presentation only, including on undo.
            RecordUndoEvent(minimized ? "Minimize Panel+" : "Restore Panel+",
                new MinimizedUndo(InstanceGuid, PanelMinimized, minimized));
            ApplyPanelMinimized(minimized);
        }

        private void ApplyPanelMinimized(bool minimized)
        {
            CanvasTextInput.CloseActive(this);
            (Attributes as BetterPanelAttributes)?.ClosePresentationControls();
            PanelMinimized = minimized;
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        private sealed class MinimizedUndo : GH_UndoAction
        {
            private readonly Guid _component;
            private readonly bool _before, _after;
            internal MinimizedUndo(Guid component, bool before, bool after)
            { _component = component; _before = before; _after = after; }
            public override bool ExpiresSolution => false;
            public override bool ExpiresDisplay => false;
            protected override void Internal_Undo(GH_Document doc) => Apply(doc, _before);
            protected override void Internal_Redo(GH_Document doc) => Apply(doc, _after);
            private void Apply(GH_Document doc, bool minimized) =>
                (doc.FindObject(_component, false) as BetterPanelComponent)?.ApplyPanelMinimized(minimized);
        }

        internal void SetUnits(PanelUnitConversion conversion, int decimals)
        {
            if (!Enum.IsDefined(typeof(PanelUnitConversion), conversion)) conversion = PanelUnitConversion.None;
            decimals = Math.Max(PanelUnits.AutomaticDecimals, Math.Min(PanelUnits.MaximumDecimals, decimals));
            bool conversionChanged = UnitConversion != conversion;
            if (!conversionChanged && DecimalPlaces == decimals) return;
            CancelPendingCopy();
            RecordUndoEvent("Panel+ units and decimals");
            UnitConversion = conversion;
            DecimalPlaces = decimals;
            OnPingDocument()?.Modified();
            if (conversionChanged)
            {
                _retainGeometryMeasurements = true;
                try { ExpireSolution(true); }
                finally { _retainGeometryMeasurements = false; }
            }
            else
            {
                var options = Session.Options;
                options.DecimalPlaces = DecimalPlaces;
                Session.UpdateQuery(options);
            }
            Instances.ActiveCanvas?.Invalidate();
        }

        internal string DisplayValue(PanelSnapshot snapshot, PanelRow row, int sourcePosition = -1)
        {
            var geometry = Session.GetGeometryDetails(snapshot, sourcePosition);
            if (geometry == null)
                geometry = Session.HasGeometryMeasurement(snapshot, sourcePosition)
                    ? row.GeometryInfo?.WithUnavailableMeasurement() : row.GeometryInfo;
            var display = PanelValuePresentation.FormatDisplay(row, snapshot.UnitConversion, DecimalPlaces, geometry);
            if (row.IsValid && geometry != null)
                return display + (Session.IsGeometryPending(snapshot, sourcePosition) ? " · measuring…" : "");
            return display;
        }

        internal string CopyValue(PanelSnapshot snapshot, int position)
        {
            if (!ReferenceEquals(snapshot, Session.Snapshot) || position < 0 || position >= snapshot.Rows.Count)
                throw new InvalidOperationException("The selected data has changed. Select the rows again.");
            var row = snapshot.Rows[position];
            if (!row.IsValid)
                return PanelValuePresentation.FormatDisplay(row, snapshot.UnitConversion, DecimalPlaces);
            if (row.NumericValue.HasValue)
                return PanelUnits.Format(row.NumericValue.Value, PanelUnitConversion.None, DecimalPlaces, CultureInfo.InvariantCulture);
            var geometry = Session.GetGeometryDetails(snapshot, position) ?? row.GeometryInfo;
            if (row.IsValid && geometry?.Value != null)
            {
                var value = PanelUnits.FormatGeometry(geometry.Value.Value, geometry.Power, geometry.Units,
                    snapshot.UnitConversion, DecimalPlaces, includeUnits: false);
                if (value == "unavailable") throw new InvalidOperationException("A selected measurement is unavailable.");
                return value;
            }
            if (row.IsValid && geometry?.CanMeasure == true)
                throw new InvalidOperationException("A selected measurement is unavailable.");
            return PanelValuePresentation.FormatDisplay(row, snapshot.UnitConversion, DecimalPlaces, geometry);
        }

        internal void CancelPendingCopy()
        {
            var cancellation = _copyCancellation;
            _copyCancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
        }

        private bool TryGetActionPositions(PanelSnapshot snapshot, IReadOnlyList<int> positions, out int[] ordered)
        {
            ordered = Array.Empty<int>();
            var session = _session;
            if (session == null || !session.HasCurrentResult || !ReferenceEquals(snapshot, session.Snapshot) || positions.Count == 0) return false;
            var selected = new HashSet<int>(positions);
            if (selected.Any(position => position < 0 || position >= _sourceItems.Length || position >= snapshot.Rows.Count)) return false;
            ordered = session.Result.RowPositions.Where(selected.Contains).ToArray();
            return ordered.Length == selected.Count;
        }

        internal string? FrameRows(PanelSnapshot snapshot, IReadOnlyList<int> positions)
        {
            if (_previewSuspended) return "Activate this Grasshopper document to frame geometry";
            if (!TryGetActionPositions(snapshot, positions, out var ordered)) return "Data changed; select rows again";
            var result = PanelGeometryActions.Frame(ordered.Select(position => _sourceItems[position]));
            return result.Success ? null : result.Error;
        }

        internal void HighlightRows(PanelSnapshot snapshot, IReadOnlyList<int> positions)
        {
            var session = _session;
            if (_previewSuspended || Locked || PreviewMode == PanelPreviewMode.Off || session == null || !session.HasCurrentResult ||
                !ReferenceEquals(snapshot, session.Snapshot))
            {
                ClearHighlight();
                return;
            }
            var ordered = Array.Empty<int>();
            if (positions.Count > 0 && !TryGetActionPositions(snapshot, positions, out ordered))
            {
                ClearHighlight();
                return;
            }
            var previewPositions = PreviewMode == PanelPreviewMode.FilteredResults ? session.Result.RowPositions : ordered;
            _highlight.SetOrderResult(ShowItemOrder && previewPositions.Count > 0 ? session.Result : null);
            _highlight.Set(_sourceItems, previewPositions, ordered);
            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void ClearHighlight()
        {
            bool visible = _highlight.PreviewCount > 0;
            _highlight.Clear();
            if (visible) Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal async Task<string> CopyValuesAsync(PanelSnapshot snapshot, IReadOnlyList<int> positions)
        {
            CancelPendingCopy();
            if (!TryGetActionPositions(snapshot, positions, out var ordered)) return "Data changed; select rows again";
            var session = _session!;
            var cancellation = new CancellationTokenSource();
            _copyCancellation = cancellation;
            bool measured = await session.MeasureGeometryAsync(snapshot, ordered, cancellation.Token);
            var completion = new TaskCompletionSource<string>();
            // Rhino does not guarantee a SynchronizationContext for every canvas
            // callback. Live goo and the system clipboard belong to its UI thread.
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    if (!measured || !ReferenceEquals(_copyCancellation, cancellation) || !ReferenceEquals(_session, session) ||
                        !TryGetActionPositions(snapshot, ordered, out _)) completion.SetResult("Copy cancelled");
                    else
                    {
                        var text = string.Join(Environment.NewLine, ordered.Select(position => CopyValue(snapshot, position)));
                        using (var clipboard = new Eto.Forms.Clipboard()) clipboard.Text = text;
                        completion.SetResult(ordered.Length == 1 ? "Value copied" : ordered.Length + " values copied");
                    }
                }
                catch (Exception ex) { completion.SetResult("Copy failed: " + ex.Message); }
                finally
                {
                    if (ReferenceEquals(_copyCancellation, cancellation))
                    {
                        _copyCancellation = null;
                        cancellation.Dispose();
                    }
                }
            }));
            return await completion.Task;
        }

        internal bool TryCopyGeometry(PanelSnapshot snapshot, IReadOnlyList<int> positions, out string status)
        {
            CancelPendingCopy();
            if (!TryGetActionPositions(snapshot, positions, out var ordered))
            {
                status = "Data changed; select rows again";
                return false;
            }
            var result = PanelGeometryActions.CopyGeometry(ordered.Select(position => _sourceItems[position]),
                _geometryUnitSystem, _geometryTolerance);
            status = result.Success ? result.Count + " geometries copied" +
                (result.Skipped > 0 ? "; " + result.Skipped + " skipped" : "") : result.Error ?? "Copy geometry failed";
            return result.Success;
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Data", "D", "Any typed data tree to inspect", GH_ParamAccess.tree);
            p[0].Optional = true;
            p.AddGenericParameter("Paired data", "F", "Optional data paired with D by original path and item index. Paths and branch lengths must match exactly. F follows the primary tree tools and membership; its values are not converted.", GH_ParamAccess.tree);
            p[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("Data", "D", "Matching typed items after Simplify → Trim → Flatten → Reverse → Create set → unit conversion → filters. Output keeps tree order and full numeric precision. Text/regex filters use displayed formatting; sorting changes only view order.", GH_ParamAccess.tree);
            p.AddGenericParameter("Selected data", "S", "Selected rows within the current filtered results, in transformed tree order. Empty when no rows are selected.", GH_ParamAccess.tree);
            p.AddBooleanParameter("Membership mask", "B", "True at original D input positions retained by the B + F source setting (Filtered by default, or Selected). Preserves original paths and item counts. Removed set duplicates are False. This mask conveys membership, not transformed ordering.", GH_ParamAccess.tree);
            p.AddGenericParameter("Paired results", "F", "Paired input values corresponding to the B + F source setting (Filtered by default, or Selected), in the primary transformed tree order. F retains its original values and types.", GH_ParamAccess.tree);
        }

        private void ScheduleOutput()
        {
            var document = OnPingDocument();
            var session = _session;
            if (_solving || _restoringView || Locked || document == null || session == null || _queryTree == null) return;
            var options = session.Options;
            if (!_outputPending && _outputOptions != null && SameOutputFilter(_outputOptions, options) &&
                _publishedSelectionVersion == _selectionVersion && _publishedScope == OutputScope) return;
            var snapshot = session.Snapshot;
            long request = ++_outputRequest;
            // Never solve recursively from a query callback. Publish through a
            // normal GH solution so downstream components collect the new tree.
            document.ScheduleSolution(1, _ =>
            {
                if (request != _outputRequest || !ReferenceEquals(document, OnPingDocument()) ||
                    !ReferenceEquals(session, _session) || !ReferenceEquals(snapshot, session.Snapshot) || _queryTree == null) return;
                _preserveQuery = true;
                _reuseQuery = true;
                try { ExpireSolution(false); }
                finally { _preserveQuery = false; }
            });
        }

        private static bool SameOutputFilter(PanelQueryOptions left, PanelQueryOptions right) =>
            left.Search == right.Search && left.SearchMode == right.SearchMode && left.TypeName == right.TypeName &&
            left.OnlyInvalid == right.OnlyInvalid && left.BranchPath == right.BranchPath && left.BranchPrefix == right.BranchPrefix &&
            (left.DecimalPlaces == right.DecimalPlaces || !PanelQueryEngine.UsesDisplayedText(right));

        private static bool HasOutputFilter(PanelQueryOptions options) =>
            (options.SearchMode == PanelSearchMode.Regex ? !string.IsNullOrEmpty(options.Search) : !string.IsNullOrWhiteSpace(options.Search)) ||
            options.TypeName != null || options.OnlyInvalid || !string.IsNullOrWhiteSpace(options.BranchPath);

        public override void ClearData()
        {
            // Grasshopper expires the component before collecting its next input.
            // Invalidate now, even when solving is deferred or an upstream node fails.
            if (!_preserveQuery)
            {
                ++_outputRequest;
                _queryTree = null;
                ClearOutputSources();
                if (!_retainGeometryMeasurements) _sourceItems = Array.Empty<IGH_Goo?>();
                _reuseQuery = false;
                CancelPendingCopy();
                ClearHighlight();
                (Attributes as BetterPanelAttributes)?.ResetViewState();
                RowSelection.Clear();
                _session?.InvalidateInput(_retainGeometryMeasurements, suspended: Locked);
            }
            base.ClearData();
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            if (!da.GetDataTree(0, out GH_Structure<IGH_Goo> tree)) tree = new GH_Structure<IGH_Goo>();
            // Any upstream expiration clears this flag through ClearData. GH may
            // duplicate input goo during collection even when sources are unchanged,
            // so ownership follows the captured solution, not recollected identity.
            bool reuse = _reuseQuery && _queryTree != null;
            _reuseQuery = false;
            _solving = true;
            try
            {
                if (!reuse)
                {
                    CancelPendingCopy();
                    ClearHighlight();
                    var options = Session.Options;
                    if (options.DecimalPlaces != DecimalPlaces)
                    {
                        options.DecimalPlaces = DecimalPlaces;
                        Session.UpdateQuery(options);
                    }
                    _inputTree = PanelTreeOperations.CopyStructure(tree);
                    tree = PanelTreeOperations.ApplyWithSources(tree, TrimDepth, SimplifyTree, FlattenTree, ReverseTree, CreateSetTree,
                        out _retainedSources, out var setOccurrences);
                    _queryTree = PanelTreeOperations.CopyStructure(PanelUnitOperations.Apply(tree, UnitConversion));
                    CapturePairedInput(da);
                    var previousSources = _sourceItems;
                    var previousUnits = _geometryUnits;
                    var rhinoDocument = Rhino.RhinoDoc.ActiveDoc;
                    _geometryUnitSystem = rhinoDocument?.ModelUnitSystem ?? Rhino.UnitSystem.None;
                    _geometryTolerance = rhinoDocument?.ModelAbsoluteTolerance ?? 0.001;
                    _geometryUnits = rhinoDocument == null || rhinoDocument.ModelUnitSystem == Rhino.UnitSystem.None
                        ? "units" : rhinoDocument.GetUnitSystemName(true, false, false, true);
                    if (string.IsNullOrWhiteSpace(_geometryUnits)) _geometryUnits = "units";
                    var snapshot = SnapshotCapture.CaptureWithOccurrences(_queryTree, out _sourceItems, UnitConversion, _geometryUnits, setOccurrences);
                    if (_retainGeometryMeasurements && previousUnits == _geometryUnits)
                        Session.AttachConvertedSnapshot(snapshot, position => position < previousSources.Length &&
                            position < _sourceItems.Length && ReferenceEquals(previousSources[position], _sourceItems[position]));
                    else Session.AttachSnapshot(snapshot);
                }
                // Pending/failed filters expose no stale or unfiltered items. The
                // completed query schedules another solution using this same tree.
                PublishOutputs(da);
                if (Session.Error != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Filter: " + Session.Error);
            }
            finally { _solving = false; }
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            ReleaseSession();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close) ReleaseSession();
            else if (context == GH_DocumentContext.Unloaded)
            {
                _previewSuspended = true;
                (Attributes as BetterPanelAttributes)?.ClearRowSelection();
                ClearHighlight();
                CancelPendingCopy();
                CanvasTextInput.CloseActive();
                // Unloaded is a canvas/tab switch, not document destruction.
                if (_session != null)
                {
                    _session.AnimateBusyState(false);
                    _session.RequestGeometryMeasurements(_session.Snapshot, Array.Empty<int>());
                }
            }
            else if (context == GH_DocumentContext.Loaded || context == GH_DocumentContext.Open)
            {
                _previewSuspended = false;
                (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
            }
            base.DocumentContextChanged(document, context);
        }

        private void ReleaseSession()
        {
            ++_outputRequest;
            _queryTree = null;
            ClearOutputSources();
            _reuseQuery = false;
            CancelPendingCopy();
            ClearHighlight();
            (Attributes as BetterPanelAttributes)?.ResetViewState();
            RowSelection.Clear();
            CanvasTextInput.CloseActive();
            if (_session != null) _savedOptions = _session.Options;
            _session?.Dispose();
            _session = null;
            _sourceItems = Array.Empty<IGH_Goo?>();
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, PanelMinimized ? "Restore panel" : "Minimize to capsule", (_, __) => SetPanelMinimized(!PanelMinimized));
            Menu_AppendItem(menu, "Reset panel", (_, __) => ResetPanel());
            Menu_AppendItem(menu, "Reset panel view", (_, __) => ResetPanelView());
            Menu_AppendItem(menu, "Reset tree tools", (_, __) => SetTreeOperations(0, false, false, false, false), HasTreeOperations);
            var outputMenu = Menu_AppendItem(menu, "B + F source: " + (OutputScope == PanelOutputScope.Filtered ? "Filtered" : "Selected"));
            Menu_AppendItem(outputMenu.DropDown, "Filtered results", (_, __) => SetOutputScope(PanelOutputScope.Filtered), true, OutputScope == PanelOutputScope.Filtered);
            Menu_AppendItem(outputMenu.DropDown, "Selected rows", (_, __) => SetOutputScope(PanelOutputScope.Selected), true, OutputScope == PanelOutputScope.Selected);
            var sizeMenu = Menu_AppendItem(menu, "Canvas panel size");
            var names = CanvasPanelSize.PresetNames;
            for (var i = 0; i < names.Length; i++)
            {
                var size = i;
                Menu_AppendItem(sizeMenu.DropDown, names[i], (_, __) =>
                {
                    RecordUndoEvent("Panel preview size");
                    SetCanvasSize(CanvasPanelSize.PresetWidth(size), CanvasPanelSize.PresetHeight(size));
                }, true, PreviewSize == size);
            }
            Menu_AppendItem(sizeMenu.DropDown, "Custom", (_, __) => { }, false, PreviewSize < 0);
        }

        public override bool Write(GH_IWriter writer)
        {
            var options = _session?.Options ?? _savedOptions;
            writer.SetString("PanelSearch", options.Search ?? "");
            writer.SetInt32("PanelSearchMode", (int)options.SearchMode);
            writer.SetString("PanelType", options.TypeName ?? "");
            writer.SetString("PanelBranch", options.BranchPath ?? "");
            writer.SetBoolean("PanelPrefix", options.BranchPrefix);
            writer.SetBoolean("PanelInvalid", options.OnlyInvalid);
            writer.SetInt32("PanelSort", (int)options.SortMode);
            // Retain the legacy key for older plug-in versions; dimensions take precedence.
            writer.SetInt32("PanelSize", PreviewSize < 0 ? 1 : PreviewSize);
            writer.SetSingle("PanelWidth", CanvasWidth);
            writer.SetSingle("PanelHeight", CanvasHeight);
            writer.SetInt32("PanelDisplay", (int)CanvasDisplay);
            writer.SetBoolean("PanelMinimized", PanelMinimized);
            writer.SetInt32("PanelPreviewMode", (int)PreviewMode);
            writer.SetBoolean("PanelShowItemOrder", ShowItemOrder);
            writer.SetInt32("PanelTrimDepth", TrimDepth);
            writer.SetBoolean("PanelFlatten", FlattenTree);
            writer.SetBoolean("PanelReverse", ReverseTree);
            writer.SetBoolean("PanelSimplify", SimplifyTree);
            writer.SetBoolean("PanelCreateSet", CreateSetTree);
            writer.SetInt32("PanelUnitConversion", (int)UnitConversion);
            writer.SetInt32("PanelDecimalPlaces", DecimalPlaces);
            writer.SetInt32("PanelOutputScope", (int)OutputScope);
            writer.SetInt32("PanelOutputSchema", 1);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            ++_outputRequest;
            _queryTree = null;
            _reuseQuery = false;
            ClearOutputSources();
            _sourceItems = Array.Empty<IGH_Goo?>();
            _session?.InvalidateInput(false, suspended: Locked);
            CancelPendingCopy();
            ClearHighlight();
            (Attributes as BetterPanelAttributes)?.ResetViewState();
            RowSelection.Clear();
            var search = ""; var type = ""; var branch = "";
            var prefix = false; var invalid = false; var sort = 0; var size = 1; var searchMode = 0;
            reader.TryGetString("PanelSearch", ref search);
            reader.TryGetInt32("PanelSearchMode", ref searchMode);
            reader.TryGetString("PanelType", ref type);
            reader.TryGetString("PanelBranch", ref branch);
            reader.TryGetBoolean("PanelPrefix", ref prefix);
            reader.TryGetBoolean("PanelInvalid", ref invalid);
            reader.TryGetInt32("PanelSort", ref sort);
            bool hasLegacySize = reader.TryGetInt32("PanelSize", ref size);
            _savedOptions = new PanelQueryOptions { Search = search, TypeName = string.IsNullOrEmpty(type) ? null : type,
                SearchMode = Enum.IsDefined(typeof(PanelSearchMode), searchMode) ? (PanelSearchMode)searchMode : PanelSearchMode.Text,
                BranchPath = string.IsNullOrEmpty(branch) ? null : branch, BranchPrefix = prefix, OnlyInvalid = invalid,
                SortMode = Enum.IsDefined(typeof(PanelSortMode), sort) ? (PanelSortMode)sort : PanelSortMode.SourceOrder };
            size = Math.Max(0, Math.Min(CanvasPanelSize.PresetNames.Length - 1, size));
            float width = hasLegacySize ? CanvasPanelSize.PresetWidth(size) : CanvasPanelSize.DefaultWidth;
            float height = hasLegacySize ? CanvasPanelSize.PresetHeight(size) : CanvasPanelSize.DefaultHeight;
            reader.TryGetSingle("PanelWidth", ref width);
            reader.TryGetSingle("PanelHeight", ref height);
            CanvasWidth = CanvasPanelSize.Width(width);
            CanvasHeight = CanvasPanelSize.Height(height);
            int display = 0;
            reader.TryGetInt32("PanelDisplay", ref display);
            CanvasDisplay = Enum.IsDefined(typeof(CanvasPanelDisplay), display)
                ? (CanvasPanelDisplay)display : CanvasPanelDisplay.Full;
            bool minimized = false;
            reader.TryGetBoolean("PanelMinimized", ref minimized);
            PanelMinimized = minimized;
            int previewMode = 0;
            reader.TryGetInt32("PanelPreviewMode", ref previewMode);
            PreviewMode = Enum.IsDefined(typeof(PanelPreviewMode), previewMode)
                ? (PanelPreviewMode)previewMode : PanelPreviewMode.Selected;
            bool showOrder = false;
            reader.TryGetBoolean("PanelShowItemOrder", ref showOrder);
            ShowItemOrder = showOrder;
            int trimDepth = 0;
            bool flatten = false, reverse = false, simplify = false, createSet = false;
            reader.TryGetInt32("PanelTrimDepth", ref trimDepth);
            reader.TryGetBoolean("PanelFlatten", ref flatten);
            reader.TryGetBoolean("PanelReverse", ref reverse);
            reader.TryGetBoolean("PanelSimplify", ref simplify);
            reader.TryGetBoolean("PanelCreateSet", ref createSet);
            TrimDepth = Math.Max(0, trimDepth);
            FlattenTree = flatten;
            ReverseTree = reverse;
            SimplifyTree = simplify;
            CreateSetTree = createSet;
            int conversion = 0, decimals = PanelUnits.AutomaticDecimals;
            reader.TryGetInt32("PanelUnitConversion", ref conversion);
            reader.TryGetInt32("PanelDecimalPlaces", ref decimals);
            UnitConversion = Enum.IsDefined(typeof(PanelUnitConversion), conversion)
                ? (PanelUnitConversion)conversion : PanelUnitConversion.None;
            DecimalPlaces = Math.Max(PanelUnits.AutomaticDecimals, Math.Min(PanelUnits.MaximumDecimals, decimals));
            int outputScope = 0;
            reader.TryGetInt32("PanelOutputScope", ref outputScope);
            OutputScope = Enum.IsDefined(typeof(PanelOutputScope), outputScope) ? (PanelOutputScope)outputScope : PanelOutputScope.Filtered;
            _savedOptions.DecimalPlaces = DecimalPlaces;
            _restoringView = true;
            try { _session?.UpdateQuery(_savedOptions); }
            finally { _restoringView = false; }
            var success = base.Read(UpgradeLegacyOutputParameters(reader));
            (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
            Attributes?.ExpireLayout();
            return success;
        }
    }
}
