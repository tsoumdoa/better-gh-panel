using Xunit;
using System.Drawing;
using System.Linq;
using System.Threading;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

public class SetOccurrencesTests
{
    private readonly PanelSetOccurrences occurrences;
    private readonly PanelRow[] rows;
    private readonly PanelSnapshot snapshot;
    private readonly PanelQueryResult sorted;
    private readonly CanvasSetDetails details;
    private readonly RectangleF panel;

    public SetOccurrencesTests()
    {
        var addresses = Enumerable.Range(0, 11).Select(i => new PanelSourceAddress("{" + i % 2 + "}", i)).ToArray();
        occurrences = new PanelSetOccurrences(addresses);
        addresses[0] = new PanelSourceAddress("{99}", 99);
        rows = new[] { new PanelRow(0, "{0}", "Number", "2", 2, setOccurrences: occurrences),
            new PanelRow(1, "{0}", "Number", "1", 1, setOccurrences: new PanelSetOccurrences(new[] { new PanelSourceAddress("{1}", 12) })) };
        snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 2) });
        sorted = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending }, CancellationToken.None);
        details = new CanvasSetDetails();
        panel = new RectangleF(40, 40, 300, 110);
    }

    [Fact]
    public void OccurrenceCountsAndSourceLocationsAreImmutable()
    {
        Assert.True(occurrences.Count == 11 && occurrences.DuplicatesRemoved == 10 && occurrences.BranchCount == 2 &&
            occurrences.Locations[0].Path == "{0}" && occurrences.Locations[0].ItemIndex == 0,
            "Counts and locations are immutable and include the retained item");
    }

    [Fact]
    public void SortingKeepsOccurrencesAttachedToTheirValue()
    {
        Assert.True(sorted.RowPositions.SequenceEqual(new[] { 1, 0 }) && sorted.Snapshot.Rows[sorted.RowPositions[1]].SetOccurrences == occurrences,
            "Sorting keeps source frequencies attached to their own value");
    }

    [Fact]
    public void HalfPanelOpensAnIndependentFourRowDrawer()
    {
        details.Toggle(sorted, 0);
        details.Synchronize(snapshot, sorted);
        details.Layout(panel, true);
        Assert.True(details.IsOpen && details.Bounds.Top > panel.Bottom && details.Bounds.Width == 300 &&
            details.Visible == 4 && !details.CanPrevious && details.CanNext,
            "Minimum Half layout has an independent four-row drawer below the panel");
    }

    [Fact]
    public void DrawerPagingResolvesLastPageAddressesAndCannotOvershoot()
    {
        details.Toggle(sorted, 0);
        details.Synchronize(snapshot, sorted);
        details.Layout(panel, true);
        details.Page(1);
        details.Page(1);
        details.Layout(panel, true);
        Assert.True(details.Offset == 8 && details.Visible == 3 && details.CanPrevious && !details.CanNext,
            "Paging reaches the final occurrences without changing the drawer height");
        var location = details.LocationAt(new PointF(details.Table.X + 3, details.Table.Y + 34));
        Assert.True(location?.ItemIndex == 10, "Last-page rows resolve their original input address");
        details.Page(1);
        Assert.True(details.Offset == 8, "Paging cannot overshoot the last occurrence");
    }

    [Fact]
    public void HiddenDrawersRetainTheirOpenValueAndPage()
    {
        details.Toggle(sorted, 0);
        details.Synchronize(snapshot, sorted);
        details.Layout(panel, true);
        details.Page(1);
        details.Page(1);
        details.Layout(panel, true);
        details.Layout(panel, false);
        Assert.True(details.Bounds.IsEmpty && details.IsOpen, "Pending or hidden views hide the drawer while retaining presentation state");
        details.Synchronize(snapshot, sorted);
        details.Layout(panel, true);
        Assert.True(details.Offset == 8 && !details.Bounds.IsEmpty, "Sorting or showing the same snapshot preserves the open value and page");
    }

    [Fact]
    public void FilteringOutTheInspectedValueClosesItsDrawer()
    {
        details.Toggle(sorted, 0);
        details.Synchronize(snapshot, sorted);
        details.Layout(panel, true);
        var filtered = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "< 2" }, CancellationToken.None);
        details.Synchronize(snapshot, filtered);
        Assert.True(!details.IsOpen && details.Bounds.IsEmpty, "Filtering out the inspected value closes its drawer");
    }

    [Fact]
    public void SingleOccurrencesNeedNoPagerOrFooter()
    {
        details.Toggle(sorted, 0);
        details.Synchronize(snapshot, sorted);
        details.Layout(panel, true);
        details.Synchronize(snapshot, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "< 2" }));
        details.Toggle(sorted, 1);
        Assert.True(details.Occurrences!.Count == 1 && details.Offset == 0, "Single occurrences remain inspectable");
        details.Layout(panel, true);
        Assert.True(!details.HasPages && details.Previous.IsEmpty && details.Next.IsEmpty &&
            details.Bounds.Bottom - details.Table.Bottom == 4,
            "Short occurrence lists use no pager or reserved footer space");
    }

    [Fact]
    public void TogglingTheSameOccurrenceRowClosesItsDrawer()
    {
        details.Toggle(sorted, 0);
        details.Synchronize(snapshot, sorted);
        details.Layout(panel, true);
        details.Synchronize(snapshot, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "< 2" }));
        details.Toggle(sorted, 1);
        details.Layout(panel, true);
        details.Toggle(sorted, 1);
        Assert.True(!details.IsOpen, "The same row toggles closed");
    }

    [Fact]
    public void ReplacementSnapshotsInvalidateOccurrenceLocations()
    {
        details.Toggle(sorted, 0);
        details.Synchronize(snapshot, sorted);
        details.Layout(panel, true);
        details.Synchronize(new PanelSnapshot(rows, snapshot.Branches), null);
        Assert.True(!details.IsOpen, "New input invalidates all old locations even if the rows look identical");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void DrawerPagingStartsOnlyWhenASecondPageIsNeeded(int count)
    {
        var boundary = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Number", "2", 2,
            setOccurrences: new PanelSetOccurrences(occurrences.Locations.Take(count).ToArray())) }, snapshot.Branches);
        details.Toggle(PanelQueryEngine.Execute(boundary, new PanelQueryOptions()), 0);
        details.Layout(panel, true);
        Assert.True(details.HasPages == (count > 4) && details.Next.IsEmpty == (count <= 4),
            "Paging starts only when the table needs a second page");
    }

}
