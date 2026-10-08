using System;

namespace BetterGhPanel.UI
{
    internal enum CanvasPanelDisplay { Full, Half, Collapsed }

    // Dimensions are canonical; presets are commands, not separately persisted state.
    internal static class CanvasPanelSize
    {
        internal const float DefaultWidth = 360;
        internal const float DefaultHeight = 360;
        internal const float HeaderHeight = 22;
        internal static readonly string[] PresetNames = { "Small", "Medium", "Large" };

        internal static float Width(float value) => Clamp(value, DefaultWidth, 300);
        internal static float Height(float value) => Clamp(value, DefaultHeight, 220);
        internal static CanvasPanelDisplay NextDisplay(CanvasPanelDisplay display) =>
            display == CanvasPanelDisplay.Full ? CanvasPanelDisplay.Half :
            display == CanvasPanelDisplay.Half ? CanvasPanelDisplay.Collapsed : CanvasPanelDisplay.Full;
        internal static float DisplayHeight(float fullHeight, CanvasPanelDisplay display) =>
            display == CanvasPanelDisplay.Collapsed ? 28 :
            display == CanvasPanelDisplay.Half ? Height(fullHeight) / 2 : Height(fullHeight);
        internal static float DisplayTopOffset(float fullHeight, CanvasPanelDisplay display) =>
            (Height(fullHeight) - DisplayHeight(fullHeight, display)) / 2 +
            (display == CanvasPanelDisplay.Collapsed ? HeaderHeight / 2 : 0);
        internal static float PresetWidth(int index) => 320 + index * 60;
        internal static float PresetHeight(int index) => 240 + index * 80;

        internal static int PresetIndex(float width, float height)
        {
            for (var i = 0; i < PresetNames.Length; i++)
                if (width == PresetWidth(i) && height == PresetHeight(i)) return i;
            return -1;
        }

        private static float Clamp(float value, float fallback, float minimum) =>
            float.IsNaN(value) || float.IsInfinity(value) ? fallback : Math.Max(minimum, Math.Min(900, value));
    }
}
