using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

internal static class StatisticsChecks
{
    internal static void Run()
    {
        ExactValuesAndScope();
        BranchCountsAndFilters();
        BudgetedFrequencies();
        MenuPagingAndLifetime();
    }

    private static PanelSnapshot Sample() => new PanelSnapshot(new[]
    {
        new PanelRow(0, "{0}", "Number", "1", 1),
        new PanelRow(1, "{0}", "Integer", "1", 1),
        new PanelRow(2, "{0}", "Number", "1", 1.0000000000000002),
        new PanelRow(3, "{0}", "Number", "0", 0),
        new PanelRow(4, "{0}", "Number", "-0", -0.0),
        Text(5, "1"), Text(6, "Alpha"), Text(7, "alpha"), Text(8, " alpha "), Text(9, ""), Text(10, "Alpha"),
        new PanelRow(11, "{0}", "Text", "<same summary>", null),
        new PanelRow(12, "{0}", "Text", "<same summary>", null),
        new PanelRow(13, "{0}", "Null", "<null>", null, isNull: true),
        new PanelRow(14, "{0}", "Text", "broken", null, isValid: false, stringValue: "broken"),
        new PanelRow(15, "{0}", "Number", "NaN", double.NaN),
        new PanelRow(16, "{0}", "Number", "Infinity", double.PositiveInfinity),
        new PanelRow(0, "{1}", "Number", "8", 8)
    }, new[] { new PanelBranch("{0}", 17), new PanelBranch("{1}", 1), new PanelBranch("{2}", 0) });

    private static PanelRow Text(int index, string value) => new PanelRow(index, "{0}", "Text", value, null, stringValue: value);

