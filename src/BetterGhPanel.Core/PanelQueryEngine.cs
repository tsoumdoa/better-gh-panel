using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;

namespace BetterGhPanel.Core
{
    public static class PanelQueryEngine
    {
        public static PanelQueryResult Execute(PanelSnapshot snapshot, PanelQueryOptions options,
            CancellationToken cancellationToken = default)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (options == null) throw new ArgumentNullException(nameof(options));
            cancellationToken.ThrowIfCancellationRequested();
            // Capture mutable UI settings once before the background scan.
            var query = options.Clone();
            var search = (query.Search ?? string.Empty).Trim();
            var selectedBranch = string.IsNullOrWhiteSpace(query.BranchPath) ? null : query.BranchPath!.Trim();
            var branchPrefix = query.BranchPrefix && selectedBranch != null
                ? selectedBranch.EndsWith("}", StringComparison.Ordinal)
                    ? selectedBranch.Substring(0, selectedBranch.Length - 1) + ";"
                    : selectedBranch + ";"
                : null;
            var numericPredicate = ParseNumericPredicate(search);
            var rowIds = new List<int>(snapshot.Rows.Count);
            var branchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var hasItemFilter = search.Length != 0 || query.TypeName != null || query.OnlyInvalid;

            // Empty branches remain visible when browsing the tree, but not in item-filter results.
            if (!hasItemFilter)
            {
                for (var i = 0; i < snapshot.Branches.Count; i++)
                {
                    CheckCancellation(i, cancellationToken);
                    var branch = snapshot.Branches[i];
                    if (MatchesBranch(branch.Path, selectedBranch, branchPrefix)) branchCounts[branch.Path] = 0;
                }
            }

            var numericCount = 0;
            var nullCount = 0;
            var invalidCount = 0;
            var minimum = double.PositiveInfinity;
            var maximum = double.NegativeInfinity;
            var mean = 0.0;
            for (var i = 0; i < snapshot.Rows.Count; i++)
            {
                CheckCancellation(i, cancellationToken);
                var row = snapshot.Rows[i];
                if (!MatchesBranch(row.Path, selectedBranch, branchPrefix)) continue;
                if (query.TypeName != null && !string.Equals(row.TypeName, query.TypeName, StringComparison.OrdinalIgnoreCase)) continue;
                if (query.OnlyInvalid && row.IsValid) continue;
                if (numericPredicate != null)
                {
                    if (!HasFiniteNumber(row) || !numericPredicate(row.NumericValue!.Value)) continue;
                }
                else if (search.Length != 0 && !Contains(row.DisplayValue, search) &&
                    !Contains(row.Path, search) && !Contains(row.TypeName, search)) continue;

                rowIds.Add(i);
                Increment(branchCounts, row.Path);
                Increment(typeCounts, row.TypeName);
                if (row.IsNull) nullCount++;
                if (!row.IsValid) invalidCount++;
                if (!HasFiniteNumber(row)) continue;
                var value = row.NumericValue!.Value;
                numericCount++;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                var delta = value - mean;
                // The alternate update handles opposite-sign values near double.MaxValue.
                mean = double.IsInfinity(delta)
                    ? mean * ((numericCount - 1.0) / numericCount) + value / numericCount
                    : mean + delta / numericCount;
            }

            var ids = rowIds.ToArray();
            if (query.SortMode != PanelSortMode.SourceOrder)
                StableSort(ids, snapshot.Rows, query.SortMode == PanelSortMode.ValueDescending, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var statistics = new PanelStatistics(ids.Length, branchCounts.Count, numericCount, nullCount,
                invalidCount, numericCount == 0 ? (double?)null : minimum,
                numericCount == 0 ? (double?)null : maximum, numericCount == 0 ? (double?)null : mean,
                new ReadOnlyDictionary<string, int>(typeCounts), () => BuildHistogram(snapshot, ids, minimum, maximum));
            return new PanelQueryResult(snapshot, ids, statistics, new ReadOnlyDictionary<string, int>(branchCounts));
        }

