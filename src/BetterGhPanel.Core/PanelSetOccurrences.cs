using System;
using System.Collections.Generic;

namespace BetterGhPanel.Core
{
    /// <summary>Validity counts for source occurrences, not the emitted set value.</summary>
    public readonly struct PanelSourceStatusCounts
    {
        public PanelSourceStatusCounts(int verifiedValid, int verifiedInvalid, int unverified)
        { VerifiedValid = verifiedValid; VerifiedInvalid = verifiedInvalid; Unverified = unverified; }
        public int VerifiedValid { get; }
        public int VerifiedInvalid { get; }
        public int Unverified { get; }
        public int Total => VerifiedValid + VerifiedInvalid + Unverified;

        internal PanelSourceStatusCounts Add(PanelValidity validity) => validity switch
        {
            PanelValidity.VerifiedInvalid => new PanelSourceStatusCounts(VerifiedValid, VerifiedInvalid + 1, Unverified),
            PanelValidity.Unverified => new PanelSourceStatusCounts(VerifiedValid, VerifiedInvalid, Unverified + 1),
            _ => new PanelSourceStatusCounts(VerifiedValid + 1, VerifiedInvalid, Unverified)
        };
    }

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
        public PanelSetOccurrences(IReadOnlyList<PanelSourceAddress> locations) : this(locations, default) { }

        public PanelSetOccurrences(IReadOnlyList<PanelSourceAddress> locations, PanelSourceStatusCounts sourceStatuses)
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
            SourceStatuses = sourceStatuses;
        }

        public IReadOnlyList<PanelSourceAddress> Locations { get; }
        public int Count => Locations.Count;
        public int DuplicatesRemoved => Count - 1;
        public int BranchCount { get; }
        public PanelSourceStatusCounts SourceStatuses { get; }
    }
}
