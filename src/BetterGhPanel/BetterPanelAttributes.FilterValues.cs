using System;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private void RenderListLengths(Graphics graphics, PanelQueryResult result, Font font, Font small, Font mono, StringFormat trim)
        {
            var pane = _d;
            var list = pane.List;
            var track = pane.Track;
            ref int offset = ref pane.Offset;
            ref int shown = ref pane.Shown;
            offset = Math.Max(0, Math.Min(Math.Max(0, Count - 1), offset));
            using var renderer = new CanvasRowRenderer(graphics, result.Snapshot.Branches.Count, false);
            if (TableVisible) { PrepareTable(graphics, result, font, small, renderer.GutterWidth); DrawTableHeader(graphics, small, trim, renderer.GutterWidth); }
            var selected = _selection.BranchPaths(result);
            bool set = result.DisplaySet?.IsBranchSet == true;
            float y = list.Y;
            // A header fits in Full and ordinary Half layouts; minimum Half still
            // leaves room for a branch and its explicitly labeled count.
            if (!TableVisible && list.Height >= BranchHeight + RowHeight)
            {
                Fill(graphics, PanelTheme.Rgb(236, 241, 235), new RectangleF(list.X, y, list.Width, BranchHeight));
                Text(graphics, "Branch", small, Accent, new RectangleF(list.X + 6, y, list.Width / 2, BranchHeight), trim);
                Text(graphics, "List length", small, Accent,
                    new RectangleF(list.Right - 90 - (set ? 40 : 0), y, 84, BranchHeight), trim);
                y += BranchHeight;
            }
            for (int index = offset; index < result.BranchPaths.Count && y + RowHeight <= list.Bottom; index++)
            {
                string path = result.BranchPaths[index];
                var bounds = new RectangleF(list.X, y, list.Width, RowHeight);
                bool isSelected = selected.Contains(path);
                Fill(graphics, isSelected ? PanelTheme.Rgb(216, 232, 219) : (shown & 1) == 0 ? PanelTheme.Background : PanelTheme.Rgb(247, 249, 246), bounds);
                // The selection bar owns the edge; validity marks always start beside it.
                if (isSelected) Fill(graphics, Accent, new RectangleF(bounds.X, bounds.Y, 2, bounds.Height));
                if (set && result.DisplaySet!.BranchSourceStatuses.TryGetValue(path, out var statuses))
                {
                    if (statuses.VerifiedInvalid > 0) Fill(graphics, PanelTheme.Rgb(180, 106, 67), new RectangleF(bounds.X + 2, bounds.Y + 3, 2, bounds.Height - 6));
                    if (statuses.Unverified > 0) Fill(graphics, PanelTheme.Rgb(103, 112, 166), new RectangleF(bounds.X + (statuses.VerifiedInvalid > 0 ? 4 : 2), bounds.Y + 3, 2, bounds.Height - 6));
                }
                var dataBounds = TableVisible ? new RectangleF(bounds.X, bounds.Y, _frozenWidth, bounds.Height) : bounds;
                float badgeWidth = set ? 40 : 0;
                string label = set ? result.DisplaySet!.BranchIndices[path] + " · " + path : path;
                var labelBounds = new RectangleF(bounds.X + 6, y, Math.Max(0, dataBounds.Width - (TableVisible ? 76 : 120) - badgeWidth), RowHeight);
                renderer.DrawCell(graphics, label, mono, Accent, labelBounds);
                int count = result.Snapshot.BranchItemCounts[path];
                renderer.DrawValue(graphics, count.ToString(System.Globalization.CultureInfo.InvariantCulture) + (count == 1 ? " item" : " items"),
                    small, result.Warnings.IsOutlier(result.Snapshot, path) ? PanelTheme.Warning : Ink, new RectangleF(dataBounds.Right - (TableVisible ? 70 : 96) - badgeWidth, y, TableVisible ? 64 : 90, RowHeight), true);
                if (set)
                    renderer.DrawValue(graphics, result.DisplaySet!.BranchOccurrences[path].Count + "×", small, Accent,
                        new RectangleF(dataBounds.Right - badgeWidth, y, badgeWidth - 6, RowHeight), true);
                DrawBranchExtras(graphics, result, bounds, path, mono, small, trim, label: label, labelBounds: labelBounds);
                if (TableVisible)
                    for (int column = 0; column < PanelOwner.TableColumns.Count; column++)
                    {
                        string text = ColumnBranchCell(path, column);
                        PaintColumn(graphics, column + 1, y, RowHeight, cell => DrawTableCell(graphics, renderer, null, text, cell, font, small));
                    }
                shown++;
                y += RowHeight;
            }
            if (Count == 0)
                Text(graphics, result.Snapshot.Branches.Count == 0 ? "Connect a tree to start exploring." : "No matching branches. Clear a filter or Reset all.",
                    small, Muted, new RectangleF(list.X + 6, list.Y, list.Width - 12, RowHeight), trim);
            if (TableVisible && _horizontalMaximum > 0) { Fill(graphics, PanelTheme.Rgb(232, 236, 229), _horizontalTrack); Fill(graphics, PanelTheme.Rgb(153, 174, 156), HorizontalThumb()); }
            if (Count > shown && !track.IsEmpty)
            {
                Fill(graphics, PanelTheme.Rgb(232, 236, 229), track);
                Fill(graphics, PanelTheme.Rgb(153, 174, 156), Thumb());
            }
        }
    }
}
