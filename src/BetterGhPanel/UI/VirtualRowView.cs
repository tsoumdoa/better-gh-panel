using System;
using System.Linq;
using BetterGhPanel.Core;
using Eto.Drawing;
using Eto.Forms;

namespace BetterGhPanel.UI
{
    /// <summary>
    /// A fixed-size viewport, rather than a giant scrollable surface. Both drawing and
    /// navigation remain bounded by the visible rows, even for million-item trees.
    /// </summary>
    internal sealed class VirtualRowView : Drawable
    {
        private const int HeaderHeight = 32;
        private const int RowHeight = 28;
        private const int ScrollWidth = 16;
        private static readonly Color Ink = Color.FromArgb(38, 52, 43);
        private static readonly Color Muted = Color.FromArgb(105, 119, 108);
        private static readonly Color Sage = Color.FromArgb(226, 237, 223);
        // These are owned instances; dispose them with the viewport, unlike cached fonts.
        private readonly Font _font = new Font(SystemFont.Default, 11);
        private readonly Font _bold = new Font(SystemFont.Bold, 10);
        private bool _fontsDisposed;
        private PanelQueryResult? _result;
        private int _first;
        private int _selectedSourcePosition = -1;
        private bool _dragging;
        private float _dragOffset;

        public event Action<PanelRow>? RowSelected;
        public event EventHandler? ViewportChanged;
        public int FirstVisibleRow => _first;
        public int SelectedSourcePosition => _selectedSourcePosition;
        public int VisibleRowCount => Math.Max(1, (Height - HeaderHeight) / RowHeight);
        private int Count => _result?.RowIds.Count ?? 0;
        private int MaximumFirst => Math.Max(0, Count - VisibleRowCount);

        public VirtualRowView()
        {
            CanFocus = true;
            BackgroundColor = Colors.White;
            Paint += PaintRows;
            SizeChanged += (s, e) => SetFirst(_first);
            MouseWheel += (s, e) =>
            {
                SetFirst(_first - (int)Math.Round(e.Delta.Height * 3));
                e.Handled = true;
            };
            MouseDown += SelectOrScroll;
            MouseMove += (s, e) =>
            {
                if (_dragging && (e.Buttons & MouseButtons.Primary) != 0)
                    ScrollFromThumb(e.Location.Y - _dragOffset);
                else _dragging = false;
            };
            MouseUp += (s, e) =>
            {
                _dragging = false;
                Invalidate();
            };
            MouseLeave += (s, e) => { _dragging = false; Invalidate(); };
            KeyDown += Navigate;
        }

        public void SetResult(PanelQueryResult? result, bool resetPosition)
        {
            if (_result?.Snapshot != result?.Snapshot ||
                (_selectedSourcePosition >= 0 && (result == null || !result.RowIds.Contains(_selectedSourcePosition))))
                ClearSelection();
            _result = result;
            SetFirst(resetPosition ? 0 : _first);
        }

        public void JumpToRow(int zeroBasedRow) => SetFirst(zeroBasedRow);

        public void ClearSelection()
        {
            _selectedSourcePosition = -1;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_fontsDisposed)
            {
                _fontsDisposed = true;
                Paint -= PaintRows;
                _font.Dispose();
                _bold.Dispose();
            }
            base.Dispose(disposing);
        }

        private void SetFirst(int value)
        {
            _first = Math.Max(0, Math.Min(MaximumFirst, value));
            Invalidate();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        private RectangleF Thumb()
        {
            float track = Math.Max(1, Height - HeaderHeight);
            float thumb = Math.Min(track, Math.Max(30, track * VisibleRowCount / Math.Max(1, Count)));
            float y = HeaderHeight + (track - thumb) * _first / Math.Max(1, MaximumFirst);
            return new RectangleF(Width - ScrollWidth + 3, y, ScrollWidth - 6, thumb);
        }

        private void ScrollFromThumb(float top)
        {
            var thumb = Thumb();
            float travel = Height - HeaderHeight - thumb.Height;
            if (travel <= 0) return;
            SetFirst((int)Math.Round((top - HeaderHeight) / travel * MaximumFirst));
        }

        private void SelectOrScroll(object? sender, MouseEventArgs e)
        {
            if ((e.Buttons & MouseButtons.Primary) == 0) return;
            Focus();
            if (e.Location.X >= Width - ScrollWidth)
            {
                var thumb = Thumb();
                if (e.Location.Y >= thumb.Top && e.Location.Y <= thumb.Bottom)
                {
                    _dragging = true;
                    _dragOffset = e.Location.Y - thumb.Top;
                }
                else SetFirst(_first + (e.Location.Y < thumb.Top ? -VisibleRowCount : VisibleRowCount));
                e.Handled = true;
                return;
            }
            if (e.Location.Y < HeaderHeight || _result == null) return;
            int position = _first + (int)((e.Location.Y - HeaderHeight) / RowHeight);
            SelectPosition(position);
            e.Handled = true;
        }

        private void SelectPosition(int position)
        {
            if (_result == null || position < 0 || position >= Count) return;
            var row = _result.Snapshot.Rows[_result.RowIds[position]];
            _selectedSourcePosition = _result.RowIds[position];
            RowSelected?.Invoke(row);
            Invalidate();
        }

        private void Navigate(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Keys.Home: SetFirst(0); break;
                case Keys.End: SetFirst(MaximumFirst); break;
                case Keys.PageUp: SetFirst(_first - VisibleRowCount); break;
                case Keys.PageDown: SetFirst(_first + VisibleRowCount); break;
                case Keys.Up: SetFirst(_first - 1); break;
                case Keys.Down: SetFirst(_first + 1); break;
                case Keys.Enter: SelectPosition(_first); break;
                default: return;
            }
            e.Handled = true;
        }

