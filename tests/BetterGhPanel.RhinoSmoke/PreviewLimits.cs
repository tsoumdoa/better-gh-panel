using System.Reflection;

// Most preview safety fixtures were written against the original per-panel
// 64 MiB budget and 20,000-visit turns. They exercise the accounting logic, so
// they keep those limits; preview-large-geometry checks the shipped defaults.
internal static class PreviewLimits
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    internal static void UseLegacy(Assembly plugin) => Set(plugin, 64L * 1024 * 1024, 20000, false);

    internal static void UseDefaults(Assembly plugin)
    {
        var highlight = plugin.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!;
        var sources = plugin.GetType("BetterGhPanel.Runtime.PanelGeometrySources", true)!;
        Set(plugin, (long)highlight.GetField("DefaultMaxRetainedBytes", Static)!.GetValue(null)!,
            (int)sources.GetField("DefaultMaxNodesPerOperation", Static)!.GetValue(null)!, true);
    }

    internal static void SetRetainedBytes(Assembly plugin, long bytes) =>
        plugin.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!.GetProperty("MaxRetainedBytes", Static)!.SetValue(null, bytes);

    private static void Set(Assembly plugin, long bytes, int nodes, bool shared)
    {
        var highlight = plugin.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!;
        highlight.GetProperty("MaxRetainedBytes", Static)!.SetValue(null, bytes);
        highlight.GetProperty("ShareRetainedBytes", Static)!.SetValue(null, shared);
        plugin.GetType("BetterGhPanel.Runtime.PanelGeometrySources", true)!.GetProperty("MaxNodesPerOperation", Static)!.SetValue(null, nodes);
    }
}
