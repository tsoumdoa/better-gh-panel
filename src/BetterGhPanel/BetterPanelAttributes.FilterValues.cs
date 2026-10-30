using System;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private void RenderListLengths(Graphics graphics, PanelQueryResult result, Font small, Font mono, StringFormat trim)
        {
            _offset = Math.Max(0, Math.Min(Math.Max(0, Count - 1), _offset));
            using var renderer = new CanvasRowRenderer(graphics, result.Snapshot.Branches.Count, false);
            var selected = _selection.BranchPaths(result);
            bool set = result.DisplaySet?.IsBranchSet == true;
            float y = _list.Y;
            // A header fits in Full and ordinary Half layouts; minimum Half still
            // leaves room for a branch and its explicitly labeled count.
            if (_list.Height >= BranchHeight + RowHeight)
            {
                Fill(graphics, PanelTheme.Rgb(236, 241, 235), new RectangleF(_list.X, y, _list.Width, BranchHeight));
                Text(graphics, "Branch", small, Accent, new RectangleF(_list.X + 6, y, _list.Width / 2, BranchHeight), trim);
                Text(graphics, "List length", small, Accent, new RectangleF(_list.Right - 90 - (set ? 40 : 0), y, 84, BranchHeight), trim);
                y += BranchHeight;
            }
            for (int index = _offset; index < result.BranchPaths.Count && y + RowHeight <= _list.Bottom; index++)
            {
                string path = result.BranchPaths[index];
                var bounds = new RectangleF(_list.X, y, _list.Width, RowHeight);
                Fill(graphics, selected.Contains(path) ? PanelTheme.Rgb(216, 232, 219) : (_shown & 1) == 0 ? PanelTheme.Background : PanelTheme.Rgb(247, 249, 246), bounds);
                if (set && result.DisplaySet!.BranchSourceStatuses.TryGetValue(path, out var statuses))
                {
                    if (statuses.VerifiedInvalid > 0) Fill(graphics, PanelTheme.Rgb(180, 106, 67), new RectangleF(bounds.X, bounds.Y + 3, 2, bounds.Height - 6));
                    if (statuses.Unverified > 0) Fill(graphics, PanelTheme.Rgb(103, 112, 166), new RectangleF(bounds.X + (statuses.VerifiedInvalid > 0 ? 2 : 0), bounds.Y + 3, 2, bounds.Height - 6));
                }
                float badgeWidth = set ? 40 : 0;
                renderer.DrawCell(graphics, path, mono, Accent, new RectangleF(bounds.X + 6, y, Math.Max(0, bounds.Width - 102 - badgeWidth), RowHeight));
                int count = result.Snapshot.BranchItemCounts[path];
                renderer.DrawValue(graphics, count.ToString(System.Globalization.CultureInfo.InvariantCulture) + (count == 1 ? " item" : " items"),
                    small, Ink, new RectangleF(bounds.Right - 96 - badgeWidth, y, 90, RowHeight), true);
                if (set)
                    renderer.DrawValue(graphics, result.DisplaySet!.BranchOccurrences[path].Count + "×", small, Accent,
                        new RectangleF(bounds.Right - badgeWidth, y, badgeWidth - 6, RowHeight), true);
                if (TargetPainted(graphics, bounds)) AddBranchHit(bounds, path);
                _shown++;
                y += RowHeight;
            }
            if (Count == 0)
                Text(graphics, result.Snapshot.Branches.Count == 0 ? "Connect a tree to start exploring." : "No matching branches. Clear a filter or Reset.",
                    small, Muted, new RectangleF(_list.X + 6, _list.Y, _list.Width - 12, RowHeight), trim);
            if (Count > _shown)
            {
                Fill(graphics, PanelTheme.Rgb(232, 236, 229), _track);
                Fill(graphics, PanelTheme.Rgb(153, 174, 156), Thumb());
            }
        }
    }
}
