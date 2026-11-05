namespace BetterGhPanel.Core
{
    /// <summary>Transformed input diagnostics collected before search restrictions in the budgeted query.</summary>
    public sealed class PanelDataWarnings
    {
        public int NullCount { get; internal set; }
        public int InvalidCount { get; internal set; }
        public int UnverifiedCount { get; internal set; }
        public int? MajorityLength { get; internal set; }
        public int OutlierBranches { get; internal set; }
        public bool Any => NullCount > 0 || InvalidCount > 0 || UnverifiedCount > 0 || OutlierBranches > 0;
        public bool IsOutlier(PanelSnapshot snapshot, string path) => MajorityLength.HasValue &&
            snapshot.BranchItemCounts.TryGetValue(path, out int count) && count != MajorityLength.Value;
    }
}