    private static void ExactValuesAndScope()
    {
        var snapshot = Sample();
        var stats = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions()).Statistics;
        Check(stats.ScalarCount == 12 && stats.UniqueExcludedCount == 6 && stats.UniqueValues.Count == 9,
            "Only valid finite numbers and explicitly captured strings participate in unique values");
        Check(stats.UniqueValues[0].NumericValue == 1 && stats.UniqueValues[0].Count == 2 &&
            stats.UniqueValues[1].NumericValue == 1.0000000000000002 && stats.UniqueValues[1].Count == 1 &&
            stats.UniqueValues[2].NumericValue == 0 && stats.UniqueValues[2].Count == 2,
            "Numeric identity unifies integer/number and signed zero without rounding close doubles");
        Check(stats.UniqueValues.Skip(3).Take(5).Select(value => value.StringValue)
            .SequenceEqual(new[] { "1", "Alpha", "alpha", " alpha ", "" }) && stats.UniqueValues[4].Count == 2,
            "Exact text keeps case, whitespace and empty values separate from numbers");
        Check(stats.UniqueValues.Sum(value => value.Count) == stats.ScalarCount && stats.NullCount == 1 && stats.InvalidCount == 1,
            "All included scalar rows have one frequency and excluded rows retain normal validity counts");
        try { ((IList<PanelValueFrequency>)stats.UniqueValues)[0] = stats.UniqueValues[1]; throw new Exception("Mutable frequency list"); }
        catch (NotSupportedException) { }
        try { ((IDictionary<string, int>)stats.BranchItemCounts)["{0}"] = 0; throw new Exception("Mutable branch counts"); }
        catch (NotSupportedException) { }
        var sorted = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        Check(sorted.Statistics.UniqueValues.Select(Key).SequenceEqual(stats.UniqueValues.Select(Key)),
            "Distinct values retain source occurrence order regardless of row sorting");
        Check(CanvasStatistics.Label(sorted, CanvasStatistics.OverviewRows, 0) !=
            CanvasStatistics.Label(sorted, CanvasStatistics.OverviewRows + 1, 0),
            "Exact unique labels remain distinguishable with zero displayed decimal places");
        var longText = new string('x', 200) + "\n\t\\\"";
        var textResult = PanelQueryEngine.Execute(new PanelSnapshot(new[] { Text(0, longText) },
            new[] { new PanelBranch("{0}", 1) }), new PanelQueryOptions());
        Check(CanvasStatistics.Label(textResult, CanvasStatistics.OverviewRows, -1).Length < 150 &&
            CanvasStatistics.Label(textResult, CanvasStatistics.OverviewRows, -1, full: true).Contains("\\n\\t\\\\\\\""),
            "Painting bounds string formatting while details retain and escape the complete value");
    }

    private static string Key(PanelValueFrequency value) => value.NumericValue.HasValue
        ? "Number " + value.NumericValue.Value.ToString("R") : "Text " + value.StringValue;

    private static void BranchCountsAndFilters()
    {
        var snapshot = Sample();
        var all = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Check(all.Statistics.BranchItemCounts["{2}"] == 0 && all.Statistics.BranchCount == 3,
            "Unfiltered branch counts retain empty branches");
        var filtered = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "> 1" });
        Check(filtered.Statistics.ItemCount == 2 && filtered.Statistics.BranchItemCounts["{0}"] == 1 &&
            filtered.Statistics.BranchItemCounts["{1}"] == 1 && !filtered.Statistics.BranchItemCounts.ContainsKey("{2}"),
            "Per-branch counts follow the complete filtered result and exclude nonmatching empty branches");
        Check(CanvasStatistics.Summary(filtered) == "2/18 items · 2/3 branches" &&
            CanvasStatistics.BranchCount(filtered, "{0}") == "1/17 items",
            "Compact counts distinguish result length from pre-filter tree length");
        var empty = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { BranchPath = "{2}" });
        Check(empty.Statistics.ItemCount == 0 && empty.Statistics.BranchCount == 1 && empty.Statistics.UniqueValues.Count == 0,
            "Selecting an empty branch retains the branch without inventing scalar values");
    }

    private static void BudgetedFrequencies()
    {
        var snapshot = Sample();
        using var work = PanelQueryEngine.Start(snapshot, new PanelQueryOptions());
        int slices = 0;
        while (true)
        {
            int checks = 0;
            if (work.TryComplete(() => ++checks > 1)) break;
            Check(work.Result == null, "Statistics cannot escape while frequencies are still being frozen");
            if (++slices > 200) throw new Exception("Statistics stopped making progress");
        }
        Check(slices > snapshot.Rows.Count + snapshot.Branches.Count && work.Result!.Statistics.UniqueValues[0].Count == 2,
            "The deadline also yields during frequency publication without recounting rows");
        using var cancellation = new CancellationTokenSource();
        using var canceled = PanelQueryEngine.Start(snapshot, new PanelQueryOptions(), cancellation.Token);
        for (int i = 0; i < snapshot.Rows.Count + snapshot.Branches.Count + 2; i++)
        {
            int checks = 0;
            canceled.TryComplete(() => ++checks > 1);
        }
        cancellation.Cancel();
        try { canceled.Complete(); throw new Exception("Canceled frequencies were published"); }
        catch (OperationCanceledException) { }
        Check(canceled.Result == null, "Cancellation during frequency materialization preserves atomic publication");
    }

    private static void MenuPagingAndLifetime()
    {
        var result = PanelQueryEngine.Execute(Sample(), new PanelQueryOptions());
        foreach (int height in new[] { 110, 220, 360 })
        {
            var menu = new CanvasMenu();
            var panel = new RectangleF(50, 100, 300, height);
            var anchor = new RectangleF(panel.Right - 74, panel.Bottom - 38, 46, 16);
            menu.Toggle(CanvasMenuKind.Statistics, result.Snapshot, new PanelQueryOptions(), result);
            menu.Layout(panel, default, default, default, statistics: anchor);
            Check(panel.Contains(menu.Bounds) && menu.Bounds.Bottom < anchor.Top,
                "The stats menu opens upward inside minimum Full and Half panels");
            var reached = new HashSet<int>();
            for (int page = 0; page < menu.Count; page++)
            {
                menu.MarkPainted();
                for (int row = 0; row < menu.Visible; row++)
                {
                    Check(menu.TrySelect(new PointF(menu.Bounds.X + 8, menu.Bounds.Y + row * 20 + 10), out int index),
                        "Every visible stats row can be inspected");
                    Check(menu.Label(index).Length > 0 && !menu.IsSelected(index, new PanelQueryOptions(), PanelUnitConversion.None, -1),
                        "Stats rows have readable labels and no selectable filter checkmarks");
                    reached.Add(index);
                }
                menu.TrySelect(new PointF(menu.Bounds.Right - 8, menu.Bounds.Bottom - 5), out _);
            }
            Check(reached.Count == menu.Count, "Paging reaches every summary and exact unique value");
            var refiltered = PanelQueryEngine.Execute(result.Snapshot, new PanelQueryOptions { Search = "> 1" });
            Check(menu.IsCurrent(result) && !menu.IsCurrent(refiltered),
                "A new result invalidates statistics even when it shares the same immutable snapshot");
            menu.Close();
            Check(menu.Count == 0, "Closing stats releases its result");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
