using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

internal static class SetOccurrencesChecks
{
    internal static void Run()
    {
        var addresses = Enumerable.Range(0, 11).Select(i => new PanelSourceAddress("{" + i % 2 + "}", i)).ToArray();
        var occurrences = new PanelSetOccurrences(addresses);
        addresses[0] = new PanelSourceAddress("{99}", 99);
        Check(occurrences.Count == 11 && occurrences.DuplicatesRemoved == 10 && occurrences.BranchCount == 2 &&
            occurrences.Locations[0].Path == "{0}" && occurrences.Locations[0].ItemIndex == 0,
            "Counts and locations are immutable and include the retained item");
        var rows = new[] { new PanelRow(0, "{0}", "Number", "2", 2, setOccurrences: occurrences),
            new PanelRow(1, "{0}", "Number", "1", 1, setOccurrences: new PanelSetOccurrences(new[] { new PanelSourceAddress("{1}", 12) })) };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 2) });
        var sorted = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending }, CancellationToken.None);
        Check(sorted.RowPositions.SequenceEqual(new[] { 1, 0 }) && sorted.Snapshot.Rows[sorted.RowPositions[1]].SetOccurrences == occurrences,
            "Sorting keeps source frequencies attached to their own value");
        var details = new CanvasSetDetails();
        var panel = new RectangleF(40, 40, 300, 110);
        details.Toggle(snapshot, 0);
        details.Synchronize(snapshot, sorted);
        details.Layout(panel, true);
        Check(details.IsOpen && details.Bounds.Top > panel.Bottom && details.Bounds.Width == 300 &&
            details.Visible == 4 && !details.CanPrevious && details.CanNext,
            "Minimum Half layout has an independent four-row drawer below the panel");
        details.Page(1);
        details.Page(1);
        details.Layout(panel, true);
        Check(details.Offset == 8 && details.Visible == 3 && details.CanPrevious && !details.CanNext,
            "Paging reaches the final occurrences without changing the drawer height");
        var location = details.LocationAt(new PointF(details.Table.X + 3, details.Table.Y + 34));
        Check(location?.ItemIndex == 10, "Last-page rows resolve their original input address");
        details.Page(1);
        Check(details.Offset == 8, "Paging cannot overshoot the last occurrence");
        details.Layout(panel, false);
        Check(details.Bounds.IsEmpty && details.IsOpen, "Pending or hidden views hide the drawer while retaining presentation state");
        details.Synchronize(snapshot, sorted);
        details.Layout(panel, true);
        Check(details.Offset == 8 && !details.Bounds.IsEmpty, "Sorting or showing the same snapshot preserves the open value and page");
        var filtered = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "< 2" }, CancellationToken.None);
        details.Synchronize(snapshot, filtered);
        Check(!details.IsOpen && details.Bounds.IsEmpty, "Filtering out the inspected value closes its drawer");
        details.Toggle(snapshot, 1);
        Check(details.Occurrences!.Count == 1 && details.Offset == 0, "Single occurrences remain inspectable");
        details.Layout(panel, true);
        Check(!details.HasPages && details.Previous.IsEmpty && details.Next.IsEmpty &&
            details.Bounds.Bottom - details.Table.Bottom == 4,
            "Short occurrence lists use no pager or reserved footer space");
        details.Toggle(snapshot, 1);
        Check(!details.IsOpen, "The same row toggles closed");
        details.Toggle(snapshot, 0);
        details.Synchronize(new PanelSnapshot(rows, snapshot.Branches), null);
        Check(!details.IsOpen, "New input invalidates all old locations even if the rows look identical");

        foreach (int count in new[] { 4, 5 })
        {
            var boundary = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Number", "2", 2,
                setOccurrences: new PanelSetOccurrences(occurrences.Locations.Take(count).ToArray())) }, snapshot.Branches);
            details.Toggle(boundary, 0);
            details.Layout(panel, true);
            Check(details.HasPages == (count > 4) && details.Next.IsEmpty == (count <= 4),
                "Paging starts only when the table needs a second page");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
