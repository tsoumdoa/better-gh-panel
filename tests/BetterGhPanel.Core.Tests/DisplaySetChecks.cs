using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

internal static class DisplaySetChecks
{
    internal static void Run()
    {
        RoundedAndFilteredValues();
        ListLengthsAndSelection();
        GeometryValues();
        LargeSetsAndCancellation();
    }

    private static void RoundedAndFilteredValues()
    {
        var rows = new[] { Number(0, "{0}", 1.234), Number(1, "{0}", 1.233), Number(2, "{0}", 2), Number(0, "{1}", 1.234) };
        var sources = rows.Select((row, index) => new PanelSourceAddress("{9}", index + 10)).ToArray();
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 3), new PanelBranch("{1}", 1) }, sourceAddresses: sources);
        var options = new PanelQueryOptions { CreateSet = true, DecimalPlaces = 2, SortMode = PanelSortMode.ValueDescending };
        var set = PanelQueryEngine.Execute(snapshot, options);
        Check(set.RowPositions.SequenceEqual(new[] { 2, 0, 3 }) && set.DisplaySet!.Values[0].Number == 1.23 &&
            set.Statistics.NumericCount == 3 && set.Statistics.Minimum == 1.23,
            "Sets compare complete displayed values per branch, output displayed precision and sort/project statistics consistently");
        Check(set.SetOccurrencesAt(0)!.Locations.Select(address => address.ItemIndex).SequenceEqual(new[] { 10, 11 }) &&
            set.SetOccurrencesAt(0)!.Locations[0].Path == "{9}", "Display grouping keeps original input addresses");
        options.Search = "< 1.234";
        set = PanelQueryEngine.Execute(snapshot, options);
        Check(set.RowPositions.SequenceEqual(new[] { 1 }) && set.SetOccurrencesAt(1)!.Count == 1 && set.DisplaySet!.Values[1].Number == 1.23,
            "Filtering precedes set grouping so a hidden first occurrence cannot remove a visible equal display value");
        options.Search = "";
        options.DecimalPlaces = 3;
        Check(PanelQueryEngine.Execute(snapshot, options).RowPositions.Count == 4, "Changing decimals recomputes the displayed set");
        options.CreateSet = false;
        Check(PanelQueryEngine.Execute(snapshot, options).DisplaySet == null && snapshot.Rows[0].NumericValue == 1.234,
            "Turning Create set off restores original values without modifying the captured input");
        var strings = new PanelSnapshot(new[] {
            new PanelRow(0, "{0}", "Text", "same", null, stringValue: "same"),
            new PanelRow(1, "{0}", "Text", "same ", null, stringValue: "same "),
            new PanelRow(2, "{0}", "Text", "Same", null, stringValue: "Same") }, new[] { new PanelBranch("{0}", 3) });
        Check(PanelQueryEngine.Execute(strings, new PanelQueryOptions { CreateSet = true }).RowPositions.Count == 3,
            "Displayed text equality preserves case and trailing whitespace");
    }

    private static void ListLengthsAndSelection()
    {
        var counts = new[] { 3, 3, 5, 0, 0 };
        var rows = counts.SelectMany((count, branch) => Enumerable.Range(0, count).Select(index => Number(index, "{" + branch + "}", index % 2))).ToArray();
        var snapshot = new PanelSnapshot(rows, counts.Select((count, branch) => new PanelBranch("{" + branch + "}", count)).ToArray());
        var options = new PanelQueryOptions { CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength };
        var result = PanelQueryEngine.Execute(snapshot, options);
        Check(result.BranchPaths.SequenceEqual(new[] { "{0}", "{2}", "{3}" }) && result.RowPositions.Count == 8 &&
            result.Statistics.ItemCount == 3 && result.Statistics.NumericCount == 3 && result.Statistics.Minimum == 0 && result.Statistics.Maximum == 5,
            "List-length sets output the distinct counts 3, 5 and 0 without deduplicating items inside the counted branches");
        Check(result.DisplaySet!.BranchOccurrences["{0}"].SequenceEqual(new[] { "{0}", "{1}" }) &&
            result.DisplaySet.BranchOccurrences["{3}"].SequenceEqual(new[] { "{3}", "{4}" }),
            "Equal-length and empty-branch occurrences are recorded as branches");
        options.BranchPath = "{1}";
        var restricted = PanelQueryEngine.Execute(snapshot, options);
        Check(restricted.BranchPaths.SequenceEqual(new[] { "{1}" }), "Paths restrictions apply before list-length deduplication");
        var selection = new PanelRowSelection();
        selection.SelectAll(result, "{3}", includeEmptyBranches: true);
        Check(selection.Count == 1 && selection.OrderedPositions(result).Count == 0 && selection.BranchPaths(result).SequenceEqual(new[] { "{3}" }),
            "An empty source branch still has a selectable zero-valued set item");
        selection.Synchronize(restricted);
        Check(selection.Count == 0 && selection.BranchPaths(restricted).Count == 0, "Filtering removes hidden empty-branch selections");
        selection.SelectAll(result, includeEmptyBranches: true);
        Check(selection.BranchPaths(result).Count == 3, "Select all includes zero-valued list-length entries");
        selection.Clear();
        Check(selection.BranchPaths(result).Count == 0, "Clear removes item and empty-branch selections");
    }

    private static void GeometryValues()
    {
        var rows = new[] {
            new PanelRow(0, "{0}", "Circle", "radius", null, geometryInfo: new PanelGeometryInfo("Circle", 1000, units: "mm", metricName: "Radius")),
            new PanelRow(1, "{0}", "Curve", "curve", null, geometryInfo: new PanelGeometryInfo("Curve", units: "mm", canMeasure: true)),
            new PanelRow(2, "{0}", "Curve", "curve", null, geometryInfo: new PanelGeometryInfo("Curve", units: "mm", canMeasure: true)) };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 3) }, PanelUnitConversion.MillimetersToMeters);
        var query = new PanelQueryOptions { CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.Length,
            DecimalPlaces = 2, SortMode = PanelSortMode.ValueAscending };
        var measured = new Dictionary<int, PanelGeometryInfo> {
            [1] = new PanelGeometryInfo("Curve", 6283, units: "mm"), [2] = new PanelGeometryInfo("Curve", 2000, units: "mm") };
        var set = PanelQueryEngine.Execute(snapshot, query, measuredGeometry: measured);
        Check(PanelQueryEngine.RequiresGeometryMeasurements(query) && PanelQueryEngine.GeometryCandidates(snapshot, query).SequenceEqual(new[] { 1, 2 }),
            "Create set measures eligible offscreen geometry before grouping");
        Check(set.RowPositions.SequenceEqual(new[] { 2, 0 }) && set.DisplaySet!.Values[0].Number == 6.28 &&
            set.DisplaySet.Values[2].Number == 2 && set.SetOccurrencesAt(0)!.Count == 2,
            "Different geometry with equal displayed converted lengths collapses to one number and sorts numerically");
        query.SearchMode = PanelSearchMode.Value;
        query.SortMode = PanelSortMode.SourceOrder;
        Check(!PanelQueryEngine.RequiresGeometryMeasurements(query), "Numeric display sets do not measure unsupported geometry");
    }

    private static void LargeSetsAndCancellation()
    {
        var rows = Enumerable.Range(0, 100000).Select(index => Number(index, "{0}", index % 1000 + 0.001)).ToArray();
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", rows.Length) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { CreateSet = true, DecimalPlaces = 0 });
        Check(result.RowPositions.Count == 1000 && result.SetOccurrencesAt(0)!.Count == 100 && result.DisplaySet!.Values[0].Number == 0,
            "Large displayed sets keep complete occurrence counts and numeric output values");
        using var cancellation = new CancellationTokenSource();
        using var work = PanelQueryEngine.Start(snapshot, new PanelQueryOptions { CreateSet = true }, cancellation.Token);
        Check(!work.TryComplete(TimeSpan.Zero), "Set work yields within the query time budget");
        cancellation.Cancel();
        try { work.Complete(); throw new InvalidOperationException("Cancelled set completed"); }
        catch (OperationCanceledException) { }
    }

    private static PanelRow Number(int index, string path, double value) => new PanelRow(index, path, "Number", value.ToString("G12", System.Globalization.CultureInfo.InvariantCulture), value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
