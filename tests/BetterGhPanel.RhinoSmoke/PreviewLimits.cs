using System.Reflection;

// Preview safety checks were written against the original per-panel 64 MiB budget and
// 20,000-visit turns. They exercise the accounting logic, so they keep those limits.
internal static class PreviewLimits
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    internal static void UseLegacy(Assembly plugin) => Set(plugin, 64L * 1024 * 1024, 20000, false);

    private static void Set(Assembly plugin, long bytes, int nodes, bool shared)
    {
        var highlight = plugin.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!;
        highlight.GetProperty("MaxRetainedBytes", Static)!.SetValue(null, bytes);
        highlight.GetProperty("ShareRetainedBytes", Static)!.SetValue(null, shared);
        plugin.GetType("BetterGhPanel.Runtime.PanelGeometrySources", true)!.GetProperty("MaxNodesPerOperation", Static)!.SetValue(null, nodes);
    }
}
