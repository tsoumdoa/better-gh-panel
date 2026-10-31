using System;
using System.Collections.Generic;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;
using BetterGhPanel.UI;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Undo;

namespace BetterGhPanel
{
    public sealed partial class BetterPanelComponent
    {
        private readonly PanelRowSelection _committedSelection = new PanelRowSelection();
        private PanelRowSelection? _queryPreviewSelection;
        internal PanelRowSelection RowSelection => _queryPreviewSelection ?? _committedSelection;
        internal PanelOutputScope OutputScope { get; private set; }
        private GH_Structure<IGH_Goo>? _inputTree, _preConversionTree, _pairedQueryTree;
        private GH_Structure<IGH_Goo>? _liveInputTree, _livePairedTree, _pairedInputTree;
        private bool _pairedInputPresent;
        private IReadOnlyList<PanelSourceAddress> _retainedSources = Array.Empty<PanelSourceAddress>();
        private string? _pairedError;
        internal PanelSnapshot? PairedSnapshot { get; private set; }
        internal string? PairedError => _pairedError;
        internal bool CanSplit => _pairedInputPresent && _pairedQueryTree != null && _pairedError == null && Session.HasCurrentResult;
        private long _selectionVersion, _publishedSelectionVersion;
        private PanelOutputScope _publishedScope;
        private PanelQueryResult? _cachedOutputResult;
        private GH_Structure<IGH_Goo>? _filteredData, _filteredPair;
        private GH_Structure<GH_Boolean>? _filteredMask;

        internal void SetOutputScope(PanelOutputScope scope)
        {
            if (!Enum.IsDefined(typeof(PanelOutputScope), scope) || OutputScope == scope) return;
            // Scope changes must not reload the component and discard its selection.
            RecordUndoEvent("Panel+ B + F source", new OutputScopeUndo(InstanceGuid, OutputScope, scope));
            ApplyOutputScope(scope);
        }

        private void ApplyOutputScope(PanelOutputScope scope)
        {
            OutputScope = scope;
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            ScheduleOutput();
            Instances.ActiveCanvas?.Invalidate();
        }

        private sealed class OutputScopeUndo : GH_UndoAction
        {
            private readonly Guid _component;
            private readonly PanelOutputScope _before, _after;
            internal OutputScopeUndo(Guid component, PanelOutputScope before, PanelOutputScope after)
            { _component = component; _before = before; _after = after; }
            public override bool ExpiresSolution => false;
            public override bool ExpiresDisplay => false;
            protected override void Internal_Undo(GH_Document doc) => Apply(doc, _before);
            protected override void Internal_Redo(GH_Document doc) => Apply(doc, _after);
            private void Apply(GH_Document doc, PanelOutputScope scope) =>
                (doc.FindObject(_component, false) as BetterPanelComponent)?.ApplyOutputScope(scope);
        }

        private void ClearOutputSources()
        {
            _inputTree = _preConversionTree = _pairedQueryTree = null;
            _liveInputTree = _livePairedTree = _pairedInputTree = null;
            _pairedInputPresent = false;
            _captureGraphs = Array.Empty<CaptureGraphWitness>();
            _captureHasGeometry = false;
            _retainedSources = Array.Empty<PanelSourceAddress>();
            _pairedError = null;
            PairedSnapshot = null;
            _pairedSourceItems = Array.Empty<IGH_Goo?>();
            _pairedMeasurementSources = Array.Empty<CapturedMeasurementSource?>();
            _pairedCaptureGraphs = Array.Empty<CaptureGraphWitness>();
            _cachedOutputResult = null;
            _filteredData = _filteredPair = null;
            _filteredMask = null;
        }

        private void CapturePairedInput(IGH_DataAccess da, GH_Structure<IGH_Goo> inputTree, GH_Structure<IGH_Goo> queryTree,
            IReadOnlyList<PanelSourceAddress> retainedSources, out GH_Structure<IGH_Goo>? pairedTree, out string? error,
            out GH_Structure<IGH_Goo>? livePairedTree, out GH_Structure<IGH_Goo>? capturedPairedTree, out bool present)
        {
            pairedTree = null;
            error = null;
            livePairedTree = capturedPairedTree = null;
            present = PairedInputPresent();
            if (!present) return;
            if (!da.GetDataTree(1, out GH_Structure<IGH_Goo> paired)) paired = new GH_Structure<IGH_Goo>();
            livePairedTree = paired;
            capturedPairedTree = PanelTreeOperations.CopyStructure(paired);
            pairedTree = PanelOutputOperations.Pair(inputTree, capturedPairedTree, queryTree, retainedSources, out error);
        }

        private bool PairedInputPresent() => Params.Input[1].SourceCount != 0 ||
            !(Params.Input[1] is Param_GenericObject input) || input.PersistentData.PathCount != 0;

