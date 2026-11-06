using static NumericAssertions;
using Xunit;
using System.Linq;
using BetterGhPanel.Core;
using static PanelQueryTestData;

public class QueryStatisticsTests
{
    [Fact]
    public void StatisticsAndEmptyBranches()
    {
        var result = Query();
        Assert.Equal(6, result.Statistics.ItemCount);
        Assert.Equal(4, result.Statistics.BranchCount);
        Assert.True(result.BranchPaths.ToHashSet().SetEquals(Sample().Branches.Select(branch => branch.Path)));
        Assert.Equal(3, result.Statistics.NumericCount);
        Assert.Equal(1, result.Statistics.NullCount);
        Assert.Equal(2, result.Statistics.InvalidCount);
        Assert.Equal(2.0, result.Statistics.Minimum!.Value);
        Assert.Equal(10.0, result.Statistics.Maximum!.Value);
        AssertNear(22.0 / 3, result.Statistics.Mean!.Value);
        Assert.Equal(3, result.Statistics.TypeCounts["Number"]);
        Assert.Equal(2, result.Statistics.TypeCounts["Text"]);
    }

    [Fact]
    public void ExtremeNumericStatistics()
    {
        var snapshot = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "Number", "min", -double.MaxValue),
            new PanelRow(1, "{0}", "Number", "max", double.MaxValue)
        }, new[] { new PanelBranch("{0}", 2) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Assert.Equal(0.0, result.Statistics.Mean!.Value);
    }
}
