using System;
using System.Drawing;

// Grasshopper's Bitmap contract uses Rhino's host drawing implementation on Mac.
// Never ship Microsoft's Windows-only System.Drawing runtime with the plug-in.
#pragma warning disable CA1416

namespace BetterGhPanel
{
    internal static class PanelIcon
    {
        private static readonly Lazy<Bitmap> Icon = new Lazy<Bitmap>(() =>
        {
            using (var stream = typeof(PanelIcon).Assembly.GetManifestResourceStream("BetterGhPanel.icon.png"))
            {
                if (stream == null) throw new InvalidOperationException("Panel+ icon resource is missing.");
                using (var source = new Bitmap(stream)) return new Bitmap(source, 24, 24);
            }
        });

        public static Bitmap Create() => Icon.Value;
    }
}
#pragma warning restore CA1416
