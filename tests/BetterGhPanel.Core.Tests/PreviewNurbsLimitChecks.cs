using System;
using BetterGhPanel.Runtime;

internal static class PreviewNurbsLimitChecks
{
    internal static void Run()
    {
        Check(PanelNurbsLimits.Curve(3, 3, 4, 5, out long ordinary) && ordinary > 0,
            "ordinary rational-curve dimensions are accepted with a preparation reservation");
        Check(PanelNurbsLimits.Curve(2, 12, 1024, 1034, out long maximum) && maximum > ordinary && maximum < 1024 * 1024,
            "the largest admitted curve has a bounded value and native reservation");
        Check(!PanelNurbsLimits.Curve(3, 2, 1025, 1025, out _) &&
            !PanelNurbsLimits.Curve(3, 13, 20, 31, out _) &&
            !PanelNurbsLimits.Curve(3, 3, 4, 4, out _) &&
            !PanelNurbsLimits.Curve(4, 3, 4, 5, out _) &&
            !PanelNurbsLimits.Curve(3, int.MaxValue, int.MaxValue, int.MaxValue, out _),
            "oversized, malformed and unsupported geometry is rejected before allocation");
        Check(PanelNurbsLimits.Surface(4, 3, 64, 64, 66, 65) &&
            !PanelNurbsLimits.Surface(4, 3, 65, 64, 67, 65) &&
            !PanelNurbsLimits.Surface(4, 3, 64, 64, 65, 65) &&
            !PanelNurbsLimits.Surface(int.MaxValue, 3, int.MaxValue, 64, int.MaxValue, 65),
            "borrowed NURBS surface limits reject excessive or malformed control nets from counts alone");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
