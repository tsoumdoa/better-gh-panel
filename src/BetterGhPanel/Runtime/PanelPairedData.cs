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
        internal int DifferingCount { get; }
        internal bool ByOrder { get; }
        private readonly Dictionary<string, PanelBranchPair> _byData;
        private readonly Dictionary<string, int> _dataOrder;
        private readonly IReadOnlyList<PanelSourceAddress> _sources;
        private readonly bool _reverse;
        private readonly Dictionary<string, List<PanelBranchPair>> _byDestination = new Dictionary<string, List<PanelBranchPair>>(StringComparer.Ordinal);
        private readonly Dictionary<string, IList<IGH_Goo>> _items;
        internal PanelPairedData(GH_Structure<IGH_Goo> data, GH_Structure<IGH_Goo> paired,
            IReadOnlyList<PanelSourceAddress> sources, int trim, bool simplify, bool flatten, bool reverse)
        {
            Pairs = PanelPairing.Match(Shape(data), Shape(paired));
            DifferingCount = Pairs.Count(pair => pair.Unequal);
            ByOrder = Pairs.Any(pair => pair.ByOrder);
            _byData = Pairs.ToDictionary(pair => pair.Data.Path, StringComparer.Ordinal);
            _dataOrder = Pairs.Select((pair, index) => (pair, index)).ToDictionary(value => value.pair.Data.Path, value => value.index, StringComparer.Ordinal);
            _sources = sources; _reverse = reverse;
            _items = new Dictionary<string, IList<IGH_Goo>>(StringComparer.Ordinal);
            for (int b = 0; b < paired.PathCount; b++) _items.Add(paired.Paths[b].ToString(), paired.Branches[b]);
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
        }
        internal static PanelBranch[] Shape(GH_Structure<IGH_Goo> tree) =>
            Enumerable.Range(0, tree.PathCount).Select(b => new PanelBranch(tree.Paths[b].ToString(), tree.Branches[b].Count)).ToArray();
        internal PanelBranchPair BranchAt(int position) => _byData[_sources[position].Path];
        internal int? ItemAt(int position) => BranchAt(position).ItemPosition(_sources[position].ItemIndex);
        internal IEnumerable<PanelBranchPair> BranchesAt(string path) => _byDestination.TryGetValue(path, out var pairs) ? pairs : Enumerable.Empty<PanelBranchPair>();
        internal IEnumerable<int> BranchItems(string path)
        {
            var pairs = BranchesAt(path).ToArray();
            foreach (var pair in _reverse ? Enumerable.Reverse(pairs) : pairs)
            {
                int count = Math.Min(pair.Data.ItemCount, pair.Paired.ItemCount);
                for (int i = 0; i < count; i++) yield return pair.PairedOffset + (_reverse ? count - i - 1 : i);
            }
            foreach (var pair in pairs)
                for (int i = Math.Min(pair.Data.ItemCount, pair.Paired.ItemCount); i < pair.Paired.ItemCount; i++)
                    yield return pair.PairedOffset + i;
        }

        internal GH_Structure<IGH_Goo> Output(PanelQueryResult query, IReadOnlyList<int> positions,
            IReadOnlyList<string> branchPaths, bool flatten, Action<long>? countVisits = null)
        {
            var result = new GH_Structure<IGH_Goo>();
            long visits = 0;
            var set = query.DisplaySet;
            if (set == null) AddGroup(positions, branchPaths, path => Parse(path));
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
                AddGroup(set.Members[representative], Array.Empty<string>(), _ => destination);
            }
            countVisits?.Invoke(visits + result.DataCount);
            return result;

            void AddGroup(IReadOnlyList<int> members, IEnumerable<string> paths, Func<string, GH_Path> destination)
            {
                var emitted = new HashSet<string>(StringComparer.Ordinal);
                var extras = new Dictionary<string, (PanelBranchPair Pair, string Path)>(StringComparer.Ordinal);
                foreach (int position in members.OrderBy(value => value))
                {
                    visits++;
                    var pair = BranchAt(position);
                    string path = query.Snapshot.Rows[position].Path;
                    if (!pair.Unequal)
                        result.EnsurePath(destination(path)).Add(_items[pair.Paired.Path][_sources[position].ItemIndex]);
                    else if (emitted.Add(pair.Data.Path))
                    {
                        // Reverse changes the paired prefix in exactly D's order.
                        // F-only tails stay after the D rows, as in the split pane.
                        int pairedCount = Math.Min(pair.Data.ItemCount, pair.Paired.ItemCount);
                        var branch = result.EnsurePath(destination(path));
                        for (int i = 0; i < pairedCount; i++)
                            branch.Add(_items[pair.Paired.Path][_reverse ? pairedCount - i - 1 : i]);
                        extras.Add(pair.Data.Path, (pair, path));
                    }
                }
                foreach (string path in paths)
                {
                    visits++;
                    result.EnsurePath(destination(path));
                    foreach (var pair in BranchesAt(path))
                        if (pair.Data.ItemCount == 0 && emitted.Add(pair.Data.Path)) extras.Add(pair.Data.Path, (pair, path));
                }
                // Only scoped original branches enter this pass. Their F extras use
                // original branch order, also after merged/flattened D rows.
                foreach (var extra in extras.Values.OrderBy(value => _dataOrder[value.Pair.Data.Path]))
                {
                    var pair = extra.Pair;
                    var branch = result.EnsurePath(destination(extra.Path));
                    for (int i = Math.Min(pair.Data.ItemCount, pair.Paired.ItemCount); i < pair.Paired.ItemCount; i++)
                        branch.Add(_items[pair.Paired.Path][i]);
                }
            }
        }

        private static GH_Path Parse(string text)
        { var path = new GH_Path(); path.FromString(text); return path; }
    }
}
