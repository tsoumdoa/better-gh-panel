using System;
using System.Collections.Generic;

namespace BetterGhPanel.Core
{
    public enum PanelDotSize { Small, Medium, Large }

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
        internal int Band { get; }
    }

    public struct PanelDotOutput
    {
        public double TextScale { get; internal set; }
        public double SwatchRadius { get; internal set; }
        // Indices into the input: every member retains its own dot; the group's
        // text uses the representative's content at the anchor member's position.
        public int LabelAnchor { get; internal set; }
    }

    /// <summary>Screen-only dot layout. Buffers and spatial hashes are reused across frames.</summary>
    public sealed class PanelDotLayout
    {
        private const int BandCount = 10;
        private const double Group = 1.5, AllowedOverlap = .15;
        private static readonly double[] ShrinkScales = { .95, .90, .85, .80, .75, .70 };
        private int[] _order = Array.Empty<int>(), _nextBand = Array.Empty<int>(), _blocker = Array.Empty<int>();
        private int[] _count = Array.Empty<int>(), _anchor = Array.Empty<int>(), _representative = Array.Empty<int>();
        private readonly double[] _roundedScales = new double[ShrinkScales.Length];
        private PlacedBox[] _boxes = Array.Empty<PlacedBox>();
        private double[] _sumX = Array.Empty<double>(), _sumY = Array.Empty<double>(), _best = Array.Empty<double>();
        private readonly int[] _bandHead = new int[BandCount], _bandTail = new int[BandCount];
        private bool[] _previousText = Array.Empty<bool>();
        private readonly HashSet<int> _previousSparseText = new HashSet<int>();
        private readonly Dictionary<int, int> _firstText = new Dictionary<int, int>();
        private readonly Grid _groups = new Grid(), _placed = new Grid(), _all = new Grid();
        public PanelDotOutput[] Results { get; private set; } = Array.Empty<PanelDotOutput>();
        public int TextCount { get; private set; }
        public int TextIndex(int index) => _order[index];
        public void ResetStability()
        {
            Array.Clear(_previousText, 0, _previousText.Length);
            _previousSparseText.Clear();
        }

        public static int ValueBand(double value, double min, double max)
        {
            if (double.IsNaN(value) || !(max > min)) return 0;
            if (double.IsPositiveInfinity(max - min)) { value *= .5; min *= .5; max *= .5; }
            double fraction = Math.Max(0, Math.Min(1, (value - min) / (max - min)));
            return Math.Min(BandCount - 1, (int)(fraction * BandCount));
        }

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
        public void Calculate(PanelDotInput[] dots, int count, double size = 1, double dpi = 1, double fontHeight = 0)
        {
            Ensure(count);
            double fullScale = fontHeight > 0 ? RoundedScale(1, fontHeight) : 1;
            int shrinkCount = 0;
            double previous = fullScale;
            foreach (double scale in ShrinkScales)
            {
                double rounded = fontHeight > 0 ? RoundedScale(scale, fontHeight) : scale;
                if (rounded == previous) continue;
                _roundedScales[shrinkCount++] = previous = rounded;
            }
            double maxWidth = 1, maxHeight = 1, left = 0, right = 0, bottom = 0, top = 0;
            for (int i = 0; i < count; i++)
            {
                ref readonly var dot = ref dots[i];
                maxWidth = Math.Max(maxWidth, dot.Width); maxHeight = Math.Max(maxHeight, dot.Height);
                left = Math.Min(left, dot.X); right = Math.Max(right, dot.X);
                bottom = Math.Min(bottom, dot.Y); top = Math.Max(top, dot.Y);
            }
            _groups.Reset(maxWidth * Group, maxWidth * Group, count);
            _placed.Reset(maxWidth * fullScale, maxHeight * fullScale, count, left, right, bottom, top);
            double groupCap = 3 * size * dpi, cap = 5 * size * dpi;
            double floor = 1.5 * dpi, gap = .5 * dpi;
            double swatchCellSize = 2 * (cap + gap);
            _all.Reset(swatchCellSize, swatchCellSize, count, left, right, bottom, top);
            GroupDots(dots, count);
            int representatives = OrderRepresentatives(dots, count);

            for (int k = 0; k < representatives; k++)
            {
                int i = _order[k];
                int blocker = -1;
                if (Fits(dots, i, fullScale, ref blocker)) Place(dots, i, fullScale);
                else _blocker[i] = blocker;
            }
            for (int k = 0; k < representatives && shrinkCount != 0; k++)
            {
                int i = _order[k];
                if (Results[i].TextScale != 0) continue;
                int blocker = _blocker[i];
                double smallest = _roundedScales[shrinkCount - 1];
                // The smallest overlap can rule out all larger boxes if it
                // exceeds even the largest candidate's allowed overlap.
                if (!Fits(dots, i, smallest, ref blocker, _roundedScales[0])) continue;
                if (shrinkCount == 1) { Place(dots, i, smallest); continue; }
                for (int step = 0; step < shrinkCount; step++)
                {
                    double scale = _roundedScales[step];
                    if (Fits(dots, i, scale, ref blocker))
                    {
                        Place(dots, i, scale);
                        break;
                    }
                }
            }
            TextCount = 0;
            ResetStability();
            for (int k = 0; k < representatives; k++)
            {
                int i = _order[k];
                if (Results[i].TextScale == 0) continue;
                _order[TextCount++] = i;
                int id = dots[i].ItemId;
                if ((uint)id < (uint)_previousText.Length) _previousText[id] = true;
                else _previousSparseText.Add(id);
            }
            double stop = 2 * (floor + gap); stop *= stop;
            for (int i = 0; i < count; i++)
            {
                ref var result = ref Results[i];
                int representative = _representative[i];
                result.LabelAnchor = _anchor[representative];
                if (i != result.LabelAnchor || Results[representative].TextScale == 0)
                    result.SwatchRadius = Radius(i, dots, _count[representative] > 1 ? groupCap : cap, floor, gap, stop);
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
                Results[i] = default;
                _representative[i] = home;
                _count[home]++; _sumX[home] += dot.X; _sumY[home] += dot.Y;
                _all.Add(i, dot.X, dot.Y);
            }
            for (int i = 0; i < count; i++)
            {
                int r = _representative[i];
                if (_count[r] == 1) continue;
                // The representative precedes its members in input order.
                if (r == i) { _sumX[r] /= _count[r]; _sumY[r] /= _count[r]; }
                double dx = dots[i].X - _sumX[r], dy = dots[i].Y - _sumY[r];
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
                if (_representative[i] != i) continue;
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
                if (_representative[i] != i || dot.Selected || i == low || i == high) continue;
                int id = dot.ItemId;
                if (((uint)id < (uint)_previousText.Length && _previousText[id]) ||
                    (_previousSparseText.Count != 0 && _previousSparseText.Contains(id))) { _order[cursor++] = i; continue; }
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

        private bool Fits(PanelDotInput[] dots, int i, double scale, ref int blocker, double allowanceScale = 0)
        {
            ref readonly var dot = ref dots[i];
            ref readonly var anchor = ref dots[_anchor[i]];
            double width = dot.Width * scale, height = dot.Height * scale;
            double area = allowanceScale > scale ? (dot.Width * allowanceScale) * (dot.Height * allowanceScale) : width * height;
            // The same placed label often blocks several successive sizes.
            if (blocker >= 0 && _boxes[blocker].Conflicts(in anchor, width, height, area)) return false;
            int cellX = _placed.X(anchor.X), cellY = _placed.Y(anchor.Y);
            for (int x = 0; x < 3; x++) for (int y = 0; y < 3; y++)
                for (int j = _placed.Head(cellX + Neighbour(x), cellY + Neighbour(y)); j >= 0; j = _placed.Next[j])
                {
                    if (!_boxes[j].Conflicts(in anchor, width, height, area)) continue;
                    blocker = j;
                    return false;
                }
            return true;
        }

        private readonly struct PlacedBox
        {
            private readonly double _x, _y, _width, _height, _area;
            internal PlacedBox(double x, double y, double width, double height)
            { _x = x; _y = y; _width = width; _height = height; _area = width * height; }
            internal bool Conflicts(in PanelDotInput anchor, double width, double height, double area)
            {
                double sx = (width + _width) * .5 - Math.Abs(anchor.X - _x);
                if (sx <= 0) return false;
                double sy = (height + _height) * .5 - Math.Abs(anchor.Y - _y);
                return sy > 0 && Math.Min(sx, Math.Min(width, _width)) * Math.Min(sy, Math.Min(height, _height)) >
                    AllowedOverlap * Math.Min(area, _area);
            }
        }

        private void Place(PanelDotInput[] dots, int i, double scale)
        {
            ref readonly var anchor = ref dots[_anchor[i]];
            Results[i].TextScale = scale;
            _boxes[i] = new PlacedBox(anchor.X, anchor.Y, dots[i].Width * scale, dots[i].Height * scale);
            _placed.Add(i, anchor.X, anchor.Y);
        }

        private static double RoundedScale(double scale, double fontHeight) => Math.Max(1, Math.Round(scale * fontHeight)) / fontHeight;
        private double Radius(int i, PanelDotInput[] dots, double cap, double floor, double gap, double stop)
        {
            ref readonly var d = ref dots[i];
            int cellX = _all.X(d.X), cellY = _all.Y(d.Y);
            double reach = 2 * (cap + gap);
            double squared = NearestInCell(cellX, cellY, i, dots, in d, reach * reach, stop);
            if (squared <= stop) return floor;

            // The home cell usually finds a close neighbour. Search only cells
            // that could improve on that distance.
            double nearest = Math.Sqrt(squared);
            int left = Math.Max(cellX - 1, _all.X(d.X - nearest)), right = Math.Min(cellX + 1, _all.X(d.X + nearest));
            int bottom = Math.Max(cellY - 1, _all.Y(d.Y - nearest)), top = Math.Min(cellY + 1, _all.Y(d.Y + nearest));
            for (int y = bottom; y <= top; y++) for (int x = left; x <= right; x++)
                if (x != cellX || y != cellY)
                {
                    squared = NearestInCell(x, y, i, dots, in d, squared, stop);
                    if (squared <= stop) return floor;
                }
            return Math.Max(floor, Math.Min(cap, Math.Sqrt(squared) / 2 - gap));
        }

        private double NearestInCell(int x, int y, int i, PanelDotInput[] dots, in PanelDotInput dot, double squared, double stop)
        {
            for (int j = _all.Head(x, y); j >= 0; j = _all.Next[j])
            {
                if (i == j) continue;
                double dx = dots[j].X - dot.X, dy = dots[j].Y - dot.Y;
                double distance = dx * dx + dy * dy;
                if (distance <= stop) return distance;
                if (distance < squared) squared = distance;
            }
            return squared;
        }

        private static int Neighbour(int index) => index == 0 ? 0 : index == 1 ? -1 : 1;

        private void Ensure(int n)
        {
            if (Results.Length >= n) return;
            int capacity = Math.Max(n, Math.Max(16, Results.Length * 2));
            Results = new PanelDotOutput[capacity]; _order = new int[capacity]; _nextBand = new int[capacity]; _blocker = new int[capacity];
            _count = new int[capacity]; _anchor = new int[capacity]; _representative = new int[capacity];
            _boxes = new PlacedBox[capacity];
            _sumX = new double[capacity]; _sumY = new double[capacity]; _best = new double[capacity];
            Array.Resize(ref _previousText, capacity);
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
            private int HashSlot(int x, int y, int text)
            {
                uint hash = unchecked((uint)x * 0x9e3779b1u ^ (uint)y * 0x85ebca77u ^ (uint)text * 0xc2b2ae3du);
                hash ^= hash >> 16;
                int slot = (int)hash & _mask;
                while (_stamp[slot] == _epoch && (_x[slot] != x || _y[slot] != y || _text[slot] != text)) slot = (slot + 1) & _mask;
                return slot;
            }
            internal int Head(int x, int y, int text = 0)
            {
                int slot = _stride > 0 ? (y - _bottom) * _stride + x - _left : HashSlot(x, y, text);
                return _stamp[slot] == _epoch ? _head[slot] : -1;
            }
            internal void Add(int i, double x, double y, int text = 0)
            {
                int cx = X(x), cy = Y(y);
                int slot = _stride > 0 ? (cy - _bottom) * _stride + cx - _left : HashSlot(cx, cy, text);
                Next[i] = _stamp[slot] == _epoch ? _head[slot] : -1;
                _x[slot] = cx; _y[slot] = cy; _text[slot] = text; _head[slot] = i; _stamp[slot] = _epoch;
            }
        }
    }
}
