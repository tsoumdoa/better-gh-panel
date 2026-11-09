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
        internal GeometryMeasurementQueue<PanelSnapshot, PanelGeometryInfo>? Geometry;
        private PanelGeometryInfo? _headerMetric;
        internal bool HomogeneousMetric => _headerMetric != null && _headerMetric.MetricLabel.Length > 0;
        internal string HeaderSuffix(PanelUnitConversion conversion) => PanelTableText.GeometryHeaderSuffix(_headerMetric, conversion);
        internal bool CellIsNumeric(PanelRow? row) => !HomogeneousMetric && row?.NumericValue.HasValue == true ||
            row != null && HomogeneousMetric && row.GeometryInfo != null && (PanelTableText.HasGeometryMetric(row.GeometryInfo) || !row.IsValid);
        internal bool CellShowsType(PanelRow? row) => ShowCellTypes && row != null &&
            !PanelTableText.HasGeometryMetric(row.GeometryInfo) && (row.GeometryInfo == null || StockTypeLabels.SeparateLabel(row.TypeName));
        internal float CellPadding(PanelRow? row) => (CellShowsType(row) ? 60 : 12) +
            (row?.Validity == PanelValidity.VerifiedInvalid || row?.Validity == PanelValidity.Unverified ? 12 : 0);
        internal string HeaderUnit(PanelUnitConversion conversion) => PanelTableText.GeometryHeaderUnit(_headerMetric, conversion);
        internal string MetricTooltip(PanelUnitConversion conversion) => !HomogeneousMetric ? "" :
            _headerMetric!.MetricName + (HeaderUnit(conversion).Length > 0 ? " · " + HeaderUnit(conversion) : "");
        internal string MetricLane(PanelRow? row) => !HomogeneousMetric && row?.IsValid == true && PanelTableText.HasGeometryMetric(row.GeometryInfo)
            ? StockTypeLabels.ShortName(row.TypeName) + (row.GeometryInfo!.MetricLabel.Length > 0 ? " " + row.GeometryInfo.MetricLabel : "") : "";
        internal PanelGeometryInfo? Details(int item) => Snapshot == null ? null :
            Geometry?.Get(Snapshot, item) ?? (Geometry?.Has(Snapshot, item) == true
                ? Snapshot.Rows[item].GeometryInfo?.WithUnavailableMeasurement() : Snapshot.Rows[item].GeometryInfo);
        internal bool Measuring(int item) => Snapshot != null && item >= 0 && Snapshot.Rows[item].IsValid &&
            Snapshot.Rows[item].GeometryInfo?.CanMeasure == true && !Snapshot.Rows[item].GeometryInfo!.Value.HasValue &&
            Geometry?.Has(Snapshot, item) != true;
        internal IEnumerable<int> CellItems(PanelQueryResult result, int position)
        {
            if (result.DisplaySet?.IsBranchSet == false && result.TableSummaries.TryGetValue(Input.InstanceGuid, out var summary) &&
                summary.Sets.TryGetValue(position, out var cell)) return cell.Preview.Where(item => item >= 0);
            return Pair?.ItemAt(position) is int item ? new[] { item } : Array.Empty<int>();
        }
        internal IEnumerable<int> BranchItems(PanelQueryResult result, string path) =>
            result.TableSummaries.TryGetValue(Input.InstanceGuid, out var summary) && summary.Branches.TryGetValue(path, out var cell)
                ? cell.Preview.Where(item => item >= 0) : Enumerable.Empty<int>();
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
            _headerMetric = PanelTableText.CommonGeometryMetric(Snapshot);
        }
        internal void ReleasePresentation()
        {
            var retired = Geometry; Geometry = null;
            _headerMetric = null;
            Snapshot = null;
            Sources = Array.Empty<IGH_Goo?>(); Measurements = Array.Empty<CapturedMeasurementSource?>(); Graphs = Array.Empty<CaptureGraphWitness>();
            HasGeometry = false;
            retired?.Dispose();
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
        internal void RequireMeasurements(IEnumerable<int> items)
        {
            foreach (int item in items)
                if (Measuring(item) || Snapshot?.Rows[item].IsValid == true && Snapshot.Rows[item].GeometryInfo?.CanMeasure == true &&
                    Details(item)?.Value == null)
                    throw new InvalidOperationException("A selected measurement is unavailable.");
        }
        internal bool SummaryCurrent(PanelQueryResult result) => Pair == null || result.TableSummaries.ContainsKey(Input.InstanceGuid);
        internal string BranchExtraNote(PanelQueryResult result, string path, string title) =>
            result.TableSummaries.TryGetValue(Input.InstanceGuid, out var summary) && summary.Extras.TryGetValue(path, out int count) && count > 0
                ? "+" + count + " only in " + title : "";
        internal string BranchCellText(PanelQueryResult result, string path, PanelUnitConversion conversion, int decimals, double factor, string title)
        {
            string value = BranchCell(result, path, conversion, decimals, factor);
            string note = BranchExtraNote(result, path, title);
            return value + (note.Length > 0 ? " · " + note : "");
        }
        internal string ItemValue(int item, PanelUnitConversion conversion, int decimals, double factor)
        {
            var row = Snapshot!.Rows[item];
            if (row.GeometryInfo == null) return PanelTableText.Value(row, conversion, decimals, factor);
            return PanelTableText.GeometryCell(row, Details(item), HomogeneousMetric, Measuring(item), conversion, decimals, factor);
        }
        internal string ItemTooltip(int item, PanelUnitConversion conversion, int decimals, double factor)
        {
            var row = Snapshot!.Rows[item];
            var geometry = Details(item);
            string value = PanelValuePresentation.FormatDisplay(row, conversion, decimals, geometry, roundUpFactor: factor);
            if (!row.IsValid)
                return value + "\n" + (string.IsNullOrWhiteSpace(row.InvalidReason)
                    ? row.Validity == PanelValidity.VerifiedInvalid ? "Geometry or value is invalid." : "Validity could not be verified." : row.InvalidReason);
            return geometry == null ? value : geometry.Description + "\n" + geometry.FormatDetails(conversion, decimals, factor) +
                (Measuring(item) ? "\nCalculating measurement\u2026" : geometry.MeasurementUnavailable ? "\nMeasurement unavailable." : "");
        }
        internal string Cell(PanelQueryResult result, int position, PanelUnitConversion conversion, int decimals, double factor, bool raw = false)
        {
            string Format(int item) => raw ? Snapshot!.Rows[item].StringValue ?? PanelValuePresentation.FormatDisplay(Snapshot.Rows[item], PanelUnitConversion.None, PanelUnits.AutomaticDecimals) : ItemValue(item, conversion, decimals, factor);
            if (Snapshot == null || Pair == null) return "";
            if (result.DisplaySet?.IsBranchSet == false)
            {
                if (!SummaryCurrent(result)) return "…";
                if (raw) return string.Join(Environment.NewLine, result.DisplaySet.Members[position].Select(Pair.ItemAt)
                    .Select(item => item.HasValue ? Format(item.Value) : "—"));
                var summary = result.TableSummaries[Input.InstanceGuid].Sets[position];
                if (!raw && summary.Preview.Any(item => item >= 0 && Measuring(item))) return "measuring…";
                return PanelTableText.Summary(summary.Preview.Select(item => item < 0 ? "—" : Format(item)).ToArray(), summary.Count);
            }
            return Pair.ItemAt(position) is int paired ? Format(paired) : "";
        }
        internal string BranchCell(PanelQueryResult result, string path, PanelUnitConversion conversion, int decimals, double factor, bool raw = false)
        {
            if (Pair == null || Snapshot == null) return "";
            if (!SummaryCurrent(result)) return "…";
            if (raw)
            {
                var members = result.DisplaySet?.IsBranchSet == true ? result.DisplaySet.BranchMembers[path] : result.RowPositions.Where(position => result.Snapshot.Rows[position].Path == path).ToArray();
                var paths = result.DisplaySet?.IsBranchSet == true ? result.DisplaySet.BranchOccurrences[path] : new[] { path };
                string Raw(int item) => Snapshot.Rows[item].StringValue ??
                    PanelValuePresentation.FormatDisplay(Snapshot.Rows[item], PanelUnitConversion.None, PanelUnits.AutomaticDecimals);
                return string.Join(Environment.NewLine, result.DisplaySet?.IsBranchSet == true
                    ? members.Select(Pair.ItemAt).Select(item => item.HasValue ? Raw(item.Value) : "—")
                    : Pair.BranchItems(result.Snapshot, members, paths, result.DisplaySet == null).Select(item => Raw(item.Item)));
            }
            result.TableSummaries[Input.InstanceGuid].Branches.TryGetValue(path, out var summary);
            if (summary?.Preview.Any(item => item >= 0 && Measuring(item)) == true) return "measuring…";
            return summary == null ? result.DisplaySet != null ? "<empty set>" : "<empty branch>" :
                PanelTableText.Summary(summary.Preview.Select(item => item < 0 ? "—" : ItemValue(item, conversion, decimals, factor)).ToArray(), summary.Count, result.DisplaySet != null);
        }
        private PanelTableSummarySource SummarySource()
        {
            var pair = Pair!;
            return new PanelTableSummarySource(Input.InstanceGuid, pair.ItemAt,
                (result, members, includeEmpty) => pair.BranchItemSteps(result.Snapshot, members, result.BranchPaths, includeEmpty, true),
                path => pair.BranchesAt(path).Select(branch => Math.Max(0, branch.Paired.ItemCount - branch.Data.ItemCount)));
        }
        internal static PanelTableSource? ViewSource(System.Collections.Generic.IReadOnlyList<PanelTableColumn> columns, bool summariesVisible = false)
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
                        long order = ((long)map.OriginalBranchOrder(pair) << 32) + (uint)index;
                        yield return new PanelTableExtra(cell.Path, pair.Data.Path + ":" + index, column, cell.Item, order);
                    }
                }
            }
            return new PanelTableSource(columns.Count, hasExtras ? Extras : (Func<PanelQueryResult, IReadOnlyList<int>, bool, IEnumerable<PanelTableExtra?>>?)null, summaries);
        }
        internal PanelColumnSortKeys SortKeys(PanelUnitConversion conversion, int decimals, double factor, IReadOnlyDictionary<int, PanelGeometryInfo?>? metrics = null)
        {
            // Capture both references now: later UI hiding/re-solving cannot change a worker's keys.
            var snapshot = Snapshot;
            var pair = Pair;
            bool homogeneous = HomogeneousMetric;
            PanelRow? Convert(int item, bool summary = false)
            {
                var row = snapshot!.Rows[item];
                var geometry = metrics != null && metrics.TryGetValue(item, out var value) ? value ?? row.GeometryInfo?.WithUnavailableMeasurement() : row.GeometryInfo;
                return PanelTableText.ColumnSortRow(snapshot, row, geometry, homogeneous, conversion, decimals, factor, summary);
            }
            return new PanelColumnSortKeys(position => pair?.ItemAt(position) is int item && snapshot != null ? Convert(item) : null,
                (data, members) => pair == null || snapshot == null ? Enumerable.Empty<PanelRow>() :
                    members.Select(position => pair.ItemAt(position) is int item ? Convert(item, true) : null));
        }
    }
}
