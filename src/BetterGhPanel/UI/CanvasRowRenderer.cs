using System;
using System.Drawing;
using System.Globalization;

// Rhino supplies System.Drawing on the Grasshopper canvas.
#pragma warning disable CA1416
namespace BetterGhPanel.UI
{
    // Every cell shares one baseline in canvas units, independent of display DPI.
    // Capacity comes from the entire captured tree, never the filtered result or viewport.
    internal sealed class CanvasRowRenderer : IDisposable
    {
        private const float Baseline = 12.5f;
        private static readonly string MonospaceName = FindMonospaceName();
        private readonly Font _number = MonospaceFont(11);
        private readonly Font _index = MonospaceFont(8);
        private readonly StringFormat _format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
            LineAlignment = StringAlignment.Near
        };
        private readonly float _indexWidth;
        internal float GutterWidth { get; }

        internal static Font MonospaceFont(float size) => new Font(MonospaceName, size, FontStyle.Regular, GraphicsUnit.Pixel);

        private static string FindMonospaceName()
        {
            // Prefer the readable native monospace on Windows/Mac, with a host fallback.
            foreach (string name in new[] { "Consolas", "Menlo" })
            {
                using var font = new Font(name, 11, FontStyle.Regular, GraphicsUnit.Pixel);
                if (string.Equals(font.Name, name, StringComparison.OrdinalIgnoreCase)) return name;
            }
            return FontFamily.GenericMonospace.Name;
        }

        internal CanvasRowRenderer(Graphics graphics, int totalItems)
        {
            int digits = Math.Max(1, totalItems).ToString(CultureInfo.InvariantCulture).Length;
            string capacity = new string('0', digits);
            float numberWidth = (float)Math.Ceiling(graphics.MeasureString(capacity, _number, PointF.Empty, _format).Width);
            _indexWidth = (float)Math.Ceiling(graphics.MeasureString("[" + capacity + "]", _index, PointF.Empty, _format).Width);
            GutterWidth = Math.Max(54, 18 + numberWidth + _indexWidth);
        }

        internal void DrawAddress(Graphics graphics, RectangleF row, int viewNumber, int originalIndex, bool sorted, bool selected)
        {
            using (var brush = new SolidBrush(selected ? Color.FromArgb(209, 225, 211) : Color.FromArgb(238, 242, 236)))
                graphics.FillRectangle(brush, row.X, row.Y, GutterWidth, row.Height);
            using (var pen = new Pen(selected ? Color.FromArgb(194, 213, 195) : Color.FromArgb(223, 230, 220)))
                graphics.DrawLine(pen, row.X + GutterWidth - 0.5f, row.Y, row.X + GutterWidth - 0.5f, row.Bottom);
            float indexStart = row.X + GutterWidth - 8 - _indexWidth;
            DrawCell(graphics, (sorted ? viewNumber : originalIndex).ToString(CultureInfo.InvariantCulture),
                _number, Color.FromArgb(78, 92, 82),
                new RectangleF(row.X + 6, row.Y, indexStart - row.X - 10, row.Height), true);
            if (sorted)
                DrawCell(graphics, "[" + originalIndex.ToString(CultureInfo.InvariantCulture) + "]",
                    _index, Color.FromArgb(107, 120, 111),
                    new RectangleF(indexStart, row.Y, _indexWidth, row.Height), true, 2);
        }

        internal void DrawValue(Graphics graphics, string value, Font textFont, Color color, RectangleF bounds, bool numeric) =>
            DrawCell(graphics, value, numeric ? _number : textFont, color, bounds, numeric);

        internal void DrawCell(Graphics graphics, string value, Font font, Color color, RectangleF bounds,
            bool rightAligned = false, float baselineOffset = 0)
        {
            if (bounds.Width <= 0) return;
            if (value.Length > 256) value = value.Substring(0, 253) + "…";
            value = value.Replace('\r', ' ').Replace('\n', ' ');
            float ascent = font.Size * font.FontFamily.GetCellAscent(font.Style) / font.FontFamily.GetEmHeight(font.Style);
            float top = bounds.Y + Baseline + baselineOffset - ascent;
            _format.Alignment = rightAligned ? StringAlignment.Far : StringAlignment.Near;
            using (var brush = new SolidBrush(color))
                graphics.DrawString(value, font, brush, new RectangleF(bounds.X, top, bounds.Width, bounds.Bottom - top), _format);
        }

        public void Dispose()
        {
            _number.Dispose();
            _index.Dispose();
            _format.Dispose();
        }
    }
}
#pragma warning restore CA1416
