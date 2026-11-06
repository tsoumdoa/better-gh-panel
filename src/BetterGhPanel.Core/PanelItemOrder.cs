using System;
using System.Globalization;

namespace BetterGhPanel.Core
{
    internal static class PanelItemOrder
    {
        internal static string Address(PanelQueryResult result, int position, bool setItems, bool flatten)
        {
            TryGetNumber(result, position, out int number);
            string index = number.ToString(CultureInfo.InvariantCulture);
            string path = result.Snapshot.Rows[position].Path;
            return setItems && result.DisplaySet != null && !result.DisplaySet.IsBranchSet
                ? flatten ? "{" + index + "}" : path.Substring(0, path.Length - 1) + ";" + index + "}"
                : path + "(" + index + ")";
        }

        internal static bool TryGetNumber(PanelQueryResult? result, int position, out int number, Action? rowVisited = null)
        {
            number = 0;
            if (result == null) return false;
            rowVisited?.Invoke();
            if (result.DisplaySet != null) return result.DisplaySet.MemberIndices.TryGetValue(position, out number);
            if (!result.TryGetViewIndex(position, out _)) return false;
            number = result.Snapshot.Rows[position].OriginalIndex;
            return true;
        }
    }
}
