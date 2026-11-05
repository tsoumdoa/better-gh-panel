using System;
using System.Collections.Generic;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Xunit;

public sealed class StockTypeChecks
{
    [Fact]
    public void StockCatalogKeepsIndependentSafetyFacetsAndCompactLabels()
    {
        Assert.Equal(PanelStockTypes.All.Count, PanelStockTypes.All.Select(type => type.SdkName).Distinct(StringComparer.Ordinal).Count());
        Assert.All(PanelStockTypes.All.Where(type => type.Has(StockTypeFacets.Capture)), type => Assert.True(type.Has(StockTypeFacets.Validity)));
        Assert.All(PanelStockTypes.All.Where(type => type.Has(StockTypeFacets.Preview)), type =>
            Assert.True(type.Has(StockTypeFacets.Managed) || type.Has(StockTypeFacets.Capture)));
        var dot = PanelStockTypes.All.Single(type => type.SdkName == "GH_TextDot");
        Assert.True(dot.Has(StockTypeFacets.Capture | StockTypeFacets.Preview | StockTypeFacets.Validity));
        Assert.False(dot.Has(StockTypeFacets.Managed));
        Assert.False(PanelStockTypes.All.Single(type => type.SdkName == "GH_Hatch").Has(StockTypeFacets.Preview));
        Assert.Equal("Dot", CanvasMenu.ShortTypeName("Text Dot"));
        Assert.Equal("Cplx", CanvasMenu.ShortTypeName("Complex"));
        Assert.Equal("Plugin type", CanvasMenu.ShortTypeName("Plugin type"));
    }

    [Fact]
    public void DotPropertiesShareFilteringSetsAndUnitAwareValuePresentation()
    {
        PanelRow Dot(int index, string text, double x) => new PanelRow(index, "{0}", "Text Dot", text + " @ location", null, PanelValidity.VerifiedValid,
            properties: new Dictionary<PanelSearchMode, PanelPropertyValue?> {
                [PanelSearchMode.TextValue] = PanelPropertyValue.String(text),
                [PanelSearchMode.LocationX] = PanelPropertyValue.Numeric(x, 1, "mm"),
                [PanelSearchMode.FontHeight] = PanelPropertyValue.Integer(18),
                [PanelSearchMode.FontFace] = PanelPropertyValue.String("Arial") });
        var snapshot = new PanelSnapshot(new[] { Dot(0, "Lobby", 1000), Dot(1, "Lobby", 2000), Dot(2, "Roof", 3000) },
            new[] { new PanelBranch("{0}", 3) }, PanelUnitConversion.MillimetersToMeters);
        Assert.Equal(new[] { 1, 2 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.LocationX, Search = "> 1" }).RowPositions);
        Assert.Equal("1 m", PanelFilterValue.Format(snapshot, snapshot.Rows[0], PanelSearchMode.LocationX, -1));
        Assert.Equal("18", PanelFilterValue.Format(snapshot, snapshot.Rows[0], PanelSearchMode.FontHeight, -1));
        Assert.Equal(new[] { 0, 1, 2 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.FontFace, Search = "= Arial" }).RowPositions);
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.TextValue, DisplayFilterValue = true, CreateSet = true });
        Assert.Equal(new[] { 0, 2 }, result.RowPositions);
        Assert.Equal(new[] { 0, 1 }, result.DisplaySet!.Members[0]);
        Assert.Equal("Lobby", result.DisplaySet.Values[0].Text);
        Assert.Equal(PanelPropertyKind.Text, PanelProperties.Kind(PanelSearchMode.FontFace));
    }
}
