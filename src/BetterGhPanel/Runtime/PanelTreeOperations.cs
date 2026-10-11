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
            var positions = new HashSet<int>(query.RowPositions);
            var paths = new HashSet<string>(query.BranchPaths);
            var result = new GH_Structure<IGH_Goo>();
            int position = 0;
            for (int b = 0; b < source.PathCount; b++)
            {
                var path = new GH_Path(source.Paths[b]);
                if (paths.Contains(path.ToString())) result.EnsurePath(path);
                foreach (var item in source.Branches[b])
                {
                    if (positions.Contains(position)) result.Append(item, path);
                    position++;
                }
            }
            return result;
        }

        internal static GH_Structure<IGH_Goo> Apply(GH_Structure<IGH_Goo> source,
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
