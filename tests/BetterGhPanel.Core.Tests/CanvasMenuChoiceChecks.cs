using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

internal static class CanvasMenuChoiceChecks
{
    internal static void Run()
    {
        SearchCapabilitiesAndPaging();
        ChoiceIdentityAndPersistedValues();
    }

    private static void SearchCapabilitiesAndPaging()
    {
        var snapshot = new PanelSnapshot(Array.Empty<PanelRow>(), Array.Empty<PanelBranch>());
        var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Area, Search = "> 7", DisplayFilterValue = false };
        var supported = new HashSet<PanelSearchMode> { PanelSearchMode.Text, PanelSearchMode.Regex, PanelSearchMode.Length, PanelSearchMode.ListLength };
        var menu = new CanvasMenu();
        menu.Toggle(CanvasMenuKind.SearchMode, snapshot, options, supportedSearchModes: supported);
        supported.Clear(); // An open menu captures its own choice identities.
        var expected = new[] { PanelSearchMode.Text, PanelSearchMode.Regex, PanelSearchMode.Length, PanelSearchMode.Area, PanelSearchMode.ListLength };
        Check(Enumerable.Range(0, menu.Count).Select(menu.SearchModeAt).SequenceEqual(expected),
            "Unavailable earlier modes are omitted, while the saved area mode remains in canonical order");
        Check(menu.IsSelected(3, options, PanelUnitConversion.None, -1) && menu.Label(3) == "Geometry · area" &&
            options.SearchMode == PanelSearchMode.Area && options.Search == "> 7" && !options.DisplayFilterValue,
            "Keeping an unavailable saved mode does not change its expression or display setting");
        menu.Layout(new RectangleF(0, 0, 300, 110), default, default, default, search: new RectangleF(12, 24, 100, 20));
        var clicked = new List<PanelSearchMode>();
        int previousOffset = -1;
        while (menu.Offset != previousOffset)
        {
            previousOffset = menu.Offset;
            for (int row = 0; row < menu.Visible && menu.Offset + row < menu.Count; row++)
            {
                Check(menu.TrySelect(new PointF(menu.Bounds.Left + 10, menu.Bounds.Top + row * 20 + 10), out int index),
                    "A visible search choice is selectable");
                clicked.Add(menu.SearchModeAt(index));
            }
            Check(!menu.TrySelect(new PointF(menu.Bounds.Right - 5, menu.Bounds.Bottom - 5), out _),
                "Paging does not select a search choice");
        }
        Check(clicked.Distinct().SequenceEqual(expected), "Paged clicks resolve the actual modes, including choices following omitted modes");
        Check(menu.Offset > 0, "Compact search menu reaches a later page");
        menu.TrySelect(new PointF(menu.Bounds.Left + 5, menu.Bounds.Bottom - 5), out _);
        Check(menu.Offset < previousOffset, "Previous page returns toward the first search choices");
        Check(menu.IsCurrent(snapshot) && !menu.IsCurrent(new PanelSnapshot(Array.Empty<PanelRow>(), Array.Empty<PanelBranch>())),
            "A menu cannot act on a replacement snapshot");
        menu.Close();
        menu.Toggle(CanvasMenuKind.SearchMode, snapshot, options, supportedSearchModes: Array.Empty<PanelSearchMode>());
        Check(menu.Count == 1 && menu.SearchModeAt(0) == PanelSearchMode.Area && menu.IsSelected(0, options, PanelUnitConversion.None, -1),
            "Even an empty supported set retains the active saved mode");
        menu.Close();
        menu.Toggle(CanvasMenuKind.SearchMode, snapshot, new PanelQueryOptions());
        Check(Enumerable.Range(0, menu.Count).Select(menu.SearchModeAt).SequenceEqual(
            new[] { PanelSearchMode.Text, PanelSearchMode.Regex, PanelSearchMode.Value, PanelSearchMode.Length, PanelSearchMode.Area, PanelSearchMode.Volume, PanelSearchMode.ListLength }),
            "The default remains the existing complete search menu");
    }

    private static void ChoiceIdentityAndPersistedValues()
    {
        Check((int)PanelSearchMode.Text == 0 && (int)PanelSearchMode.Regex == 1 && (int)PanelSearchMode.Value == 2 &&
            (int)PanelSearchMode.Length == 3 && (int)PanelSearchMode.Area == 4 && (int)PanelSearchMode.Volume == 5 && (int)PanelSearchMode.ListLength == 6,
            "Persisted search mode values remain unchanged");
        Check((int)PanelSortMode.SourceOrder == 0 && (int)PanelSortMode.ValueAscending == 1 && (int)PanelSortMode.ValueDescending == 2 &&
            (int)PanelPreviewMode.Selected == 0 && (int)PanelPreviewMode.FilteredResults == 1 && (int)PanelPreviewMode.Off == 2,
            "Persisted sort and preview values remain unchanged");
        var conversions = new[] { PanelUnitConversion.None, PanelUnitConversion.MillimetersToMeters, PanelUnitConversion.MetersToMillimeters,
            PanelUnitConversion.SquareMillimetersToSquareMeters, PanelUnitConversion.SquareMetersToSquareMillimeters,
            PanelUnitConversion.RadiansToDegrees, PanelUnitConversion.DegreesToRadians,
            PanelUnitConversion.CubicMillimetersToCubicMeters, PanelUnitConversion.CubicMetersToCubicMillimeters };
        Check(conversions.Select(value => (int)value).SequenceEqual(Enumerable.Range(0, conversions.Length)),
            "Persisted unit conversion values remain unchanged");
        var menu = new CanvasMenu();
        var options = new PanelQueryOptions();
        menu.Toggle(CanvasMenuKind.Sort, PanelSnapshot.Empty, options);
        foreach (PanelSortMode mode in Enum.GetValues(typeof(PanelSortMode)))
        {
            int index = Enumerable.Range(0, menu.Count).Single(i => menu.SortModeAt(i) == mode);
            options.SortMode = mode;
            Check(menu.IsSelected(index, options, PanelUnitConversion.None, -1), "Sort check follows its typed identity");
        }
        menu.Close();
        menu.Toggle(CanvasMenuKind.Preview, PanelSnapshot.Empty, options);
        foreach (PanelPreviewMode mode in Enum.GetValues(typeof(PanelPreviewMode)))
        {
            int index = Enumerable.Range(0, menu.Count).Single(i => menu.PreviewChoiceAt(i).Mode == mode);
            Check(menu.IsSelected(index, options, PanelUnitConversion.None, -1, mode), "Preview check follows its typed identity");
        }
        int itemOrder = Enumerable.Range(0, menu.Count).Single(i => menu.PreviewChoiceAt(i).IsItemOrder);
        Check(menu.IsSectionStart(itemOrder) && menu.IsSelected(itemOrder, options, PanelUnitConversion.None, -1, PanelPreviewMode.Off, true),
            "Item-order action has a distinct identity and checkmark");
        menu.Close();
        menu.Toggle(CanvasMenuKind.Units, PanelSnapshot.Empty, options);
        foreach (var conversion in conversions)
        {
            int index = Enumerable.Range(0, menu.Count).Single(i => menu.UnitChoiceAt(i).Conversion == conversion);
            Check(menu.Label(index) == PanelUnits.Label(conversion) && menu.IsSelected(index, options, conversion, -1),
                "Each unit choice retains the corresponding conversion and label");
        }
        int automatic = Enumerable.Range(0, menu.Count).Single(i => menu.UnitChoiceAt(i).Decimals == CanvasDecimalChoice.Automatic);
        int custom = Enumerable.Range(0, menu.Count).Single(i => menu.UnitChoiceAt(i).Decimals == CanvasDecimalChoice.Custom);
        Check(menu.IsSectionStart(automatic) && menu.IsSelected(automatic, options, PanelUnitConversion.None, -1) &&
            menu.IsSelected(custom, options, PanelUnitConversion.None, 2) && menu.Label(custom, 2) == "Decimal places… (2)",
            "Decimal actions do not alias a unit conversion");
        Check(PanelUnits.Convert(1000000000, PanelUnitConversion.CubicMillimetersToCubicMeters) == 1 &&
            PanelUnits.Convert(180, PanelUnitConversion.DegreesToRadians) == Math.PI,
            "Typed unit choices leave conversion arithmetic unchanged");
        menu.Close();
        menu.Toggle(CanvasMenuKind.Comparison, PanelSnapshot.Empty, options);
        foreach (CanvasComparison comparison in Enum.GetValues(typeof(CanvasComparison)))
        {
            int index = Enumerable.Range(0, menu.Count).Single(i => menu.ComparisonChoiceAt(i).Comparison == comparison);
            options.SearchMode = PanelSearchMode.Value;
            options.Search = CanvasMenu.ComparisonOperator(comparison) + " 5";
            Check(menu.IsSelected(index, options, PanelUnitConversion.None, -1), "Comparison check follows its typed operator");
        }
        int display = Enumerable.Range(0, menu.Count).Single(i => menu.ComparisonChoiceAt(i).IsDisplayValue);
        options.DisplayFilterValue = true;
        Check(menu.IsSectionStart(display) && menu.IsSelected(display, options, PanelUnitConversion.None, -1),
            "Display value has its own action identity");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
