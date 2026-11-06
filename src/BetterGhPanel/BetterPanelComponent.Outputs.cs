using System;
using System.Collections.Generic;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;
using BetterGhPanel.UI;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel
{
    public sealed partial class BetterPanelComponent
    {
        private readonly PanelRowSelection _committedSelection = new PanelRowSelection();
        private PanelRowSelection? _queryPreviewSelection;
        internal PanelRowSelection CommittedSelection => _committedSelection;
        internal PanelRowSelection RowSelection => _queryPreviewSelection ?? _committedSelection;
        internal PanelOutputScope OutputScope { get; private set; }
        internal PanelOutputValues OutputValues { get; private set; }
        internal static bool CanOutputValuesFor(bool createSet, PanelQueryOptions options) => createSet || options.DisplayFilterValue;
        internal bool CanOutputValues => CanOutputValuesFor(CreateSetTree, Session.Options);
        internal bool OutputsValues => CanOutputValues && OutputValues == PanelOutputValues.Values;
        private GH_Structure<IGH_Goo>? _inputTree, _preConversionTree;
        private GH_Structure<IGH_Goo>? _liveInputTree, _livePairedTree, _pairedInputTree;
        private bool _pairedInputPresent;
        private IReadOnlyList<PanelSourceAddress> _retainedSources = Array.Empty<PanelSourceAddress>();
        private string? _pairedError;
        internal PanelPairedData? PairedData { get; private set; }
        internal IEnumerable<string> PairedPaths(string path) => PairedData?.BranchesAt(path)
            .Where(pair => pair.Data.ItemCount > 0 || PanelPairedData.EmptyPartnersInScope(Session.Options))
            .Select(pair => pair.Paired.Path) ?? Enumerable.Empty<string>();
        internal PanelRow? PairedRow(int position) => PairedData?.ItemAt(position) is int item ? PairedSnapshot?.Rows[item] : null;
        internal string PairedValue(int position)
        {
            var query = Session.Result;
            if (query.DisplaySet?.IsBranchSet == false && PairedSnapshot != null && PairedData != null)
                return string.Join(Environment.NewLine, PairedData.BranchItems(query.Snapshot, query.DisplaySet.Members[position],
                    Array.Empty<string>(), false).Select(item => BetterPanelAttributes.PairedDisplayValue(PairedSnapshot.Rows[item.Item])));
            return PairedRow(position) is PanelRow row ? BetterPanelAttributes.PairedDisplayValue(row) : "";
        }
        private string PairedBranchValues(PanelQueryResult query, IReadOnlyList<string> paths) =>
            string.Join(Environment.NewLine, paths.SelectMany(path => PairedData!.BranchItems(query.Snapshot,
                query.DisplaySet!.BranchMembers[path], query.DisplaySet.BranchOccurrences[path], false)
                .GroupBy(item => item.Path).SelectMany(group => group))
                .Select(item => BetterPanelAttributes.PairedDisplayValue(PairedSnapshot!.Rows[item.Item])));
        internal PanelSnapshot? PairedSnapshot { get; private set; }
        internal string? PairedError => _pairedError;
        internal bool CanSplit => _pairedInputPresent && PairedData != null && _pairedError == null && Session.HasCurrentResult;
        private PanelQueryOptions? _pairedSummaryOptions;
        internal bool PairedSummaryCurrent => Session.HasCurrentResult && PairedData?.SummaryResult is PanelQueryResult summary &&
            ReferenceEquals(summary.Snapshot, Session.Snapshot) && _pairedSummaryOptions != null &&
            PanelQueryPolicy.SameOutputFilter(_pairedSummaryOptions, Session.Options);
        private long _selectionVersion, _publishedSelectionVersion;
        private PanelOutputScope _publishedScope;
        private PanelOutputValues _publishedValues;
        private PanelQueryResult? _cachedOutputResult;
        private GH_Structure<IGH_Goo>? _filteredData, _filteredPair;
        private GH_Structure<GH_Boolean>? _filteredMask;

        internal void SetOutputValues(PanelOutputValues values)
        {
            if (!Enum.IsDefined(typeof(PanelOutputValues), values) || OutputValues == values) return;
            RecordUndoEvent("Panel+ output Items / Values", new PanelUndo<PanelOutputValues>(InstanceGuid, OutputValues, values, (component, value) => component.ApplyOutputValues(value)));
            ApplyOutputValues(values);
        }

        private void ApplyOutputValues(PanelOutputValues values)
        {
            OutputValues = values;
            _cachedOutputResult = null;
            Session.UpdateQuery(Session.Options);
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            ScheduleOutput();
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetOutputScope(PanelOutputScope scope)
        {
            if (!Enum.IsDefined(typeof(PanelOutputScope), scope) || OutputScope == scope) return;
            // Scope changes must not reload the component and discard its selection.
            RecordUndoEvent("Panel+ B + F follow", new PanelUndo<PanelOutputScope>(InstanceGuid, OutputScope, scope, (component, value) => component.ApplyOutputScope(value)));
            ApplyOutputScope(scope);
        }

        private void ApplyOutputScope(PanelOutputScope scope)
        {
            OutputScope = scope;
            UpdateOutputDescriptions();
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            ScheduleOutput();
            Instances.ActiveCanvas?.Invalidate();
        }

        private void UpdateOutputDescriptions()
        {
            if (Params.Output.Count < 4) return;
            var scope = OutputScope == PanelOutputScope.Filtered ? "Results" : "Selection";
            Params.Output[2].Description = MaskDescription + " B + F follow: " + scope + ".";
            Params.Output[3].Description = PairedDescription + " B + F follow: " + scope + ".";
        }

        private void ClearOutputSources()
        {
            _inputTree = _preConversionTree = null;
            _liveInputTree = _livePairedTree = _pairedInputTree = null;
            _pairedInputPresent = false;
            _captureGraphs = Array.Empty<CaptureGraphWitness>();
            _captureHasGeometry = _pairedHasGeometry = false;
            _retainedSources = Array.Empty<PanelSourceAddress>();
            _pairedError = null;
            PairedSnapshot = null;
            PairedData = null;
            _pairedSummaryOptions = null;
            _pairedSourceItems = Array.Empty<IGH_Goo?>();
            _pairedMeasurementSources = Array.Empty<CapturedMeasurementSource?>();
            _pairedCaptureGraphs = Array.Empty<CaptureGraphWitness>();
            _cachedOutputResult = null;
            _filteredData = _filteredPair = null;
            _filteredMask = null;
        }

        private void CapturePairedInput(IGH_DataAccess da, GH_Structure<IGH_Goo> inputTree,
            IReadOnlyList<PanelSourceAddress> retainedSources, out PanelPairedData? pairedTree, out string? error,
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
            pairedTree = PanelOutputOperations.Pair(inputTree, capturedPairedTree, retainedSources,
                TrimDepth, SimplifyTree, FlattenTree, ReverseTree, out error);
        }

        internal bool PairedInputPresent() => Params.Input[1].SourceCount != 0 ||
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
            var query = session.Result;
            var set = query.DisplaySet;
            var empty = new GH_Structure<IGH_Goo>();
            if (current && _queryTree != null && _inputTree != null && !ReferenceEquals(_cachedOutputResult, session.Result))
            {
                _cachedOutputResult = session.Result;
                if (set == null && !OutputsValues && filtering && (session.Result.RowPositions.Count != _queryTree.DataCount || session.Result.BranchPaths.Count != _queryTree.PathCount))
                    CountOutputVisits(_queryTree.DataCount);
                _filteredData = BuildDataOutput(_queryTree, query, query.RowPositions, query.BranchPaths, false);
                _filteredPair = PairedData?.Output(query, query.RowPositions, query.BranchPaths, FlattenTree, PanelPairedData.EmptyPartnersInScope(session.Options), CountOutputVisits);
                // Busy publications advance _outputOptions without rebuilding summaries.
                // Retain their own filter identity, allowing unchanged sort-only reuse.
                _pairedSummaryOptions = session.Options;
                _filteredMask = null;
            }
            // Selection-only solutions reuse unchanged query outputs. A large
            // original-shape mask is built once per filtered result, not per click.
            if (current && _inputTree != null && OutputScope == PanelOutputScope.Filtered && _filteredMask == null)
            {
                var members = set?.AllMembers ?? query.RowPositions;
                CountOutputVisits((long)_inputTree.DataCount + members.Count);
                _filteredMask = PanelOutputOperations.Mask(_inputTree, _retainedSources, members);
            }
            var data = current ? _filteredData ?? empty : !filtering && _queryTree != null ? _queryTree : empty;
            var selectedPositions = current ? RowSelection.OrderedPositions(session.Result) : Array.Empty<int>();
            var selectedBranches = current ? RowSelection.BranchPaths(query) : Array.Empty<string>();
            var selectedMembers = current && set != null && OutputScope == PanelOutputScope.Selected
                ? set.ExpandMembers(query.Snapshot, selectedPositions, selectedBranches) : selectedPositions;
            if (current && set != null && OutputScope == PanelOutputScope.Selected)
                CountOutputVisits(selectedMembers.Count);
            if (current && _queryTree != null && set == null && !OutputsValues && selectedPositions.Count > 0)
                CountOutputVisits(_queryTree.DataCount);
            var selected = current && _queryTree != null
                ? BuildDataOutput(_queryTree, query, selectedPositions, selectedBranches, true) : empty;
            if (current && _inputTree != null && OutputScope == PanelOutputScope.Selected)
                CountOutputVisits((long)_inputTree.DataCount + selectedMembers.Count);
            var mask = !current || _inputTree == null ? new GH_Structure<GH_Boolean>() : OutputScope == PanelOutputScope.Filtered
                ? _filteredMask! : PanelOutputOperations.Mask(_inputTree, _retainedSources, selectedMembers);
            var paired = !current || PairedData == null ? empty : OutputScope == PanelOutputScope.Filtered
                ? _filteredPair ?? empty : PairedData.Output(query, selectedPositions, selectedBranches, FlattenTree, PanelPairedData.EmptyPartnersInScope(session.Options), CountOutputVisits, selected: true);
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
            _publishedValues = OutputValues;
        }

        private GH_Structure<IGH_Goo> BuildDataOutput(GH_Structure<IGH_Goo> source, PanelQueryResult query,
            IReadOnlyList<int> positions, IReadOnlyList<string> branches, bool selected)
        {
            if (selected && positions.Count == 0 && branches.Count == 0) return new GH_Structure<IGH_Goo>();
            if (query.DisplaySet != null) return OutputsValues
                ? PanelOutputOperations.DisplaySet(source, query, positions, branches, CountOutputVisits)
                : PanelOutputOperations.SetItems(source, query, positions, branches, FlattenTree, CountOutputVisits);
            if (OutputsValues) return PanelOutputOperations.Values(source, query, positions,
                selected ? Array.Empty<string>() : branches, CountOutputVisits);
            return selected ? PanelTreeOperations.Select(source, positions)
                : PanelQueryPolicy.HasOutputFilter(Session.Options) ? PanelTreeOperations.Filter(source, query) : source;
        }
    }
}
