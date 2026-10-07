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
            using (var source = Load("BetterGhPanel.icon.png")) return new Bitmap(source, 24, 24);
        });

        internal static Bitmap Load(string resourceName)
        {
            using (var stream = typeof(PanelIcon).Assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) throw new InvalidOperationException("Panel+ image resource is missing: " + resourceName);
                using (var source = new Bitmap(stream)) return new Bitmap(source);
            }
        }

        public static Bitmap Create() => Icon.Value;
    }
}
#pragma warning restore CA1416
