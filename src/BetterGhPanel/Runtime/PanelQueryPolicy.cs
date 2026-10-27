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

        private static bool SameFilterSettings(PanelQueryOptions left, PanelQueryOptions right) =>
            left.Search == right.Search && left.SearchMode == right.SearchMode && left.TypeName == right.TypeName &&
            left.BranchPath == right.BranchPath && left.BranchPrefix == right.BranchPrefix &&
            left.OnlyInvalid == right.OnlyInvalid && left.DisplayFilterValue == right.DisplayFilterValue &&
            left.CreateSet == right.CreateSet;
    }
}
