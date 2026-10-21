using System;

namespace BetterGhPanel.Core
{
    // Cheap restrictions shared by the final query and deferred measurement selection.
    // Keep them together so a new restriction cannot measure rows the query excludes.
    internal sealed class PanelQueryScope
    {
        private readonly PanelSnapshot _snapshot;
        private readonly PanelQueryOptions _query;
        private readonly PanelSearchFilter _search;
        private readonly string? _selectedBranch, _branchPrefix;

        internal PanelQueryScope(PanelSnapshot snapshot, PanelQueryOptions query, PanelSearchFilter search)
        {
            _snapshot = snapshot;
            _query = query;
            _search = search;
            _selectedBranch = PanelQueryEngine.NormalizeBranchPath(query.BranchPath);
            _branchPrefix = query.BranchPrefix && _selectedBranch != null
                ? _selectedBranch.Substring(0, _selectedBranch.Length - 1) + ";" : null;
        }

        internal bool HasItemFilter => _search.HasFilter && !_search.IsBranchFilter || _query.TypeName != null || _query.OnlyInvalid;

        internal bool MatchesBranch(PanelBranch branch) => MatchesPath(branch.Path) && _search.MatchesBranchCount(branch.ItemCount);

        internal bool MatchesRow(PanelRow row)
        {
            if (!MatchesPath(row.Path)) return false;
            // List length uses the transformed branch count, before any item restrictions.
            if (_search.IsBranchFilter && _search.HasFilter &&
                (!_snapshot.BranchItemCounts.TryGetValue(row.Path, out var count) || !_search.MatchesBranchCount(count))) return false;
            return (_query.TypeName == null || string.Equals(row.TypeName, _query.TypeName, StringComparison.OrdinalIgnoreCase)) &&
                (!_query.OnlyInvalid || !row.IsValid);
        }

        private bool MatchesPath(string path) => _selectedBranch == null ||
            string.Equals(path, _selectedBranch, StringComparison.Ordinal) ||
            // Prefix selection means descendants of {0;1}, not siblings like {0;10}.
            _branchPrefix != null && path.StartsWith(_branchPrefix, StringComparison.Ordinal);
    }
}
