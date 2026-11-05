using System;
using System.Collections.Generic;
using System.Linq;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel.Runtime
{
    // Output membership is expressed in transformed row positions. Only the mask
    // returns to original input addresses; paired values follow the primary tree.
    internal static class PanelOutputOperations
    {
        internal static GH_Structure<IGH_Goo> Values(GH_Structure<IGH_Goo> source, PanelQueryResult query,
            IReadOnlyList<int> positions, IEnumerable<string> branchPaths, Action<long>? countVisits = null)
        {
            var result = new GH_Structure<IGH_Goo>();
            var lookup = new SourceLookup(source);
            foreach (string path in branchPaths) result.EnsurePath(lookup.Path(path));
            foreach (int position in positions.OrderBy(value => value))
            {
                var value = query.OutputValues![position];
                IGH_Goo item = value == null ? null! : value.Kind switch
                {
                    PanelPropertyKind.Number => new GH_Number(value.Number),
                    PanelPropertyKind.Boolean => new GH_Boolean(value.Number != 0),
                    PanelPropertyKind.DateTime => new GH_Time(value.Date),
                    _ => new GH_String(value.Text)
                };
                // Append to the list directly: a null is an item, not a missing row.
                result.EnsurePath(lookup.Path(query.Snapshot.Rows[position].Path)).Add(item);
            }
            countVisits?.Invoke((long)lookup.PathCount + positions.Count);
            return result;
        }

        internal static GH_Structure<IGH_Goo> SetItems(GH_Structure<IGH_Goo> source, PanelQueryResult query,
            IReadOnlyList<int> positions, IReadOnlyList<string> branchPaths, bool flatten, Action<long>? countVisits = null)
        {
            var result = new GH_Structure<IGH_Goo>();
            if (positions.Count == 0 && branchPaths.Count == 0) return result;
            var set = query.DisplaySet!;
            var lookup = new SourceLookup(source);
            long members = 0;
            if (set.IsBranchSet)
            {
                foreach (string representative in branchPaths)
                {
                    int k = set.BranchIndices[representative];
                    foreach (string path in set.BranchOccurrences[representative])
                        result.EnsurePath(new GH_Path(new[] { k }.Concat(lookup.Path(path).Indices).ToArray()));
                    foreach (int member in set.BranchMembers[representative])
                    {
                        var row = query.Snapshot.Rows[member];
                        var path = new GH_Path(new[] { k }.Concat(lookup.Path(row.Path).Indices).ToArray());
                        result.EnsurePath(path).Add(lookup.Item(row));
                        members++;
                    }
                }
            }
            else foreach (int representative in positions.OrderBy(value => value))
            {
                var row = query.Snapshot.Rows[representative];
                int k = set.MemberIndices[representative];
                var path = flatten ? new GH_Path(k) : lookup.Path(row.Path).AppendElement(k);
                var branch = result.EnsurePath(path);
                foreach (int member in set.Members[representative])
                {
                    var memberRow = query.Snapshot.Rows[member];
                    branch.Add(lookup.Item(memberRow));
                    members++;
                }
            }
            countVisits?.Invoke(lookup.PathCount + members);
            return result;
        }

        internal static GH_Structure<IGH_Goo> DisplaySet(GH_Structure<IGH_Goo> source, PanelQueryResult query,
            IReadOnlyList<int> positions, IEnumerable<string> branchPaths, Action<long>? countVisits = null)
        {
            var result = new GH_Structure<IGH_Goo>();
            var lookup = new SourceLookup(source);
            long values = 0;
            var set = query.DisplaySet ?? throw new ArgumentException("A completed displayed set is required.", nameof(query));
            foreach (string path in branchPaths)
            {
                var outputPath = lookup.Path(path);
                result.EnsurePath(outputPath);
                if (set.IsBranchSet)
                {
                    result.Append(new GH_Integer(query.Snapshot.BranchItemCounts[path]), outputPath);
                    values++;
                }
            }
            if (!set.IsBranchSet)
                foreach (int position in positions.OrderBy(value => value))
                {
                    var value = set.Values[position];
                    IGH_Goo item = value.Number.HasValue ? new GH_Number(value.Number.Value) : new GH_String(value.Text);
                    result.Append(item, lookup.Path(query.Snapshot.Rows[position].Path));
                    values++;
                }
            countVisits?.Invoke(lookup.PathCount + values);
            return result;
        }

        // Resolve only published paths. Captured trees are sorted, so PathIndex
        // uses a binary lookup without scanning or indexing unrelated branches.
        private sealed class SourceLookup
        {
            private readonly GH_Structure<IGH_Goo> _source;
            private readonly Dictionary<string, int> _indices = new Dictionary<string, int>(StringComparer.Ordinal);
            internal SourceLookup(GH_Structure<IGH_Goo> source) { _source = source; }
            internal int PathCount => _indices.Count;
            internal GH_Path Path(string text) => new GH_Path(_source.Paths[Index(text)]);
            internal IGH_Goo Item(PanelRow row) => _source.Branches[Index(row.Path)][row.OriginalIndex];
            private int Index(string text)
            {
                if (!_indices.TryGetValue(text, out int index))
                {
                    var path = new GH_Path();
                    if (!path.FromString(text)) throw new ArgumentException("Invalid captured path.", nameof(text));
                    int upper = 0;
                    _source.PathIndex(path, ref index, ref upper);
                    if (index != upper || index < 0 || index >= _source.PathCount || _source.Paths[index] != path)
                        throw new ArgumentException("Captured path is missing from the output source.", nameof(text));
                    _indices.Add(text, index);
                }
                return index;
            }
        }

        internal static GH_Structure<GH_Boolean> Mask(GH_Structure<IGH_Goo> input,
            IReadOnlyList<PanelSourceAddress> retainedSources, IReadOnlyList<int> positions)
        {
            var flags = new Dictionary<string, bool[]>(StringComparer.Ordinal);
            for (int b = 0; b < input.PathCount; b++)
                flags.Add(input.Paths[b].ToString(), new bool[input.Branches[b].Count]);
            foreach (int position in positions)
            {
                var address = retainedSources[position];
                flags[address.Path][address.ItemIndex] = true;
            }
            var result = new GH_Structure<GH_Boolean>();
            for (int b = 0; b < input.PathCount; b++)
            {
                var branch = result.EnsurePath(new GH_Path(input.Paths[b]));
                var values = flags[input.Paths[b].ToString()];
                foreach (bool value in values) branch.Add(new GH_Boolean(value));
            }
            return result;
        }

        internal static PanelPairedData? Pair(GH_Structure<IGH_Goo> input, GH_Structure<IGH_Goo> paired,
            IReadOnlyList<PanelSourceAddress> retainedSources, int trim, bool simplify, bool flatten, bool reverse, out string? error)
        {
            error = PanelPairing.Diagnose(PanelPairedData.Shape(input), PanelPairedData.Shape(paired));
            return error == null ? new PanelPairedData(input, paired, retainedSources, trim, simplify, flatten, reverse) : null;
        }
    }
}