        private void PaintRows(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int right = Math.Max(1, Width - ScrollWidth);
            int pathWidth = Math.Min(180, Math.Max(120, right / 5));
            int indexWidth = 72;
            int typeWidth = 115;
            int valueStart = pathWidth + indexWidth + typeWidth;
            g.FillRectangle(Colors.White, 0, 0, Width, Height);
            g.FillRectangle(Sage, 0, 0, right, HeaderHeight);
            DrawCell(g, "BRANCH", 0, 0, pathWidth, HeaderHeight, true);
            DrawCell(g, "INDEX", pathWidth, 0, indexWidth, HeaderHeight, true);
            DrawCell(g, "TYPE", pathWidth + indexWidth, 0, typeWidth, HeaderHeight, true);
            DrawCell(g, "VALUE", valueStart, 0, right - valueStart, HeaderHeight, true);

            if (_result == null || Count == 0)
            {
                g.DrawText(_font, Muted, 18, HeaderHeight + 24,
                    _result == null ? "Connect a tree to inspect its data." : "No items match this view. Reset or adjust the filters.");
            }
            else
            {
                // Only this small range is materialized; full values are resolved on selection.
                int end = Math.Min(Count, _first + VisibleRowCount + 1);
                for (int position = _first; position < end; position++)
                {
                    var row = _result.Snapshot.Rows[_result.RowIds[position]];
                    int y = HeaderHeight + (position - _first) * RowHeight;
                    if (_result.RowIds[position] == _selectedSourcePosition)
                        g.FillRectangle(Sage, 0, y, right, RowHeight);
                    else if ((position & 1) != 0)
                        g.FillRectangle(Color.FromArgb(248, 250, 247), 0, y, right, RowHeight);
                    DrawCell(g, row.Path, 0, y, pathWidth, RowHeight, false);
                    DrawCell(g, row.OriginalIndex.ToString(), pathWidth, y, indexWidth, RowHeight, false);
                    DrawCell(g, row.TypeName, pathWidth + indexWidth, y, typeWidth, RowHeight, false);
                    DrawCell(g, row.DisplayValue, valueStart, y, right - valueStart, RowHeight, false);
                    if (!row.IsValid)
                        g.FillRectangle(Color.FromArgb(191, 95, 59), 0, y + 3, 3, RowHeight - 6);
                }
            }

            if (MaximumFirst > 0)
            {
                g.FillRectangle(Color.FromArgb(243, 246, 241), right, HeaderHeight, ScrollWidth, Height - HeaderHeight);
                g.FillRectangle(_dragging ? Ink : Color.FromArgb(160, 181, 154), Thumb());
            }
            g.DrawLine(Color.FromArgb(215, 225, 211), 0, HeaderHeight, right, HeaderHeight);
        }

        private void DrawCell(Graphics g, string text, int x, int y, int width, int height, bool header)
        {
            if (width <= 10) return;
            // Bound both string formatting and font measurement for huge string-valued items.
            text = text ?? string.Empty;
            if (text.Length > 256) text = text.Substring(0, 253) + "…";
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            var font = header ? _bold : _font;
            int available = width - 18;
            if (g.MeasureString(font, text).Width > available)
            {
                int low = 0, high = text.Length;
                while (low < high)
                {
                    int middle = (low + high + 1) / 2;
                    if (g.MeasureString(font, text.Substring(0, middle) + "…").Width <= available) low = middle;
                    else high = middle - 1;
                }
                text = text.Substring(0, low) + "…";
            }
            g.DrawText(font, header ? Muted : Ink, x + 9, y + (height - font.LineHeight) / 2, text);
        }
    }
}
