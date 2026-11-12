using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;

#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private int StatViewCount(PanelQueryResult result) => result.BranchPaths.Count * (result.StatRows.Count + 1);

        private void DrawStatCell(Graphics g, RectangleF cell, PanelBranchStats? stats, PanelStatKinds kind, Font small, Font mono, StringFormat trim, int decimals, bool showName = true, bool measuring = false)
        {
            float label = showName ? 48 : 0;
            if (showName) Text(g, PanelBranchStats.Name(kind), small, Muted, new RectangleF(cell.X + 4, cell.Y, label, cell.Height), trim);
            using var right = new StringFormat(trim) { Alignment = StringAlignment.Far };
            string value = stats?.Format(kind, decimals) ?? (measuring ? "measuring…" : "—");
            Text(g, value, mono, stats == null || stats.Measuring || !stats.Value(kind).HasValue ? PanelTheme.Disabled : Ink,
                new RectangleF(cell.X + label + 4, cell.Y, Math.Max(0, cell.Width - label - 12), cell.Height), right);
        }

        private void RenderBranchStats(Graphics g, PanelQueryResult result, Font font, Font small, Font mono, StringFormat trim, bool interactive = true)
        {
            _setDetails.Close();
            var picks = result.StatRows;
            int total = StatViewCount(result);
            if (interactive && TableVisible) PanelOwner.Session.EnsureBranchStatColumns(result);
            using var renderer = new CanvasRowRenderer(g, picks.Count, false);
            float gutter = renderer.GutterWidth;
            if (TableVisible)
            {
                PrepareTable(g, result, font, gutter);
                DrawTableHeader(g, font, trim, gutter);
            }
            var state = g.Save();
            g.SetClip(_list, CombineMode.Intersect);
            try
            {
                float y = _list.Y;
                int perBranch = picks.Count + 1;
                _offset = Math.Max(0, Math.Min(MaximumOffset, _offset));
                for (int offset = _offset; offset < total && y + RowHeight <= _list.Bottom; offset++)
                {
                    string path = result.BranchPaths[offset / perBranch];
                    var stats = result.BranchStats![path];
                    int index = offset % perBranch - 1;
                    var bounds = new RectangleF(_list.X, y, _list.Width, RowHeight);
                    if (index < 0)
                    {
                        Fill(g, PanelTheme.Rgb(236, 241, 235), bounds);
                        string count = CanvasStatistics.BranchCount(result, path);
                        string note = stats.Note.Length > 0 ? " · " + stats.Note : "";
                        float width = g.MeasureString(count + note, small).Width + 8;
                        Text(g, path, mono, Accent, new RectangleF(bounds.X + 6, y, Math.Max(0, bounds.Width - width - 12), bounds.Height), trim);
                        Text(g, count, small, Muted, new RectangleF(bounds.Right - width, y, width, bounds.Height), trim);
                        if (note.Length > 0)
                        {
                            float countWidth = g.MeasureString(count, small).Width;
                            var noteBounds = new RectangleF(bounds.Right - width + countWidth, y, Math.Max(0, width - countWidth), bounds.Height);
                            Text(g, note, small, stats.Mixed ? PanelTheme.Warning : Muted, noteBounds, trim);
                            using var pen = new Pen(stats.Mixed ? PanelTheme.Warning : Muted) { DashStyle = DashStyle.Dot };
                            g.DrawLine(pen, noteBounds.Left + 7, noteBounds.Bottom - 4, noteBounds.Right - 6, noteBounds.Bottom - 4);
                        }
                        DrawBranchExtras(g, result, bounds, path, mono, small, trim);
                    }
                    else
                    {
                        var kind = picks[index];
                        int source = stats.Source(kind);
                        bool selected = source >= 0 && _selection.Contains(source);
                        Fill(g, selected ? PanelTheme.Rgb(216, 232, 219) : index % 2 == 0 ? PanelTheme.Background : PanelTheme.Rgb(247, 249, 246), bounds);
                        renderer.DrawAddress(g, bounds, index + 1, index, false, selected);
                        if (TableVisible)
                        {
                            PaintColumn(g, 0, y, RowHeight, cell => DrawStatCell(g, cell, stats, kind, small, mono, trim, result.StatDecimals));
                            for (int column = 0; column < PanelOwner.TableColumns.Count; column++)
                            {
                                result.ColumnBranchStats.TryGetValue(PanelOwner.TableColumns[column].Input.InstanceGuid, out var branches);
                                PanelBranchStats? cellStats = null;
                                branches?.TryGetValue(path, out cellStats);
                                PaintColumn(g, column + 1, y, RowHeight, cell => DrawStatCell(g, cell, cellStats, kind, small, mono, trim, result.StatDecimals, showName: false,
                                    measuring: branches == null && PanelOwner.TableColumns[column].Snapshot != null && PanelOwner.TableColumns[column].Pair != null));
                            }
                        }
                        else DrawStatCell(g, new RectangleF(bounds.X + gutter, y, bounds.Width - gutter, RowHeight), stats, kind, small, mono, trim, result.StatDecimals);
                        var hitBounds = TableVisible ? RectangleF.Intersect(bounds, ColumnClip(0, y, RowHeight)) : bounds;
                        if (interactive && source >= 0 && TargetPainted(g, hitBounds) && result.TryGetViewIndex(source, out int viewIndex))
                            if (!_rowHits.Any(hit => hit.Bounds == hitBounds && hit.SourcePosition == source))
                                _rowHits.Add(new RowHit(hitBounds, source, viewIndex + 1));
                    }
                    _shown++; y += RowHeight;
                }
            }
            finally { g.Restore(state); }
            if (Count > _shown && !_track.IsEmpty)
            {
                Fill(g, PanelTheme.Rgb(232, 236, 229), _track);
                Fill(g, PanelTheme.Rgb(153, 174, 156), Thumb());
            }
            if (TableVisible && _horizontalMaximum > 0) { Fill(g, PanelTheme.Rgb(232, 236, 229), _horizontalTrack); Fill(g, PanelTheme.Rgb(153, 174, 156), HorizontalThumb()); }
        }
    }
}
