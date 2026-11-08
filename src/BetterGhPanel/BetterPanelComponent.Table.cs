using System;
using System.Collections.Generic;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel
{
    public sealed partial class BetterPanelComponent
    {
        private readonly List<PanelTableColumn> _tableColumns = new List<PanelTableColumn>();
        internal int InputBranchCount => _inputTree?.PathCount ?? 0;
        internal IReadOnlyList<PanelTableColumn> TableColumns => _tableColumns;
        internal bool TableInputPresent => Params.Input.Skip(1).Where(IsTableDataInput).Any(InputPresent);
        private static bool InputPresent(IGH_Param input) => input.SourceCount != 0 ||
            input is not Param_GenericObject generic || generic.PersistentData.PathCount != 0;

        private List<PanelTableColumn> CaptureTableColumns(IGH_DataAccess da, GH_Structure<IGH_Goo> inputTree,
            IReadOnlyList<PanelSourceAddress> sources, PanelPairedData? paired, GH_Structure<IGH_Goo>? pairedTree,
            GH_Structure<IGH_Goo>? livePairedTree, string? pairedError, bool pairedPresent, string units)
        {
            var columns = new List<PanelTableColumn>();
            if (pairedPresent)
            {
                var column = new PanelTableColumn(Params.Input[1], paired, pairedTree, pairedError, true, livePairedTree);
                if (PanelSplit || Session.Options.SortColumn == column.Input.InstanceGuid) column.Capture(units);
                columns.Add(column);
            }
            for (int index = 2; index < Params.Input.Count; index++)
            {
                if (IsHeaderInput(Params.Input[index])) continue;
                bool present = InputPresent(Params.Input[index]);
                PanelPairedData? map = null;
                GH_Structure<IGH_Goo>? tree = null, liveTree = null;
                string? error = null;
                if (present)
                {
                    if (!da.GetDataTree(index, out GH_Structure<IGH_Goo> live)) live = new GH_Structure<IGH_Goo>();
                    liveTree = live;
                    tree = PanelTreeOperations.CopyStructure(live);
                    map = PanelOutputOperations.Pair(inputTree, tree, sources, TrimDepth, SimplifyTree, FlattenTree, ReverseTree, out error);
                }
                var column = new PanelTableColumn(Params.Input[index], map, tree, error, present, liveTree);
                if (PanelSplit || Session.Options.SortColumn == column.Input.InstanceGuid) column.Capture(units);
                columns.Add(column);
            }
            return columns;
        }
        private bool ColumnsCurrent(IReadOnlyList<PanelTableColumn> columns, out long visits)
        {
            visits = 0;
            if (columns.Any(column => column.Input == Params.Input[1]) != PairedInputPresent()) return false;
            foreach (var column in columns)
            {
                if (!Params.Input.Contains(column.Input) || InputPresent(column.Input) != column.Present) return false;
                bool current = column.Current(out long count); visits += count;
                if (!current) return false;
            }
            return true;
        }
        internal bool EffectiveTableVisible => (Attributes as BetterPanelAttributes)?.EffectiveTableVisible == true;
        internal bool TableSummariesCurrent => !PanelSplit || SummariesCurrent(Session.Result);
        private bool SummariesCurrent(PanelQueryResult result) => !(result.DisplaySet != null || Session.Options.DisplayFilterValue && Session.Options.SearchMode == PanelSearchMode.ListLength) ||
            TableColumns.All(column => column.SummaryCurrent(result));
        private void RequireTableSummaries(PanelQueryResult result)
        {
            if (!SummariesCurrent(result)) throw new InvalidOperationException("Column summaries are recomputing. Try copying again.");
        }
        private PanelQueryOptions WithCurrentColumnSort(PanelQueryOptions saved)
        {
            var options = saved.Clone();
            ResolveSortInput(options);
            var column = options.SortMode == PanelSortMode.SourceOrder ? null :
                _tableColumns.FirstOrDefault(value => value.Input.InstanceGuid == options.SortColumn);
            if (column == null)
            {
                if (options.SortColumn != Guid.Empty) { options.SortColumn = Guid.Empty; options.SortMode = PanelSortMode.SourceOrder; }
            }
            foreach (var item in _tableColumns)
                if (!PanelSplit && (options.SortMode == PanelSortMode.SourceOrder || !ReferenceEquals(item, column))) item.ReleasePresentation();
            column?.Capture(_geometryUnits);
            ConfigureColumnGeometry();
            options.ColumnSortKeys = column?.SortKeys(UnitConversion, DecimalPlaces, RoundUpFactor);
            return options;
        }
        private void RefreshColumnSort()
        {
            var options = WithCurrentColumnSort(Session.Options);
            Session.PrepareColumnSort(options.SortColumn, options.ColumnSortKeys);
            ConfigureColumnGeometry();
            Session.SetTableSource(CurrentTableSource(), false);
        }
        private void ConfigureColumnGeometry() => Session.ConfigureColumnGeometry(_tableColumns,
            (column, position) => column.Snapshot is PanelSnapshot snapshot && ColumnMeasurementCurrent(column, snapshot, position)
                ? PrepareGeometrySource(column.Sources[position], column.Measurements[position], false,
                    () => ColumnMeasurementCurrent(column, snapshot, position)) : null,
            ColumnMeasurementCurrent);

        private bool ColumnMeasurementCurrent(PanelTableColumn column, PanelSnapshot snapshot, int position)
        {
            var data = _measurementSnapshot;
            var context = _geometryContext;
            bool Owned() => data != null && !Locked && _session != null && !_session.IsDisposed &&
                ReferenceEquals(data, _session.Snapshot) && ReferenceEquals(data, _measurementSnapshot) &&
                _tableColumns.Contains(column) && Params.Input.Contains(column.Input) &&
                ReferenceEquals(snapshot, column.Snapshot) && column.Geometry?.IsAttachedTo(snapshot) == true &&
                position >= 0 && position < column.Measurements.Length && position < column.Sources.Length;
            if (!Owned()) return false;
            var capture = column.Measurements[position];
            var holder = column.Sources[position];
            try
            {
                if (GeometryContextCurrent(context) && (capture == null || SafeGooInspection.IsCurrentCapturedGeometry(holder, capture.Leaf)) &&
                    context.Equals(_geometryContext) && GeometryContextCurrent(context) && Owned() &&
                    ReferenceEquals(capture, column.Measurements[position]) && ReferenceEquals(holder, column.Sources[position])) return true;
            }
            catch (Exception) { /* A replaced or disposed column source is stale. */ }
            finally { GC.KeepAlive(capture?.Geometry); }
            if (data != null) RecaptureStaleMeasurementSource(data);
            return false;
        }

        private void RefreshTableSource()
        {
            (Attributes as BetterPanelAttributes)?.PreserveTableScroll();
            bool restoring = _restoringView;
            _restoringView = true;
            try { Session.SetTableSource(CurrentTableSource(), true); }
            finally { _restoringView = restoring; }
        }
        // Query mode decides whether to consume summaries; viewport and zoom never change this provider.
        private PanelTableSource? CurrentTableSource() => PanelSplit ? PanelTableColumn.ViewSource(TableColumns, ReverseTree, !PanelMinimized) : null;
        private static int[] ColumnCopyItems(PanelTableColumn column, PanelQueryResult result,
            IReadOnlyList<int> positions, IReadOnlyList<string> branches) =>
            positions.SelectMany(position => column.CellItems(result, position))
                .Concat(branches.SelectMany(path => column.BranchItems(result, path))).Distinct().ToArray();

        internal string TableTsv(PanelQueryResult result, IReadOnlyList<int> positions)
        {
            RequireTableSummaries(result);
            foreach (var column in TableColumns)
                column.RequireMeasurements(ColumnCopyItems(column, result, positions,
                    Session.Options.DisplayFilterValue && Session.Options.SearchMode == PanelSearchMode.ListLength
                        ? RowSelection.BranchPaths(result).ToArray() : Array.Empty<string>()));
            var titles = new[] { TableHeaderTitle(0) }.Concat(TableColumns.Select((column, index) => TableHeaderTitle(index + 1) + column.HeaderSuffix(UnitConversion)));
            if (Session.Options.DisplayFilterValue && Session.Options.SearchMode == PanelSearchMode.ListLength)
            {
                var selected = new HashSet<string>(RowSelection.BranchPaths(result), StringComparer.Ordinal);
                var numbers = result.BranchPaths.Select((path, index) => (path, index)).ToDictionary(value => value.path, value => value.index, StringComparer.Ordinal);
                return PanelTableTsv.Build(titles, result.BranchPaths.Where(selected.Contains).Select(path =>
                {
                    int index = result.DisplaySet?.BranchIndices.TryGetValue(path, out int k) == true ? k : numbers[path];
                    IReadOnlyList<string> values = new[] { result.Snapshot.BranchItemCounts[path].ToString(System.Globalization.CultureInfo.InvariantCulture) }
                        .Concat(TableColumns.Select(column => column.BranchCell(result, path, UnitConversion, DecimalPlaces, RoundUpFactor))).ToArray();
                    return (path, index, values);
                }));
            }
            return PanelTableTsv.Build(titles, positions.Select(position =>
            {
                PanelItemOrder.TryGetNumber(result, position, out int index);
                IReadOnlyList<string> cells = new[] { DisplayValue(result.Snapshot, result.Snapshot.Rows[position], position) }
                    .Concat(TableColumns.Select(column => column.Cell(result, position, UnitConversion, DecimalPlaces, RoundUpFactor))).ToArray();
                return (result.Snapshot.Rows[position].Path, index, cells);
            }));
        }
    }
}
