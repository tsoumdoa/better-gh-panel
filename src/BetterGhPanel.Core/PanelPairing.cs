using System;
using System.Collections.Generic;

namespace BetterGhPanel.Core
{
    /// <summary>Original branch identity, independent of transformed paths and item positions.</summary>
    public sealed class PanelBranchPair
    {
        internal PanelBranchPair(PanelBranch data, PanelBranch paired, int pairedOffset)
        { Data = data; Paired = paired; PairedOffset = pairedOffset; }
        public PanelBranch Data { get; }
        public PanelBranch Paired { get; }
        public int PairedOffset { get; }
        public bool ByOrder => Data.Path != Paired.Path;
        public bool Unequal => Data.ItemCount != Paired.ItemCount;
        public int? ItemPosition(int dataIndex) => dataIndex < Paired.ItemCount ? PairedOffset + dataIndex : (int?)null;
    }

    public static class PanelPairing
    {
        public static string? Diagnose(IReadOnlyList<PanelBranch> data, IReadOnlyList<PanelBranch> paired) =>
            data.Count == paired.Count ? null : $"F has {paired.Count} {(paired.Count == 1 ? "branch" : "branches")}, D has {data.Count}.";

        public static IReadOnlyList<PanelBranchPair> Match(IReadOnlyList<PanelBranch> data, IReadOnlyList<PanelBranch> paired)
        {
            if (Diagnose(data, paired) is string error) throw new ArgumentException(error, nameof(paired));
            var indices = new Dictionary<string, int>(StringComparer.Ordinal);
            var offsets = new int[paired.Count];
            int offset = 0;
            for (int i = 0; i < paired.Count; i++)
            { indices.Add(paired[i].Path, i); offsets[i] = offset; offset += paired[i].ItemCount; }
            var matches = new int[data.Count];
            var reserved = new bool[paired.Count];
            // Reserve every exact match before assigning any order fallback.
            for (int i = 0; i < data.Count; i++)
            {
                matches[i] = indices.TryGetValue(data[i].Path, out int index) ? index : -1;
                if (matches[i] >= 0) reserved[matches[i]] = true;
            }
            var result = new PanelBranchPair[data.Count];
            int next = 0;
            for (int i = 0; i < data.Count; i++)
            {
                int index = matches[i];
                if (index < 0)
                { while (reserved[next]) next++; index = next++; }
                result[i] = new PanelBranchPair(data[i], paired[index], offsets[index]);
            }
            return Array.AsReadOnly(result);
        }
    }
}
