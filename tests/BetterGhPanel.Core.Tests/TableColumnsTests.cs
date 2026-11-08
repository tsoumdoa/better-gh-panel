using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BetterGhPanel.Core;
using Xunit;

public class TableColumnsTests
{
    [Fact]
    public void HeaderTitlesMapByShownColumnOrderAndKeepNamesForEmptyItems()
    {
        string?[] titles = { "Panel", "Code", "Area (m²)", "", null, " \t\r\n", "  Title  ", "ignored extra" };
        string[] names = { "D", "F", "C1", "C2", "C3", "C4", "C5" };
        Assert.Equal(new[] { "Panel", "Code", "Area (m²)", "C2", "C3", "C4", "  Title  " },
            names.Select((name, index) => PanelTableText.HeaderTitle(titles, index, name)));
        Assert.Equal("C6", PanelTableText.HeaderTitle(titles, 8, "C6"));
        Assert.Equal("D", PanelTableText.HeaderTitle(Array.Empty<string?>(), 0, "D"));
        Assert.Equal("D", PanelTableText.HeaderTitle(titles, -1, "D"));
        Assert.StartsWith("Path\tIndex\tPanel\tCode\tArea (m²)\tC2" + Environment.NewLine,
            PanelTableTsv.Build(names.Take(4).Select((name, index) => PanelTableText.HeaderTitle(titles, index, name)),
                Array.Empty<(string, int, IReadOnlyList<string>)>()));
    }

    [Theory]
    [InlineData(PanelUnitConversion.MillimetersToMeters, 1, 4200, "L 4.20 m")]
    [InlineData(PanelUnitConversion.MillimetersToMeters, 2, 1800, "A 1800.00 mm²")]
    [InlineData(PanelUnitConversion.MillimetersToMeters, 3, 5600, "V 5600.00 mm³")]
    [InlineData(PanelUnitConversion.SquareMillimetersToSquareMeters, 2, 18000000, "A 18.00 m²")]
    [InlineData(PanelUnitConversion.CubicMillimetersToCubicMeters, 3, 56000000000, "V 56.00 m³")]
    public void MixedGeometryColumnUsesTheSameDimensionSpecificConversionAsPlainD(
        PanelUnitConversion conversion, int power, double rawMetric, string expectedMetric)
    {
        string type = power == 1 ? "Curve" : power == 2 ? "Surface" : "Brep";
        string label = power == 1 ? "Crv" : power == 2 ? "Srf" : "Brep";
        var geometry = new PanelGeometryInfo(label + " · shape hints", rawMetric, power, "mm",
            power == 1 ? "Length" : power == 2 ? "Area" : "Volume", power == 1 ? "L" : power == 2 ? "A" : "V");
        var row = new PanelRow(0, "{0}", type, "", null, geometryInfo: geometry);
        string plainD = PanelValuePresentation.FormatDisplay(row, conversion, 2, geometry);
        string column = PanelTableText.GeometryCell(row, geometry, false, false, conversion, 2, 0);
        Assert.EndsWith(" · " + expectedMetric, plainD);
        Assert.Equal(label + " " + expectedMetric, column);
    }

    [Fact]
    public void CompactGeometryCellsShareMetricRoundingAndOmitHints()
    {
        var geometry = new PanelGeometryInfo("Crv · closed · degree 1", 4201, units: "mm");
        var row = new PanelRow(0, "{0}", "Curve", "curve", null, geometryInfo: geometry);
        string Cell(bool homogeneous, bool measuring = false, PanelGeometryInfo? measured = null) =>
            PanelTableText.GeometryCell(row, measured ?? geometry, homogeneous, measuring, PanelUnitConversion.MillimetersToMeters, 2, 0.1);
        Assert.Equal("4.30", Cell(true));
        Assert.Equal("Crv L 4.30 m", Cell(false));
        Assert.Equal("measuring…", Cell(true, true));
        Assert.Equal("measuring…", Cell(false, true));
        Assert.Equal("unavailable", Cell(true, measured: geometry.WithUnavailableMeasurement()));
        Assert.Equal("Crv L unavailable", Cell(false, measured: geometry.WithUnavailableMeasurement()));
        Assert.Equal("4.30 · — · 5.60 … +2 in this set", PanelTableText.Summary(new[] { Cell(true), "—", "5.60" }, 5));

    }

