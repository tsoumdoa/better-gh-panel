using System;
using System.Linq;
using BetterGhPanel.Core;
using Xunit;

public class PanelDotLayoutTests
{
    private static PanelDotInput Dot(double x, double y = 0, string text = "value", int priority = 0, bool selected = false,
        double width = 100, double height = 20) => new PanelDotInput(x, y, width, height, text, selected, priority);

    [Fact]
    public void GroupsOnlyExactShownTextAndAnchorsAtTheCentroidMember()
    {
        var dots = new[] { Dot(0), Dot(20, priority: 1), Dot(60, priority: 2), Dot(20, text: "Value", priority: 3),
            Dot(20, text: "value ", priority: 4), Dot(20, text: "value", priority: 5, selected: true), Dot(400, priority: 6) };
        var layout = new PanelDotLayout(); layout.Calculate(dots);
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
        layout.Calculate(new[] { Dot(0, priority: 4), Dot(160, priority: 2), Dot(80, priority: 8), Dot(80, 151, priority: 10) });
        Assert.Equal(0, layout.Results[2].Representative);
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
        var layout = new PanelDotLayout(); layout.Calculate(new[] { Dot(0, text: "a"), Dot(separation, text: "b", priority: 1) });
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
        layout.Calculate(new[] { Dot(0, priority: 0), Dot(50, priority: 20, selected: true) });
        Assert.Equal(PanelDotTier.Swatch, layout.Results[0].Tier);
        Assert.Equal(PanelDotTier.Text, layout.Results[1].Tier);
        Assert.Equal(.7, layout.Results[1].TextScale, 8);
        Assert.Equal(1, layout.PriorityIndex(0));
        Assert.Equal(1, layout.Results[1].Representative);
        layout.Calculate(new[] { Dot(0, text: "a"), Dot(80, text: "b", priority: 1, selected: true) });
        Assert.Equal(PanelDotTier.Full, layout.Results[1].Tier);
    }

    [Fact]
    public void SwatchesRespectNearestDistanceCapsSizeAndDpiWithoutHiding()
    {
        var layout = new PanelDotLayout();
        layout.Calculate(new[] { Dot(0, text: "a"), Dot(6, text: "b", priority: 1) });
        Assert.Equal(2.5, layout.Results[0].SwatchRadius);
        layout.Calculate(new[] { Dot(0, text: "a"), Dot(60, text: "b", priority: 1) }, size: 1.25, dpi: 2);
        Assert.Equal(12.5, layout.Results[0].SwatchRadius);
        var dense = Enumerable.Range(0, 20000).Select(i => Dot(0, text: i.ToString(), priority: i, selected: i == 19999)).ToArray();
        layout.Calculate(dense, dpi: 2);
        Assert.Equal(dense.Length, layout.Count);
        for (int i = 0; i < dense.Length; i++)
        {
            Assert.Equal(PanelDotTier.Swatch, layout.Results[i].Tier);
            Assert.Equal(3, layout.Results[i].SwatchRadius);
        }
        Assert.True(layout.CandidateVisits < dense.Length * 10L, "Coincident unique order labels must not produce a quadratic neighbour scan");
    }

    [Fact]
    public void PermutingInputsDoesNotChangeGroupsAnchorsOrTiers()
    {
        var random = new Random(17);
        var original = Enumerable.Range(0, 1000).Select(i => Dot(random.NextDouble() * 1200, random.NextDouble() * 800,
            text: (i % 5).ToString(), priority: i - 400, selected: i % 37 == 0)).ToArray();
        var shuffled = original.OrderBy(_ => random.Next()).ToArray();
        var a = new PanelDotLayout(); var b = new PanelDotLayout(); a.Calculate(original); b.Calculate(shuffled);
        var byPriority = Enumerable.Range(0, shuffled.Length).ToDictionary(i => shuffled[i].Priority);
        for (int i = 0; i < original.Length; i++)
        {
            int j = byPriority[original[i].Priority]; var x = a.Results[i]; var y = b.Results[j];
            Assert.Equal(x.Tier, y.Tier); Assert.Equal(x.TextScale, y.TextScale);
            Assert.Equal(x.SwatchRadius, y.SwatchRadius); Assert.Equal(x.GroupSwatchRadius, y.GroupSwatchRadius);
            Assert.Equal(original[x.Representative].Priority, shuffled[y.Representative].Priority);
            Assert.Equal(original[x.LabelAnchor].Priority, shuffled[y.LabelAnchor].Priority);
        }
    }

    [Fact]
    public void VariableBoxFitsMatchAllPairsReference()
    {
        var random = new Random(23);
        var dots = Enumerable.Range(0, 400).Select(i => Dot(random.NextDouble() * 5000, random.NextDouble() * 5000,
            text: i.ToString(), priority: i, selected: i % 7 == 0, width: random.Next(20, 200), height: random.Next(10, 50))).ToArray();
        var layout = new PanelDotLayout(); layout.Calculate(dots);
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
    public void IntegerTextIdsAndOrderedFastPathMatchStringInputs()
    {
        var random = new Random(51);
        var dots = Enumerable.Range(0, 600).Select(i => Dot(random.NextDouble() * 1500, random.NextDouble() * 900,
            (i % 9).ToString(), i, i % 41 == 0)).ToArray();
        var numeric = dots.Select((d, i) => new PanelDotInput(d.X, d.Y, d.Width, d.Height, i % 9, d.Selected, d.Priority)).ToArray();
        var a = new PanelDotLayout(); var b = new PanelDotLayout(); a.Calculate(dots); b.Calculate(numeric, numeric.Length);
        for (int i = 0; i < dots.Length; i++)
        {
            Assert.Equal(a.Results[i].Representative, b.Results[i].Representative);
            Assert.Equal(a.Results[i].LabelAnchor, b.Results[i].LabelAnchor);
            Assert.Equal(a.Results[i].Tier, b.Results[i].Tier);
            Assert.Equal(a.Results[i].TextScale, b.Results[i].TextScale);
            Assert.Equal(a.Results[i].SwatchRadius, b.Results[i].SwatchRadius);
        }
    }

    [Fact]
    public void GroupSwatchesKeepTheSameHalfPixelGapAtEveryDpi()
    {
        var layout = new PanelDotLayout();
        foreach (double dpi in new[] { 1.0, 1.5, 2.0 })
        {
            layout.Calculate(new[] { Dot(0), Dot(6 * dpi, priority: 1) }, dpi: dpi);
            Assert.Equal(2.5 * dpi, layout.Results[0].GroupSwatchRadius, 8);
            Assert.Equal(2.5 * dpi, layout.Results[1].SwatchRadius, 8);
        }
    }
}
