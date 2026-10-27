using System;
using System.Collections.Generic;
using BetterGhPanel.Core;

namespace BetterGhPanel.UI
{
    internal static class PanelActionPositions
    {
        // TryGet also accepts arbitrary position lists. Their positions can all
        // remain in the result after the user's selection has changed, so an
        // action must separately bind them to the selection revision it began with.
        internal static bool IsCurrent(PanelQueryResult result, int sourceCount, PanelRowSelection selection,
            IReadOnlyList<int> positions, long selectionVersion)
        {
            if (selection.Version != selectionVersion ||
                !TryGet(result, sourceCount, selection, positions, out var ordered) || positions.Count != ordered.Count)
                return false;
            if (!ReferenceEquals(positions, ordered))
            {
                using var expected = positions.GetEnumerator();
                using var actual = ordered.GetEnumerator();
                while (expected.MoveNext())
                    if (!actual.MoveNext() || expected.Current != actual.Current) return false;
                if (actual.MoveNext()) return false;
            }
            return selection.Version == selectionVersion;
        }

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
