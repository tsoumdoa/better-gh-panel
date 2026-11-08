using System;

namespace BetterGhPanel.Core
{

    internal readonly struct CanvasRowAction
    {
        internal CanvasRowAction(PanelRowAction action, string label, string description, string? disabledReason = null)
        { Action = action; Label = label; Description = description; DisabledReason = disabledReason; }

        internal PanelRowAction Action { get; }
        internal string Label { get; }
        internal string Description { get; }
        // Unavailable actions stay visible, greyed out, so the menu keeps a stable shape.
        internal string? DisabledReason { get; }
        internal bool Enabled => DisabledReason == null;
    }

    // The choice carries its identity, so changing menu order cannot change its action or tooltip.
    internal static class CanvasRowActions
    {
        internal const float RowHeight = 20;
        internal const string NoGeometry = "The selection contains no geometry.";
        internal const string GeometryCopyUnsupported = "Copying geometry is not supported on Mac yet.";

        internal static CanvasRowAction[] Create(int selectedCount, int[] indices, bool filtered, bool hasGeometry = true, bool canCopyGeometry = true,
            bool setItems = false) => new[]
        {
            new CanvasRowAction(PanelRowAction.FrameSelection, "Frame selection in Rhino",
                "Zoom Rhino's active viewport to the selected geometry.", hasGeometry ? null : NoGeometry),
            new CanvasRowAction(PanelRowAction.CopyValues, "Copy value" + (selectedCount == 1 ? "" : "s"),
                "One value per line, in displayed order and formatting. Measurements copy as plain numbers."),
            new CanvasRowAction(PanelRowAction.CopyAddresses, (setItems ? "Copy D branch address" : "Copy address") + (selectedCount == 1 ? "" : "es"),
                setItems ? "Copy the D output branch {path;k} (or {k} with Flatten), one address per selected set row." : "Copy {path}(index), one address per selected row."),
            new CanvasRowAction(PanelRowAction.CopyTable, "Copy as table", "Copy Path, Index and every visible column as a tab-separated table."),
            new CanvasRowAction(PanelRowAction.CopyGeometry, "Copy geometry for Rhino",
                "Copy the selected geometry, then Paste in Rhino. Other rows are skipped.",
                !canCopyGeometry ? GeometryCopyUnsupported : hasGeometry ? null : NoGeometry),
            new CanvasRowAction(PanelRowAction.ViewFullValue, "View full value",
                "Open the value of the row you right-clicked in a scrollable, read-only window."),
            new CanvasRowAction(PanelRowAction.SelectIndices,
                "Select " + IndexLabel(indices).ToLowerInvariant() + (filtered ? " across results" : " across branches"),
                "Select the same zero-based item indices in every branch, including offscreen rows. Shortcut: Alt/Option-click.")
        };

        // The row menu opens below the click and may extend beyond the panel. Near
        // the bottom of the viewport it opens above the click, so every action stays reachable.
        internal static float MenuTop(float anchorY, float height, float viewportTop, float viewportBottom) =>
            anchorY + height <= viewportBottom - 4 ? anchorY : Math.Max(viewportTop + 4, anchorY - height);

        // A menu taller than the viewport scrolls between its top and bottom edges.
        internal static float ScrollMenu(float top, float shift, float height, float viewportTop, float viewportBottom) =>
            height + 8 <= viewportBottom - viewportTop ? top : Math.Min(viewportTop + 4, Math.Max(viewportBottom - height - 4, top + shift));

        internal static string IndexLabel(int[] indices) => indices.Length == 1 ? "Index " + indices[0] :
            indices.Length <= 3 ? "Indices " + string.Join(", ", indices) : indices.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " indices";
    }
}
