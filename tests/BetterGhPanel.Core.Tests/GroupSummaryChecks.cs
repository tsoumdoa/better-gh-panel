using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using BetterGhPanel.Core;
using Xunit;

public sealed class GroupSummaryChecks
{
    [Fact]
    public void SegmentedValuesMatchFlatValuesAcrossBoundariesAndUnicode()
    {
        string[] names = { "", "a", "A) with 1 object", " Δß😀z ", "\ud801\udc28", "\ud800", "(Mixed)" };
        foreach (string name in names)
        foreach (int count in new[] { 0, 1, 2, 12 })
        foreach (bool invalid in new[] { false, true })
        {
            var group = new PanelGroupSummary(name, count, false);
            var value = new PanelText(group, invalid);
            string flat = (invalid ? "Invalid · " : "") + group.Format();
            Assert.Equal(flat, value.ToString());
            Assert.Equal(new PanelText(flat), value);
            Assert.Equal(new PanelText(flat).GetHashCode(), value.GetHashCode());
            for (int start = 0; start < flat.Length; start++)
            for (int length = 1; length <= Math.Min(12, flat.Length - start); length++)
            {
                string search = flat.Substring(start, length).ToUpperInvariant();
                Assert.Equal(flat.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0, value.Contains(search));
            }
            Assert.Equal(flat.IndexOf("not present", StringComparison.OrdinalIgnoreCase) >= 0, value.Contains("not present"));
            foreach (string other in names)
            {
                var otherValue = new PanelText(new PanelGroupSummary(other, count, false), invalid);
                string otherFlat = otherValue.ToString();
                Assert.Equal(Math.Sign(StringComparer.OrdinalIgnoreCase.Compare(flat, otherFlat)),
                    Math.Sign(value.Compare(otherValue, StringComparison.OrdinalIgnoreCase)));
            }
        }
    }

    [Fact]
    public void SegmentedComparisonAndSearchAgreeWithFlatRuntimeStrings()
    {
        var random = new Random(7384);
        string[] pieces = { "a", "A", "x", "(", ")", " ", " with ", "object", "ß", "Δ", "δ", "😀", "𐐀", "𐐨", "\ud800", "\udc00" };
        for (int i = 0; i < 5000; i++)
        {
            string Name() => string.Concat(Enumerable.Range(0, random.Next(0, 9)).Select(_ => pieces[random.Next(pieces.Length)]));
            var left = new PanelText(new PanelGroupSummary(Name(), random.Next(0, 4), false), random.Next(2) != 0);
            var right = new PanelText(new PanelGroupSummary(Name(), random.Next(0, 4), false), random.Next(2) != 0);
            string a = left.ToString(), b = right.ToString();
            int expected = Math.Sign(StringComparer.OrdinalIgnoreCase.Compare(a, b));
            int actual = Math.Sign(left.Compare(right, StringComparison.OrdinalIgnoreCase));
            Assert.True(expected == actual, $"Expected {expected}, got {actual}; a={string.Join(",", a.Select(c => ((int)c).ToString("X4")))}; b={string.Join(",", b.Select(c => ((int)c).ToString("X4")))}");
            Assert.Equal(Math.Sign(StringComparer.Ordinal.Compare(a, b)), Math.Sign(left.Compare(right, StringComparison.Ordinal)));
            string candidate = i % 2 == 0 ? a.Substring(random.Next(a.Length)) : Name();
            Assert.Equal(a.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0, left.Contains(candidate));
        }
    }

    [Fact]
    public void RepeatedLongSummariesReuseTransientTextWithoutRetainingIt()
    {
        var group = new PanelGroupSummary(new string('x', 524288), 0, false);
        long before = GC.GetAllocatedBytesForCurrentThread();
        string text = group.Format();
        for (int i = 0; i < 20; i++) Assert.Same(text, group.Format());
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 2_000_000);
        Assert.Equal("Empty Group (" + new string('x', 524288) + ")", text);

