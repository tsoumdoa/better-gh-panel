using BetterGhPanel.Core;

namespace BetterGhPanel.Runtime
{
    // View changes and downstream membership deliberately use different comparisons.
    // Compare normalized options here; the session owns their normalization.
    internal static class PanelQueryPolicy
    {
        internal static bool SameQuery(PanelQueryOptions left, PanelQueryOptions right) =>
            SameFilterSettings(left, right) && left.SortMode == right.SortMode &&
            left.DecimalPlaces == right.DecimalPlaces;

        internal static bool SameOutputFilter(PanelQueryOptions left, PanelQueryOptions right) =>
            SameFilterSettings(left, right) &&
            (left.DecimalPlaces == right.DecimalPlaces || !right.CreateSet && !PanelQueryEngine.UsesDisplayedText(right));

        // While a query is pending or has failed, an unfiltered tree may pass through.
        internal static bool HasOutputFilter(PanelQueryOptions options) =>
            options.CreateSet ||
            (!options.DisplayFilterValue && (options.SearchMode == PanelSearchMode.Regex
                ? !string.IsNullOrEmpty(options.Search) : !string.IsNullOrWhiteSpace(options.Search))) ||
            options.TypeName != null || options.OnlyInvalid || !string.IsNullOrWhiteSpace(options.BranchPath);

        // These counts describe input before filtering, including rows that may no
        // longer be visible in the result or its projected set statistics.
        internal static string InputDiagnostic(PanelSnapshot snapshot, PanelQueryOptions options)
        {
            if (!HasOutputFilter(options) || snapshot.UnverifiedCount == 0 && snapshot.UnavailableTypeIdentityCount == 0)
                return string.Empty;
            string identity = snapshot.UnavailableTypeIdentityCount == 1 ? "type identity" : "type identities";
            return $"Input: {snapshot.UnverifiedCount:N0} unverified · {snapshot.UnavailableTypeIdentityCount:N0} {identity} unavailable. Active filters may omit some values.";
        }

        private static bool SameFilterSettings(PanelQueryOptions left, PanelQueryOptions right) =>
            left.Search == right.Search && left.SearchMode == right.SearchMode && left.TypeName == right.TypeName &&
            left.BranchPath == right.BranchPath && left.BranchPrefix == right.BranchPrefix &&
            left.OnlyInvalid == right.OnlyInvalid && left.DisplayFilterValue == right.DisplayFilterValue &&
            left.CreateSet == right.CreateSet;
    }
}
