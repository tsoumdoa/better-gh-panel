using System;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using BetterGhPanel.Runtime;

internal static class CapturePolicyChecks
{
    internal static void Run()
    {
        SnapshotCapabilitiesAndDiagnostics();
        SemanticEmptyAndGroupSetKeys();
        ItemSetSourceStatuses();
        BranchSetSourceStatuses();
    }

    private static void SnapshotCapabilitiesAndDiagnostics()
    {
        var rows = new[] {
            Row(0, "{0}", "Invalid number", PanelValidity.VerifiedInvalid, capabilities: PanelSearchCapabilities.Value),
            Row(1, "{0}", "unverified", PanelValidity.Unverified, typeKnown: false),
            Row(2, "{0}", "arc", PanelValidity.VerifiedValid, capabilities: PanelSearchCapabilities.Length),
            Row(3, "{0}", "surface", PanelValidity.VerifiedInvalid, capabilities: PanelSearchCapabilities.Area) };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 4) });
        Check(snapshot.SupportsSearchMode(PanelSearchMode.Text) && snapshot.SupportsSearchMode(PanelSearchMode.Regex) &&
            snapshot.SupportsSearchMode(PanelSearchMode.ListLength) && snapshot.SupportsSearchMode(PanelSearchMode.Value) &&
            snapshot.SupportsSearchMode(PanelSearchMode.Length) && snapshot.SupportsSearchMode(PanelSearchMode.Area) &&
            !snapshot.SupportsSearchMode(PanelSearchMode.Volume), "Capabilities are the union of captured input types, including trusted invalids");
        Check(snapshot.UnverifiedCount == 1 && snapshot.UnavailableTypeIdentityCount == 1,
            "Snapshot diagnostics are captured before filtering");
        var saved = new PanelQueryOptions { SearchMode = PanelSearchMode.Volume, Search = "> 3", OnlyInvalid = true, TypeName = "missing" };
        var empty = PanelQueryEngine.Execute(snapshot, saved);
        Check(empty.RowPositions.Count == 0 && empty.Snapshot.UnverifiedCount == 1 && empty.Snapshot.UnavailableTypeIdentityCount == 1 &&
            saved.SearchMode == PanelSearchMode.Volume && saved.Search == "> 3" && saved.OnlyInvalid && saved.TypeName == "missing",
            "Empty filtered results preserve input diagnostics and unavailable saved settings");
        Check(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { OnlyInvalid = true }).RowPositions.SequenceEqual(new[] { 0, 3 }),
            "Invalid-only excludes unverified values");
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Check(result.Statistics.InvalidCount == 2 && result.Statistics.UnverifiedCount == 1 &&
            result.Statistics.FilteredSourceStatuses.VerifiedInvalid == 2 && result.Statistics.FilteredSourceStatuses.Unverified == 1,
            "Statistics distinguish invalid, unverified and source statuses");
        Check(CanvasStatistics.Label(result, 5, -1).Contains("2 invalid") && CanvasStatistics.Label(result, 5, -1).Contains("1 unverified") &&
            CanvasStatistics.Label(empty, 8, -1).Contains("0 unverified") &&
            PanelQueryPolicy.InputDiagnostic(snapshot, saved).Contains("1 unverified"),
            "Statistics distinguish current row statuses from the prefilter warning retained by saved empty filters");
        var emptyInput = PanelSnapshot.Empty;
        Check(!emptyInput.SupportsSearchMode(PanelSearchMode.Value) && emptyInput.SupportsSearchMode(PanelSearchMode.ListLength),
            "Empty input retains only unconditional search modes");
    }

    private static void SemanticEmptyAndGroupSetKeys()
    {
        var rows = new[] {
            new PanelRow(0, "{0}", "Text", "", null, stringValue: ""),
            new PanelRow(1, "{0}", "Text", "<empty>", null, stringValue: "<empty>"),
            Row(2, "{0}", "Group (Facade A) with 2 objects", PanelValidity.VerifiedValid),
            Row(3, "{0}", "Group (Facade B) with 2 objects", PanelValidity.VerifiedValid),
            Row(4, "{0}", "Group (Facade A) with 3 objects", PanelValidity.VerifiedValid),
            Row(5, "{0}", "Group (Facade A) with 2 objects", PanelValidity.Unverified) };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", rows.Length) });
        Check(PanelValuePresentation.Visualize(PanelValuePresentation.FormatDisplay(rows[0], PanelUnitConversion.None, -1)) == "<empty>" &&
            PanelValuePresentation.FormatDisplay(rows[0], PanelUnitConversion.None, -1) == "", "Empty indication never changes the semantic value");
        var regex = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "^$" });
        Check(regex.RowPositions.SequenceEqual(new[] { 0 }), "Regex can match exact empty text");
        var literal = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "<empty>" });
        Check(literal.RowPositions.SequenceEqual(new[] { 1 }), "The visual empty marker is not searchable as an empty string");
        var set = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { CreateSet = true });
        Check(set.RowPositions.SequenceEqual(new[] { 0, 1, 2, 3, 4 }) && set.DisplaySet!.Values[0].Text == "" &&
            set.DisplaySet.Values[0].CopyText == "" && set.SetOccurrencesAt(2)!.SourceStatuses.Unverified == 1,
            "Sets preserve empty text, distinguish group names and counts, and group equal valid/unverified summaries");
    }

    private static void ItemSetSourceStatuses()
    {
        foreach (bool reversed in new[] { false, true })
        {
            var first = reversed ? PanelValidity.Unverified : PanelValidity.VerifiedValid;
            var second = reversed ? PanelValidity.VerifiedValid : PanelValidity.Unverified;
            var snapshot = new PanelSnapshot(new[] { Row(0, "{0}", "same", first), Row(1, "{0}", "same", second),
                Row(2, "{0}", "same", PanelValidity.VerifiedInvalid) }, new[] { new PanelBranch("{0}", 3) });
            var set = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { CreateSet = true });
            Check(set.RowPositions.SequenceEqual(new[] { 0, 2 }) && set.DisplaySet!.Values[2].Text == "Invalid · same" &&
                set.SetOccurrencesAt(0)!.SourceStatuses.VerifiedValid == 1 && set.SetOccurrencesAt(0)!.SourceStatuses.Unverified == 1 &&
                set.SetOccurrencesAt(2)!.SourceStatuses.VerifiedInvalid == 1 && set.Statistics.InvalidCount == 0 &&
                set.Statistics.UnverifiedCount == 0 && set.Statistics.FilteredSourceStatuses.VerifiedInvalid == 1 &&
                set.Statistics.FilteredSourceStatuses.Unverified == 1,
                "Set warnings aggregate sources independent of representative while projected statistics remain separate");
            var displayed = CanvasStatistics.Label(set, 5, -1);
            var sources = CanvasStatistics.Label(set, 8, -1);
            var rowSources = CanvasStatistics.RowSourceStatuses(set, 0);
            Check(displayed.Contains("0 invalid") && displayed.Contains("0 unverified") &&
                sources.Contains("1 invalid") && sources.Contains("1 unverified") && rowSources.VerifiedValid == 1 && rowSources.Unverified == 1 &&
                CanvasStatistics.SourceStatuses(rowSources).Contains("1 valid") && CanvasStatistics.SourceStatuses(rowSources).Contains("1 unverified"),
                "Projected row statistics and occurrence status summaries remain accurate in either source order");
            var invalid = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { CreateSet = true, OnlyInvalid = true });
            Check(invalid.RowPositions.SequenceEqual(new[] { 2 }) && invalid.SetOccurrencesAt(2)!.SourceStatuses.Total == 1,
                "Invalid-only filters source occurrences before set projection");
            Check(CanvasStatistics.Label(invalid, 8, -1).Contains("1 invalid") && CanvasStatistics.Label(invalid, 8, -1).Contains("0 unverified"),
                "Invalid-only source counts exclude unverified occurrences");
        }
    }

    private static void BranchSetSourceStatuses()
    {
        foreach (bool reversed in new[] { false, true })
        {
            var a = reversed ? PanelValidity.VerifiedInvalid : PanelValidity.VerifiedValid;
            var b = reversed ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid;
            var rows = new[] { Row(0, "{0}", "x", a), Row(1, "{0}", "x", PanelValidity.Unverified),
                Row(0, "{1}", "x", b), Row(1, "{1}", "x", PanelValidity.VerifiedValid) };
            var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 2), new PanelBranch("{1}", 2),
                new PanelBranch("{2}", 0), new PanelBranch("{3}", 0) });
            var query = new PanelQueryOptions { CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength };
            var result = PanelQueryEngine.Execute(snapshot, query);
            var statuses = result.DisplaySet!.BranchSourceStatuses["{0}"];
            Check(result.BranchPaths.SequenceEqual(new[] { "{0}", "{2}" }) && result.RowPositions.SequenceEqual(new[] { 0, 1 }) &&
                statuses.VerifiedValid == 2 && statuses.VerifiedInvalid == 1 && statuses.Unverified == 1 &&
                result.DisplaySet.BranchSourceStatuses["{2}"].Total == 0 && result.Statistics.ItemCount == 2,
                "Equal-length branch warnings include filtered nonrepresentatives and empty branches");
            Check(CanvasStatistics.SourceStatuses(statuses).Contains("1 invalid") && CanvasStatistics.SourceStatuses(statuses).Contains("1 unverified") &&
                CanvasStatistics.Label(result, 8, -1).Contains("1 invalid") && CanvasStatistics.Label(result, 8, -1).Contains("1 unverified"),
                "List-length set labels expose statuses from all matching source branches");
            query.OnlyInvalid = true;
            var invalid = PanelQueryEngine.Execute(snapshot, query);
            Check(invalid.RowPositions.Count == 1 && invalid.Statistics.FilteredSourceStatuses.VerifiedInvalid == 1 &&
                invalid.Statistics.FilteredSourceStatuses.Unverified == 0 && invalid.DisplaySet!.BranchSourceStatuses.Values.Single().VerifiedInvalid == 1,
                "Invalid-only branch sets exclude unverified occurrences before choosing representatives");
        }
    }

    private static PanelRow Row(int index, string path, string display, PanelValidity validity,
        bool typeKnown = true, PanelSearchCapabilities capabilities = PanelSearchCapabilities.None) =>
        new PanelRow(index, path, "Group", display, null, validity: validity,
            typeIdentityAvailable: typeKnown, searchCapabilities: capabilities);

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