        var collectible = new PanelGroupSummary(new string('y', 524288), 1, false);
        WeakReference<string> weak = MakeTransient(collectible);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.TryGetTarget(out _));
        GC.KeepAlive(collectible);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<string> MakeTransient(PanelGroupSummary group) => new WeakReference<string>(group.Format());

    [Fact]
    public void TextRegexSetAndSortBoundAllocationsAndPreserveMembership()
    {
        const int count = 1000;
        var group = new PanelGroupSummary(new string('x', 524288), 2, false);
        var rows = Enumerable.Range(0, count).Select(i => Row(i, "{0}", group)).ToArray();
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", count) });

        foreach (var options in new[] {
            new PanelQueryOptions { Search = "x) with 2 objects" },
            new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "x\\) with 2 objects$" } })
        {
            var filter = new PanelSearchFilter(options);
            long before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var row in rows) Assert.True(filter.Matches(row, PanelUnitConversion.None, null, CancellationToken.None));
            Assert.Equal(1, filter.GroupEvaluationCount);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 2_000_000);
            Assert.Equal(count, PanelQueryEngine.Execute(snapshot, options).RowPositions.Count);
        }

        long allocated = Measure(() => PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending }), out var sorted);
        Assert.True(allocated < 2_000_000, "Sorting must not build one long string per row: " + allocated);
        Assert.Equal(Enumerable.Range(0, count), sorted.RowPositions);
        allocated = Measure(() => PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { CreateSet = true, SortMode = PanelSortMode.ValueAscending }), out var set);
        Assert.True(allocated < 2_000_000, "Sets must keep segmented keys and lazy values: " + allocated);
        Assert.Single(set.RowPositions);
        Assert.Equal(count, set.SetOccurrencesAt(0)!.Count);
        Assert.Equal(group.Format(), set.DisplaySet!.Values[0].Text);
        Assert.Equal(group.Format(), set.DisplaySet.Values[0].CopyText);
        Assert.Equal(group.Format(), set.Statistics.UniqueValues.Single().StringValue);
    }

    [Fact]
    public void SetHashesEachSharedOrDistinctSummaryOnce()
    {
        var groups = Enumerable.Range(0, 25).Select(i => new PanelGroupSummary(new string('x', 32768) + i, 0, false)).ToArray();
        var rows = groups.SelectMany((group, i) => Enumerable.Range(0, 40).Select(j => Row(i * 40 + j, "{0}", group))).ToArray();
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", rows.Length) });
        var set = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { CreateSet = true, SortMode = PanelSortMode.ValueDescending });
        Assert.Equal(groups.Length, set.RowPositions.Count);
        Assert.All(groups, group => Assert.Equal(1, group.HashComputationCount));
        Assert.All(set.RowPositions, position => Assert.Equal(40, set.SetOccurrencesAt(position)!.Count));
    }

    [Fact]
    public void SearchCacheKeepsValidityAndRowSpecificPathAndTypeSeparate()
    {
        var group = new PanelGroupSummary(new string('x', 1024), 0, false);
        var rows = new[] { Row(0, "{needle}", group),
            new PanelRow(0, "{1}", "NeedleType", "", null, PanelValidity.VerifiedValid, groupSummary: group),
            Row(1, "{1}", group), Row(2, "{1}", group, PanelValidity.VerifiedInvalid) };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{needle}", 1), new PanelBranch("{1}", 3) });
        Assert.Equal(new[] { 0, 1 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "needle" }).RowPositions);
        Assert.Equal(new[] { 3 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "invalid" }).RowPositions);
        Assert.Equal(new[] { 3 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions {
            SearchMode = PanelSearchMode.Regex, Search = "^invalid" }).RowPositions);
        string longNeedle = new string('x', 300) + ")";
        Assert.Equal(new[] { 0, 1, 2, 3 }, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = longNeedle }).RowPositions);
    }

    [Fact]
    public void RepeatedNamesStillReuseWorkAfterBoundedCaptureCacheFills()
    {
        const int capacity = 16384;
        var filter = new PanelSearchFilter(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "tail\\)$" });
        var comparer = new PanelTextComparer();
        for (int i = 0; i < capacity; i++)
        {
            var group = new PanelGroupSummary(i.ToString(), 0, false);
            Assert.False(filter.Matches(Row(i, "{0}", group), PanelUnitConversion.None, null, CancellationToken.None));
            _ = comparer.GetHashCode(new PanelText(group, false));
        }

        string sharedName = new string('x', 262144) + "tail";
        // Capture creates a new summary per occurrence after its own object cache
        // reaches capacity, but the exact stock group exposes the same name field.
        var groups = Enumerable.Range(0, 100).Select(_ => new PanelGroupSummary(sharedName, 0, false)).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var group in groups)
        {
            Assert.True(filter.Matches(Row(0, "{0}", group), PanelUnitConversion.None, null, CancellationToken.None));
            _ = comparer.GetHashCode(new PanelText(group, false));
        }
        Assert.Equal(capacity + 1, filter.GroupEvaluationCount);
        Assert.Equal(1, groups.Sum(group => group.HashComputationCount));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_500_000);

        var rows = groups.Select((group, i) => Row(i, "{0}", group)).ToArray();
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", rows.Length) });
        long allocated = Measure(() => PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { CreateSet = true,
            SortMode = PanelSortMode.ValueAscending }), out var set);
        Assert.True(allocated < 500_000, "Repeated post-capacity summaries should not be reformatted: " + allocated);
        Assert.Single(set.RowPositions);
        Assert.Equal(groups.Length, set.SetOccurrencesAt(0)!.Count);
    }

    [Fact]
    public void DistinctLongNamesAndFlatTextKeepExactSetAndSearchSemantics()
    {
        var groups = Enumerable.Range(0, 40).Select(i => new PanelGroupSummary(new string('a', 32768) + i, 1, false)).ToArray();
        var rows = groups.SelectMany((g, i) => new[] { Row(i * 2, "{0}", g), Row(i * 2 + 1, "{0}", g) }).ToArray();
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", rows.Length) });
        foreach (var options in new[] { new PanelQueryOptions { Search = "object" },
            new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "object$" } })
        {
            var filter = new PanelSearchFilter(options);
            long before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var row in rows) Assert.True(filter.Matches(row, PanelUnitConversion.None, null, CancellationToken.None));
            long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(groups.Length, filter.GroupEvaluationCount);
            Assert.True(allocation < (options.SearchMode == PanelSearchMode.Regex ? 3_500_000 : 300_000));
        }
        long allocated = Measure(() => PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { CreateSet = true }), out var set);
        Assert.True(allocated < 500_000, "Distinct set values should stay segmented: " + allocated);
        Assert.Equal(groups.Length, set.RowPositions.Count);
        Assert.All(set.RowPositions, position => Assert.Equal(2, set.SetOccurrencesAt(position)!.Count));

        string sameText = groups[0].Format();
        var mixed = new PanelSnapshot(new[] { Row(0, "{0}", groups[0]),
            new PanelRow(1, "{0}", "Text", sameText, null, stringValue: sameText),
            Row(2, "{0}", groups[0], PanelValidity.VerifiedInvalid),
            new PanelRow(3, "{0}", "Text", "Invalid · " + sameText, null, stringValue: "Invalid · " + sameText) },
            new[] { new PanelBranch("{0}", 4) });
        var mixedSet = PanelQueryEngine.Execute(mixed, new PanelQueryOptions { CreateSet = true });
        Assert.Equal(new[] { 0, 2 }, mixedSet.RowPositions);
        Assert.Equal(2, mixedSet.SetOccurrencesAt(0)!.Count);
        Assert.Equal(2, mixedSet.SetOccurrencesAt(2)!.Count);
    }

    private static long Measure(Func<PanelQueryResult> query, out PanelQueryResult result)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        result = query();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static PanelRow Row(int index, string path, PanelGroupSummary group, PanelValidity validity = PanelValidity.VerifiedValid) =>
        new PanelRow(index, path, "Group", "", null, validity, groupSummary: group);
}
