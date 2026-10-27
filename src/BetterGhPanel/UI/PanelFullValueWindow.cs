using System;
using Eto.Drawing;
using Eto.Forms;

namespace BetterGhPanel.UI
{
    // A captured string is sufficient: the window never owns the snapshot,
    // changes the selection, or reads or writes the clipboard.
    internal static class PanelFullValueWindow
    {
        internal const string WindowTitle = "Panel+ · Full value";

        internal static void Show(string value)
        {
            var area = Screen.FromPoint(Mouse.Position).WorkingArea;
            int left = (int)Math.Ceiling(area.Left), top = (int)Math.Ceiling(area.Top);
            int availableWidth = Math.Max(1, (int)Math.Floor(area.Right) - left);
            int availableHeight = Math.Max(1, (int)Math.Floor(area.Bottom) - top);
            int width = Math.Max(1, Math.Min(680, availableWidth - 32));
            int height = Math.Max(1, Math.Min(520, availableHeight - 32));
            var text = new TextArea { Text = value, ReadOnly = true, Wrap = true };
            var window = new Form
            {
                Title = WindowTitle,
                Content = text,
                Resizable = true,
                Bounds = new Rectangle(left + (availableWidth - width) / 2, top + (availableHeight - height) / 2, width, height)
            };
            window.Closed += (_, __) => window.Dispose();
            try { window.Show(); }
            catch { window.Dispose(); throw; }
        }
    }
}
