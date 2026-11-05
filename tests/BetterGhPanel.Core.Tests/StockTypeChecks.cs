using System;
using System.Collections.Generic;
using BetterGhPanel.Core;
using Xunit;

public sealed class StockTypeChecks
{
    [Fact]
    public void DotPropertiesShareFilteringSetsAndUnitAwareValuePresentation()
    {
        PanelRow Dot(int index, string text, double x) => new PanelRow(index, "{0}", "Text Dot", text, null, PanelValidity.VerifiedValid,
            components: new PanelComponents(x, 0, 0, "mm"), properties: new Dictionary<PanelSearchMode, PanelPropertyValue?> {
                [PanelSearchMode.TextValue] = PanelPropertyValue.String(text),
                [PanelSearchMode.DotFontSize] = PanelPropertyValue.Integer(18),
                [PanelSearchMode.FontFace] = PanelPropertyValue.String("Arial") });
        var snapshot = new PanelSnapshot(new[] { Dot(0, "Lobby", 1000), Dot(1, "Lobby", 2000), Dot(2, "Roof", 3000) },
            new[] { new PanelBranch("{0}", 3) }, PanelUnitConversion.MillimetersToMeters);
        Assert.Equal(new[] { 1, 2 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.X, Search = "> 1" }).RowPositions);
        Assert.Equal("1", PanelFilterValue.Format(snapshot, snapshot.Rows[0], PanelSearchMode.X, -1));
        Assert.Equal("18", PanelFilterValue.Format(snapshot, snapshot.Rows[0], PanelSearchMode.DotFontSize, -1));
        Assert.Equal(new[] { 0, 1, 2 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.FontFace, Search = "= Arial" }).RowPositions);
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.TextValue, DisplayFilterValue = true, CreateSet = true });
        Assert.Equal(new[] { 0, 2 }, result.RowPositions);
        Assert.Equal(new[] { 0, 1 }, result.DisplaySet!.Members[0]);
        Assert.Equal("Lobby", result.DisplaySet.Values[0].Text);
        Assert.Equal(PanelPropertyKind.Text, PanelProperties.Kind(PanelSearchMode.FontFace));
    }
    [Fact]
    public void DimensionMetricsSharePresentedLengthAndAngleValues()
    {
        var length = new PanelGeometryInfo("", 1234.567, 1, "mm", "Length", "");
        var angle = new PanelGeometryInfo("", Math.PI / 2, -1, "rad", "Angle", "").WithProperties(
            new Dictionary<PanelSearchMode, PanelPropertyValue?> { [PanelSearchMode.DimensionAngle] = PanelPropertyValue.Numeric(Math.PI / 2, -1) });
        PanelSnapshot Snapshot(PanelGeometryInfo geometry, PanelUnitConversion units) => new PanelSnapshot(
            new[] { new PanelRow(0, "{0}", "Dimension", "", null, geometryInfo: geometry) }, new[] { new PanelBranch("{0}", 1) }, units);
        var lengths = Snapshot(length, PanelUnitConversion.MillimetersToMeters);
        var angles = Snapshot(angle, PanelUnitConversion.RadiansToDegrees);
        Assert.Equal("1.23 m", PanelValuePresentation.FormatDisplay(lengths.Rows[0], lengths.UnitConversion, 2));
        Assert.Equal("90.00 °", PanelValuePresentation.FormatDisplay(angles.Rows[0], angles.UnitConversion, 2));
        foreach (var sample in new[] { (lengths, PanelSearchMode.Length, 1.23), (angles, PanelSearchMode.DimensionAngle, 90.0) })
        {
            var query = new PanelQueryOptions { SearchMode = sample.Item2, DisplayFilterValue = true, OutputValues = true, DecimalPlaces = 2,
                Search = "= " + sample.Item3.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var result = PanelQueryEngine.Execute(sample.Item1, query);
            Assert.Single(result.RowPositions);
            Assert.Equal(sample.Item3, result.OutputValues![0]!.Number);
            Assert.Equal(sample.Item3, result.Statistics.Mean);
        }
        Assert.Equal(PanelPropertyValue.Numeric(Math.PI / 2, -1).Format(PanelUnitConversion.DegreesToRadians, 2, true),
            angle.FormatCompact(PanelUnitConversion.DegreesToRadians, 2));
    }

}
