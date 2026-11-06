using Xunit;
using System;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

// Pure policy behind the UI/UX audit fixes: greyed-out row actions, Paths
// type-to-find, keyboard/wheel menu navigation and live-search validation.
public class UxAuditTests
{


    [Fact]
    public void RowActionAvailability()
    {
        var full = CanvasRowActions.Create(2, new[] { 0 }, false);
        Assert.True(full.All(action => action.Enabled), "Geometry selections on Windows keep every row action available");
        var text = CanvasRowActions.Create(2, new[] { 0 }, false, hasGeometry: false);
        Assert.True(!Find(text, PanelRowAction.FrameSelection).Enabled && !Find(text, PanelRowAction.CopyGeometry).Enabled &&
            Find(text, PanelRowAction.CopyValues).Enabled && Find(text, PanelRowAction.ViewFullValue).Enabled,
            "Selections without geometry grey out framing and geometry copy but keep value actions");
        Assert.True(Find(text, PanelRowAction.FrameSelection).DisabledReason == CanvasRowActions.NoGeometry,
            "Disabled actions explain why");
        var mac = CanvasRowActions.Create(1, new[] { 0 }, false, hasGeometry: true, canCopyGeometry: false);
        Assert.True(Find(mac, PanelRowAction.FrameSelection).Enabled &&
            Find(mac, PanelRowAction.CopyGeometry).DisabledReason == CanvasRowActions.GeometryCopyUnsupported,
            "Unsupported geometry copy is greyed out with its platform reason");
        Assert.True(text.Select(action => action.Action).SequenceEqual(full.Select(action => action.Action)),
            "Disabled actions stay in place so the menu keeps a stable shape");
    }

    [Fact]
    public void BranchMenuFind()
    {
        var branches = new[] { "{0;0}", "{0;1}", "{0;12}", "{1;2}", "{2;0}" }.Select(path => new PanelBranch(path, 1)).ToArray();
        var snapshot = new PanelSnapshot(Array.Empty<PanelRow>(), branches);
        var menu = new CanvasMenu();
        menu.Toggle(CanvasMenuKind.Branch, snapshot, new PanelQueryOptions());
        menu.Layout(new RectangleF(0, 0, 360, 280), new RectangleF(9, 60, 226, 22), default, new RectangleF(9, 60, 226, 22));
        Assert.True(menu.Count == 7 && menu.Label(1) == "Jump to exact path…", "An unfiltered Paths menu lists every branch");
        menu.SetBranchFilter("0;1");
        Assert.True(menu.Count == 4 && menu.BranchPathAt(2) == "{0;1}" && menu.BranchPathAt(3) == "{0;12}",
            "Typed digits narrow the list to paths containing them, ignoring braces");
        Assert.True(menu.HoveredIndex == 2 && menu.Label(1).StartsWith("Jump to {0;1}", StringComparison.Ordinal),
            "The first match is highlighted and the jump row names the typed path");
        Assert.True(menu.Label(2).StartsWith("{0;1}", StringComparison.Ordinal), "Labels follow the filtered branch order");
        menu.SetBranchFilter("9");
        Assert.True(menu.Count == 2 && menu.HoveredIndex == 1, "No matches leave the exact-path jump available");
        menu.SetBranchFilter(string.Empty);
        Assert.True(menu.Count == 7 && menu.BranchPathAt(6) == "{2;0}", "Clearing the typed filter restores every branch");
        menu.SetBranchFilter("1");
        menu.Close();
        menu.Toggle(CanvasMenuKind.Branch, snapshot, new PanelQueryOptions());
        Assert.True(menu.BranchFilter.Length == 0 && menu.Count == 7, "Reopening the menu starts without a stale filter");

        var ambiguous = Enumerable.Range(0, 20).Select(i => new PanelBranch("{" + i + ";1}", 0))
            .Concat(new[] { new PanelBranch("{1}", 0) }).ToArray();
        menu.Close();
        menu.Toggle(CanvasMenuKind.Branch, new PanelSnapshot(Array.Empty<PanelRow>(), ambiguous), new PanelQueryOptions());
        menu.Layout(new RectangleF(0, 0, 360, 140), default, default, new RectangleF(9, 60, 64, 22));
        menu.SetBranchFilter("1");
        Assert.True(menu.BranchPathAt(menu.HoveredIndex) == "{1}", "Enter prefers an exact path over earlier substring matches");
        Assert.True(menu.HoveredIndex >= menu.Offset && menu.HoveredIndex < menu.Offset + menu.Visible,
            "The exact match stays visible when it is on a later menu page");
    }

