using Xunit;
using System.Drawing;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

public class ItemOrderTests
{
    [Fact]
    public void PreservesBehaviour()
    {
        var grid = new LabelCollisionGrid();
        Assert.True(grid.TryAdd(new RectangleF(40, 40, 30, 24)), "The first priority label occupies its screen rectangle");
        Assert.True(!grid.TryAdd(new RectangleF(60, 45, 30, 24)), "Intersecting labels are rejected across cell boundaries");
        Assert.True(grid.TryAdd(new RectangleF(71, 40, 30, 24)), "Nonoverlapping labels remain visible in shared cells");
        Assert.True(grid.TryAdd(new RectangleF(-10, -10, 20, 20)) && !grid.TryAdd(new RectangleF(-2, 2, 20, 20)),
            "Labels at the viewport edge handle negative screen footprint coordinates");
        Assert.True(!grid.TryAdd(new RectangleF(0, 0, float.NaN, 20)) && !grid.TryAdd(new RectangleF(float.PositiveInfinity, 0, 20, 20)),
            "Invalid screen rectangles are rejected");
        var dense = new LabelCollisionGrid();
        int accepted = 0;
        for (int row = 0; row < 100; row++)
            for (int column = 0; column < 100; column++)
                if (dense.TryAdd(new RectangleF(column * 32, row * 32, 24, 24))) accepted++;
        Assert.True(accepted == 10000, "Decluttering uses spatial overlap rather than silently imposing a global label cap");

        var menu = new CanvasMenu();
        var options = new PanelQueryOptions();
        var panel = new RectangleF(50, 100, 300, 110);
        menu.Toggle(CanvasMenuKind.Preview, PanelSnapshot.Empty, options);
        menu.Layout(panel, default, default, default, preview: new RectangleF(110, 102, 112, 18));
        menu.MarkPainted();
        Assert.True(panel.Contains(menu.Bounds) && menu.Count == 4 && menu.Visible == 4,
            "All preview scopes and the order toggle fit directly in a minimum Half panel");
        Assert.True(menu.TrySelect(new PointF(menu.Bounds.Right - 8, menu.Bounds.Bottom - 5), out int selected) && selected == 3 &&
            menu.Label(3) == "Show item order", "The additional item-order toggle is directly reachable in minimum Half mode");
        Assert.True(menu.IsSelected(3, options, PanelUnitConversion.None, -1, PanelPreviewMode.Off, true) &&
            menu.IsSelected(2, options, PanelUnitConversion.None, -1, PanelPreviewMode.Off, true) &&
            !menu.IsSelected(3, options, PanelUnitConversion.None, -1, PanelPreviewMode.FilteredResults, false),
            "The saved order-label checkbox is independent from the mutually exclusive preview scope");
    }


}
