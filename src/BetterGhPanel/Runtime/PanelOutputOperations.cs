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
            IReadOnlyList<int> positions, IEnumerable<string> branchPaths)
        {
            var result = new GH_Structure<IGH_Goo>();
            var paths = source.Paths.ToDictionary(path => path.ToString(), path => new GH_Path(path), StringComparer.Ordinal);
            foreach (string path in branchPaths) result.EnsurePath(paths[path]);
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
                result.EnsurePath(paths[query.Snapshot.Rows[position].Path]).Add(item);
            }
            return result;
        }

        internal static GH_Structure<IGH_Goo> SetItems(GH_Structure<IGH_Goo> source, PanelQueryResult query,
            IReadOnlyList<int> positions, IReadOnlyList<string> branchPaths, bool flatten)
        {
            var result = new GH_Structure<IGH_Goo>();
            if (positions.Count == 0 && branchPaths.Count == 0) return result;
            var set = query.DisplaySet!;
            var paths = source.Paths.ToDictionary(path => path.ToString(), path => new GH_Path(path), StringComparer.Ordinal);
            var branches = new Dictionary<string, IList<IGH_Goo>>(StringComparer.Ordinal);
            for (int b = 0; b < source.PathCount; b++) branches.Add(source.Paths[b].ToString(), source.Branches[b]);
            if (set.IsBranchSet)
            {
                foreach (string representative in branchPaths)
                {
                    int k = set.BranchIndices[representative];
                    foreach (string path in set.BranchOccurrences[representative])
                        result.EnsurePath(new GH_Path(new[] { k }.Concat(paths[path].Indices).ToArray()));
                    foreach (int member in set.BranchMembers[representative])
                    {
                        var row = query.Snapshot.Rows[member];
                        var path = new GH_Path(new[] { k }.Concat(paths[row.Path].Indices).ToArray());
                        result.EnsurePath(path).Add(branches[row.Path][row.OriginalIndex]);
                    }
                }
            }
            else foreach (int representative in positions.OrderBy(value => value))
            {
                var row = query.Snapshot.Rows[representative];
                int k = set.Indices[representative];
                var path = flatten ? new GH_Path(k) : paths[row.Path].AppendElement(k);
                var branch = result.EnsurePath(path);
                foreach (int member in set.Members[representative])
                {
                    var memberRow = query.Snapshot.Rows[member];
                    branch.Add(branches[memberRow.Path][memberRow.OriginalIndex]);
                }
            }
            return result;
        }

        internal static GH_Structure<IGH_Goo> DisplaySet(GH_Structure<IGH_Goo> source, PanelQueryResult query,
            IReadOnlyList<int> positions, IEnumerable<string> branchPaths)
        {
            var result = new GH_Structure<IGH_Goo>();
            var paths = new Dictionary<string, GH_Path>(StringComparer.Ordinal);
            foreach (var path in source.Paths) paths[path.ToString()] = new GH_Path(path);
            var set = query.DisplaySet ?? throw new ArgumentException("A completed displayed set is required.", nameof(query));
            foreach (string path in branchPaths)
            {
                result.EnsurePath(paths[path]);
                if (set.IsBranchSet) result.Append(new GH_Integer(query.Snapshot.BranchItemCounts[path]), paths[path]);
            }
            if (!set.IsBranchSet)
                foreach (int position in positions.OrderBy(value => value))
                {
                    var value = set.Values[position];
                    IGH_Goo item = value.Number.HasValue ? new GH_Number(value.Number.Value) : new GH_String(value.Text);
                    result.Append(item, paths[query.Snapshot.Rows[position].Path]);
                }
            return result;
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

        internal static GH_Structure<IGH_Goo>? Pair(GH_Structure<IGH_Goo> input, GH_Structure<IGH_Goo> paired,
            GH_Structure<IGH_Goo> transformed, IReadOnlyList<PanelSourceAddress> retainedSources, out string? error)
        {
            error = PanelPairing.Diagnose(BranchShape(input), BranchShape(paired));
            if (error != null) return null;
            var branches = new Dictionary<string, IList<IGH_Goo>>(StringComparer.Ordinal);
            for (int b = 0; b < input.PathCount; b++)
            {
                branches.Add(paired.Paths[b].ToString(), paired.Branches[b]);
            }
            var result = new GH_Structure<IGH_Goo>();
            int position = 0;
            for (int b = 0; b < transformed.PathCount; b++)
            {
                var branch = result.EnsurePath(new GH_Path(transformed.Paths[b]));
                for (int i = 0; i < transformed.Branches[b].Count; i++)
                {
                    var address = retainedSources[position++];
                    branch.Add(branches[address.Path][address.ItemIndex]);
                }
            }
            return result;
        }

        private static PanelBranch[] BranchShape(GH_Structure<IGH_Goo> tree)
        {
            var branches = new PanelBranch[tree.PathCount];
            for (int b = 0; b < branches.Length; b++) branches[b] = new PanelBranch(tree.Paths[b].ToString(), tree.Branches[b].Count);
            return branches;
        }
    }
}
