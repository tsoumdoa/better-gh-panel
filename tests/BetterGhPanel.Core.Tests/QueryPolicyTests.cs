using Xunit;
using System;
using BetterGhPanel.Core;

public class QueryPolicyTests
{
    [Fact]
    public void SortingChangesTheViewWithoutChangingOutputMembership()
    {
        var original = new PanelQueryOptions();
        var changed = original.Clone();
        Assert.True(PanelQueryPolicy.SameQuery(original, changed), "A cloned query has the same settings");
        changed.SortMode = PanelSortMode.ValueAscending;
        Assert.True(!PanelQueryPolicy.SameQuery(original, changed) && PanelQueryPolicy.SameOutputFilter(original, changed),
            "Sorting changes the view without changing downstream membership");
    }

    [Fact]
    public void PrecisionChangesQueryAndOutputValues()
    {
        var original = new PanelQueryOptions();
        var changed = original.Clone();
        changed.DecimalPlaces = 2;
        Assert.True(!PanelQueryPolicy.SameQuery(original, changed) && !PanelQueryPolicy.SameOutputFilter(original, changed),
            "Precision changes numeric items on the wire even for an unfiltered tree");
    }

    [Fact]
    public void PrecisionChangesTextAndNumericSearchMembership()
    {
        var text = new PanelQueryOptions { Search = "alpha" };
        var textPrecision = text.Clone();
        textPrecision.DecimalPlaces = 2;
        Assert.True(!PanelQueryPolicy.SameOutputFilter(text, textPrecision),
            "Precision can change membership of displayed-text searches");
        text.Search = "> 2.5";
        textPrecision.Search = text.Search;
        Assert.True(!PanelQueryPolicy.SameOutputFilter(text, textPrecision),
            "Explicit numeric comparisons in text mode use presented numbers");
    }

    [Fact]
    public void RegexWhitespaceIsSignificantWhileTextWhitespaceIsInactive()
    {
        var regex = new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = " " };
        var regexPrecision = regex.Clone();
        regexPrecision.DecimalPlaces = 2;
        Assert.True(PanelQueryPolicy.HasOutputFilter(regex) && !PanelQueryPolicy.SameOutputFilter(regex, regexPrecision),
            "Whitespace is significant for regular-expression membership");
        Assert.True(!PanelQueryPolicy.HasOutputFilter(new PanelQueryOptions { Search = " " }),
            "Whitespace alone is not an active text filter");
    }

    [Fact]
    public void DisplayedSetsDependOnPrecision()
    {
        var set = new PanelQueryOptions { CreateSet = true };
        var setPrecision = set.Clone();
        setPrecision.DecimalPlaces = 2;
        Assert.True(PanelQueryPolicy.HasOutputFilter(set) && !PanelQueryPolicy.SameOutputFilter(set, setPrecision),
            "A displayed set depends on precision even without a search");
    }

    [Fact]
    public void RestrictionsFilterPendingOutputWhileMetricDisplayAloneDoesNot()
    {
        Assert.True(PanelQueryPolicy.HasOutputFilter(new PanelQueryOptions { BranchPath = "{malformed" }),
            "A malformed path remains an active filter while its error is shown");
        Assert.True(PanelQueryPolicy.HasOutputFilter(new PanelQueryOptions { TypeName = "Curve" }) &&
            PanelQueryPolicy.HasOutputFilter(new PanelQueryOptions { OnlyInvalid = true }),
            "Type and invalid-only restrictions do not pass through while pending");
        Assert.True(!PanelQueryPolicy.HasOutputFilter(new PanelQueryOptions
        {
            SearchMode = PanelSearchMode.Length, DisplayFilterValue = true
        }), "Displaying a metric alone does not filter pending output");
    }

    [Theory]
    [InlineData("search")]
    [InlineData("mode")]
    [InlineData("set")]
    [InlineData("type")]
    [InlineData("path")]
    [InlineData("path prefix")]
    [InlineData("invalid only")]
    public void MembershipSettingsInvalidateQueryAndOutputComparisons(string name)
    {
        var original = new PanelQueryOptions();
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
        var change = Array.Find(changes, candidate => candidate.Name == name);
        var next = original.Clone();
        change.Apply(next);
        Assert.True(!PanelQueryPolicy.SameQuery(original, next) && !PanelQueryPolicy.SameOutputFilter(original, next),
            "A change to " + change.Name + " invalidates both query and output comparisons");
    }

    [Fact]
    public void MetricDisplayChangesQueryAndOutputComparisons()
    {
        var metric = new PanelQueryOptions { SearchMode = PanelSearchMode.Length };
        var displayedMetric = metric.Clone();
        displayedMetric.DisplayFilterValue = true;
        Assert.True(!PanelQueryPolicy.SameQuery(metric, displayedMetric) && !PanelQueryPolicy.SameOutputFilter(metric, displayedMetric),
            "Switching metric display is recorded independently of the pending-output policy");
    }

    [Fact]
    public void InputDiagnosticsSurviveAnEmptyInvalidOnlyResult()
    {
        var uncertain = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "Unavailable", "<unavailable value>", null,
                PanelValidity.Unverified, typeIdentityAvailable: false)
        }, new[] { new PanelBranch("{0}", 1) });
        var invalidOnly = new PanelQueryOptions { OnlyInvalid = true };
        Assert.True(PanelQueryEngine.Execute(uncertain, invalidOnly).RowPositions.Count == 0 &&
            PanelQueryPolicy.InputDiagnostic(uncertain, invalidOnly).Contains("1 unverified") &&
            PanelQueryPolicy.InputDiagnostic(uncertain, invalidOnly).Contains("1 type identity unavailable"),
            "Input diagnostics survive an invalid-only filter that returns no rows");
        Assert.True(PanelQueryPolicy.InputDiagnostic(uncertain, new PanelQueryOptions()).Length == 0,
            "The filter warning is not shown when no restriction is active");
    }

}
