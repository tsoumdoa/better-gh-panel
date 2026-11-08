using System;
using System.Linq;
using System.Collections.Generic;
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
        internal bool SummaryCurrent(PanelQueryResult result) => Pair == null || result.TableSummaries.ContainsKey(Input.InstanceGuid);
        internal string BranchExtraNote(PanelQueryResult result, string path) =>
            result.TableSummaries.TryGetValue(Input.InstanceGuid, out var summary) && summary.Extras.TryGetValue(path, out int count) && count > 0
                ? "+" + count + " only in " + Input.NickName : "";
        internal string SetExtraNote(PanelQueryResult result, int position) => BranchExtraNote(result, result.Snapshot.Rows[position].Path);
        internal string BranchCellText(PanelQueryResult result, string path, PanelUnitConversion conversion, int decimals, double factor)
        {
            string value = BranchCell(result, path, conversion, decimals, factor);
            string note = BranchExtraNote(result, path);
            return value + (note.Length > 0 ? " · " + note : "");
        }
        internal string CellText(PanelQueryResult result, int position, PanelUnitConversion conversion, int decimals, double factor)
        {
            string value = Cell(result, position, conversion, decimals, factor);
            string note = SetExtraNote(result, position);
            return value + (note.Length > 0 ? " · " + note : "");
        }
        internal string Value(PanelRow row, PanelUnitConversion conversion, int decimals, double factor) =>
            PanelTableText.Value(row, conversion, decimals, factor);
        internal string Cell(PanelQueryResult result, int position, PanelUnitConversion conversion, int decimals, double factor, bool raw = false)
        {
            string Format(PanelRow row) => raw ? row.StringValue ?? PanelValuePresentation.FormatDisplay(row, PanelUnitConversion.None, PanelUnits.AutomaticDecimals) : Value(row, conversion, decimals, factor);
            if (Snapshot == null || Pair == null) return "";
            if (result.DisplaySet?.IsBranchSet == false)
            {
                if (!SummaryCurrent(result)) return "…";
                if (raw) return string.Join(Environment.NewLine, result.DisplaySet.Members[position].Select(Pair.ItemAt)
                    .Where(item => item.HasValue).Select(item => Format(Snapshot.Rows[item!.Value])));
                var summary = result.TableSummaries[Input.InstanceGuid].Sets[position];
                return PanelTableText.Summary(summary.Preview.Select(item => Format(Snapshot.Rows[item])).ToArray(), summary.Count);
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
                var items = result.DisplaySet?.IsBranchSet == true ? members.Select(Pair.ItemAt).Where(item => item.HasValue).Select(item => item!.Value) :
                    Pair.BranchItems(result.Snapshot, members, paths, result.DisplaySet == null).Select(item => item.Item);
                return string.Join(Environment.NewLine, items.Select(item => Snapshot.Rows[item].StringValue ??
                    PanelValuePresentation.FormatDisplay(Snapshot.Rows[item], PanelUnitConversion.None, PanelUnits.AutomaticDecimals)));
            }
            result.TableSummaries[Input.InstanceGuid].Branches.TryGetValue(path, out var summary);
            return summary == null ? result.DisplaySet != null ? "<empty set>" : "<empty branch>" :
                PanelTableText.Summary(summary.Preview.Select(item => Value(Snapshot.Rows[item], conversion, decimals, factor)).ToArray(), summary.Count, result.DisplaySet != null);
        }
        private PanelTableSummarySource SummarySource()
        {
            var pair = Pair!;
            return new PanelTableSummarySource(Input.InstanceGuid, pair.ItemAt,
                (result, members, includeEmpty) => pair.BranchItemSteps(result.Snapshot, members, result.BranchPaths, includeEmpty, true),
                path => pair.BranchesAt(path).Select(branch => Math.Max(0, branch.Paired.ItemCount - branch.Data.ItemCount)));
        }
        internal static PanelTableSource? ViewSource(System.Collections.Generic.IReadOnlyList<PanelTableColumn> columns, bool reverse, bool summariesVisible = false)
        {
            var maps = columns.Select(column => column.Pair).ToArray();
            bool hasExtras = maps.Any(map => map?.HasUnequal == true);
            var summaries = summariesVisible ? columns.Where(column => column.Pair != null && column.Snapshot != null)
                .Select(column => column.SummarySource()).ToArray() : Array.Empty<PanelTableSummarySource>();
            if (!hasExtras && summaries.Length == 0) return null;
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
            return new PanelTableSource(columns.Count, hasExtras ? Extras : (Func<PanelQueryResult, IReadOnlyList<int>, bool, IEnumerable<PanelTableExtra?>>?)null, summaries);
        }
        internal PanelColumnSortKeys SortKeys(PanelUnitConversion conversion, int decimals, double factor)
        {
            // Capture both references now: later UI hiding/re-solving cannot change a worker's keys.
            var snapshot = Snapshot;
            var pair = Pair;
            PanelRow Convert(PanelRow row) => PanelTableText.PresentedRow(row, conversion, decimals, factor);
            return new PanelColumnSortKeys(position => pair?.ItemAt(position) is int item && snapshot != null ? Convert(snapshot.Rows[item]) : null,
                (data, members) => pair == null || snapshot == null ? Enumerable.Empty<PanelRow>() :
                    members.Select(position => pair.ItemAt(position) is int item ? Convert(snapshot.Rows[item]) : null));
        }
    }
}
