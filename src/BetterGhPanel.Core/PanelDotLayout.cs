using System;
using System.Collections.Generic;

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
        public PanelDotInput(double x, double y, double width, double height, int textId, bool selected,
            int itemId, double value = double.NaN, int band = 0)
        { X = x; Y = y; Width = width; Height = height; TextId = textId; Selected = selected; ItemId = itemId; Value = value; Band = band; }
        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }
        public int TextId { get; }
        public bool Selected { get; }
        public int ItemId { get; }
        public double Value { get; }
        public int Band { get; }
    }

    public struct PanelDotOutput
    {
        public PanelDotTier Tier { get; internal set; }
        public double TextScale { get; internal set; }
        public double SwatchRadius { get; internal set; }
        // Indices into the input: every member retains its own dot; the group's
        // text uses the representative's content at the anchor member's position.
        public int Representative { get; internal set; }
        public int LabelAnchor { get; internal set; }
        public int GroupCount { get; internal set; }
    }

    /// <summary>Screen-only dot layout. Buffers and spatial hashes are reused across frames.</summary>
    public sealed class PanelDotLayout
    {
        public const int BandCount = 10;
        private const double Group = 1.5, AllowedOverlap = .15, MinimumTextScale = 8.0 / 12;
        private const int FirstShrinkStep = 19, LastShrinkStep = (int)(MinimumTextScale * 20 + 1);
        private static readonly double[] TextScales = { .70, .75, .80, .85, .90, .95, 1 };
        private int[] _order = Array.Empty<int>(), _nextBand = Array.Empty<int>();
        private bool[] _retry = Array.Empty<bool>();
        private int[] _count = Array.Empty<int>(), _anchor = Array.Empty<int>();
        private double[] _sumX = Array.Empty<double>(), _sumY = Array.Empty<double>(), _best = Array.Empty<double>();
        private readonly int[] _bandHead = new int[BandCount], _bandTail = new int[BandCount];
        private readonly HashSet<int> _previousText = new HashSet<int>();
        private readonly Dictionary<int, int> _firstText = new Dictionary<int, int>();
        private readonly Grid _groups = new Grid(), _placed = new Grid(), _all = new Grid();
        public PanelDotOutput[] Results { get; private set; } = Array.Empty<PanelDotOutput>();
        public int Count { get; private set; }
        public int TextCount { get; private set; }
        public int TextIndex(int index) => _order[index];
        public void ResetStability() => _previousText.Clear();

        public static uint ItemHash(int itemId)
        {
            unchecked
            {
                uint hash = (uint)itemId;
                hash = (hash ^ (hash >> 16)) * 0x7feb352du;
                hash = (hash ^ (hash >> 15)) * 0x846ca68bu;
                return hash ^ (hash >> 16);
            }
        }

        // Inputs arrive in ItemHash order. Negative text IDs are never grouped.
        public void Calculate(PanelDotInput[] dots, int count, double size = 1, double dpi = 1)
        {
            Count = count;
            Ensure(count);
            double maxWidth = 1, maxHeight = 1, left = 0, right = 0, bottom = 0, top = 0;
            for (int i = 0; i < count; i++)
            {
                ref readonly var dot = ref dots[i];
                maxWidth = Math.Max(maxWidth, dot.Width); maxHeight = Math.Max(maxHeight, dot.Height);
                left = Math.Min(left, dot.X); right = Math.Max(right, dot.X);
                bottom = Math.Min(bottom, dot.Y); top = Math.Max(top, dot.Y);
            }
            _groups.Reset(maxWidth * Group, maxWidth * Group, count);
            _placed.Reset(maxWidth, maxHeight, count, left, right, bottom, top);
            double swatchCellSize = 2 * (10 * size + 1) * dpi;
            _all.Reset(swatchCellSize, swatchCellSize, count, left, right, bottom, top);
            GroupDots(dots, count);
            int representatives = OrderRepresentatives(dots, count);

            for (int k = 0; k < representatives; k++)
            {
                int i = _order[k];
                int step = Fit(dots, i, 20, 20);
                _retry[i] = step == 0;
                if (step > 0) Place(dots, i, step);
            }
            for (int k = 0; k < representatives; k++)
            {
                int i = _order[k];
                if (!_retry[i]) continue;
                int step = Fit(dots, i, FirstShrinkStep, LastShrinkStep);
                if (step != 0) Place(dots, i, step);
            }

            TextCount = 0;
            _previousText.Clear();
            for (int k = 0; k < representatives; k++)
            {
                int i = _order[k];
                if (Results[i].TextScale == 0) continue;
                _order[TextCount++] = i;
                _previousText.Add(dots[i].ItemId);
            }
            for (int i = 0; i < count; i++)
            {
                ref var result = ref Results[i];
                int representative = result.Representative;
                result.LabelAnchor = _anchor[representative];
                result.GroupCount = _count[representative];
                if (i != result.LabelAnchor || Results[representative].TextScale == 0)
                    result.SwatchRadius = Radius(i, dots, (result.GroupCount > 1 ? 3 : 5) * size * dpi, 1.5 * dpi, .5 * dpi);
            }
        }

        private void GroupDots(PanelDotInput[] dots, int count)
        {
            _firstText.Clear();
            for (int i = 0; i < count; i++)
            {
                ref readonly var dot = ref dots[i];
                int home = -1;
                if (!dot.Selected && dot.TextId >= 0)
                {
                    if (!_firstText.TryGetValue(dot.TextId, out int first)) _firstText.Add(dot.TextId, i);
                    else if (first >= 0)
                    {
                        ref readonly var other = ref dots[first];
                        if (CanGroup(dot, other)) home = first;
                        else
                        {
                            // A text ID needs spatial lookup only once it has
                            // more than one representative in the viewport.
                            _groups.Add(first, other.X, other.Y, dot.TextId);
                            _groups.Add(i, dot.X, dot.Y, dot.TextId);
                            _firstText[dot.TextId] = -1;
                        }
                    }
                    else home = FindGroup(dots, i);
                }
                if (home < 0)
                {
                    home = i;
                    _count[i] = 0; _sumX[i] = _sumY[i] = 0;
                    _best[i] = double.PositiveInfinity; _anchor[i] = i;
                }
                Results[i] = new PanelDotOutput { Representative = home, Tier = home == i ? PanelDotTier.Swatch : PanelDotTier.GroupMember };
                _count[home]++; _sumX[home] += dot.X; _sumY[home] += dot.Y;
                _all.Add(i, dot.X, dot.Y);
            }
            for (int i = 0; i < count; i++)
            {
                int r = Results[i].Representative;
                if (_count[r] == 1) continue;
                double dx = dots[i].X - _sumX[r] / _count[r], dy = dots[i].Y - _sumY[r] / _count[r];
                double distance = dx * dx + dy * dy;
                if (distance < _best[r]) { _best[r] = distance; _anchor[r] = i; }
            }
        }

        private int FindGroup(PanelDotInput[] dots, int i)
        {
            ref readonly var dot = ref dots[i];
            int home = -1, cellX = _groups.X(dot.X), cellY = _groups.Y(dot.Y);
            // Follow the mock's cell order, then take the earliest rep in
            // that cell. The grid stores its linked lists in reverse order.
            for (int x = -1; x <= 1 && home < 0; x++) for (int y = -1; y <= 1 && home < 0; y++)
                for (int r = _groups.Head(cellX + x, cellY + y, dot.TextId); r >= 0; r = _groups.Next[r])
                    if (CanGroup(dot, dots[r])) home = r;
            if (home < 0) _groups.Add(i, dot.X, dot.Y, dot.TextId);
            return home;
        }

        private static bool CanGroup(in PanelDotInput a, in PanelDotInput b)
        {
            double reach = Group * (a.Width + b.Width) * .5;
            return Math.Abs(a.X - b.X) < reach && Math.Abs(a.Y - b.Y) < reach;
        }

        private int OrderRepresentatives(PanelDotInput[] dots, int count)
        {
            int cursor = 0, low = -1, high = -1;
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                if (Results[i].Representative != i) continue;
                if (dots[i].Selected) _order[cursor++] = i;
                double value = dots[i].Value;
                if (value < min) { min = value; low = i; }
                if (value > max) { max = value; high = i; }
            }
            if (low >= 0 && !dots[low].Selected) _order[cursor++] = low;
            if (high >= 0 && high != low && !dots[high].Selected) _order[cursor++] = high;
            for (int band = 0; band < BandCount; band++) _bandHead[band] = -1;
            for (int i = 0; i < count; i++)
            {
                ref readonly var dot = ref dots[i];
                if (Results[i].Representative != i || dot.Selected || i == low || i == high) continue;
                if (_previousText.Contains(dot.ItemId)) { _order[cursor++] = i; continue; }
                int band = dot.Band;
                if (_bandHead[band] < 0) _bandHead[band] = i;
                else _nextBand[_bandTail[band]] = i;
                _bandTail[band] = i;
                _nextBand[i] = -1;
            }
            int active = (1 << BandCount) - 1;
            while (active != 0)
                for (int band = 0; band < BandCount; band++)
                {
                    int i = _bandHead[band];
                    if (i < 0) { active &= ~(1 << band); continue; }
                    _order[cursor++] = i;
                    _bandHead[band] = _nextBand[i];
                }
            return cursor;
        }

        private int Fit(PanelDotInput[] dots, int i, int largest, int smallest)
        {
            ref readonly var dot = ref dots[i];
            ref readonly var anchor = ref dots[_anchor[i]];
            double largestScale = TextScales[largest - LastShrinkStep], smallestScale = TextScales[smallest - LastShrinkStep];
            double maxWidth = dot.Width * largestScale, maxHeight = dot.Height * largestScale;
            double minWidth = dot.Width * smallestScale, minHeight = dot.Height * smallestScale;
            double maxArea = maxWidth * maxHeight;
            int cellX = _placed.X(anchor.X), cellY = _placed.Y(anchor.Y);
            // Each bit is a candidate size. Visit the neighbours once, testing
            // their boxes against the sizes that still fit.
            int sizes = (2 << (largest - smallest)) - 1;
            for (int x = 0; x < 3; x++) for (int y = 0; y < 3; y++)
                for (int j = _placed.Head(cellX + Neighbour(x), cellY + Neighbour(y)); j >= 0; j = _placed.Next[j])
                {
                    ref readonly var other = ref dots[_anchor[j]];
                    double dx = Math.Abs(anchor.X - other.X), dy = Math.Abs(anchor.Y - other.Y);
                    double width = dots[j].Width * Results[j].TextScale, height = dots[j].Height * Results[j].TextScale;
                    double xReach = width / 2 - dx, yReach = height / 2 - dy;
                    if (xReach + maxWidth * .5 <= 0 || yReach + maxHeight * .5 <= 0) continue;
                    double area = width * height;
                    double allowance = AllowedOverlap * Math.Min(maxArea, area);
                    if (smallest == largest)
                    {
                        if (!Overlaps(maxWidth, maxHeight, xReach, yReach, width, height, allowance)) continue;
                        double minScale = TextScales[0];
                        // A blocker already placed in the full-size pass stays
                        // in place. Skip retries that it rules out at every size.
                        return Overlaps(dot.Width * minScale, dot.Height * minScale, xReach, yReach, width, height, allowance) ? -1 : 0;
                    }
                    // If even the smallest box overlaps more than the largest
                    // allowance, no candidate size can fit this neighbour.
                    if (Overlaps(minWidth, minHeight, xReach, yReach, width, height, allowance)) return 0;
                    for (int step = largest, bit = 1 << (largest - smallest); bit != 0; step--, bit >>= 1)
                    {
                        if ((sizes & bit) == 0) continue;
                        double scale = TextScales[step - LastShrinkStep];
                        double w = dot.Width * scale, h = dot.Height * scale;
                        if (Overlaps(w, h, xReach, yReach, width, height, AllowedOverlap * Math.Min(w * h, area)))
                            sizes &= ~bit;
                    }
                    if (sizes == 0) return 0;
                }
            for (int step = largest, bit = 1 << (largest - smallest); bit != 0; step--, bit >>= 1)
                if ((sizes & bit) != 0) return step;
            return 0;
        }

        private static bool Overlaps(double width, double height, double xReach, double yReach, double otherWidth, double otherHeight, double allowance)
        {
            double sx = xReach + width * .5, sy = yReach + height * .5;
            return sx > 0 && sy > 0 && Math.Min(sx, Math.Min(width, otherWidth)) * Math.Min(sy, Math.Min(height, otherHeight)) > allowance;
        }

        private void Place(PanelDotInput[] dots, int i, int step)
        {
            ref readonly var anchor = ref dots[_anchor[i]];
            Results[i].TextScale = TextScales[step - LastShrinkStep];
            Results[i].Tier = step == 20 ? PanelDotTier.Full : PanelDotTier.Text;
            _placed.Add(i, anchor.X, anchor.Y);
        }
        private double Radius(int i, PanelDotInput[] dots, double cap, double floor, double gap)
        {
            ref readonly var d = ref dots[i]; double nearest = 2 * (cap + gap), squared = nearest * nearest;
            double stop = 2 * (floor + gap); stop *= stop;
            int left = _all.X(d.X - nearest), right = _all.X(d.X + nearest);
            int bottom = _all.Y(d.Y - nearest), top = _all.Y(d.Y + nearest);
            int cellX = _all.X(d.X), cellY = _all.Y(d.Y);
            for (int cx = 0; cx <= right - left; cx++) for (int cy = 0; cy <= top - bottom; cy++)
            {
                int x = cx <= cellX - left ? cellX - cx : left + cx;
                int y = cy <= cellY - bottom ? cellY - cy : bottom + cy;
                for (int j = _all.Head(x, y); j >= 0; j = _all.Next[j])
                {
                    if (i == j) continue;
                    double dx = dots[j].X - d.X, dy = dots[j].Y - d.Y;
                    squared = Math.Min(squared, dx * dx + dy * dy);
                    if (squared <= stop) return floor;
                }
            }
            return Math.Max(floor, Math.Min(cap, Math.Sqrt(squared) / 2 - gap));
        }

        private static int Neighbour(int index) => index == 0 ? 0 : index == 1 ? -1 : 1;

        private void Ensure(int n)
        {
            if (Results.Length >= n) return;
            int capacity = Math.Max(n, Math.Max(16, Results.Length * 2));
            Results = new PanelDotOutput[capacity]; _order = new int[capacity]; _nextBand = new int[capacity];
            _retry = new bool[capacity];
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
            private int _stride, _left, _bottom;
            private double _inverseWidth, _inverseHeight;
            internal int[] Next = Array.Empty<int>();
            internal void Reset(double width, double height, int count, double left = 0, double right = -1, double bottom = 0, double top = -1)
            {
                _inverseWidth = 1 / width; _inverseHeight = 1 / height;
                _left = X(left) - 1; _bottom = Y(bottom) - 1;
                int columns = X(right) - _left + 2, rows = Y(top) - _bottom + 2;
                long cells = (long)columns * rows;
                _stride = right >= left && cells <= count * 4L + 16 ? columns : 0;
                int capacity = 16;
                long slots = _stride > 0 ? cells : count * 2L;
                while (capacity < slots) capacity *= 2;
                if (_head.Length < capacity)
                {
                    _x = new int[capacity]; _y = new int[capacity]; _text = new int[capacity];
                    _head = new int[capacity]; _stamp = new int[capacity]; _mask = capacity - 1; _epoch = 0;
                }
                if (++_epoch == int.MaxValue) { Array.Clear(_stamp, 0, _stamp.Length); _epoch = 1; }
                if (Next.Length < count) Next = new int[Math.Max(count, Next.Length * 2)];
            }
            internal int X(double x) => (int)Math.Floor(x * _inverseWidth);
            internal int Y(double y) => (int)Math.Floor(y * _inverseHeight);
            private int Slot(int x, int y, int text)
            {
                // Non-grouped grids can address compact screen regions directly.
                if (_stride > 0) return (y - _bottom) * _stride + x - _left;
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