    [Fact]
    public void MenuKeyboardAndWheel()
    {
        var branches = Enumerable.Range(0, 30).Select(i => new PanelBranch("{" + i + "}", 0)).ToArray();
        var snapshot = new PanelSnapshot(Array.Empty<PanelRow>(), branches);
        var menu = new CanvasMenu();
        var anchor = new RectangleF(9, 60, 226, 22);
        menu.Toggle(CanvasMenuKind.Branch, snapshot, new PanelQueryOptions());
        menu.Layout(new RectangleF(0, 0, 360, 280), anchor, anchor, anchor);
        int visible = menu.Visible;
        Assert.True(visible < menu.Count, "The fixture overflows the menu");
        menu.MoveHover(1);
        Assert.True(menu.HoveredIndex == 0 && menu.Offset == 0, "Down starts at the first entry");
        for (int i = 0; i < visible; i++) menu.MoveHover(1);
        Assert.True(menu.HoveredIndex == visible && menu.Offset == 1, "Moving past the last visible row scrolls by one");
        menu.MoveHover(-100);
        Assert.True(menu.HoveredIndex == 0 && menu.Offset == 0, "Up clamps at the first entry");
        Assert.True(menu.Scroll(3) && menu.Offset == 3 && menu.HoveredIndex == -1, "The wheel scrolls the menu and clears the hover");
        Assert.True(!menu.Scroll(-10) || menu.Offset == 0, "Scrolling up clamps at the top");
        menu.Scroll(1000);
        Assert.True(menu.Offset == menu.Count - menu.Visible && !menu.Scroll(1), "Scrolling down clamps at the last page");
    }

    [Fact]
    public void LiveSearchValidation()
    {
        Assert.True(PanelQueryEngine.IsValidSearch(new PanelQueryOptions { Search = "wall" }), "Plain text is always a valid live search");
        Assert.True(PanelQueryEngine.IsValidSearch(new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = "> 8" }),
            "A complete comparison previews");
        Assert.True(!PanelQueryEngine.IsValidSearch(new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = "> -" }),
            "A half-typed comparison keeps the previous view");
        Assert.True(!PanelQueryEngine.IsValidSearch(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "beam-[0-" }),
            "A half-typed regular expression keeps the previous view");
        Assert.True(!PanelQueryEngine.IsValidSearch(new PanelQueryOptions { BranchPath = "0;x" }), "A malformed path is not previewed");
    }

    [Fact]
    public void PreviewSelectionIsolation()
    {
        var rows = Enumerable.Range(0, 20).Select(i => new PanelRow(i, "{0}", "Number", i.ToString(), i)).ToArray();
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", rows.Length), new PanelBranch("{1}", 0) });
        var original = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        var filtered = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = "> 10" });
        var selection = new PanelRowSelection();
        int changes = 0;
        selection.Changed += (_, __) => changes++;
        selection.Select(original, 0);
        int before = changes;
        var preview = selection.CopyForPreview();
        preview.Synchronize(filtered);
        Assert.True(preview.Count == 0 && selection.Count == 1 && selection.Anchor == 0 && changes == before,
            "A preview can hide selected rows without changing committed selection or scheduling outputs");
        preview.Select(filtered, 19);
        selection.Synchronize(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions()));
        Assert.True(selection.OrderedPositions(original).SequenceEqual(new[] { 0 }) && changes == before,
            "Cancelling a preview retains original membership, order and notifications");
        selection.Synchronize(filtered);
        Assert.True(selection.Count == 0 && changes == before + 1, "Committing a filter prunes hidden selections and notifies outputs once");
        selection.SelectAll(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions
            { SearchMode = PanelSearchMode.ListLength, DisplayFilterValue = true, CreateSet = true }), includeEmptyBranches: true);
        preview = selection.CopyForPreview();
        preview.Clear();
        Assert.True(selection.Count == 21, "A preview also isolates selected empty branches");
    }

    private static CanvasRowAction Find(CanvasRowAction[] actions, PanelRowAction action) => actions.Single(item => item.Action == action);


}
