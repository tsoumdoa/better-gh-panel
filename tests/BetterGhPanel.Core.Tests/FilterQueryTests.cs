using Xunit;
using System;
using System.Globalization;
using System.Linq;
using BetterGhPanel.Core;

public class FilterQueryTests
{

    [Fact]
    public void SharedQueryScope()
    {
        var geometry = new PanelGeometryInfo("shape", units: "mm", metricName: "Length", canMeasure: true);
        var rows = new[]
        {
            new PanelRow(0, "{0;1}", "Curve", "shape", null, geometryInfo: geometry),
            new PanelRow(1, "{0;1}", "Surface", "shape", null, geometryInfo: geometry),
            new PanelRow(0, "{0;1;2}", "Curve", "shape", null, geometryInfo: geometry),
            new PanelRow(0, "{0;10}", "Curve", "shape", null, geometryInfo: geometry),
            new PanelRow(2, "{0;1}", "Curve", "shape", null, isValid: false, geometryInfo: geometry),
            new PanelRow(0, "{7}", "Curve", "shape", null, geometryInfo: geometry)
        };
        var snapshot = new PanelSnapshot(rows, new[]
        {
            new PanelBranch("{0;1}", 3), new PanelBranch("{0;1;2}", 1),
            new PanelBranch("{0;10}", 1), new PanelBranch("{9}", 0)
        });
        var query = new PanelQueryOptions { BranchPath = "0;1", BranchPrefix = true, TypeName = "curve",
            SortMode = PanelSortMode.ValueAscending };
        Verify(new[] { 0, 2, 4 }, new[] { 0, 2 });
        query.BranchPrefix = false;
        Verify(new[] { 0, 4 }, new[] { 0 });
        query.OnlyInvalid = true;
        Verify(new[] { 4 }, Array.Empty<int>());
        query.OnlyInvalid = false;
        query.BranchPath = null;
        query.SearchMode = PanelSearchMode.ListLength;
        query.Search = ">= 2";
        Verify(new[] { 0, 4 }, new[] { 0 });
        query.Search = "= 0";
        query.TypeName = null;
        Verify(Array.Empty<int>(), Array.Empty<int>());
        Assert.True(PanelQueryEngine.Execute(snapshot, query).BranchPaths.SequenceEqual(new[] { "{9}" }),
            "A branch-only predicate retains matching empty branches");
        query.Search = "";
        query.BranchPath = "7";
        Verify(new[] { 5 }, new[] { 5 });
        query.Search = ">= 0";
        Verify(Array.Empty<int>(), Array.Empty<int>());

        void Verify(int[] expectedRows, int[] expectedMeasurements)
        {
            // Compare explicit memberships, independent of the final display ordering.
            Assert.True(PanelQueryEngine.Execute(snapshot, query).RowPositions.OrderBy(position => position).SequenceEqual(expectedRows),
                "Final queries preserve branch, type, invalid-only and list-length scope");
            Assert.True(PanelQueryEngine.GeometryCandidates(snapshot, query).SequenceEqual(expectedMeasurements),
                "Measurement preparation shares query scope and never measures invalid or excluded rows");
        }
    }

