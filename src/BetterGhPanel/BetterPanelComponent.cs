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
        private CapturedMeasurementSource?[] _measurementSources = Array.Empty<CapturedMeasurementSource?>();
        private CaptureGraphWitness[] _captureGraphs = Array.Empty<CaptureGraphWitness>();
        private IGH_Goo?[] _pairedSourceItems = Array.Empty<IGH_Goo?>();
        private CapturedMeasurementSource?[] _pairedMeasurementSources = Array.Empty<CapturedMeasurementSource?>();
        private CaptureGraphWitness[] _pairedCaptureGraphs = Array.Empty<CaptureGraphWitness>();
        private PanelSnapshot? _measurementSnapshot, _staleMeasurementSnapshot;
        private string _geometryUnits = "units";
        private Rhino.UnitSystem _geometryUnitSystem = Rhino.UnitSystem.None;
        private double _geometryTolerance = 0.001;
        private MeasurementContextStamp _geometryContext;
        private bool _captureHasGeometry, _pairedHasGeometry;
        // F geometry is fenced on the document context only while its snapshot is shown.
        private bool ContextFenced => _captureHasGeometry || PairedSnapshot != null && _pairedHasGeometry;
        private CancellationTokenSource? _copyCancellation;
        private bool _retainGeometryMeasurements;
        private GH_Structure<IGH_Goo>? _queryTree;
        private bool _solving, _preserveQuery, _reuseQuery;
        private long _solveGeneration;
        private bool _issuingCaptureRetry;
        private int _captureRetries;
        // Fixture injection runs at the same boundary as native capture callbacks.
        internal Action<int, IGH_Goo?>? CaptureObserverForTesting { get; set; }
        internal Action<object>? GraphVisitObserverForTesting { get; set; }
        internal Action<GH_Structure<IGH_Goo>, GH_Structure<IGH_Goo>?>? StructureObserverForTesting { get; set; }
        internal Action? BeforeOutputValidationForTesting { get; set; }
        private bool _captureDiagnosticsForTesting;
        internal bool CaptureDiagnosticsForTesting
        {
            get => _captureDiagnosticsForTesting;
            set { _captureDiagnosticsForTesting = value; if (_session != null) _session.GeometryDiagnosticsForTesting = value; }
        }
        internal long CaptureValidationPassesForTesting { get; private set; }
        internal long CaptureValidationVisitsForTesting { get; private set; }
        internal long CaptureMeasurementSourceVisitsForTesting { get; private set; }
        internal long MeasurementEntryVisitsForTesting => _session?.GeometryEntryVisitsForTesting ?? 0;
        internal long MeasurementEntryCopiesForTesting => _session?.GeometryEntryCopiesForTesting ?? 0;
        internal long MeasurementSourceChecksForTesting => _session?.GeometrySourceChecksForTesting ?? 0;
        internal long MeasurementSourceScansForTesting => _session?.GeometrySourceScansForTesting ?? 0;
        internal int MeasurementEntryCountForTesting => _session?.GeometryEntryCountForTesting ?? 0;
        internal long OutputReconstructionVisitsForTesting { get; private set; }
        internal long OutputPublicationItemsForTesting { get; private set; }
        internal void ResetCaptureDiagnosticsForTesting()
        {
            CaptureValidationPassesForTesting = CaptureValidationVisitsForTesting = CaptureMeasurementSourceVisitsForTesting =
                OutputReconstructionVisitsForTesting = OutputPublicationItemsForTesting = 0;
            _session?.ResetGeometryDiagnosticsForTesting();
        }
        private void CountOutputVisits(long visits)
        { if (CaptureDiagnosticsForTesting) OutputReconstructionVisitsForTesting += visits; }
        private long _outputRequest;
        private PanelQueryOptions? _outputOptions;
        private bool _outputPending;
        private PanelQueryOptions _savedOptions = new PanelQueryOptions();
        private bool _restoringView;
        // Tree tools and Reset change what each address holds; their recompute
        // starts at the top instead of keeping the scroll position.
        private bool _discardViewRestore;
        // A live search edit previews the view only. Wire outputs, document
        // modification and downstream solutions wait for Enter.
        internal bool QueryPreviewActive { get; private set; }
        private PanelQueryOptions? _queryPreviewOptions;
        internal int PreviewSize => CanvasPanelSize.PresetIndex(CanvasWidth, CanvasHeight);
        internal float CanvasWidth { get; private set; } = CanvasPanelSize.DefaultWidth;
        internal float CanvasHeight { get; private set; } = CanvasPanelSize.DefaultHeight;
        internal CanvasPanelDisplay CanvasDisplay { get; private set; }
        internal bool PanelMinimized { get; private set; }
        internal bool PanelSplit { get; private set; }
        internal PanelPreviewMode PreviewMode { get; private set; }
        internal bool ShowItemOrder { get; private set; }
        internal string PreviewStatus => _highlight.Status;
        internal float DisplayHeight => CanvasPanelSize.DisplayHeight(CanvasHeight, CanvasDisplay);
        internal float DisplayWidth => CanvasWidth * (PanelSplit ? 2 : 1);
        internal int TrimDepth { get; private set; }
        internal bool FlattenTree { get; private set; }
        internal bool ReverseTree { get; private set; }
        internal bool SimplifyTree { get; private set; }
        internal bool CreateSetTree { get; private set; }
        internal bool HasTreeOperations => TrimDepth > 0 || FlattenTree || ReverseTree || SimplifyTree || CreateSetTree;
        internal PanelUnitConversion UnitConversion { get; private set; }
        internal int DecimalPlaces { get; private set; } = PanelUnits.AutomaticDecimals;
        internal double RoundUpFactor { get; private set; }

        public BetterPanelComponent() : base("Panel+", "Panel+",
            "Inspect and manipulate data trees on the canvas. Tree tools, units and filters change output. Create set emits unique displayed numbers or text at the displayed precision; otherwise output retains original types and precision. Sorting changes view order.",
            "Params", "Util")
        {
            _highlight.PreviewChanged = () =>
            {
                InvalidatePreviewCaches();
                Instances.ActiveCanvas?.Invalidate();
                Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
            };
            RowSelection.Changed += (_, __) =>
            {
                ++_selectionVersion;
                if (!QueryPreviewActive) ScheduleOutput();
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
                    _session.GeometryDiagnosticsForTesting = CaptureDiagnosticsForTesting;
                    _session.SetDecimalPlacesResolver(() => DecimalPlaces);
                    _session.SetCreateSetResolver(() => CreateSetTree);
                    if (Locked) _session.InvalidateInput(false, suspended: true);
                    _session.SetGeometryPreparationResolver(PrepareCapturedGeometry);
                    _session.SetGeometryRetentionResolver(PrepareCapturedGeometryForRetention);
                    _session.SetGeometrySourceCurrentResolver(MeasurementSourceCurrent);
                    _session.SetCaptureCurrentResolver(CapturedSnapshotCurrent);
                    _savedOptions.DecimalPlaces = DecimalPlaces;
                    _session.UpdateQuery(_savedOptions);
                    _session.QueryChanged += (_, __) =>
                    {
                        (Attributes as BetterPanelAttributes)?.OnQueryStarted();
                        (Attributes as BetterPanelAttributes)?.InvalidateStatistics();
                        CancelPendingCopy();
                        ClearHighlight();
                        if (!_restoringView && !QueryPreviewActive)
                        {
                            var document = OnPingDocument();
                            document?.Modified();
                            ScheduleOutput();
                        }
                    };
                    _session.ResultChanged += (_, __) =>
                    {
                        (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
                        if (!_solving && !_restoringView && !QueryPreviewActive) ScheduleOutput();
                    };
                    (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
                }
                return _session;
            }
        }

        private PreparedMeasurement<PanelGeometryInfo>? PrepareCapturedGeometry(int position)
            => PrepareCapturedGeometryCore(position, false);

        private PreparedMeasurement<PanelGeometryInfo>? PrepareCapturedGeometryForRetention(int position)
            => PrepareCapturedGeometryCore(position, true);

        private PreparedMeasurement<PanelGeometryInfo>? PrepareCapturedGeometryCore(int position, bool proofOnly)
        {
            var snapshot = _measurementSnapshot;
            if (snapshot == null || !ReferenceEquals(_session?.Snapshot, snapshot) || !MeasurementSourceCurrent(snapshot, position)) return null;
            var capture = _measurementSources[position];
            if (capture == null || capture.Geometry == null || !capture.Leaf.Trusted || !capture.Leaf.HasReference) return null;
            var source = capture.Geometry;
            try
            {
                var prepared = proofOnly
                    ? GeometryHints.PrepareCapturedMeasurement(source, _geometryUnits, (int)_geometryUnitSystem, _geometryTolerance, true)
                    : GeometryHints.PrepareStandaloneMeasurement(_sourceItems[position]!, source, capture.Reference,
                        _geometryUnits, (int)_geometryUnitSystem, _geometryTolerance, true);
                if (MeasurementSourceCurrent(snapshot, position)) return prepared;
                prepared?.Dispose();
                return null;
            }
            finally { GC.KeepAlive(source); }
        }

        private bool MeasurementSourceCurrent(PanelSnapshot snapshot, int position)
        {
            if (!ReferenceEquals(snapshot, _measurementSnapshot) || position < 0 || position >= _measurementSources.Length ||
                position >= _sourceItems.Length) return false;
            var context = _geometryContext;
            if (ContextFenced && !GeometryContextCurrent(context))
            {
                RecaptureStaleMeasurementSource(snapshot);
                return false;
            }
            var capture = _measurementSources[position];
            if (capture == null) return true;
            var source = capture.Geometry;
            var holder = _sourceItems[position];
            try
            {
                if (SafeGooInspection.IsCurrentCapturedGeometry(holder, capture.Leaf) &&
                    (!ContextFenced || (context.Equals(_geometryContext) && GeometryContextCurrent(context))) &&
                    ReferenceEquals(snapshot, _measurementSnapshot) && ReferenceEquals(snapshot, _session?.Snapshot) &&
                    position < _measurementSources.Length && position < _sourceItems.Length &&
                    ReferenceEquals(capture, _measurementSources[position]) && ReferenceEquals(holder, _sourceItems[position])) return true;
            }
            catch (Exception) { /* The captured holder is no longer usable. */ }
            finally { GC.KeepAlive(source); }
            RecaptureStaleMeasurementSource(snapshot);
            return false;
        }

        private void RecaptureStaleMeasurementSource(PanelSnapshot snapshot)
        {
            if (ReferenceEquals(_staleMeasurementSnapshot, snapshot)) return;
            _staleMeasurementSnapshot = snapshot;
            long generation = _solveGeneration;
            // Avoid invalidating the queue reentrantly from its preparation or read.
            _ = Task.Run(() => Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (generation != _solveGeneration || !ReferenceEquals(_measurementSnapshot, snapshot) ||
                    !ReferenceEquals(_session?.Snapshot, snapshot) || _session == null) return;
                InvalidateStaleCapture(_session, OnPingDocument(), generation);
            })));
        }

        internal bool CapturedSnapshotCurrent(PanelSnapshot snapshot)
        {
            long generation = _solveGeneration;
            var session = _session;
            var tree = _queryTree;
            var context = _geometryContext;
            if (session == null || session.IsDisposed || Locked || !ReferenceEquals(snapshot, _measurementSnapshot) ||
                !ReferenceEquals(snapshot, session.Snapshot) || !session.GeometrySnapshotAttached(snapshot) || tree == null) return false;
            long visits = 0;
            bool HoldersCurrent(IGH_Goo?[] holders, CapturedMeasurementSource?[] sources)
            {
                bool result = SnapshotCapture.MeasurementSourcesCurrentWithVisits(holders, sources, out int count);
                if (CaptureDiagnosticsForTesting) CaptureMeasurementSourceVisitsForTesting += count;
                visits += count; return result;
            }
            bool GraphsCurrent(CaptureGraphWitness[] graphs)
            {
                bool result = SnapshotCapture.GraphSourcesCurrentWithVisits(graphs, out long count);
                visits += count; return result;
            }
            bool ManagedCurrent(PanelSnapshot captured, IGH_Goo?[] holders)
            {
                bool result = SnapshotCapture.ManagedRowsCurrentWithVisits(captured, holders, out int count);
                visits += count; return result;
            }
            bool UnitsCurrent()
            {
                bool result = PanelUnitOperations.InputsStillMatchWithVisits(_preConversionTree, tree, snapshot.UnitConversion, out int count, RoundUpFactor);
                visits += count; return result;
            }
            bool StructuresCurrent()
            {
                bool result = PanelTreeOperations.StructureStillMatchesWithVisits(_liveInputTree, _inputTree, out int count);
                visits += count;
                if (!result || PairedInputPresent() != _pairedInputPresent) return false;
                if (!_pairedInputPresent) return true;
                result = PanelTreeOperations.StructureStillMatchesWithVisits(_livePairedTree, _pairedInputTree, out count);
                visits += count; return result;
            }
            bool current = (!ContextFenced || GeometryContextCurrent(context)) &&
                HoldersCurrent(_sourceItems, _measurementSources) && GraphsCurrent(_captureGraphs) && ManagedCurrent(snapshot, _sourceItems) &&
                (PairedSnapshot == null || HoldersCurrent(_pairedSourceItems, _pairedMeasurementSources) &&
                    GraphsCurrent(_pairedCaptureGraphs) && ManagedCurrent(PairedSnapshot, _pairedSourceItems)) &&
                UnitsCurrent() && StructuresCurrent() &&
                (!ContextFenced || (context.Equals(_geometryContext) && GeometryContextCurrent(context)));
            if (CaptureDiagnosticsForTesting) { CaptureValidationPassesForTesting++; CaptureValidationVisitsForTesting += visits; }
            if (!current && generation == _solveGeneration && ReferenceEquals(snapshot, _measurementSnapshot))
                RecaptureStaleMeasurementSource(snapshot);
            return current && generation == _solveGeneration && ReferenceEquals(session, _session) && !session.IsDisposed &&
                !Locked && ReferenceEquals(snapshot, _measurementSnapshot) && ReferenceEquals(snapshot, session.Snapshot) &&
                session.GeometrySnapshotAttached(snapshot) && ReferenceEquals(tree, _queryTree);
        }

        private static MeasurementContextStamp ReadGeometryContext(out Rhino.UnitSystem unitSystem, out double tolerance, out string units)
        {
            var document = Rhino.RhinoDoc.ActiveDoc;
            unitSystem = document?.ModelUnitSystem ?? Rhino.UnitSystem.None;
            tolerance = document?.ModelAbsoluteTolerance ?? 0.001;
            units = document == null || unitSystem == Rhino.UnitSystem.None ? "units" : document.GetUnitSystemName(true, false, false, true);
            if (string.IsNullOrWhiteSpace(units)) units = "units";
            return new MeasurementContextStamp(document?.RuntimeSerialNumber ?? 0, (int)unitSystem, tolerance, units);
        }

        private static bool GeometryContextCurrent(MeasurementContextStamp captured)
        {
            try { return captured.Equals(ReadGeometryContext(out _, out _, out _)); }
            catch (Exception) { return false; }
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

        internal void BeginQueryPreview()
        {
            _queryPreviewOptions = Session.Options;
            _queryPreviewSelection = _committedSelection.CopyForPreview();
            QueryPreviewActive = true;
            (Attributes as BetterPanelAttributes)?.UseRowSelection(RowSelection);
        }

        internal void RecordQueryUndo(string name, PanelQueryOptions before, PanelQueryOptions after)
        {
            if (!PanelQueryPolicy.SameQuery(before, after))
                RecordUndoEvent(name, new QueryUndo(InstanceGuid, before, after));
        }

        private sealed class QueryUndo : GH_UndoAction
        {
            private readonly Guid _component;
            private readonly PanelQueryOptions _before, _after;
            internal QueryUndo(Guid component, PanelQueryOptions before, PanelQueryOptions after)
            { _component = component; _before = before.Clone(); _after = after.Clone(); }
            public override bool ExpiresSolution => false;
            public override bool ExpiresDisplay => false;
            protected override void Internal_Undo(GH_Document doc) => Apply(doc, _before);
            protected override void Internal_Redo(GH_Document doc) => Apply(doc, _after);
            private void Apply(GH_Document doc, PanelQueryOptions options)
            {
                var component = doc.FindObject(_component, false) as BetterPanelComponent;
                if (component == null) return;
                (component.Attributes as BetterPanelAttributes)?.CancelQueryEditor();
                component.Session.UpdateQuery(options);
            }
        }

        // Ends a live edit. A committed edit publishes like any other query change;
        // an abandoned one already restored the original query, so outputs and the
        // document never changed.
        internal void EndQueryPreview(bool committed)
        {
            if (!QueryPreviewActive) return;
            _queryPreviewSelection = null;
            _queryPreviewOptions = null;
            QueryPreviewActive = false;
            (Attributes as BetterPanelAttributes)?.UseRowSelection(RowSelection);
            (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
            if (committed) OnPingDocument()?.Modified();
            ScheduleOutput();
        }

        internal bool SelectionHasGeometry(IReadOnlyList<int> positions)
        {
            var source = _sourceItems;
            var budget = new PanelGeometrySources.Budget();
            foreach (int position in positions)
            {
                if (position < 0 || position >= source.Length) continue;
                var item = source[position];
                if (item is GH_ObjectWrapper)
                {
                    using var inspected = PanelGeometrySources.Inspect(item, budget, allowStandalonePreview: true);
                    // Uncertain wrappers stay available; the action performs its
                    // full freshness and capability checks when invoked.
                    if (inspected.Unavailable || inspected.Leaves.Count > 0) return true;
                }
                else if (item is IGH_GeometricGoo || item is IGH_PreviewData) return true;
            }
            return false;
        }

        internal void ResetPanel()
        {
            _discardViewRestore = true;
            var options = Session.Options;
            bool outputChanged = HasTreeOperations || UnitConversion != PanelUnitConversion.None || RoundUpFactor > 0;
            bool settingsChanged = outputChanged || OutputScope != PanelOutputScope.Filtered || DecimalPlaces != PanelUnits.AutomaticDecimals ||
                !string.IsNullOrEmpty(options.Search) || options.SearchMode != PanelSearchMode.Text || options.TypeName != null || options.BranchPath != null ||
                options.BranchPrefix || options.OnlyInvalid || options.SortMode != PanelSortMode.SourceOrder;
            if (settingsChanged) RecordUndoEvent("Reset Panel+");
            TrimDepth = 0;
            FlattenTree = ReverseTree = SimplifyTree = CreateSetTree = false;
            UnitConversion = PanelUnitConversion.None;
            RoundUpFactor = 0;
            DecimalPlaces = PanelUnits.AutomaticDecimals;
            OutputScope = PanelOutputScope.Filtered;
            ResetPanelView();
            if (settingsChanged) OnPingDocument()?.Modified();
            try
            {
                if (outputChanged) ExpireSolution(true);
                else ScheduleOutput();
            }
            finally { _discardViewRestore = false; }
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetCanvasSize(float width, float height)
        {
            (Attributes as BetterPanelAttributes)?.InvalidatePaintedHits();
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
            else Session.UpdateQuery(Session.Options);
            OnPingDocument()?.Modified();
            _discardViewRestore = true;
            try { ExpireSolution(true); }
            finally { _discardViewRestore = false; }
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetCanvasDisplay(CanvasPanelDisplay display)
        {
            if (!Enum.IsDefined(typeof(CanvasPanelDisplay), display) || CanvasDisplay == display) return;
            (Attributes as BetterPanelAttributes)?.InvalidatePaintedHits();
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

        internal void SetPanelSplit(bool split)
        {
            if (PanelSplit == split || split && !CanSplit) return;
            RecordUndoEvent("Panel+ D | F view", new SplitUndo(InstanceGuid, PanelSplit, split));
            ApplyPanelSplit(split);
        }

        private void ApplyPanelSplit(bool split)
        {
            CanvasTextInput.CloseActive(this);
            (Attributes as BetterPanelAttributes)?.ClosePresentationControls();
            PanelSplit = split;
            if (split && PairedSnapshot == null && _pairedQueryTree != null)
            {
                PairedSnapshot = SnapshotCapture.CaptureForMeasurements(_pairedQueryTree, out _pairedSourceItems,
                    out _pairedMeasurementSources, out _pairedCaptureGraphs, PanelUnitConversion.None, _geometryUnits, null, null, null, null, null);
                _pairedHasGeometry = HasGeometry(PairedSnapshot);
                // F geometry was labelled with the units of the last solve. If the document
                // units changed since, recompute now rather than show stale measurements.
                if (_pairedHasGeometry && !GeometryContextCurrent(_geometryContext) && _session != null)
                    InvalidateStaleCapture(_session, OnPingDocument(), _solveGeneration);
            }
            else if (!split)
            {
                // A closed split stops paying for F presentation and its freshness checks.
                PairedSnapshot = null;
                _pairedSourceItems = Array.Empty<IGH_Goo?>();
                _pairedMeasurementSources = Array.Empty<CapturedMeasurementSource?>();
                _pairedCaptureGraphs = Array.Empty<CaptureGraphWitness>();
                (Attributes as BetterPanelAttributes)?.ClearPaneSummary();
            }
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        private static bool HasGeometry(PanelSnapshot snapshot) =>
            snapshot.Rows.Any(row => row.GeometryInfo != null || row.Components != null);

        private sealed class SplitUndo : GH_UndoAction
        {
            private readonly Guid _component;
            private readonly bool _before, _after;
            internal SplitUndo(Guid component, bool before, bool after)
            { _component = component; _before = before; _after = after; }
            public override bool ExpiresSolution => false;
            public override bool ExpiresDisplay => false;
            protected override void Internal_Undo(GH_Document doc) => Apply(doc, _before);
            protected override void Internal_Redo(GH_Document doc) => Apply(doc, _after);
            private void Apply(GH_Document doc, bool split) =>
                (doc.FindObject(_component, false) as BetterPanelComponent)?.ApplyPanelSplit(split);
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

        internal void SetUnits(PanelUnitConversion conversion, int decimals, double? roundUpFactor = null)
        {
            if (!Enum.IsDefined(typeof(PanelUnitConversion), conversion)) conversion = PanelUnitConversion.None;
            decimals = Math.Max(PanelUnits.AutomaticDecimals, Math.Min(PanelUnits.MaximumDecimals, decimals));
            double factor = roundUpFactor ?? RoundUpFactor;
            if (!PanelUnits.IsRoundingFactor(factor)) factor = 0;
            bool conversionChanged = UnitConversion != conversion || RoundUpFactor != factor;
            if (!conversionChanged && DecimalPlaces == decimals) return;
            CancelPendingCopy();
            RecordUndoEvent("Panel+ units, rounding and decimals");
            UnitConversion = conversion;
            RoundUpFactor = factor;
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
            if (ReferenceEquals(snapshot, Session.Result.Snapshot) && Session.Result.DisplaySet?.Values.TryGetValue(sourcePosition, out var projected) == true)
                return projected.Text;
            var geometry = Session.GetGeometryDetails(snapshot, sourcePosition);
            if (geometry == null)
                geometry = Session.HasGeometryMeasurement(snapshot, sourcePosition)
                    ? row.GeometryInfo?.WithUnavailableMeasurement() : row.GeometryInfo;
            var options = Session.Options;
            if (options.DisplayFilterValue)
                return PanelFilterValue.Format(snapshot, row, options.SearchMode, DecimalPlaces, geometry);
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
            if (Session.Result.DisplaySet?.Values.TryGetValue(position, out var projected) == true) return projected.CopyText;
            var options = Session.Options;
            if (options.DisplayFilterValue)
            {
                var metric = Session.GetGeometryDetails(snapshot, position);
                if (metric == null && Session.HasGeometryMeasurement(snapshot, position)) metric = row.GeometryInfo?.WithUnavailableMeasurement();
                var value = PanelFilterValue.Format(snapshot, row, options.SearchMode, DecimalPlaces, metric, includeUnits: false);
                bool literalProperty = PanelProperties.IsProperty(options.SearchMode) &&
                    PanelProperties.TryGet(row, options.SearchMode, metric, out var property) && property != null && property.Kind != PanelPropertyKind.Number;
                if (!literalProperty && (value == "measuring…" || value == "unavailable")) throw new InvalidOperationException("A selected measurement is unavailable.");
                return value;
            }
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

        private bool TryGetActionPositions(PanelSnapshot snapshot, IReadOnlyList<int> positions, out IReadOnlyList<int> ordered)
        {
            ordered = Array.Empty<int>();
            var session = _session;
            if (session == null || !session.HasCurrentResult || !ReferenceEquals(snapshot, session.Snapshot) || positions.Count == 0) return false;
            return PanelActionPositions.TryGet(session.Result, _sourceItems.Length, RowSelection, positions, out ordered);
        }

        internal string? FrameRows(PanelSnapshot snapshot, IReadOnlyList<int> positions)
            => FrameRowsWithCompletion(snapshot, positions, null);

        internal string? FrameRowsWithCompletion(PanelSnapshot snapshot, IReadOnlyList<int> positions, Action<string>? completed)
        {
            if (_previewSuspended) return "Activate this Grasshopper document to frame geometry";
            if (!TryGetActionPositions(snapshot, positions, out var ordered)) return "Data changed; select rows again";
            var session = _session!;
            var expectedResult = session.Result;
            var source = _sourceItems;
            long selection = _selectionVersion;
            bool Current() => !_previewSuspended && ReferenceEquals(_session, session) && session.HasCurrentResult &&
                ReferenceEquals(session.Snapshot, snapshot) && ReferenceEquals(session.Result, expectedResult) &&
                ReferenceEquals(_sourceItems, source) && _selectionVersion == selection;
            string? Format(PanelGeometryActionResult result) => !result.Success ? result.Error :
                result.Skipped > 0 || result.Exhausted
                ? "Selection framed" + (result.Skipped > 0 ? "; " + result.Skipped + " skipped" : "") +
                    (result.Exhausted ? "; inspection limit reached" : "") : null;
            var result = PanelGeometryActions.FrameWithSnapshot(ordered.Select(position => source[position]).ToArray(), Current,
                () => Current() && CapturedSnapshotCurrent(snapshot) && Current(),
                value => completed?.Invoke(Format(value) ?? "Selection framed"));
            return Format(result);
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
            IReadOnlyList<int> ordered = Array.Empty<int>();
            if (positions.Count > 0 && !TryGetActionPositions(snapshot, positions, out ordered))
            {
                ClearHighlight();
                return;
            }
            var previewPositions = PreviewMode == PanelPreviewMode.FilteredResults ? session.Result.RowPositions : ordered;
            _highlight.SetOrderResult(ShowItemOrder && previewPositions.Count > 0 ? session.Result : null);
            var source = _sourceItems;
            var expectedResult = session.Result;
            var mode = PreviewMode;
            long selection = _selectionVersion;
            bool Current() => !_previewSuspended && !Locked && PreviewMode == mode &&
                ReferenceEquals(_session, session) && session.HasCurrentResult && ReferenceEquals(session.Snapshot, snapshot) &&
                ReferenceEquals(session.Result, expectedResult) && ReferenceEquals(_sourceItems, source) && _selectionVersion == selection;
            _highlight.SetScopeChecked(source, expectedResult, mode == PanelPreviewMode.FilteredResults, ordered, Current, RowSelection);
            InvalidatePreviewCaches();
            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void ClearHighlight()
        {
            bool visible = _highlight.PreviewCount > 0;
            _highlight.Clear();
            if (visible) { InvalidatePreviewCaches(); Rhino.RhinoDoc.ActiveDoc?.Views.Redraw(); }
        }

        private void InvalidatePreviewCaches()
        {
            // GH's registered PreviewExpired handler also destroys the document
            // draw list and clipping box. Calling GH_Document.ExpirePreview here
            // would additionally mark a selection-only gesture as a document edit.
            ExpirePreview(false);
        }

        internal Task<string> CopyValuesAsync(PanelSnapshot snapshot, IReadOnlyList<int> positions) => CopyValuesCoreAsync(snapshot, positions, false);

        internal Task<string> CopyPairedValuesAsync(PanelSnapshot snapshot, IReadOnlyList<int> positions) => CopyValuesCoreAsync(snapshot, positions, true);

        private async Task<string> CopyValuesCoreAsync(PanelSnapshot snapshot, IReadOnlyList<int> positions, bool paired)
        {
            CancelPendingCopy();
            if (!TryGetActionPositions(snapshot, positions, out var ordered)) return "Data changed; select rows again";
            var pairedSnapshot = paired ? PairedSnapshot : null;
            if (paired && (!CanSplit || pairedSnapshot == null)) return "Data changed; select rows again";
            long clipboardGeneration = PanelGeometryActions.BeginClipboard();
            var session = _session!;
            var result = session.Result;
            var source = _sourceItems;
            long selectionVersion = RowSelection.Version;
            var cancellation = new CancellationTokenSource();
            _copyCancellation = cancellation;
            var measurementPositions = session.Options.DisplayFilterValue && PanelProperties.IsProperty(session.Options.SearchMode)
                ? ordered.Where(position => PanelProperties.TryGet(snapshot.Rows[position], session.Options.SearchMode,
                    session.GetGeometryDetails(snapshot, position), out var property) && property == null).ToArray() : ordered;
            bool measured = paired || await session.MeasureGeometryAsync(snapshot, measurementPositions, cancellation.Token);
            var completion = new TaskCompletionSource<string>();
            // Rhino does not guarantee a SynchronizationContext for every canvas
            // callback. Live goo and the system clipboard belong to its UI thread.
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                void Finish(PanelCommitOutcome outcome)
                {
                    string status = outcome == PanelCommitOutcome.Uncertain ? "Copy failed; clipboard state is uncertain." :
                        outcome == PanelCommitOutcome.Committed && !PanelGeometryActions.ClipboardCurrent(clipboardGeneration)
                            ? "Copy context changed during clipboard publication; clipboard state is uncertain." :
                        outcome != PanelCommitOutcome.Committed ? "Copy cancelled" :
                        ordered.Count == 1 ? "Value copied" : ordered.Count + " values copied";
                    completion.TrySetResult(status);
                    if (ReferenceEquals(_copyCancellation, cancellation)) { _copyCancellation = null; cancellation.Dispose(); }
                }
                try
                {
                    bool SelectionCurrent() => PanelGeometryActions.ClipboardCurrent(clipboardGeneration) &&
                        ReferenceEquals(_copyCancellation, cancellation) && !cancellation.IsCancellationRequested &&
                        ReferenceEquals(_session, session) && session.HasCurrentResult &&
                        ReferenceEquals(session.Snapshot, snapshot) && ReferenceEquals(session.Result, result) &&
                        ReferenceEquals(_sourceItems, source) &&
                        (!paired || CanSplit && ReferenceEquals(PairedSnapshot, pairedSnapshot)) &&
                        PanelActionPositions.IsCurrent(result, source.Length, RowSelection, ordered, selectionVersion);
                    if (!measured || !SelectionCurrent()) Finish(PanelCommitOutcome.Stale);
                    else
                    {
                        var text = string.Join(Environment.NewLine, ordered.Select(position => paired
                            ? BetterPanelAttributes.PairedDisplayValue(pairedSnapshot!.Rows[position]) : CopyValue(snapshot, position)));
                        // Measurements cover only queued geometry; value-only
                        // rows and unmeasured geometry can change too.
                        bool Current() => SelectionCurrent() && CapturedSnapshotCurrent(snapshot) && SelectionCurrent() &&
                            session.GeometrySourcesCurrent(snapshot) && SelectionCurrent();
                        var state = PanelGeometryActions.CommitClipboard(Current,
                            () => { using (var clipboard = new Eto.Forms.Clipboard()) clipboard.Text = text; }, Finish);
                        if (state != PanelCommitOutcome.Deferred) Finish(state);
                    }
                }
                catch (Exception ex)
                {
                    completion.TrySetResult("Copy failed: " + ex.Message);
                    if (ReferenceEquals(_copyCancellation, cancellation)) { _copyCancellation = null; cancellation.Dispose(); }
                }
            }));
            return await completion.Task;
        }

        internal bool TryCopyGeometry(PanelSnapshot snapshot, IReadOnlyList<int> positions, out string status)
            => TryCopyGeometryWithCompletion(snapshot, positions, null, out status);

        internal bool TryCopyGeometryWithCompletion(PanelSnapshot snapshot, IReadOnlyList<int> positions,
            Action<string>? completed, out string status)
        {
            CancelPendingCopy();
            if (!TryGetActionPositions(snapshot, positions, out var ordered))
            {
                status = "Data changed; select rows again";
                return false;
            }
            var session = _session!;
            var expectedResult = session.Result;
            var source = _sourceItems;
            long selection = _selectionVersion;
            bool Current() => ReferenceEquals(_session, session) && session.HasCurrentResult &&
                ReferenceEquals(session.Snapshot, snapshot) && ReferenceEquals(session.Result, expectedResult) &&
                ReferenceEquals(_sourceItems, source) && _selectionVersion == selection;
            string Format(PanelGeometryActionResult result) => result.Success ? result.Count + " geometries copied" +
                (result.Skipped > 0 ? "; " + result.Skipped + " skipped (" + (result.Detail ?? "unsupported geometry or unverified metadata") + ")" : "") +
                (result.Exhausted ? "; preparation limit reached" : "") +
                (result.UnverifiedMetadata ? "; custom metadata content not fully verified" : "") : result.Error ?? "Copy geometry failed";
            var result = PanelGeometryActions.CopyGeometryWithSnapshot(ordered.Select(position => source[position]).ToArray(),
                _geometryUnitSystem, _geometryTolerance, Current, () => Current() && CapturedSnapshotCurrent(snapshot) && Current(),
                value => completed?.Invoke(Format(value)));
            status = Format(result);
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
            p.AddGenericParameter("Data", "D", "Matching items after Simplify → Trim → Flatten → Reverse → unit conversion → filters. Create set outputs unique displayed numbers or text at the displayed precision. Otherwise output keeps original types and full precision. Sorting changes only view order.", GH_ParamAccess.tree);
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
            if (!_outputPending && _outputOptions != null && PanelQueryPolicy.SameOutputFilter(_outputOptions, options) &&
                _publishedSelectionVersion == _selectionVersion && _publishedScope == OutputScope) return;
            var snapshot = session.Snapshot;
            long request = ++_outputRequest;
            // Never solve recursively from a query callback. Publish through a
            // normal GH solution so downstream components collect the new tree.
            document.ScheduleSolution(1, _ =>
            {
                if (request != _outputRequest || !ReferenceEquals(document, OnPingDocument()) ||
                    !ReferenceEquals(session, _session) || !ReferenceEquals(snapshot, session.Snapshot) || _queryTree == null || QueryPreviewActive) return;
                _preserveQuery = true;
                _reuseQuery = true;
                try { ExpireSolution(false); }
                finally { _preserveQuery = false; }
            });
        }

        public override void ClearData()
        {
            ++_solveGeneration;
            if (!_issuingCaptureRetry) _captureRetries = 0;
            // Grasshopper expires the component before collecting its next input.
            // Invalidate now, even when solving is deferred or an upstream node fails.
            if (!_preserveQuery)
            {
                ++_outputRequest;
                _queryTree = null;
                ClearOutputSources();
                if (!_retainGeometryMeasurements)
                {
                    _sourceItems = Array.Empty<IGH_Goo?>();
                    _measurementSources = Array.Empty<CapturedMeasurementSource?>();
                    _measurementSnapshot = null;
                    _staleMeasurementSnapshot = null;
                }
                _reuseQuery = false;
                CancelPendingCopy();
                ClearHighlight();
                // An upstream recompute keeps the scroll position when the new tree
                // has exactly the same paths and lengths. Selection always clears.
                var attributes = Attributes as BetterPanelAttributes;
                if (!_discardViewRestore) attributes?.StashViewForRestore();
                attributes?.ResetViewState(keepViewRestore: !_discardViewRestore);
                RowSelection.Clear();
                _session?.InvalidateInput(_retainGeometryMeasurements, suspended: Locked);
            }
            base.ClearData();
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            long generation = ++_solveGeneration;
            var document = OnPingDocument();
            bool previouslySolving = _solving;
            bool rescheduleOutput = false;
            _solving = true;
            try
            {
                if (Locked || !da.GetDataTree(0, out GH_Structure<IGH_Goo> tree)) tree = new GH_Structure<IGH_Goo>();
                if (Locked || generation != _solveGeneration || !ReferenceEquals(document, OnPingDocument())) return;
                var session = Session;
                bool Current() => generation == _solveGeneration && !Locked && ReferenceEquals(document, OnPingDocument()) &&
                    ReferenceEquals(session, _session) && !session.IsDisposed;
                if (!Current()) return;
                // GH may duplicate input goo during collection even when sources
                // are unchanged. A publication solve reuses the committed snapshot.
                bool reuse = _reuseQuery && _queryTree != null;
                _reuseQuery = false;
                if (!reuse)
                {
                    CancelPendingCopy();
                    ClearHighlight();
                    var options = session.Options;
                    if (options.DecimalPlaces != DecimalPlaces)
                    {
                        options.DecimalPlaces = DecimalPlaces;
                        session.UpdateQuery(options);
                    }
                    if (!Current()) return;
                    var liveInputTree = tree;
                    var inputTree = PanelTreeOperations.CopyStructure(liveInputTree);
                    tree = PanelTreeOperations.ApplyWithSources(tree, TrimDepth, SimplifyTree, FlattenTree, ReverseTree, out var retainedSources);
                    var preConversionTree = tree;
                    var conversion = UnitConversion;
                    var roundUpFactor = RoundUpFactor;
                    var scalarInputs = CapturedScalar.Capture(preConversionTree);
                    var queryScalars = CapturedScalar.Convert(scalarInputs, conversion, roundUpFactor);
                    var queryTree = PanelTreeOperations.CopyStructure(PanelUnitOperations.ApplyCaptured(preConversionTree, conversion, scalarInputs, roundUpFactor));
                    var inputSeed = SnapshotCapture.PrepareInputs(queryTree);
                    CapturePairedInput(da, inputTree, queryTree, retainedSources, out var pairedTree, out var pairedError,
                        out var livePairedTree, out var capturedPairedTree, out bool pairedPresent);
                    var context = ReadGeometryContext(out var unitSystem, out double tolerance, out string units);
                    if (!Current()) return;
                    var snapshot = SnapshotCapture.CaptureForMeasurements(queryTree, out var sources, out var measurementSources, out var graphs,
                        conversion, units, retainedSources, CaptureObserverForTesting, GraphVisitObserverForTesting, queryScalars, inputSeed);
                    var pairedSources = Array.Empty<IGH_Goo?>();
                    var pairedMeasurements = Array.Empty<CapturedMeasurementSource?>();
                    var pairedGraphs = Array.Empty<CaptureGraphWitness>();
                    var pairedSnapshot = PanelSplit && pairedTree != null ? SnapshotCapture.CaptureForMeasurements(pairedTree,
                        out pairedSources, out pairedMeasurements, out pairedGraphs, PanelUnitConversion.None, units, null, null, null, null, null) : null;
                    StructureObserverForTesting?.Invoke(liveInputTree, livePairedTree);
                    if (!Current()) return;
                    bool hasGeometry = HasGeometry(snapshot) || pairedSnapshot != null && HasGeometry(pairedSnapshot);
                    if (!SnapshotCapture.MeasurementSourcesCurrent(sources, measurementSources) || !SnapshotCapture.GraphSourcesCurrent(graphs) ||
                        !SnapshotCapture.ManagedRowsCurrent(snapshot, sources) || !PanelUnitOperations.InputsStillMatch(preConversionTree, queryTree, conversion, roundUpFactor) ||
                        (pairedSnapshot != null && (!SnapshotCapture.MeasurementSourcesCurrent(pairedSources, pairedMeasurements) ||
                            !SnapshotCapture.GraphSourcesCurrent(pairedGraphs) || !SnapshotCapture.ManagedRowsCurrent(pairedSnapshot, pairedSources))) ||
                        !PanelTreeOperations.StructureStillMatches(liveInputTree, inputTree) || pairedPresent != PairedInputPresent() ||
                        (pairedPresent && !PanelTreeOperations.StructureStillMatches(livePairedTree, capturedPairedTree)) ||
                        (hasGeometry && !GeometryContextCurrent(context)))
                    {
                        if (Current()) InvalidateStaleCapture(session, document, generation);
                        return;
                    }
                    if (!Current()) return;
                    // All callback-bearing inspection has completed. Commit these
                    // locals together only while this solve still owns the component.
                    _inputTree = inputTree; _preConversionTree = preConversionTree; _queryTree = queryTree; _retainedSources = retainedSources;
                    _liveInputTree = liveInputTree; _livePairedTree = livePairedTree; _pairedInputTree = capturedPairedTree; _pairedInputPresent = pairedPresent;
                    _pairedQueryTree = pairedTree; _pairedError = pairedError;
                    PairedSnapshot = pairedSnapshot;
                    _pairedSourceItems = pairedSources; _pairedMeasurementSources = pairedMeasurements; _pairedCaptureGraphs = pairedGraphs;
                    bool retainContext = _geometryContext.Equals(context);
                    _geometryUnitSystem = unitSystem; _geometryTolerance = tolerance; _geometryUnits = units;
                    _geometryContext = context; _captureHasGeometry = HasGeometry(snapshot);
                    _pairedHasGeometry = pairedSnapshot != null && HasGeometry(pairedSnapshot);
                    _sourceItems = sources; _measurementSources = measurementSources; _captureGraphs = graphs;
                    _measurementSnapshot = snapshot;
                    _staleMeasurementSnapshot = null;
                    if (_retainGeometryMeasurements)
                        session.AttachConvertedSnapshot(snapshot, _ => retainContext);
                    else session.AttachSnapshot(snapshot);
                    if (!Current() || !ReferenceEquals(session.Snapshot, snapshot) || !ReferenceEquals(_queryTree, queryTree)) return;
                }
                var committedTree = _queryTree;
                var committedSnapshot = session.Snapshot;
                var committedResult = session.Result;
                var committedScope = OutputScope;
                var committedSelection = _selectionVersion;
                bool committedResultCurrent = session.HasCurrentResult;
                bool PublicationOwnerCurrent() => Current() && ReferenceEquals(committedTree, _queryTree) &&
                    ReferenceEquals(committedSnapshot, session.Snapshot) && ReferenceEquals(committedResult, session.Result) &&
                    committedScope == OutputScope && committedSelection == _selectionVersion &&
                    committedResultCurrent == session.HasCurrentResult;
                bool PublicationCurrent()
                {
                    if (!PublicationOwnerCurrent())
                    {
                        if (Current() && _queryTree != null) rescheduleOutput = true;
                        return false;
                    }
                    bool inputsCurrent = CapturedSnapshotCurrent(committedSnapshot);
                    bool ownerCurrent = PublicationOwnerCurrent();
                    if (!ownerCurrent && Current() && _queryTree != null) rescheduleOutput = true;
                    if (!inputsCurrent && ownerCurrent)
                        InvalidateStaleCapture(session, document, generation);
                    return inputsCurrent && ownerCurrent;
                }
                // Pending/failed filters expose no stale or unfiltered items. The
                // completed query schedules another solution using this same tree.
                if (!PublicationOwnerCurrent()) return;
                PublishOutputs(da, PublicationCurrent);
                if (PublicationOwnerCurrent())
                {
                    if (session.Error != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Filter: " + session.Error);
                }
            }
            finally
            {
                _solving = previouslySolving;
                if (rescheduleOutput && !previouslySolving) ScheduleOutput();
            }
        }

        private void InvalidateStaleCapture(PanelSession session, GH_Document? document, long generation)
        {
            if (generation != _solveGeneration || !ReferenceEquals(session, _session)) return;
            long invalidated = ++_solveGeneration;
            _queryTree = null;
            ClearOutputSources();
            _measurementSnapshot = null;
            _measurementSources = Array.Empty<CapturedMeasurementSource?>();
            _sourceItems = Array.Empty<IGH_Goo?>();
            bool retry = _captureRetries++ == 0 && document != null && !Locked;
            session.InvalidateInput(false, suspended: !retry);
            if (invalidated != _solveGeneration || !ReferenceEquals(session, _session)) return;
            if (!retry)
            {
                if (!Locked && document != null && _captureRetries > 1)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Input kept changing during capture; expire the source to retry.");
                return;
            }
            document!.ScheduleSolution(1, _ =>
            {
                if (invalidated != _solveGeneration || !ReferenceEquals(document, OnPingDocument()) || Locked) return;
                _issuingCaptureRetry = true;
                try { ExpireSolution(false); }
                finally { _issuingCaptureRetry = false; }
            });
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
            ++_solveGeneration;
            ++_outputRequest;
            _queryTree = null;
            ClearOutputSources();
            _reuseQuery = false;
            CancelPendingCopy();
            ClearHighlight();
            (Attributes as BetterPanelAttributes)?.ResetViewState();
            (Attributes as BetterPanelAttributes)?.ReleaseKeyboardFocus();
            RowSelection.Clear();
            CanvasTextInput.CloseActive();
            QueryPreviewActive = false;
            if (_session != null) _savedOptions = _session.Options;
            _session?.Dispose();
            _session = null;
            _sourceItems = Array.Empty<IGH_Goo?>();
            _measurementSources = Array.Empty<CapturedMeasurementSource?>();
            _measurementSnapshot = null;
            _staleMeasurementSnapshot = null;
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
            var options = _queryPreviewOptions ?? _session?.Options ?? _savedOptions;
            writer.SetString("PanelSearch", options.Search ?? "");
            writer.SetInt32("PanelSearchMode", (int)options.SearchMode);
            writer.SetBoolean("PanelDisplayFilterValue", options.DisplayFilterValue);
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
            writer.SetBoolean("PanelSplit", PanelSplit);
            writer.SetInt32("PanelPreviewMode", (int)PreviewMode);
            writer.SetBoolean("PanelShowItemOrder", ShowItemOrder);
            writer.SetInt32("PanelTrimDepth", TrimDepth);
            writer.SetBoolean("PanelFlatten", FlattenTree);
            writer.SetBoolean("PanelReverse", ReverseTree);
            writer.SetBoolean("PanelSimplify", SimplifyTree);
            writer.SetBoolean("PanelCreateSet", CreateSetTree);
            writer.SetInt32("PanelUnitConversion", (int)UnitConversion);
            writer.SetInt32("PanelDecimalPlaces", DecimalPlaces);
            writer.SetDouble("PanelRoundUpFactor", RoundUpFactor);
            writer.SetInt32("PanelOutputScope", (int)OutputScope);
            writer.SetInt32("PanelOutputSchema", 1);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            ++_solveGeneration;
            ++_outputRequest;
            _queryTree = null;
            _reuseQuery = false;
            ClearOutputSources();
            _sourceItems = Array.Empty<IGH_Goo?>();
            _measurementSources = Array.Empty<CapturedMeasurementSource?>();
            _measurementSnapshot = null;
            _staleMeasurementSnapshot = null;
            _session?.InvalidateInput(false, suspended: Locked);
            CancelPendingCopy();
            ClearHighlight();
            (Attributes as BetterPanelAttributes)?.ResetViewState();
            RowSelection.Clear();
            var search = ""; var type = ""; var branch = "";
            var prefix = false; var invalid = false; var sort = 0; var size = 1; var searchMode = 0;
            reader.TryGetString("PanelSearch", ref search);
            reader.TryGetInt32("PanelSearchMode", ref searchMode);
            bool displayFilterValue = false;
            reader.TryGetBoolean("PanelDisplayFilterValue", ref displayFilterValue);
            reader.TryGetString("PanelType", ref type);
            reader.TryGetString("PanelBranch", ref branch);
            reader.TryGetBoolean("PanelPrefix", ref prefix);
            reader.TryGetBoolean("PanelInvalid", ref invalid);
            reader.TryGetInt32("PanelSort", ref sort);
            bool hasLegacySize = reader.TryGetInt32("PanelSize", ref size);
            _savedOptions = new PanelQueryOptions { Search = search, TypeName = string.IsNullOrEmpty(type) ? null : type,
                DisplayFilterValue = displayFilterValue && searchMode >= (int)PanelSearchMode.Value && Enum.IsDefined(typeof(PanelSearchMode), searchMode),
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
            bool split = false;
            reader.TryGetBoolean("PanelSplit", ref split);
            PanelSplit = split;
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
            double roundUpFactor = 0;
            reader.TryGetDouble("PanelRoundUpFactor", ref roundUpFactor);
            RoundUpFactor = PanelUnits.IsRoundingFactor(roundUpFactor) ? roundUpFactor : 0;
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
