using Xunit;
using System;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

public class FilterCanvasTests
{


    [Fact]
    public void VerifyRowActions()
    {
        var single = CanvasRowActions.Create(1, new[] { 7 }, false);
        Assert.True(single.Select(choice => choice.Action).Distinct().Count() == single.Length,
            "Row menu choices have distinct action identities");
        Assert.True(single.Single(choice => choice.Action == PanelRowAction.CopyValues).Label == "Copy value" &&
            single.Single(choice => choice.Action == PanelRowAction.SelectIndices).Label == "Select index 7 across branches",
            "Single-row action labels describe the selected item and its branch index");
        Assert.True(single.Single(choice => choice.Action == PanelRowAction.ViewFullValue).Label == "View full value" &&
            single.Single(choice => choice.Action == PanelRowAction.ViewFullValue).Description.Contains("row you right-clicked"),
            "The viewer action explicitly targets the context row, even with multiple selected rows");

        var multiple = CanvasRowActions.Create(6, new[] { 1, 3 }, true);
        Assert.True(multiple.Single(choice => choice.Action == PanelRowAction.CopyValues).Label == "Copy values" &&
            multiple.Single(choice => choice.Action == PanelRowAction.SelectIndices).Label == "Select indices 1, 3 across results",
            "Multi-row action labels preserve distinct indices and Results scope");
        var manyIndices = CanvasRowActions.Create(8, new[] { 1, 3, 5, 7 }, false);
        Assert.True(manyIndices.Single(choice => choice.Action == PanelRowAction.SelectIndices).Label == "Select 4 indices across branches",
            "Long index selections use a bounded count label");
        foreach (var choice in single)
            Assert.True(choice.Description.Length > 0 && choice.Description == multiple.Single(other => other.Action == choice.Action).Description,
                "Row action explanations stay with the action across selection changes");
    }

    [Fact]
    public void RowMenuOpensAboveTheClickNearTheViewportBottom()
    {
        Assert.Equal(100f, CanvasRowActions.MenuTop(100, 140, 0, 400));
        Assert.Equal(160f, CanvasRowActions.MenuTop(300, 140, 0, 400));
        Assert.Equal(54f, CanvasRowActions.MenuTop(100, 140, 50, 200));
    }

    [Fact]
    public void RowMenuTallerThanTheViewportScrollsBetweenItsEdges()
    {
        Assert.Equal(54f, CanvasRowActions.ScrollMenu(54, -20, 140, 50, 300));
        Assert.Equal(34f, CanvasRowActions.ScrollMenu(54, -20, 140, 50, 150));
        Assert.Equal(6f, CanvasRowActions.ScrollMenu(34, -40, 140, 50, 150));
        Assert.Equal(54f, CanvasRowActions.ScrollMenu(34, 40, 140, 50, 150));
    }

    [Fact]
    public void VerifyTextPreview()
    {
        const string unicode = "first\r\n第二行 🧪\nend𐐷";
        Assert.True(PanelTextPreview.ForTooltip(unicode) == unicode, "Short Unicode values and their line endings remain intact");
        var manyLines = string.Join("\r\n", Enumerable.Repeat("行", 40));
        var linePreview = PanelTextPreview.ForTooltip(manyLines);
        Assert.True(linePreview.EndsWith("…", StringComparison.Ordinal) &&
            linePreview.Count(character => character == '\n') < PanelTextPreview.MaximumLines &&
            !linePreview.Contains("\r…"), "Tooltip previews bound line count without splitting CRLF");
        var unicodeLines = PanelTextPreview.ForTooltip(string.Join("\u2028", Enumerable.Repeat("行", 40)));
        Assert.True(unicodeLines.EndsWith("…", StringComparison.Ordinal) &&
            unicodeLines.Count(character => character == '\u2028') < PanelTextPreview.MaximumLines,
            "Unicode line separators are also bounded in native tooltip previews");
        var astralAtBoundary = new string('x', PanelTextPreview.MaximumCharacters - 1) + "🧪tail";
        var textPreview = PanelTextPreview.ForTooltip(astralAtBoundary);
        Assert.True(textPreview.Length <= PanelTextPreview.MaximumCharacters + 1 && textPreview.EndsWith("…", StringComparison.Ordinal) &&
            !char.IsHighSurrogate(textPreview[textPreview.Length - 2]),
            "Tooltip previews bound characters without splitting a Unicode surrogate pair");
        Assert.True(PanelTextPreview.ForTooltip(new string('x', PanelTextPreview.MaximumCharacters)).Length == PanelTextPreview.MaximumCharacters,
            "An exactly fitting preview does not acquire an ellipsis");
    }

    [Fact]
    public void VerifyPreviewMenu()
    {
        var panel = new RectangleF(50, 100, 300, 110);
        var anchor = new RectangleF(160, 102, 112, 18);
        var menu = new CanvasMenu();
        var options = new PanelQueryOptions();
        menu.Toggle(CanvasMenuKind.Preview, PanelSnapshot.Empty, options);
        menu.Layout(panel, default, default, default, preview: anchor);
        menu.MarkPainted();
        Assert.True(menu.Count == 9 && menu.Visible == menu.Count, "Preview controls fit on one page without a paging row");
        foreach (PanelPreviewMode mode in Enum.GetValues(typeof(PanelPreviewMode)))
        {
            int index = (int)mode;
            Assert.True(menu.TrySelect(new PointF(menu.Bounds.X + 10, menu.Bounds.Y + index * 20 + 10), out int chosen) && chosen == index,
                "Each preview mode is directly selectable");
            Assert.True(menu.IsSelected(index, options, PanelUnitConversion.None, -1, mode) &&
                !menu.IsSelected((index + 1) % 3, options, PanelUnitConversion.None, -1, mode),
                "Only the active preview mode is checked");
        }
    }