    [Fact]
    public void BranchListLengths()
    {
        var snapshot = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0;1}", "Number", "1", 1),
            new PanelRow(1, "{0;1}", "Text", "x", null),
            new PanelRow(2, "{0;1}", "Number", "invalid", -1, isValid: false),
            new PanelRow(0, "{0;1;2}", "Number", "2", 2),
            new PanelRow(0, "{0;10}", "Number", "3", 3),
            new PanelRow(1, "{0;10}", "Number", "4", 4)
        }, new[]
        {
            new PanelBranch("{0;1}", 3), new PanelBranch("{0;1;2}", 1),
            new PanelBranch("{0;10}", 2), new PanelBranch("{0;1;3}", 0), new PanelBranch("{9}", 0)
        });
        Positions(snapshot, PanelSearchMode.ListLength, "> 2", 0, 1, 2);
        Positions(snapshot, PanelSearchMode.ListLength, ">= 2", 0, 1, 2, 4, 5);
        Positions(snapshot, PanelSearchMode.ListLength, "< 2", 3);
        Positions(snapshot, PanelSearchMode.ListLength, "<= 2", 3, 4, 5);
        Positions(snapshot, PanelSearchMode.ListLength, "= 2", 4, 5);
        Positions(snapshot, PanelSearchMode.ListLength, "== 2", 4, 5);
        Positions(snapshot, PanelSearchMode.ListLength, "!= 2", 0, 1, 2, 3);
        Positions(snapshot, PanelSearchMode.ListLength, "2", 4, 5);
        Positions(snapshot, PanelSearchMode.ListLength, "= 0");
        Positions(snapshot, PanelSearchMode.ListLength, "> 1.5", 0, 1, 2, 4, 5);
        Positions(snapshot, PanelSearchMode.ListLength, "= 1.5");

        var query = new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength, Search = "0" };
        var empty = PanelQueryEngine.Execute(snapshot, query);
        Assert.True(empty.BranchPaths.SequenceEqual(new[] { "{0;1;3}", "{9}" }) &&
            empty.Statistics.BranchCount == 2 && empty.Statistics.ItemCount == 0 &&
            empty.Statistics.BranchItemCounts.Count == 2 && empty.Statistics.BranchItemCounts.Values.All(count => count == 0),
            "Zero-length filter retains only existing empty branches in result paths and statistics");
        query.BranchPath = "0;1";
        query.BranchPrefix = true;
        Assert.True(PanelQueryEngine.Execute(snapshot, query).BranchPaths.SequenceEqual(new[] { "{0;1;3}" }),
            "Zero-length branch filters compose with descendant selection");
        query.Search = ">= 1";
        query.TypeName = "Number";
        var numbers = PanelQueryEngine.Execute(snapshot, query);
        Assert.True(numbers.RowPositions.SequenceEqual(new[] { 0, 2, 3 }) && numbers.Statistics.BranchItemCounts["{0;1}"] == 2,
            "Branch lengths use pre-item-filter counts, while statistics describe retained items");
        query.Search = "= 3";
        query.OnlyInvalid = true;
        Assert.True(PanelQueryEngine.Execute(snapshot, query).RowPositions.SequenceEqual(new[] { 2 }),
            "Branch length is independent of invalid-only and type restrictions");
        query.Search = "= 0";
        Assert.True(PanelQueryEngine.Execute(snapshot, query).BranchPaths.Count == 0,
            "Item restrictions suppress empty branches because they contain no matching items");
        query = new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength };
        Assert.True(PanelQueryEngine.Execute(snapshot, query).BranchPaths.Count == 5 &&
            PanelQueryEngine.Execute(snapshot, query).RowPositions.Count == 6,
            "Selecting list length without an expression preserves all branches and items");
        Assert.True(!PanelQueryEngine.RequiresGeometryMeasurements(query) &&
            PanelQueryEngine.GeometryCandidates(snapshot, query).Count == 0,
            "Branch length never requires geometry measurements");
    }

    [Fact]
    public void BranchPaths()
    {
        var snapshot = Snapshot(
            new PanelRow(0, "{0;0;1}", "Number", "1", 1),
            new PanelRow(0, "{0;0;1;2}", "Number", "2", 2),
            new PanelRow(0, "{0;0;10}", "Number", "3", 3));
        foreach (string path in new[] { "{0;0;1}", "0;0;1", " 0 ; 0 ; 01 ", " { 0 ; 0 ; 1 } " })
        {
            var query = new PanelQueryOptions { BranchPath = path };
            Assert.True(PanelQueryEngine.NormalizeBranchPath(path) == "{0;0;1}" &&
                PanelQueryEngine.Execute(snapshot, query).RowPositions.SequenceEqual(new[] { 0 }),
                "Bare and braced branch paths select the same canonical branch");
            query.BranchPrefix = true;
            Assert.True(PanelQueryEngine.Execute(snapshot, query).RowPositions.SequenceEqual(new[] { 0, 1 }),
                "Prefix matching selects descendants without selecting similar sibling paths");
        }
        Assert.True(PanelQueryEngine.NormalizeBranchPath(null) == null && PanelQueryEngine.NormalizeBranchPath(" \t ") == null,
            "Only absent or blank paths clear branch selection");
        foreach (string malformed in new[] { "{0;1", "0;1}", "{}", "0;;1", "0,1", "0;-1", "-1", "2147483648", "{{0;1}}", "hello" })
            Assert.ThrowsAny<ArgumentException>(() => PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { BranchPath = malformed }));
        var measured = Snapshot(Geometry(new PanelGeometryInfo("pending", power: 2, metricName: "Area", canMeasure: true), "{0;0;1}"));
        Assert.True(PanelQueryEngine.GeometryCandidates(measured,
            new PanelQueryOptions { BranchPath = "0;0;1", SearchMode = PanelSearchMode.Area, Search = "> 0" }).SequenceEqual(new[] { 0 }),
            "Geometry candidate discovery uses the same normalized branch selection");
    }

    [Fact]
    public void NumericComparisons()
    {
        var snapshot = Snapshot(
            Number(1), Number(2), Number(3), Number(double.NaN), Number(double.PositiveInfinity),
            new PanelRow(5, "{0}", "Text", "2", null), new PanelRow(6, "{0}", "Null", "<null>", 2, true));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Positions(snapshot, PanelSearchMode.Value, "> 2", 2);
            Positions(snapshot, PanelSearchMode.Value, ">= 2", 1, 2);
            Positions(snapshot, PanelSearchMode.Value, "< 2", 0);
            Positions(snapshot, PanelSearchMode.Value, "<= 2", 0, 1);
            Positions(snapshot, PanelSearchMode.Value, "= 2", 1);
            Positions(snapshot, PanelSearchMode.Value, "== 2", 1);
            Positions(snapshot, PanelSearchMode.Value, "!= 2", 0, 2);
            Positions(snapshot, PanelSearchMode.Value, "2e0", 1);
            Positions(snapshot, PanelSearchMode.Value, "> 1.5", 1, 2);
            Positions(snapshot, PanelSearchMode.Text, "> 2", 2);
            Positions(snapshot, PanelSearchMode.Text, "2", 1, 5);
        }
        finally { CultureInfo.CurrentCulture = previous; }
        var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> 2" };
        var clone = options.Clone();
        options.SearchMode = PanelSearchMode.Text;
        Assert.True(clone.SearchMode == PanelSearchMode.Area && clone.Search == "> 2", "Cloned query retains its filter mode and expression");
        Assert.True(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.Value }).RowPositions.Count == 7,
            "Selecting a mode without an expression leaves the view unrestricted");
    }

    [Fact]
    public void RegularExpressions()
    {
        var snapshot = Snapshot(
            new PanelRow(0, "{0}", "Text", "Door-01", null),
            new PanelRow(1, "{1}", "Text", "door-12", null),
            new PanelRow(2, "{2}", "Text", "Door-X", null),
            new PanelRow(3, "{3}", "Text", " a ", null),
            new PanelRow(4, "{4}", "Text", "I", null));
        Positions(snapshot, PanelSearchMode.Regex, @"^door-\d{2}$", 0, 1);
        Positions(snapshot, PanelSearchMode.Regex, "Text");
        Positions(snapshot, PanelSearchMode.Regex, @"\{0\}");
        Positions(snapshot, PanelSearchMode.Regex, " a ", 3);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Positions(snapshot, PanelSearchMode.Regex, "^i$", 4);
        }
        finally { CultureInfo.CurrentCulture = previous; }
        var restricted = PanelQueryEngine.Execute(snapshot,
            new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = @"^door-\d{2}$", BranchPath = "{1}", TypeName = "Text" });
        Assert.True(restricted.RowPositions.SequenceEqual(new[] { 1 }), "Regex composes with branch and type filters");
        var hostile = Snapshot(new PanelRow(0, "{0}", "Text", new string('a', 50000) + "!", null));
        Assert.ThrowsAny<ArgumentException>(() => PanelQueryEngine.Execute(hostile,
            new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "^(a+)+$" }));
        // Each short input is cheap; a whole tree must still have a finite budget.
        var repeated = new PanelRow(0, "{0}", "Text", new string('a', 14) + "!", null);
        var aggregate = new PanelSnapshot(Enumerable.Repeat(repeated, 100000).ToArray(), new[] { new PanelBranch("{0}", 100000) });
        Assert.ThrowsAny<ArgumentException>(() => PanelQueryEngine.Execute(aggregate,
            new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "^(a+)+$" }));
    }

    [Fact]
    public void InvalidExpressions()
    {
        foreach (string expression in new[] { "> nonsense", ">=", "NaN", "Infinity", "1e999", "2,5", "! 3" })
        {
            Assert.ThrowsAny<ArgumentException>(() => PanelQueryEngine.Execute(PanelSnapshot.Empty,
                new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = expression }));
            Assert.ThrowsAny<ArgumentException>(() => PanelQueryEngine.Execute(PanelSnapshot.Empty,
                new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength, Search = expression }));
        }
        Assert.ThrowsAny<ArgumentException>(() => PanelQueryEngine.GeometryCandidates(PanelSnapshot.Empty,
            new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> bad" }));
        Assert.ThrowsAny<ArgumentException>(() => PanelQueryEngine.Execute(PanelSnapshot.Empty,
            new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "[" }));
        Assert.True(PanelQueryEngine.Execute(PanelSnapshot.Empty, new PanelQueryOptions { Search = "> nonsense" }).RowPositions.Count == 0,
            "Legacy text mode still treats malformed comparisons as literal text");
    }

    private static PanelRow Number(double value) => new PanelRow(0, "{0}", "Number", value.ToString(CultureInfo.InvariantCulture), value);
    private static PanelRow Geometry(PanelGeometryInfo info, string path = "{0}", bool valid = true) =>
        new PanelRow(0, path, "Geometry", info.Description, null, isValid: valid, geometryInfo: info);
    private static PanelSnapshot Snapshot(params PanelRow[] rows) => new PanelSnapshot(rows,
        rows.GroupBy(row => row.Path).Select(group => new PanelBranch(group.Key, group.Count())).ToArray());
    private static void Positions(PanelSnapshot snapshot, PanelSearchMode mode, string expression, params int[] positions) =>
        Assert.True(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = mode, Search = expression }).RowPositions.SequenceEqual(positions),
            mode + " query '" + expression + "' matches expected rows");

}
