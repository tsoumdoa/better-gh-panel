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

    [Theory]
    [InlineData(110f)]
    [InlineData(180f)]
    [InlineData(220f)]
    [InlineData(360f)]
    [InlineData(900f)]
    public void UnitMenuFitsThePanelAndPagesToDecimalPlaces(float height)
    {
        var menu = new CanvasMenu();
        var panel = new System.Drawing.RectangleF(0, 0, 300, height);
        var anchor = new System.Drawing.RectangleF(180, height - 24, 93, 17);
        menu.Toggle(CanvasMenuKind.Units, PanelSnapshot.Empty, new PanelQueryOptions());
        menu.Layout(panel, anchor, anchor, anchor, anchor);
        menu.MarkPainted();
        Assert.Equal(PanelUnits.ConversionCount + 4, menu.Count);
        Assert.True(menu.Bounds.Top >= panel.Top + 25 && menu.Bounds.Bottom < anchor.Top && menu.Bounds.Right <= panel.Right);
        Assert.True(menu.Bounds.Width < 180 && menu.Bounds.Right == anchor.Right);
        Assert.Equal("mm² → m²", menu.Label(3));
        Assert.True(menu.IsSelected(3, new PanelQueryOptions(), PanelUnitConversion.SquareMillimetersToSquareMeters, 2));
        int decimalsIndex = PanelUnits.ConversionCount + 1;
        Assert.Equal("Decimal places… (2)", menu.Label(decimalsIndex, decimals: 2));
        Assert.Equal("mm³ → m³", menu.Label((int)PanelUnitConversion.CubicMillimetersToCubicMeters));
        Assert.True(menu.Hover(new System.Drawing.PointF(menu.Bounds.Left + 5, menu.Bounds.Top + 5)));
        Assert.Equal(menu.Offset, menu.HoveredIndex);
        Assert.False(menu.Hover(new System.Drawing.PointF(menu.Bounds.Left + 5, menu.Bounds.Top + 5)));
        if (menu.Count > menu.Visible)
        {
            while (menu.Offset + menu.Visible <= decimalsIndex)
            {
                menu.TrySelect(new System.Drawing.PointF(menu.Bounds.Right - 5, menu.Bounds.Bottom - 5), out _);
                menu.MarkPainted();
            }
            Assert.True(menu.TrySelect(new System.Drawing.PointF(menu.Bounds.Left + 5, menu.Bounds.Top + (decimalsIndex - menu.Offset) * 20 + 5), out int index));
            Assert.Equal(decimalsIndex, index);
        }
    }

}
