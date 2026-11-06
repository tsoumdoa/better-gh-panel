using Xunit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

public class QueryAndPresentationTests
{
    private static PanelSnapshot Sample() => new PanelSnapshot(
        new[]
        {
            new PanelRow(0, "{0;1}", "Number", "10", 10),
            new PanelRow(1, "{0;1}", "Number", "2", 2),
            new PanelRow(0, "{0;1;2}", "Text", "Alpha", null),
            new PanelRow(0, "{0;10}", "Number", "10", 10),
            new PanelRow(1, "{0;10}", "Null", "<null>", null, true, false),
            new PanelRow(2, "{0;10}", "Text", "broken", null, false, false)
        },
        new[]
        {
            new PanelBranch("{0;1}", 2), new PanelBranch("{0;1;2}", 1),
            new PanelBranch("{0;10}", 3), new PanelBranch("{9}", 0)
        });

    private static PanelQueryResult Query(PanelQueryOptions? options = null) =>
        PanelQueryEngine.Execute(Sample(), options ?? new PanelQueryOptions());

    [Fact]
    public void StatisticsAndEmptyBranches()
    {
        var result = Query();
        Assert.Equal(6, result.Statistics.ItemCount);
        Assert.Equal(4, result.Statistics.BranchCount);
        Assert.Equal(true, result.BranchPaths.ToHashSet().SetEquals(Sample().Branches.Select(branch => branch.Path)));
        Assert.Equal(3, result.Statistics.NumericCount);
        Assert.Equal(1, result.Statistics.NullCount);
        Assert.Equal(2, result.Statistics.InvalidCount);
        Assert.Equal(2.0, result.Statistics.Minimum!.Value);
        Assert.Equal(10.0, result.Statistics.Maximum!.Value);
        Assert.True(Math.Abs((22.0 / 3) - (result.Statistics.Mean!.Value)) <= Math.Max(1, Math.Abs(22.0 / 3)) * 1e-12);
        Assert.Equal(3, result.Statistics.TypeCounts["Number"]);
        Assert.Equal(2, result.Statistics.TypeCounts["Text"]);
    }

    [Fact]
    public void SearchMatchesValuePathAndType()
    {
        Assert.Equal(new[] { 2 }, Query(new PanelQueryOptions { Search = "ALpHa" }).RowPositions);
        Assert.Equal(new[] { 0, 1, 3 }, Query(new PanelQueryOptions { Search = "nUmBeR" }).RowPositions);
        Assert.Equal(new[] { 2 }, Query(new PanelQueryOptions { Search = "{0;1;2}" }).RowPositions);
        Assert.Equal(0, Query(new PanelQueryOptions { Search = "absent" }).Statistics.BranchCount);
        Assert.Equal(3, Query(new PanelQueryOptions { Search = "number" }).Statistics.NumericCount);
    }

    [Fact]
    public void NumericComparisonsUseInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert.Equal(new[] { 0, 3 }, Query(new PanelQueryOptions { Search = "> 2.5" }).RowPositions);
            Assert.Equal(new[] { 0, 1, 3 }, Query(new PanelQueryOptions { Search = ">= 2" }).RowPositions);
            Assert.Equal(new[] { 1 }, Query(new PanelQueryOptions { Search = "< 10" }).RowPositions);
            Assert.Equal(new[] { 1 }, Query(new PanelQueryOptions { Search = "<= 2" }).RowPositions);
            Assert.Equal(new[] { 0, 3 }, Query(new PanelQueryOptions { Search = "= 1e1" }).RowPositions);
            Assert.Equal(new[] { 0, 3 }, Query(new PanelQueryOptions { Search = "==10" }).RowPositions);
            Assert.Equal(0, Query(new PanelQueryOptions { Search = "> nonsense" }).RowPositions.Count);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void BranchSelectionPreservesTreeSemantics()
    {
        Assert.Equal(new[] { 0, 1 }, Query(new PanelQueryOptions { BranchPath = "{0;1}" }).RowPositions);
        var descendants = Query(new PanelQueryOptions { BranchPath = "{0;1}", BranchPrefix = true });
        Assert.Equal(new[] { 0, 1, 2 }, descendants.RowPositions);
        Assert.Equal(2, descendants.Statistics.BranchCount);
        Assert.Equal(1, Query(new PanelQueryOptions { BranchPath = "{9}" }).Statistics.BranchCount);
        Assert.Equal(0, Query(new PanelQueryOptions { BranchPath = "{9}", Search = "hello" }).Statistics.BranchCount);
        Assert.Equal(0, Query(new PanelQueryOptions { BranchPath = "{404}" }).Statistics.BranchCount);
    }

