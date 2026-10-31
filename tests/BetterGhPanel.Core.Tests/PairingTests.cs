using System;
using BetterGhPanel.Core;
using Xunit;

public sealed class PairingTests
{
    [Fact]
    public void MatchesExactPathsIncludingEmptyBranchesRegardlessOfEnumerationOrder()
    {
        var data = new[] { new PanelBranch("{0;2}", 0), new PanelBranch("{0;10}", 3) };
        Assert.Null(PanelPairing.Diagnose(data, new[] { data[1], data[0] }));
        Assert.Null(PanelPairing.Diagnose(Array.Empty<PanelBranch>(), Array.Empty<PanelBranch>()));
        Assert.Equal("F is missing branch {0;2}", PanelPairing.Diagnose(data, new[] { data[1] }));
        Assert.Equal("F has extra branch {0;2}", PanelPairing.Diagnose(new[] { data[1] }, data));
    }

    [Fact]
    public void ReportsFirstLengthMismatchWithBothCounts()
    {
        var data = new[] { new PanelBranch("{0;1}", 20), new PanelBranch("{0;2}", 5) };
        var paired = new[] { new PanelBranch("{0;1}", 19), new PanelBranch("{0;2}", 0) };
        Assert.Equal("Branch {0;1}: D has 20 items, F has 19", PanelPairing.Diagnose(data, paired));
    }

    [Fact]
    public void EqualPathCountsDoNotHideMissingOrReplacedBranches()
    {
        var data = new[] { new PanelBranch("{0;2}", 1), new PanelBranch("{0;10}", 1) };
        var paired = new[] { new PanelBranch("{0;2}", 1), new PanelBranch("{1;10}", 1) };
        Assert.Equal("F is missing branch {0;10}", PanelPairing.Diagnose(data, paired));
    }
}
