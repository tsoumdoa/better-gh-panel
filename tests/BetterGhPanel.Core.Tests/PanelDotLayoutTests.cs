using BetterGhPanel.Core;
using System.Linq;
using Xunit;

public class PanelDotLayoutTests
{
    private static readonly int[] ItemIds = Enumerable.Range(0, 20).OrderBy(PanelDotLayout.ItemHash).ToArray();

    private static PanelDotInput Dot(double x, int rank = 0, double y = 0, int text = -1, bool selected = false,
        double value = double.NaN) =>
        new PanelDotInput(x, y, 100, 20, text, selected, ItemIds[rank], value, PanelDotLayout.ValueBand(value, 1, 100));

    [Fact]
    public void ShowAllKeepsFullTextAtEachAnchorAndDrawsSelectionLastAtThePreviewLimit()
    {
        var dots = Enumerable.Range(0, 20000).OrderBy(PanelDotLayout.ItemHash)
            .Select((id, index) => new PanelDotInput(0, 0, 100, 20, 1, index == 4 || index == 19999, id)).ToArray();
        var layout = new PanelDotLayout();
        layout.Calculate(dots, 4);
        Assert.Equal(1, layout.TextCount);

        layout.CalculateAll(dots, dots.Length);
        Assert.Equal(dots.Length, layout.TextCount);
        Assert.Equal(new[] { 4, 19999 }, new[] { layout.TextIndex(0), layout.TextIndex(1) });
        for (int i = 0; i < dots.Length; i++)
        {
            Assert.Equal(1, layout.Results[i].TextScale);
            Assert.Equal(0, layout.Results[i].SwatchRadius);
            Assert.Equal(i, layout.Results[i].LabelAnchor);
        }
        Assert.Equal(dots.Length, Enumerable.Range(0, layout.TextCount).Select(layout.TextIndex).Distinct().Count());

        layout.Calculate(dots, 32);
        Assert.Equal(1, layout.TextCount);
        Assert.Equal(4, layout.TextIndex(0));
    }

    [Fact]
    public void ARejectedSwatchLeavesRoomForFullSizeText()
    {
        var dots = new[] { Dot(0), Dot(49, 1), Dot(90, 2) };
        var layout = new PanelDotLayout();
        layout.Calculate(dots, dots.Length);
        Assert.Equal(1, layout.Results[0].TextScale);
        Assert.Equal(0, layout.Results[1].TextScale);
        Assert.Equal(1, layout.Results[2].TextScale);
        Assert.Equal(5, layout.Results[1].SwatchRadius);
    }

    [Theory]
    [InlineData(85, 1)]
    [InlineData(84.99, .95)]
    [InlineData(75, .70)]
    [InlineData(73, 0)]
    public void AllowsFifteenPercentOverlapThenTriesTheLargestFittingStep(double separation, double scale)
    {
        var layout = new PanelDotLayout();
        layout.Calculate(new[] { Dot(0), Dot(separation, 1) }, 2);
        Assert.Equal(scale, layout.Results[1].TextScale, 8);
    }

    [Fact]
    public void UsesSharedAreaOfTheSmallerBox()
    {
        var layout = new PanelDotLayout();
        // Fifteen square pixels are shared with the smaller, 100-square-pixel box.
        var dots = new[]
        {
            new PanelDotInput(0, 0, 10, 10, -1, false, 0),
            new PanelDotInput(53.5, 0, 100, 20, -1, false, 1)
        };
        layout.Calculate(dots, dots.Length);
        Assert.Equal(1, layout.Results[1].TextScale);
    }

    [Theory]
    [InlineData(84.9, 13)]
    [InlineData(82.2, 12)]
    public void TestsOverlapAtTheRoundedDrawingHeight(double separation, int fontHeight)
    {
        var layout = new PanelDotLayout();
        layout.Calculate(new[] { Dot(0), Dot(separation, 1) }, 2, fontHeight: 14);
        Assert.Equal(fontHeight / 14d, layout.Results[1].TextScale);
    }

    [Fact]
    public void SmallestOverlapDoesNotRejectALargerFittingScale()
    {
        var dots = new[]
        {
            new PanelDotInput(79, 6, 160, 17, -1, true, ItemIds[0]),
            new PanelDotInput(-2, 40, 20, 20, -1, true, ItemIds[1]),
            new PanelDotInput(0, 0, 30, 70, -1, false, ItemIds[2])
        };
        var layout = new PanelDotLayout();
        layout.Calculate(dots, dots.Length, fontHeight: 14);
        Assert.Equal(13d / 14, layout.Results[2].TextScale);
    }

    [Fact]
    public void PicksSelectionAndExtremesThenRotatesBandsInHashOrder()
    {
        var dots = new[]
        {
            Dot(300, 0, value: 12), Dot(600, 1, value: 14), Dot(0, 2, value: 3),
            Dot(1000, 3, value: 80), Dot(600, 4, value: 30),
            Dot(0, 5, value: 1), Dot(1000, 6, value: 100), Dot(300, 7, selected: true, value: 50),
            Dot(800, 8, value: 60)
        };
        var layout = new PanelDotLayout();
        layout.Calculate(dots, dots.Length);
        Assert.Equal(new[] { 7, 5, 6, 4, 8 }, Enumerable.Range(0, layout.TextCount).Select(layout.TextIndex));
    }

    [Fact]
    public void RetainsTextByItemAcrossACameraMove()
    {
        var layout = new PanelDotLayout();
        layout.Calculate(new[] { Dot(300, 1), Dot(302, 2) }, 2);
        var moved = new[] { Dot(301), Dot(304, 1), Dot(306, 2) };
        layout.Calculate(moved, moved.Length);
        Assert.Equal(1, layout.TextIndex(0));
        layout.ResetStability();
        layout.Calculate(moved, moved.Length);
        Assert.Equal(0, layout.TextIndex(0));
    }

    [Fact]
    public void GroupsMatchingTextAtTheCentroidAndKeepsSelectionSeparate()
    {
        foreach (double dpi in new[] { 1.0, 1.5, 2.0 })
        {
            var layout = new PanelDotLayout();
            var dots = new[]
            {
                Dot(0, text: 1), Dot(6 * dpi, 1, text: 1), Dot(30 * dpi, 2, text: 1),
                Dot(400 * dpi, 3, text: 1, selected: true), Dot(408 * dpi, 4, text: 1)
            };
            layout.Calculate(dots, dots.Length, dpi: dpi);
            Assert.Equal(2, layout.TextCount);
            Assert.Equal(1, layout.Results[0].LabelAnchor);
            Assert.Equal(2.5 * dpi, layout.Results[0].SwatchRadius, 8);
            Assert.Equal(0, layout.Results[1].SwatchRadius);
            Assert.Equal(3 * dpi, layout.Results[2].SwatchRadius, 8);
            Assert.Equal(0, layout.Results[4].TextScale);
            Assert.Equal(3.5 * dpi, layout.Results[4].SwatchRadius, 8);
        }
    }
}
