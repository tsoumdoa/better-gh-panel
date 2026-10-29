using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Xunit;

public sealed class PropertyChecks
{
    private static PanelRow Row(int index, PanelSearchMode mode, PanelPropertyValue? value, PanelValidity validity = PanelValidity.VerifiedValid) =>
        new PanelRow(index, "{0}", "Example", "summary", null, validity,
            properties: new Dictionary<PanelSearchMode, PanelPropertyValue?> { [mode] = value });
    private static PanelSnapshot Snapshot(params PanelRow[] rows) => new PanelSnapshot(rows, new[] { new PanelBranch("{0}", rows.Length) });
    private static PanelQueryOptions Query(PanelSearchMode mode, string search = "") => new PanelQueryOptions { SearchMode = mode, Search = search };

    [Fact]
    public void NumericPropertiesKeepPrecisionUnitsAndValidity()
    {
        var rows = new[] { Row(0, PanelSearchMode.Radius, PanelPropertyValue.Numeric(1234.567, 1, "mm")),
            Row(1, PanelSearchMode.Radius, PanelPropertyValue.Numeric(900, 1, "mm")),
            Row(2, PanelSearchMode.Radius, PanelPropertyValue.Numeric(9000, 1, "mm"), PanelValidity.VerifiedInvalid),
            Row(3, PanelSearchMode.FaceCount, PanelPropertyValue.Numeric(8)) };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 4) }, PanelUnitConversion.MillimetersToMeters);
        var query = Query(PanelSearchMode.Radius, "> 1.23456"); query.DecimalPlaces = 2;
        Assert.Equal(new[] { 0 }, PanelQueryEngine.Execute(snapshot, query).RowPositions);
        Assert.Equal("1.23 m", PanelFilterValue.Format(snapshot, rows[0], query.SearchMode, 2));
        Assert.Equal("1.23", PanelFilterValue.Format(snapshot, rows[0], query.SearchMode, 2, includeUnits: false));
        Assert.Equal("8", PanelFilterValue.Format(snapshot, rows[3], PanelSearchMode.FaceCount, -1));
        Assert.Equal("n/a", PanelFilterValue.Format(snapshot, rows[3], query.SearchMode, 2));
        Assert.Equal("invalid", PanelFilterValue.Format(snapshot, rows[2], query.SearchMode, 2));
    }

    [Fact]
    public void BooleanTextAndDateFiltersUseTheirOwnSemantics()
    {
        var booleans = Snapshot(Row(0, PanelSearchMode.Closed, PanelPropertyValue.Boolean(true)), Row(1, PanelSearchMode.Closed, PanelPropertyValue.Boolean(false)));
        Assert.Equal(new[] { 1 }, PanelQueryEngine.Execute(booleans, Query(PanelSearchMode.Closed, "!= true")).RowPositions);
        Assert.Throws<ArgumentException>(() => PanelQueryEngine.Execute(booleans, Query(PanelSearchMode.Closed, "> True")));
        var text = Snapshot(Row(0, PanelSearchMode.GroupName, PanelPropertyValue.String(" 01 ")), Row(1, PanelSearchMode.GroupName, PanelPropertyValue.String("01")));
        Assert.Equal(new[] { 0 }, PanelQueryEngine.Execute(text, Query(PanelSearchMode.GroupName, "= \" 01 \"")).RowPositions);
        Assert.Throws<ArgumentException>(() => PanelQueryEngine.Execute(text, Query(PanelSearchMode.GroupName, "> name")));
        var time = new DateTime(2026, 10, 29, 12, 0, 0);
        var dates = Snapshot(Row(0, PanelSearchMode.DateTime, PanelPropertyValue.Time(time)), Row(1, PanelSearchMode.DateTime, PanelPropertyValue.Time(time.AddTicks(1))));
        Assert.Equal(new[] { 1 }, PanelQueryEngine.Execute(dates, Query(PanelSearchMode.DateTime, "> 2026-10-29 12:00:00")).RowPositions);
        Assert.Throws<ArgumentException>(() => PanelQueryEngine.Execute(dates, Query(PanelSearchMode.DateTime, "= 10/29/2026")));
    }

    [Fact]
    public void DisplaySetsPreserveNumericLookingTextAndOccurrences()
    {
        var snapshot = Snapshot(Row(0, PanelSearchMode.GroupName, PanelPropertyValue.String("001")), Row(1, PanelSearchMode.GroupName, PanelPropertyValue.String("001")),
            Row(2, PanelSearchMode.GroupName, PanelPropertyValue.String("1")));
        var query = Query(PanelSearchMode.GroupName); query.DisplayFilterValue = true; query.CreateSet = true;
        var result = PanelQueryEngine.Execute(snapshot, query);
        Assert.Equal(new[] { 0, 2 }, result.RowPositions);
        Assert.Null(result.DisplaySet!.Values[0].Number);
        Assert.Equal("001", result.DisplaySet.Values[0].CopyText);
        Assert.Equal(2, result.SetOccurrencesAt(0)!.Count);
        var numbers = Snapshot(Row(0, PanelSearchMode.Radius, PanelPropertyValue.Numeric(1.234)), Row(1, PanelSearchMode.Radius, PanelPropertyValue.Numeric(1.233)));
        query.SearchMode = PanelSearchMode.Radius; query.DecimalPlaces = 2;
        result = PanelQueryEngine.Execute(numbers, query);
        Assert.Single(result.RowPositions);
        Assert.Equal(1.23, result.DisplaySet!.Values[0].Number);
    }

    [Fact]
    public void DeferredPropertiesMeasureOffscreenAndKeepCheapPropertiesAvailable()
    {
        var properties = new Dictionary<PanelSearchMode, PanelPropertyValue?> { [PanelSearchMode.Planar] = null, [PanelSearchMode.Degree] = PanelPropertyValue.Numeric(3) };
        var info = new PanelGeometryInfo("Curve", canMeasure: true).WithProperties(properties);
        var row = new PanelRow(0, "{0}", "Curve", "Curve", null, geometryInfo: info);
        var snapshot = Snapshot(row);
        Assert.True(snapshot.SupportsSearchMode(PanelSearchMode.Planar));
        var query = Query(PanelSearchMode.Planar, "= True");
        Assert.Equal(new[] { 0 }, PanelQueryEngine.GeometryCandidates(snapshot, query));
        Assert.Equal("measuring…", PanelFilterValue.Format(snapshot, row, PanelSearchMode.Planar, -1));
        properties[PanelSearchMode.Planar] = PanelPropertyValue.Boolean(true);
        Assert.Null(info.Properties[PanelSearchMode.Planar]); // Input collections cannot mutate a snapshot.
        var measured = new Dictionary<int, PanelGeometryInfo> { [0] = new PanelGeometryInfo("Curve", 12).WithProperties(properties) };
        Assert.Equal(new[] { 0 }, PanelQueryEngine.Execute(snapshot, query, measuredGeometry: measured).RowPositions);
        Assert.Empty(PanelQueryEngine.GeometryCandidates(snapshot, Query(PanelSearchMode.Degree, "= 3")));
        var sortedSummary = Query(PanelSearchMode.Degree, "= 3"); sortedSummary.SortMode = PanelSortMode.ValueAscending;
        Assert.Equal(new[] { 0 }, PanelQueryEngine.GeometryCandidates(snapshot, sortedSummary));
        var summarySet = Query(PanelSearchMode.Degree, "= 3"); summarySet.CreateSet = true;
        Assert.Equal(new[] { 0 }, PanelQueryEngine.GeometryCandidates(snapshot, summarySet));
        Assert.Equal("3", PanelFilterValue.Format(snapshot, row, PanelSearchMode.Degree, -1, info.WithUnavailableMeasurement()));
        Assert.Equal("unavailable", PanelFilterValue.Format(snapshot, row, PanelSearchMode.Planar, -1, info.WithUnavailableMeasurement()));
        query = Query(PanelSearchMode.Planar); query.DisplayFilterValue = true; query.CreateSet = true;
        Assert.Equal(new[] { 0 }, PanelQueryEngine.GeometryCandidates(snapshot, query));
    }

    [Fact]
    public void MenusExposeOnlySupportedPropertiesAndAppropriateOperators()
    {
        var snapshot = Snapshot(Row(0, PanelSearchMode.Closed, PanelPropertyValue.Boolean(true)));
        var options = Query(PanelSearchMode.Radius);
        var menu = new CanvasMenu();
        menu.Toggle(CanvasMenuKind.SearchMode, snapshot, options, supportedSearchModes: Enum.GetValues(typeof(PanelSearchMode)).Cast<PanelSearchMode>().Where(snapshot.SupportsSearchMode));
        var modes = Enumerable.Range(0, menu.Count).Select(menu.SearchModeAt).ToArray();
        Assert.Contains(PanelSearchMode.Closed, modes); Assert.Contains(PanelSearchMode.Radius, modes);
        Assert.DoesNotContain(PanelSearchMode.FaceCount, modes);
        menu.Close(); menu.Toggle(CanvasMenuKind.Comparison, snapshot, options, comparisonMode: PanelSearchMode.Closed);
        Assert.Equal(3, menu.Count);
        Assert.Equal(CanvasComparison.Equal, menu.ComparisonChoiceAt(0).Comparison);
        Assert.Equal(CanvasComparison.NotEqual, menu.ComparisonChoiceAt(1).Comparison);
        Assert.True(menu.ComparisonChoiceAt(2).IsDisplayValue);
        var panel = new RectangleF(0, 0, 300, 110); menu.Layout(panel, panel, panel, panel, search: new RectangleF(6, 25, 88, 21));
        Assert.True(panel.Contains(menu.Bounds));
    }

    [Fact]
    public void MetadataWorksForInvalidRowsAndDisplaySortsSelectedProperty()
    {
        var snapshot = Snapshot(Row(0, PanelSearchMode.FaceCount, PanelPropertyValue.Numeric(10)), Row(1, PanelSearchMode.FaceCount, PanelPropertyValue.Numeric(2)),
            Row(2, PanelSearchMode.FaceCount, PanelPropertyValue.Numeric(99), PanelValidity.Unverified));
        Assert.Equal(new[] { 2 }, PanelQueryEngine.Execute(snapshot, Query(PanelSearchMode.Validity, "= Unverified")).RowPositions);
        var query = Query(PanelSearchMode.FaceCount); query.DisplayFilterValue = true; query.SortMode = PanelSortMode.ValueAscending;
        var result = PanelQueryEngine.Execute(snapshot, query);
        Assert.Equal(new[] { 1, 0, 2 }, result.RowPositions);
        Assert.Equal(0, result.Statistics.NumericCount); // Typed input statistics remain intact without Create set.
    }
}
