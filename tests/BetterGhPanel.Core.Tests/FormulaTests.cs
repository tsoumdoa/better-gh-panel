using System;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Xunit;

public sealed class FormulaTests
{
    private static PanelFormula Formula(string text)
    {
        Assert.True(PanelFormula.TryCompile(text, out var formula, out string error), error);
        return formula!;
    }

    [Theory]
    [InlineData("x*2+3", 4, 11)]
    [InlineData("(x+2)*3", 4, 18)]
    [InlineData("x^2^3", 2, 256)]
    [InlineData("-x^2", 3, -9)]
    [InlineData("(-x)^2", 3, 9)]
    [InlineData("x^-2", 2, .25)]
    [InlineData("x--2", 3, 5)]
    [InlineData("abs(x)", -3, 3)]
    [InlineData("sqrt(x)", 9, 3)]
    [InlineData("floor(x)", -1.2, -2)]
    [InlineData("ceil(x)", -1.2, -1)]
    [InlineData("min(x,2)", 3, 2)]
    [InlineData("max(x-50,0)", 30, 0)]
    [InlineData("min(max(x,2),sqrt(16))", 3, 3)]
    [InlineData("X+2e3+.5", 1, 2001.5)]
    [InlineData("x/pi", Math.PI, 1)]
    [InlineData("x × 2 ÷ 4 − 1 – 2", 8, 1)]
    public void EvaluatesWithUsualPrecedence(string text, double x, double expected) => Assert.Equal(expected, Formula(text).Evaluate(x));

    [Theory]
    [InlineData(" *2 ", "x*2", 20)]
    [InlineData("−50", "x-50", -40)]
    [InlineData("+2", "x+2", 12)]
    [InlineData("/2", "x/2", 5)]
    [InlineData("^2", "x^2", 100)]
    public void SavesExpandedNormalizedShorthand(string text, string normalized, double expected)
    {
        var formula = Formula(text);
        Assert.Equal(normalized, formula.Text);
        Assert.Equal(expected, formula.Evaluate(10));
    }

    [Theory]
    [InlineData("", "Use x for the shown number, e.g. x*2")]
    [InlineData("2*pi", "Use x for the shown number, e.g. x*2")]
    [InlineData("x+y", "Unknown name 'y'. Use x")]
    [InlineData("x+1,5", "Use . for decimals (1.5, not 1,5)")]
    [InlineData("x-50mm", "Leave out units like 'mm'. x is already in the shown unit")]
    [InlineData("(x+2", "Missing )")]
    [InlineData("x+2)", "Extra )")]
    [InlineData("x*", "Formula ends early")]
    [InlineData("sqrt(x,2)", "sqrt takes 1 value")]
    [InlineData("max(x)", "max takes 2 values")]
    [InlineData("abs()", "abs takes 1 value")]
    public void GivesUsefulErrors(string text, string expected)
    {
        Assert.False(PanelFormula.TryCompile(text, out var formula, out string error));
        Assert.Null(formula);
        Assert.Equal(expected, error);
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
        Assert.Equal(-30, sorted.Statistics.Minimum);
        Assert.Equal(-10, PanelFilterValue.OutputValue(snapshot, snapshot.Rows[0], PanelSearchMode.Length, null, formula: options.Formula)!.Number);
        options.Formula = Formula("floor(x/100)"); options.CreateSet = true;
        var set = PanelQueryEngine.Execute(snapshot, options);
        Assert.Single(set.RowPositions);
        Assert.Equal("0 mm", set.DisplaySet!.Values[set.RowPositions[0]].Text);
    }

