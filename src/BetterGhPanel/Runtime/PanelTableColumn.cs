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
        internal readonly GH_Structure<IGH_Goo>? Tree;
        internal readonly string? Error;
        internal readonly bool Present;
        internal PanelSnapshot? Snapshot;
        internal PanelTableColumn(IGH_Param input, PanelPairedData? pair, GH_Structure<IGH_Goo>? tree, string? error, bool present)
        { Input = input; Pair = tree?.DataCount == 0 ? null : pair; Tree = tree; Error = error; Present = present; }
        internal PanelRow? Row(int position) => Pair?.ItemAt(position) is int item ? Snapshot?.Rows[item] : null;
        internal void Capture(string units)
        {
            if (Snapshot == null && Pair != null && Tree != null)
                Snapshot = SnapshotCapture.CaptureForMeasurements(Tree, out _, out _, out _, PanelUnitConversion.None, units, null, null, null, null, null);
        }
        internal string Value(PanelRow row, PanelUnitConversion conversion, int decimals, double factor) =>
            PanelTableText.Value(row, conversion, decimals, factor);
        internal string Cell(PanelQueryResult result, int position, PanelUnitConversion conversion, int decimals, double factor, bool raw = false)
        {
            string Format(PanelRow row) => raw ? row.StringValue ?? PanelValuePresentation.FormatDisplay(row, PanelUnitConversion.None, PanelUnits.AutomaticDecimals) : Value(row, conversion, decimals, factor);
            if (Snapshot == null || Pair == null) return "";
            if (result.DisplaySet?.IsBranchSet == false)
            {
                if (raw) return string.Join(Environment.NewLine, Pair.BranchItems(result.Snapshot, result.DisplaySet.Members[position], Array.Empty<string>(), false).Select(item => Format(Snapshot.Rows[item.Item])));
                Pair.SetSummaries.TryGetValue(position, out var summary);
                return Summary(summary, Format, true);
            }
            return Row(position) is PanelRow row ? Format(row) : "";
        }
        internal string BranchCell(string path, PanelUnitConversion conversion, int decimals, double factor)
        {
            if (Pair == null || !Pair.BranchSummaries.TryGetValue(path, out var summary)) return "";
            return Summary(summary, row => Value(row, conversion, decimals, factor), true);
        }
        private string Summary(PanelPairedData.Summary? summary, Func<PanelRow, string> format, bool set)
        {
            if (Snapshot == null || summary == null || summary.Count == 0) return set ? "<empty set>" : "";
            return PanelTableText.Summary(summary.Preview.Select(index => format(Snapshot.Rows[index])).ToArray(), summary.Count);
        }
        internal PanelColumnSortKeys SortKeys(PanelUnitConversion conversion, int decimals, double factor)
        {
            // Capture both references now: later UI hiding/re-solving cannot change a worker's keys.
            var snapshot = Snapshot;
            var pair = Pair;
            PanelRow Convert(PanelRow row) => PanelTableText.PresentedRow(row, conversion, decimals, factor);
            return new PanelColumnSortKeys(position => pair?.ItemAt(position) is int item && snapshot != null ? Convert(snapshot.Rows[item]) : null,
                (data, members) => pair == null || snapshot == null ? Enumerable.Empty<PanelRow>() :
                    pair.BranchItems(data, members, Array.Empty<string>(), false).Select(item => Convert(snapshot.Rows[item.Item])));
        }
    }
}