    [Fact]
    public void VerifyCompactMenus()
    {
        var panel = new RectangleF(50, 100, 300, 110);
        var search = new RectangleF(56, 125, 88, 21);
        foreach (var kind in new[] { CanvasMenuKind.SearchMode, CanvasMenuKind.Comparison })
        {
            var menu = new CanvasMenu();
            var options = new PanelQueryOptions { SearchMode = PanelSearchMode.ListLength, Search = "!= 10" };
            menu.Toggle(kind, PanelSnapshot.Empty, options);
            menu.Layout(panel, search, search, search, search, search);
            Assert.True(menu.Bounds.Top == search.Bottom + 2 && menu.Bounds.Left >= panel.Left && menu.Bounds.Right <= panel.Right &&
                menu.Visible == Math.Min(menu.Count, 12), "Compact filter menus show up to 12 rows below their anchor, beyond the panel bottom");
            var reachable = new System.Collections.Generic.HashSet<int>();
            for (int page = 0; page < menu.Count; page++)
            {
                menu.MarkPainted();
                for (int row = 0; row < menu.Visible; row++)
                {
                    Assert.True(menu.TrySelect(new PointF(menu.Bounds.X + 10, menu.Bounds.Y + row * 20 + 10), out int selected),
                        "Visible menu choices are selectable");
                    reachable.Add(selected);
                }
                menu.TrySelect(new PointF(menu.Bounds.Right - 10, menu.Bounds.Bottom - 5), out _);
            }
            int count = kind == CanvasMenuKind.SearchMode ? Enum.GetValues(typeof(PanelSearchMode)).Length : 7;
            Assert.True(reachable.Count == count && menu.Label(count - 1).Length > 0, "Every filter and comparison remains reachable through compact paging");
            Assert.True(menu.IsSelected(kind == CanvasMenuKind.SearchMode ? 9 : 5, options, PanelUnitConversion.None, -1), "Saved mode and comparison remain visibly checked");
            if (kind == CanvasMenuKind.Comparison)
            {
                options.DisplayFilterValue = true;
                int display = Enumerable.Range(0, menu.Count).Single(index => menu.ComparisonChoiceAt(index).IsDisplayValue);
                Assert.True(menu.IsSelected(display, options, PanelUnitConversion.None, -1) &&
                    !menu.IsSelected(5, options, PanelUnitConversion.None, -1) &&
                    CanvasMenu.SearchModeChip(options.SearchMode) == "List" && CanvasMenu.SearchHint(options) == "filter…", "Show values has its own checkmark and invites filtering");
                options.DisplayFilterValue = false;
                options.Search = "5";
                Assert.True(menu.IsSelected(4, options, PanelUnitConversion.None, -1),
                    "A bare list-length threshold visibly checks implicit equality");
                options.SearchMode = PanelSearchMode.Text;
                Assert.True(!menu.IsSelected(4, options, PanelUnitConversion.None, -1),
                    "A literal numeric text search is not marked as an equality comparison");
            }
        }
        Assert.True(CanvasMenu.SearchModeChip(PanelSearchMode.Area) == "Area" && CanvasMenu.SearchModeChip(PanelSearchMode.Regex) == "Regex" &&
            CanvasMenu.SearchModeChip(PanelSearchMode.ListLength) == "List" && CanvasMenu.SearchModeChip(PanelSearchMode.VertexCount) == "Vertex count" &&
            CanvasMenu.SearchHint(new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> 12" }) == "> 12" &&
            CanvasMenu.SearchHint(new PanelQueryOptions { SearchMode = PanelSearchMode.VertexCount }) == "filter…",
            "Active filter mode is visible in the compact search control");
    }

    [Fact]
    public void VerifyBranchSelection()
    {
        var rows = Enumerable.Range(0, 100).Select(index =>
            new PanelRow(index % 50, index < 50 ? "{0}" : "{1}", "Number", index.ToString(), index)).ToArray();
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 50), new PanelBranch("{1}", 50) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "> 20", SortMode = PanelSortMode.ValueDescending });
        var selection = new PanelRowSelection();
        selection.SelectAll(result, "{0}");
        Assert.True(selection.OrderedPositions(result).SequenceEqual(Enumerable.Range(21, 29).Reverse()),
            "Branch selection includes offscreen matches and follows filtered result order");
        selection.SelectAll(result, "{missing}");
        Assert.True(selection.Count == 0, "Selecting an empty branch clears prior selection");
        selection.SelectAll(result);
        Assert.True(selection.OrderedPositions(result).SequenceEqual(result.RowPositions), "Completed branch filters can select their full result in one pass");
        var changed = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { BranchPath = "{1}" });
        Assert.True(selection.OrderedPositions(changed).Count == 50, "Subsequent filters retain only surviving selected identities");
        var newSnapshot = new PanelSnapshot(rows, snapshot.Branches);
        Assert.True(selection.OrderedPositions(PanelQueryEngine.Execute(newSnapshot, new PanelQueryOptions())).Count == 0,
            "A new solution clears all branch-selected row identities");
    }


}
