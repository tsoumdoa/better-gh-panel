using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// Rhino supplies the host canvas drawing implementation on Windows and Mac.
#pragma warning disable CA1416
namespace BetterGhPanel.UI
{
    internal enum TreeToolIcon { Trim, Simplify, Flatten, Reverse, CreateSet }

    // Draw in canvas units so the glyphs stay crisp at every zoom and host DPI.
    internal static class TreeToolIcons
    {
        private static readonly Color Muted = Color.FromArgb(120, 127, 123);
        private static readonly Color Accent = Color.FromArgb(77, 115, 94);

        internal static void Draw(Graphics g, RectangleF bounds, TreeToolIcon icon, bool active)
        {
            float size = Math.Min(16, Math.Min(bounds.Width, bounds.Height) - 4);
            if (size <= 0) return;
            if (active)
            {
                using (var brush = new SolidBrush(Color.FromArgb(227, 237, 223))) g.FillRectangle(brush, bounds);
                using (var pen = new Pen(Accent)) g.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
            }

            var state = g.Save();
            try
            {
                g.TranslateTransform(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2);
                g.ScaleTransform(size / 16, size / 16);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(active ? Accent : Muted, 1.2f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                })
                {
                    switch (icon)
                    {
                        case TreeToolIcon.CreateSet:
                            // Braces around a single member identify a set.
                            g.DrawLines(pen, new[] { new PointF(5, 2), new PointF(3.5f, 2), new PointF(3.5f, 6),
                                new PointF(2, 8), new PointF(3.5f, 10), new PointF(3.5f, 14), new PointF(5, 14) });
                            g.DrawLines(pen, new[] { new PointF(11, 2), new PointF(12.5f, 2), new PointF(12.5f, 6),
                                new PointF(14, 8), new PointF(12.5f, 10), new PointF(12.5f, 14), new PointF(11, 14) });
                            g.DrawEllipse(pen, 6.5f, 6.5f, 3, 3);
                            break;
                        case TreeToolIcon.Trim:
                            // A pruned tree, with a broken cut line above its branches.
                            g.DrawLine(pen, 2, 2.5f, 4, 2.5f);
                            g.DrawLine(pen, 7, 2.5f, 9, 2.5f);
                            g.DrawLine(pen, 12, 2.5f, 14, 2.5f);
                            g.DrawLine(pen, 8, 5, 8, 13.5f);
                            g.DrawLines(pen, new[] { new PointF(3, 5), new PointF(8, 9), new PointF(13, 5) });
                            g.DrawLine(pen, 5.5f, 13.5f, 10.5f, 13.5f);
                            break;
                        case TreeToolIcon.Simplify:
                            // Keep the familiar taper to a single, shorter path.
                            g.DrawLines(pen, new[]
                            {
                                new PointF(2.5f, 3), new PointF(13.5f, 3), new PointF(9, 8),
                                new PointF(9, 12), new PointF(7, 13), new PointF(7, 8), new PointF(2.5f, 3)
                            });
                            break;
                        case TreeToolIcon.Flatten:
                            g.DrawLine(pen, 8, 2.5f, 8, 10);
                            g.DrawLines(pen, new[] { new PointF(4, 6.5f), new PointF(8, 10.5f), new PointF(12, 6.5f) });
                            g.DrawLine(pen, 2.5f, 13.5f, 13.5f, 13.5f);
                            break;
                        case TreeToolIcon.Reverse:
                            // Opposed directions distinguish reverse order from reset.
                            g.DrawLine(pen, 4.5f, 13, 4.5f, 3);
                            g.DrawLines(pen, new[] { new PointF(1.5f, 6), new PointF(4.5f, 3), new PointF(7.5f, 6) });
                            g.DrawLine(pen, 11.5f, 3, 11.5f, 13);
                            g.DrawLines(pen, new[] { new PointF(8.5f, 10), new PointF(11.5f, 13), new PointF(14.5f, 10) });
                            break;
                    }
                }
            }
            finally
            {
                g.Restore(state);
            }
        }
    }
}
#pragma warning restore CA1416
