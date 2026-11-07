using Xunit;
using System;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using static PanelQueryTestData;

public class CanvasMenuTests
{
    [Fact]
    public void BranchMenusBelongToTheirCapturedSnapshot()
    {
        var branches = Enumerable.Range(0, 100).Select(i => new PanelBranch("{" + i + "}", 0)).ToArray();
        var snapshot = new PanelSnapshot(Array.Empty<PanelRow>(), branches);
        var menu = new CanvasMenu();
        var panel = new System.Drawing.RectangleF(0, 0, 360, 280);
        var anchor = new System.Drawing.RectangleF(9, 60, 226, 22);
        menu.Toggle(CanvasMenuKind.Branch, snapshot, new PanelQueryOptions());
        menu.Layout(panel, anchor, anchor, anchor);
        Assert.True(menu.IsCurrent(snapshot));
        Assert.False(menu.IsCurrent(PanelSnapshot.Empty));
        Assert.Equal(102, menu.Count);
    }

    [Fact]
    public void UnpaintedMenuPagesCannotBeSelectedOrHovered()
    {
        var branches = Enumerable.Range(0, 100).Select(i => new PanelBranch("{" + i + "}", 0)).ToArray();
        var snapshot = new PanelSnapshot(Array.Empty<PanelRow>(), branches);
        var menu = new CanvasMenu();
        var panel = new System.Drawing.RectangleF(0, 0, 360, 280);
        var anchor = new System.Drawing.RectangleF(9, 60, 226, 22);
        menu.Toggle(CanvasMenuKind.Branch, snapshot, new PanelQueryOptions());
        menu.Layout(panel, anchor, anchor, anchor);
        var next = new System.Drawing.PointF(menu.Bounds.Right - 5, menu.Bounds.Bottom - 5);
        var firstRow = new System.Drawing.PointF(menu.Bounds.X + 5, menu.Bounds.Y + 5);
        Assert.False(menu.TrySelect(firstRow, out _)); // No page has been painted yet.
        menu.MarkPainted();
        Assert.False(menu.TrySelect(next, out _));
        Assert.False(menu.TrySelect(firstRow, out _)); // The next page has not been painted.
        Assert.False(menu.Hover(firstRow));
    }

    [Fact]
    public void BranchPagingClampsAtTheLastPageAndResolvesSourcePaths()
    {
        var branches = Enumerable.Range(0, 100).Select(i => new PanelBranch("{" + i + "}", 0)).ToArray();
        var snapshot = new PanelSnapshot(Array.Empty<PanelRow>(), branches);
        var menu = new CanvasMenu();
        var panel = new System.Drawing.RectangleF(0, 0, 360, 280);
        var anchor = new System.Drawing.RectangleF(9, 60, 226, 22);
        menu.Toggle(CanvasMenuKind.Branch, snapshot, new PanelQueryOptions());
        menu.Layout(panel, anchor, anchor, anchor);
        var next = new System.Drawing.PointF(menu.Bounds.Right - 5, menu.Bounds.Bottom - 5);
        menu.MarkPainted();
        menu.TrySelect(next, out _);
        for (int i = 0; i < 50; i++) { menu.MarkPainted(); Assert.False(menu.TrySelect(next, out _)); }
        menu.MarkPainted();
        Assert.Equal(menu.Count - menu.Visible, menu.Offset);
        Assert.True(menu.TrySelect(new System.Drawing.PointF(menu.Bounds.X + 5, menu.Bounds.Y + 5), out var choice));
        Assert.Equal(menu.Offset, choice);
        Assert.Equal(branches[choice - 2].Path, menu.BranchPathAt(choice));
    }

