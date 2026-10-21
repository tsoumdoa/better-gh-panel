namespace BetterGhPanel.UI
{
    internal enum PanelRowAction { FrameSelection, CopyValues, CopyGeometry, SelectIndices }

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
        internal static CanvasRowAction[] Create(int selectedCount, int[] indices, bool filtered) => new[]
        {
            new CanvasRowAction(PanelRowAction.FrameSelection, "Frame selection in Rhino",
                "Fit the selected geometry in Rhino's active viewport."),
            new CanvasRowAction(PanelRowAction.CopyValues, selectedCount == 1 ? "Copy value" : "Copy values",
                "Copy one value per line in the current displayed order, using the selected unit conversion and decimal formatting. Geometry measurements such as length, area and volume copy as numbers without unit suffixes. Copied numbers use a decimal point for pasting."),
            new CanvasRowAction(PanelRowAction.CopyGeometry, "Copy geometry for Rhino",
                "Copy the selected supported geometry to the clipboard, then paste it into Rhino. Other rows are skipped. Model geometry keeps its original dimensions."),
            new CanvasRowAction(PanelRowAction.SelectIndices,
                "Select " + IndexLabel(indices).ToLowerInvariant() + (filtered ? " across filtered results" : " across branches"),
                "Select the same zero-based item indices in every branch of the filtered results, including offscreen rows. Uses the tree after tree tools, before filtering or sorting. Shorter branches are skipped. Alt/Option-click a row to select its index across branches.")
        };

        internal static string IndexLabel(int[] indices) => indices.Length == 1 ? "Index " + indices[0] :
            indices.Length <= 3 ? "Indices " + string.Join(", ", indices) : indices.Length.ToString("N0") + " indices";
    }
}
