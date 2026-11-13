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
        public PanelDotInput(double x, double y, double width, double height, string text, bool selected, int priority)
        { X = x; Y = y; Width = width; Height = height; Text = text; Selected = selected; Priority = priority; }
        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }
        public string Text { get; }
        public bool Selected { get; }
        // Lower values draw on top. Priorities must uniquely identify view order.
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
        private int[] _order = Array.Empty<int>(), _scratch = Array.Empty<int>();
        private int[] _count = Array.Empty<int>(), _anchor = Array.Empty<int>();
        private double[] _sumX = Array.Empty<double>(), _sumY = Array.Empty<double>(), _best = Array.Empty<double>();
        private readonly int[] _digits = new int[256];
        private readonly Grid _groups = new Grid(), _fits = new Grid(), _all = new Grid();
        public PanelDotOutput[] Results { get; private set; } = Array.Empty<PanelDotOutput>();
        public int Count { get; private set; }
        public long CandidateVisits { get; private set; }
        public int PriorityIndex(int index) => _order[index];

        public void Calculate(IReadOnlyList<PanelDotInput> dots, double size = 1, double dpi = 1)
        {
            int n = Count = dots.Count;
            Ensure(n); CandidateVisits = 0;
            double maxWidth = 1, maxHeight = 1;
            for (int i = 0; i < n; i++)
            {
                _order[i] = i; _count[i] = 0; _sumX[i] = _sumY[i] = 0; _best[i] = double.PositiveInfinity;
                _anchor[i] = i;
                maxWidth = Math.Max(maxWidth, dots[i].Width); maxHeight = Math.Max(maxHeight, dots[i].Height);
            }
            // Four stable radix passes plus selection partition: O(n), independent
            // of the caller's enumeration order (including negative priorities).
            for (int shift = 0; shift < 32; shift += 8)
            {
                Array.Clear(_digits, 0, 256);
                for (int k = 0; k < n; k++) _digits[(int)(((uint)dots[_order[k]].Priority ^ 0x80000000u) >> shift) & 255]++;
                int start = 0;
                for (int k = 0; k < 256; k++) { int length = _digits[k]; _digits[k] = start; start += length; }
                for (int k = 0; k < n; k++) { int i = _order[k]; _scratch[_digits[(int)(((uint)dots[i].Priority ^ 0x80000000u) >> shift) & 255]++] = i; }
                var swap = _order; _order = _scratch; _scratch = swap;
            }
            int cursor = 0;
            for (int k = 0; k < n; k++) if (dots[_order[k]].Selected) _scratch[cursor++] = _order[k];
            for (int k = 0; k < n; k++) if (!dots[_order[k]].Selected) _scratch[cursor++] = _order[k];
            { var swap = _order; _order = _scratch; _scratch = swap; }

            _groups.Reset(maxWidth * Group, maxWidth * Group, n); _fits.Reset(maxWidth, maxHeight, n);
            double floor = 1.5 * dpi;
            _all.Reset((10 * size + 1) * dpi, (10 * size + 1) * dpi, n);
            for (int k = 0; k < n; k++)
            {
                int i = _order[k]; var d = dots[i]; int home = -1, rank = int.MaxValue;
                // The approved mock uses combined half-width in both grouping axes.
                if (!d.Selected)
                {
                    var cell = _groups.Cell(d.X, d.Y, d.Text);
                    // Match the mock's cell traversal and insertion order. Linked
                    // buckets run backwards, so choose their earliest eligible rep.
                    for (int x = -1; x <= 1 && home < 0; x++) for (int y = -1; y <= 1 && home < 0; y++)
                        for (int r = _groups.Head(cell.Offset(x, y)); r >= 0; r = _groups.Next[r])
                        {
                            CandidateVisits++; var other = dots[r];
                            if ((home < 0 || other.Priority < rank) && Math.Abs(other.X - d.X) < Group * (other.Width + d.Width) / 2 &&
                                Math.Abs(other.Y - d.Y) < Group * (other.Width + d.Width) / 2)
                            { home = r; rank = other.Priority; }
                        }
                }
                if (home < 0) { home = i; if (!d.Selected) _groups.Add(i, d.X, d.Y, d.Text); }
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
                var a = dots[_anchor[i]]; _fits.Add(i, a.X, a.Y);
            }
            for (int k = 0; k < n; k++)
            {
                int i = _order[k], r = Results[i].Representative; var d = dots[i];
                Results[i].LabelAnchor = _anchor[r]; Results[i].GroupCount = _count[r];
                if (_count[r] > 1) Results[i].GroupSwatchRadius = Radius(i, dots, 3 * size * dpi, floor, 0);
                if (r != i) { Results[i].SwatchRadius = Results[i].GroupSwatchRadius; continue; }
                var a = dots[_anchor[r]];
                double fit = 1, threshold = MinimumTextScale / (d.Selected ? 1.4 : 1);
                var cell = _fits.Cell(a.X, a.Y);
                bool swatch = false;
                for (int x = 0; x < 3 && !swatch; x++) for (int y = 0; y < 3 && !swatch; y++)
                    for (int j = _fits.Head(cell.Offset(Neighbour(x), Neighbour(y))); j >= 0; j = _fits.Next[j])
                    {
                        if (i == j) continue;
                        CandidateVisits++; var b = dots[_anchor[j]];
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

        private double Radius(int i, IReadOnlyList<PanelDotInput> dots, double cap, double floor, double gap)
        {
            var d = dots[i]; double nearest = 2 * (cap + gap), squared = nearest * nearest;
            double stop = 2 * (floor + gap); stop *= stop;
            var cell = _all.Cell(d.X, d.Y);
            for (int x = 0; x < 3; x++) for (int y = 0; y < 3; y++)
                for (int j = _all.Head(cell.Offset(Neighbour(x), Neighbour(y))); j >= 0; j = _all.Next[j])
                {
                    if (i == j) continue;
                    CandidateVisits++; double dx = dots[j].X - d.X, dy = dots[j].Y - d.Y;
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
            Results = new PanelDotOutput[capacity]; _order = new int[capacity]; _scratch = new int[capacity];
            _count = new int[capacity]; _anchor = new int[capacity];
            _sumX = new double[capacity]; _sumY = new double[capacity]; _best = new double[capacity];
        }

        private readonly struct CellKey : IEquatable<CellKey>
        {
            private readonly int _x, _y;
            private readonly string? _text;
            internal CellKey(int x, int y, string? text) { _x = x; _y = y; _text = text; }
            internal CellKey Offset(int x, int y) => new CellKey(_x + x, _y + y, _text);
            public bool Equals(CellKey other) => _x == other._x && _y == other._y && string.Equals(_text, other._text, StringComparison.Ordinal);
            public override bool Equals(object? obj) => obj is CellKey other && Equals(other);
            public override int GetHashCode() => unchecked((_x * 100003 ^ _y) * 397 ^ (_text?.GetHashCode() ?? 0));
        }

        private sealed class Grid
        {
            private readonly Dictionary<CellKey, int> _heads = new Dictionary<CellKey, int>();
            private double _width, _height;
            internal int[] Next = Array.Empty<int>();
            internal void Reset(double width, double height, int count) { _heads.Clear(); _width = width; _height = height; if (Next.Length < count) Next = new int[Math.Max(count, Next.Length * 2)]; }
            internal CellKey Cell(double x, double y, string? text = null) => new CellKey((int)Math.Floor(x / _width), (int)Math.Floor(y / _height), text);
            internal int Head(CellKey key) => _heads.TryGetValue(key, out int head) ? head : -1;
            internal void Add(int i, double x, double y, string? text = null) { var key = Cell(x, y, text); Next[i] = Head(key); _heads[key] = i; }
        }
    }
}
