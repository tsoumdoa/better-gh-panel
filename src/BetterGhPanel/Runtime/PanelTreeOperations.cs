using System.Collections.Generic;
using System.Linq;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel.Runtime
{
    // Structural copies own their paths and lists; typed values remain untouched.
    internal static class PanelTreeOperations
    {
        internal static GH_Structure<IGH_Goo> CopyStructure(GH_Structure<IGH_Goo> source)
        {
            var copy = new GH_Structure<IGH_Goo>();
            for (int b = 0; b < source.PathCount; b++)
            {
                var path = new GH_Path(source.Paths[b]);
                copy.EnsurePath(path);
                copy.AppendRange(source.Branches[b], path);
            }
            return copy;
        }

        internal static GH_Structure<IGH_Goo> Filter(GH_Structure<IGH_Goo> source, PanelQueryResult query)
        {
            if (query.RowPositions.Count == source.DataCount && query.BranchPaths.Count == source.PathCount) return source;
            return FilterRows(source, new HashSet<int>(query.RowPositions), new HashSet<string>(query.BranchPaths));
        }

        internal static GH_Structure<IGH_Goo> Select(GH_Structure<IGH_Goo> source, IReadOnlyList<int> positions) =>
            positions.Count == 0 ? new GH_Structure<IGH_Goo>() : FilterRows(source, new HashSet<int>(positions), null);

        internal static GH_Structure<IGH_Goo> SelectBranches(GH_Structure<IGH_Goo> source, IEnumerable<string> branchPaths)
        {
            var paths = new HashSet<string>(branchPaths);
            var result = new GH_Structure<IGH_Goo>();
            for (int b = 0; b < source.PathCount; b++)
            {
                if (!paths.Contains(source.Paths[b].ToString())) continue;
                var path = new GH_Path(source.Paths[b]);
                result.EnsurePath(path);
                result.AppendRange(source.Branches[b], path);
            }
            return result;
        }

        // Filter retains matching empty paths; selection emits only selected rows.
        // Both traverse the tree rather than the view/selection order.
        private static GH_Structure<IGH_Goo> FilterRows(GH_Structure<IGH_Goo> source, HashSet<int> positions, HashSet<string>? paths)
        {
            var result = new GH_Structure<IGH_Goo>();
            int position = 0;
            for (int b = 0; b < source.PathCount; b++)
            {
                var path = new GH_Path(source.Paths[b]);
                if (paths?.Contains(path.ToString()) == true) result.EnsurePath(path);
                foreach (var item in source.Branches[b])
                {
                    if (positions.Contains(position)) result.Append(item, path);
                    position++;
                }
            }
            return result;
        }

        internal static GH_Structure<IGH_Goo> ApplyWithSources(GH_Structure<IGH_Goo> source,
            int trimDepth, bool simplify, bool flatten, bool reverse, out IReadOnlyList<PanelSourceAddress> retainedSources)
        {
            var retained = new List<PanelSourceAddress>(source.DataCount);
            if (trimDepth == 0 && !simplify && !flatten && !reverse)
            {
                for (int b = 0; b < source.PathCount; b++)
                    for (int i = 0; i < source.Branches[b].Count; i++)
                        retained.Add(new PanelSourceAddress(source.Paths[b].ToString(), i));
                retainedSources = retained.AsReadOnly();
                return source;
            }
            // Transform positions so typed input and original addresses stay aligned.
            // Create set belongs to the displayed-value query, after filters and units.
            var markers = new GH_Structure<IGH_Goo>();
            var inputs = new List<(IGH_Goo Item, PanelSourceAddress Address)>(source.DataCount);
            for (int b = 0; b < source.PathCount; b++)
            {
                var path = new GH_Path(source.Paths[b]);
                var positions = markers.EnsurePath(path);
                positions.Capacity = source.Branches[b].Count;
                for (int i = 0; i < source.Branches[b].Count; i++)
                {
                    positions.Add(new GH_Integer(inputs.Count));
                    inputs.Add((source.Branches[b][i], new PanelSourceAddress(path.ToString(), i)));
                }
            }
            markers = Transform(markers, trimDepth, simplify, flatten, reverse);
            foreach (var branch in markers.Branches)
                for (int i = 0; i < branch.Count; i++)
                {
                    var (item, address) = inputs[((GH_Integer)branch[i]).Value];
                    branch[i] = item;
                    retained.Add(address);
                }
            retainedSources = retained.AsReadOnly();
            return markers;
        }
        private static GH_Structure<IGH_Goo> Transform(GH_Structure<IGH_Goo> source,
            int trimDepth, bool simplify, bool flatten, bool reverse)
        {
            if (trimDepth == 0 && !simplify && !flatten && !reverse) return source;
            var result = source;
            // Trim creates its own structure; in-place tools need a copy first.
            if (simplify || trimDepth == 0)
            {
                result = CopyStructure(source);
                if (simplify) result.Simplify(GH_SimplificationMode.CollapseAllOverlaps);
            }
            if (trimDepth > 0)
            {
                var trimmed = new GH_Structure<IGH_Goo>();
                for (int b = 0; b < result.PathCount; b++)
                {
                    var indices = result.Paths[b].Indices;
                    var path = trimDepth >= indices.Length ? new GH_Path(0) :
                        new GH_Path(indices.Take(indices.Length - trimDepth).ToArray());
                    trimmed.EnsurePath(path);
                    trimmed.AppendRange(result.Branches[b], path);
                }
                result = trimmed;
            }
            if (flatten) result.Flatten(new GH_Path(0));
            if (reverse) result.Reverse();
            return result;
        }

    }
}
