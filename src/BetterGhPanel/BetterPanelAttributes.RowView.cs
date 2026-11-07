using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

// Native Grasshopper supplies the drawing contract on Windows and Mac.
#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private PanelQueryResult? _tableViewResult;
        private PanelTableIndex? _tableView;
        private bool HasPairView => TableVisible && !DisplayListLengths && PanelOwner.Session.Result.DisplaySet == null && PanelOwner.TableColumns.Any(column => column.Pair?.HasUnequal == true);
        private PanelTableIndex TableIndex(PanelQueryResult result)
        {
            if (ReferenceEquals(_tableViewResult, result) && _tableView != null) return _tableView;
            _tableViewResult = result;
            var extras = new List<PanelTableExtra>();
            var retained = new HashSet<int>(result.RowPositions);
            for (int column = 0; column < PanelOwner.TableColumns.Count; column++)
            {
                var map = PanelOwner.TableColumns[column].Pair;
                if (map == null || !map.HasUnequal) continue;
                var branchOrder = map.Pairs.Select((pair, index) => (pair.Data.Path, index)).ToDictionary(value => value.Path, value => value.index);
                foreach (var cell in map.BranchItems(result.Snapshot, result.RowPositions, result.BranchPaths,
                    Runtime.PanelPairedData.EmptyPartnersInScope(PanelOwner.Session.Options)))
                {
                    if (retained.Contains(map.DataPositionAt(cell.Item))) continue;
                    var pair = map.PairAtPaired(cell.Item);
                    int index = cell.Item - pair.PairedOffset;
                    int withinBranch = PanelOwner.ReverseTree && index < pair.Data.ItemCount ? pair.Data.ItemCount - 1 - index : index;
                    long order = ((long)branchOrder[pair.Data.Path] << 32) + (uint)withinBranch;
                    extras.Add(new PanelTableExtra(cell.Path, pair.Data.Path + ":" + index, column, cell.Item, order));
                }
            }
            return _tableView = new PanelTableIndex(result, PanelOwner.TableColumns.Count, extras);
        }
        private (int Index, bool Sorted) RowAddress(PanelQueryResult result, int sourcePosition)
        {
            int index = result.DisplaySet?.MemberIndices.TryGetValue(sourcePosition, out int k) == true ? k : result.Snapshot.Rows[sourcePosition].OriginalIndex;
            return (index, PanelOwner.Session.Options.SortMode != PanelSortMode.SourceOrder);
        }
        private int ViewCount(PanelQueryResult result) => HasPairView ? TableIndex(result).Entries.Count : result.ViewIndex.Count;
        private void ViewEntry(PanelQueryResult result, int offset, out int item, out string? empty, out int extra, out string label)
        {
            extra = -1; label = "";
            if (!HasPairView) { result.ViewIndex.TryGet(offset, out item, out empty); return; }
            var entry = TableIndex(result).Entries[offset]; item = entry.Item; empty = entry.Empty; extra = entry.Extra ? offset : -1;
        }
        private int ViewOffset(PanelQueryResult result, int item) => HasPairView ? TableIndex(result).OffsetOfItem(item) : result.ViewIndex.OffsetOfItem(item);
        private int ViewItemAtOrAfter(PanelQueryResult result, int offset) => HasPairView ? TableIndex(result).ItemAtOrAfter(offset) : result.ViewIndex.ItemAtOrAfter(offset);
        private string ViewPath(PanelQueryResult result, int offset)
        {
            if (HasPairView) return TableIndex(result).Entries[offset].Path;
            result.ViewIndex.TryGet(offset, out int item, out string? empty);
            return empty ?? result.Snapshot.Rows[result.RowPositions[item]].Path;
        }
        private void DrawBranchExtras(Graphics graphics, PanelQueryResult result, RectangleF bounds, string path,
            Font mono, Font small, StringFormat trim, string? countText = null,
            string? label = null, RectangleF labelBounds = default)
        {
            float countWidth = countText == null ? 96 : System.Math.Min(bounds.Width / 2, graphics.MeasureString(countText, small).Width + 6);
            if (labelBounds.IsEmpty) labelBounds = new RectangleF(bounds.X + 6, bounds.Y, bounds.Width - countWidth - 36, bounds.Height);
            float labelWidth = graphics.MeasureString(label ?? path, mono, int.MaxValue, trim).Width;
            float x = labelBounds.X + labelWidth + 2;
            var copy = _hoverBounds == bounds && x + 17 <= labelBounds.Right
                ? new RectangleF(x, bounds.Y, 17, bounds.Height) : RectangleF.Empty;
            if (!copy.IsEmpty && TargetPainted(graphics, copy))
            {
                using var pen = new Pen(Accent, 1);
                using var back = new System.Drawing.Drawing2D.GraphicsPath();
                using var front = new System.Drawing.Drawing2D.GraphicsPath();
                void Square(System.Drawing.Drawing2D.GraphicsPath shape, float left, float top)
                {
                    shape.AddArc(left, top, 2, 2, 180, 90); shape.AddArc(left + 6, top, 2, 2, 270, 90);
                    shape.AddArc(left + 6, top + 6, 2, 2, 0, 90); shape.AddArc(left, top + 6, 2, 2, 90, 90); shape.CloseFigure();
                }
                Square(back, x + 5, bounds.Y + 3); Square(front, x + 2, bounds.Y + 6);
                graphics.DrawPath(pen, back);
                using var brush = new SolidBrush(PanelTheme.Rgb(236, 241, 235));
                graphics.FillPath(brush, front); graphics.DrawPath(pen, front);
            }
            else copy = RectangleF.Empty;
            if (countText != null && result.Warnings.IsOutlier(result.Snapshot, path))
                Text(graphics, countText, small, PanelTheme.Warning, new RectangleF(bounds.Right - countWidth - 6, bounds.Y, countWidth, bounds.Height), trim);
            if (TargetPainted(graphics, bounds)) AddBranchHit(bounds, path, copy);
        }

    }
}
