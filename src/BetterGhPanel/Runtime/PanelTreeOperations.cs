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
            int trimDepth, bool simplify, bool flatten, bool reverse, bool createSet)
        {
            if (trimDepth == 0 && !simplify && !flatten && !reverse && !createSet) return source;
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
            if (createSet)
            {
                foreach (var branch in result.Branches)
                {
                    var seen = new HashSet<object>();
                    bool seenNull = false;
                    int count = 0;
                    for (int i = 0; i < branch.Count; i++)
                    {
                        var item = branch[i];
                        if (item == null)
                        {
                            if (seenNull) continue;
                            seenNull = true;
                        }
                        else
                        {
                            var key = SetKey(item);
                            if (key != null && !seen.Add(key)) continue;
                        }
                        branch[count++] = item!;
                    }
                    branch.RemoveRange(count, branch.Count - count);
                }
            }
            return result;
        }

        // Only compare exact, supported values. Summaries are never equality keys:
        // different curves or third-party goo can have identical display text.
        private static object? SetKey(IGH_Goo item)
        {
            object? key = item switch
            {
                GH_Number number => number.Value,
                GH_Integer integer => (double)integer.Value,
                GH_String text => text.Value,
                GH_Boolean boolean => boolean.Value,
                GH_Colour colour => colour.Value.ToArgb(),
                GH_ComplexNumber complex => complex.Value,
                GH_Interval interval => interval.Value,
                GH_Interval2D interval => interval.Value,
                GH_Point point => point.Value,
                GH_Vector vector => vector.Value,
                GH_Line line => line.Value,
                GH_Plane plane => plane.Value,
                GH_Rectangle rectangle => rectangle.Value,
                GH_Box box => box.Value,
                GH_Arc arc => arc.Value,
                GH_Circle circle => circle.Value,
                GH_Transform transform => transform.Value,
                GH_Guid guid => guid.Value,
                GH_Time time => time.Value,
                _ => null
            };
            return key != null && item.IsValid ? key : null;
        }
    }
}
