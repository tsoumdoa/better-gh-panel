using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

internal static class Program
{
    private static int Main()
    {
        var tests = new Action[]
        {
            StatisticsAndEmptyBranches,
            SearchMatchesValuePathAndType,
            NumericComparisonsUseInvariantCulture,
            BranchSelectionPreservesTreeSemantics,
            TypeAndInvalidFilters,
            StableMixedSorting,
            OddLengthSorting,
            ImmutableSnapshotAndView,
            WindowRetrieval,
            EmptyAndNonfiniteNumbers,
            ExtremeNumericStatistics,
            Cancellation,
            LargeData,
            CanvasDimensionsAndPresets,
            CanvasMenuPagingAndSnapshot,
            UnitConversionsAndFormatting,
            UnitMenuLayout
        };
        var failures = 0;
        foreach (var test in tests)
        {
            try { test(); Console.WriteLine("PASS " + test.Method.Name); }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + exception);
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static PanelSnapshot Sample() => new PanelSnapshot(42,
        new[]
        {
            Row(100, 0, 0, "{0;1}", "Number", "10", 10),
            Row(200, 0, 1, "{0;1}", "Number", "2", 2),
            Row(300, 1, 0, "{0;1;2}", "Text", "Alpha", null),
            Row(400, 2, 0, "{0;10}", "Number", "10", 10),
            Row(500, 2, 1, "{0;10}", "Null", "<null>", null, true, false),
            Row(600, 2, 2, "{0;10}", "Text", "broken", null, false, false)
        },
        new[]
        {
            new PanelBranch(0, "{0;1}", 2), new PanelBranch(1, "{0;1;2}", 1),
            new PanelBranch(2, "{0;10}", 3), new PanelBranch(3, "{9}", 0)
        });

    private static PanelRow Row(int id, int branch, int index, string path, string type,
        string value, double? number, bool isNull = false, bool isValid = true) =>
        new PanelRow(id, branch, index, path, type, value, number, isNull, isValid);

    private static PanelQueryResult Query(PanelQueryOptions? options = null) =>
        PanelQueryEngine.Execute(Sample(), options ?? new PanelQueryOptions());

    private static void StatisticsAndEmptyBranches()
    {
        var result = Query();
        Equal(42L, result.Snapshot.Version);
        Equal(6, result.Statistics.ItemCount);
        Equal(4, result.Statistics.BranchCount);
        Equal(0, result.BranchCounts["{9}"]);
        Equal(3, result.Statistics.NumericCount);
        Equal(1, result.Statistics.NullCount);
        Equal(2, result.Statistics.InvalidCount);
        Equal(2.0, result.Statistics.Minimum!.Value);
        Equal(10.0, result.Statistics.Maximum!.Value);
        Near(22.0 / 3, result.Statistics.Mean!.Value);
        Equal(3, result.Statistics.TypeCounts["Number"]);
        Equal(2, result.Statistics.TypeCounts["Text"]);
        Equal(20, result.Statistics.Histogram.Count);
        Equal(1, result.Statistics.Histogram[0]);
        Equal(2, result.Statistics.Histogram[19]);
        Equal(3, result.Statistics.Histogram.Sum());
    }

    private static void SearchMatchesValuePathAndType()
    {
        Sequence(new[] { 2 }, Query(new PanelQueryOptions { Search = "ALpHa" }).RowIds);
        Sequence(new[] { 0, 1, 3 }, Query(new PanelQueryOptions { Search = "nUmBeR" }).RowIds);
        Sequence(new[] { 2 }, Query(new PanelQueryOptions { Search = "{0;1;2}" }).RowIds);
        Equal(0, Query(new PanelQueryOptions { Search = "absent" }).Statistics.BranchCount);
        Equal(3, Query(new PanelQueryOptions { Search = "number" }).Statistics.NumericCount);
    }

    private static void NumericComparisonsUseInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Sequence(new[] { 0, 3 }, Query(new PanelQueryOptions { Search = "> 2.5" }).RowIds);
            Sequence(new[] { 0, 1, 3 }, Query(new PanelQueryOptions { Search = ">= 2" }).RowIds);
            Sequence(new[] { 1 }, Query(new PanelQueryOptions { Search = "< 10" }).RowIds);
            Sequence(new[] { 1 }, Query(new PanelQueryOptions { Search = "<= 2" }).RowIds);
            Sequence(new[] { 0, 3 }, Query(new PanelQueryOptions { Search = "= 1e1" }).RowIds);
            Sequence(new[] { 0, 3 }, Query(new PanelQueryOptions { Search = "==10" }).RowIds);
            Equal(0, Query(new PanelQueryOptions { Search = "> nonsense" }).RowIds.Count);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void BranchSelectionPreservesTreeSemantics()
    {
        Sequence(new[] { 0, 1 }, Query(new PanelQueryOptions { BranchPath = "{0;1}" }).RowIds);
        var descendants = Query(new PanelQueryOptions { BranchPath = "{0;1}", BranchPrefix = true });
        Sequence(new[] { 0, 1, 2 }, descendants.RowIds);
        Equal(2, descendants.Statistics.BranchCount);
        Equal(1, Query(new PanelQueryOptions { BranchPath = "{9}" }).Statistics.BranchCount);
        Equal(0, Query(new PanelQueryOptions { BranchPath = "{9}", Search = "hello" }).Statistics.BranchCount);
        Equal(0, Query(new PanelQueryOptions { BranchPath = "{404}" }).Statistics.BranchCount);
    }

    private static void TypeAndInvalidFilters()
    {
        Sequence(new[] { 0, 1, 3 }, Query(new PanelQueryOptions { TypeName = "number" }).RowIds);
        Sequence(new[] { 4, 5 }, Query(new PanelQueryOptions { OnlyInvalid = true }).RowIds);
        Sequence(new[] { 5 }, Query(new PanelQueryOptions { OnlyInvalid = true, TypeName = "text" }).RowIds);
        var options = new PanelQueryOptions { Search = "alpha", BranchPath = "{0;1}", BranchPrefix = true,
            TypeName = "Text", OnlyInvalid = true, SortMode = PanelSortMode.ValueDescending };
        var clone = options.Clone();
        options.Search = "changed";
        Equal("alpha", clone.Search);
        Equal(options.BranchPath, clone.BranchPath);
        Equal(options.BranchPrefix, clone.BranchPrefix);
        Equal(options.TypeName, clone.TypeName);
        Equal(options.OnlyInvalid, clone.OnlyInvalid);
        Equal(options.SortMode, clone.SortMode);
    }

    private static void StableMixedSorting()
    {
        var ascending = Query(new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
        Sequence(new[] { 1, 0, 3, 2, 5, 4 }, ascending.RowIds);
        var descending = Query(new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        Sequence(new[] { 0, 3, 1, 5, 2, 4 }, descending.RowIds);
        Equal(100, descending.GetRows(0, 1)[0].Id);
        Equal(0, descending.GetRows(0, 1)[0].OriginalIndex);
        Equal("{0;1}", descending.GetRows(0, 1)[0].Path);
        var mixed = new PanelSnapshot(1, new[]
        {
            Row(1, 0, 0, "{0}", "ZType", "A", null),
            Row(2, 0, 1, "{0}", "AType", "Z", null),
            Row(3, 0, 2, "{0}", "AType", "A", null),
            Row(4, 0, 3, "{0}", "AType", "a", null)
        }, new[] { new PanelBranch(0, "{0}", 4) });
        Sequence(new[] { 1, 2, 3, 0 }, PanelQueryEngine.Execute(mixed,
            new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending }).RowIds);
    }

    private static void ImmutableSnapshotAndView()
    {
        var rows = new List<PanelRow>(Sample().Rows);
        var branches = new List<PanelBranch>(Sample().Branches);
        var snapshot = new PanelSnapshot(1, rows, branches);
        rows.Clear(); branches.Clear();
        Equal(6, snapshot.Rows.Count);
        Equal(4, snapshot.Branches.Count);
        Equal(3, snapshot.TypeCounts["number"]);
        PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        Sequence(new[] { 0, 1, 2, 3, 4, 5 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions()).RowIds);
        Sequence(new[] { 100, 200, 300, 400, 500, 600 }, snapshot.Rows.Select(row => row.Id));
        Throws<NotSupportedException>(() => ((IList<PanelRow>)snapshot.Rows)[0] = snapshot.Rows[1]);
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Throws<NotSupportedException>(() => ((IList<int>)result.RowIds)[0] = 5);
        Throws<NotSupportedException>(() => ((IDictionary<string, int>)result.BranchCounts)["{9}"] = 5);
    }

    private static void OddLengthSorting()
    {
        foreach (var length in new[] { 1, 3, 7, 17, 257 })
        {
            var rows = Enumerable.Range(0, length).Select(index =>
                Row(index, 0, index, "{0}", "Number", string.Empty, (index * 7) % 11)).ToArray();
            var snapshot = new PanelSnapshot(1, rows, new[] { new PanelBranch(0, "{0}", length) });
            var ascending = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
            var descending = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
            Sequence(rows.OrderBy(row => row.NumericValue).Select(row => row.Id), ascending.GetRows(0, length).Select(row => row.Id));
            Sequence(rows.OrderByDescending(row => row.NumericValue).Select(row => row.Id), descending.GetRows(0, length).Select(row => row.Id));
        }
    }

    private static void WindowRetrieval()
    {
        var result = Query();
        Sequence(new[] { 500, 600 }, result.GetRows(4, 100).Select(row => row.Id));
        Equal(0, result.GetRows(10, 2).Count);
        Equal(0, result.GetRows(0, 0).Count);
        Equal(0, result.GetRows(int.MaxValue, int.MaxValue).Count);
        Throws<ArgumentOutOfRangeException>(() => result.GetRows(-1, 1));
        Throws<ArgumentOutOfRangeException>(() => result.GetRows(0, -1));
    }

    private static void EmptyAndNonfiniteNumbers()
    {
        var empty = PanelQueryEngine.Execute(PanelSnapshot.Empty, new PanelQueryOptions());
        Equal(0, empty.RowIds.Count);
        Equal(0, empty.Statistics.BranchCount);
        Equal<double?>(null, empty.Statistics.Mean);
        Equal<double?>(null, empty.Statistics.Minimum);
        Equal<double?>(null, empty.Statistics.Maximum);
        Equal(0, empty.Statistics.Histogram.Sum());
        var snapshot = new PanelSnapshot(1, new[]
        {
            Row(1, 0, 0, "{0}", "Number", "NaN", double.NaN),
            Row(2, 0, 1, "{0}", "Number", "Infinity", double.PositiveInfinity),
            Row(3, 0, 2, "{0}", "Number", "null", 5, true),
            Row(4, 0, 3, "{0}", "Number", "3", 3),
            Row(5, 0, 4, "{0}", "Number", "3", 3)
        }, new[] { new PanelBranch(0, "{0}", 5) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Equal(2, result.Statistics.NumericCount);
        Equal(2, result.Statistics.Histogram[0]);
        Equal(3.0, result.Statistics.Mean!.Value);
        Sequence(new[] { 3, 4 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = ">0" }).RowIds);
    }

    private static void ExtremeNumericStatistics()
    {
        var snapshot = new PanelSnapshot(1, new[]
        {
            Row(1, 0, 0, "{0}", "Number", "min", -double.MaxValue),
            Row(2, 0, 1, "{0}", "Number", "max", double.MaxValue)
        }, new[] { new PanelBranch(0, "{0}", 2) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Equal(0.0, result.Statistics.Mean!.Value);
        Equal(1, result.Statistics.Histogram[0]);
        Equal(1, result.Statistics.Histogram[19]);
    }

    private static void Cancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Throws<OperationCanceledException>(() => PanelQueryEngine.Execute(Sample(), new PanelQueryOptions(), source.Token));
        Throws<OperationCanceledException>(() => PanelQueryEngine.Execute(PanelSnapshot.Empty, new PanelQueryOptions(), source.Token));
    }

    private static void LargeData()
    {
        const int count = 262144;
        const int branchSize = 1024;
        var rows = new PanelRow[count];
        var branches = new PanelBranch[count / branchSize];
        for (var branch = 0; branch < branches.Length; branch++)
        {
            var path = "{" + branch.ToString(CultureInfo.InvariantCulture) + "}";
            branches[branch] = new PanelBranch(branch, path, branchSize);
            for (var index = 0; index < branchSize; index++)
            {
                var position = branch * branchSize + index;
                rows[position] = Row(position, branch, index, path, "Number",
                    position.ToString(CultureInfo.InvariantCulture), position);
            }
        }
        var snapshot = new PanelSnapshot(1, rows, branches);
        var watch = Stopwatch.StartNew();
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        watch.Stop();
        Equal(count, result.Statistics.ItemCount);
        Equal(count / branchSize, result.Statistics.BranchCount);
        Near((count - 1) / 2.0, result.Statistics.Mean!.Value);
        Equal(count, result.Statistics.Histogram.Sum());
        Equal(count - 1, result.RowIds[0]);
        Equal(0, result.RowIds[count - 1]);
        Equal(0, result.GetRows(count - 1, 50)[0].Id);
        Console.WriteLine($"  {count:N0} rows filtered, aggregated and sorted in {watch.ElapsedMilliseconds} ms (local measurement).");
    }

    private static void CanvasDimensionsAndPresets()
    {
        Equal(CanvasPanelSize.DefaultWidth, CanvasPanelSize.DefaultHeight);
        Equal(CanvasPanelSize.DefaultWidth, CanvasPanelSize.Width(float.NaN));
        Equal(CanvasPanelSize.DefaultHeight, CanvasPanelSize.Height(float.PositiveInfinity));
        Equal(300f, CanvasPanelSize.Width(-100));
        Equal(220f, CanvasPanelSize.Height(-100));
        Equal(900f, CanvasPanelSize.Width(1000));
        for (int i = 0; i < CanvasPanelSize.PresetNames.Length; i++)
            Equal(i, CanvasPanelSize.PresetIndex(CanvasPanelSize.PresetWidth(i), CanvasPanelSize.PresetHeight(i)));
        Equal(-1, CanvasPanelSize.PresetIndex(500, 420));
    }

    private static void CanvasMenuPagingAndSnapshot()
    {
        var branches = Enumerable.Range(0, 100).Select(i => new PanelBranch(i, "{" + i + "}", 0)).ToArray();
        var snapshot = new PanelSnapshot(1, Array.Empty<PanelRow>(), branches);
        var menu = new CanvasMenu();
        var panel = new System.Drawing.RectangleF(0, 0, 360, 280);
        var anchor = new System.Drawing.RectangleF(9, 60, 226, 22);
        menu.Toggle(CanvasMenuKind.Branch, snapshot, new PanelQueryOptions());
        menu.Layout(panel, anchor, anchor, anchor);
        Equal(true, menu.IsCurrent(snapshot));
        Equal(false, menu.IsCurrent(PanelSnapshot.Empty));
        Equal(102, menu.Count);
        var next = new System.Drawing.PointF(menu.Bounds.Right - 5, menu.Bounds.Bottom - 5);
        for (int i = 0; i < 50; i++) Equal(false, menu.TrySelect(next, out _));
        Equal(menu.Count - menu.Visible, menu.Offset);
        Equal(true, menu.TrySelect(new System.Drawing.PointF(menu.Bounds.X + 5, menu.Bounds.Y + 5), out var choice));
        Equal(menu.Offset, choice);
        Equal(branches[choice - 2].Path, menu.BranchPathAt(choice));
        Equal(false, menu.TrySelect(new System.Drawing.PointF(-10, -10), out _));
        Equal(CanvasMenuKind.None, menu.Kind);

        menu.Toggle(CanvasMenuKind.Type, snapshot, new PanelQueryOptions { TypeName = "Saved type" });
        menu.Layout(panel, anchor, anchor, anchor);
        Equal("Saved type", menu.TypeNameAt(2));
        Equal("✓ Invalid only", menu.Label(1, true));
        menu.Toggle(CanvasMenuKind.Type, snapshot, new PanelQueryOptions());
        Equal(CanvasMenuKind.None, menu.Kind);
    }

    private static void UnitConversionsAndFormatting()
    {
        Near(1.23456789, PanelUnits.Convert(1234.56789, PanelUnitConversion.MillimetersToMeters));
        Near(1234.56789, PanelUnits.Convert(1.23456789, PanelUnitConversion.MetersToMillimeters));
        Near(1, PanelUnits.Convert(1000000, PanelUnitConversion.SquareMillimetersToSquareMeters));
        Near(1000000, PanelUnits.Convert(1, PanelUnitConversion.SquareMetersToSquareMillimeters));
        Near(180, PanelUnits.Convert(Math.PI, PanelUnitConversion.RadiansToDegrees));
        Near(Math.PI, PanelUnits.Convert(180, PanelUnitConversion.DegreesToRadians));
        foreach (var pair in new[] { PanelUnitConversion.MillimetersToMeters, PanelUnitConversion.SquareMillimetersToSquareMeters, PanelUnitConversion.RadiansToDegrees })
            Near(-1234.5, PanelUnits.Convert(PanelUnits.Convert(-1234.5, pair), (PanelUnitConversion)((int)pair + 1)));
        Equal("1.23 m", PanelUnits.Format(1.23456789, PanelUnitConversion.MillimetersToMeters, 2, CultureInfo.InvariantCulture));
        Equal("1.23456789 m", PanelUnits.Format(1.23456789, PanelUnitConversion.MillimetersToMeters, -1, CultureInfo.InvariantCulture));
        Equal("1 m²", PanelUnits.Format(1, PanelUnitConversion.SquareMillimetersToSquareMeters, 0, CultureInfo.InvariantCulture));
        Equal("180.00 °", PanelUnits.Format(180, PanelUnitConversion.RadiansToDegrees, 2, CultureInfo.InvariantCulture));
        Equal("1,23 m", PanelUnits.Format(1.234, PanelUnitConversion.MillimetersToMeters, 2, CultureInfo.GetCultureInfo("de-DE")));
        Equal("1.000000000000", PanelUnits.Format(1, PanelUnitConversion.None, 100, CultureInfo.InvariantCulture));
        Equal(true, double.IsNaN(PanelUnits.Convert(double.NaN, PanelUnitConversion.RadiansToDegrees)));
        Equal(double.PositiveInfinity, PanelUnits.Convert(double.PositiveInfinity, PanelUnitConversion.MillimetersToMeters));
    }

    private static void UnitMenuLayout()
    {
        foreach (float height in new[] { 220f, 360f, 900f })
        {
            var menu = new CanvasMenu();
            var panel = new System.Drawing.RectangleF(0, 0, 300, height);
            var anchor = new System.Drawing.RectangleF(180, height - 24, 93, 17);
            menu.Toggle(CanvasMenuKind.Units, PanelSnapshot.Empty, new PanelQueryOptions());
            menu.Layout(panel, anchor, anchor, anchor, anchor);
            Equal(9, menu.Count);
            Equal(true, menu.Bounds.Top >= panel.Top + 25 && menu.Bounds.Bottom < anchor.Top && menu.Bounds.Right <= panel.Right);
            Equal("✓ mm² → m²", menu.Label(3, false, PanelUnitConversion.SquareMillimetersToSquareMeters));
            Equal("Decimal places…  (2)", menu.Label(8, false, decimals: 2));
            if (menu.Count > menu.Visible)
            {
                menu.TrySelect(new System.Drawing.PointF(menu.Bounds.Right - 5, menu.Bounds.Bottom - 5), out _);
                Equal(true, menu.TrySelect(new System.Drawing.PointF(menu.Bounds.Left + 5, menu.Bounds.Top + (8 - menu.Offset) * 20 + 5), out int index));
                Equal(8, index);
            }
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; got {actual}.");
    }

    private static void Near(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > Math.Max(1, Math.Abs(expected)) * 1e-12)
            throw new Exception($"Expected approximately {expected}; got {actual}.");
    }

    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual)) throw new Exception("Sequences differ.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }
}