    [Fact]
    public void ClickingOutsideTheMenuClosesIt()
    {
        var branches = Enumerable.Range(0, 100).Select(i => new PanelBranch("{" + i + "}", 0)).ToArray();
        var snapshot = new PanelSnapshot(Array.Empty<PanelRow>(), branches);
        var menu = new CanvasMenu();
        var panel = new System.Drawing.RectangleF(0, 0, 360, 280);
        var anchor = new System.Drawing.RectangleF(9, 60, 226, 22);
        menu.Toggle(CanvasMenuKind.Branch, snapshot, new PanelQueryOptions());
        menu.Layout(panel, anchor, anchor, anchor);
        menu.MarkPainted();
        Assert.False(menu.TrySelect(new System.Drawing.PointF(-10, -10), out _));
        Assert.Equal(CanvasMenuKind.None, menu.Kind);
    }

    [Fact]
    public void TypeMenuRetainsSavedTypesAndTogglesClosed()
    {
        var branches = Enumerable.Range(0, 100).Select(i => new PanelBranch("{" + i + "}", 0)).ToArray();
        var snapshot = new PanelSnapshot(Array.Empty<PanelRow>(), branches);
        var menu = new CanvasMenu();
        var panel = new System.Drawing.RectangleF(0, 0, 360, 280);
        var anchor = new System.Drawing.RectangleF(9, 60, 226, 22);
        menu.Toggle(CanvasMenuKind.Type, snapshot, new PanelQueryOptions { TypeName = "Saved type" });
        menu.Layout(panel, anchor, anchor, anchor);
        Assert.Equal("Saved type", menu.TypeNameAt(2));
        Assert.Equal("Invalid only", menu.Label(1));
        Assert.True(menu.IsSelected(1, new PanelQueryOptions { OnlyInvalid = true }, PanelUnitConversion.None, -1));
        menu.Toggle(CanvasMenuKind.Type, snapshot, new PanelQueryOptions());
        Assert.Equal(CanvasMenuKind.None, menu.Kind);
    }

    [Fact]
    public void TypeHintsFollowSearchAndSavedRestrictions()
    {
        var options = new PanelQueryOptions();
        Assert.Equal("Num/Text +1", CanvasMenu.TypeHint(Query(options), options));
        options.Search = "Alpha";
        Assert.Equal("Text", CanvasMenu.TypeHint(Query(options), options));
        options.Search = "no matches";
        Assert.Equal("Num/Text +1", CanvasMenu.TypeHint(Query(options), options));
        options.TypeName = "Saved type";
        Assert.Equal("Saved type", CanvasMenu.TypeHint(Query(options), options));
        options.OnlyInvalid = true;
        Assert.Equal("Invalid", CanvasMenu.TypeHint(Query(options), options));
        Assert.Equal("No data", CanvasMenu.TypeHint(PanelQueryEngine.Execute(PanelSnapshot.Empty, new PanelQueryOptions()), new PanelQueryOptions()));
    }

