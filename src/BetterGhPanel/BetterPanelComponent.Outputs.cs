using System;
using System.Collections.Generic;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel
{
    public sealed partial class BetterPanelComponent
    {
        private readonly PanelRowSelection _committedSelection = new PanelRowSelection();
        private PanelRowSelection? _queryPreviewSelection;
        internal PanelRowSelection CommittedSelection => _committedSelection;
        internal PanelRowSelection RowSelection => _queryPreviewSelection ?? _committedSelection;
        internal static bool CanOutputValuesFor(bool createSet, PanelQueryOptions options) => !options.BranchStats && (createSet || options.DisplayFilterValue);
        internal bool CanOutputValues => !BranchStatsTree && CanOutputValuesFor(CreateSetTree, Session.Options);
        internal bool OutputsValues => CanOutputValues && OutputValues == PanelOutputValues.Values;
        private GH_Structure<IGH_Goo>? _inputTree, _preConversionTree;
        private GH_Structure<IGH_Goo>? _liveInputTree, _itemOutputTree;
        private IReadOnlyList<PanelSourceAddress> _retainedSources = Array.Empty<PanelSourceAddress>();
        private string? _pairedError;
        internal PanelPairedData? PairedData { get; private set; }
        internal string? PairedError => _pairedError;
        internal bool CanSplit => TableColumns.Any(column => column.Present && column.Pair != null) && Session.HasCurrentResult;
        private long _selectionVersion, _publishedSelectionVersion;
        private PanelOutputScope _publishedScope;
        private PanelOutputValues _publishedValues;
        private PanelQueryResult? _cachedOutputResult;
        private GH_Structure<IGH_Goo>? _filteredData, _filteredPair;
        private GH_Structure<GH_Boolean>? _filteredMask;

        private void UpdateOutputDescriptions()
        {
            if (Params.Output.Count < 4) return;
            var scope = OutputScope == PanelOutputScope.Filtered ? "Results" : "Selection";
            Params.Output[2].Description = MaskDescription + " B + F follow: " + scope + ".";
            Params.Output[3].Description = PairedDescription + " B + F follow: " + scope + ".";
        }

        private void ClearOutputSources()
        {
            (Attributes as BetterPanelAttributes)?.ClearTableWidths();
            _inputTree = _preConversionTree = null;
            _liveInputTree = _itemOutputTree = null;
            _captureGraphs = Array.Empty<CaptureGraphWitness>();
            _captureHasGeometry = false;
            _retainedSources = Array.Empty<PanelSourceAddress>();
            _pairedError = null;
            PairedData = null;
            _tableColumns.Clear();
            if (_outputOptions != null) { _outputOptions.ColumnSortKeys = null; _outputOptions.TableSource = null; }
            _cachedOutputResult = null;
            _filteredData = _filteredPair = null;
            _filteredMask = null;
        }

        private void RefreshFormulaItems()
        {
            if (_queryTree == null || _preConversionTree == null) return;
            var presented = Formula == null ? _presentedScalars : CapturedScalar.Convert(_scalarInputs, UnitConversion, RoundUpFactor, DecimalPlaces, Formula);
            var items = Formula == null ? _queryTree : PanelUnitOperations.ApplyCaptured(_preConversionTree, UnitConversion,
                _scalarInputs, RoundUpFactor, DecimalPlaces, presented, Formula);
            _itemOutputTree = PanelUnitOperations.OutputItems(items, presented);
            _cachedOutputResult = null;
        }

        private void PublishOutputs(IGH_DataAccess da, Func<bool> solveCurrent)
        {
            var session = Session;
            var items = _itemOutputTree ?? _queryTree;
            _outputOptions = session.Options;
            _outputOptions.ColumnSortKeys = null; _outputOptions.TableSource = null;
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
                _filteredData = BuildDataOutput(items!, query, query.RowPositions, query.BranchPaths, false);
                _filteredPair = PairedData?.Output(query, query.RowPositions, query.BranchPaths, FlattenTree, PanelPairedData.EmptyPartnersInScope(session.Options), CountOutputVisits);
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
            var data = current ? _filteredData ?? empty : !filtering && items != null ? items : empty;
            var selectedPositions = current ? RowSelection.OrderedPositions(session.Result) : Array.Empty<int>();
            var selectedBranches = current ? RowSelection.BranchPaths(query) : Array.Empty<string>();
            var selectedMembers = current && set != null && OutputScope == PanelOutputScope.Selected
                ? set.ExpandMembers(query.Snapshot, selectedPositions, selectedBranches) : selectedPositions;
            if (current && set != null && OutputScope == PanelOutputScope.Selected)
                CountOutputVisits(selectedMembers.Count);
            if (current && _queryTree != null && set == null && !OutputsValues && selectedPositions.Count > 0)
                CountOutputVisits(_queryTree.DataCount);
            var selected = current && _queryTree != null
                ? BuildDataOutput(items!, query, selectedPositions, selectedBranches, true) : empty;
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

        private GH_Structure<IGH_Goo> StatsOutput(PanelQueryResult query, IReadOnlyList<int> positions, bool selected)
        {
            var tree = new GH_Structure<IGH_Goo>();
            var selection = selected ? new HashSet<int>(positions) : null;
            foreach (string path in query.BranchPaths)
            {
                var stats = query.BranchStats![path];
                var ghPath = new GH_Path();
                if (!ghPath.FromString(path)) throw new InvalidOperationException("Invalid captured branch path.");
                if (!selected) tree.EnsurePath(ghPath);
                foreach (var stat in stats.Output(StatKinds, selection))
                {
                    var kind = stat.Kind;
                    var value = stat.Number;
                    tree.EnsurePath(ghPath).Add(!value.HasValue ? null! : kind == PanelStatKinds.Count ? (IGH_Goo)new GH_Integer((int)value.Value) : new GH_Number(value.Value));
                }
            }
            return tree;
        }

        private GH_Structure<IGH_Goo> BuildDataOutput(GH_Structure<IGH_Goo> source, PanelQueryResult query,
            IReadOnlyList<int> positions, IReadOnlyList<string> branches, bool selected)
        {
            if (selected && positions.Count == 0 && branches.Count == 0) return new GH_Structure<IGH_Goo>();
            if (query.BranchStats != null) return StatsOutput(query, positions, selected);
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