    [Fact]
    public void TypeAndInvalidFilters()
    {
        Assert.Equal(new[] { 0, 1, 3 }, Query(new PanelQueryOptions { TypeName = "number" }).RowPositions);
        Assert.Equal(new[] { 4, 5 }, Query(new PanelQueryOptions { OnlyInvalid = true }).RowPositions);
        Assert.Equal(new[] { 5 }, Query(new PanelQueryOptions { OnlyInvalid = true, TypeName = "text" }).RowPositions);
    }

    [Fact]
    public void StableMixedSorting()
    {
        var ascending = Query(new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
        Assert.Equal(new[] { 1, 0, 2, 3, 5, 4 }, ascending.RowPositions);
        var descending = Query(new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        Assert.Equal(new[] { 0, 1, 2, 3, 5, 4 }, descending.RowPositions);
        var first = descending.Snapshot.Rows[descending.RowPositions[0]];
        Assert.Equal(0, first.OriginalIndex);
        Assert.Equal("{0;1}", first.Path);
        var mixed = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "ZType", "A", null),
            new PanelRow(1, "{0}", "AType", "Z", null),
            new PanelRow(2, "{0}", "AType", "A", null),
            new PanelRow(3, "{0}", "AType", "a", null)
        }, new[] { new PanelBranch("{0}", 4) });
        Assert.Equal(new[] { 1, 2, 3, 0 }, PanelQueryEngine.Execute(mixed,
            new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending }).RowPositions);
    }

    [Fact]
    public void ImmutableSnapshotAndView()
    {
        var rows = new List<PanelRow>(Sample().Rows);
        var originalRows = rows.ToArray();
        var branches = new List<PanelBranch>(Sample().Branches);
        var snapshot = new PanelSnapshot(rows, branches);
        rows.Clear(); branches.Clear();
        Assert.Equal(6, snapshot.Rows.Count);
        Assert.Equal(4, snapshot.Branches.Count);
        Assert.Equal(3, snapshot.TypeCounts["number"]);
        PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions()).RowPositions);
        Assert.Equal(originalRows, snapshot.Rows);
        Assert.ThrowsAny<NotSupportedException>(() => ((IList<PanelRow>)snapshot.Rows)[0] = snapshot.Rows[1]);
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Assert.ThrowsAny<NotSupportedException>(() => ((IList<int>)result.RowPositions)[0] = 5);
    }

    [Fact]
    public void OddLengthSorting()
    {
        foreach (var length in new[] { 1, 3, 7, 17, 257 })
        {
            var rows = Enumerable.Range(0, length).Select(index =>
                new PanelRow(index, "{0}", "Number", string.Empty, (index * 7) % 11)).ToArray();
            var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", length) });
            var ascending = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
            var descending = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
            Assert.Equal(rows.OrderBy(row => row.NumericValue), ascending.RowPositions.Select(position => snapshot.Rows[position]));
            Assert.Equal(rows.OrderByDescending(row => row.NumericValue), descending.RowPositions.Select(position => snapshot.Rows[position]));
        }
    }

    [Fact]
    public void EmptyAndNonfiniteNumbers()
    {
        var empty = PanelQueryEngine.Execute(PanelSnapshot.Empty, new PanelQueryOptions());
        Assert.Equal(0, empty.RowPositions.Count);
        Assert.Equal(0, empty.Statistics.BranchCount);
        Assert.Equal<double?>(null, empty.Statistics.Mean);
        Assert.Equal<double?>(null, empty.Statistics.Minimum);
        Assert.Equal<double?>(null, empty.Statistics.Maximum);
        var snapshot = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "Number", "NaN", double.NaN),
            new PanelRow(1, "{0}", "Number", "Infinity", double.PositiveInfinity),
            new PanelRow(2, "{0}", "Number", "null", 5, true),
            new PanelRow(3, "{0}", "Number", "3", 3),
            new PanelRow(4, "{0}", "Number", "3", 3)
        }, new[] { new PanelBranch("{0}", 5) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Assert.Equal(2, result.Statistics.NumericCount);
        Assert.Equal(3.0, result.Statistics.Mean!.Value);
        Assert.Equal(new[] { 3, 4 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = ">0" }).RowPositions);
    }

    [Fact]
    public void InvalidFiniteNumbers()
    {
        // RhinoMath.UnsetValue is finite, but a native GH_Number carrying it is invalid.
        const double unsetValue = -1.23432101234321e308;
        var snapshot = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "Number", "-1.23432101234E+308", unsetValue, isValid: false),
            new PanelRow(1, "{0}", "Number", "10", 10),
            new PanelRow(2, "{0}", "Number", "42", 42, isValid: false)
        }, new[] { new PanelBranch("{0}", 3) });
        var all = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Assert.Equal(new[] { 0, 1, 2 }, all.RowPositions);
        Assert.Equal(2, all.Statistics.InvalidCount);
        Assert.Equal(1, all.Statistics.NumericCount);
        Assert.Equal<double?>(10, all.Statistics.Minimum);
        Assert.Equal<double?>(10, all.Statistics.Maximum);
        Assert.Equal<double?>(10, all.Statistics.Mean);
        Assert.Equal(1, all.Statistics.ScalarCount);
        Assert.Equal(2, all.Statistics.UniqueExcludedCount);

        foreach (var mode in new[] { PanelSearchMode.Text, PanelSearchMode.Value })
        {
            Assert.Equal(0, PanelQueryEngine.Execute(snapshot,
                new PanelQueryOptions { SearchMode = mode, Search = "< 0" }).RowPositions.Count);
            Assert.Equal(0, PanelQueryEngine.Execute(snapshot,
                new PanelQueryOptions { SearchMode = mode, Search = "> 20" }).RowPositions.Count);
            Assert.Equal(new[] { 1 }, PanelQueryEngine.Execute(snapshot,
                new PanelQueryOptions { SearchMode = mode, Search = "< 20" }).RowPositions);
        }

        var invalid = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { OnlyInvalid = true });
        Assert.Equal(new[] { 0, 2 }, invalid.RowPositions);
        Assert.Equal(0, invalid.Statistics.NumericCount);
        Assert.Equal<double?>(null, invalid.Statistics.Mean);
        Assert.Equal(new[] { 1, 0, 2 }, PanelQueryEngine.Execute(snapshot,
            new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending }).RowPositions);
        Assert.Equal(new[] { 1, 2, 0 }, PanelQueryEngine.Execute(snapshot,
            new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending }).RowPositions);
    }

    [Fact]
    public void ExtremeNumericStatistics()
    {
        var snapshot = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "Number", "min", -double.MaxValue),
            new PanelRow(1, "{0}", "Number", "max", double.MaxValue)
        }, new[] { new PanelBranch("{0}", 2) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Assert.Equal(0.0, result.Statistics.Mean!.Value);
    }

    [Fact]
    public void Cancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PanelQueryEngine.Execute(Sample(), new PanelQueryOptions(), source.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => PanelQueryEngine.Execute(PanelSnapshot.Empty, new PanelQueryOptions(), source.Token));
    }

    [Fact]
    public void LargeData()
    {
        const int count = 262144;
        const int branchSize = 1024;
        var rows = new PanelRow[count];
        var branches = new PanelBranch[count / branchSize];
        for (var branch = 0; branch < branches.Length; branch++)
        {
            var path = "{" + branch.ToString(CultureInfo.InvariantCulture) + "}";
            branches[branch] = new PanelBranch(path, branchSize);
            for (var index = 0; index < branchSize; index++)
            {
                var position = branch * branchSize + index;
                rows[position] = new PanelRow(index, path, "Number",
                    position.ToString(CultureInfo.InvariantCulture), position);
            }
        }
        var snapshot = new PanelSnapshot(rows, branches);
        var watch = Stopwatch.StartNew();
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        watch.Stop();
        Assert.Equal(count, result.Statistics.ItemCount);
        Assert.Equal(count / branchSize, result.Statistics.BranchCount);
        Assert.True(Math.Abs(((count - 1) / 2.0) - (result.Statistics.Mean!.Value)) <= Math.Max(1, Math.Abs((count - 1) / 2.0)) * 1e-12);
        Assert.Equal(Enumerable.Range(0, branches.Length).SelectMany(branch =>
            Enumerable.Range(branch * branchSize, branchSize).Reverse()), result.RowPositions);
        Console.WriteLine($"  {count:N0} rows filtered, aggregated and sorted in {watch.ElapsedMilliseconds} ms (local measurement).");
    }

    [Fact]
    public void CanvasDimensionsAndPresets()
    {
        Assert.Equal(CanvasPanelSize.DefaultWidth, CanvasPanelSize.DefaultHeight);
        Assert.Equal(CanvasPanelSize.DefaultWidth, CanvasPanelSize.Width(float.NaN));
        Assert.Equal(CanvasPanelSize.DefaultHeight, CanvasPanelSize.Height(float.PositiveInfinity));
        Assert.Equal(300f, CanvasPanelSize.Width(-100));
        Assert.Equal(220f, CanvasPanelSize.Height(-100));
        Assert.Equal(900f, CanvasPanelSize.Width(1000));
        for (int i = 0; i < CanvasPanelSize.PresetNames.Length; i++)
            Assert.Equal(i, CanvasPanelSize.PresetIndex(CanvasPanelSize.PresetWidth(i), CanvasPanelSize.PresetHeight(i)));
        Assert.Equal(-1, CanvasPanelSize.PresetIndex(500, 420));
        Assert.Equal(360f, CanvasPanelSize.DisplayHeight(360, CanvasPanelDisplay.Full));
        Assert.Equal(180f, CanvasPanelSize.DisplayHeight(360, CanvasPanelDisplay.Half));
        Assert.Equal(360f, CanvasPanelSize.DisplayHeight(360, (CanvasPanelDisplay)2));
        Assert.Equal(210f, CanvasPanelSize.DisplayHeight(420, CanvasPanelDisplay.Half));
        Assert.Equal(110f, CanvasPanelSize.DisplayHeight(220, CanvasPanelDisplay.Half));
        Assert.Equal(180f, CanvasPanelSize.DisplayHeight(float.NaN, CanvasPanelDisplay.Half));
        Assert.Equal(CanvasPanelDisplay.Half, CanvasPanelSize.NextDisplay(CanvasPanelDisplay.Full));
        Assert.Equal(CanvasPanelDisplay.Full, CanvasPanelSize.NextDisplay(CanvasPanelDisplay.Half));
        Assert.Equal(CanvasPanelDisplay.Full, CanvasPanelSize.NextDisplay((CanvasPanelDisplay)2));
        foreach (float fullHeight in new[] { 220f, 360f, 420f })
        {
            float fixedGrip = (fullHeight + CanvasPanelSize.HeaderHeight) / 2;
            foreach (CanvasPanelDisplay display in Enum.GetValues(typeof(CanvasPanelDisplay)))
            {
                float visibleGrip = CanvasPanelSize.DisplayTopOffset(fullHeight, display) +
                    (CanvasPanelSize.DisplayHeight(fullHeight, display) + CanvasPanelSize.HeaderHeight) / 2;
                Assert.Equal(fixedGrip, visibleGrip);
            }
        }
    }

    [Fact]
    public void CanvasMenuPagingAndSnapshot()
    {
        var branches = Enumerable.Range(0, 100).Select(i => new PanelBranch("{" + i + "}", 0)).ToArray();
        var snapshot = new PanelSnapshot(Array.Empty<PanelRow>(), branches);
        var menu = new CanvasMenu();
        var panel = new System.Drawing.RectangleF(0, 0, 360, 280);
        var anchor = new System.Drawing.RectangleF(9, 60, 226, 22);
        menu.Toggle(CanvasMenuKind.Branch, snapshot, new PanelQueryOptions());
        menu.Layout(panel, anchor, anchor, anchor);
        Assert.Equal(true, menu.IsCurrent(snapshot));
        Assert.Equal(false, menu.IsCurrent(PanelSnapshot.Empty));
        Assert.Equal(102, menu.Count);
        var next = new System.Drawing.PointF(menu.Bounds.Right - 5, menu.Bounds.Bottom - 5);
        var firstRow = new System.Drawing.PointF(menu.Bounds.X + 5, menu.Bounds.Y + 5);
        Assert.Equal(false, menu.TrySelect(firstRow, out _)); // No page has been painted yet.
        menu.MarkPainted();
        Assert.Equal(false, menu.TrySelect(next, out _));
        Assert.Equal(false, menu.TrySelect(firstRow, out _)); // The next page has not been painted.
        Assert.Equal(false, menu.Hover(firstRow));
        for (int i = 0; i < 50; i++) { menu.MarkPainted(); Assert.Equal(false, menu.TrySelect(next, out _)); }
        menu.MarkPainted();
        Assert.Equal(menu.Count - menu.Visible, menu.Offset);
        Assert.Equal(true, menu.TrySelect(new System.Drawing.PointF(menu.Bounds.X + 5, menu.Bounds.Y + 5), out var choice));
        Assert.Equal(menu.Offset, choice);
        Assert.Equal(branches[choice - 2].Path, menu.BranchPathAt(choice));
        Assert.Equal(false, menu.TrySelect(new System.Drawing.PointF(-10, -10), out _));
        Assert.Equal(CanvasMenuKind.None, menu.Kind);

        menu.Toggle(CanvasMenuKind.Type, snapshot, new PanelQueryOptions { TypeName = "Saved type" });
        menu.Layout(panel, anchor, anchor, anchor);
        Assert.Equal("Saved type", menu.TypeNameAt(2));
        Assert.Equal("Invalid only", menu.Label(1));
        Assert.Equal(true, menu.IsSelected(1, new PanelQueryOptions { OnlyInvalid = true }, PanelUnitConversion.None, -1));
        menu.Toggle(CanvasMenuKind.Type, snapshot, new PanelQueryOptions());
        Assert.Equal(CanvasMenuKind.None, menu.Kind);

        var options = new PanelQueryOptions();
        Assert.Equal("Num/Text +1", CanvasMenu.TypeHint(Query(options), options));
        options.Search = "Alpha";
        Assert.Equal("Text", CanvasMenu.TypeHint(Query(options), options));
        options.Search = "no matches";
        Assert.Equal("Num/Text +1", CanvasMenu.TypeHint(Query(options), options));
        options.TypeName = "Saved type";
        Assert.Equal("Saved type", CanvasMenu.TypeHint(Query(options), options));
        options.OnlyInvalid = true;
        Assert.Equal("Invalid", CanvasMenu.TypeHint(Query(options), options));
        Assert.Equal("No data", CanvasMenu.TypeHint(PanelQueryEngine.Execute(PanelSnapshot.Empty, new PanelQueryOptions()), new PanelQueryOptions()));

        var geometry = new PanelSnapshot(new[]
        {
            new PanelRow(0, "{0}", "Point", "(0,0,0)", null),
            new PanelRow(1, "{0}", "Surface", "<Surface>", null),
            new PanelRow(2, "{0}", "Mesh", "<Mesh>", null)
        }, new[] { new PanelBranch("{0}", 3) });
        options = new PanelQueryOptions();
        var geometryResult = PanelQueryEngine.Execute(geometry, options);
        Assert.Equal("Mesh/Pt +1", CanvasMenu.TypeHint(geometryResult, options));
        Assert.Equal("Mesh: 1, Point: 1, Surface: 1", CanvasMenu.TypeDetails(geometryResult));
        options.TypeName = "Surface";
        geometryResult = PanelQueryEngine.Execute(geometry, options);
        Assert.Equal("Srf", CanvasMenu.TypeHint(geometryResult, options));
        Assert.Equal("Surface", geometryResult.Snapshot.Rows[geometryResult.RowPositions[0]].TypeName);
        Assert.Equal("Plugin geometry", CanvasMenu.ShortTypeName("Plugin geometry"));
    }

    [Fact]
    public void UnitConversionsAndFormatting()
    {
        Assert.True(Math.Abs((1.23456789) - (PanelUnits.Convert(1234.56789, PanelUnitConversion.MillimetersToMeters))) <= Math.Max(1, Math.Abs(1.23456789)) * 1e-12);
        Assert.True(Math.Abs((1234.56789) - (PanelUnits.Convert(1.23456789, PanelUnitConversion.MetersToMillimeters))) <= Math.Max(1, Math.Abs(1234.56789)) * 1e-12);
        Assert.True(Math.Abs((1) - (PanelUnits.Convert(1000000, PanelUnitConversion.SquareMillimetersToSquareMeters))) <= Math.Max(1, Math.Abs(1)) * 1e-12);
        Assert.True(Math.Abs((1000000) - (PanelUnits.Convert(1, PanelUnitConversion.SquareMetersToSquareMillimeters))) <= Math.Max(1, Math.Abs(1000000)) * 1e-12);
        Assert.True(Math.Abs((180) - (PanelUnits.Convert(Math.PI, PanelUnitConversion.RadiansToDegrees))) <= Math.Max(1, Math.Abs(180)) * 1e-12);
        Assert.True(Math.Abs((Math.PI) - (PanelUnits.Convert(180, PanelUnitConversion.DegreesToRadians))) <= Math.Max(1, Math.Abs(Math.PI)) * 1e-12);
        foreach (var pair in new[] { PanelUnitConversion.MillimetersToMeters, PanelUnitConversion.SquareMillimetersToSquareMeters, PanelUnitConversion.RadiansToDegrees })
            Assert.True(Math.Abs((-1234.5) - (PanelUnits.Convert(PanelUnits.Convert(-1234.5, pair), (PanelUnitConversion)((int)pair + 1)))) <= Math.Max(1, Math.Abs(-1234.5)) * 1e-12);
        Assert.Equal("1.23 m", PanelUnits.Format(1.23456789, PanelUnitConversion.MillimetersToMeters, 2));
        Assert.Equal("1.23456789 m", PanelUnits.Format(1.23456789, PanelUnitConversion.MillimetersToMeters, -1));
        Assert.Equal("1 m²", PanelUnits.Format(1, PanelUnitConversion.SquareMillimetersToSquareMeters, 0));
        Assert.Equal("180.00 °", PanelUnits.Format(180, PanelUnitConversion.RadiansToDegrees, 2));
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1.23 m", PanelUnits.Format(1.234, PanelUnitConversion.MillimetersToMeters, 2));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        Assert.Equal("1.000000000000", PanelUnits.Format(1, PanelUnitConversion.None, 100));
        Assert.Equal(true, double.IsNaN(PanelUnits.Convert(double.NaN, PanelUnitConversion.RadiansToDegrees)));
        Assert.Equal(double.PositiveInfinity, PanelUnits.Convert(double.PositiveInfinity, PanelUnitConversion.MillimetersToMeters));
    }

    [Fact]
    public void UnitMenuLayout()
    {
        foreach (float height in new[] { 110f, 180f, 220f, 360f, 900f })
        {
            var menu = new CanvasMenu();
            var panel = new System.Drawing.RectangleF(0, 0, 300, height);
            var anchor = new System.Drawing.RectangleF(180, height - 24, 93, 17);
            menu.Toggle(CanvasMenuKind.Units, PanelSnapshot.Empty, new PanelQueryOptions());
            menu.Layout(panel, anchor, anchor, anchor, anchor);
            menu.MarkPainted();
            Assert.Equal(PanelUnits.ConversionCount + 4, menu.Count);
            Assert.Equal(true, menu.Bounds.Top >= panel.Top + 25 && menu.Bounds.Bottom < anchor.Top && menu.Bounds.Right <= panel.Right);
            Assert.Equal(true, menu.Bounds.Width < 180 && menu.Bounds.Right == anchor.Right);
            Assert.Equal("mm² → m²", menu.Label(3));
            Assert.Equal(true, menu.IsSelected(3, new PanelQueryOptions(), PanelUnitConversion.SquareMillimetersToSquareMeters, 2));
            int decimalsIndex = PanelUnits.ConversionCount + 1;
            Assert.Equal("Decimal places… (2)", menu.Label(decimalsIndex, decimals: 2));
            Assert.Equal("mm³ → m³", menu.Label((int)PanelUnitConversion.CubicMillimetersToCubicMeters));
            Assert.Equal(true, menu.Hover(new System.Drawing.PointF(menu.Bounds.Left + 5, menu.Bounds.Top + 5)));
            Assert.Equal(menu.Offset, menu.HoveredIndex);
            Assert.Equal(false, menu.Hover(new System.Drawing.PointF(menu.Bounds.Left + 5, menu.Bounds.Top + 5)));
            if (menu.Count > menu.Visible)
            {
                while (menu.Offset + menu.Visible <= decimalsIndex)
                {
                    menu.TrySelect(new System.Drawing.PointF(menu.Bounds.Right - 5, menu.Bounds.Bottom - 5), out _);
                    menu.MarkPainted();
                }
                Assert.Equal(true, menu.TrySelect(new System.Drawing.PointF(menu.Bounds.Left + 5, menu.Bounds.Top + (decimalsIndex - menu.Offset) * 20 + 5), out int index));
                Assert.Equal(decimalsIndex, index);
            }
        }
    }

    [Fact]
    public void GeometryMetricConversions()
    {
        Assert.Equal("1 m", PanelUnits.FormatGeometry(1000, 1, "mm", PanelUnitConversion.MillimetersToMeters, -1));
        Assert.Equal("1 m²", PanelUnits.FormatGeometry(1000000, 2, "mm", PanelUnitConversion.SquareMillimetersToSquareMeters, -1));
        Assert.Equal("1 m³", PanelUnits.FormatGeometry(1000000000, 3, "mm", PanelUnitConversion.CubicMillimetersToCubicMeters, -1));
        Assert.Equal("1000 mm", PanelUnits.FormatGeometry(1, 1, "m", PanelUnitConversion.MetersToMillimeters, -1));
        Assert.Equal("1e+06 mm²", PanelUnits.FormatGeometry(1, 2, "m", PanelUnitConversion.SquareMetersToSquareMillimeters, -1).ToLowerInvariant());
        Assert.Equal("1e+09 mm³", PanelUnits.FormatGeometry(1, 3, "m", PanelUnitConversion.CubicMetersToCubicMillimeters, -1).ToLowerInvariant());
        Assert.Equal("1000 mm²", PanelUnits.FormatGeometry(1000, 2, "mm", PanelUnitConversion.MillimetersToMeters, -1));
        Assert.Equal("1 m", PanelUnits.FormatGeometry(1, 1, "m", PanelUnitConversion.MillimetersToMeters, -1));
        Assert.Equal("2 ft³", PanelUnits.FormatGeometry(2, 3, "ft", PanelUnitConversion.CubicMillimetersToCubicMeters, -1));
        Assert.Equal("1.235 m", PanelUnits.FormatGeometry(1234.56789, 1, "mm", PanelUnitConversion.MillimetersToMeters, 3));
        Assert.Equal("1000.00 mm", PanelUnits.FormatGeometry(1000, 1, "mm", PanelUnitConversion.RadiansToDegrees, 2));
        Assert.Equal("unavailable", PanelUnits.FormatGeometry(double.NaN, 2, "mm", PanelUnitConversion.None, -1));
        Assert.Equal("unavailable", PanelUnits.FormatGeometry(double.MaxValue, 3, "m", PanelUnitConversion.CubicMetersToCubicMillimeters, -1));
        Assert.True(Math.Abs((1) - (PanelUnits.Convert(1000000000, PanelUnitConversion.CubicMillimetersToCubicMeters))) <= Math.Max(1, Math.Abs(1)) * 1e-12);
        Assert.True(Math.Abs((1000000000) - (PanelUnits.Convert(1, PanelUnitConversion.CubicMetersToCubicMillimeters))) <= Math.Max(1, Math.Abs(1000000000)) * 1e-12);
        Assert.Equal(5, (int)PanelUnitConversion.RadiansToDegrees);
        var info = new PanelGeometryInfo("Brep · closed", 1000000000, 3, "mm", "Volume", "V");
        Assert.Equal("Brep · closed · V 1 m³", info.FormatCompact(PanelUnitConversion.CubicMillimetersToCubicMeters, -1));
        Assert.Equal(1000000000d, info.Value!.Value);
        var surface = new PanelGeometryInfo("Srf · untrimmed · planar", 1000000, 2, "mm", "Area", "A",
            details: "Trimming: Untrimmed\nPlanarity: Planar\nDegree (U/V): 1/1");
        Assert.Equal("Srf · untrimmed · planar · A 1.00 m²", surface.FormatCompact(PanelUnitConversion.SquareMillimetersToSquareMeters, 2));
        Assert.Equal("Area: 1.00 m²\nTrimming: Untrimmed\nPlanarity: Planar\nDegree (U/V): 1/1",
            surface.FormatDetails(PanelUnitConversion.SquareMillimetersToSquareMeters, 2));
        Assert.Equal("Trimming: Untrimmed", new PanelGeometryInfo("Srf · untrimmed", canMeasure: true,
            details: "Trimming: Untrimmed").FormatDetails(PanelUnitConversion.None, -1));
        Assert.Equal("1.00", PanelUnits.FormatGeometry(1000000, 2, "mm", PanelUnitConversion.SquareMillimetersToSquareMeters, 2, false));
        Assert.Equal("1E+06", PanelUnits.FormatGeometry(1000000, 2, "mm", PanelUnitConversion.MillimetersToMeters, -1, false));
        Assert.Equal("1", PanelUnits.FormatGeometry(1000000000, 3, "mm", PanelUnitConversion.CubicMillimetersToCubicMeters, -1, false));
        Assert.Equal("unavailable", PanelUnits.FormatGeometry(double.MaxValue, 3, "m", PanelUnitConversion.CubicMetersToCubicMillimeters, -1, false));
    }

}