    [Fact]
    public void GeometryTypeHintsSummarizeAndFilterTypes()
    {
        var geometry = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "Point", "(0,0,0)", null),
            new PanelRow(1, "{0}", "Surface", "<Surface>", null),
            new PanelRow(2, "{0}", "Mesh", "<Mesh>", null)
        }, new[] { new PanelBranch("{0}", 3) });
        PanelQueryOptions? options = new PanelQueryOptions();
        var geometryResult = PanelQueryEngine.Execute(geometry, options);
        Assert.Equal("Mesh/Pt +1", CanvasMenu.TypeHint(geometryResult, options));
        Assert.Equal("Mesh: 1, Point: 1, Surface: 1", CanvasMenu.TypeDetails(geometryResult));
        options.TypeName = "Surface";
        geometryResult = PanelQueryEngine.Execute(geometry, options);
        Assert.Equal("Srf", CanvasMenu.TypeHint(geometryResult, options));
        Assert.Equal("Surface", geometryResult.Snapshot.Rows[geometryResult.RowPositions[0]].TypeName);
        Assert.Equal("Plugin geometry", CanvasMenu.ShortTypeName("Plugin geometry"));
    }

    [Fact]
    public void UnitMenuOpensAboveItsAnchorWithEveryChoice()
    {
        var menu = new CanvasMenu();
        var panel = new System.Drawing.RectangleF(0, 0, 300, 110);
        var anchor = new System.Drawing.RectangleF(180, 86, 93, 17);
        menu.Toggle(CanvasMenuKind.Units, PanelSnapshot.Empty, new PanelQueryOptions());
        menu.Layout(panel, anchor, anchor, anchor, anchor);
        menu.MarkPainted();
        Assert.Equal(PanelUnits.ConversionCount + 4, menu.Count);
        Assert.True(menu.Bounds.Bottom < anchor.Top && menu.Bounds.Right <= panel.Right && menu.Visible == menu.Count);
        Assert.True(menu.Bounds.Width < 180 && menu.Bounds.Right == anchor.Right);
        Assert.Equal("mm² → m²", menu.Label(3));
        Assert.True(menu.IsSelected(3, new PanelQueryOptions(), PanelUnitConversion.SquareMillimetersToSquareMeters, 2));
        int decimalsIndex = PanelUnits.ConversionCount + 1;
        Assert.Equal("Decimal places… (2)", menu.Label(decimalsIndex, decimals: 2));
        Assert.Equal("mm³ → m³", menu.Label((int)PanelUnitConversion.CubicMillimetersToCubicMeters));
        Assert.True(menu.Hover(new System.Drawing.PointF(menu.Bounds.Left + 5, menu.Bounds.Top + 5)));
        Assert.Equal(menu.Offset, menu.HoveredIndex);
        Assert.False(menu.Hover(new System.Drawing.PointF(menu.Bounds.Left + 5, menu.Bounds.Top + 5)));
        Assert.True(menu.TrySelect(new System.Drawing.PointF(menu.Bounds.Left + 5, menu.Bounds.Top + decimalsIndex * 20 + 5), out int index));
        Assert.Equal(decimalsIndex, index);
    }

    [Fact]
    public void MenusFlipToTheViewportSideWithMoreRoomAndPageWhatDoesNotFit()
    {
        var panel = new System.Drawing.RectangleF(0, 0, 300, 220);
        var anchor = new System.Drawing.RectangleF(180, 196, 93, 17);
        var units = new CanvasMenu();
        units.Toggle(CanvasMenuKind.Units, PanelSnapshot.Empty, new PanelQueryOptions());
        units.Layout(panel, default, default, default, anchor, viewport: new System.Drawing.RectangleF(0, 150, 600, 600));
        Assert.True(units.Bounds.Top == anchor.Bottom + 2 && units.Visible == units.Count,
            "Units opens below its anchor when the viewport has no room above");

        units.Layout(panel, default, default, default, anchor, viewport: new System.Drawing.RectangleF(0, 100, 600, 160));
        Assert.True(units.Bounds.Bottom == anchor.Top - 3 && units.Bounds.Top >= 104 && units.Visible == 3 && units.Count > units.Visible,
            "With no room on either side, the menu keeps the larger side and pages the rows that do not fit");
        units.MarkPainted();
        units.TrySelect(new System.Drawing.PointF(units.Bounds.Right - 5, units.Bounds.Bottom - 5), out _);
        Assert.Equal(3, units.Offset);

        var sort = new CanvasMenu();
        var sortAnchor = new System.Drawing.RectangleF(100, 196, 60, 17);
        sort.Toggle(CanvasMenuKind.Sort, PanelSnapshot.Empty, new PanelQueryOptions());
        sort.Layout(panel, default, sortAnchor, default, viewport: new System.Drawing.RectangleF(0, -500, 600, 720));
        Assert.True(sort.Bounds.Bottom == sortAnchor.Top - 3 && sort.Visible == sort.Count,
            "A dropdown opens above its anchor near the bottom of the viewport");
        sort.Layout(panel, default, sortAnchor, default, viewport: new System.Drawing.RectangleF(0, -500, 600, 1000));
        Assert.True(sort.Bounds.Top == sortAnchor.Bottom + 2, "A dropdown that fits below its anchor opens downward");
    }

}
