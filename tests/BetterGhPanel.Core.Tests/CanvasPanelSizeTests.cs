using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using BetterGhPanel.Core;

public class CanvasPanelSizeTests
{
    public static IEnumerable<object[]> PresetIndices => Enumerable.Range(0, CanvasPanelSize.PresetNames.Length)
        .Select(i => new object[] { i });

    public static IEnumerable<object[]> DisplayGripCases =>
        from fullHeight in new[] { 220f, 360f, 420f }
        from CanvasPanelDisplay display in Enum.GetValues(typeof(CanvasPanelDisplay))
        select new object[] { fullHeight, (int)display };

    [Fact]
    public void InvalidDimensionsUseDefaultsAndFiniteDimensionsAreClamped()
    {
        Assert.Equal(CanvasPanelSize.DefaultWidth, CanvasPanelSize.DefaultHeight);
        Assert.Equal(CanvasPanelSize.DefaultWidth, CanvasPanelSize.Width(float.NaN));
        Assert.Equal(CanvasPanelSize.DefaultHeight, CanvasPanelSize.Height(float.PositiveInfinity));
        Assert.Equal(300f, CanvasPanelSize.Width(-100));
        Assert.Equal(220f, CanvasPanelSize.Height(-100));
        Assert.Equal(900f, CanvasPanelSize.Width(1000));
    }

    [Theory]
    [MemberData(nameof(PresetIndices))]
    public void PresetDimensionsResolveTheirOwnIndices(int i)
    {
        Assert.Equal(i, CanvasPanelSize.PresetIndex(CanvasPanelSize.PresetWidth(i), CanvasPanelSize.PresetHeight(i)));
    }

    [Fact]
    public void CustomDimensionsHaveNoPresetIndex()
    {
        Assert.Equal(-1, CanvasPanelSize.PresetIndex(500, 420));
    }

    [Fact]
    public void DisplayHeightRespectsModeAndNormalizesInvalidValues()
    {
        Assert.Equal(360f, CanvasPanelSize.DisplayHeight(360, CanvasPanelDisplay.Full));
        Assert.Equal(180f, CanvasPanelSize.DisplayHeight(360, CanvasPanelDisplay.Half));
        Assert.Equal(360f, CanvasPanelSize.DisplayHeight(360, (CanvasPanelDisplay)2));
        Assert.Equal(210f, CanvasPanelSize.DisplayHeight(420, CanvasPanelDisplay.Half));
        Assert.Equal(110f, CanvasPanelSize.DisplayHeight(220, CanvasPanelDisplay.Half));
        Assert.Equal(180f, CanvasPanelSize.DisplayHeight(float.NaN, CanvasPanelDisplay.Half));
    }

    [Fact]
    public void DisplayModeCyclesBetweenFullAndHalf()
    {
        Assert.Equal(CanvasPanelDisplay.Half, CanvasPanelSize.NextDisplay(CanvasPanelDisplay.Full));
        Assert.Equal(CanvasPanelDisplay.Full, CanvasPanelSize.NextDisplay(CanvasPanelDisplay.Half));
        Assert.Equal(CanvasPanelDisplay.Full, CanvasPanelSize.NextDisplay((CanvasPanelDisplay)2));
    }

    [Theory]
    [MemberData(nameof(DisplayGripCases))]
    public void DisplayModesKeepTheGripAtTheSamePosition(float fullHeight, int displayValue)
    {
        var display = (CanvasPanelDisplay)displayValue;
        float fixedGrip = (fullHeight + CanvasPanelSize.HeaderHeight) / 2;
        float visibleGrip = CanvasPanelSize.DisplayTopOffset(fullHeight, display) +
            (CanvasPanelSize.DisplayHeight(fullHeight, display) + CanvasPanelSize.HeaderHeight) / 2;
        Assert.Equal(fixedGrip, visibleGrip);
    }

}
