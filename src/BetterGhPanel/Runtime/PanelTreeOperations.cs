using System.Linq;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel.Runtime
{
    // Structural copies own their paths and lists; typed values remain untouched.
    internal static class PanelTreeOperations
    {
        internal static GH_Structure<IGH_Goo> Apply(GH_Structure<IGH_Goo> source,
            int trimDepth, bool simplify, bool flatten, bool reverse)
        {
            if (trimDepth == 0 && !simplify && !flatten && !reverse) return source;
            var result = new GH_Structure<IGH_Goo>();
            for (int b = 0; b < source.PathCount; b++)
            {
                var indices = source.Paths[b].Indices;
                var path = trimDepth >= indices.Length ? new GH_Path(0) :
                    new GH_Path(indices.Take(indices.Length - trimDepth).ToArray());
                result.EnsurePath(path);
                result.AppendRange(source.Branches[b], path);
            }
            if (simplify) result.Simplify(GH_SimplificationMode.CollapseAllOverlaps);
            if (flatten) result.Flatten(new GH_Path(0));
            if (reverse) result.Reverse();
            return result;
        }
    }
}
