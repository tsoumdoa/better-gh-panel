using Xunit;
using System.Drawing;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

public class ItemOrderTests
{
    [Fact]
    public void PreviewMenuKeepsItemOrderAccessible()
    {
        var menu = new CanvasMenu();
        var options = new PanelQueryOptions();
        var panel = new RectangleF(50, 100, 300, 110);
        menu.Toggle(CanvasMenuKind.Preview, PanelSnapshot.Empty, options);
        menu.Layout(panel, default, default, default, preview: new RectangleF(110, 102, 112, 18));
        menu.MarkPainted();
        Assert.True(menu.Visible == menu.Count,
            "All preview scopes and value controls fit without paging");
        Assert.True(menu.TrySelect(new PointF(menu.Bounds.Right - 8, menu.Bounds.Top + CanvasPreviewRows.ItemOrder * 20 + 10), out int selected) && selected == CanvasPreviewRows.ItemOrder &&
            menu.Label(CanvasPreviewRows.ItemOrder) == "Show item order", "The additional item-order toggle is directly reachable in minimum Half mode");
        Assert.True(menu.IsSelected(CanvasPreviewRows.ItemOrder, options, PanelUnitConversion.None, -1, PanelPreviewMode.Off, true) &&
            menu.IsSelected(3, options, PanelUnitConversion.None, -1, PanelPreviewMode.Off, true) &&
            !menu.IsSelected(CanvasPreviewRows.ItemOrder, options, PanelUnitConversion.None, -1, PanelPreviewMode.FilteredResults, false),
            "The saved order-label checkbox is independent from the mutually exclusive preview scope");
    }

    [Fact]
    public void PreviewSubmenusKeepCurrentSettingsReachable()
    {
        var menu = new CanvasMenu(); var options = new PanelQueryOptions();
        var panel = new RectangleF(50, 100, 300, 110); var anchor = new RectangleF(110, 102, 112, 18);
        menu.Toggle(CanvasMenuKind.Preview, PanelSnapshot.Empty, options);
        menu.Layout(panel, default, default, default, preview: anchor); menu.MarkPainted();
        int matchScope = CanvasPreviewRows.MatchScope;
        Assert.Equal(12, menu.Count);
        Assert.Equal(new[] { "Selection", "Results", "Match / No match", "Off", "Show all dots", "Preview limit" },
            new[] { menu.Label(0), menu.Label(1), menu.Label(matchScope), menu.Label(3), menu.Label(CanvasPreviewRows.ShowAllDots), menu.Label(CanvasPreviewRows.Limit) });
        Assert.Equal("Grey…", menu.Trailing(matchScope, PanelDotPlacement.Centre, PanelValuePalette.Off));
        Assert.Equal("Red…", menu.Trailing(matchScope, PanelDotPlacement.Centre, PanelValuePalette.Off, noMatchStyle: PanelNoMatchStyle.Red));
        Assert.Equal("Ghosted…", menu.Trailing(matchScope, PanelDotPlacement.Centre, PanelValuePalette.Off, noMatchStyle: PanelNoMatchStyle.Ghosted));
        Assert.True(menu.RowEnabled(matchScope, false, false, PanelValuePalette.Off, PanelPreviewMode.Off));
        Assert.False(menu.IsSelected(matchScope, options, PanelUnitConversion.None, -1));
        Assert.True(menu.IsSelected(matchScope, options, PanelUnitConversion.None, -1, PanelPreviewMode.PassFail));
        Assert.False(menu.RowEnabled(CanvasPreviewRows.ShowAllDots, false, false, PanelValuePalette.Off));
        Assert.True(menu.RowEnabled(CanvasPreviewRows.ShowAllDots, true, false, PanelValuePalette.Off));
        Assert.False(menu.RowEnabled(CanvasPreviewRows.ShowAllDots, true, false, PanelValuePalette.Off, PanelPreviewMode.Off));
        Assert.True(menu.IsSectionStart(CanvasPreviewRows.ShowAllDots));
        Assert.False(menu.IsSelected(CanvasPreviewRows.ShowAllDots, options, PanelUnitConversion.None, -1));
        Assert.True(menu.IsSelected(CanvasPreviewRows.ShowAllDots, options, PanelUnitConversion.None, -1, showAllDots: true));
        Assert.False(menu.RowEnabled(CanvasPreviewRows.Size, false, false, PanelValuePalette.Viridis));
        Assert.True(menu.RowEnabled(CanvasPreviewRows.Size, true, false, PanelValuePalette.Off));
        Assert.True(menu.RowEnabled(CanvasPreviewRows.Limit, false, false, PanelValuePalette.Off));
        Assert.Equal("Dot size", menu.Label(CanvasPreviewRows.Size));
        int occlusion = CanvasPreviewRows.HideOccludedDots;
        Assert.Equal("Hide dots behind objects", menu.Label(occlusion));
        Assert.False(menu.RowEnabled(occlusion, false, false, PanelValuePalette.Viridis));
        Assert.True(menu.RowEnabled(occlusion, false, true, PanelValuePalette.Off));
        Assert.False(menu.RowEnabled(occlusion, true, true, PanelValuePalette.Off, PanelPreviewMode.Off));
        Assert.False(menu.RowEnabled(occlusion, true, true, PanelValuePalette.Off, showAllDots: true));
        Assert.True(menu.IsSelected(occlusion, options, PanelUnitConversion.None, -1));
        Assert.False(menu.IsSelected(occlusion, options, PanelUnitConversion.None, -1, showAllDots: true));
        Assert.False(menu.IsSelected(occlusion, options, PanelUnitConversion.None, -1, hideOccludedDots: false));
        Assert.Equal("Preview limit", menu.Label(CanvasPreviewRows.Limit));
        Assert.True(menu.TrySelect(new PointF(menu.Bounds.Left + 10, menu.Bounds.Top + CanvasPreviewRows.Limit * 20 + 10), out int index));
        Assert.Equal(CanvasPreviewRows.Limit, index);
        foreach (var kind in new[] { CanvasMenuKind.DotSize, CanvasMenuKind.PreviewLimit, CanvasMenuKind.NoMatchStyle })
        {
            menu.Close(); menu.Toggle(kind, PanelSnapshot.Empty, options);
            menu.Layout(panel, default, default, default, preview: anchor); menu.MarkPainted();
            Assert.True(menu.IsPreviewPage && menu.Visible == menu.Count);
            Assert.Equal("‹ Preview", menu.Label(0));
            if (kind == CanvasMenuKind.NoMatchStyle)
            {
                Assert.True(menu.IsSectionStart(1));
                Assert.False(menu.IsSelected(0, options, PanelUnitConversion.None, -1));
                Assert.Equal("No match in red", menu.Label(1));
                Assert.Equal("No match in grey", menu.Label(2));
                Assert.Equal("No match ghosted", menu.Label(3));
                Assert.True(menu.IsSelected(2, options, PanelUnitConversion.None, -1));
            }
            for (int choice = 0; choice < 3; choice++)
            {
                Assert.True(menu.IsSelected(choice + 1, options, PanelUnitConversion.None, -1,
                    size: (PanelDotSize)choice, limit: PanelDotSettings.LimitAt(choice), noMatchStyle: (PanelNoMatchStyle)choice));
                Assert.True(menu.RowEnabled(choice + 1, false, false, PanelValuePalette.Off, PanelPreviewMode.Off));
                Assert.True(menu.TrySelect(new PointF(menu.Bounds.Left + 10, menu.Bounds.Top + (choice + 1) * 20 + 10), out index));
                Assert.Equal(choice + 1, index);
            }
        }
    }

}
