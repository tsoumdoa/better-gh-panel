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
        InvalidNumbers();
        BranchListLengths();
        BranchPaths();
        DisplayedTextAndPrecision();
        FailedMeasurementPresentation();
        DisplayedSorting();
        GeometryMetricsAndUnits();
        MeasurementCandidates();
        SharedQueryScope();
        RegularExpressions();
        InvalidExpressions();
    }

    private static void SharedQueryScope()
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
        Check(PanelQueryEngine.Execute(snapshot, query).BranchPaths.SequenceEqual(new[] { "{9}" }),
            "A branch-only predicate retains matching empty branches");
        query.Search = "";
        query.BranchPath = "7";
        Verify(new[] { 5 }, new[] { 5 });
        query.Search = ">= 0";
        Verify(Array.Empty<int>(), Array.Empty<int>());

        void Verify(int[] expectedRows, int[] expectedMeasurements)
        {
            // Compare explicit memberships, independent of the final display ordering.
            Check(PanelQueryEngine.Execute(snapshot, query).RowPositions.OrderBy(position => position).SequenceEqual(expectedRows),
                "Final queries preserve branch, type, invalid-only and list-length scope");
            Check(PanelQueryEngine.GeometryCandidates(snapshot, query).SequenceEqual(expectedMeasurements),
                "Measurement preparation shares query scope and never measures invalid or excluded rows");
        }
    }

    private static void FailedMeasurementPresentation()
    {
        var pending = new PanelGeometryInfo("Pending surface", power: 2, units: "mm",
            metricName: "Area", metricLabel: "A", canMeasure: true, details: "Surface details");
        var snapshot = Snapshot(Geometry(pending), Number(2));
        var failed = pending.WithUnavailableMeasurement();
        Check(failed.MeasurementUnavailable && !failed.CanMeasure && failed.Value == null &&
            failed.Description == pending.Description && failed.Details == pending.Details && failed.Power == pending.Power &&
            failed.Units == pending.Units && failed.MetricName == pending.MetricName && failed.MetricLabel == pending.MetricLabel,
            "Failed-measurement metadata retains captured presentation without a stale metric or pending state");
        var measurements = new Dictionary<int, PanelGeometryInfo> { [0] = failed };
        var query = new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "^Pending surface · measurement unavailable$" };
        Check(PanelQueryEngine.Execute(snapshot, query, measuredGeometry: measurements).RowPositions.SequenceEqual(new[] { 0 }) &&
            PanelValuePresentation.FormatDisplay(snapshot.Rows[0], PanelUnitConversion.None, -1, failed) ==
                "Pending surface · measurement unavailable",
            "Regex and the canvas share persistent failed-measurement presentation");
        query.SearchMode = PanelSearchMode.Text;
        query.Search = "measurement unavailable";
        Check(PanelQueryEngine.Execute(snapshot, query, measuredGeometry: measurements).RowPositions.SequenceEqual(new[] { 0 }),
            "Persistent unavailable status is discoverable by substring search");
        query.SearchMode = PanelSearchMode.Area;
        query.Search = ">= 0";
        Check(PanelQueryEngine.Execute(snapshot, query, measuredGeometry: measurements).RowPositions.Count == 0,
            "A failed measurement cannot satisfy a metric comparison");
        Check(snapshot.HasMeasurableGeometry && !Snapshot(Number(2)).HasMeasurableGeometry &&
            !Snapshot(Geometry(new PanelGeometryInfo("Analytic", 2))).HasMeasurableGeometry &&
            !Snapshot(Geometry(pending, valid: false)).HasMeasurableGeometry &&
            !PanelSnapshot.Empty.HasMeasurableGeometry,
            "Snapshot metadata identifies deferred geometry without forcing scalar, analytic, or invalid-only queries onto workers");
    }

    private static void DisplayedSorting()
    {
        var snapshot = new PanelSnapshot(new[]
        {
            Geometry(new PanelGeometryInfo("Line", 1000, units: "mm")),
            Geometry(new PanelGeometryInfo("Line", 200, units: "mm")),
            Geometry(new PanelGeometryInfo("Placeholder", units: "mm", canMeasure: true))
        }, new[] { new PanelBranch("{0}", 3) }, PanelUnitConversion.MillimetersToMeters);
        var measured = new Dictionary<int, PanelGeometryInfo> { [2] = new PanelGeometryInfo("Line", 100, units: "mm") };
        var query = new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending };
        Check(PanelQueryEngine.RequiresGeometryMeasurements(query) &&
            PanelQueryEngine.GeometryCandidates(snapshot, query).SequenceEqual(new[] { 2 }),
            "Sorting without a search measures offscreen geometry before display-text order is fixed");
        Check(PanelQueryEngine.Execute(snapshot, query, measuredGeometry: measured).RowPositions.SequenceEqual(new[] { 2, 1, 0 }),
            "Nonnumeric rows sort by measured, converted displayed text instead of raw captured summaries");
        query.SortMode = PanelSortMode.ValueDescending;
        Check(PanelQueryEngine.Execute(snapshot, query, measuredGeometry: measured).RowPositions.SequenceEqual(new[] { 0, 1, 2 }),
            "Descending display-text ordering uses the same converted geometry keys");
        query.SearchMode = PanelSearchMode.Value;
        query.Search = "> 0";
        Check(PanelQueryEngine.GeometryCandidates(snapshot, query).Count == 0,
            "Scalar comparisons reject nonnumeric geometry before any sorting measurement");
        var mixed = Snapshot(Number(4), Geometry(new PanelGeometryInfo("Expensive surface", power: 2,
            metricName: "Area", canMeasure: true)), Geometry(new PanelGeometryInfo("Expensive curve", canMeasure: true)));
        Check(!PanelQueryEngine.RequiresGeometryMeasurements(query) && PanelQueryEngine.GeometryCandidates(mixed, query).Count == 0 &&
            PanelQueryEngine.Execute(mixed, query).RowPositions.SequenceEqual(new[] { 0 }),
            "Sorted scalar filters over mixed input do not measure geometry that cannot be returned");
        query.SearchMode = PanelSearchMode.Text;
        Check(!PanelQueryEngine.RequiresGeometryMeasurements(query) && PanelQueryEngine.GeometryCandidates(mixed, query).Count == 0,
            "Sorted legacy scalar comparisons in Text mode also exclude geometry candidates");
        query.SearchMode = PanelSearchMode.Area;
        Check(PanelQueryEngine.GeometryCandidates(mixed, query).SequenceEqual(new[] { 1 }),
            "Sorting an Area-filtered result does not measure excluded Length geometry");
        query.SearchMode = PanelSearchMode.ListLength;
        query.Search = "= 0";
        Check(PanelQueryEngine.GeometryCandidates(snapshot, query).Count == 0,
            "Branch-count restrictions exclude impossible sorting candidates before measuring");
    }

    private static void DisplayedTextAndPrecision()
    {
        // Scalar values have already been converted by the runtime before snapshot capture.
        var numeric = new PanelSnapshot(new[] { Number(1.234), Number(2.345) },
            new[] { new PanelBranch("{0}", 2) }, PanelUnitConversion.MillimetersToMeters);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var query = new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "^1,23 m$", DecimalPlaces = 2 };
            Check(PanelQueryEngine.Execute(numeric, query).RowPositions.SequenceEqual(new[] { 0 }),
                "Regex uses converted scalar units and the same decimals and culture as displayed text");
            Check(PanelValuePresentation.FormatDisplay(numeric.Rows[0], numeric.UnitConversion, 2) == "1,23 m",
                "Shared scalar presentation formats converted snapshot values without converting twice");
            using var work = PanelQueryEngine.Start(numeric, query);
            query.DecimalPlaces = 0;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Check(work.Complete().RowPositions.SequenceEqual(new[] { 0 }),
                "Deferred queries retain captured formatting options and culture");
            query = new PanelQueryOptions { Search = "1.23 m", DecimalPlaces = 2 };
            Check(PanelQueryEngine.Execute(numeric, query).RowPositions.SequenceEqual(new[] { 0 }),
                "Text substring search uses the same fixed-decimal presentation as regex");
            query.SearchMode = PanelSearchMode.Value;
            query.Search = "> 1.23";
            Check(PanelQueryEngine.Execute(numeric, query).RowPositions.SequenceEqual(new[] { 0, 1 }),
                "Numeric comparisons retain full converted precision even when display rounds");
            query.Search = "= 1.23";
            Check(PanelQueryEngine.Execute(numeric, query).RowPositions.Count == 0,
                "Numeric equality never compares a rounded display string");
            query.SearchMode = PanelSearchMode.Text;
            query.Search = "> 1.23";
            Check(PanelQueryEngine.Execute(numeric, query).RowPositions.SequenceEqual(new[] { 0, 1 }) &&
                !PanelQueryEngine.RequiresGeometryMeasurements(query) && !PanelQueryEngine.UsesDisplayedText(query),
                "Legacy numeric comparisons in Text mode retain exact numeric semantics and skip geometry work");
        }
        finally { CultureInfo.CurrentCulture = previous; }

        var geometries = new PanelSnapshot(new[]
        {
            Geometry(new PanelGeometryInfo("Curve", 1234.5, units: "mm")),
            Geometry(new PanelGeometryInfo("Pending curve", units: "mm", canMeasure: true)),
            new PanelRow(2, "{0}", "Number", "unavailable", -1, isValid: false)
        }, new[] { new PanelBranch("{0}", 3) }, PanelUnitConversion.MillimetersToMeters);
        var measurements = new Dictionary<int, PanelGeometryInfo>
        {
            [1] = new PanelGeometryInfo("Measured curve", 1234.5, units: "mm")
        };
        var textQuery = new PanelQueryOptions { Search = "L 1.23 m", DecimalPlaces = 2 };
        Check(PanelQueryEngine.Execute(geometries, textQuery, measuredGeometry: measurements).RowPositions.SequenceEqual(new[] { 0, 1 }),
            "Text search uses both analytic and measured geometry metrics in displayed units and precision");
        textQuery.SearchMode = PanelSearchMode.Regex;
        textQuery.Search = "^Measured curve · L 1\\.23 m$";
        Check(PanelQueryEngine.Execute(geometries, textQuery, measuredGeometry: measurements).RowPositions.SequenceEqual(new[] { 1 }),
            "Regex uses the measured compact description rather than the captured placeholder");
        textQuery.Search = "^Invalid · unavailable$";
        Check(PanelQueryEngine.Execute(geometries, textQuery).RowPositions.SequenceEqual(new[] { 2 }),
            "Regex sees the same invalid marker displayed on the canvas");
        Check(PanelValuePresentation.FormatDisplay(geometries.Rows[0], geometries.UnitConversion, 2) == "Curve · L 1.23 m",
            "Shared geometry presentation retains the unit-aware formatter");
    }

    private static void InvalidNumbers()
    {
        var snapshot = Snapshot(Number(2), new PanelRow(1, "{0}", "Number", "invalid", -1.234321e308, isValid: false));
        Positions(snapshot, PanelSearchMode.Value, "< 0");
        Positions(snapshot, PanelSearchMode.Value, "!= 2");
        Positions(snapshot, PanelSearchMode.Text, "< 0");
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Check(result.Statistics.NumericCount == 1 && result.Statistics.Minimum == 2 &&
            result.Statistics.Maximum == 2 && result.Statistics.Mean == 2 && result.Statistics.InvalidCount == 1,
            "Invalid finite sentinels remain browsable but do not contaminate numeric filters or statistics");
    }

    private static void BranchListLengths()
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
        Check(empty.BranchPaths.SequenceEqual(new[] { "{0;1;3}", "{9}" }) &&
            empty.Statistics.BranchCount == 2 && empty.Statistics.ItemCount == 0 &&
            empty.Statistics.BranchItemCounts.Count == 2 && empty.Statistics.BranchItemCounts.Values.All(count => count == 0),
            "Zero-length filter retains only existing empty branches in result paths and statistics");
        query.BranchPath = "0;1";
        query.BranchPrefix = true;
        Check(PanelQueryEngine.Execute(snapshot, query).BranchPaths.SequenceEqual(new[] { "{0;1;3}" }),
            "Zero-length branch filters compose with descendant selection");
        query.Search = ">= 1";
        query.TypeName = "Number";
        var numbers = PanelQueryEngine.Execute(snapshot, query);
        Check(numbers.RowPositions.SequenceEqual(new[] { 0, 2, 3 }) && numbers.Statistics.BranchItemCounts["{0;1}"] == 2,
            "Branch lengths use pre-item-filter counts, while statistics describe retained items");
        query.Search = "= 3";
        query.OnlyInvalid = true;
        Check(PanelQueryEngine.Execute(snapshot, query).RowPositions.SequenceEqual(new[] { 2 }),
            "Branch length is independent of invalid-only and type restrictions");
        query.Search = "= 0";
        Check(PanelQueryEngine.Execute(snapshot, query).BranchPaths.Count == 0,
            "Item restrictions suppress empty branches because they contain no matching items");
        query = new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength };
        Check(PanelQueryEngine.Execute(snapshot, query).BranchPaths.Count == 5 &&
            PanelQueryEngine.Execute(snapshot, query).RowPositions.Count == 6,
            "Selecting list length without an expression preserves all branches and items");
        Check(!PanelQueryEngine.RequiresGeometryMeasurements(query) &&
            PanelQueryEngine.GeometryCandidates(snapshot, query).Count == 0,
            "Branch length never requires geometry measurements");
    }

    private static void BranchPaths()
    {
        var snapshot = Snapshot(
            new PanelRow(0, "{0;0;1}", "Number", "1", 1),
            new PanelRow(0, "{0;0;1;2}", "Number", "2", 2),
            new PanelRow(0, "{0;0;10}", "Number", "3", 3));
        foreach (string path in new[] { "{0;0;1}", "0;0;1", " 0 ; 0 ; 01 ", " { 0 ; 0 ; 1 } " })
        {
            var query = new PanelQueryOptions { BranchPath = path };
            Check(PanelQueryEngine.NormalizeBranchPath(path) == "{0;0;1}" &&
                PanelQueryEngine.Execute(snapshot, query).RowPositions.SequenceEqual(new[] { 0 }),
                "Bare and braced branch paths select the same canonical branch");
            query.BranchPrefix = true;
            Check(PanelQueryEngine.Execute(snapshot, query).RowPositions.SequenceEqual(new[] { 0, 1 }),
                "Prefix matching selects descendants without selecting similar sibling paths");
        }
        Check(PanelQueryEngine.NormalizeBranchPath(null) == null && PanelQueryEngine.NormalizeBranchPath(" \t ") == null,
            "Only absent or blank paths clear branch selection");
        foreach (string malformed in new[] { "{0;1", "0;1}", "{}", "0;;1", "0,1", "0;-1", "-1", "2147483648", "{{0;1}}", "hello" })
            Throws<ArgumentException>(() => PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { BranchPath = malformed }));
        var measured = Snapshot(Geometry(new PanelGeometryInfo("pending", power: 2, metricName: "Area", canMeasure: true), "{0;0;1}"));
        Check(PanelQueryEngine.GeometryCandidates(measured,
            new PanelQueryOptions { BranchPath = "0;0;1", SearchMode = PanelSearchMode.Area, Search = "> 0" }).SequenceEqual(new[] { 0 }),
            "Geometry candidate discovery uses the same normalized branch selection");
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
        options.OnlyInvalid = false;
        options.SearchMode = PanelSearchMode.Text;
        options.Search = "Area";
        Check(PanelQueryEngine.RequiresGeometryMeasurements(options) && PanelQueryEngine.UsesDisplayedText(options) &&
            PanelQueryEngine.GeometryCandidates(snapshot, options).SequenceEqual(new[] { 0, 1, 3, 4 }),
            "Text queries measure all compatible offscreen geometry kinds after cheap branch restrictions");
        options.Search = "> 1";
        Check(!PanelQueryEngine.RequiresGeometryMeasurements(options) &&
            PanelQueryEngine.GeometryCandidates(snapshot, options).Count == 0,
            "Numeric expressions in Text mode never request geometry measurements");
        options.SearchMode = PanelSearchMode.Regex;
        options.Search = " ";
        Check(PanelQueryEngine.RequiresGeometryMeasurements(options) &&
            PanelQueryEngine.GeometryCandidates(snapshot, options).SequenceEqual(new[] { 0, 1, 3, 4 }),
            "Significant regex whitespace still uses displayed geometry and requests its measurements");
        options.SearchMode = PanelSearchMode.Text;
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
        {
            Throws<ArgumentException>(() => PanelQueryEngine.Execute(PanelSnapshot.Empty,
                new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = expression }));
            Throws<ArgumentException>(() => PanelQueryEngine.Execute(PanelSnapshot.Empty,
                new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength, Search = expression }));
        }
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
