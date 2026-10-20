using System;
using System.Collections.Generic;

namespace BetterGhPanel.Core
{
    /// <summary>An address on the original input tree, before any Panel+ tree tools.</summary>
    public readonly struct PanelSourceAddress
    {
        public PanelSourceAddress(string path, int itemIndex)
        {
            if (itemIndex < 0) throw new ArgumentOutOfRangeException(nameof(itemIndex));
            Path = path ?? throw new ArgumentNullException(nameof(path));
            ItemIndex = itemIndex;
        }

        public string Path { get; }
        public int ItemIndex { get; }
    }

    /// <summary>Immutable provenance for one retained set item, including its first occurrence.</summary>
    public sealed class PanelSetOccurrences
    {
        public PanelSetOccurrences(IReadOnlyList<PanelSourceAddress> locations)
        {
            if (locations == null) throw new ArgumentNullException(nameof(locations));
            if (locations.Count == 0) throw new ArgumentException("An item must have an input occurrence.", nameof(locations));
            var copy = new PanelSourceAddress[locations.Count];
            HashSet<string>? branches = null;
            string firstPath = locations[0].Path;
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = locations[i];
                if (!string.Equals(firstPath, copy[i].Path, StringComparison.Ordinal))
                {
                    if (branches == null) branches = new HashSet<string>(StringComparer.Ordinal) { firstPath };
                    branches.Add(copy[i].Path);
                }
            }
            Locations = Array.AsReadOnly(copy);
            BranchCount = branches?.Count ?? 1;
        }

        public IReadOnlyList<PanelSourceAddress> Locations { get; }
        public int Count => Locations.Count;
        public int DuplicatesRemoved => Count - 1;
        public int BranchCount { get; }
    }
}