    [Fact]
    public void AuxiliaryFailureDoesNotHideTheFinitePrimaryMetricOrItsSortKey()
    {
        var geometry = new PanelGeometryInfo("Brep", 8, 3, "mm", "Volume", "V", measurementUnavailable: true, hasArea: true);
        var row = new PanelRow(0, "{0}", "Brep", "", null, geometryInfo: geometry);
        Assert.Equal("8.00", PanelTableText.GeometryCell(row, geometry, true, false, PanelUnitConversion.None, 2, 0));
        Assert.Equal("Brep V 8.00 mm³", PanelTableText.GeometryCell(row, geometry, false, false, PanelUnitConversion.None, 2, 0));
        Assert.Equal(8, PanelTableText.MetricSortRow(row, geometry, PanelUnitConversion.None, 2, 0).NumericValue);
        Assert.True(geometry.MeasurementUnavailable);
    }

    [Fact]
    public void GeometrySummarySortKeysRetainNonMetricTextWhileOrdinaryRowsKeepMetricKeys()
    {
        var geometry = new PanelGeometryInfo("curve", 1, units: "mm");
        var text = new PanelRow(0, "{0}", "Text", "z", null);
        Assert.True(PanelTableText.MetricSortRow(text, null, PanelUnitConversion.None, 2, 0).IsNull);
        var z = PanelTableText.SummarySortRow(text, null, true, PanelUnitConversion.None, 2, 0);
        var a = PanelTableText.SummarySortRow(new PanelRow(1, "{0}", "Text", "a", null), null, true, PanelUnitConversion.None, 2, 0);
        var metric = PanelTableText.SummarySortRow(new PanelRow(2, "{0}", "Curve", "", null, geometryInfo: geometry), geometry, true, PanelUnitConversion.None, 2, 0);
        Assert.Equal("z · 1.00", PanelTableText.Summary(new[] { z.DisplayValue, metric.DisplayValue }, 2));
        Assert.Equal("a · 1.00", PanelTableText.Summary(new[] { a.DisplayValue, metric.DisplayValue }, 2));
        Assert.True(new PanelColumnSortKeys(new[] { z, a }).Compare(0, 1, false) > 0);
        Assert.Equal("1.00 … +2 items", PanelTableText.Summary(new[] { metric.DisplayValue }, 3, false));
    }

