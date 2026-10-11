using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using BetterGhPanel.Core;

internal static class FilterQueryChecks
{
    internal static void Run()
    {
        NumericComparisons();
        GeometryMetricsAndUnits();
        MeasurementCandidates();
        RegularExpressions();
        InvalidExpressions();
    }

    private static void NumericComparisons()
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
        Check(clone.SearchMode == PanelSearchMode.Area && clone.Search == "> 2", "Cloned query retains its filter mode and expression");
        Check(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.Value }).RowPositions.Count == 7,
            "Selecting a mode without an expression leaves the view unrestricted");
    }

    private static void GeometryMetricsAndUnits()
    {
        var snapshot = Snapshot(
            Geometry(new PanelGeometryInfo("line", 2000, units: "mm")),
            Geometry(new PanelGeometryInfo("surface", 2000000, 2, "mm", "Area", "A")),
            Geometry(new PanelGeometryInfo("solid", 2000000000, 3, "mm", "Volume", "V")),
            Geometry(new PanelGeometryInfo("circle", 1, units: "mm", metricName: "Radius", metricLabel: "R")),
            Geometry(new PanelGeometryInfo("failed", double.NaN, 2, "mm", "Area", "A")),
            Geometry(new PanelGeometryInfo("negative", -1, 2, "mm", "Area", "A")),
            Number(2000000),
            Geometry(new PanelGeometryInfo("pending", power: 2, units: "mm", metricName: "Area", metricLabel: "A", canMeasure: true)),
            Geometry(new PanelGeometryInfo("other units", 2.01, 2, "cm", "Area", "A")),
            Geometry(new PanelGeometryInfo("invalid", 2000000, 2, "mm", "Area", "A"), valid: false));
        Positions(snapshot, PanelSearchMode.Length, "> 6", 0, 3);
        Positions(snapshot, PanelSearchMode.Length, "< 2");
        Positions(snapshot, PanelSearchMode.Area, "> 10", 1);
        Positions(snapshot, PanelSearchMode.Volume, "!= 0", 2);
        Positions(snapshot, PanelSearchMode.Value, "> 10", 6);

        var converted = new PanelSnapshot(snapshot.Rows, snapshot.Branches, PanelUnitConversion.SquareMillimetersToSquareMeters);
        Positions(converted, PanelSearchMode.Area, "= 2", 1);
        Positions(converted, PanelSearchMode.Area, "> 2.005", 8);
        Positions(converted, PanelSearchMode.Length, "> 6", 0, 3);
        var volumes = new PanelSnapshot(snapshot.Rows, snapshot.Branches, PanelUnitConversion.CubicMillimetersToCubicMeters);
        Positions(volumes, PanelSearchMode.Volume, "= 2", 2);
        var lengths = new PanelSnapshot(snapshot.Rows, snapshot.Branches, PanelUnitConversion.MillimetersToMeters);
        Positions(lengths, PanelSearchMode.Length, "> 0.006", 0, 3);
        Positions(lengths, PanelSearchMode.Length, "< 0.007", 3);

        var measurements = new Dictionary<int, PanelGeometryInfo>
        {
            [7] = new PanelGeometryInfo("measured", 3000000.001, 2, "mm", "Area", "A")
        };
        var result = PanelQueryEngine.Execute(converted, new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> 3" },
            measuredGeometry: measurements);
        Check(result.RowPositions.SequenceEqual(new[] { 7 }) && ReferenceEquals(result.Snapshot, converted),
            "Measured metrics are filtered at full precision without changing the snapshot identity");
        Check(!converted.Rows[7].GeometryInfo!.Value.HasValue, "Filtering never mutates geometry presentation records");
        Check(result.Statistics.NumericCount == 0 && result.Statistics.BranchCount == 1,
            "Geometry predicates preserve numeric statistics and branch counting semantics");
    }

    private static void MeasurementCandidates()
    {
        PanelGeometryInfo Pending(string metric, int power) => new PanelGeometryInfo(metric, power: power, metricName: metric, canMeasure: true);
        var snapshot = Snapshot(
            Geometry(Pending("Area", 2), "{0;1}"),
            Geometry(Pending("Area", 2), "{0;1;2}"),
            Geometry(Pending("Area", 2), "{0;10}"),
            Geometry(Pending("Length", 1), "{0;1}"),
            Geometry(Pending("Volume", 3), "{0;1}"),
            Geometry(new PanelGeometryInfo("analytic", 2, 2, metricName: "Area"), "{0;1}"),
            Geometry(Pending("Area", 2), "{0;1}", false));
        var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> 1", BranchPath = "{0;1}", BranchPrefix = true };
        Check(PanelQueryEngine.RequiresGeometryMeasurements(options), "Geometry expressions request measurements");
        Check(PanelQueryEngine.GeometryCandidates(snapshot, options).SequenceEqual(new[] { 0, 1 }),
            "Candidates include compatible descendants and exclude sibling paths, analytic values, wrong metrics and invalid geometry");
        options.TypeName = "Missing";
        Check(PanelQueryEngine.GeometryCandidates(snapshot, options).Count == 0, "Type restriction is applied before measuring");
        options.TypeName = null;
        options.OnlyInvalid = true;
        Check(PanelQueryEngine.GeometryCandidates(snapshot, options).Count == 0, "Invalid geometry is never measured");
        options.Search = " ";
        Check(!PanelQueryEngine.RequiresGeometryMeasurements(options) && PanelQueryEngine.GeometryCandidates(snapshot, options).Count == 0,
            "Blank expressions never start offscreen measurements");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => PanelQueryEngine.GeometryCandidates(snapshot, options, cancellation.Token));
    }

    private static void RegularExpressions()
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
        Check(restricted.RowPositions.SequenceEqual(new[] { 1 }), "Regex composes with branch and type filters");
        var hostile = Snapshot(new PanelRow(0, "{0}", "Text", new string('a', 50000) + "!", null));
        var watch = Stopwatch.StartNew();
        Throws<ArgumentException>(() => PanelQueryEngine.Execute(hostile,
            new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "^(a+)+$" }));
        Check(watch.Elapsed < TimeSpan.FromSeconds(5), "Pathological regex exits within a bounded match budget");
        // Each short input is cheap; a whole tree must still have a finite budget.
        var repeated = new PanelRow(0, "{0}", "Text", new string('a', 14) + "!", null);
        var aggregate = new PanelSnapshot(Enumerable.Repeat(repeated, 100000).ToArray(), new[] { new PanelBranch("{0}", 100000) });
        watch.Restart();
        Throws<ArgumentException>(() => PanelQueryEngine.Execute(aggregate,
            new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "^(a+)+$" }));
        Check(watch.Elapsed < TimeSpan.FromSeconds(5), "Repeated short regex matches share an overall query budget");
    }

    private static void InvalidExpressions()
    {
        foreach (string expression in new[] { "> nonsense", ">=", "NaN", "Infinity", "1e999", "2,5", "! 3" })
            Throws<ArgumentException>(() => PanelQueryEngine.Execute(PanelSnapshot.Empty,
                new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = expression }));
        Throws<ArgumentException>(() => PanelQueryEngine.GeometryCandidates(PanelSnapshot.Empty,
            new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> bad" }));
        Throws<ArgumentException>(() => PanelQueryEngine.Execute(PanelSnapshot.Empty,
            new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "[" }));
        Check(PanelQueryEngine.Execute(PanelSnapshot.Empty, new PanelQueryOptions { Search = "> nonsense" }).RowPositions.Count == 0,
            "Legacy text mode still treats malformed comparisons as literal text");
    }

    private static PanelRow Number(double value) => new PanelRow(0, "{0}", "Number", value.ToString(CultureInfo.InvariantCulture), value);
    private static PanelRow Geometry(PanelGeometryInfo info, string path = "{0}", bool valid = true) =>
        new PanelRow(0, path, "Geometry", info.Description, null, isValid: valid, geometryInfo: info);
    private static PanelSnapshot Snapshot(params PanelRow[] rows) => new PanelSnapshot(rows,
        rows.GroupBy(row => row.Path).Select(group => new PanelBranch(group.Key, group.Count())).ToArray());
    private static void Positions(PanelSnapshot snapshot, PanelSearchMode mode, string expression, params int[] positions) =>
        Check(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = mode, Search = expression }).RowPositions.SequenceEqual(positions),
            mode + " query '" + expression + "' matches expected rows");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
