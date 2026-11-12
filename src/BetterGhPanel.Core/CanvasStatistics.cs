using System;

namespace BetterGhPanel.Core
{
    // Presentation reads completed aggregates only. Opening or painting Stats never scans rows.
    internal static class CanvasStatistics
    {
        internal static string Summary(PanelQueryResult result) =>
            Count(result.Statistics.ItemCount, Total(result)) + (result.DisplaySet?.IsBranchSet == true ? " values · " : " items · ") +
            Count(result.Statistics.BranchCount, result.Snapshot.Branches.Count) + (result.Snapshot.Branches.Count == 1 ? " branch" : " branches");

        internal static string BranchCount(PanelQueryResult result, string path)
        {
            result.Statistics.BranchItemCounts.TryGetValue(path, out var matched);
            result.Snapshot.BranchItemCounts.TryGetValue(path, out var total);
            return Count(matched, total) + (total == 1 ? " item" : " items");
        }

        internal static string SourceStatuses(PanelSourceStatusCounts counts) =>
            FormattableString.Invariant($"{counts.VerifiedValid:N0} valid · {counts.VerifiedInvalid:N0} invalid · {counts.Unverified:N0} unverified");

        internal static PanelSourceStatusCounts RowSourceStatuses(PanelQueryResult result, int position)
        {
            var occurrences = result.SetOccurrencesAt(position);
            if (occurrences != null) return occurrences.SourceStatuses;
            var validity = result.Snapshot.Rows[position].Validity;
            return new PanelSourceStatusCounts(validity == PanelValidity.VerifiedValid ? 1 : 0,
                validity == PanelValidity.VerifiedInvalid ? 1 : 0, validity == PanelValidity.Unverified ? 1 : 0);
        }

        private static string Count(int matched, int total) => matched == total ? FormattableString.Invariant($"{total:N0}") : FormattableString.Invariant($"{matched:N0}/{total:N0}");
        private static int Total(PanelQueryResult result) => result.DisplaySet?.IsBranchSet == true ? result.Snapshot.Branches.Count : result.Snapshot.Rows.Count;
    }
}
