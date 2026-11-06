using System;
using System.Collections.Generic;
using System.Linq;
using BetterGhPanel.Core;
using Xunit;

public sealed class OutputValuesChecks
{
    [Fact]
    public void SetIndicesAndMembersSurviveViewSorting()
    {
        var snapshot = new PanelSnapshot(new[] { 2, 6, 2, 2, 6 }.Select((value, i) =>
            new PanelRow(i, "{0}", "Number", value.ToString(), value)).ToArray(), new[] { new PanelBranch("{0}", 5) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { CreateSet = true, SortMode = PanelSortMode.ValueDescending });
        var set = result.DisplaySet!;
        Assert.Equal(new[] { 1, 0 }, result.RowPositions);
        Assert.Equal(0, set.MemberIndices[0]);
        Assert.Equal(1, set.MemberIndices[1]);
        Assert.Equal(new[] { 0, 2, 3 }, set.Members[0]);
        Assert.Equal(new[] { 1, 4 }, set.ExpandMembers(snapshot, new[] { 1 }));
        Assert.Equal(Enumerable.Range(0, 5), set.AllMembers);
        Assert.True(PanelItemOrder.TryGetNumber(result, 4, out int number));
        Assert.Equal(1, number);
        var selection = new PanelRowSelection();
        selection.SelectIndices(result, new[] { 1 });
        Assert.Equal(new[] { 1 }, selection.OrderedPositions(result));
    }

    [Fact]
    public void ValuesFreezePresentedNumbersTypesAndUnavailableSlots()
    {
        var date = new DateTime(2026, 1, 2, 3, 4, 5);
        var properties = new Dictionary<PanelSearchMode, PanelPropertyValue?> {
            [PanelSearchMode.Radius] = PanelPropertyValue.Numeric(1234.56789, 1, "mm"),
            [PanelSearchMode.Closed] = PanelPropertyValue.Boolean(false),
            [PanelSearchMode.TextValue] = PanelPropertyValue.String("n/a"),
            [PanelSearchMode.DateTime] = PanelPropertyValue.Time(date)
        };
        var snapshot = new PanelSnapshot(new[] {
            new PanelRow(0, "{0}", "Example", "summary", null, PanelValidity.VerifiedValid, properties: properties),
            new PanelRow(1, "{0}", "Text", "missing", null)
        }, new[] { new PanelBranch("{0}", 2) }, PanelUnitConversion.MillimetersToMeters);
        var query = new PanelQueryOptions { OutputValues = true, DisplayFilterValue = true, DecimalPlaces = 2 };
        foreach (var mode in properties.Keys)
        {
            query.SearchMode = mode;
            var result = PanelQueryEngine.Execute(snapshot, query);
            var value = result.OutputValues![0]!;
            Assert.Equal(properties[mode]!.Kind, value.Kind);
            if (mode == PanelSearchMode.Radius) Assert.Equal(1.23, value.Number);
            if (mode == PanelSearchMode.Closed) Assert.Equal(0, value.Number);
            if (mode == PanelSearchMode.TextValue) Assert.Equal("n/a", value.Text);
            if (mode == PanelSearchMode.DateTime) Assert.Equal(date, value.Date);
            Assert.Null(result.OutputValues[1]);
            Assert.Equal(new[] { 0, 1 }, result.RowPositions);
        }
        var pending = new PanelSnapshot(new[] {
            new PanelRow(0, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", canMeasure: true))
        }, new[] { new PanelBranch("{0}", 1) });
        query.SearchMode = PanelSearchMode.Length;
        Assert.True(PanelQueryEngine.RequiresGeometryMeasurements(query));
        Assert.Equal(new[] { 0 }, PanelQueryEngine.GeometryCandidates(pending, query));
        Assert.Null(PanelQueryEngine.Execute(pending, query).OutputValues![0]);
        query.OutputValues = false;
        Assert.False(PanelQueryEngine.RequiresGeometryMeasurements(query)); // Plain display keeps visible-row measurement.
    }

    [Fact]
    public void BranchSetsExpandEqualAndEmptyBranches()
    {
        var snapshot = new PanelSnapshot(new[] {
            new PanelRow(0, "{0}", "Number", "1", 1), new PanelRow(0, "{1}", "Number", "2", 2)
        }, new[] { new PanelBranch("{0}", 1), new PanelBranch("{1}", 1), new PanelBranch("{2}", 0), new PanelBranch("{3}", 0) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions {
            CreateSet = true, DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength });
        var set = result.DisplaySet!;
        Assert.Equal(0, set.BranchIndices["{0}"]);
        Assert.Equal(1, set.BranchIndices["{2}"]);
        Assert.Equal(new[] { 0, 1 }, set.ExpandMembers(snapshot, result.RowPositions));
        Assert.Equal(new[] { "{2}", "{3}" }, set.BranchOccurrences["{2}"]);
        Assert.Empty(set.ExpandMembers(snapshot, Array.Empty<int>(), new[] { "{2}" }));
    }
}
