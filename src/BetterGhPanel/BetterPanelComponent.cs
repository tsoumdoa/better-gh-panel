using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;
using BetterGhPanel.UI;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel
{
    public sealed partial class BetterPanelComponent : GH_Component
    {
        private PanelSession? _session;
        private readonly PanelGeometryHighlight _highlight = new PanelGeometryHighlight();
        private bool _previewSuspended;
        private IGH_Goo?[] _sourceItems = Array.Empty<IGH_Goo?>();
        private CapturedScalar[] _scalarInputs = Array.Empty<CapturedScalar>(), _presentedScalars = Array.Empty<CapturedScalar>();
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
        private bool _completingInClearData;
        private long _solveGeneration;
        private bool _issuingCaptureRetry;
        private int _captureRetries;
        // Fixture injection runs at the same boundary as native capture callbacks.
        internal Action<int, IGH_Goo?>? CaptureObserverForTesting { get; set; }
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
        public BetterPanelComponent() : base("Panel+", "Panel+",
            "Inspect and manipulate data trees on the canvas. Choose Items or displayed Values for D/S. Create set groups every member in a branch per value, or emits unique displayed values. B/F and geometry actions include all set members. Sorting changes only view order.",
            "Params", "Util")
        {
            _highlight.PreviewChanged = () =>
            {
                InvalidatePreviewCaches();
                Instances.ActiveCanvas?.Invalidate();
                Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
            };
            UpdateOutputDescriptions();
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
                    _session.SetRoundUpFactorResolver(() => RoundUpFactor);
                    _session.SetCreateSetResolver(() => CreateSetTree);
                    _session.SetOutputValuesResolver(() => OutputValues == PanelOutputValues.Values);
                    if (Locked) _session.InvalidateInput(false, suspended: true);
                    _session.SetGeometryPreparationResolver(PrepareCapturedGeometry);
                    _session.SetGeometryRetentionResolver(PrepareCapturedGeometryForRetention);
                    _session.SetGeometrySourceCurrentResolver(MeasurementSourceCurrent);
                    _session.SetCaptureCurrentResolver(CapturedSnapshotCurrent);
                    _savedOptions.DecimalPlaces = DecimalPlaces;
                    _savedOptions.RoundUpFactor = RoundUpFactor;
                    _session.UpdateQuery(_savedOptions);
                    _session.QueryChanged += (_, __) =>
                    {
                        if (!_restoringView && !QueryPreviewActive && !CanOutputValues) OutputValues = PanelOutputValues.Items;
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

        public override void CreateAttributes() => m_attributes = new BetterPanelAttributes(this);

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Data", "D", "Any typed data tree to inspect", GH_ParamAccess.tree);
            p[0].Optional = true;
            p.AddGenericParameter("Paired data", "F", "Optional data paired with D by original branch identity. F must have the same number of branches as D. Exact paths pair first, then remaining branches in tree order; items pair by index. F values are not converted.", GH_ParamAccess.tree);
            p[1].Optional = true;
        }

        private const string MaskDescription = "True/False mask, in the original D input tree, of the items B + F follow. Preserves original paths and item counts. Every member of a kept or selected set is True. This mask conveys membership, not transformed ordering.";

        private const string PairedDescription = "F sends the F items paired with what D sends, at D's paths; an unequal branch sends its whole F branch. Values and types are unchanged.";

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("Data", "D", "Matching Items or displayed Values after tree tools, units and filters. Set Items: all members in {path;k} ({k} when flattened); list-length sets use {k;path}. Set Values: unique values at displayed precision. Values use the shown rounding; unavailable values are null. Sorting changes only the view.", GH_ParamAccess.tree);
            p.AddGenericParameter("Selected data", "S", "Selected Items or Values in tree order. Set Items include every member and keep the same group paths as D. Empty when no rows are selected.", GH_ParamAccess.tree);
            p.AddBooleanParameter("Membership mask", "B", MaskDescription, GH_ParamAccess.tree);
            p.AddGenericParameter("Paired results", "F", PairedDescription, GH_ParamAccess.tree);
        }

        private void ScheduleOutput()
        {
            var document = OnPingDocument();
            var session = _session;
            if (_solving || _restoringView || Locked || document == null || session == null || _queryTree == null) return;
            var options = session.Options;
            if (!_outputPending && _outputOptions != null && PanelQueryPolicy.SameOutputFilter(_outputOptions, options) &&
                _publishedSelectionVersion == _selectionVersion && _publishedScope == OutputScope && _publishedValues == OutputValues) return;
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
            // An upstream click may have hidden an editor without yielding to its
            // deferred completion. Keep that edit before replacing its snapshot.
            if (!_preserveQuery)
            {
                bool completing = _completingInClearData;
                _completingInClearData = true;
                try { CanvasTextInput.CompletePending(); }
                finally { _completingInClearData = completing; }
            }
            ++_solveGeneration;
            if (!_issuingCaptureRetry) _captureRetries = 0;
            // Grasshopper expires the component before collecting its next input.
            // Invalidate now, even when solving is deferred or an upstream node fails.
            if (!_preserveQuery)
            {
                ++_outputRequest;
                _queryTree = null;
                _scalarInputs = _presentedScalars = Array.Empty<CapturedScalar>();
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
                // has exactly the same paths and lengths. Selection is explicitly re-bound after checking the replacement structure.
                var attributes = Attributes as BetterPanelAttributes;
                if (!_discardViewRestore) attributes?.StashViewForRestore();
                attributes?.ResetViewState(keepViewRestore: !_discardViewRestore);
                RowSelection.Clear();
                _session?.InvalidateInput(_retainGeometryMeasurements, suspended: Locked);
            }
            base.ClearData();
        }

        protected override void SolveInstance(IGH_DataAccess da) => CaptureAndPublishInput(da);

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
            _scalarInputs = _presentedScalars = Array.Empty<CapturedScalar>();
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
            Menu_AppendItem(menu, "Reset all", (_, __) => ResetAll());
            Menu_AppendItem(menu, "Clear filters", (_, __) => ClearFilters());
            var valuesMenu = Menu_AppendItem(menu, "Output: " + (OutputsValues ? "Values" : "Items"));
            Menu_AppendItem(valuesMenu.DropDown, "Items", (_, __) => SetOutputValues(PanelOutputValues.Items), true, !OutputsValues);
            Menu_AppendItem(valuesMenu.DropDown, "Values", (_, __) => SetOutputValues(PanelOutputValues.Values), CanOutputValues, OutputsValues);
            var outputMenu = Menu_AppendItem(menu, "B + F follow: " + (OutputScope == PanelOutputScope.Filtered ? "Results" : "Selection"));
            Menu_AppendItem(outputMenu.DropDown, "Results", (_, __) => SetOutputScope(PanelOutputScope.Filtered), true, OutputScope == PanelOutputScope.Filtered);
            Menu_AppendItem(outputMenu.DropDown, "Selection", (_, __) => SetOutputScope(PanelOutputScope.Selected), true, OutputScope == PanelOutputScope.Selected);
            var sizeMenu = Menu_AppendItem(menu, "Canvas panel size");
            var names = CanvasPanelSize.PresetNames;
            for (var i = 0; i < names.Length; i++)
            {
                var size = i;
                Menu_AppendItem(sizeMenu.DropDown, names[i], (_, __) =>
                {
                    RecordUndoEvent("Panel+ panel size");
                    SetCanvasSize(CanvasPanelSize.PresetWidth(size), CanvasPanelSize.PresetHeight(size));
                }, true, PreviewSize == size);
            }
            Menu_AppendItem(sizeMenu.DropDown, "Custom", (_, __) => { }, false, PreviewSize < 0);
        }

    }
}
