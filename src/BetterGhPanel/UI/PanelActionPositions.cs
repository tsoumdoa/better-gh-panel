using System;
using System.Collections.Generic;
using BetterGhPanel.Core;

namespace BetterGhPanel.UI
{
    internal static class PanelActionPositions
    {
        internal static bool TryGet(PanelQueryResult result, int sourceCount, PanelRowSelection selection,
            IReadOnlyList<int> positions, out IReadOnlyList<int> ordered, Action? rowVisited = null)
        {
            ordered = Array.Empty<int>();
            if (positions.Count == 0) return false;
            if (sourceCount == result.Snapshot.Rows.Count && selection.IsCurrentOrderedPositions(result, positions))
            {
                ordered = positions;
                return true;
            }

            // External or stale lists still require full validation and deduplication,
            // but only of the supplied positions, not the whole query result.
            var indices = new List<int>(positions.Count);
            var seen = new HashSet<int>();
            foreach (int position in positions)
            {
                rowVisited?.Invoke();
                if (position < 0 || position >= sourceCount || !result.TryGetViewIndex(position, out int index)) return false;
                if (seen.Add(position)) indices.Add(index);
            }
            indices.Sort();
            var values = new int[indices.Count];
            for (int i = 0; i < indices.Count; i++)
            {
                rowVisited?.Invoke();
                values[i] = result.RowPositions[indices[i]];
            }
            ordered = Array.AsReadOnly(values);
            return true;
        }
    }
}
