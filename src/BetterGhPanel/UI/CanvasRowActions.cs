using System.Linq;

namespace BetterGhPanel.UI
{
    internal enum PanelRowAction { FrameSelection, CopyValues, CopyGeometry, SelectIndices, ViewFullValue, NextPage, PreviousPage, Unpainted }

    internal readonly struct CanvasRowAction
    {
        internal CanvasRowAction(PanelRowAction action, string label, string description)
        { Action = action; Label = label; Description = description; }

        internal PanelRowAction Action { get; }
        internal string Label { get; }
        internal string Description { get; }
    }

    // The choice carries its identity, so changing menu order cannot change its action or tooltip.
    internal static class CanvasRowActions
    {
        internal const float RowHeight = 20;

        internal static CanvasRowAction[] Create(int selectedCount, int[] indices, bool filtered) => new[]
        {
            new CanvasRowAction(PanelRowAction.FrameSelection, "Frame selection in Rhino",
                "Fit the selected geometry in Rhino's active viewport."),
            new CanvasRowAction(PanelRowAction.CopyValues, selectedCount == 1 ? "Copy value" : "Copy values",
                "Copy one value per line in the current displayed order, using the selected unit conversion and decimal formatting. Geometry measurements such as length, area and volume copy as numbers without unit suffixes. Copied numbers use a decimal point for pasting."),
            new CanvasRowAction(PanelRowAction.CopyGeometry, "Copy geometry for Rhino",
                "Copy the selected supported geometry to the clipboard, then paste it into Rhino. Other rows are skipped. Model geometry keeps its original dimensions."),
            new CanvasRowAction(PanelRowAction.ViewFullValue, "View full value",
                "Open the complete value of the row you right-clicked in a scrollable, read-only window. The selection and outputs stay unchanged."),
            new CanvasRowAction(PanelRowAction.SelectIndices,
                "Select " + IndexLabel(indices).ToLowerInvariant() + (filtered ? " across filtered results" : " across branches"),
                "Select the same zero-based item indices in every branch of the filtered results, including offscreen rows. Uses the tree after tree tools, before filtering or sorting. Shorter branches are skipped. Alt/Option-click a row to select its index across branches.")
        };

        // The minimum Half panel fits four 20-pixel rows. Keep the viewer on
        // the first page and retain every existing action on the second page.
        internal static bool NeedsPages(float availableHeight) => availableHeight < 5 * RowHeight;

        internal static CanvasRowAction[] Page(CanvasRowAction[] actions, int page) => page == 0
            ? new[] { Find(actions, PanelRowAction.FrameSelection), Find(actions, PanelRowAction.CopyValues), Find(actions, PanelRowAction.ViewFullValue),
                new CanvasRowAction(PanelRowAction.NextPage, "More actions…", "Show the remaining row actions.") }
            : new[] { Find(actions, PanelRowAction.CopyGeometry), Find(actions, PanelRowAction.SelectIndices),
                new CanvasRowAction(PanelRowAction.PreviousPage, "Previous actions…", "Return to the first row actions.") };

        private static CanvasRowAction Find(CanvasRowAction[] actions, PanelRowAction action) => actions.Single(item => item.Action == action);

        internal static string IndexLabel(int[] indices) => indices.Length == 1 ? "Index " + indices[0] :
            indices.Length <= 3 ? "Indices " + string.Join(", ", indices) : indices.Length.ToString("N0") + " indices";
    }
}
