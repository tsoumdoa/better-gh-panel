using System.Collections;
using System.Collections.Generic;

namespace BetterGhPanel.Core
{
    internal sealed class SequentialPositions : IReadOnlyList<int>
    {
        internal SequentialPositions(int count) { Count = count; }
        public int Count { get; }
        public int this[int index] => index;
        public IEnumerator<int> GetEnumerator() { for (int i = 0; i < Count; i++) yield return i; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
