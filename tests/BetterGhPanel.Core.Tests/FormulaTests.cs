using System.Linq;
using BetterGhPanel.Core;
using Xunit;

public sealed class FormulaTests
{
    private static PanelFormula Formula(string text)
    {
        Assert.True(PanelFormula.TryCompile(text, out var formula, out string error), error);
        return formula!;
    }

    [Fact]
    public void BoundsParserWork()
    {
        Assert.False(PanelFormula.TryCompile("x" + new string(' ', 201) + "+1", out _, out string length));
        Assert.Contains("200", length);
        Assert.False(PanelFormula.TryCompile(new string('(', 40) + "x" + new string(')', 40), out _, out string depth));
        Assert.Equal("Formula is nested too deeply", depth);
    }

    [Fact]
    public void PipelineConvertsThenFormulaThenFactorThenDecimals()
    {
        var formula = Formula("x*2-1");
        Assert.Equal(1.5, PanelUnits.Transform(1240.5, PanelUnitConversion.MillimetersToMeters, .25, 1, formula));
        Assert.Equal("1.5 m", PanelUnits.FormatGeometry(1240.5, 1, "mm", PanelUnitConversion.MillimetersToMeters, 1, roundUpFactor: .25, formula: formula));
        Assert.Equal("-20 mm", PanelUnits.FormatGeometry(30, 1, "mm", PanelUnitConversion.None, -1, formula: Formula("x-50")));
        Assert.Equal("unavailable", PanelUnits.FormatGeometry(-1, 1, "mm", PanelUnitConversion.None, -1, formula: Formula("abs(x)")));
    }

    private static PanelSnapshot Curves() => new PanelSnapshot(new[] { 10d, 30, 0 }.Select((v, i) =>
        new PanelRow(i, "{0}", "Curve", "", null, geometryInfo: new PanelGeometryInfo("Crv", v, units: "mm"))).ToArray(),
        new[] { new PanelBranch("{0}", 3) });

    [Fact]
    public void ComparisonsStatsSortSetsAndValuesShareTheFormula()
    {
        var snapshot = Curves();
        var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Length, Search = "< -15", Formula = Formula("x-50") };
        var filtered = PanelQueryEngine.Execute(snapshot, options);
        Assert.Equal(new[] { 0, 1, 2 }, filtered.RowPositions);
        options.Search = ""; options.DisplayFilterValue = true; options.SortMode = PanelSortMode.ValueAscending;
        options.Formula = Formula("x*-1");
        var sorted = PanelQueryEngine.Execute(snapshot, options);
        Assert.Equal(new[] { 1, 0, 2 }, sorted.RowPositions);

        Assert.Equal(-10, PanelFilterValue.OutputValue(snapshot, snapshot.Rows[0], PanelSearchMode.Length, null, formula: options.Formula)!.Number);
        options.Formula = Formula("floor(x/100)"); options.CreateSet = true;
        var set = PanelQueryEngine.Execute(snapshot, options);
        Assert.Single(set.RowPositions);
        Assert.Equal("0 mm", set.DisplaySet!.Values[set.RowPositions[0]].Text);
    }

}