        private void PublishOutputs(IGH_DataAccess da, Func<bool> solveCurrent)
        {
            var session = Session;
            _outputOptions = session.Options;
            _outputPending = session.IsBusy;
            // Construct from the committed managed result. PublicationCurrent runs
            // the full capture fence below, including every measurement source and
            // the queue's snapshot ownership. Scanning/copying the measurement cache
            // here would repeat only a subset of that fence on every selection.
            bool current = session.HasCurrentResult;
            bool filtering = PanelQueryPolicy.HasOutputFilter(_outputOptions);
            var empty = new GH_Structure<IGH_Goo>();
            if (current && _queryTree != null && _inputTree != null && !ReferenceEquals(_cachedOutputResult, session.Result))
            {
                _cachedOutputResult = session.Result;
                if (session.Result.DisplaySet != null) CountOutputVisits(session.Result.RowPositions.Count);
                else if (filtering && (session.Result.RowPositions.Count != _queryTree.DataCount || session.Result.BranchPaths.Count != _queryTree.PathCount))
                    CountOutputVisits(_queryTree.DataCount);
                _filteredData = session.Result.DisplaySet != null
                    ? PanelOutputOperations.DisplaySet(_queryTree, session.Result, session.Result.RowPositions, session.Result.BranchPaths)
                    : filtering ? PanelTreeOperations.Filter(_queryTree, session.Result) : _queryTree;
                if (_pairedQueryTree != null && (session.Result.RowPositions.Count != _pairedQueryTree.DataCount || session.Result.BranchPaths.Count != _pairedQueryTree.PathCount))
                    CountOutputVisits(_pairedQueryTree.DataCount);
                _filteredPair = _pairedQueryTree == null ? null : PanelTreeOperations.Filter(_pairedQueryTree, session.Result);
                _filteredMask = null;
            }
            // Selection-only solutions reuse unchanged query outputs. A large
            // original-shape mask is built once per filtered result, not per click.
            if (current && _inputTree != null && OutputScope == PanelOutputScope.Filtered && _filteredMask == null)
            {
                CountOutputVisits((long)_inputTree.DataCount + session.Result.RowPositions.Count);
                _filteredMask = PanelOutputOperations.Mask(_inputTree, _retainedSources, session.Result.RowPositions);
            }
            var data = current ? _filteredData ?? empty : !filtering && _queryTree != null ? _queryTree : empty;
            var selectedPositions = current ? RowSelection.OrderedPositions(session.Result) : Array.Empty<int>();
            if (current && _queryTree != null && selectedPositions.Count > 0)
                CountOutputVisits(session.Result.DisplaySet != null ? selectedPositions.Count : _queryTree.DataCount);
            var selected = current && _queryTree != null ? session.Result.DisplaySet != null
                ? PanelOutputOperations.DisplaySet(_queryTree, session.Result, selectedPositions, RowSelection.BranchPaths(session.Result))
                : PanelTreeOperations.Select(_queryTree, selectedPositions) : empty;
            if (current && _inputTree != null && OutputScope == PanelOutputScope.Selected)
                CountOutputVisits((long)_inputTree.DataCount + selectedPositions.Count);
            var mask = !current || _inputTree == null ? new GH_Structure<GH_Boolean>() : OutputScope == PanelOutputScope.Filtered
                ? _filteredMask! : PanelOutputOperations.Mask(_inputTree, _retainedSources, selectedPositions);
            if (current && _pairedQueryTree != null && OutputScope == PanelOutputScope.Selected && selectedPositions.Count > 0)
                CountOutputVisits(_pairedQueryTree.DataCount);
            var paired = !current || _pairedQueryTree == null ? empty : OutputScope == PanelOutputScope.Filtered
                ? _filteredPair ?? empty : session.Result.DisplaySet?.IsBranchSet == true
                    ? PanelTreeOperations.SelectBranches(_filteredPair ?? empty, RowSelection.BranchPaths(session.Result))
                    : PanelTreeOperations.Select(_pairedQueryTree, selectedPositions);
            // Tree construction above only reads captured structures and managed
            // query data. Run the full freshness fence once, after all of it and
            // immediately before GH receives any output.
            BeforeOutputValidationForTesting?.Invoke();
            if (!solveCurrent()) return;
            if (CaptureDiagnosticsForTesting)
                OutputPublicationItemsForTesting += (long)data.DataCount + selected.DataCount + mask.DataCount + paired.DataCount;
            da.SetDataTree(0, data);
            da.SetDataTree(1, selected);
            da.SetDataTree(2, mask);
            da.SetDataTree(3, paired);
            if (_pairedError != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _pairedError);
            _publishedSelectionVersion = _selectionVersion;
            _publishedScope = OutputScope;
        }
    }
}
