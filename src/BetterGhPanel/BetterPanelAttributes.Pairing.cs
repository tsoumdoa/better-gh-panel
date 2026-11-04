using System;
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
        private sealed class PairedViewRow
        {
            internal int Item = -1, Extra = -1;
            internal string? Empty;
            internal string Path = "", Label = "";
        }
        private PanelQueryResult? _pairViewResult;
        private Runtime.PanelPairedData? _pairViewData;
        private readonly List<PairedViewRow> _pairView = new List<PairedViewRow>();
        private bool HasPairView => PanelOwner.PanelSplit && PanelOwner.PairedData != null;
        private IReadOnlyList<PairedViewRow> PairView(PanelQueryResult result)
        {
            if (ReferenceEquals(_pairViewResult, result) && ReferenceEquals(_pairViewData, PanelOwner.PairedData)) return _pairView;
            _pairViewResult = result; _pairViewData = PanelOwner.PairedData; _pairView.Clear();
            var entries = new List<PairedViewRow>();
            void Flush()
            {
                if (entries.Count == 0) return;
                string path = entries[0].Path;
                // Missing-cell runs are presentation runs, including sorted/filtered views.
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].Item < 0 || PanelOwner.PairedRow(result.RowPositions[entries[i].Item]) != null) continue;
                    int end = i;
                    while (end + 1 < entries.Count && entries[end + 1].Item >= 0 &&
                        PanelOwner.PairedRow(result.RowPositions[entries[end + 1].Item]) == null) end++;
                    int firstIndex = result.Snapshot.Rows[result.RowPositions[entries[i].Item]].OriginalIndex;
                    int lastIndex = result.Snapshot.Rows[result.RowPositions[entries[end].Item]].OriginalIndex;
                    entries[i].Label = "no F items for " + firstIndex + (end == i ? "" : "–" + lastIndex);
                    i = end;
                }
                _pairView.AddRange(entries);
                var referenced = new HashSet<int>(entries.Where(entry => entry.Item >= 0)
                    .Select(entry => PanelOwner.PairedData!.ItemAt(result.RowPositions[entry.Item]))
                    .Where(item => item.HasValue).Select(item => item!.Value));
                foreach (var pair in PanelOwner.PairedData!.BranchesAt(path))
                {
                    if (!pair.Unequal) continue;
                    var extras = Enumerable.Range(pair.PairedOffset, pair.Paired.ItemCount).Where(item => !referenced.Contains(item)).ToArray();
                    for (int i = 0; i < extras.Length; i++)
                        _pairView.Add(new PairedViewRow { Path = path, Extra = extras[i], Label = i == 0 ? "+" + extras.Length + " only in F" : "" });
                }
                entries.Clear();
            }
            for (int offset = 0; offset < result.ViewIndex.Count; offset++)
            {
                result.ViewIndex.TryGet(offset, out int item, out string? empty);
                string path = empty ?? result.Snapshot.Rows[result.RowPositions[item]].Path;
                if (entries.Count > 0 && entries[0].Path != path) Flush();
                entries.Add(new PairedViewRow { Item = item, Empty = empty, Path = path });
            }
            Flush();
            return _pairView;
        }
        private int ViewCount(PanelQueryResult result) => HasPairView ? PairView(result).Count : result.ViewIndex.Count;
        private void ViewEntry(PanelQueryResult result, int offset, out int item, out string? empty, out int extra, out string label)
        {
            extra = -1; label = "";
            if (!HasPairView) { result.ViewIndex.TryGet(offset, out item, out empty); return; }
            var entry = PairView(result)[offset]; item = entry.Item; empty = entry.Empty; extra = entry.Extra; label = entry.Label;
        }
        private int ViewOffset(PanelQueryResult result, int item) => HasPairView
            ? PairView(result).ToList().FindIndex(entry => entry.Item == item) : result.ViewIndex.OffsetOfItem(item);
        private int ViewItemAtOrAfter(PanelQueryResult result, int offset)
        {
            if (!HasPairView) return result.ViewIndex.ItemAtOrAfter(offset);
            var entries = PairView(result);
            for (int i = Math.Max(0, offset); i < entries.Count; i++) if (entries[i].Item >= 0) return entries[i].Item;
            return result.RowPositions.Count;
        }
        private string ViewPath(PanelQueryResult result, int offset)
        {
            if (HasPairView) return PairView(result)[offset].Path;
            result.ViewIndex.TryGet(offset, out int item, out string? empty);
            return empty ?? result.Snapshot.Rows[result.RowPositions[item]].Path;
        }
        private void DrawPairedHeading(Graphics graphics, string path, RectangleF bounds, Font small, Font mono, StringFormat trim)
        {
            var pairs = PanelOwner.PairedData!.BranchesAt(path).ToArray();
            int data = pairs.Sum(pair => pair.Data.ItemCount), paired = pairs.Sum(pair => pair.Paired.ItemCount);
            string count = paired + (paired == 1 ? " item" : " items");
            float countWidth = graphics.MeasureString(count, small).Width + 6;
            float badgeWidth = pairs.Any(pair => pair.Unequal) ? graphics.MeasureString("D " + data, small).Width + 10 : 0;
            float right = bounds.Right - countWidth - 6;
            Text(graphics, count, small, Muted, new RectangleF(right, bounds.Y, countWidth, bounds.Height), trim);
            if (badgeWidth > 0)
            {
                var badge = new RectangleF(right - badgeWidth - 4, bounds.Y + 1, badgeWidth, bounds.Height - 2);
                Fill(graphics, PanelTheme.Rgb(249, 233, 198), badge);
                Text(graphics, "D " + data, small, PanelTheme.Warning, badge, trim);
                right = badge.X;
            }
            float pathWidth = Math.Min(right - bounds.X - 12, graphics.MeasureString(path, mono).Width + 3);
            Text(graphics, path, mono, Accent, new RectangleF(bounds.X + 6, bounds.Y, pathWidth, bounds.Height), trim);
            if (pairs.Any(pair => pair.ByOrder))
                Text(graphics, " ← F " + string.Join(", ", pairs.Select(pair => pair.Paired.Path)), mono, Muted,
                    new RectangleF(bounds.X + 6 + pathWidth, bounds.Y, Math.Max(0, right - bounds.X - pathWidth - 12), bounds.Height), trim);
        }
    }
}
