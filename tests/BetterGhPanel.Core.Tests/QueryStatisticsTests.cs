using Xunit;
using System.Linq;
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

        Assert.Equal(1, result.Statistics.NullCount);
        Assert.Equal(2, result.Statistics.InvalidCount);

        Assert.Equal(3, result.Statistics.TypeCounts["Number"]);
        Assert.Equal(2, result.Statistics.TypeCounts["Text"]);
    }

}
