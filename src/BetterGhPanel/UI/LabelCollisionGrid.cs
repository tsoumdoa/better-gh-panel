using System;
using System.Collections.Generic;
using System.Drawing;

namespace BetterGhPanel.UI
{
    // Call in priority order. Sparse screen cells avoid comparing every label with every other one.
    internal sealed class LabelCollisionGrid
    {
        private readonly Dictionary<long, List<RectangleF>> _cells = new Dictionary<long, List<RectangleF>>();
        private const float CellSize = 48;

        internal bool TryAdd(RectangleF bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0 || float.IsNaN(bounds.Width) || float.IsNaN(bounds.Height) || float.IsNaN(bounds.X) || float.IsNaN(bounds.Y) ||
                float.IsInfinity(bounds.X) || float.IsInfinity(bounds.Y) || float.IsInfinity(bounds.Right) || float.IsInfinity(bounds.Bottom)) return false;
            int left = (int)Math.Floor(bounds.Left / CellSize), right = (int)Math.Floor(bounds.Right / CellSize);
            int top = (int)Math.Floor(bounds.Top / CellSize), bottom = (int)Math.Floor(bounds.Bottom / CellSize);
            for (int x = left; x <= right; x++)
                for (int y = top; y <= bottom; y++)
                    if (_cells.TryGetValue(Key(x, y), out var occupied))
                        foreach (var previous in occupied)
                            if (previous.IntersectsWith(bounds)) return false;
            for (int x = left; x <= right; x++)
                for (int y = top; y <= bottom; y++)
                {
                    long key = Key(x, y);
                    if (!_cells.TryGetValue(key, out var occupied)) _cells.Add(key, occupied = new List<RectangleF>());
                    occupied.Add(bounds);
                }
            return true;
        }

        private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
    }
}
