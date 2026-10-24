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
            int trimDepth, bool simplify, bool flatten, bool reverse, bool createSet,
            out IReadOnlyList<PanelSourceAddress> retainedSources, out IReadOnlyList<PanelSetOccurrences>? occurrences)
        {
            occurrences = null;
            var retained = new List<PanelSourceAddress>(source.DataCount);
            if (trimDepth == 0 && !simplify && !flatten && !reverse && !createSet)
            {
                for (int b = 0; b < source.PathCount; b++)
                {
                    string path = source.Paths[b].ToString();
                    for (int i = 0; i < source.Branches[b].Count; i++)
                        retained.Add(new PanelSourceAddress(path, i));
                }
                retainedSources = retained.AsReadOnly();
                return source;
            }

            // Transform only positions; each position carries its typed value and original
            // address together, so merging or reversing cannot separate them.
            var markers = new GH_Structure<IGH_Goo>();
            var inputs = new List<(IGH_Goo Item, PanelSourceAddress Address)>(source.DataCount);
            for (int b = 0; b < source.PathCount; b++)
            {
                var path = new GH_Path(source.Paths[b]);
                var pathText = path.ToString();
                var positions = markers.EnsurePath(path);
                positions.Capacity = source.Branches[b].Count;
                for (int i = 0; i < source.Branches[b].Count; i++)
                {
                    positions.Add(new GH_Integer(inputs.Count));
                    inputs.Add((source.Branches[b][i], new PanelSourceAddress(pathText, i)));
                }
            }
            markers = Transform(markers, trimDepth, simplify, flatten, reverse);
            if (!createSet)
            {
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
            var groups = new List<PanelSetOccurrences>();
            var nullKey = new object();
            // Replace positions with retained values in the same owned tree.
            for (int b = 0; b < markers.PathCount; b++)
            {
                var branch = markers.Branches[b];
                var seen = new Dictionary<object, int>();
                var locations = new List<List<PanelSourceAddress>>();
                int count = 0;
                for (int i = 0; i < branch.Count; i++)
                {
                    var (item, address) = inputs[((GH_Integer)branch[i]).Value];
                    var key = item == null ? nullKey : SetKey(item);
                    if (key != null && seen.TryGetValue(key, out int group))
                    {
                        locations[group].Add(address);
                        continue;
                    }
                    if (key != null) seen.Add(key, locations.Count);
                    locations.Add(new List<PanelSourceAddress>(1) { address });
                    branch[count++] = item!;
                    retained.Add(address);
                }
                branch.RemoveRange(count, branch.Count - count);
                foreach (var location in locations) groups.Add(new PanelSetOccurrences(location));
            }
            occurrences = groups.AsReadOnly();
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
