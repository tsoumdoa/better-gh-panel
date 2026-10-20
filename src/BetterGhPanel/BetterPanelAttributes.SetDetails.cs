using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;

#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private float _setCountWidth = 44;
        private PanelSnapshot? _setCountSnapshot;
        private readonly CanvasSetDetails _setDetails = new CanvasSetDetails();
        private readonly List<RowHit> _setHits = new List<RowHit>();
        private bool _setControlsVisible;

        private void SizeSetColumn(Graphics graphics, CanvasRowRenderer renderer, PanelSnapshot snapshot)
        {
            if (ReferenceEquals(_setCountSnapshot, snapshot)) return;
            _setCountSnapshot = snapshot;
            int largest = 1;
            foreach (var row in snapshot.Rows)
                if (row.SetOccurrences != null && row.SetOccurrences.Count > largest) largest = row.SetOccurrences.Count;
            // Measure the whole snapshot once so filtering and scrolling never shift columns.
            _setCountWidth = System.Math.Max(44, renderer.MeasureNumber(graphics, $"{largest:N0}") + 36);
        }

        private bool SetControlAt(PointF point) => _setControlsVisible && _menu.Kind == CanvasMenuKind.None && _rowMenuResult == null &&
            RowsAreCurrent(PanelOwner.Session.Result) &&
            (_setHits.Any(hit => hit.Bounds.Contains(point)) || _setDetails.Collapse.Contains(point) ||
            _setDetails.CanPrevious && _setDetails.Previous.Contains(point) || _setDetails.CanNext && _setDetails.Next.Contains(point));

        private void LayoutSetDetails()
        {
            bool current = false;
            if (_setDetails.IsOpen)
            {
                var session = PanelOwner.Session;
                current = !PanelOwner.Locked && session.HasCurrentResult;
                _setDetails.Synchronize(session.Snapshot, current ? session.Result : null);
            }
            _setDetails.Layout(_panel, _setControlsVisible && current && !PanelOwner.PanelMinimized);
            m_innerBounds = _setDetails.Bounds.IsEmpty ? _panel : RectangleF.Union(_panel, _setDetails.Bounds);
            Bounds = new RectangleF(m_innerBounds.X - 6, m_innerBounds.Y, m_innerBounds.Width + 12, m_innerBounds.Height);
        }

        private void DrawSetCount(Graphics g, CanvasRowRenderer renderer, PanelRow row, RectangleF bounds,
            int sourcePosition, int viewNumber, Font small)
        {
            if (row.SetOccurrences == null) return;
            var countBounds = new RectangleF(bounds.Right - _setCountWidth, bounds.Y, _setCountWidth, bounds.Height);
            bool expanded = _setDetails.SourcePosition == sourcePosition && _setDetails.IsOpen;
            bool repeated = row.SetOccurrences.Count > 1;
            DrawSetBadge(g, new RectangleF(countBounds.X + 2, bounds.Y + 1.5f, countBounds.Width - 18, bounds.Height - 3), repeated, expanded);
            renderer.DrawValue(g, $"{row.SetOccurrences.Count:N0}", small, expanded || repeated ? Accent : Color.FromArgb(139, 148, 132),
                new RectangleF(countBounds.X + 6, bounds.Y, countBounds.Width - 36, bounds.Height), true);
            renderer.DrawCell(g, "×", small, expanded ? Accent : repeated ? Color.FromArgb(130, 149, 117) : Color.FromArgb(161, 171, 153),
                new RectangleF(countBounds.Right - 28, bounds.Y, 8, bounds.Height));
            DrawSetChevron(g, new RectangleF(countBounds.Right - 12, bounds.Y, 10, bounds.Height), expanded,
                expanded ? Accent : Color.FromArgb(152, 167, 141));
            _setHits.Add(new RowHit(countBounds, sourcePosition, viewNumber));
        }

        private static void DrawSetBadge(Graphics g, RectangleF bounds, bool repeated, bool expanded)
        {
            const float corner = 6;
            using var shape = new GraphicsPath();
            shape.AddArc(bounds.X, bounds.Y, corner, corner, 180, 90);
            shape.AddArc(bounds.Right - corner, bounds.Y, corner, corner, 270, 90);
            shape.AddArc(bounds.Right - corner, bounds.Bottom - corner, corner, corner, 0, 90);
            shape.AddArc(bounds.X, bounds.Bottom - corner, corner, corner, 90, 90);
            shape.CloseFigure();
            using var fill = new SolidBrush(expanded ? Color.FromArgb(220, 233, 210) :
                repeated ? Color.FromArgb(237, 243, 232) : Color.FromArgb(250, 251, 249));
            using var border = new Pen(expanded ? Color.FromArgb(158, 182, 141) :
                repeated ? Color.FromArgb(210, 223, 203) : Color.FromArgb(228, 233, 223));
            var state = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(fill, shape);
                g.DrawPath(border, shape);
            }
            finally { g.Restore(state); }
        }

        private void RenderSetDetails(Graphics g, PanelQueryResult result, Font small, Font mono, StringFormat trim)
        {
            var bounds = _setDetails.Bounds;
            var occurrences = _setDetails.Occurrences;
            if (bounds.IsEmpty || occurrences == null) return;
            var row = result.Snapshot.Rows[_setDetails.SourcePosition];
            Fill(g, Color.White, bounds);
            Fill(g, Color.FromArgb(236, 241, 235), new RectangleF(bounds.X, bounds.Y, bounds.Width, 23));
            Text(g, "Occurrences: " + PanelOwner.DisplayValue(result.Snapshot, row, _setDetails.SourcePosition), small, Ink,
                new RectangleF(bounds.X + 7, bounds.Y + 1, bounds.Width - 34, 21), trim);
            DrawSetChevron(g, _setDetails.Collapse, true);
            Text(g, CanvasSetDetails.Summary(occurrences), small, Muted, new RectangleF(bounds.X + 7, bounds.Y + 23, bounds.Width - 14, 16), trim);
            float indexWidth = 94;
            Text(g, "Input branch", small, Accent, new RectangleF(bounds.X + 9, bounds.Y + 38, bounds.Width - indexWidth - 20, 15), trim);
            Text(g, "Index (0-based)", small, Accent, new RectangleF(bounds.Right - indexWidth - 7, bounds.Y + 38, indexWidth, 15), trim);
            for (int i = 0; i < _setDetails.Visible; i++)
            {
                var address = occurrences.Locations[_setDetails.Offset + i];
                var line = new RectangleF(_setDetails.Table.X, _setDetails.Table.Y + i * RowHeight, _setDetails.Table.Width, RowHeight);
                Fill(g, (i & 1) == 0 ? Color.FromArgb(247, 249, 246) : Color.White, line);
                Text(g, address.Path, mono, Ink, new RectangleF(line.X + 3, line.Y, line.Width - indexWidth - 9, line.Height), trim);
                Text(g, address.ItemIndex.ToString(), mono, Ink, new RectangleF(line.Right - indexWidth, line.Y, indexWidth - 3, line.Height), trim);
            }
            if (_setDetails.HasPages)
            {
                Text(g, $"{_setDetails.Offset + 1:N0}–{_setDetails.Offset + _setDetails.Visible:N0} of {occurrences.Count:N0}", small, Muted,
                    new RectangleF(bounds.X + 8, bounds.Bottom - 21, bounds.Width - 128, 18), trim);
                Text(g, "‹ Previous", small, _setDetails.CanPrevious ? Accent : Color.LightGray, _setDetails.Previous, trim);
                Text(g, "Next ›", small, _setDetails.CanNext ? Accent : Color.LightGray, _setDetails.Next, trim);
            }
            using var border = new Pen(Color.FromArgb(164, 171, 164));
            g.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        private static void DrawSetChevron(Graphics g, RectangleF bounds, bool expanded, Color? color = null)
        {
            float x = bounds.X + (bounds.Width - 6) / 2, y = bounds.Y + (bounds.Height - 6) / 2;
            using var pen = new Pen(color ?? Accent, 1.1f);
            g.DrawLines(pen, expanded
                ? new[] { new PointF(x, y + 4), new PointF(x + 3, y + 1), new PointF(x + 6, y + 4) }
                : new[] { new PointF(x, y + 1), new PointF(x + 3, y + 4), new PointF(x + 6, y + 1) });
        }

        private bool HandleSetDetailsClick(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            var point = e.CanvasLocation;
            LayoutSetDetails();
            bool handled = _setDetails.Bounds.Contains(point);
            if (handled)
            {
                if (_setDetails.Collapse.Contains(point)) _setDetails.Close();
                else if (_setDetails.Previous.Contains(point)) _setDetails.Page(-1);
                else if (_setDetails.Next.Contains(point)) _setDetails.Page(1);
            }
            else if (_setControlsVisible && RowsAreCurrent(PanelOwner.Session.Result))
            {
                foreach (var hit in _setHits)
                {
                    if (!hit.Bounds.Contains(point)) continue;
                    _setDetails.Toggle(PanelOwner.Session.Snapshot, hit.SourcePosition);
                    handled = true;
                    break;
                }
            }
            if (!handled) return false;
            SuppressTrailingDoubleClick();
            LayoutSetDetails();
            CanvasDoubleClickGuard.Start(canvas, e);
            canvas.Invalidate();
            return true;
        }

        private bool SetDetailsTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            if (!_setControlsVisible || PanelOwner.Locked || !PanelOwner.Session.HasCurrentResult) return false;
            if (_setDetails.Bounds.Contains(point))
            {
                var location = _setDetails.LocationAt(point);
                e.Title = _setDetails.Collapse.Contains(point) ? "Collapse occurrences" : "Original input locations";
                e.Text = location.HasValue ? location.Value.Path + " [" + location.Value.ItemIndex + "]" : CanvasSetDetails.Summary(_setDetails.Occurrences!);
                e.Description = CanvasSetDetails.Scope;
                return true;
            }
            if (!RowsAreCurrent(PanelOwner.Session.Result)) return false;
            foreach (var hit in _setHits)
            {
                if (!hit.Bounds.Contains(point)) continue;
                var occurrences = PanelOwner.Session.Snapshot.Rows[hit.SourcePosition].SetOccurrences!;
                e.Title = _setDetails.IsOpen && _setDetails.SourcePosition == hit.SourcePosition ? "Collapse occurrences" : "Expand occurrences";
                e.Text = CanvasSetDetails.Summary(occurrences) + $". {occurrences.DuplicatesRemoved:N0} " +
                    (occurrences.DuplicatesRemoved == 1 ? "duplicate removed." : "duplicates removed.");
                e.Description = CanvasSetDetails.Scope;
                return true;
            }
            return false;
        }
    }
}
#pragma warning restore CA1416
