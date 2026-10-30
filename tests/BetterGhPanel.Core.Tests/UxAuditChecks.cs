using System;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

// Pure policy behind the UI/UX audit fixes: greyed-out row actions, Paths
// type-to-find, keyboard/wheel menu navigation and live-search validation.
internal static class UxAuditChecks
{
    internal static void Run()
    {
        RowActionAvailability();
        BranchMenuFind();
        MenuKeyboardAndWheel();
        LiveSearchValidation();
    }

    private static void RowActionAvailability()
    {
        var full = CanvasRowActions.Create(2, new[] { 0 }, false);
        Check(full.All(action => action.Enabled), "Geometry selections on Windows keep every row action available");
        var text = CanvasRowActions.Create(2, new[] { 0 }, false, hasGeometry: false);
        Check(!Find(text, PanelRowAction.FrameSelection).Enabled && !Find(text, PanelRowAction.CopyGeometry).Enabled &&
            Find(text, PanelRowAction.CopyValues).Enabled && Find(text, PanelRowAction.ViewFullValue).Enabled,
            "Selections without geometry grey out framing and geometry copy but keep value actions");
        Check(Find(text, PanelRowAction.FrameSelection).DisabledReason == CanvasRowActions.NoGeometry,
            "Disabled actions explain why");
        var mac = CanvasRowActions.Create(1, new[] { 0 }, false, hasGeometry: true, canCopyGeometry: false);
        Check(Find(mac, PanelRowAction.FrameSelection).Enabled &&
            Find(mac, PanelRowAction.CopyGeometry).DisabledReason == CanvasRowActions.GeometryCopyUnsupported,
            "Unsupported geometry copy is greyed out with its platform reason");
        Check(text.Select(action => action.Action).SequenceEqual(full.Select(action => action.Action)),
            "Disabled actions stay in place so the menu keeps a stable shape");
    }

    private static void BranchMenuFind()
    {
        var branches = new[] { "{0;0}", "{0;1}", "{0;12}", "{1;2}", "{2;0}" }.Select(path => new PanelBranch(path, 1)).ToArray();
        var snapshot = new PanelSnapshot(Array.Empty<PanelRow>(), branches);
        var menu = new CanvasMenu();
        menu.Toggle(CanvasMenuKind.Branch, snapshot, new PanelQueryOptions());
        menu.Layout(new RectangleF(0, 0, 360, 280), new RectangleF(9, 60, 226, 22), default, new RectangleF(9, 60, 226, 22));
        Check(menu.Count == 7 && menu.Label(1) == "Jump to exact path…", "An unfiltered Paths menu lists every branch");
        menu.SetBranchFilter("0;1");
        Check(menu.Count == 4 && menu.BranchPathAt(2) == "{0;1}" && menu.BranchPathAt(3) == "{0;12}",
            "Typed digits narrow the list to paths containing them, ignoring braces");
        Check(menu.HoveredIndex == 2 && menu.Label(1).StartsWith("Jump to {0;1}", StringComparison.Ordinal),
            "The first match is highlighted and the jump row names the typed path");
        Check(menu.Label(2).StartsWith("{0;1}", StringComparison.Ordinal), "Labels follow the filtered branch order");
        menu.SetBranchFilter("9");
        Check(menu.Count == 2 && menu.HoveredIndex == 1, "No matches leave the exact-path jump available");
        menu.SetBranchFilter(string.Empty);
        Check(menu.Count == 7 && menu.BranchPathAt(6) == "{2;0}", "Clearing the typed filter restores every branch");
        menu.SetBranchFilter("1");
        menu.Close();
        menu.Toggle(CanvasMenuKind.Branch, snapshot, new PanelQueryOptions());
        Check(menu.BranchFilter.Length == 0 && menu.Count == 7, "Reopening the menu starts without a stale filter");
    }

    private static void MenuKeyboardAndWheel()
    {
        var branches = Enumerable.Range(0, 30).Select(i => new PanelBranch("{" + i + "}", 0)).ToArray();
        var snapshot = new PanelSnapshot(Array.Empty<PanelRow>(), branches);
        var menu = new CanvasMenu();
        var anchor = new RectangleF(9, 60, 226, 22);
        menu.Toggle(CanvasMenuKind.Branch, snapshot, new PanelQueryOptions());
        menu.Layout(new RectangleF(0, 0, 360, 280), anchor, anchor, anchor);
        int visible = menu.Visible;
        Check(visible < menu.Count, "The fixture overflows the menu");
        menu.MoveHover(1);
        Check(menu.HoveredIndex == 0 && menu.Offset == 0, "Down starts at the first entry");
        for (int i = 0; i < visible; i++) menu.MoveHover(1);
        Check(menu.HoveredIndex == visible && menu.Offset == 1, "Moving past the last visible row scrolls by one");
        menu.MoveHover(-100);
        Check(menu.HoveredIndex == 0 && menu.Offset == 0, "Up clamps at the first entry");
        Check(menu.Scroll(3) && menu.Offset == 3 && menu.HoveredIndex == -1, "The wheel scrolls the menu and clears the hover");
        Check(!menu.Scroll(-10) || menu.Offset == 0, "Scrolling up clamps at the top");
        menu.Scroll(1000);
        Check(menu.Offset == menu.Count - menu.Visible && !menu.Scroll(1), "Scrolling down clamps at the last page");
    }

    private static void LiveSearchValidation()
    {
        Check(PanelQueryEngine.IsValidSearch(new PanelQueryOptions { Search = "wall" }), "Plain text is always a valid live search");
        Check(PanelQueryEngine.IsValidSearch(new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = "> 8" }),
            "A complete comparison previews");
        Check(!PanelQueryEngine.IsValidSearch(new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = "> -" }),
            "A half-typed comparison keeps the previous view");
        Check(!PanelQueryEngine.IsValidSearch(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "beam-[0-" }),
            "A half-typed regular expression keeps the previous view");
        Check(!PanelQueryEngine.IsValidSearch(new PanelQueryOptions { BranchPath = "0;x" }), "A malformed path is not previewed");
    }

    private static CanvasRowAction Find(CanvasRowAction[] actions, PanelRowAction action) => actions.Single(item => item.Action == action);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
