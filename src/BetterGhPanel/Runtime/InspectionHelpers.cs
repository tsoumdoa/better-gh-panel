using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace BetterGhPanel.Runtime
{
    internal static class InspectionLimits
    {
        internal const int MaxDepth = 64;
        internal const int MaxNodesPerRoot = 4096;
    }

    internal sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new ReferenceComparer();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }

    internal sealed class Frame
    {
        internal Frame(object node, int depth) { Node = node; Depth = depth; }
        internal readonly object Node;
        internal readonly int Depth;
        internal bool Entered;
        internal int Next;
        internal object?[]? Children;
    }
}