        private static IReadOnlyList<int> BuildHistogram(PanelSnapshot snapshot, int[] positions, double minimum, double maximum)
        {
            var histogram = new int[20];
            var range = maximum - minimum;
            foreach (var position in positions)
            {
                var row = snapshot.Rows[position];
                if (!HasFiniteNumber(row)) continue;
                var value = row.NumericValue!.Value;
                var fraction = range == 0 ? 0 : double.IsInfinity(range)
                    ? (value / 2 - minimum / 2) / (maximum / 2 - minimum / 2)
                    : (value - minimum) / range;
                var bin = Math.Max(0, Math.Min(19, (int)(fraction * 20)));
                histogram[bin]++;
            }
            return Array.AsReadOnly(histogram);
        }

        private static bool MatchesBranch(string path, string? selected, string? prefix)
        {
            if (selected == null) return true;
            if (string.Equals(path, selected, StringComparison.Ordinal)) return true;
            // Prefix selection means descendants of {0;1}, not siblings like {0;10}.
            return prefix != null && path.StartsWith(prefix, StringComparison.Ordinal);
        }

        private static bool Contains(string value, string search) =>
            value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }

        private static bool HasFiniteNumber(PanelRow row) => !row.IsNull && row.NumericValue.HasValue &&
            !double.IsNaN(row.NumericValue.Value) && !double.IsInfinity(row.NumericValue.Value);

        private static void CheckCancellation(int index, CancellationToken token)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
        }

        private static Func<double, bool>? ParseNumericPredicate(string text)
        {
            var operatorLength = text.StartsWith(">=", StringComparison.Ordinal) ||
                text.StartsWith("<=", StringComparison.Ordinal) || text.StartsWith("==", StringComparison.Ordinal) ? 2 :
                text.StartsWith(">", StringComparison.Ordinal) || text.StartsWith("<", StringComparison.Ordinal) ||
                text.StartsWith("=", StringComparison.Ordinal) ? 1 : 0;
            if (operatorLength == 0 || !double.TryParse(text.Substring(operatorLength).Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) ||
                double.IsNaN(threshold) || double.IsInfinity(threshold)) return null;
            switch (text.Substring(0, operatorLength))
            {
                case ">": return value => value > threshold;
                case ">=": return value => value >= threshold;
                case "<": return value => value < threshold;
                case "<=": return value => value <= threshold;
                default: return value => value == threshold;
            }
        }

        // Own merge sort keeps cancellation exceptions unwrapped and never mutates snapshot data.
        private static void StableSort(int[] ids, IReadOnlyList<PanelRow> rows, bool descending, CancellationToken token)
        {
            if (ids.Length < 2) return;
            var buffer = new int[ids.Length];
            var source = ids;
            var destination = buffer;
            for (var width = 1; width < ids.Length; width = width > ids.Length / 2 ? ids.Length : width * 2)
            {
                token.ThrowIfCancellationRequested();
                for (var start = 0; start < ids.Length;)
                {
                    var middle = (int)Math.Min((long)start + width, ids.Length);
                    var end = (int)Math.Min((long)middle + width, ids.Length);
                    var left = start;
                    var right = middle;
                    for (var output = start; output < end; output++)
                    {
                        CheckCancellation(output, token);
                        destination[output] = left < middle && (right >= end ||
                            Compare(source[left], source[right], rows, descending) <= 0)
                            ? source[left++] : source[right++];
                    }
                    start = end;
                }
                var temporary = source;
                source = destination;
                destination = temporary;
            }
            if (!ReferenceEquals(source, ids)) Array.Copy(source, ids, ids.Length);
        }

        private static int Compare(int leftId, int rightId, IReadOnlyList<PanelRow> rows, bool descending)
        {
            var left = rows[leftId];
            var right = rows[rightId];
            if (left.IsNull != right.IsNull) return left.IsNull ? 1 : -1;
            if (left.IsNull) return leftId.CompareTo(rightId);
            var leftNumeric = HasFiniteNumber(left);
            var rightNumeric = HasFiniteNumber(right);
            if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
            int comparison;
            if (leftNumeric) comparison = left.NumericValue!.Value.CompareTo(right.NumericValue!.Value);
            else
            {
                comparison = StringComparer.OrdinalIgnoreCase.Compare(left.TypeName, right.TypeName);
                if (comparison != 0) return comparison;
                comparison = StringComparer.OrdinalIgnoreCase.Compare(left.DisplayValue, right.DisplayValue);
            }
            if (comparison != 0) return descending ? -comparison : comparison;
            return leftId.CompareTo(rightId);
        }
    }
}