    [Fact]
    public void NonGeometryAndInvalidMembersDoNotChangeTheCommonMetric()
    {
        var length = new PanelGeometryInfo("Crv · closed", 4200, units: "mm");
        var area = new PanelGeometryInfo("Srf · planar", 12, 2, "mm", "Area", "A");
        var curve = new PanelRow(0, "{0}", "Curve", "", null, geometryInfo: length);
        var text = new PanelRow(1, "{0}", "Text", "note", null);
        var missing = new PanelRow(2, "{0}", "Null", "<null>", null, isNull: true);
        var invalid = new PanelRow(3, "{0}", "Surface", "bad", null, isValid: false, geometryInfo: area);
        PanelSnapshot Snapshot(params PanelRow[] rows) => new PanelSnapshot(rows, new[] { new PanelBranch("{0}", rows.Length) });
        Assert.Same(length, PanelTableText.CommonGeometryMetric(Snapshot(curve, text, missing, invalid)));
        Assert.Null(PanelTableText.CommonGeometryMetric(Snapshot(text, missing)));
        Assert.Null(PanelTableText.CommonGeometryMetric(Snapshot(curve, new PanelRow(4, "{0}", "Surface", "", null, geometryInfo: area))));
        Assert.Equal("note", PanelTableText.GeometryCell(text, null, true, false, PanelUnitConversion.None, 2, 0));
        Assert.Equal(PanelTableText.Value(missing, PanelUnitConversion.None, 2, 0),
            PanelTableText.GeometryCell(missing, null, true, false, PanelUnitConversion.None, 2, 0));
        Assert.Equal("invalid", PanelTableText.GeometryCell(invalid, area, true, false, PanelUnitConversion.None, 2, 0));
        var unknownGeometry = new PanelGeometryInfo("unsupported shape details");
        var unverified = new PanelRow(4, "{0}", "Curve", "", null, PanelValidity.Unverified, geometryInfo: unknownGeometry);
        Assert.Equal("unverified", PanelTableText.GeometryCell(unverified, unknownGeometry, true, false, PanelUnitConversion.None, 2, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeometryColumnSortUsesPresentedPrimaryMetricAndKeepsEveryMissingMetricLast(bool descending)
    {
        var geometry = new PanelGeometryInfo("closed", 4201, units: "mm");
        PanelRow Key(double? metric, bool valid = true) => PanelTableText.MetricSortRow(
            new PanelRow(0, "{0}", "Curve", "ignored", null, isValid: valid),
            metric.HasValue ? new PanelGeometryInfo("closed", metric, units: "mm") : geometry.WithUnavailableMeasurement(),
            PanelUnitConversion.MillimetersToMeters, 2, 0.1);
        var key = Key(4201);
        Assert.Equal(4.3, key.NumericValue);
        Assert.Contains("L 4.30 m", key.DisplayValue);
        var keys = new PanelColumnSortKeys(new PanelRow?[] { key, Key(2000), Key(null), Key(1, false), Key(double.NaN),
            PanelTableText.MetricSortRow(new PanelRow(0, "{0}", "Number", "900", 900), null, PanelUnitConversion.None, 2, 0),
            Key(-1), Key(double.PositiveInfinity) });
        int[] order = Enumerable.Range(0, 8).OrderBy(position => position,
            System.Collections.Generic.Comparer<int>.Create((a, b) => keys.Compare(a, b, descending))).ToArray();
        Assert.Equal(descending ? new[] { 0, 1, 2, 3, 4, 5, 6, 7 } : new[] { 1, 0, 2, 3, 4, 5, 6, 7 }, order);
        // Rounding ties retain source order; conversion must match the displayed metric's dimension.
        Assert.Equal(0, keys.Compare(0, 0, descending));
        var area = PanelTableText.MetricSortRow(new PanelRow(0, "{0}", "Surface", "", null),
            new PanelGeometryInfo("planar", 4201, 2, "mm", "Area", "A"), PanelUnitConversion.MillimetersToMeters, 2, 0);
        Assert.Equal(4201, area.NumericValue);
    }

    [Fact]
    public void GeometryHeaderUnitsAlwaysKeepTheModelUnitUnlessConversionAppliesAndNeverLabelCounts()
    {
        PanelSnapshot Snapshot(params PanelGeometryInfo[] metrics) => new PanelSnapshot(metrics.Select((metric, index) =>
            new PanelRow(index, "{0}", "Geometry", "", null, geometryInfo: metric)).ToArray(), new[] { new PanelBranch("{0}", metrics.Length) });
        var length = new PanelGeometryInfo("closed", canMeasure: true, units: "mm");
        var area = new PanelGeometryInfo("planar", power: 2, units: "mm", metricName: "Area", metricLabel: "A", canMeasure: true);
        string Unit(PanelSnapshot snapshot, PanelUnitConversion conversion) =>
            PanelTableText.GeometryHeaderUnit(PanelTableText.CommonGeometryMetric(snapshot), conversion);
        Assert.Equal("m", Unit(Snapshot(length, length), PanelUnitConversion.MillimetersToMeters));
        Assert.Equal("", Unit(Snapshot(length, area), PanelUnitConversion.MillimetersToMeters));
        Assert.Equal("mm²", Unit(Snapshot(area), PanelUnitConversion.MillimetersToMeters));
        Assert.Equal("mm", Unit(Snapshot(length), PanelUnitConversion.None));
        Assert.Equal(" · L · m", PanelTableText.GeometryHeaderSuffix(length, PanelUnitConversion.MillimetersToMeters));
        Assert.Equal(" · L · mm", PanelTableText.GeometryHeaderSuffix(length, PanelUnitConversion.None));
        Assert.Equal("", PanelTableText.GeometryHeaderSuffix(null, PanelUnitConversion.MillimetersToMeters));
        Assert.Equal("", Unit(Snapshot(new PanelGeometryInfo("points", 4, 0, "", "Count", "")), PanelUnitConversion.MillimetersToMeters));
    }

    [Fact]
    public void HomogeneousScalarColumnsUseHeaderTypesAndNumericMissingAlignment()
    {
        PanelSnapshot Snapshot(params PanelRow[] rows) => new PanelSnapshot(rows, new[] { new PanelBranch("{0}", rows.Length) });
        var numbers = Snapshot(new PanelRow(0, "{0}", "Number", "1", 1));
        Assert.True(PanelTableText.NumericColumn(numbers));
        Assert.False(PanelTableText.ShowCellTypes(numbers));
        var text = Snapshot(new PanelRow(0, "{0}", "Text", "a", null));
        Assert.False(PanelTableText.NumericColumn(text));
        Assert.False(PanelTableText.ShowCellTypes(text));
        var mixed = Snapshot(numbers.Rows[0], text.Rows[0]);
        Assert.True(PanelTableText.ShowCellTypes(mixed));
        Assert.False(PanelTableText.NumericColumn(mixed));
        Assert.True(PanelTableText.ShowCellTypes(Snapshot(new PanelRow(0, "{0}", "Curve", "curve", null))));
        Assert.False(PanelTableText.NumericColumn(PanelSnapshot.Empty));
    }

    private static PanelTableSource SummarySource(Guid id, Func<int, int?> itemAt,
        Func<PanelQueryResult, IReadOnlyList<int>, bool, IEnumerable<(int Item, string Path)?>>? branches = null) =>
        new PanelTableSource(1, null, new[] { new PanelTableSummarySource(id, itemAt,
            branches ?? ((_, __, ___) => Array.Empty<(int Item, string Path)?>()), _ => Array.Empty<int>()) });

    [Fact]
    public void SetCellSummariesPreserveMissingMembersAndBoundThePreview()
    {
        var members = new HashSet<int>(new[] { 0, 2, 5, 7, 9, 12, 15 });
        var id = Guid.NewGuid();
        var snapshot = new PanelSnapshot(Enumerable.Range(0, 16).Select(index => new PanelRow(index, "{0}", "Number", "1", 1)).ToArray(), new[] { new PanelBranch("{0}", 16) });
        using var work = PanelQueryEngine.Start(snapshot, new PanelQueryOptions { CreateSet = true,
            TableSource = SummarySource(id, position => members.Contains(position) && position < 12 ? position + 100 : (int?)null) });
        int checks = 0;
        while (!work.TryComplete(() => ++checks % 7 == 0)) Assert.Null(work.Result);
        var summary = work.Result!.TableSummaries[id].Sets[0];
        Assert.Equal(16, summary.Count);
        Assert.Equal(new[] { 100, -1, 102, -1 }, summary.Preview);
    }

    [Fact]
    public void ColumnSummariesAreSkippedOutsideSummaryViewsOrWithoutVisibleProviders()
    {
        int visits = 0;
        var id = Guid.NewGuid();
        var source = SummarySource(id, position => { visits++; return position; }, (_, __, ___) =>
        { visits++; return Array.Empty<(int, string)?>(); });
        Assert.Empty(PanelQueryEngine.Execute(Data(), new PanelQueryOptions { TableSource = source }).TableSummaries);
        Assert.Empty(PanelQueryEngine.Execute(Data(), new PanelQueryOptions { CreateSet = true }).TableSummaries);
        Assert.Equal(0, visits);
    }

    [Fact]
    public void ListLengthSummariesPublishBoundedBranchPreviewsWithinTheQueryBudget()
    {
        var id = Guid.NewGuid();
        IEnumerable<(int Item, string Path)?> Branches()
        {
            for (int item = 0; item < 100; item++) { yield return null; yield return (item, "{0}"); }
        }
        var source = SummarySource(id, _ => throw new InvalidOperationException("List lengths use branch traversal"), (_, __, includeEmpty) =>
        { Assert.True(includeEmpty); return Branches(); });
        using var work = PanelQueryEngine.Start(Data(), new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength, TableSource = source });
        int checks = 0;
        while (!work.TryComplete(() => ++checks % 7 == 0)) Assert.Null(work.Result);
        var branch = work.Result!.TableSummaries[id].Branches["{0}"];
        Assert.Equal(100, branch.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, branch.Preview);
    }

    [Fact]
    public void CancellationInterruptsColumnSummaryMembersAndNullBranchCheckpoints()
    {
        var snapshot = new PanelSnapshot(Enumerable.Range(0, 4096).Select(index => new PanelRow(index, "{0}", "Number", "1", 1)).ToArray(), new[] { new PanelBranch("{0}", 4096) });
        foreach (bool branchView in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            int visits = 0;
            int? Item(int position) { if (++visits == 37) cancellation.Cancel(); return position; }
            IEnumerable<(int Item, string Path)?> Branches()
            {
                for (int i = 0; i < 4096; i++) { if (++visits == 37) cancellation.Cancel(); yield return null; }
            }
            var source = SummarySource(Guid.NewGuid(), Item, (_, __, ___) => Branches());
            using var work = PanelQueryEngine.Start(snapshot, new PanelQueryOptions { CreateSet = !branchView, DisplayFilterValue = branchView,
                SearchMode = branchView ? PanelSearchMode.ListLength : PanelSearchMode.Text, TableSource = source }, cancellation.Token);
            Assert.Throws<OperationCanceledException>(() => work.Complete());
            Assert.Equal(37, visits);
            Assert.Null(work.Result);
        }
    }

    [Fact]
    public void SavedColumnIdentityPrefersGuidAndFallsBackToTheInputIndexAfterDuplication()
    {
        var saved = Guid.NewGuid(); var replacement = Guid.NewGuid();
        var inputs = new[] { Guid.NewGuid(), replacement, saved };
        Assert.Equal(saved, PanelTableInputIdentity.Resolve(saved, inputs, 1));
        Assert.Equal(replacement, PanelTableInputIdentity.Resolve(Guid.NewGuid(), inputs, 1));
        Assert.Equal(Guid.Empty, PanelTableInputIdentity.Resolve(Guid.NewGuid(), inputs, -1));
        Assert.Equal(Guid.Empty, PanelTableInputIdentity.Resolve(Guid.NewGuid(), inputs, 3));
    }

    private static PanelSnapshot Data() => new PanelSnapshot(new[]
    {
        new PanelRow(0, "{0}", "Number", "3", 3), new PanelRow(1, "{0}", "Number", "1", 1),
        new PanelRow(2, "{0}", "Number", "2", 2), new PanelRow(0, "{2}", "Number", "4", 4)
    }, new[] { new PanelBranch("{0}", 3), new PanelBranch("{1}", 0), new PanelBranch("{2}", 1) });

    [Fact]
    public void AugmentedIndexAlignsExtrasAcrossColumnsAndKeepsEmptyBranches()
    {
        var options = new PanelQueryOptions { TableSource = new PanelTableSource(3, (_, __, ___) => new[]
        {
            new PanelTableExtra("{0}", "{0}:4", 0, 4, 4), new PanelTableExtra("{0}", "{0}:3", 0, 3, 3),
            new PanelTableExtra("{0}", "{0}:3", 1, 8, 3), new PanelTableExtra("{0}", "{0}:5", 2, 20, 5),
            new PanelTableExtra("{1}", "{1}:0", 2, 21)
        }) };
        using var work = PanelQueryEngine.Start(Data(), options);
        int checkpoints = 0;
        while (!work.TryComplete(() => ++checkpoints % 7 == 0)) Assert.Null(work.Result);
        var index = work.Result!.TableIndex!;
        Assert.True(PanelQueryPolicy.SameOutputFilter(new PanelQueryOptions(), options));
        Assert.Equal(9, index.Entries.Count);
        Assert.Equal(new[] { 3, 8, -1 }, index.Entries[3].Cells);
        Assert.Equal(new[] { 4, -1, -1 }, index.Entries[4].Cells);
        Assert.Equal(new[] { -1, -1, 20 }, index.Entries[5].Cells);
        Assert.Equal("{1}", index.Entries[6].Empty);
        Assert.True(index.Entries[7].Extra);
        Assert.Equal(8, index.OffsetOfItem(3));
        Assert.Equal(3, index.ItemAtOrAfter(3));
        Assert.Equal(4, index.ItemAtOrAfter(99));
    }

    [Theory]
    [InlineData(PanelSortMode.ValueAscending, 2, 0, 1)]
    [InlineData(PanelSortMode.ValueDescending, 0, 2, 1)]
    public void ColumnSortIsBranchLocalMissingLastAndOutputEquivalent(PanelSortMode mode, int first, int second, int third)
    {
        var snapshot = Data();
        var source = new PanelQueryOptions();
        var query = new PanelQueryOptions
        {
            SortMode = mode, SortColumn = Guid.NewGuid(), ColumnSortKeys = new PanelColumnSortKeys(new PanelRow?[]
            { new PanelRow(0, "{9}", "Number", "20", 20), null, new PanelRow(2, "{9}", "Number", "10", 10), null })
        };
        using var work = PanelQueryEngine.Start(snapshot, query);
        int checkpoints = 0;
        while (!work.TryComplete(() => ++checkpoints % 7 == 0)) { }
        Assert.Equal(new[] { first, second, third, 3 }, work.Result!.RowPositions);
        Assert.Equal(new[] { "{0}", "{1}", "{2}" }, work.Result.BranchPaths);
        Assert.True(PanelQueryPolicy.SameOutputFilter(source, query));
        Assert.False(PanelQueryPolicy.SameQuery(source, query));
        Assert.Equal(new[] { 0, 1, 2, 3 }, PanelQueryEngine.Execute(snapshot, source).RowPositions);
    }

    [Fact]
    public void CancellationInterruptsSetColumnKeyPreparationIncludingMissingCells()
    {
        var snapshot = new PanelSnapshot(Enumerable.Range(0, 4096).Select(index => new PanelRow(index, "{0}", "Number", "1", 1)).ToArray(),
            new[] { new PanelBranch("{0}", 4096) });
        using var cancellation = new CancellationTokenSource();
        int prepared = 0;
        IEnumerable<PanelRow?> Keys(IReadOnlyList<int> members)
        {
            foreach (int member in members)
            {
                if (++prepared == 37) cancellation.Cancel();
                yield return null; // Real adapters also yield checkpoints for missing/repeated partners.
            }
        }
        var options = new PanelQueryOptions { CreateSet = true, SortMode = PanelSortMode.ValueAscending,
            ColumnSortKeys = new PanelColumnSortKeys(_ => null, (_, members) => Keys(members)) };
        using var work = PanelQueryEngine.Start(snapshot, options, cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => work.Complete());
        Assert.InRange(prepared, 37, 257);
        Assert.Null(work.Result);
    }

    [Fact]
    public void CancellationInterruptsAugmentedIndexCollectionBeforePublication()
    {
        using var cancellation = new CancellationTokenSource();
        int visited = 0;
        IEnumerable<PanelTableExtra?> Extras()
        {
            for (int index = 0; index < 4096; index++)
            {
                if (++visited == 37) cancellation.Cancel();
                yield return new PanelTableExtra("{0}", index.ToString(), 0, index, index);
            }
        }
        var options = new PanelQueryOptions { TableSource = new PanelTableSource(1, (_, __, ___) => Extras()) };
        using var work = PanelQueryEngine.Start(Data(), options, cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => work.Complete());
        Assert.Equal(37, visited);
        Assert.Null(work.Result);
    }

    [Fact]
    public void TextColumnSortKeepsStableTiesAndNullCellsLast()
    {
        var options = new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending, ColumnSortKeys = new PanelColumnSortKeys(new PanelRow?[]
        { new PanelRow(0, "{0}", "Text", "b", null), new PanelRow(1, "{0}", "Text", "A", null), new PanelRow(2, "{0}", "Text", "a", null), null }) };
        Assert.Equal(new[] { 1, 2, 0, 3 }, PanelQueryEngine.Execute(Data(), options).RowPositions);
    }

    [Fact]
    public void SetColumnSortUsesTheBoundedSummaryIncludingNonrepresentativeMembers()
    {
        var snapshot = new PanelSnapshot(new[] { 1, 1, 2, 2, 3 }.Select((value, index) =>
            new PanelRow(index, "{0}", "Number", value.ToString(), value)).ToArray(), new[] { new PanelBranch("{0}", 5) });
        var options = new PanelQueryOptions
        {
            CreateSet = true, SortMode = PanelSortMode.ValueAscending,
            ColumnSortKeys = new PanelColumnSortKeys(new PanelRow?[] { null,
                new PanelRow(1, "{9}", "Text", "z", null), new PanelRow(2, "{9}", "Text", "b", null),
                new PanelRow(3, "{9}", "Text", "a", null), null })
        };
        using var work = PanelQueryEngine.Start(snapshot, options);
        int checkpoints = 0;
        while (!work.TryComplete(() => ++checkpoints % 5 == 0)) { }
        Assert.Equal(new[] { 2, 0, 4 }, work.Result!.RowPositions);
        options.SortMode = PanelSortMode.ValueDescending;
        Assert.Equal(new[] { 0, 2, 4 }, PanelQueryEngine.Execute(snapshot, options).RowPositions);
        options.SortMode = PanelSortMode.ValueAscending;
        options.ColumnSortKeys = new PanelColumnSortKeys(new PanelRow?[] { null,
            new PanelRow(1, "{9}", "Text", "", null), new PanelRow(2, "{9}", "Text", " ", null), null, null });
        Assert.Equal(new[] { 2, 0, 4 }, PanelQueryEngine.Execute(snapshot, options).RowPositions);
    }

    [Fact]
    public void TsvPreservesTitlesMissingCellsAndEmbeddedDelimiters()
    {
        var rows = new (string Path, int Index, IReadOnlyList<string> Values)[]
        { ("{0}", 2, new[] { "a\tb", "", "x\"y\nz" }), ("{2}", 0, new[] { "", "3.20", "" }) };
        Assert.Equal("Path\tIndex\tD\tArea\tName" + Environment.NewLine +
            "{0}\t2\t\"a\tb\"\t\t\"x\"\"y\nz\"" + Environment.NewLine + "{2}\t0\t\t3.20\t" + Environment.NewLine,
            PanelTableTsv.Build(new[] { "D", "Area", "Name" }, rows));
    }
}
