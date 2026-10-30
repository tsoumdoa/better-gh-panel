using System;
using System.Drawing;
using Grasshopper.GUI.Canvas;

// Rhino supplies System.Drawing on the Grasshopper canvas.
#pragma warning disable CA1416
namespace BetterGhPanel.UI
{
    // Panel+ is designed against a light canvas. A dark canvas skin (Grasshopper
    // Preferences → Display) mirrors each colour's lightness and keeps its hue, so
    // the panel never glares on a dark canvas and contrast relationships survive.
    internal static class PanelTheme
    {
        internal static bool Dark
        {
            get
            {
                try { return GH_Skin.canvas_back.GetBrightness() < 0.35f; }
                catch (Exception) { return false; }
            }
        }

        internal static Color Background => Map(Color.White);
        internal static Color Ink => Rgb(49, 55, 53);
        internal static Color Muted => Rgb(120, 127, 123);
        internal static Color Accent => Rgb(77, 115, 94);
        internal static Color Warning => Rgb(180, 106, 67);
        internal static Color Unverified => Rgb(103, 112, 166);
        // Translucent overlays drawn above any control or row on hover.
        internal static Color Hover => Color.FromArgb(Dark ? 40 : 26, Dark ? 170 : 77, Dark ? 214 : 115, Dark ? 186 : 94);
        internal static Color Shadow => Color.FromArgb(Dark ? 70 : 32, 0, 0, 0);

        internal static Color Rgb(int red, int green, int blue) => Map(Color.FromArgb(red, green, blue));
        internal static Color Rgb(int alpha, int red, int green, int blue) => Map(Color.FromArgb(alpha, red, green, blue));

        internal static Color Map(Color color)
        {
            if (!Dark) return color;
            float lightness = color.GetBrightness();
            // White becomes a dark surface, dark ink becomes light text.
            return FromHsl(color.A, color.GetHue(), color.GetSaturation(), 0.13f + (1 - lightness) * 0.8f);
        }

        private static Color FromHsl(int alpha, float hue, float saturation, float lightness)
        {
            float q = lightness < 0.5f ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
            float p = 2 * lightness - q;
            float h = hue / 360f;
            int Channel(float t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                float value = t < 1f / 6 ? p + (q - p) * 6 * t : t < 0.5f ? q : t < 2f / 3 ? p + (q - p) * (2f / 3 - t) * 6 : p;
                return Math.Max(0, Math.Min(255, (int)Math.Round(value * 255)));
            }
            return Color.FromArgb(alpha, Channel(h + 1f / 3), Channel(h), Channel(h - 1f / 3));
        }
    }
}
#pragma warning restore CA1416
