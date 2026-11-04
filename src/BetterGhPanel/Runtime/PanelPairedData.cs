using System;
using System.Collections.Generic;
using System.Linq;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel.Runtime
{
    // One original identity map serves output, presentation, copying and row actions.
    internal sealed class PanelPairedData
    {
        internal IReadOnlyList<PanelBranchPair> Pairs { get; }
        internal string Caption { get; }
        internal bool HasUnequal { get; }
        internal readonly struct PairHeading
        {
            internal readonly string Count, DataCount, Paths;
            internal readonly bool Unequal, ByOrder;
            internal readonly int Total;
            internal PairHeading(int count, int dataCount, bool unequal, bool byOrder, string paths)
            { Count = count + (count == 1 ? " item" : " items"); DataCount = "D " + dataCount;
                Total = count; Unequal = unequal; ByOrder = byOrder; Paths = paths; }
        }
        // Built during filtered output, never by expanding members while painting.
        internal sealed class Summary
        {
            internal int Count;
            internal readonly List<int> Preview = new List<int>(4);
            internal void Add(int item) { Count++; if (Preview.Count < 4) Preview.Add(item); }
            internal void Add(Summary other)
            { Count += other.Count; Preview.AddRange(other.Preview.Take(4 - Preview.Count)); }
        }
        internal readonly Dictionary<int, Summary> SetSummaries = new Dictionary<int, Summary>();
        internal readonly Dictionary<string, Summary> BranchSummaries = new Dictionary<string, Summary>(StringComparer.Ordinal);
        internal PanelQueryResult? SummaryResult { get; private set; }
        internal readonly Dictionary<string, PairHeading> Headings =
            new Dictionary<string, PairHeading>(StringComparer.Ordinal);
        private readonly int[] _sourcePairs, _pairedPairs, _dataPositions;
        private readonly Dictionary<string, PairHeading> _headingsWithoutEmpty = new Dictionary<string, PairHeading>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _dataOrder;
        private readonly IReadOnlyList<PanelSourceAddress> _sources;
        private readonly bool _reverse;
        private readonly Dictionary<string, List<PanelBranchPair>> _byDestination = new Dictionary<string, List<PanelBranchPair>>(StringComparer.Ordinal);
        private readonly IGH_Goo[] _rawItems;
        internal PanelPairedData(GH_Structure<IGH_Goo> data, GH_Structure<IGH_Goo> paired,
            IReadOnlyList<PanelSourceAddress> sources, int trim, bool simplify, bool flatten, bool reverse)
        {
            Pairs = PanelPairing.Match(Shape(data), Shape(paired));
            int differingCount = Pairs.Count(pair => pair.Unequal);
            HasUnequal = differingCount > 0;
            int byOrder = Pairs.Count(pair => pair.ByOrder);
            Caption = string.Join(" · ", new[] { "Paired by branch", byOrder > 0 ? byOrder + " by order" : null,
                differingCount > 0 ? differingCount + " of " + Pairs.Count + " differ in length" : null }.Where(part => part != null));
            _dataOrder = Pairs.Select((pair, index) => (pair, index)).ToDictionary(value => value.pair.Data.Path, value => value.index, StringComparer.Ordinal);
            _sources = sources; _reverse = reverse;
            _sourcePairs = sources.Select(source => _dataOrder[source.Path]).ToArray();
            _rawItems = paired.Branches.SelectMany(branch => branch).ToArray();
            _pairedPairs = new int[_rawItems.Length];
            _dataPositions = Enumerable.Repeat(-1, _rawItems.Length).ToArray();
            for (int index = 0; index < Pairs.Count; index++)
                for (int i = 0; i < Pairs[index].Paired.ItemCount; i++) _pairedPairs[Pairs[index].PairedOffset + i] = index;
            for (int position = 0; position < sources.Count; position++)
                if (ItemAt(position) is int item) _dataPositions[item] = position;
            // Mark every branch, including empty ones, through precisely D's tools.
            var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
            if (trim == 0 && !simplify && !flatten)
                foreach (var pair in Pairs) destinations.Add(pair.Data.Path, pair.Data.Path);
            else
            {
                var markers = new GH_Structure<IGH_Goo>();
                for (int b = 0; b < data.PathCount; b++) markers.Append(new GH_Integer(b), new GH_Path(data.Paths[b]));
                var mapped = PanelTreeOperations.ApplyWithSources(markers, trim, simplify, flatten, reverse, out _);
                for (int b = 0; b < mapped.PathCount; b++)
                    foreach (GH_Integer marker in mapped.Branches[b]) destinations.Add(Pairs[marker.Value].Data.Path, mapped.Paths[b].ToString());
            }
            foreach (var pair in Pairs)
            {
                string path = destinations[pair.Data.Path];
                if (!_byDestination.TryGetValue(path, out var branches)) _byDestination.Add(path, branches = new List<PanelBranchPair>());
                branches.Add(pair);
            }
            foreach (var destination in _byDestination)
            {
                PairHeading BuildHeading(IEnumerable<PanelBranchPair> scope)
                {
                    var pairs = scope.ToArray();
                    string paths = string.Join(", ", pairs.Take(3).Select(pair => pair.Paired.Path.Length > 80 ? pair.Paired.Path.Substring(0, 77) + "…" : pair.Paired.Path));
                    return new PairHeading(pairs.Sum(pair => pair.Paired.ItemCount), pairs.Sum(pair => pair.Data.ItemCount),
                        pairs.Any(pair => pair.Unequal), pairs.Any(pair => pair.ByOrder), paths + (pairs.Length > 3 ? " …" : ""));
                }
                Headings.Add(destination.Key, BuildHeading(destination.Value));
                _headingsWithoutEmpty.Add(destination.Key, BuildHeading(destination.Value.Where(pair => pair.Data.ItemCount > 0)));
            }
        }
        internal static PanelBranch[] Shape(GH_Structure<IGH_Goo> tree) =>
            Enumerable.Range(0, tree.PathCount).Select(b => new PanelBranch(tree.Paths[b].ToString(), tree.Branches[b].Count)).ToArray();
        internal PairHeading Heading(string path, bool includeEmpty) => (includeEmpty ? Headings : _headingsWithoutEmpty)[path];
        internal string HeadingCount(string path, bool includeEmpty)
        {
            var heading = Heading(path, includeEmpty);
            int count = BranchSummaries.TryGetValue(path, out var summary) ? summary.Count : 0;
            return count < heading.Total ? $"{count:N0} of {heading.Total:N0} items" : count + (count == 1 ? " item" : " items");
        }
        internal static bool EmptyPartnersInScope(PanelQueryOptions options) => !options.CreateSet && !PanelQueryEngine.HasItemFilter(options);
        internal PanelBranchPair BranchAt(int position) => Pairs[_sourcePairs[position]];
        internal PanelBranchPair PairAtPaired(int item) => Pairs[_pairedPairs[item]];
        internal int DataPositionAt(int item) => _dataPositions[item];
        internal int? ItemAt(int position) => BranchAt(position).ItemPosition(_sources[position].ItemIndex);
        internal IEnumerable<PanelBranchPair> BranchesAt(string path) => _byDestination.TryGetValue(path, out var pairs) ? pairs : Enumerable.Empty<PanelBranchPair>();

        // The single ordered membership traversal used by wire output and branch summaries.
        internal IEnumerable<(int Item, string Path)> BranchItems(PanelSnapshot snapshot, IReadOnlyList<int> members,
            IEnumerable<string> paths, bool includeEmpty)
        {
            var emitted = new HashSet<int>();
            var extras = new Dictionary<int, string>();
            foreach (int position in members.OrderBy(value => value))
            {
                int index = _sourcePairs[position];
                var pair = Pairs[index];
                string path = snapshot.Rows[position].Path;
                if (!pair.Unequal)
                    yield return (pair.PairedOffset + _sources[position].ItemIndex, path);
                else if (emitted.Add(index))
                {
                    int count = Math.Min(pair.Data.ItemCount, pair.Paired.ItemCount);
                    for (int i = 0; i < count; i++)
                        yield return (pair.PairedOffset + (_reverse ? count - i - 1 : i), path);
                    extras.Add(index, path);
                }
            }
            if (includeEmpty)
                foreach (string path in paths)
                {
                    // Nonempty destinations may also contain original empty branches after merging.
                    foreach (var pair in BranchesAt(path).Where(pair => pair.Data.ItemCount == 0))
                        if (!extras.ContainsKey(_dataOrder[pair.Data.Path])) extras.Add(_dataOrder[pair.Data.Path], path);
                }
            foreach (var extra in extras.OrderBy(value => value.Key))
            {
                var pair = Pairs[extra.Key];
                for (int i = Math.Min(pair.Data.ItemCount, pair.Paired.ItemCount); i < pair.Paired.ItemCount; i++)
                    yield return (pair.PairedOffset + i, extra.Value);
            }
        }

        internal GH_Structure<IGH_Goo> Output(PanelQueryResult query, IReadOnlyList<int> positions,
            IReadOnlyList<string> branchPaths, bool flatten, bool includeEmpty, Action<long>? countVisits = null, bool selected = false)
        {
            var result = new GH_Structure<IGH_Goo>();
            long visits = 0;
            var set = query.DisplaySet;
            if (!selected) { SetSummaries.Clear(); BranchSummaries.Clear(); }
            if (set == null) AddGroup(positions, branchPaths, path => Parse(path));
            else if (set.IsBranchSet)
                foreach (string representative in branchPaths)
                {
                    int k = set.BranchIndices[representative];
                    var summary = AddGroup(set.BranchMembers[representative], set.BranchOccurrences[representative],
                        path => new GH_Path(new[] { k }.Concat(Parse(path).Indices).ToArray()));
                    if (!selected) BranchSummaries[representative] = summary;
                }
            else foreach (int representative in positions.OrderBy(value => value))
            {
                int k = set.MemberIndices[representative];
                var destination = flatten ? new GH_Path(k) : Parse(query.Snapshot.Rows[representative].Path).AppendElement(k);
                var summary = AddGroup(set.Members[representative], Array.Empty<string>(), _ => destination);
                if (!selected)
                {
                    SetSummaries[representative] = summary;
                    string path = query.Snapshot.Rows[representative].Path;
                    if (!BranchSummaries.TryGetValue(path, out var branchSummary)) BranchSummaries.Add(path, branchSummary = new Summary());
                    branchSummary.Add(summary);
                }
            }
            countVisits?.Invoke(visits + result.DataCount);
            if (!selected) SummaryResult = query;
            return result;

            Summary AddGroup(IReadOnlyList<int> members, IEnumerable<string> paths, Func<string, GH_Path> destination)
            {
                visits += members.Count;
                var summaries = new Dictionary<string, Summary>(StringComparer.Ordinal);
                var retainedPaths = paths.ToArray();
                foreach (string path in retainedPaths)
                {
                    visits++;
                    result.EnsurePath(destination(path));
                }
                string? previousPath = null;
                IList<IGH_Goo>? branch = null;
                // A selected nonempty destination does not select unrelated empty
                // original branches that structural tools merged into it.
                var emptyPaths = selected ? retainedPaths.Where(path => query.Snapshot.BranchItemCounts[path] == 0) : retainedPaths;
                foreach (var item in BranchItems(query.Snapshot, members, emptyPaths, includeEmpty))
                {
                    visits++;
                    if (item.Path != previousPath)
                    { branch = result.EnsurePath(destination(item.Path)); previousPath = item.Path; }
                    branch!.Add(_rawItems[item.Item]);
                    if (!selected)
                    {
                        // Item sets have one destination; branch sets retain tree path order.
                        string key = set?.IsBranchSet == false ? "" : item.Path;
                        if (!summaries.TryGetValue(key, out var summary)) summaries.Add(key, summary = new Summary());
                        summary.Add(item.Item);
                    }
                }
                var combined = new Summary();
                if (set?.IsBranchSet == true)
                    foreach (string path in retainedPaths)
                    { if (summaries.TryGetValue(path, out var summary)) combined.Add(summary); }
                else foreach (var summary in summaries) combined.Add(summary.Value);
                if (!selected && set == null)
                    foreach (var summary in summaries) BranchSummaries[summary.Key] = summary.Value;
                return combined;
            }
        }

        private static GH_Path Parse(string text)
        { var path = new GH_Path(); path.FromString(text); return path; }
    }
}
