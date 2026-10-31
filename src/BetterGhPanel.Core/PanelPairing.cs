using System;
using System.Collections.Generic;

namespace BetterGhPanel.Core
{
    /// <summary>Exact original-path pairing diagnostics, before any tree tools.</summary>
    public static class PanelPairing
    {
        public static string? Diagnose(IReadOnlyList<PanelBranch> data, IReadOnlyList<PanelBranch> paired)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var branch in paired) counts.Add(branch.Path, branch.ItemCount);
            foreach (var branch in data)
            {
                if (!counts.TryGetValue(branch.Path, out int count)) return "F is missing branch " + branch.Path;
                if (branch.ItemCount != count) return $"Branch {branch.Path}: D has {branch.ItemCount} items, F has {count}";
                counts.Remove(branch.Path);
            }
            if (counts.Count > 0)
                foreach (var branch in paired)
                    if (counts.ContainsKey(branch.Path)) return "F has extra branch " + branch.Path;
            return null;
        }
    }
}
