using System;
using System.Drawing;

// Rhino supplies the host canvas drawing implementation on Windows and Mac.
#pragma warning disable CA1416
namespace BetterGhPanel.UI
{
    // Original Grasshopper bitmaps, embedded to avoid private host resource APIs.
    internal static class TreeToolIcons
    {
        internal static readonly Bitmap Trim = PanelIcon.Load("BetterGhPanel.tree-tools.trim.png");
        internal static readonly Bitmap Simplify = PanelIcon.Load("BetterGhPanel.tree-tools.simplify.png");
        internal static readonly Bitmap Flatten = PanelIcon.Load("BetterGhPanel.tree-tools.flatten.png");
        internal static readonly Bitmap Reverse = PanelIcon.Load("BetterGhPanel.tree-tools.reverse.png");
        internal static void Draw(Graphics g, RectangleF bounds, Bitmap icon, bool active)
        {
            if (bounds.IsEmpty) return;
            if (active)
            {
                using (var brush = new SolidBrush(Color.FromArgb(210, 229, 207))) g.FillRectangle(brush, bounds);
                using (var pen = new Pen(Color.FromArgb(100, 144, 98))) g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            }
            float size = Math.Min(20, Math.Min(bounds.Width, bounds.Height) - 4);
            var target = new RectangleF(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2, size, size);
            g.DrawImage(icon, target);
            if (active)
            {
                using (var brush = new SolidBrush(Color.FromArgb(64, 109, 68))) g.FillEllipse(brush, bounds.Right - 5, bounds.Bottom - 5, 4, 4);
            }
        }
    }
}
#pragma warning restore CA1416
