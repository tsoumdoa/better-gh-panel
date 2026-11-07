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
        internal bool TableInputPresent => Params.Input.Skip(1).Any(InputPresent);
        private static bool InputPresent(IGH_Param input) => input.SourceCount != 0 ||
            input is Param_GenericObject generic && generic.PersistentData.PathCount != 0;

        private List<PanelTableColumn> CaptureTableColumns(IGH_DataAccess da, GH_Structure<IGH_Goo> inputTree,
            IReadOnlyList<PanelSourceAddress> sources, PanelPairedData? paired, GH_Structure<IGH_Goo>? pairedTree,
            PanelSnapshot? pairedSnapshot, string? pairedError, bool pairedPresent, string units)
        {
            var columns = new List<PanelTableColumn>();
            if (pairedPresent) columns.Add(new PanelTableColumn(Params.Input[1], paired, pairedTree, pairedError, true) { Snapshot = pairedSnapshot });
            for (int index = 2; index < Params.Input.Count; index++)
            {
                bool present = InputPresent(Params.Input[index]);
                PanelPairedData? map = null;
                GH_Structure<IGH_Goo>? tree = null;
                string? error = null;
                if (present)
                {
                    if (!da.GetDataTree(index, out GH_Structure<IGH_Goo> live)) live = new GH_Structure<IGH_Goo>();
                    tree = PanelTreeOperations.CopyStructure(live);
                    map = PanelOutputOperations.Pair(inputTree, tree, sources, TrimDepth, SimplifyTree, FlattenTree, ReverseTree, out error);
                }
                var column = new PanelTableColumn(Params.Input[index], map, tree, error, present);
                if (PanelSplit || Session.Options.SortColumn == column.Input.InstanceGuid) column.Capture(units);
                columns.Add(column);
            }
            return columns;
        }
        private PanelQueryOptions WithCurrentColumnSort(PanelQueryOptions saved)
        {
            var options = saved.Clone();
            var column = _tableColumns.FirstOrDefault(value => value.Input.InstanceGuid == options.SortColumn);
            if (column == null) options.SortColumn = Guid.Empty;
            else column.Capture(_geometryUnits);
            options.ColumnSortKeys = column?.SortKeys(UnitConversion, DecimalPlaces, RoundUpFactor);
            return options;
        }
        private void RefreshColumnSort()
        {
            var options = WithCurrentColumnSort(Session.Options);
            Session.PrepareColumnSort(options.SortColumn, options.ColumnSortKeys);
        }
        internal string TableTsv(PanelQueryResult result, IReadOnlyList<int> positions)
        {
            var titles = new[] { Params.Input[0].NickName }.Concat(TableColumns.Select(column => column.Input.NickName));
            if (Session.Options.DisplayFilterValue && Session.Options.SearchMode == PanelSearchMode.ListLength)
            {
                var selected = new HashSet<string>(RowSelection.BranchPaths(result), StringComparer.Ordinal);
                return PanelTableTsv.Build(titles, result.BranchPaths.Where(selected.Contains).Select(path =>
                {
                    int index = result.DisplaySet?.BranchIndices.TryGetValue(path, out int k) == true ? k : 0;
                    IReadOnlyList<string> values = new[] { result.Snapshot.BranchItemCounts[path].ToString(System.Globalization.CultureInfo.InvariantCulture) }
                        .Concat(TableColumns.Select(column => column.BranchCell(path, UnitConversion, DecimalPlaces, RoundUpFactor))).ToArray();
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
