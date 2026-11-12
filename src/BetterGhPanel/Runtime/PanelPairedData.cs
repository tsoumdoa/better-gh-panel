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
        internal bool HasUnequal { get; }
        internal readonly struct PairHeading
        {
            internal readonly string DataCount;
            internal readonly bool Unequal;
            internal readonly int Total, DataTotal;
            internal PairHeading(int count, int dataCount, bool unequal)
            { DataCount = "D " + dataCount; DataTotal = dataCount; Total = count; Unequal = unequal; }
        }
        internal readonly Dictionary<string, PairHeading> Headings =
            new Dictionary<string, PairHeading>(StringComparer.Ordinal);
        private readonly int[] _sourcePairs, _pairedPairs, _dataPositions;
        private readonly Dictionary<string, PairHeading> _headingsWithoutEmpty = new Dictionary<string, PairHeading>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _dataOrder;
        private readonly IReadOnlyList<PanelSourceAddress> _sources;
        private readonly Dictionary<string, List<PanelBranchPair>> _byDestination = new Dictionary<string, List<PanelBranchPair>>(StringComparer.Ordinal);
        private readonly IGH_Goo[] _rawItems;
        internal PanelPairedData(GH_Structure<IGH_Goo> data, GH_Structure<IGH_Goo> paired,
            IReadOnlyList<PanelSourceAddress> sources, int trim, bool simplify, bool flatten, bool reverse)
        {
            Pairs = PanelPairing.Match(Shape(data), Shape(paired));
            HasUnequal = Pairs.Any(pair => pair.Unequal);
            _dataOrder = Pairs.Select((pair, index) => (pair, index)).ToDictionary(value => value.pair.Data.Path, value => value.index, StringComparer.Ordinal);
            _sources = sources;
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
                    return new PairHeading(pairs.Sum(pair => pair.Paired.ItemCount), pairs.Sum(pair => pair.Data.ItemCount), pairs.Any(pair => pair.Unequal));
                }
                Headings.Add(destination.Key, BuildHeading(destination.Value));
                _headingsWithoutEmpty.Add(destination.Key, BuildHeading(destination.Value.Where(pair => pair.Data.ItemCount > 0)));
            }
        }
        internal static PanelBranch[] Shape(GH_Structure<IGH_Goo> tree) =>
            Enumerable.Range(0, tree.PathCount).Select(b => new PanelBranch(tree.Paths[b].ToString(), tree.Branches[b].Count)).ToArray();
        internal PairHeading Heading(string path, bool includeEmpty) => (includeEmpty ? Headings : _headingsWithoutEmpty)[path];
        internal static bool EmptyPartnersInScope(PanelQueryOptions options) => !options.BranchStats && !options.CreateSet && !PanelQueryEngine.HasItemFilter(options);
        internal PanelBranchPair BranchAt(int position) => Pairs[_sourcePairs[position]];
        internal PanelBranchPair PairAtPaired(int item) => Pairs[_pairedPairs[item]];
        internal int DataPositionAt(int item) => _dataPositions[item];
        internal int? ItemAt(int position) => BranchAt(position).ItemPosition(_sources[position].ItemIndex);
        internal IEnumerable<PanelBranchPair> BranchesAt(string path) => _byDestination.TryGetValue(path, out var pairs) ? pairs : Enumerable.Empty<PanelBranchPair>();

        // The single ordered membership traversal used by wire output and branch summaries.
        internal IEnumerable<(int Item, string Path)> BranchItems(PanelSnapshot snapshot, IReadOnlyList<int> members,
            IEnumerable<string> paths, bool includeFOnly)
        {
            foreach (var step in BranchItemSteps(snapshot, members, paths, includeFOnly, false))
                if (step.HasValue) yield return step.Value;
        }
        internal int OriginalBranchOrder(PanelBranchPair pair) => _dataOrder[pair.Data.Path];
        // Emits the partner of each member, so F matches B item for item. F-only items
        // have no B entry and follow only when nothing narrows D's rows (includeFOnly).
        // Query callers already hold members in snapshot order. Null steps preserve
        // cancellation/budget checkpoints even for missing cells.
        internal IEnumerable<(int Item, string Path)?> BranchItemSteps(PanelSnapshot snapshot, IReadOnlyList<int> members,
            IEnumerable<string> paths, bool includeFOnly, bool snapshotOrder)
        {
            var tails = new SortedDictionary<int, string>();
            foreach (int position in snapshotOrder ? (IEnumerable<int>)members : members.OrderBy(value => value))
            {
                yield return null;
                string path = snapshot.Rows[position].Path;
                if (ItemAt(position) is int item) yield return (item, path);
                if (includeFOnly && BranchAt(position).Unequal) tails[_sourcePairs[position]] = path;
            }
            if (includeFOnly)
                foreach (string path in paths)
                {
                    yield return null;
                    foreach (var pair in BranchesAt(path))
                    {
                        yield return null;
                        if (pair.Data.ItemCount == 0) tails[_dataOrder[pair.Data.Path]] = path;
                    }
                }
            foreach (var tail in tails)
            {
                yield return null;
                var pair = Pairs[tail.Key];
                for (int i = pair.Data.ItemCount; i < pair.Paired.ItemCount; i++)
                    yield return (pair.PairedOffset + i, tail.Value);
            }
        }

        internal GH_Structure<IGH_Goo> Output(PanelQueryResult query, IReadOnlyList<int> positions,
            IReadOnlyList<string> branchPaths, bool flatten, bool includeEmpty, Action<long>? countVisits = null, bool selected = false)
        {
            var result = new GH_Structure<IGH_Goo>();
            long visits = 0;
            var set = query.DisplaySet;
            // A selected row or set group with no F partner keeps its path, as S and D do.
            if (set == null) AddGroup(positions, selected ? branchPaths.Concat(positions.Select(position => query.Snapshot.Rows[position].Path)).Distinct() : branchPaths, path => Parse(path));
            else if (set.IsBranchSet)
                foreach (string representative in branchPaths)
                {
                    int k = set.BranchIndices[representative];
                    AddGroup(set.BranchMembers[representative], set.BranchOccurrences[representative],
                        path => new GH_Path(new[] { k }.Concat(Parse(path).Indices).ToArray()));
                }
            else foreach (int representative in positions.OrderBy(value => value))
            {
                int k = set.MemberIndices[representative];
                var destination = flatten ? new GH_Path(k) : Parse(query.Snapshot.Rows[representative].Path).AppendElement(k);
                AddGroup(set.Members[representative], new[] { query.Snapshot.Rows[representative].Path }, _ => destination);
            }
            countVisits?.Invoke(visits + result.DataCount);
            return result;

            void AddGroup(IReadOnlyList<int> members, IEnumerable<string> paths, Func<string, GH_Path> destination)
            {
                visits += members.Count;
                var retainedPaths = paths.ToArray();
                foreach (string path in retainedPaths)
                {
                    visits++;
                    result.EnsurePath(destination(path));
                }
                string? previousPath = null;
                IList<IGH_Goo>? branch = null;
                foreach (var item in BranchItems(query.Snapshot, members, retainedPaths, includeEmpty && !selected))
                {
                    visits++;
                    if (item.Path != previousPath)
                    { branch = result.EnsurePath(destination(item.Path)); previousPath = item.Path; }
                    branch!.Add(_rawItems[item.Item]);
                }
            }
        }

        private static GH_Path Parse(string text)
        { var path = new GH_Path(); path.FromString(text); return path; }
    }
}
