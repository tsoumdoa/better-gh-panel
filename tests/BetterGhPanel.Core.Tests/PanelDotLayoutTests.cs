using System;
using System.Linq;
using BetterGhPanel.Core;
using Xunit;

public class PanelDotLayoutTests
{
    private static PanelDotInput Dot(double x, double y = 0, int text = 1, int priority = 0, bool selected = false,
        double width = 100, double height = 20) => new PanelDotInput(x, y, width, height, text, selected, priority);

    [Fact]
    public void GroupsMatchingKeysAtTheCentroidMember()
    {
        var dots = new[] { Dot(0), Dot(20, priority: 1), Dot(60, priority: 2), Dot(20, text: 2, priority: 3),
            Dot(20, text: 3, priority: 4), Dot(20, text: 1, priority: 5, selected: true), Dot(400, priority: 6) };
        var layout = new PanelDotLayout(); layout.Calculate(dots, dots.Length);
        Assert.Equal(3, layout.Results[0].GroupCount);
        Assert.Equal(1, layout.Results[0].LabelAnchor);
        Assert.Equal(0, layout.Results[1].Representative);
        Assert.Equal(0, layout.Results[2].Representative);
        foreach (int i in new[] { 3, 4, 5, 6 }) Assert.Equal(i, layout.Results[i].Representative);
        Assert.Equal(PanelDotTier.GroupMember, layout.Results[1].Tier);
    }

    [Fact]
    public void GroupingMatchesMockNeighbourhoodAndStableEarlierPriority()
    {
        var layout = new PanelDotLayout();
        var dots = new[] { Dot(160, priority: 2), Dot(0, priority: 4), Dot(80, priority: 8), Dot(80, 151, priority: 10) };
        layout.Calculate(dots, dots.Length);
        Assert.Equal(1, layout.Results[2].Representative);
        Assert.Equal(3, layout.Results[3].Representative);
    }

    [Theory]
    [InlineData(100, PanelDotTier.Full, 1)]
    [InlineData(80, PanelDotTier.Text, .8)]
    [InlineData(69, PanelDotTier.Text, .65)]
    [InlineData(66.66666666666667, PanelDotTier.Text, .65)]
    [InlineData(66, PanelDotTier.Swatch, 0)]
    [InlineData(0, PanelDotTier.Swatch, 0)]
    public void FitsSymmetricallyAndQuantisesText(double separation, PanelDotTier tier, double scale)
    {
        var layout = new PanelDotLayout(); layout.Calculate(new[] { Dot(0, text: 1), Dot(separation, text: 2, priority: 1) }, 2);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(tier, layout.Results[i].Tier);
            if (tier != PanelDotTier.Swatch) Assert.Equal(scale, layout.Results[i].TextScale, 8);
        }
    }

    [Fact]
    public void SelectionStaysSeparateKeepsTextLongerAndDrawsLast()
    {
        var layout = new PanelDotLayout();
        layout.Calculate(new[] { Dot(0, priority: 0), Dot(50, priority: 20, selected: true) }, 2);
        Assert.Equal(PanelDotTier.Swatch, layout.Results[0].Tier);
        Assert.Equal(PanelDotTier.Text, layout.Results[1].Tier);
        Assert.Equal(.7, layout.Results[1].TextScale, 8);
        Assert.Equal(1, layout.PriorityIndex(0));
        Assert.Equal(1, layout.Results[1].Representative);
        layout.Calculate(new[] { Dot(0, text: 1), Dot(80, text: 2, priority: 1, selected: true) }, 2);
        Assert.Equal(PanelDotTier.Full, layout.Results[1].Tier);
    }

    [Fact]
    public void SwatchesRespectNearestDistanceCapsSizeAndDpiWithoutHiding()
    {
        var layout = new PanelDotLayout();
        layout.Calculate(new[] { Dot(0, text: 1), Dot(6, text: 2, priority: 1) }, 2);
        Assert.Equal(2.5, layout.Results[0].SwatchRadius);
        layout.Calculate(new[] { Dot(0, text: 1), Dot(60, text: 2, priority: 1) }, 2, size: 1.25, dpi: 2);
        Assert.Equal(12.5, layout.Results[0].SwatchRadius);
        var dense = Enumerable.Range(0, 20000).Select(i => Dot(0, text: i, priority: i, selected: i == 19999)).ToArray();
        layout.Calculate(dense, dense.Length, dpi: 2);
        Assert.Equal(dense.Length, layout.Count);
        for (int i = 0; i < dense.Length; i++)
        {
            Assert.Equal(PanelDotTier.Swatch, layout.Results[i].Tier);
            Assert.Equal(3, layout.Results[i].SwatchRadius);
        }
    }

    [Fact]
    public void VariableBoxFitsMatchAllPairsReference()
    {
        var random = new Random(23);
        var dots = Enumerable.Range(0, 400).Select(i => Dot(random.NextDouble() * 5000, random.NextDouble() * 5000,
            text: i, priority: i, selected: i % 7 == 0, width: random.Next(20, 200), height: random.Next(10, 50))).ToArray();
        var layout = new PanelDotLayout(); layout.Calculate(dots, dots.Length);
        for (int i = 0; i < dots.Length; i++)
        {
            double fit = 1;
            for (int j = 0; j < dots.Length; j++) if (i != j)
                fit = Math.Min(fit, Math.Max(Math.Abs(dots[i].X - dots[j].X) * 2 / (dots[i].Width + dots[j].Width),
                    Math.Abs(dots[i].Y - dots[j].Y) * 2 / (dots[i].Height + dots[j].Height)));
            if (dots[i].Selected) fit = Math.Max(fit, Math.Min(1, fit * 1.4));
            var tier = fit >= 1 ? PanelDotTier.Full : fit >= PanelDotLayout.MinimumTextScale ? PanelDotTier.Text : PanelDotTier.Swatch;
            Assert.Equal(tier, layout.Results[i].Tier);
            if (tier == PanelDotTier.Text) Assert.Equal(Math.Floor(fit * 20) / 20, layout.Results[i].TextScale, 8);
        }
    }

    [Fact]
    public void GroupSwatchesKeepTheSameHalfPixelGapAtEveryDpi()
    {
        var layout = new PanelDotLayout();
        foreach (double dpi in new[] { 1.0, 1.5, 2.0 })
        {
            layout.Calculate(new[] { Dot(0), Dot(6 * dpi, priority: 1) }, 2, dpi: dpi);
            Assert.Equal(2.5 * dpi, layout.Results[0].GroupSwatchRadius, 8);
            Assert.Equal(2.5 * dpi, layout.Results[1].SwatchRadius, 8);
        }
    }
}
