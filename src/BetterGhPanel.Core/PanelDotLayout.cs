using System;
using System.Diagnostics;

namespace BetterGhPanel.Core
{
    public enum PanelDotSize { Small, Medium, Large }
    public enum PanelDotTier { GroupMember, Swatch, Text, Full }

    public static class PanelDotSettings
    {
        public static double Scale(PanelDotSize size) => size == PanelDotSize.Small ? .85 : size == PanelDotSize.Large ? 1.25 : 1;
        public static bool ValidLimit(int limit) => limit == 1000 || limit == 5000 || limit == 20000;
        public static int LimitAt(int index) => index == 0 ? 1000 : index == 2 ? 20000 : 5000;
    }

    public readonly struct PanelDotInput
    {
        public PanelDotInput(double x, double y, double width, double height, int textId, bool selected, int priority)
        { X = x; Y = y; Width = width; Height = height; TextId = textId; Selected = selected; Priority = priority; }
        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }
        public int TextId { get; }
        public bool Selected { get; }
        // Input must be ordered by ascending priority; selected dots draw on top.
        public int Priority { get; }
    }

    public struct PanelDotOutput
    {
        public PanelDotTier Tier { get; internal set; }
        public double TextScale { get; internal set; }
        public double SwatchRadius { get; internal set; }
        public double GroupSwatchRadius { get; internal set; }
        // Indices into the input: every member retains its own dot; the group's
        // text uses the representative's content at the anchor member's position.
        public int Representative { get; internal set; }
        public int LabelAnchor { get; internal set; }
        public int GroupCount { get; internal set; }
    }

    /// <summary>Screen-only dot layout. Buffers and spatial hashes are reused across frames.</summary>
    public sealed class PanelDotLayout
    {
        public const double MinimumTextScale = 8.0 / 12;
        private const double Group = 1.5;
        private int[] _order = Array.Empty<int>();
        private int[] _count = Array.Empty<int>(), _anchor = Array.Empty<int>();
        private double[] _sumX = Array.Empty<double>(), _sumY = Array.Empty<double>(), _best = Array.Empty<double>();
        private readonly Grid _groups = new Grid(), _fits = new Grid(), _all = new Grid();
        public PanelDotOutput[] Results { get; private set; } = Array.Empty<PanelDotOutput>();
        public int Count { get; private set; }
        public int PriorityIndex(int index) => _order[index];

        public void Calculate(PanelDotInput[] dots, int count, double size = 1, double dpi = 1)
        {
            int n = Count = count;
            Ensure(n);
            double maxWidth = 1, maxHeight = 1;
            for (int i = 0; i < n; i++)
            {
                ref readonly var d = ref dots[i];
                _count[i] = 0; _sumX[i] = _sumY[i] = 0; _best[i] = double.PositiveInfinity;
                _anchor[i] = i;
                maxWidth = Math.Max(maxWidth, d.Width); maxHeight = Math.Max(maxHeight, d.Height);
                Debug.Assert(i == 0 || dots[i - 1].Priority <= d.Priority, "Dot inputs must arrive in view priority order.");
            }
            int cursor = 0;
            for (int i = 0; i < n; i++) if (dots[i].Selected) _order[cursor++] = i;
            for (int i = 0; i < n; i++) if (!dots[i].Selected) _order[cursor++] = i;

            _groups.Reset(maxWidth * Group, maxWidth * Group, n); _fits.Reset(maxWidth, maxHeight, n);
            double floor = 1.5 * dpi;
            _all.Reset((10 * size + 1) * dpi, (10 * size + 1) * dpi, n);
            for (int k = 0; k < n; k++)
            {
                int i = _order[k]; ref readonly var d = ref dots[i]; int home = -1, rank = int.MaxValue;
                // The approved mock uses combined half-width in both grouping axes.
                if (!d.Selected)
                {
                    int cellX = _groups.X(d.X), cellY = _groups.Y(d.Y);
                    // Match the mock's cell traversal and insertion order. Linked
                    // buckets run backwards, so choose their earliest eligible rep.
                    for (int x = -1; x <= 1 && home < 0; x++) for (int y = -1; y <= 1 && home < 0; y++)
                        for (int r = _groups.Head(cellX + x, cellY + y, d.TextId); r >= 0; r = _groups.Next[r])
                        {
                            ref readonly var other = ref dots[r];
                            if ((home < 0 || other.Priority < rank) && Math.Abs(other.X - d.X) < Group * (other.Width + d.Width) / 2 &&
                                Math.Abs(other.Y - d.Y) < Group * (other.Width + d.Width) / 2)
                            { home = r; rank = other.Priority; }
                        }
                }
                if (home < 0) { home = i; if (!d.Selected) _groups.Add(i, d.X, d.Y, d.TextId); }
                Results[i] = new PanelDotOutput { Representative = home, LabelAnchor = i, Tier = PanelDotTier.GroupMember };
                _count[home]++; _sumX[home] += d.X; _sumY[home] += d.Y;
                _all.Add(i, d.X, d.Y);
            }
            // Nearest member to the centroid; ties resolve in priority order.
            for (int k = 0; k < n; k++)
            {
                int i = _order[k], r = Results[i].Representative;
                double dx = dots[i].X - _sumX[r] / _count[r], dy = dots[i].Y - _sumY[r] / _count[r];
                double distance = dx * dx + dy * dy;
                if (distance < _best[r]) { _best[r] = distance; _anchor[r] = i; }
            }
            for (int k = 0; k < n; k++)
            {
                int i = _order[k]; if (Results[i].Representative != i) continue;
                ref readonly var a = ref dots[_anchor[i]]; _fits.Add(i, a.X, a.Y);
            }
            for (int k = 0; k < n; k++)
            {
                int i = _order[k], r = Results[i].Representative; ref readonly var d = ref dots[i];
                Results[i].LabelAnchor = _anchor[r]; Results[i].GroupCount = _count[r];
                if (_count[r] > 1) Results[i].GroupSwatchRadius = Radius(i, dots, 3 * size * dpi, floor, .5 * dpi);
                if (r != i) { Results[i].SwatchRadius = Results[i].GroupSwatchRadius; continue; }
                ref readonly var a = ref dots[_anchor[r]];
                double fit = 1, threshold = MinimumTextScale / (d.Selected ? 1.4 : 1);
                int cellX = _fits.X(a.X), cellY = _fits.Y(a.Y);
                bool swatch = false;
                for (int x = 0; x < 3 && !swatch; x++) for (int y = 0; y < 3 && !swatch; y++)
                    for (int j = _fits.Head(cellX + Neighbour(x), cellY + Neighbour(y)); j >= 0; j = _fits.Next[j])
                    {
                        if (i == j) continue;
                        ref readonly var b = ref dots[_anchor[j]];
                        fit = Math.Min(fit, Math.Max(Math.Abs(a.X - b.X) * 2 / (d.Width + dots[j].Width),
                            Math.Abs(a.Y - b.Y) * 2 / (d.Height + dots[j].Height)));
                        // Below the tier threshold, the precise fit cannot affect output.
                        if (fit < threshold) { swatch = true; break; }
                    }
                if (d.Selected) fit = Math.Max(fit, Math.Min(1, fit * 1.4));
                Results[i].Tier = fit >= 1 ? PanelDotTier.Full : fit >= MinimumTextScale ? PanelDotTier.Text : PanelDotTier.Swatch;
                Results[i].TextScale = fit >= 1 ? 1 : fit >= MinimumTextScale ? Math.Floor(fit * 20 + 1e-10) / 20 : 0;
                if (Results[i].Tier == PanelDotTier.Swatch) Results[i].SwatchRadius = Radius(i, dots, 5 * size * dpi, floor, .5 * dpi);
            }
        }

        private double Radius(int i, PanelDotInput[] dots, double cap, double floor, double gap)
        {
            ref readonly var d = ref dots[i]; double nearest = 2 * (cap + gap), squared = nearest * nearest;
            double stop = 2 * (floor + gap); stop *= stop;
            int cellX = _all.X(d.X), cellY = _all.Y(d.Y);
            for (int x = 0; x < 3; x++) for (int y = 0; y < 3; y++)
                for (int j = _all.Head(cellX + Neighbour(x), cellY + Neighbour(y)); j >= 0; j = _all.Next[j])
                {
                    if (i == j) continue;
                    double dx = dots[j].X - d.X, dy = dots[j].Y - d.Y;
                    squared = Math.Min(squared, dx * dx + dy * dy);
                    if (squared <= stop) return floor;
                }
            return Math.Max(floor, Math.Min(cap, Math.Sqrt(squared) / 2 - gap));
        }

        private static int Neighbour(int index) => index == 0 ? 0 : index == 1 ? -1 : 1;

        private void Ensure(int n)
        {
            if (Results.Length >= n) return;
            int capacity = Math.Max(n, Math.Max(16, Results.Length * 2));
            Results = new PanelDotOutput[capacity]; _order = new int[capacity];
            _count = new int[capacity]; _anchor = new int[capacity];
            _sumX = new double[capacity]; _sumY = new double[capacity]; _best = new double[capacity];
        }

        // Open-addressed integer cell table. Epochs make clearing O(1); all
        // storage survives camera changes, without Dictionary or per-cell lists.
        private sealed class Grid
        {
            private int[] _x = Array.Empty<int>(), _y = Array.Empty<int>(), _text = Array.Empty<int>(),
                _head = Array.Empty<int>(), _stamp = Array.Empty<int>();
            private int _mask, _epoch;
            private double _inverseWidth, _inverseHeight;
            internal int[] Next = Array.Empty<int>();
            internal void Reset(double width, double height, int count)
            {
                int capacity = 16;
                while (capacity < count * 2) capacity *= 2;
                if (_head.Length < capacity)
                {
                    _x = new int[capacity]; _y = new int[capacity]; _text = new int[capacity];
                    _head = new int[capacity]; _stamp = new int[capacity]; _mask = capacity - 1; _epoch = 0;
                }
                if (++_epoch == int.MaxValue) { Array.Clear(_stamp, 0, _stamp.Length); _epoch = 1; }
                _inverseWidth = 1 / width; _inverseHeight = 1 / height;
                if (Next.Length < count) Next = new int[Math.Max(count, Next.Length * 2)];
            }
            internal int X(double x) => (int)Math.Floor(x * _inverseWidth);
            internal int Y(double y) => (int)Math.Floor(y * _inverseHeight);
            private int Slot(int x, int y, int text)
            {
                uint hash = unchecked((uint)x * 0x9e3779b1u ^ (uint)y * 0x85ebca77u ^ (uint)text * 0xc2b2ae3du);
                hash ^= hash >> 16;
                int slot = (int)hash & _mask;
                while (_stamp[slot] == _epoch && (_x[slot] != x || _y[slot] != y || _text[slot] != text)) slot = (slot + 1) & _mask;
                return slot;
            }
            internal int Head(int x, int y, int text = 0)
            { int slot = Slot(x, y, text); return _stamp[slot] == _epoch ? _head[slot] : -1; }
            internal void Add(int i, double x, double y, int text = 0)
            {
                int cx = X(x), cy = Y(y), slot = Slot(cx, cy, text);
                Next[i] = _stamp[slot] == _epoch ? _head[slot] : -1;
                _x[slot] = cx; _y[slot] = cy; _text[slot] = text; _head[slot] = i; _stamp[slot] = _epoch;
            }
        }
    }
}
