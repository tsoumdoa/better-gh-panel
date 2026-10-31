using System.Linq;

namespace BetterGhPanel.UI
{
    internal enum PanelRowAction { FrameSelection, CopyValues, CopyGeometry, SelectIndices, ViewFullValue, NextPage, PreviousPage, Unpainted }

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
            bool paired = false) => new[]
        {
            new CanvasRowAction(PanelRowAction.FrameSelection, "Frame selection in Rhino",
                "Zoom Rhino's active viewport to the selected geometry.", hasGeometry ? null : NoGeometry),
            // Value actions on the F pane read F; geometry actions always use D.
            new CanvasRowAction(PanelRowAction.CopyValues, (paired ? "Copy F value" : "Copy value") + (selectedCount == 1 ? "" : "s"),
                "One value per line, in displayed order and formatting. Measurements copy as plain numbers."),
            new CanvasRowAction(PanelRowAction.CopyGeometry, "Copy geometry for Rhino",
                "Copy the selected geometry, then Paste in Rhino. Other rows are skipped.",
                !canCopyGeometry ? GeometryCopyUnsupported : hasGeometry ? null : NoGeometry),
            new CanvasRowAction(PanelRowAction.ViewFullValue, paired ? "View full F value" : "View full value",
                "Open the value of the row you right-clicked in a scrollable, read-only window."),
            new CanvasRowAction(PanelRowAction.SelectIndices,
                "Select " + IndexLabel(indices).ToLowerInvariant() + (filtered ? " across filtered results" : " across branches"),
                "Select the same zero-based item indices in every branch, including offscreen rows. Shortcut: Alt/Option-click.")
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
