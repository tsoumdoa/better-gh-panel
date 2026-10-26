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
            error = null;
            if (input.PathCount != paired.PathCount)
            {
                error = "Paired F input must have the same paths and item counts per branch as D before tree tools.";
                return null;
            }
            var branches = new Dictionary<string, IList<IGH_Goo>>(StringComparer.Ordinal);
            for (int b = 0; b < input.PathCount; b++)
            {
                if (input.Paths[b].CompareTo(paired.Paths[b]) != 0 || input.Branches[b].Count != paired.Branches[b].Count)
                {
                    error = "Paired F input does not match D at " + input.Paths[b] +
                        ". Pair by original path and item index; paths and branch lengths must match exactly.";
                    return null;
                }
                branches.Add(input.Paths[b].ToString(), paired.Branches[b]);
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
    }
}
