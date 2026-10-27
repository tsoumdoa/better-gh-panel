using System;
using BetterGhPanel.Core;

namespace BetterGhPanel.UI
{
    internal static class PanelItemOrder
    {
        internal static bool TryGetNumber(PanelQueryResult? result, int position, out int number, Action? rowVisited = null)
        {
            number = 0;
            if (result == null) return false;
            rowVisited?.Invoke();
            if (!result.TryGetViewIndex(position, out _)) return false;
            number = result.Snapshot.Rows[position].OriginalIndex;
            return true;
        }
    }
}
