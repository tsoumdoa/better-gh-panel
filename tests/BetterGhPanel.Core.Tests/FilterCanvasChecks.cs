using System;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

internal static class FilterCanvasChecks
{
    internal static void Run()
    {
        VerifyCompactMenus();
        VerifyBranchSelection();
        VerifyPreviewMenu();
    }

    private static void VerifyPreviewMenu()
    {
        var panel = new RectangleF(50, 100, 300, 110);
        var anchor = new RectangleF(160, 102, 112, 18);
        var menu = new CanvasMenu();
        var options = new PanelQueryOptions();
        menu.Toggle(CanvasMenuKind.Preview, PanelSnapshot.Empty, options);
        menu.Layout(panel, default, default, default, preview: anchor);
        Require(panel.Contains(menu.Bounds) && menu.Visible == 4, "All preview scopes and the item-order toggle fit the minimum Half panel");
        foreach (PanelPreviewMode mode in Enum.GetValues(typeof(PanelPreviewMode)))
        {
            int index = (int)mode;
            Require(menu.TrySelect(new PointF(menu.Bounds.X + 10, menu.Bounds.Y + index * 20 + 10), out int chosen) && chosen == index,
                "Each preview mode is directly selectable");
            Require(menu.IsSelected(index, options, PanelUnitConversion.None, -1, mode) &&
                !menu.IsSelected((index + 1) % 3, options, PanelUnitConversion.None, -1, mode),
                "Only the active preview mode is checked");
        }
    }

    private static void VerifyCompactMenus()
    {
        var panel = new RectangleF(50, 100, 300, 110);
        var search = new RectangleF(56, 125, 88, 21);
        foreach (var kind in new[] { CanvasMenuKind.SearchMode, CanvasMenuKind.Comparison })
        {
            var menu = new CanvasMenu();
            var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Volume, Search = "!= 10" };
            menu.Toggle(kind, PanelSnapshot.Empty, options);
            menu.Layout(panel, search, search, search, search, search);
            Require(panel.Contains(menu.Bounds), "Compact filter menus stay inside the panel");
            var reachable = new System.Collections.Generic.HashSet<int>();
            for (int page = 0; page < menu.Count; page++)
            {
                for (int row = 0; row < menu.Visible; row++)
                {
                    Require(menu.TrySelect(new PointF(menu.Bounds.X + 10, menu.Bounds.Y + row * 20 + 10), out int selected),
                        "Visible menu choices are selectable");
                    reachable.Add(selected);
                }
                menu.TrySelect(new PointF(menu.Bounds.Right - 10, menu.Bounds.Bottom - 5), out _);
            }
            Require(reachable.Count == 6 && menu.Label(5).Length > 0, "Every filter and comparison remains reachable through compact paging");
            Require(menu.IsSelected(5, options, PanelUnitConversion.None, -1), "Saved mode and comparison remain visibly checked");
        }
        Require(CanvasMenu.WithComparison("== 12.5", 0) == "> 12.5" &&
            CanvasMenu.WithComparison("<= -1e3", 5) == "!= -1e3", "Changing operators preserves a finite numeric threshold");
        Require(CanvasMenu.WithComparison("Wall.*", 1) == ">= " && CanvasMenu.WithComparison("NaN", 0) == "> ",
            "Switching from text or nonfinite input starts with a clean numeric threshold");
        Require(CanvasMenu.SearchHint(new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> 12" }) == "Area > 12" &&
            CanvasMenu.SearchHint(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "Wall" }) == ".* Wall",
            "Active filter mode is visible in the compact search control");
    }

    private static void VerifyBranchSelection()
    {
        var rows = Enumerable.Range(0, 100).Select(index =>
            new PanelRow(index % 50, index < 50 ? "{0}" : "{1}", "Number", index.ToString(), index)).ToArray();
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 50), new PanelBranch("{1}", 50) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "> 20", SortMode = PanelSortMode.ValueDescending });
        var selection = new PanelRowSelection();
        selection.SelectAll(result, "{0}");
        Require(selection.OrderedPositions(result).SequenceEqual(Enumerable.Range(21, 29).Reverse()),
            "Branch selection includes offscreen matches and follows filtered result order");
        selection.SelectAll(result, "{missing}");
        Require(selection.Count == 0, "Selecting an empty branch clears prior selection");
        selection.SelectAll(result);
        Require(selection.OrderedPositions(result).SequenceEqual(result.RowPositions), "Completed branch filters can select their full result in one pass");
        var changed = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { BranchPath = "{1}" });
        Require(selection.OrderedPositions(changed).Count == 50, "Subsequent filters retain only surviving selected identities");
        var newSnapshot = new PanelSnapshot(rows, snapshot.Branches);
        Require(selection.OrderedPositions(PanelQueryEngine.Execute(newSnapshot, new PanelQueryOptions())).Count == 0,
            "A new solution clears all branch-selected row identities");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
