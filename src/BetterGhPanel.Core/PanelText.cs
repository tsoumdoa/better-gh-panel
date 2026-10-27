using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace BetterGhPanel.Core
{
    // A complete semantic display value, either flat or a captured group summary.
    // Operations work on immutable parts and do not materialize long group names.
    internal readonly struct PanelText : IEquatable<PanelText>
    {
        private static readonly uint HashSeed = unchecked((uint)Guid.NewGuid().GetHashCode()) ^ 2166136261u;
        private readonly string? _text;
        private readonly PanelGroupSummary? _group;
        private readonly bool _invalid;

        internal PanelText(string text) { _text = text; _group = null; _invalid = false; }
        internal PanelText(PanelGroupSummary group, bool invalid) { _text = null; _group = group; _invalid = invalid; }
        internal PanelGroupSummary? Group => _group;
        internal bool Invalid => _invalid;
        internal PanelGroupKey? GroupKey => _group?.Key(_invalid);
        private int PartCount => _group == null ? 1 : 6;
        private string Part(int index) => _group == null ? index == 0 ? _text ?? "" : "" : _group.Part(index, _invalid);

        public override string ToString() => _group?.Format(_invalid) ?? _text ?? "";
        public override int GetHashCode() => _group?.Hash(_invalid) ?? HashParts(this);
        internal static int HashParts(PanelText value)
        {
            uint hash = HashSeed;
            unchecked
            {
                for (int i = 0; i < value.PartCount; i++)
                {
                    string part = value.Part(i);
                    for (int j = 0; j < part.Length; j++) hash = (hash ^ part[j]) * 16777619u;
                }
            }
            return (int)hash;
        }
        public override bool Equals(object? other) => other is PanelText text && Equals(text);
        public bool Equals(PanelText other) => Compare(other, StringComparison.Ordinal) == 0;

        internal int Compare(PanelText other, StringComparison comparison)
        {
            if (_group != null && ReferenceEquals(_group, other._group) && _invalid == other._invalid) return 0;
            if (_group == null && other._group == null) return string.Compare(_text ?? "", other._text ?? "", comparison);
            int a = 0, b = 0, ai = 0, bi = 0;
            while (true)
            {
                while (a < PartCount && ai == Part(a).Length) { a++; ai = 0; }
                while (b < other.PartCount && bi == other.Part(b).Length) { b++; bi = 0; }
                if (a == PartCount || b == other.PartCount)
                    return a == PartCount ? b == other.PartCount ? 0 : -1 : 1;
                string left = Part(a), right = other.Part(b);
                int length = Math.Min(left.Length - ai, right.Length - bi);
                if (ReferenceEquals(left, right) && ai == bi) { ai += length; bi += length; continue; }
                // OrdinalIgnoreCase compares a valid surrogate pair as a unit.
                // When the other value's part boundary bisects that pair, compare
                // two virtual characters together rather than truncating it.
                if (comparison == StringComparison.OrdinalIgnoreCase &&
                    (SplitsPair(left, ai, length) || SplitsPair(right, bi, length)))
                {
                    int prefix = length - 1;
                    int prefixOrder = string.Compare(left, ai, right, bi, prefix, comparison);
                    if (prefixOrder != 0) return prefixOrder;
                    ai += prefix; bi += prefix;
                    string leftPair = TakeCharacters(ref a, ref ai, 2);
                    string rightPair = other.TakeCharacters(ref b, ref bi, 2);
                    int pairOrder = string.Compare(leftPair, rightPair, comparison);
                    if (pairOrder != 0) return pairOrder;
                    continue;
                }
                int order = string.Compare(left, ai, right, bi, length, comparison);
                if (order != 0) return order;
                ai += length; bi += length;
            }
        }

        private static bool SplitsPair(string part, int offset, int length) =>
            offset + length < part.Length && char.IsHighSurrogate(part[offset + length - 1]) &&
            char.IsLowSurrogate(part[offset + length]);

        private string TakeCharacters(ref int partIndex, ref int offset, int count)
        {
            var chars = new char[count];
            int length = 0;
            while (length < count && partIndex < PartCount)
            {
                string part = Part(partIndex);
                if (offset == part.Length) { partIndex++; offset = 0; continue; }
                chars[length++] = part[offset++];
            }
            return new string(chars, 0, length);
        }

        internal bool Contains(string search)
        {
            if (search.Length == 0) return true;
            if (_group == null) return (_text ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
            // A very long needle could otherwise cause quadratic work at part
            // boundaries. Materialize once for that query and use the runtime's
            // ordinal search; repeated group results are cached by the filter.
            if (search.Length > 256) return ToString().IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
            for (int partIndex = 0; partIndex < PartCount; partIndex++)
            {
                string part = Part(partIndex);
                if (part.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                // Check possible starts crossing this part boundary. Compare only
                // existing segments, including matches spanning several boundaries.
                int first = Math.Max(0, part.Length - search.Length + 1);
                for (int start = first; start < part.Length; start++)
                {
                    int matched = 0, segment = partIndex, offset = start;
                    while (segment < PartCount && matched < search.Length)
                    {
                        string piece = Part(segment);
                        int length = Math.Min(piece.Length - offset, search.Length - matched);
                        if (length != 0 && string.Compare(piece, offset, search, matched, length, StringComparison.OrdinalIgnoreCase) != 0) break;
                        matched += length;
                        segment++; offset = 0;
                    }
                    if (matched == search.Length) return true;
                }
            }
            return false;
        }
    }

    // Identity of an immutable summary's captured name/count, usable even when
    // the capture's bounded group-object cache has filled up.
    internal readonly struct PanelGroupKey : IEquatable<PanelGroupKey>
    {
        private readonly string _name;
        private readonly int _count;
        private readonly bool _invalid;
        internal PanelGroupKey(string name, int count, bool invalid) { _name = name; _count = count; _invalid = invalid; }
        public bool Equals(PanelGroupKey other) => ReferenceEquals(_name, other._name) && _count == other._count && _invalid == other._invalid;
        public override bool Equals(object? other) => other is PanelGroupKey key && Equals(key);
        public override int GetHashCode() => unchecked((RuntimeHelpers.GetHashCode(_name) * 397 ^ _count) * 397 ^ (_invalid ? 1 : 0));
    }

    // Reuse content hashes for repeated summaries, without retaining formatted
    // strings or allowing a capture with many distinct names to grow this cache.
    internal sealed class PanelTextComparer : IEqualityComparer<PanelText>
    {
        private const int MaxCachedHashes = 16384;
        private readonly Dictionary<PanelGroupKey, int> _hashes = new Dictionary<PanelGroupKey, int>();
        private readonly Queue<PanelGroupKey> _order = new Queue<PanelGroupKey>();
        public bool Equals(PanelText x, PanelText y) => x.Equals(y);
        public int GetHashCode(PanelText value)
        {
            var key = value.GroupKey;
            if (!key.HasValue) return value.GetHashCode();
            if (_hashes.TryGetValue(key.Value, out int hash)) return hash;
            hash = value.GetHashCode();
            if (_hashes.Count == MaxCachedHashes) _hashes.Remove(_order.Dequeue());
            _hashes.Add(key.Value, hash);
            _order.Enqueue(key.Value);
            return hash;
        }
    }
}
