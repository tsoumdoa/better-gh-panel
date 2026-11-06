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
                return PanelFilterValue.Format(snapshot, row, options.SearchMode, DecimalPlaces, geometry, roundUpFactor: RoundUpFactor);
            var display = PanelValuePresentation.FormatDisplay(row, snapshot.UnitConversion, DecimalPlaces, geometry, roundUpFactor: RoundUpFactor);
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
                var value = PanelFilterValue.Format(snapshot, row, options.SearchMode, DecimalPlaces, metric, includeUnits: false, roundUpFactor: RoundUpFactor);
                bool literalProperty = PanelProperties.IsProperty(options.SearchMode) &&
                    PanelProperties.TryGet(row, options.SearchMode, metric, out var property) && property != null && property.Kind != PanelPropertyKind.Number;
                if (!literalProperty && (value == "measuring…" || value == "unavailable")) throw new InvalidOperationException("A selected measurement is unavailable.");
                return value;
            }
            if (!row.IsValid)
                return PanelValuePresentation.FormatDisplay(row, snapshot.UnitConversion, DecimalPlaces);
            if (row.NumericValue.HasValue)
                return PanelUnits.Format(row.NumericValue.Value, PanelUnitConversion.None, DecimalPlaces);
            var geometry = Session.GetGeometryDetails(snapshot, position) ?? row.GeometryInfo;
            if (row.IsValid && geometry?.Value != null)
            {
                var value = PanelUnits.FormatGeometry(geometry.Value.Value, geometry.Power, geometry.Units,
                    snapshot.UnitConversion, DecimalPlaces, includeUnits: false, roundUpFactor: RoundUpFactor);
                if (value == "unavailable") throw new InvalidOperationException("A selected measurement is unavailable.");
                return value;
            }
            if (row.IsValid && geometry?.CanMeasure == true)
                throw new InvalidOperationException("A selected measurement is unavailable.");
            return PanelValuePresentation.FormatDisplay(row, snapshot.UnitConversion, DecimalPlaces, geometry, roundUpFactor: RoundUpFactor);
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

        private readonly struct ActionStamp
        {
            private readonly BetterPanelComponent _owner;
            private readonly PanelSession _session;
            private readonly PanelSnapshot _snapshot;
            private readonly PanelQueryResult _result;
            private readonly IGH_Goo?[] _source;
            private readonly long? _selection;
            internal ActionStamp(BetterPanelComponent owner, PanelSession session, PanelSnapshot snapshot,
                PanelQueryResult result, IGH_Goo?[] source, long? selection)
            { _owner = owner; _session = session; _snapshot = snapshot; _result = result; _source = source; _selection = selection; }
            internal bool IsCurrent() => ReferenceEquals(_owner._session, _session) && _session.HasCurrentResult &&
                ReferenceEquals(_session.Snapshot, _snapshot) && ReferenceEquals(_session.Result, _result) &&
                ReferenceEquals(_owner._sourceItems, _source) && (!_selection.HasValue || _owner._selectionVersion == _selection.Value);
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
            var stamp = new ActionStamp(this, session, snapshot, expectedResult, source, _selectionVersion);
            bool Current() => !_previewSuspended && stamp.IsCurrent();
            string? Format(PanelGeometryActionResult result) => !result.Success ? result.Error :
                result.Skipped > 0 || result.Exhausted
                ? "Selection framed" + (result.Skipped > 0 ? "; " + result.Skipped + " skipped" : "") +
                    (result.Exhausted ? "; inspection limit reached" : "") : null;
            ordered = ExpandSetMembers(ordered);
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
            ordered = ExpandSetMembers(ordered);
            var previewPositions = PreviewMode == PanelPreviewMode.FilteredResults ? session.Result.DisplaySet?.AllMembers ?? session.Result.RowPositions : ordered;
            _highlight.SetOrderResult(ShowItemOrder && previewPositions.Count > 0 ? session.Result : null);
            var source = _sourceItems;
            var expectedResult = session.Result;
            var mode = PreviewMode;
            var stamp = new ActionStamp(this, session, snapshot, expectedResult, source, _selectionVersion);
            bool Current() => !_previewSuspended && !Locked && PreviewMode == mode &&
                stamp.IsCurrent();
            _highlight.SetScopeChecked(source, expectedResult, mode == PanelPreviewMode.FilteredResults, ordered, Current, RowSelection);
            InvalidatePreviewCaches();
            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        private IReadOnlyList<int> ExpandSetMembers(IReadOnlyList<int> positions) =>
            Session.Result.DisplaySet?.ExpandMembers(Session.Snapshot, positions) ?? positions;

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

        internal Task<string> CopyAddressesAsync(PanelSnapshot snapshot, IReadOnlyList<int> positions, PanelRowAction action, string? path = null, bool paired = false)
            => CopyValuesCoreAsync(snapshot, positions, paired, action, path);

        private async Task<string> CopyValuesCoreAsync(PanelSnapshot snapshot, IReadOnlyList<int> positions, bool paired,
            PanelRowAction action = PanelRowAction.CopyValues, string? path = null)
        {
            CancelPendingCopy();
            bool listLengthCopy = path == null && action == PanelRowAction.CopyValues && Session.Options.DisplayFilterValue && Session.Options.SearchMode == PanelSearchMode.ListLength;
            bool branchSet = path == null && action == PanelRowAction.CopyValues && Session.Result.DisplaySet?.IsBranchSet == true;
            IReadOnlyList<int> ordered;
            if (path != null || action == PanelRowAction.CopyPaths && RowSelection.Count > 0) ordered = positions;
            else if (branchSet || listLengthCopy) ordered = Array.Empty<int>();
            else if (!TryGetActionPositions(snapshot, positions, out ordered)) return "Data changed; select rows again";
            var pairedSnapshot = paired ? PairedSnapshot : null;
            if (paired && (!CanSplit || pairedSnapshot == null)) return "Data changed; select rows again";
            string? pathText = path == null ? null : string.Join(Environment.NewLine, (paired ? PairedPaths(path) : new[] { path })
                .Select(value => action == PanelRowAction.CopyPathBare ? value.Trim('{', '}') : value));
            long clipboardGeneration = PanelGeometryActions.BeginClipboard();
            var session = _session!;
            var result = session.Result;
            var branchSelection = branchSet || listLengthCopy ? new HashSet<string>(RowSelection.BranchPaths(result), StringComparer.Ordinal) : null;
            var branches = branchSet || listLengthCopy ? result.BranchPaths.Where(branchPath => branchSelection!.Contains(branchPath)).ToArray() : Array.Empty<string>();
            if ((branchSet || listLengthCopy) && (branches.Length == 0 || !ReferenceEquals(snapshot, session.Snapshot))) return "Data changed; select branches again";
            var source = _sourceItems;
            var stamp = new ActionStamp(this, session, snapshot, result, source, selection: null);
            long selectionVersion = RowSelection.Version;
            var cancellation = new CancellationTokenSource();
            _copyCancellation = cancellation;
            var measurementPositions = session.Options.DisplayFilterValue && PanelProperties.IsProperty(session.Options.SearchMode)
                ? ordered.Where(position => PanelProperties.TryGet(snapshot.Rows[position], session.Options.SearchMode,
                    session.GetGeometryDetails(snapshot, position), out var property) && property == null).ToArray() : ordered;
            bool addressOnly = action != PanelRowAction.CopyValues && action != PanelRowAction.CopyTable;
            bool measured = addressOnly || paired || listLengthCopy || await session.MeasureGeometryAsync(snapshot, measurementPositions, cancellation.Token);
            int copiedCount = branches.Length;
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
                        path != null ? "Copied " + pathText :
                        addressOnly ? "Addresses copied" : action == PanelRowAction.CopyTable ? "Table copied" :
                        listLengthCopy && !paired ? copiedCount + (copiedCount == 1 ? " count copied" : " counts copied") :
                        listLengthCopy && paired && !branchSet ? copiedCount + (copiedCount == 1 ? " value copied" : " values copied") :
                        branchSet ? "F set values copied" : ordered.Count == 1 ? "Value copied" : ordered.Count + " values copied";
                    completion.TrySetResult(status);
                    if (ReferenceEquals(_copyCancellation, cancellation)) { _copyCancellation = null; cancellation.Dispose(); }
                }
                try
                {
                    bool SelectionCurrent() => PanelGeometryActions.ClipboardCurrent(clipboardGeneration) &&
                        ReferenceEquals(_copyCancellation, cancellation) && !cancellation.IsCancellationRequested &&
                        stamp.IsCurrent() &&
                        (!paired || CanSplit && ReferenceEquals(PairedSnapshot, pairedSnapshot)) &&
                        (path != null || action == PanelRowAction.CopyPaths || branchSet || listLengthCopy ? RowSelection.Version == selectionVersion && (path == null || result.ContainsBranchPath(path)) : PanelActionPositions.IsCurrent(result, source.Length, RowSelection, ordered, selectionVersion));
                    if (!measured || !SelectionCurrent()) Finish(PanelCommitOutcome.Stale);
                    else
                    {
                        string Value(int position) => paired ? PairedValue(position) : CopyValue(snapshot, position);
                        string Address(int position)
                        {
                            return PanelItemOrder.Address(result, position, OutputValues == PanelOutputValues.Items, FlattenTree);
                        }
                        string text;
                        if (path != null) text = pathText!;
                        else if (action == PanelRowAction.CopyPaths)
                        {
                            var paths = new HashSet<string>(RowSelection.BranchPaths(result), StringComparer.Ordinal);
                            if (paired) paths = new HashSet<string>(paths.SelectMany(PairedPaths), StringComparer.Ordinal);
                            text = string.Join(Environment.NewLine, (pairedSnapshot ?? snapshot).Branches.Where(branch => paths.Contains(branch.Path)).Select(branch => branch.Path));
                        }
                        else if (paired && (action == PanelRowAction.CopyAddresses || action == PanelRowAction.CopyTable))
                        {
                            // Selection identities belong to D; F addresses and cells always belong to raw F.
                            var items = result.DisplaySet?.IsBranchSet == false
                                ? ordered.SelectMany(position => PairedData!.BranchItems(snapshot, result.DisplaySet.Members[position], Array.Empty<string>(), false).Select(item => item.Item))
                                : ordered.Select(position => PairedData!.ItemAt(position)).Where(item => item.HasValue).Select(item => item!.Value);
                            var rows = items.Select(item => pairedSnapshot!.Rows[item]);
                            text = action == PanelRowAction.CopyAddresses
                                ? string.Join(Environment.NewLine, rows.Select(row => row.Path + "(" + row.OriginalIndex.ToString(CultureInfo.InvariantCulture) + ")"))
                                : "Path\tIndex\tValue" + Environment.NewLine + string.Join(Environment.NewLine, rows.Select(row =>
                                {
                                    string value = BetterPanelAttributes.PairedDisplayValue(row);
                                    if (value.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) >= 0) value = "\"" + value.Replace("\"", "\"\"") + "\"";
                                    return row.Path + "\t" + row.OriginalIndex.ToString(CultureInfo.InvariantCulture) + "\t" + value;
                                }));
                        }
                        else if (action == PanelRowAction.CopyAddresses) text = string.Join(Environment.NewLine, ordered.Select(Address));
                        else if (action == PanelRowAction.CopyTable)
                            text = "Path\tIndex\tValue" + Environment.NewLine + string.Join(Environment.NewLine, ordered.Select(position =>
                            {
                                PanelItemOrder.TryGetNumber(result, position, out int index);
                                string value = paired ? Value(position) : DisplayValue(snapshot, snapshot.Rows[position], position);
                                // Quote cells containing TSV delimiters, preserving the complete shown value.
                                if (value.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) >= 0) value = "\"" + value.Replace("\"", "\"\"") + "\"";
                                return snapshot.Rows[position].Path + "\t" + index.ToString(CultureInfo.InvariantCulture) + "\t" + value;
                            }));
                        else if (branchSet) text = paired ? PairedBranchValues(result, branches) : string.Join(Environment.NewLine, branches.Select(branch => snapshot.BranchItemCounts[branch].ToString(CultureInfo.InvariantCulture)));
                        else if (listLengthCopy && paired)
                        {
                            var paths = new HashSet<string>(branches, StringComparer.Ordinal);
                            var members = Enumerable.Range(0, snapshot.Rows.Count).Where(position => paths.Contains(snapshot.Rows[position].Path)).ToArray();
                            var values = PairedData!.BranchItems(snapshot, members, branches, true)
                                .Select(item => BetterPanelAttributes.PairedDisplayValue(pairedSnapshot!.Rows[item.Item])).ToArray();
                            copiedCount = values.Length;
                            text = string.Join(Environment.NewLine, values);
                        }
                        else if (listLengthCopy)
                            text = string.Join(Environment.NewLine, branches.Select(branch => snapshot.BranchItemCounts[branch].ToString(CultureInfo.InvariantCulture)));
                        else text = string.Join(Environment.NewLine, ordered.Select(Value));
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
            var stamp = new ActionStamp(this, session, snapshot, expectedResult, source, _selectionVersion);
            bool Current() => stamp.IsCurrent();
            string Format(PanelGeometryActionResult result) => result.Success ? result.Count + " geometries copied" +
                (result.Skipped > 0 ? "; " + result.Skipped + " skipped (" + (result.Detail ?? "unsupported geometry or unverified metadata") + ")" : "") +
                (result.Exhausted ? "; preparation limit reached" : "") +
                (result.UnverifiedMetadata ? "; custom metadata content not fully verified" : "") : result.Error ?? "Copy geometry failed";
            ordered = ExpandSetMembers(ordered);
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
                    RecordUndoEvent("Panel preview size");
                    SetCanvasSize(CanvasPanelSize.PresetWidth(size), CanvasPanelSize.PresetHeight(size));
                }, true, PreviewSize == size);
            }
            Menu_AppendItem(sizeMenu.DropDown, "Custom", (_, __) => { }, false, PreviewSize < 0);
        }

    }
}
