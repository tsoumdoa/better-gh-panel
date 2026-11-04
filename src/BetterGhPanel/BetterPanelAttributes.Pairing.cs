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
        private int[] _pairItemOffsets = Array.Empty<int>(), _pairSuccessors = Array.Empty<int>();
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
                var set = result.DisplaySet;
                var members = entries.Where(entry => entry.Item >= 0).SelectMany(entry => set == null
                    ? new[] { result.RowPositions[entry.Item] } : set.IsBranchSet
                    ? set.BranchMembers[path] : set.Members[result.RowPositions[entry.Item]]);
                IEnumerable<string> paths = set?.IsBranchSet == true ? set.BranchOccurrences[path] : new[] { path };
                foreach (var pair in PanelOwner.PairedData!.ScopedBranches(result.Snapshot, members, paths))
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
            _pairItemOffsets = new int[result.RowPositions.Count];
            _pairSuccessors = new int[_pairView.Count];
            int next = result.RowPositions.Count;
            for (int offset = _pairView.Count - 1; offset >= 0; offset--)
            {
                int item = _pairView[offset].Item;
                if (item >= 0) { _pairItemOffsets[item] = offset; next = item; }
                _pairSuccessors[offset] = next;
            }
            return _pairView;
        }
        private int ViewCount(PanelQueryResult result) => HasPairView ? PairView(result).Count : result.ViewIndex.Count;
        private void ViewEntry(PanelQueryResult result, int offset, out int item, out string? empty, out int extra, out string label)
        {
            extra = -1; label = "";
            if (!HasPairView) { result.ViewIndex.TryGet(offset, out item, out empty); return; }
            var entry = PairView(result)[offset]; item = entry.Item; empty = entry.Empty; extra = entry.Extra; label = entry.Label;
        }
        private int ViewOffset(PanelQueryResult result, int item)
        {
            if (!HasPairView) return result.ViewIndex.OffsetOfItem(item);
            PairView(result);
            return item >= 0 && item < _pairItemOffsets.Length ? _pairItemOffsets[item] : -1;
        }
        private int ViewItemAtOrAfter(PanelQueryResult result, int offset)
        {
            if (!HasPairView) return result.ViewIndex.ItemAtOrAfter(offset);
            PairView(result);
            offset = Math.Max(0, offset);
            return offset < _pairSuccessors.Length ? _pairSuccessors[offset] : result.RowPositions.Count;
        }
        private string ViewPath(PanelQueryResult result, int offset)
        {
            if (HasPairView) return PairView(result)[offset].Path;
            result.ViewIndex.TryGet(offset, out int item, out string? empty);
            return empty ?? result.Snapshot.Rows[result.RowPositions[item]].Path;
        }
        private void DrawPairedHeading(Graphics graphics, string path, RectangleF bounds, Font small, Font mono, StringFormat trim)
        {
            var heading = PanelOwner.PairedData!.Headings[path];
            string count = heading.Count;
            float countWidth = graphics.MeasureString(count, small).Width + 6;
            float badgeWidth = heading.Unequal ? graphics.MeasureString(heading.DataCount, small).Width + 10 : 0;
            float right = bounds.Right - countWidth - 6;
            Text(graphics, count, small, Muted, new RectangleF(right, bounds.Y, countWidth, bounds.Height), trim);
            if (badgeWidth > 0)
            {
                var badge = new RectangleF(right - badgeWidth - 4, bounds.Y + 1, badgeWidth, bounds.Height - 2);
                using (var pen = new Pen(PanelTheme.PairingWarning, 0.75f))
                    graphics.DrawRectangle(pen, badge.X, badge.Y, badge.Width, badge.Height);
                Text(graphics, heading.DataCount, small, PanelTheme.PairingWarning, badge, trim);
                right = badge.X;
            }
            float pathWidth = Math.Min(right - bounds.X - 12, graphics.MeasureString(path, mono).Width + 3);
            Text(graphics, path, mono, Accent, new RectangleF(bounds.X + 6, bounds.Y, pathWidth, bounds.Height), trim);
            if (heading.ByOrder)
                Text(graphics, " ← F " + heading.Paths, mono, Muted,
                    new RectangleF(bounds.X + 6 + pathWidth, bounds.Y, Math.Max(0, right - bounds.X - pathWidth - 12), bounds.Height), trim);
        }
    }
}
