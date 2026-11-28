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

}
