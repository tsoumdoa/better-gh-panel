namespace BetterGhPanel.Runtime
{
    // The value capture allocates control-point and knot arrays and an owned
    // curve. Keep the arithmetic independent of Rhino for managed checks.
    internal static class PanelNurbsLimits
    {
        internal const int MaxOrder = 12, MaxPoints = 1024;
        internal static bool Curve(int dimension, int order, int count, int knots, out long bytes)
        {
            bytes = 0;
            if ((dimension != 2 && dimension != 3) || order < 2 || order > MaxOrder ||
                count < order || count > MaxPoints || knots != count + order - 2) return false;
            bytes = 65536L + 256L * count + 64L * knots;
            return true;
        }

        // Only counts and orders are read from a borrowed surface. In
        // particular, PointSize is not a const accessor in Rhino 8.36.
        internal static bool Surface(int orderU, int orderV, int countU, int countV, int knotsU, int knotsV)
        {
            return orderU >= 2 && orderU <= MaxOrder && orderV >= 2 && orderV <= MaxOrder &&
                countU >= orderU && countV >= orderV && (long)countU * countV <= 4096 &&
                knotsU == countU + orderU - 2 && knotsV == countV + orderV - 2;
        }
    }
}
