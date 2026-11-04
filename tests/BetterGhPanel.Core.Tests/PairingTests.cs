using System;
using System.Linq;
using BetterGhPanel.Core;
using Xunit;

public sealed class PairingTests
{
    [Fact]
    public void BlocksOnlyBranchCountMismatch()
    {
        var data = new[] { new PanelBranch("{0}", 12), new PanelBranch("{1}", 0) };
        Assert.Null(PanelPairing.Diagnose(data, new[] { new PanelBranch("{4}", 1), new PanelBranch("{5}", 20) }));
        Assert.Null(PanelPairing.Diagnose(Array.Empty<PanelBranch>(), Array.Empty<PanelBranch>()));
        Assert.Equal("F has 1 branches, D has 2.", PanelPairing.Diagnose(data, new[] { data[0] }));
        Assert.Equal("F has 2 branches, D has 1.", PanelPairing.Diagnose(new[] { data[0] }, data));
        Assert.Throws<ArgumentException>(() => PanelPairing.Match(data, Array.Empty<PanelBranch>()));
    }

    [Fact]
    public void ReservesAllExactMatchesBeforeFallbackAndPairsOnlyExistingIndices()
    {
        var data = new[] { new PanelBranch("{0}", 12), new PanelBranch("{1}", 2), new PanelBranch("{2}", 0) };
        var paired = new[] { new PanelBranch("{1}", 2), new PanelBranch("{4}", 1), new PanelBranch("{5}", 3) };
        var pairs = PanelPairing.Match(data, paired);
        Assert.Equal(new[] { "{4}", "{1}", "{5}" }, pairs.Select(pair => pair.Paired.Path));
        Assert.True(pairs[0].ByOrder && pairs[0].Unequal);
        Assert.False(pairs[1].ByOrder || pairs[1].Unequal);
        Assert.Equal(2, pairs[0].ItemPosition(0));
        Assert.Null(pairs[0].ItemPosition(1));
        Assert.Equal(3, pairs[2].PairedOffset);
    }
}
