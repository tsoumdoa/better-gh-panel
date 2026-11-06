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
    public sealed partial class BetterPanelComponent
    {
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
                bool result = PanelUnitOperations.InputsStillMatchWithVisits(_preConversionTree, tree, snapshot.UnitConversion, out int count, RoundUpFactor, DecimalPlaces, _scalarInputs, _presentedScalars);
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

        private void CaptureAndPublishInput(IGH_DataAccess da)
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
                    if (options.DecimalPlaces != DecimalPlaces || options.RoundUpFactor != RoundUpFactor)
                    {
                        options.DecimalPlaces = DecimalPlaces;
                        options.RoundUpFactor = RoundUpFactor;
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
                    var queryScalars = CapturedScalar.Convert(scalarInputs, conversion, roundUpFactor, DecimalPlaces);
                    var queryTree = PanelTreeOperations.CopyStructure(PanelUnitOperations.ApplyCaptured(preConversionTree, conversion, scalarInputs, roundUpFactor, DecimalPlaces, queryScalars));
                    var inputSeed = SnapshotCapture.PrepareInputs(queryTree);
                    CapturePairedInput(da, inputTree, retainedSources, out var pairedTree, out var pairedError,
                        out var livePairedTree, out var capturedPairedTree, out bool pairedPresent);
                    var context = ReadGeometryContext(out var unitSystem, out double tolerance, out string units);
                    if (!Current()) return;
                    var snapshot = SnapshotCapture.CaptureForMeasurements(queryTree, out var sources, out var measurementSources, out var graphs,
                        conversion, units, retainedSources, CaptureObserverForTesting, null, queryScalars, inputSeed);
                    var pairedSources = Array.Empty<IGH_Goo?>();
                    var pairedMeasurements = Array.Empty<CapturedMeasurementSource?>();
                    var pairedGraphs = Array.Empty<CaptureGraphWitness>();
                    var pairedSnapshot = PanelSplit && pairedTree != null ? SnapshotCapture.CaptureForMeasurements(capturedPairedTree!,
                        out pairedSources, out pairedMeasurements, out pairedGraphs, PanelUnitConversion.None, units, null, null, null, null, null) : null;
                    StructureObserverForTesting?.Invoke(liveInputTree, livePairedTree);
                    if (!Current()) return;
                    bool hasGeometry = HasGeometry(snapshot) || pairedSnapshot != null && HasGeometry(pairedSnapshot);
                    if (!SnapshotCapture.MeasurementSourcesCurrent(sources, measurementSources) || !SnapshotCapture.GraphSourcesCurrent(graphs) ||
                        !SnapshotCapture.ManagedRowsCurrent(snapshot, sources) || !PanelUnitOperations.InputsStillMatch(preConversionTree, queryTree, conversion, roundUpFactor, DecimalPlaces, scalarInputs, queryScalars) ||
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
                    _scalarInputs = scalarInputs; _presentedScalars = queryScalars;
                    _inputTree = inputTree; _preConversionTree = preConversionTree; _queryTree = queryTree; _retainedSources = retainedSources;
                    _liveInputTree = liveInputTree; _livePairedTree = livePairedTree; _pairedInputTree = capturedPairedTree; _pairedInputPresent = pairedPresent;
                    _pairedError = pairedError;
                    PairedData = pairedTree;
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
                var committedValues = OutputValues;
                var committedSelection = _selectionVersion;
                bool committedResultCurrent = session.HasCurrentResult;
                bool PublicationOwnerCurrent() => Current() && ReferenceEquals(committedTree, _queryTree) &&
                    ReferenceEquals(committedSnapshot, session.Snapshot) && ReferenceEquals(committedResult, session.Result) &&
                    committedScope == OutputScope && committedValues == OutputValues && committedSelection == _selectionVersion &&
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
            _scalarInputs = _presentedScalars = Array.Empty<CapturedScalar>();
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

    }
}
