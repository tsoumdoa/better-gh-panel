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
        internal PanelTableColumn? PairedColumn => _tableColumns.Count > 0 && _tableColumns[0].Input == Params.Input[1] ? _tableColumns[0] : null;
        // The colour menu needs F's captured type even while colour is off.
        private bool KeepPairedPresentation(PanelQueryOptions options) => options.SearchPaired || GroupByPaired ||
            LabelSource >= PanelLabelSource.Paired || ColourByPaired;
        private bool UsesPairedPreviewValues => LabelSource >= PanelLabelSource.Paired || ColourByPaired && ActiveValuePalette != PanelValuePalette.Off;

        internal bool PairedColourIsCategorical
        {
            get
            {
                if (!ColourByPaired || PairedColumn?.Snapshot is not PanelSnapshot snapshot) return false;
                if (PairedComparisonMode(Session.Options, Session.Result) is PanelSearchMode mode)
                    return PanelProperties.IsProperty(mode) && PanelProperties.Kind(mode) != PanelPropertyKind.Number;
                return !snapshot.HasGeometryMetrics && !snapshot.SupportsSearchMode(PanelSearchMode.Value) &&
                    snapshot.TypeCounts.Keys.Any(type => type == "Text" || type == "String");
            }
        }

        private static PanelSearchMode? PairedComparisonMode(PanelQueryOptions options, PanelQueryResult result) =>
            options.SearchPaired && options.DisplayFilterValue && result.DisplaySet == null ? options.SearchMode : (PanelSearchMode?)null;

        private void RefreshPairedPreviewSource()
        {
            var column = PairedColumn;
            if (column == null) return;
            var options = Session.Options;
            bool keep = PanelSplit || KeepPairedPresentation(options) || options.SortColumn == column.Input.InstanceGuid;
            if (keep == (column.Snapshot != null) || column.Pair == null) return;
            if (keep) column.Capture(_geometryUnits);
            else column.ReleasePresentation();
            ConfigureColumnGeometry();
        }
        internal PanelSnapshot SearchSnapshot(bool paired)
        {
            if (!paired) return Session.Snapshot;
            PairedColumn?.Capture(_geometryUnits);
            return PairedColumn?.Snapshot ?? PanelSnapshot.Empty;
        }
        internal void UpdateSearchTarget(PanelQueryOptions options) => Session.UpdateQuery(WithCurrentColumnSort(options));
        internal bool ShowsPairedComparison(PanelTableColumn column)
        {
            var options = Session.Options;
            return column == PairedColumn && options.SearchPaired && options.DisplayFilterValue;
        }

        internal bool ShowsPairedValueInDataList(PanelQueryResult result)
        {
            var options = Session.Options;
            return !EffectiveTableVisible && options.SearchPaired && options.DisplayFilterValue && result.DisplaySet?.IsPaired != true;
        }

        private string PairedComparisonValue(PanelTableColumn? column, int position) =>
            column?.Snapshot != null && column.Pair?.ItemAt(position) is int item
                ? column.ItemValue(item, UnitConversion, DecimalPlaces, RoundUpFactor, Session.Options.SearchMode) : "—";

        internal string ResultColumnCell(PanelTableColumn column, PanelQueryResult result, int position)
        {
            if (column == PairedColumn)
            {
                if (result.DisplaySet?.IsPaired == true) return result.DisplaySet.Values[position].Text;
                if (ShowsPairedComparison(column)) return PairedComparisonValue(column, position);
            }
            return column.Cell(result, position, UnitConversion, DecimalPlaces, RoundUpFactor);
        }
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
                if (PanelSplit || Session.Options.SortColumn == column.Input.InstanceGuid || KeepPairedPresentation(Session.Options)) column.Capture(units);
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
                    map = PanelOutputOperations.Pair(inputTree, tree, sources, TrimDepth, SimplifyTree, FlattenTree, ReverseTree, out error, CleanTree);
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
        private bool SummariesCurrent(PanelQueryResult result) => result.BranchStats != null
            ? TableColumns.All(column => result.StatGroups != null && column == PairedColumn || column.Snapshot == null || column.Pair == null || result.ColumnBranchStats.ContainsKey(column.Input.InstanceGuid))
            : !(result.DisplaySet != null || Session.Options.DisplaysDataFilterValue && Session.Options.SearchMode == PanelSearchMode.ListLength) ||
            TableColumns.All(column => column.SummaryCurrent(result));
        private void RequireTableSummaries(PanelQueryResult result)
        {
            if (!SummariesCurrent(result)) throw new InvalidOperationException("Column summaries are recomputing. Try copying again.");
        }
        private void ReleaseInactiveColumns(PanelQueryOptions options)
        {
            if (PanelSplit) return;
            foreach (var column in _tableColumns)
                if ((options.SortMode == PanelSortMode.SourceOrder || column.Input.InstanceGuid != options.SortColumn) &&
                    (column != PairedColumn || !KeepPairedPresentation(options)))
                    column.ReleasePresentation();
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
            ReleaseInactiveColumns(options);
            if (KeepPairedPresentation(options)) PairedColumn?.Capture(_geometryUnits);
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
            ColumnMeasurementCurrent, PairedColumn);

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
        private PanelTableSource? CurrentTableSource() => PanelSplit ? PanelTableColumn.ViewSource(TableColumns, !PanelMinimized) : null;
        private static int[] ColumnCopyItems(PanelTableColumn column, PanelQueryResult result,
            IReadOnlyList<int> positions, IReadOnlyList<string> branches) =>
            positions.SelectMany(position => column.CellItems(result, position))
                .Concat(branches.SelectMany(path => column.BranchItems(result, path))).Distinct().ToArray();

        internal string StatsTsv(PanelQueryResult result, bool includeColumns)
        {
            if (includeColumns) RequireTableSummaries(result);
            var columns = includeColumns ? TableColumns.Where(column => result.StatGroups == null || column != PairedColumn).ToArray() : Array.Empty<PanelTableColumn>();
            bool countOnly = result.StatRows.All(kind => kind == PanelStatKinds.Count);
            // A column without one shared metric/unit keeps the unit in each cell.
            var suffixes = new[] { result.StatHeaderSuffix }.Concat(columns.Select(column => result.ColumnBranchStats.TryGetValue(column.Input.InstanceGuid, out var branches)
                ? PanelBranchStats.CommonHeaderSuffix(branches.Values) : "")).ToArray();
            var titles = new[] { TableHeaderTitle(0) }.Concat(columns.Select(column => HeaderTitleForInput(column.Input)))
                .Select((title, index) => title + (countOnly ? "" : suffixes[index]));
            string keyHeader = result.StatGroups == null ? "" : PanelTableTsv.Escape(HeaderTitleForInput(Params.Input[1])) + "\t";
            var text = new System.Text.StringBuilder("Path\t" + keyHeader + "Stat\t" + string.Join("\t", titles.Select(PanelTableTsv.Escape)) + Environment.NewLine);
            foreach (string path in result.StatPaths)
                foreach (var kind in result.StatRows)
                {
                    var address = result.StatGroups == null ? new[] { path } : new[] { path, result.StatGroups[path].Key.Text };
                    IReadOnlyList<string> cells = address.Concat(new[] { PanelBranchStats.Name(kind), result.BranchStats![path].Format(kind, result.StatDecimals, suffixes[0].Length == 0) })
                        .Concat(columns.Select((column, index) => result.ColumnBranchStats.TryGetValue(column.Input.InstanceGuid, out var branches)
                            ? branches[path].Format(kind, result.StatDecimals, suffixes[index + 1].Length == 0) : "—")).ToArray();
                    text.AppendLine(string.Join("\t", cells.Select(PanelTableTsv.Escape)));
                }
            return text.ToString();
        }

        internal string SelectedStatText(PanelQueryResult result, IReadOnlyList<int> positions)
        {
            var selected = new HashSet<int>(positions);
            var lines = result.StatPaths.SelectMany(path => result.StatRows
                .Where(kind => result.BranchStats![path].Source(kind) >= 0 && selected.Contains(result.BranchStats[path].Source(kind)))
                .Select(kind => (Kind: kind, Text: result.BranchStats![path].Format(kind, result.StatDecimals)))).ToArray();
            // One value copies bare; several (for example Min and Max of one item) carry their stat name.
            return lines.Length == 1 ? lines[0].Text : string.Join(Environment.NewLine, lines.Select(line => PanelBranchStats.Name(line.Kind) + "	" + line.Text));
        }

        internal string TableTsv(PanelQueryResult result, IReadOnlyList<int> positions)
        {
            if (result.BranchStats != null) return StatsTsv(result, includeColumns: true);
            RequireTableSummaries(result);
            foreach (var column in TableColumns)
                column.RequireMeasurements(ColumnCopyItems(column, result, positions,
                    Session.Options.DisplaysDataFilterValue && Session.Options.SearchMode == PanelSearchMode.ListLength
                        ? RowSelection.BranchPaths(result).ToArray() : Array.Empty<string>()));
            var titles = new[] { TableHeaderTitle(0) + PairedSetDHeaderSuffix(result) }
                .Concat(TableColumns.Select((column, index) => TableHeaderTitle(index + 1) + column.HeaderSuffix(UnitConversion)));
            if (result.DisplaySet?.IsPaired != true && Session.Options.DisplaysDataFilterValue && Session.Options.SearchMode == PanelSearchMode.ListLength)
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
                    .Concat(TableColumns.Select(column => ResultColumnCell(column, result, position))).ToArray();
                return (result.Snapshot.Rows[position].Path, index, cells);
            }));
        }
    }
}
