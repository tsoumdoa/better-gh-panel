using System.Collections.Generic;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // Only positioning changes during drawing. Text/font setters are confined to
    // creation so each native dot keeps its own renderer cache across redraws.
    internal sealed class PanelTextDotCache
    {
        private readonly int _capacity;
        private readonly Dictionary<(string Text, int Height), LinkedListNode<Entry>> _entries =
            new Dictionary<(string Text, int Height), LinkedListNode<Entry>>();
        private readonly LinkedList<Entry> _recent = new LinkedList<Entry>();
        internal PanelTextDotCache(int capacity) { _capacity = capacity; }
        internal int Count => _entries.Count;
        internal TextDot Get(string text, int height)
        {
            var key = (text, height);
            if (_entries.TryGetValue(key, out var node))
            {
                _recent.Remove(node); _recent.AddFirst(node); return node.Value.Dot;
            }
            var dot = new TextDot(text, Point3d.Origin) { FontHeight = height };
            _entries.Add(key, _recent.AddFirst(new Entry(key, dot)));
            if (_entries.Count > _capacity)
            {
                var last = _recent.Last!; _recent.RemoveLast();
                _entries.Remove(last.Value.Key); last.Value.Dot.Dispose();
            }
            return dot;
        }
        internal void Clear()
        {
            foreach (var entry in _recent) entry.Dot.Dispose();
            _entries.Clear(); _recent.Clear();
        }
        private readonly struct Entry
        {
            internal Entry((string Text, int Height) key, TextDot dot) { Key = key; Dot = dot; }
            internal readonly (string Text, int Height) Key;
            internal readonly TextDot Dot;
        }
    }
}