    [Theory]
    [InlineData("x/0")]
    [InlineData("sqrt(-x)")]
    [InlineData("x^2000")]
    public void NonFiniteResultsAreUnavailableNullAndExcluded(string text)
    {
        var snapshot = Curves(); var row = snapshot.Rows[0]; var formula = Formula(text);
        Assert.Contains("unavailable", PanelValuePresentation.FormatDisplay(row, PanelUnitConversion.None, -1, formula: formula));
        Assert.Null(PanelFilterValue.OutputValue(snapshot, row, PanelSearchMode.Length, null, formula: formula));
        var options = new PanelQueryOptions { Formula = formula, SearchMode = PanelSearchMode.Length, Search = "> -100" };
        Assert.DoesNotContain(0, PanelQueryEngine.Execute(snapshot, options).RowPositions);
        options.Search = ""; options.DisplayFilterValue = true; options.OutputValues = true;
        Assert.Null(PanelQueryEngine.Execute(snapshot, options).OutputValues![0]);
        options.DisplayFilterValue = false; options.CreateSet = true;
        var set = PanelQueryEngine.Execute(snapshot, options).DisplaySet!;
        Assert.True(set.Values[0].Unavailable);
    }

    [Theory]
    [InlineData(PanelSortMode.ValueAscending)]
    [InlineData(PanelSortMode.ValueDescending)]
    public void UnavailableGeometrySortsLast(PanelSortMode sort)
    {
        var result = PanelQueryEngine.Execute(Curves(), new PanelQueryOptions { Formula = Formula("1/x"), SortMode = sort });
        Assert.Equal(2, result.RowPositions.Last());
    }

    [Fact]
    public void DimensionalPropertiesAndCoordinatesTransformButCountsAndChannelsDoNot()
    {
        var formula = Formula("x-50");
        Assert.Equal("-20 mm", PanelPropertyValue.Numeric(30, 1, "mm").Format(PanelUnitConversion.None, -1, true, formula: formula));
        Assert.Equal("40 °", PanelPropertyValue.Numeric(Math.PI / 2, -1, "rad").Format(PanelUnitConversion.RadiansToDegrees, -1, true, formula: formula));
        Assert.Equal("30", PanelPropertyValue.Integer(30).Format(PanelUnitConversion.None, -1, true, formula: formula));
        Assert.Equal("30", PanelPropertyValue.Numeric(30).Format(PanelUnitConversion.None, -1, true, formula: formula));
        var row = new PanelRow(0, "{0}", "Point", "", null, PanelValidity.VerifiedValid, components: new PanelComponents(30, 0, 0, "mm"));
        var snapshot = new PanelSnapshot(new[] { row }, new[] { new PanelBranch("{0}", 1) });
        Assert.Equal(-20, PanelFilterValue.OutputValue(snapshot, row, PanelSearchMode.X, null, formula: formula)!.Number);
        Assert.Equal(1, PanelFilterValue.OutputValue(snapshot, row, PanelSearchMode.ListLength, null, formula: formula)!.Number);
    }

    [Fact]
    public void TableColumnsKeepUnitsAndRoundingWithoutDFormula()
    {
        var formula = Formula("x-50");
        var rawColumn = new PanelRow(0, "{0}", "Number", "1240.5", 1240.5);
        double d = PanelUnits.Transform(1240.5, PanelUnitConversion.MillimetersToMeters, .25, 1, formula);
        Assert.Equal(-48.7, d);
        Assert.Equal("1.3", PanelTableText.Value(rawColumn, PanelUnitConversion.MillimetersToMeters, 1, .25));
    }

    [Fact]
    public void MenuFollowsPipelineAndMarksFormulaState()
    {
        var menu = new CanvasMenu(); var options = new PanelQueryOptions { Formula = Formula("x-50") };
        menu.Toggle(CanvasMenuKind.Units, PanelSnapshot.Empty, options);
        int off = PanelUnits.ConversionCount, on = off + 1;
        Assert.True(menu.IsSectionStart(off));
        Assert.Equal(CanvasNumberSetting.FormulaOff, menu.UnitChoiceAt(off).Setting);
        Assert.Equal(CanvasNumberSetting.Formula, menu.UnitChoiceAt(on).Setting);
        Assert.False(menu.IsSelected(off, options, PanelUnitConversion.None, -1));
        Assert.True(menu.IsSelected(on, options, PanelUnitConversion.None, -1));
        Assert.Equal("Formula… (x−50)", menu.Label(on, formula: options.Formula));
        Assert.Equal(CanvasNumberSetting.RoundingOff, menu.UnitChoiceAt(on + 1).Setting);
    }
}
