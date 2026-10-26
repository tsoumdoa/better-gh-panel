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
            CancellationToken cancellationToken = default, IReadOnlyDictionary<int, PanelGeometryInfo>? measuredGeometry = null)
        {
            using var work = Start(snapshot, options, cancellationToken, measuredGeometry);
            return work.Complete();
        }

        public static PanelQueryWork Start(PanelSnapshot snapshot, PanelQueryOptions options,
            CancellationToken cancellationToken = default, IReadOnlyDictionary<int, PanelGeometryInfo>? measuredGeometry = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (options == null) throw new ArgumentNullException(nameof(options));
            cancellationToken.ThrowIfCancellationRequested();
            // Capture options now, before either execution or handoff can be deferred.
            var query = options.Clone();
            var culture = CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone());
            return new PanelQueryWork(work => ExecuteSteps(snapshot, query, cancellationToken, measuredGeometry, culture, work).GetEnumerator());
        }

        private static IEnumerable<PanelQueryResult?> ExecuteSteps(PanelSnapshot snapshot, PanelQueryOptions query,
            CancellationToken cancellationToken, IReadOnlyDictionary<int, PanelGeometryInfo>? measuredGeometry,
            IFormatProvider culture, PanelQueryWork work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var search = new PanelSearchFilter(query, culture);
            var scope = new PanelQueryScope(snapshot, query, search);
            var set = query.CreateSet ? new PanelDisplaySet(snapshot, query, culture) : null;
            // Grow with the work performed instead of allocating for a whole large
            // tree before the first opportunity to yield.
            var rowPositions = new List<int>();
            var sortText = query.SortMode == PanelSortMode.SourceOrder ? null : new Dictionary<int, string>();
            var branches = new HashSet<string>(StringComparer.Ordinal);
            var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var branchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var numericValues = new Dictionary<double, ValueCount>();
            var stringValues = new Dictionary<string, ValueCount>(StringComparer.Ordinal);
            var uniqueValues = new List<ValueCount>();
            var scalarCount = 0;

            // Empty branches remain visible when browsing the tree, but not in item-filter results.
            if (!scope.HasItemFilter)
            {
                for (var i = 0; i < snapshot.Branches.Count; i++)
                {
                    if (work.ShouldYield()) yield return null;
                    CheckCancellation(i, cancellationToken);
                    var branch = snapshot.Branches[i];
                    if (scope.MatchesBranch(branch) && (set == null || set.IncludeBranch(branch.Path)))
                    {
                        branches.Add(branch.Path);
                        branchCounts[branch.Path] = 0;
                    }
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
                if (work.ShouldYield()) yield return null;
                CheckCancellation(i, cancellationToken);
                var row = snapshot.Rows[i];
                if (!scope.MatchesRow(row)) continue;
                PanelGeometryInfo? measured = null;
                measuredGeometry?.TryGetValue(i, out measured);
                if (!search.Matches(row, snapshot.UnitConversion, measured, cancellationToken)) continue;
                if (set != null && !set.IncludeRow(i, measured)) continue;

                rowPositions.Add(i);
                if (sortText != null && !row.IsNull && !HasFiniteNumber(row))
                    sortText.Add(i, PanelValuePresentation.FormatDisplay(row, snapshot.UnitConversion, query.DecimalPlaces, measured, culture));
                branches.Add(row.Path);
                Increment(branchCounts, row.Path);
                if (set?.IsBranchSet == true) continue;
                var projected = set == null ? null : set.Values[i];
                var numeric = projected != null ? projected.Number : HasFiniteNumber(row) ? row.NumericValue : null;
                var text = projected != null ? projected.Number.HasValue ? null : projected.Text : row.StringValue;
                Increment(typeCounts, projected == null ? row.TypeName : projected.Number.HasValue ? "Number" : "Text");
                if (projected == null && row.IsNull) nullCount++;
                if (projected == null && !row.IsValid) invalidCount++;
                if (projected != null || !row.IsNull && row.IsValid)
                {
                    if (numeric.HasValue)
                    {
                        // Numeric equality deliberately unifies integer/number values and signed zero.
                        var number = numeric.Value == 0 ? 0 : numeric.Value;
                        if (!numericValues.TryGetValue(number, out var frequency))
                        {
                            frequency = new ValueCount(number, null);
                            numericValues.Add(number, frequency);
                            uniqueValues.Add(frequency);
                        }
                        frequency.Count++;
                        scalarCount++;
                    }
                    else if (text != null)
                    {
                        if (!stringValues.TryGetValue(text, out var frequency))
                        {
                            frequency = new ValueCount(null, text);
                            stringValues.Add(text, frequency);
                            uniqueValues.Add(frequency);
                        }
                        frequency.Count++;
                        scalarCount++;
                    }
                }
                if (!numeric.HasValue) continue;
                var value = numeric.Value;
                numericCount++;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                var delta = value - mean;
                // The alternate update handles opposite-sign values near double.MaxValue.
                mean = double.IsInfinity(delta)
                    ? mean * ((numericCount - 1.0) / numericCount) + value / numericCount
                    : mean + delta / numericCount;
            }

            if (work.ShouldYield()) yield return null;
            cancellationToken.ThrowIfCancellationRequested();
            var positions = rowPositions.ToArray();
            if (query.SortMode != PanelSortMode.SourceOrder && set?.IsBranchSet != true)
                foreach (var step in SortBranches(positions, snapshot, branchCounts, sortText!,
                    query.SortMode == PanelSortMode.ValueDescending, cancellationToken, work, set?.Values))
                    yield return step;
            if (set?.IsBranchSet == true)
            {
                // One displayed/output number per retained branch. Membership still
                // carries the source items for paired data, masks and selection.
                foreach (string path in branches)
                {
                    if (work.ShouldYield()) yield return null;
                    cancellationToken.ThrowIfCancellationRequested();
                    double count = snapshot.BranchItemCounts[path];
                    branchCounts[path] = 1;
                    numericCount++;
                    scalarCount++;
                    minimum = Math.Min(minimum, count);
                    maximum = Math.Max(maximum, count);
                    mean += (count - mean) / numericCount;
                    uniqueValues.Add(new ValueCount(count, null) { Count = 1 });
                }
                if (numericCount > 0) typeCounts.Add("Integer", numericCount);
            }
            if (work.ShouldYield()) yield return null;
            cancellationToken.ThrowIfCancellationRequested();
            var frequencies = new PanelValueFrequency[uniqueValues.Count];
            for (int index = 0; index < frequencies.Length; index++)
            {
                if (work.ShouldYield()) yield return null;
                CheckCancellation(index, cancellationToken);
                var value = uniqueValues[index];
                frequencies[index] = new PanelValueFrequency(value.Number, value.Text, value.Count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var statistics = new PanelStatistics(set?.IsBranchSet == true ? branches.Count : positions.Length, branches.Count, numericCount, nullCount,
                invalidCount, numericCount == 0 ? (double?)null : minimum,
                numericCount == 0 ? (double?)null : maximum, numericCount == 0 ? (double?)null : mean,
                new ReadOnlyDictionary<string, int>(typeCounts), new ReadOnlyDictionary<string, int>(branchCounts),
                Array.AsReadOnly(frequencies), scalarCount);
            PanelDisplaySetResult? displaySet = null;
            if (set != null)
                foreach (var step in set.Complete(work, cancellationToken))
                    if (step == null) yield return null;
                    else displaySet = step;
            yield return new PanelQueryResult(snapshot, positions, statistics, branches, displaySet);
        }

        public static bool RequiresGeometryMeasurements(PanelQueryOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (UsesDisplayedText(options)) return true;
            if (SetNeedsGeometry(options)) return true;
            bool hasSearch = !(options.DisplayFilterValue && options.SearchMode >= PanelSearchMode.Value) && !string.IsNullOrWhiteSpace(options.Search);
            if (hasSearch && (options.SearchMode == PanelSearchMode.Length ||
                options.SearchMode == PanelSearchMode.Area || options.SearchMode == PanelSearchMode.Volume)) return true;
            // Active scalar comparisons cannot return geometry or need geometry sort keys.
            return options.SortMode != PanelSortMode.SourceOrder &&
                (!hasSearch || options.SearchMode != PanelSearchMode.Value && options.SearchMode != PanelSearchMode.Text);
        }

        /// <summary>Whether row presentation can affect membership (including significant regex whitespace).</summary>
        public static bool UsesDisplayedText(PanelQueryOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            // Regex whitespace is significant, unlike the other search modes.
            if (options.SearchMode == PanelSearchMode.Regex) return !string.IsNullOrEmpty(options.Search);
            if (options.SearchMode == PanelSearchMode.Text)
                return !string.IsNullOrWhiteSpace(options.Search) && !PanelSearchFilter.IsNumericComparison(options.Search);
            return false;
        }

        /// <summary>Finds offscreen measurements needed by a query or display-text sort, after cheap tree filters.</summary>
        public static IReadOnlyList<int> GeometryCandidates(PanelSnapshot snapshot, PanelQueryOptions options,
            CancellationToken cancellationToken = default)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (options == null) throw new ArgumentNullException(nameof(options));
            cancellationToken.ThrowIfCancellationRequested();
            var query = options.Clone();
            var filter = new PanelSearchFilter(query);
            var sorting = query.SortMode != PanelSortMode.SourceOrder;
            bool setGeometry = SetNeedsGeometry(query);
            if (!sorting && !setGeometry && (!filter.HasFilter || !filter.IsGeometry && !filter.UsesDisplayText)) return Array.Empty<int>();
            var scope = new PanelQueryScope(snapshot, query, filter);
            var candidates = new List<int>();
            for (int index = 0; index < snapshot.Rows.Count; index++)
            {
                CheckCancellation(index, cancellationToken);
                var row = snapshot.Rows[index];
                bool forSet = setGeometry && (!query.DisplayFilterValue || row.GeometryInfo != null && PanelFilterValue.MatchesGeometry(query.SearchMode, row.GeometryInfo));
                if (!scope.MatchesRow(row) || !filter.CanMeasure(row, sorting || forSet)) continue;
                candidates.Add(index);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return candidates.AsReadOnly();
        }

        private static bool SetNeedsGeometry(PanelQueryOptions query) => query.CreateSet &&
            (!query.DisplayFilterValue || query.SearchMode >= PanelSearchMode.Length && query.SearchMode <= PanelSearchMode.Volume);

        /// <summary>Accepts a semicolon-separated path with or without braces. Blank means all branches.</summary>
        public static string? NormalizeBranchPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string text = path!.Trim();
            if (text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
                text = text.Substring(1, text.Length - 2);
            var parts = text.Split(';');
            for (int index = 0; index < parts.Length; index++)
            {
                if (!int.TryParse(parts[index].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    throw new ArgumentException("Enter a branch path such as 0;0;1 or {0;0;1}, using nonnegative integers separated by semicolons.");
                parts[index] = value.ToString(CultureInfo.InvariantCulture);
            }
            return "{" + string.Join(";", parts) + "}";
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }

        private static bool HasFiniteNumber(PanelRow row) => !row.IsNull && row.IsValid && row.NumericValue.HasValue &&
            !double.IsNaN(row.NumericValue.Value) && !double.IsInfinity(row.NumericValue.Value);

        private sealed class ValueCount
        {
            internal ValueCount(double? number, string? text) { Number = number; Text = text; }
            internal double? Number { get; }
            internal string? Text { get; }
            internal int Count { get; set; }
        }

        private static void CheckCancellation(int index, CancellationToken token)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
        }

        private sealed class BranchSortRange
        {
            internal BranchSortRange(int start, int count) { Start = start; Count = count; Next = start; }
            internal int Start { get; }
            internal int Count { get; }
            internal int Next { get; set; }
        }

        // Group stably in transformed-tree order, even if snapshot rows are interleaved.
        // Only values in the same branch compete; path text never determines branch order.
        private static IEnumerable<PanelQueryResult?> SortBranches(int[] ids, PanelSnapshot snapshot,
            IReadOnlyDictionary<string, int> branchCounts, IReadOnlyDictionary<int, string> sortText,
            bool descending, CancellationToken token, PanelQueryWork work, IReadOnlyDictionary<int, PanelDisplayedValue>? displayedValues = null)
        {
            if (ids.Length < 2) yield break;
            var byPath = new Dictionary<string, BranchSortRange>(StringComparer.Ordinal);
            var ranges = new List<BranchSortRange>();
            var offset = 0;
            for (var index = 0; index < snapshot.Branches.Count; index++)
            {
                if (work.ShouldYield()) yield return null;
                CheckCancellation(index, token);
                var path = snapshot.Branches[index].Path;
                if (!branchCounts.TryGetValue(path, out var count) || count == 0 || byPath.ContainsKey(path)) continue;
                var range = new BranchSortRange(offset, count);
                byPath.Add(path, range);
                ranges.Add(range);
                offset += count;
            }
            // Snapshots permit rows without matching branch metadata. Retain these
            // paths after declared branches, in order of their first matching row.
            for (var index = 0; index < ids.Length; index++)
            {
                if (work.ShouldYield()) yield return null;
                CheckCancellation(index, token);
                var path = snapshot.Rows[ids[index]].Path;
                if (byPath.ContainsKey(path)) continue;
                var range = new BranchSortRange(offset, branchCounts[path]);
                byPath.Add(path, range);
                ranges.Add(range);
                offset += range.Count;
            }
            if (work.ShouldYield()) yield return null;
            token.ThrowIfCancellationRequested();
            var grouped = new int[ids.Length];
            for (var index = 0; index < ids.Length; index++)
            {
                if (work.ShouldYield()) yield return null;
                CheckCancellation(index, token);
                var range = byPath[snapshot.Rows[ids[index]].Path];
                grouped[range.Next++] = ids[index];
            }
            for (var index = 0; index < ranges.Count; index++)
            {
                if (work.ShouldYield()) yield return null;
                CheckCancellation(index, token);
                var range = ranges[index];
                // The original positions are no longer needed, so reuse that array
                // as merge workspace instead of allocating once for every branch.
                foreach (var step in StableSort(grouped, ids, range.Start, range.Count,
                    snapshot.Rows, sortText, descending, token, work, displayedValues)) yield return step;
            }
            for (var index = 0; index < ids.Length; index++)
            {
                if (work.ShouldYield()) yield return null;
                CheckCancellation(index, token);
                ids[index] = grouped[index];
            }
            token.ThrowIfCancellationRequested();
        }

        // Own merge sort keeps cancellation exceptions unwrapped and never mutates snapshot data.
        private static IEnumerable<PanelQueryResult?> StableSort(int[] ids, int[] buffer, int offset, int count,
            IReadOnlyList<PanelRow> rows,
            IReadOnlyDictionary<int, string> sortText, bool descending,
            CancellationToken token, PanelQueryWork work, IReadOnlyDictionary<int, PanelDisplayedValue>? displayedValues)
        {
            if (count < 2) yield break;
            var limit = offset + count;
            var source = ids;
            var destination = buffer;
            for (var width = 1; width < count; width = width > count / 2 ? count : width * 2)
            {
                token.ThrowIfCancellationRequested();
                for (var start = offset; start < limit;)
                {
                    var middle = (int)Math.Min((long)start + width, limit);
                    var end = (int)Math.Min((long)middle + width, limit);
                    var left = start;
                    var right = middle;
                    for (var output = start; output < end; output++)
                    {
                        if (work.ShouldYield()) yield return null;
                        CheckCancellation(output, token);
                        destination[output] = left < middle && (right >= end ||
                            Compare(source[left], source[right], rows, sortText, descending, displayedValues) <= 0)
                            ? source[left++] : source[right++];
                    }
                    start = end;
                }
                var temporary = source;
                source = destination;
                destination = temporary;
            }
            if (work.ShouldYield()) yield return null;
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(source, ids))
            {
                for (var index = offset; index < limit; index++)
                {
                    if (work.ShouldYield()) yield return null;
                    CheckCancellation(index, token);
                    ids[index] = source[index];
                }
            }
        }

        private static int Compare(int leftId, int rightId, IReadOnlyList<PanelRow> rows,
            IReadOnlyDictionary<int, string> sortText, bool descending, IReadOnlyDictionary<int, PanelDisplayedValue>? displayedValues)
        {
            if (displayedValues != null)
            {
                var leftValue = displayedValues[leftId];
                var rightValue = displayedValues[rightId];
                if (leftValue.Number.HasValue != rightValue.Number.HasValue) return leftValue.Number.HasValue ? -1 : 1;
                int order = leftValue.Number.HasValue ? leftValue.Number.Value.CompareTo(rightValue.Number!.Value) :
                    StringComparer.OrdinalIgnoreCase.Compare(leftValue.Text, rightValue.Text);
                return order == 0 ? leftId.CompareTo(rightId) : descending ? -order : order;
            }
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
                comparison = StringComparer.OrdinalIgnoreCase.Compare(sortText[leftId], sortText[rightId]);
            }
            if (comparison != 0) return descending ? -comparison : comparison;
            return leftId.CompareTo(rightId);
        }
    }
}
