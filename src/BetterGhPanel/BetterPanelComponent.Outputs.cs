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
        internal PanelRowSelection RowSelection { get; } = new PanelRowSelection();
        internal PanelOutputScope OutputScope { get; private set; }
        private GH_Structure<IGH_Goo>? _inputTree, _pairedQueryTree;
        private IReadOnlyList<PanelSourceAddress> _retainedSources = Array.Empty<PanelSourceAddress>();
        private string? _pairedError;
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
            _inputTree = _pairedQueryTree = null;
            _retainedSources = Array.Empty<PanelSourceAddress>();
            _pairedError = null;
            _cachedOutputResult = null;
            _filteredData = _filteredPair = null;
            _filteredMask = null;
        }

        private void CapturePairedInput(IGH_DataAccess da)
        {
            _pairedQueryTree = null;
            _pairedError = null;
            if (Params.Input[1].SourceCount == 0 && Params.Input[1] is Param_GenericObject input && input.PersistentData.PathCount == 0) return;
            if (!da.GetDataTree(1, out GH_Structure<IGH_Goo> paired)) paired = new GH_Structure<IGH_Goo>();
            if (_inputTree != null && _queryTree != null)
                _pairedQueryTree = PanelOutputOperations.Pair(_inputTree, paired, _queryTree, _retainedSources, out _pairedError);
        }

        private void PublishOutputs(IGH_DataAccess da)
        {
            var session = Session;
            _outputOptions = session.Options;
            _outputPending = session.IsBusy;
            bool current = session.HasCurrentResult;
            bool filtering = PanelQueryPolicy.HasOutputFilter(_outputOptions);
            var empty = new GH_Structure<IGH_Goo>();
            if (current && _queryTree != null && _inputTree != null && !ReferenceEquals(_cachedOutputResult, session.Result))
            {
                _cachedOutputResult = session.Result;
                _filteredData = session.Result.DisplaySet != null
                    ? PanelOutputOperations.DisplaySet(_queryTree, session.Result, session.Result.RowPositions, session.Result.BranchPaths)
                    : filtering ? PanelTreeOperations.Filter(_queryTree, session.Result) : _queryTree;
                _filteredPair = _pairedQueryTree == null ? null : PanelTreeOperations.Filter(_pairedQueryTree, session.Result);
                _filteredMask = null;
            }
            // Selection-only solutions reuse unchanged query outputs. A large
            // original-shape mask is built once per filtered result, not per click.
            if (current && _inputTree != null && OutputScope == PanelOutputScope.Filtered && _filteredMask == null)
                _filteredMask = PanelOutputOperations.Mask(_inputTree, _retainedSources, session.Result.RowPositions);
            var data = current ? _filteredData ?? empty : !filtering && _queryTree != null ? _queryTree : empty;
            var selectedPositions = current ? RowSelection.OrderedPositions(session.Result) : Array.Empty<int>();
            var selected = current && _queryTree != null ? session.Result.DisplaySet != null
                ? PanelOutputOperations.DisplaySet(_queryTree, session.Result, selectedPositions, RowSelection.BranchPaths(session.Result))
                : PanelTreeOperations.Select(_queryTree, selectedPositions) : empty;
            da.SetDataTree(0, data);
            da.SetDataTree(1, selected);
            da.SetDataTree(2, !current || _inputTree == null ? new GH_Structure<GH_Boolean>() : OutputScope == PanelOutputScope.Filtered
                ? _filteredMask! : PanelOutputOperations.Mask(_inputTree, _retainedSources, selectedPositions));
            da.SetDataTree(3, !current || _pairedQueryTree == null ? empty : OutputScope == PanelOutputScope.Filtered
                ? _filteredPair ?? empty : session.Result.DisplaySet?.IsBranchSet == true
                    ? PanelTreeOperations.SelectBranches(_filteredPair ?? empty, RowSelection.BranchPaths(session.Result))
                    : PanelTreeOperations.Select(_pairedQueryTree, selectedPositions));
            if (_pairedError != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _pairedError);
            _publishedSelectionVersion = _selectionVersion;
            _publishedScope = OutputScope;
        }
    }
}
