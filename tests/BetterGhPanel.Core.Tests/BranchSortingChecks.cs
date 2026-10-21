using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BetterGhPanel.Core;

internal static class BranchSortingChecks
{
    internal static void Run()
    {
        TreeOrderAndStableTies();
        MixedTypesAndFilters();
        GeometryPresentation();
        MissingBranchMetadata();
        UnevenBranchSizes();
    }

    private static void TreeOrderAndStableTies()
    {
        var snapshot = new PanelSnapshot(new[]
        {
            Number("{2}", 5, 19), Number("{10}", 100, 8), Number("{2}", 1, 3),
            Number("{2;1}", 7), Number("{10}", 0), Number("{2}", 1, 1)
        }, new[]
        {
            new PanelBranch("{2}", 3), new PanelBranch("{2;1}", 1),
            new PanelBranch("{3}", 0), new PanelBranch("{10}", 2)
        });
        var ascending = Query(snapshot, PanelSortMode.ValueAscending);
        var descending = Query(snapshot, PanelSortMode.ValueDescending);
        Positions(ascending, 2, 5, 0, 3, 4, 1);
        Positions(descending, 0, 2, 5, 3, 1, 4);
        Check(ascending.BranchPaths.Contains("{3}") && descending.BranchPaths.Contains("{3}") &&
            ascending.Statistics.BranchCount == 4 && ascending.Statistics.BranchItemCounts["{3}"] == 0,
            "Sorting retains empty branches and their statistics");
        Check(ReferenceEquals(ascending.Snapshot, snapshot) && snapshot.Rows[2].OriginalIndex == 3 &&
            snapshot.Rows[5].OriginalIndex == 1, "Sorting retains the original tree addresses and snapshot");
        Positions(Query(snapshot, PanelSortMode.SourceOrder), 0, 1, 2, 3, 4, 5);
        Positions(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions
        {
            SortMode = PanelSortMode.ValueDescending, BranchPath = "2", BranchPrefix = true
        }), 0, 2, 5, 3);
        Positions(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions
        {
            SortMode = PanelSortMode.ValueAscending, SearchMode = PanelSearchMode.Value, Search = ">= 5"
        }), 0, 3, 1);
        Positions(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions
        {
            SortMode = PanelSortMode.ValueDescending, SearchMode = PanelSearchMode.ListLength, Search = ">= 2"
        }), 0, 2, 5, 1, 4);
        var empty = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions
        {
            SortMode = PanelSortMode.ValueDescending, SearchMode = PanelSearchMode.ListLength, Search = "= 0"
        });
        Positions(empty);
        Check(empty.BranchPaths.SequenceEqual(new[] { "{3}" }), "Sorting empty-only results keeps matching branch metadata");
    }

    private static void MixedTypesAndFilters()
    {
        var snapshot = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{10}", "Null", "<null>", null, true, false),
            new PanelRow(0, "{2}", "Text", "z", null),
            Number("{10}", 9),
            new PanelRow(1, "{2}", "Number", "-5", -5, isValid: false),
            Number("{2}", 5),
            new PanelRow(2, "{10}", "Text", "a", null),
            new PanelRow(3, "{2}", "Text", "A", null),
            new PanelRow(4, "{2}", "Null", "<null>", null, true, false),
            new PanelRow(3, "{10}", "Number", "2", 2, isValid: false),
            Number("{10}", 1)
        }, new[] { new PanelBranch("{2}", 5), new PanelBranch("{10}", 5) });
        Positions(Query(snapshot, PanelSortMode.ValueAscending), 4, 3, 6, 1, 7, 9, 2, 8, 5, 0);
        Positions(Query(snapshot, PanelSortMode.ValueDescending), 4, 3, 1, 6, 7, 2, 9, 8, 5, 0);
        Positions(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions
        {
            SortMode = PanelSortMode.ValueAscending, OnlyInvalid = true
        }), 3, 7, 8, 0);
        Positions(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions
        {
            SortMode = PanelSortMode.ValueDescending, TypeName = "Text"
        }), 1, 6, 5);
    }

    private static void GeometryPresentation()
    {
        PanelRow Geometry(string path, double? value) => new PanelRow(0, path, "Geometry", "Captured", null,
            geometryInfo: new PanelGeometryInfo(value.HasValue ? "Line" : "Pending", value, units: "mm", canMeasure: !value.HasValue));
        var snapshot = new PanelSnapshot(new[]
        {
            Geometry("{10}", 1000), Geometry("{2}", 500), Geometry("{10}", null), Geometry("{2}", null)
        }, new[] { new PanelBranch("{2}", 2), new PanelBranch("{10}", 2) }, PanelUnitConversion.MillimetersToMeters);
        var measured = new Dictionary<int, PanelGeometryInfo>
        {
            [2] = new PanelGeometryInfo("Line", 100, units: "mm"),
            [3] = new PanelGeometryInfo("Line", 200, units: "mm")
        };
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            foreach (var direction in new[] { PanelSortMode.ValueAscending, PanelSortMode.ValueDescending })
            {
                var options = new PanelQueryOptions { SortMode = direction, DecimalPlaces = 2 };
                var result = PanelQueryEngine.Execute(snapshot, options, measuredGeometry: measured);
                Positions(result, direction == PanelSortMode.ValueAscending ? new[] { 3, 1, 2, 0 } : new[] { 1, 3, 0, 2 });
                options.DecimalPlaces = 0;
                result = PanelQueryEngine.Execute(snapshot, options, measuredGeometry: measured);
                Check(result.RowPositions.Take(2).SequenceEqual(new[] { 1, 3 }),
                    "Equal formatted geometry keys keep source order within their branch in both directions");
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void MissingBranchMetadata()
    {
        var snapshot = new PanelSnapshot(new[]
        {
            Number("{20}", 9), Number("{10}", 4), Number("{2}", 8), Number("{20}", 1), Number("{10}", 2)
        }, new[] { new PanelBranch("{2}", 1), new PanelBranch("{2}", 1), new PanelBranch("{3}", 0) });
        Positions(Query(snapshot, PanelSortMode.ValueAscending), 2, 3, 0, 4, 1);
        Positions(Query(snapshot, PanelSortMode.ValueDescending), 2, 0, 3, 1, 4);
    }

    private static void UnevenBranchSizes()
    {
        var branches = new[] { 0, 1, 2, 3, 7, 17, 257 }.Select((count, index) =>
            new PanelBranch("{" + index + "}", count)).Reverse().ToArray();
        var rows = branches.SelectMany(branch => Enumerable.Range(0, branch.ItemCount).Select(index =>
            Number(branch.Path, index * 7 % 11, index))).Reverse().ToArray();
        var snapshot = new PanelSnapshot(rows, branches);
        foreach (var direction in new[] { PanelSortMode.ValueAscending, PanelSortMode.ValueDescending })
        {
            var expected = branches.SelectMany(branch =>
            {
                var positions = Enumerable.Range(0, rows.Length).Where(index => rows[index].Path == branch.Path);
                return direction == PanelSortMode.ValueAscending
                    ? positions.OrderBy(index => rows[index].NumericValue)
                    : positions.OrderByDescending(index => rows[index].NumericValue);
            });
            Check(Query(snapshot, direction).RowPositions.SequenceEqual(expected),
                "Uneven branch ranges sort independently without disturbing neighboring ranges or stable ties");
        }
    }

    private static PanelRow Number(string path, double value, int originalIndex = 0) =>
        new PanelRow(originalIndex, path, "Number", value.ToString(CultureInfo.InvariantCulture), value);
    private static PanelQueryResult Query(PanelSnapshot snapshot, PanelSortMode direction) =>
        PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = direction });
    private static void Positions(PanelQueryResult result, params int[] expected) =>
        Check(result.RowPositions.SequenceEqual(expected), "Expected rows " + string.Join(",", expected) +
            "; got " + string.Join(",", result.RowPositions));
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
