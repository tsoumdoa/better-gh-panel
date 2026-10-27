using System;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;

internal static class QueryPolicyChecks
{
    internal static void Run()
    {
        var original = new PanelQueryOptions();
        var changed = original.Clone();
        Check(PanelQueryPolicy.SameQuery(original, changed), "A cloned query has the same settings");
        changed.SortMode = PanelSortMode.ValueAscending;
        Check(!PanelQueryPolicy.SameQuery(original, changed) && PanelQueryPolicy.SameOutputFilter(original, changed),
            "Sorting changes the view without changing downstream membership");

        changed = original.Clone();
        changed.DecimalPlaces = 2;
        Check(!PanelQueryPolicy.SameQuery(original, changed) && PanelQueryPolicy.SameOutputFilter(original, changed),
            "Precision alone is a view change for an unfiltered tree");

        var text = new PanelQueryOptions { Search = "alpha" };
        var textPrecision = text.Clone();
        textPrecision.DecimalPlaces = 2;
        Check(!PanelQueryPolicy.SameOutputFilter(text, textPrecision),
            "Precision can change membership of displayed-text searches");
        text.Search = "> 2.5";
        textPrecision.Search = text.Search;
        Check(PanelQueryPolicy.SameOutputFilter(text, textPrecision),
            "Explicit numeric comparisons in text mode use full precision");

        var regex = new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = " " };
        var regexPrecision = regex.Clone();
        regexPrecision.DecimalPlaces = 2;
        Check(PanelQueryPolicy.HasOutputFilter(regex) && !PanelQueryPolicy.SameOutputFilter(regex, regexPrecision),
            "Whitespace is significant for regular-expression membership");
        Check(!PanelQueryPolicy.HasOutputFilter(new PanelQueryOptions { Search = " " }),
            "Whitespace alone is not an active text filter");

        var set = new PanelQueryOptions { CreateSet = true };
        var setPrecision = set.Clone();
        setPrecision.DecimalPlaces = 2;
        Check(PanelQueryPolicy.HasOutputFilter(set) && !PanelQueryPolicy.SameOutputFilter(set, setPrecision),
            "A displayed set depends on precision even without a search");
        Check(PanelQueryPolicy.HasOutputFilter(new PanelQueryOptions { BranchPath = "{malformed" }),
            "A malformed path remains an active filter while its error is shown");
        Check(PanelQueryPolicy.HasOutputFilter(new PanelQueryOptions { TypeName = "Curve" }) &&
            PanelQueryPolicy.HasOutputFilter(new PanelQueryOptions { OnlyInvalid = true }),
            "Type and invalid-only restrictions do not pass through while pending");
        Check(!PanelQueryPolicy.HasOutputFilter(new PanelQueryOptions
        {
            SearchMode = PanelSearchMode.Length, DisplayFilterValue = true
        }), "Displaying a metric alone does not filter pending output");

        var changes = new (string Name, Action<PanelQueryOptions> Apply)[]
        {
            ("search", query => query.Search = "needle"),
            ("mode", query => query.SearchMode = PanelSearchMode.Regex),
            ("set", query => query.CreateSet = true),
            ("type", query => query.TypeName = "Curve"),
            ("path", query => query.BranchPath = "{0}"),
            ("path prefix", query => query.BranchPrefix = true),
            ("invalid only", query => query.OnlyInvalid = true)
        };
        foreach (var change in changes)
        {
            var next = original.Clone();
            change.Apply(next);
            Check(!PanelQueryPolicy.SameQuery(original, next) && !PanelQueryPolicy.SameOutputFilter(original, next),
                "A change to " + change.Name + " invalidates both query and output comparisons");
        }
        var metric = new PanelQueryOptions { SearchMode = PanelSearchMode.Length };
        var displayedMetric = metric.Clone();
        displayedMetric.DisplayFilterValue = true;
        Check(!PanelQueryPolicy.SameQuery(metric, displayedMetric) && !PanelQueryPolicy.SameOutputFilter(metric, displayedMetric),
            "Switching metric display is recorded independently of the pending-output policy");

        var uncertain = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "Unavailable", "<unavailable value>", null,
                PanelValidity.Unverified, typeIdentityAvailable: false)
        }, new[] { new PanelBranch("{0}", 1) });
        var invalidOnly = new PanelQueryOptions { OnlyInvalid = true };
        Check(PanelQueryEngine.Execute(uncertain, invalidOnly).RowPositions.Count == 0 &&
            PanelQueryPolicy.InputDiagnostic(uncertain, invalidOnly).Contains("1 unverified") &&
            PanelQueryPolicy.InputDiagnostic(uncertain, invalidOnly).Contains("1 type identity unavailable"),
            "Input diagnostics survive an invalid-only filter that returns no rows");
        Check(PanelQueryPolicy.InputDiagnostic(uncertain, new PanelQueryOptions()).Length == 0,
            "The filter warning is not shown when no restriction is active");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
