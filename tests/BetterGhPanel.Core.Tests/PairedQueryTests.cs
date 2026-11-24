using BetterGhPanel.Core;
using Xunit;

public class PairedQueryTests
{
    [Fact]
    public void SearchUsesOriginalPartnersAndKeepsDataRestrictions()
    {
        var data = PanelQueryTestData.Sample();
        var f = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0;1}", "Text", "Wall", null),
            new PanelRow(0, "{0;1;2}", "Text", "Wall", null),
            new PanelRow(0, "{0;10}", "Text", "Beam", null),
            new PanelRow(1, "{0;10}", "Text", "Wall", null)
        }, new[] { new PanelBranch("{0;1}", 1), new PanelBranch("{0;1;2}", 1), new PanelBranch("{0;10}", 2), new PanelBranch("{9}", 0) });
        int?[] partner = { 0, null, 1, 2, 3, null };
        var options = new PanelQueryOptions
        {
            SearchPaired = true, Search = "wall", TypeName = "Number", BranchPath = "{0;1}", BranchPrefix = true,
            PairedSource = new PanelPairedQuerySource(f, position => partner[position])
        };
        Assert.Equal(new[] { 0 }, PanelQueryEngine.Execute(data, options).RowPositions);
        options.SearchMode = PanelSearchMode.ListLength;
        options.Search = "= 1";
        Assert.Equal(new[] { 0 }, PanelQueryEngine.Execute(data, options).RowPositions);
        options.PairedSource = null;
        Assert.Empty(PanelQueryEngine.Execute(data, options).RowPositions);
    }

    [Fact]
    public void PairedNumericSearchConvertsAndRoundsWithoutApplyingTheDataFormula()
    {
        var data = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Number", "2", 2) },
            new[] { new PanelBranch("{0}", 1) }, PanelUnitConversion.MillimetersToMeters);
        var paired = new PanelSnapshot(new[] { new PanelRow(0, "{7}", "Number", "1234", 1234) },
            new[] { new PanelBranch("{7}", 1) });
        Assert.True(PanelFormula.TryCompile("x*2", out var formula, out _));
        var options = new PanelQueryOptions
        {
            SearchPaired = true, SearchMode = PanelSearchMode.Value, Search = "= 1.23", DecimalPlaces = 2,
            Formula = formula, PairedSource = new PanelPairedQuerySource(paired, _ => 0)
        };
        Assert.Equal(new[] { 0 }, PanelQueryEngine.Execute(data, options).RowPositions);
        options.SearchMode = PanelSearchMode.Regex;
        options.Search = @"^1\.23$";
        Assert.Equal(new[] { 0 }, PanelQueryEngine.Execute(data, options).RowPositions);
    }
}
