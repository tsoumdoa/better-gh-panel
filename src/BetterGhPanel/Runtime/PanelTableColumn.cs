using System;
using System.Linq;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel.Runtime
{
    // Every column uses the same original-identity adapter as F output.
    internal sealed class PanelTableColumn
    {
        internal readonly IGH_Param Input;
        internal readonly PanelPairedData? Pair;
        internal readonly GH_Structure<IGH_Goo>? Tree, LiveTree;
        internal readonly string? Error;
        internal readonly bool Present;
        internal PanelSnapshot? Snapshot { get; private set; }
        internal IGH_Goo?[] Sources { get; private set; } = Array.Empty<IGH_Goo?>();
        internal CapturedMeasurementSource?[] Measurements { get; private set; } = Array.Empty<CapturedMeasurementSource?>();
        internal CaptureGraphWitness[] Graphs { get; private set; } = Array.Empty<CaptureGraphWitness>();
        internal bool HasGeometry { get; private set; }
        internal bool ShowCellTypes => PanelTableText.ShowCellTypes(Snapshot);
        internal bool Numeric => PanelTableText.NumericColumn(Snapshot);
        internal PanelTableColumn(IGH_Param input, PanelPairedData? pair, GH_Structure<IGH_Goo>? tree, string? error, bool present, GH_Structure<IGH_Goo>? liveTree)
        { Input = input; Pair = tree?.DataCount == 0 ? null : pair; Tree = tree; LiveTree = liveTree; Error = error; Present = present; }
        internal PanelRow? Row(int position) => Pair?.ItemAt(position) is int item ? Snapshot?.Rows[item] : null;
        internal void Capture(string units)
        {
            if (Snapshot != null || Pair == null || Tree == null) return;
            Snapshot = SnapshotCapture.CaptureForMeasurements(Tree, out var sources, out var measurements, out var graphs,
                PanelUnitConversion.None, units, null, null, null, null, null);
            Sources = sources; Measurements = measurements; Graphs = graphs;
            HasGeometry = Snapshot.Rows.Any(row => row.GeometryInfo != null || row.Components != null);
        }
        internal void ReleasePresentation()
        {
            Snapshot = null;
            Sources = Array.Empty<IGH_Goo?>(); Measurements = Array.Empty<CapturedMeasurementSource?>(); Graphs = Array.Empty<CaptureGraphWitness>();
            HasGeometry = false;
        }
        internal bool Current(out long visits)
        {
            visits = 0;
            if (!Present) return true;
            bool current = PanelTreeOperations.StructureStillMatchesWithVisits(LiveTree, Tree, out int count);
            visits += count;
            if (!current) return false;
            var snapshot = Snapshot;
            if (snapshot == null) return true;
            current = SnapshotCapture.MeasurementSourcesCurrentWithVisits(Sources, Measurements, out count); visits += count;
            if (!current) return false;
            current = SnapshotCapture.GraphSourcesCurrentWithVisits(Graphs, out long graphVisits); visits += graphVisits;
            if (!current) return false;
            current = SnapshotCapture.ManagedRowsCurrentWithVisits(snapshot, Sources, out count); visits += count;
            return current && ReferenceEquals(snapshot, Snapshot);
        }
        internal bool SummaryCurrent(PanelQueryResult result) => Pair == null || ReferenceEquals(Pair.SummaryResult, result);
        internal string Value(PanelRow row, PanelUnitConversion conversion, int decimals, double factor) =>
            PanelTableText.Value(row, conversion, decimals, factor);
        internal string Cell(PanelQueryResult result, int position, PanelUnitConversion conversion, int decimals, double factor, bool raw = false)
        {
            string Format(PanelRow row) => raw ? row.StringValue ?? PanelValuePresentation.FormatDisplay(row, PanelUnitConversion.None, PanelUnits.AutomaticDecimals) : Value(row, conversion, decimals, factor);
            if (Snapshot == null || Pair == null) return "";
            if (result.DisplaySet?.IsBranchSet == false)
            {
                if (!SummaryCurrent(result)) return "…";
                if (raw) return string.Join(Environment.NewLine, Pair.BranchItems(result.Snapshot, result.DisplaySet.Members[position], Array.Empty<string>(), false).Select(item => Format(Snapshot.Rows[item.Item])));
                Pair.SetSummaries.TryGetValue(position, out var summary);
                return Summary(summary, Format, true);
            }
            return Row(position) is PanelRow row ? Format(row) : "";
        }
        internal string BranchCell(PanelQueryResult result, string path, PanelUnitConversion conversion, int decimals, double factor, bool raw = false)
        {
            if (Pair == null || Snapshot == null) return "";
            if (!SummaryCurrent(result)) return "…";
            if (raw)
            {
                var members = result.DisplaySet?.IsBranchSet == true ? result.DisplaySet.BranchMembers[path] : result.RowPositions.Where(position => result.Snapshot.Rows[position].Path == path).ToArray();
                var paths = result.DisplaySet?.IsBranchSet == true ? result.DisplaySet.BranchOccurrences[path] : new[] { path };
                return string.Join(Environment.NewLine, Pair.BranchItems(result.Snapshot, members, paths, result.DisplaySet == null)
                    .Select(item => Snapshot.Rows[item.Item].StringValue ?? PanelValuePresentation.FormatDisplay(Snapshot.Rows[item.Item], PanelUnitConversion.None, PanelUnits.AutomaticDecimals)));
            }
            Pair.BranchSummaries.TryGetValue(path, out var summary);
            return Summary(summary, row => Value(row, conversion, decimals, factor), result.DisplaySet != null);
        }
        private string Summary(PanelPairedData.Summary? summary, Func<PanelRow, string> format, bool set)
        {
            if (Snapshot == null || summary == null || summary.Count == 0) return set ? "<empty set>" : "<empty branch>";
            return PanelTableText.Summary(summary.Preview.Select(index => format(Snapshot.Rows[index])).ToArray(), summary.Count, set);
        }
        internal static PanelTableSource? ViewSource(System.Collections.Generic.IReadOnlyList<PanelTableColumn> columns, bool reverse)
        {
            var maps = columns.Select(column => column.Pair).ToArray();
            if (!maps.Any(map => map?.HasUnequal == true)) return null;
            System.Collections.Generic.IEnumerable<PanelTableExtra?> Extras(PanelQueryResult result, System.Collections.Generic.IReadOnlyList<int> members, bool includeEmpty)
            {
                for (int column = 0; column < maps.Length; column++)
                {
                    yield return null;
                    var map = maps[column];
                    if (map?.HasUnequal != true) continue;
                    foreach (var step in map.BranchItemSteps(result.Snapshot, members, result.BranchPaths, includeEmpty, true))
                    {
                        if (!step.HasValue) { yield return null; continue; }
                        var cell = step.Value;
                        if (result.TryGetViewIndex(map.DataPositionAt(cell.Item), out _)) { yield return null; continue; }
                        var pair = map.PairAtPaired(cell.Item);
                        int index = cell.Item - pair.PairedOffset;
                        int withinBranch = reverse && index < pair.Data.ItemCount ? pair.Data.ItemCount - 1 - index : index;
                        long order = ((long)map.OriginalBranchOrder(pair) << 32) + (uint)withinBranch;
                        yield return new PanelTableExtra(cell.Path, pair.Data.Path + ":" + index, column, cell.Item, order);
                    }
                }
            }
            return new PanelTableSource(columns.Count, Extras);
        }
        internal PanelColumnSortKeys SortKeys(PanelUnitConversion conversion, int decimals, double factor)
        {
            // Capture both references now: later UI hiding/re-solving cannot change a worker's keys.
            var snapshot = Snapshot;
            var pair = Pair;
            PanelRow Convert(PanelRow row) => PanelTableText.PresentedRow(row, conversion, decimals, factor);
            return new PanelColumnSortKeys(position => pair?.ItemAt(position) is int item && snapshot != null ? Convert(snapshot.Rows[item]) : null,
                (data, members) => pair == null || snapshot == null ? Enumerable.Empty<PanelRow>() :
                    pair.BranchItemSteps(data, members, Array.Empty<string>(), false, true).Select(item => item.HasValue ? Convert(snapshot.Rows[item.Value.Item]) : null));
        }
    }
}
