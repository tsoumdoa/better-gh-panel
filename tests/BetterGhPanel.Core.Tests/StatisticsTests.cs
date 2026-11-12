using Xunit;
using BetterGhPanel.Core;

public class StatisticsTests
{


    private static PanelSnapshot Sample() => new PanelSnapshot(new[]
    {
        new PanelRow(0, "{0}", "Number", "1", 1),
        new PanelRow(1, "{0}", "Integer", "1", 1),
        new PanelRow(2, "{0}", "Number", "1", 1.0000000000000002),
        new PanelRow(3, "{0}", "Number", "0", 0),
        new PanelRow(4, "{0}", "Number", "-0", -0.0),
        Text(5, "1"), Text(6, "Alpha"), Text(7, "alpha"), Text(8, " alpha "), Text(9, ""), Text(10, "Alpha"),
        new PanelRow(11, "{0}", "Text", "<same summary>", null),
        new PanelRow(12, "{0}", "Text", "<same summary>", null),
        new PanelRow(13, "{0}", "Null", "<null>", null, isNull: true),
        new PanelRow(14, "{0}", "Text", "broken", null, isValid: false, stringValue: "broken"),
        new PanelRow(15, "{0}", "Number", "NaN", double.NaN),
        new PanelRow(16, "{0}", "Number", "Infinity", double.PositiveInfinity),
        new PanelRow(0, "{1}", "Number", "8", 8)
    }, new[] { new PanelBranch("{0}", 17), new PanelBranch("{1}", 1), new PanelBranch("{2}", 0) });

    private static PanelRow Text(int index, string value) => new PanelRow(index, "{0}", "Text", value, null, stringValue: value);

    [Fact]
    public void BranchCountsAndFilters()
    {
        var snapshot = Sample();
        var all = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Assert.True(all.Statistics.BranchItemCounts["{2}"] == 0 && all.Statistics.BranchCount == 3,
            "Unfiltered branch counts retain empty branches");
        var filtered = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "> 1" });
        Assert.True(filtered.Statistics.ItemCount == 2 && filtered.Statistics.BranchItemCounts["{0}"] == 1 &&
            filtered.Statistics.BranchItemCounts["{1}"] == 1 && !filtered.Statistics.BranchItemCounts.ContainsKey("{2}"),
            "Per-branch counts follow the complete filtered result and exclude nonmatching empty branches");
        Assert.True(CanvasStatistics.Summary(filtered) == "2/18 items · 2/3 branches" &&
            CanvasStatistics.BranchCount(filtered, "{0}") == "1/17 items",
            "Compact counts distinguish result length from pre-filter tree length");
        var empty = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { BranchPath = "{2}" });
        Assert.True(empty.Statistics.ItemCount == 0 && empty.Statistics.BranchCount == 1,
            "Selecting an empty branch retains the branch without inventing scalar values");
    }

}
