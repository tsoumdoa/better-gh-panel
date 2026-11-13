using Xunit;
using System.Drawing;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

public class ItemOrderTests
{
    [Fact]
    public void LabelDeclutteringAndPreviewMenuKeepItemOrderAccessible()
    {
        var menu = new CanvasMenu();
        var options = new PanelQueryOptions();
        var panel = new RectangleF(50, 100, 300, 110);
        menu.Toggle(CanvasMenuKind.Preview, PanelSnapshot.Empty, options);
        menu.Layout(panel, default, default, default, preview: new RectangleF(110, 102, 112, 18));
        menu.MarkPainted();
        Assert.True(menu.Visible == menu.Count,
            "All preview scopes and value controls fit without paging");
        Assert.True(menu.TrySelect(new PointF(menu.Bounds.Right - 8, menu.Bounds.Top + 3 * 20 + 10), out int selected) && selected == 3 &&
            menu.Label(3) == "Show item order", "The additional item-order toggle is directly reachable in minimum Half mode");
        Assert.True(menu.IsSelected(3, options, PanelUnitConversion.None, -1, PanelPreviewMode.Off, true) &&
            menu.IsSelected(2, options, PanelUnitConversion.None, -1, PanelPreviewMode.Off, true) &&
            !menu.IsSelected(3, options, PanelUnitConversion.None, -1, PanelPreviewMode.FilteredResults, false),
            "The saved order-label checkbox is independent from the mutually exclusive preview scope");
    }

    [Fact]
    public void PreviewSizeAndLimitSubmenusKeepCurrentSettingsReachable()
    {
        var menu = new CanvasMenu(); var options = new PanelQueryOptions();
        var panel = new RectangleF(50, 100, 300, 110); var anchor = new RectangleF(110, 102, 112, 18);
        menu.Toggle(CanvasMenuKind.Preview, PanelSnapshot.Empty, options);
        menu.Layout(panel, default, default, default, preview: anchor); menu.MarkPainted();
        Assert.Equal("Dot size", menu.Label(CanvasPreviewRows.Size));
        Assert.Equal("Preview limit", menu.Label(CanvasPreviewRows.Limit));
        Assert.True(menu.TrySelect(new PointF(menu.Bounds.Left + 10, menu.Bounds.Top + CanvasPreviewRows.Limit * 20 + 10), out int index));
        Assert.Equal(CanvasPreviewRows.Limit, index);
        foreach (var kind in new[] { CanvasMenuKind.DotSize, CanvasMenuKind.PreviewLimit })
        {
            menu.Close(); menu.Toggle(kind, PanelSnapshot.Empty, options);
            menu.Layout(panel, default, default, default, preview: anchor); menu.MarkPainted();
            Assert.True(menu.IsPreviewPage && menu.Visible == menu.Count);
            Assert.Equal("‹ Preview", menu.Label(0));
            for (int choice = 0; choice < 3; choice++)
            {
                Assert.True(menu.IsSelected(choice + 1, options, PanelUnitConversion.None, -1,
                    size: (PanelDotSize)choice, limit: PanelDotSettings.LimitAt(choice)));
                Assert.True(menu.TrySelect(new PointF(menu.Bounds.Left + 10, menu.Bounds.Top + (choice + 1) * 20 + 10), out index));
                Assert.Equal(choice + 1, index);
            }
        }
    }

}
