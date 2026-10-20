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
            var selectedBranch = NormalizeBranchPath(query.BranchPath);
            var branchPrefix = BranchPrefix(query, selectedBranch);
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
            var hasItemFilter = search.HasFilter && !search.IsBranchFilter || query.TypeName != null || query.OnlyInvalid;

            // Empty branches remain visible when browsing the tree, but not in item-filter results.
            if (!hasItemFilter)
            {
                for (var i = 0; i < snapshot.Branches.Count; i++)
                {
                    if (work.ShouldYield()) yield return null;
                    CheckCancellation(i, cancellationToken);
                    var branch = snapshot.Branches[i];
                    if (MatchesBranch(branch.Path, selectedBranch, branchPrefix) && search.MatchesBranchCount(branch.ItemCount))
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
                if (!MatchesBranch(row.Path, selectedBranch, branchPrefix)) continue;
                // Counts describe each transformed branch before any per-item restrictions.
                if (search.IsBranchFilter && search.HasFilter &&
                    (!snapshot.BranchItemCounts.TryGetValue(row.Path, out var itemCount) || !search.MatchesBranchCount(itemCount))) continue;
                if (query.TypeName != null && !string.Equals(row.TypeName, query.TypeName, StringComparison.OrdinalIgnoreCase)) continue;
                if (query.OnlyInvalid && row.IsValid) continue;
                PanelGeometryInfo? measured = null;
                measuredGeometry?.TryGetValue(i, out measured);
                if (!search.Matches(row, snapshot.UnitConversion, measured, cancellationToken)) continue;

                rowPositions.Add(i);
                if (sortText != null && !row.IsNull && !HasFiniteNumber(row))
                    sortText.Add(i, PanelValuePresentation.FormatDisplay(row, snapshot.UnitConversion, query.DecimalPlaces, measured, culture));
                branches.Add(row.Path);
                Increment(typeCounts, row.TypeName);
                Increment(branchCounts, row.Path);
                if (row.IsNull) nullCount++;
                if (!row.IsValid) invalidCount++;
                if (!row.IsNull && row.IsValid)
                {
                    if (HasFiniteNumber(row))
                    {
                        // Numeric equality deliberately unifies integer/number values and signed zero.
                        var number = row.NumericValue!.Value == 0 ? 0 : row.NumericValue.Value;
                        if (!numericValues.TryGetValue(number, out var frequency))
                        {
                            frequency = new ValueCount(number, null);
                            numericValues.Add(number, frequency);
                            uniqueValues.Add(frequency);
                        }
                        frequency.Count++;
                        scalarCount++;
                    }
                    else if (row.StringValue != null)
                    {
                        if (!stringValues.TryGetValue(row.StringValue, out var frequency))
                        {
                            frequency = new ValueCount(null, row.StringValue);
                            stringValues.Add(row.StringValue, frequency);
                            uniqueValues.Add(frequency);
                        }
                        frequency.Count++;
                        scalarCount++;
                    }
                }
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

            if (work.ShouldYield()) yield return null;
            cancellationToken.ThrowIfCancellationRequested();
            var positions = rowPositions.ToArray();
            if (query.SortMode != PanelSortMode.SourceOrder)
                foreach (var step in StableSort(positions, snapshot.Rows, sortText!, query.SortMode == PanelSortMode.ValueDescending, cancellationToken, work))
                    yield return step;
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
            var statistics = new PanelStatistics(positions.Length, branches.Count, numericCount, nullCount,
                invalidCount, numericCount == 0 ? (double?)null : minimum,
                numericCount == 0 ? (double?)null : maximum, numericCount == 0 ? (double?)null : mean,
                new ReadOnlyDictionary<string, int>(typeCounts), new ReadOnlyDictionary<string, int>(branchCounts),
                Array.AsReadOnly(frequencies), scalarCount);
            yield return new PanelQueryResult(snapshot, positions, statistics, branches);
        }

        public static bool RequiresGeometryMeasurements(PanelQueryOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (UsesDisplayedText(options)) return true;
            bool hasSearch = !string.IsNullOrWhiteSpace(options.Search);
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
            if (!sorting && (!filter.HasFilter || !filter.IsGeometry && !filter.UsesDisplayText)) return Array.Empty<int>();
            var selected = NormalizeBranchPath(query.BranchPath);
            var prefix = BranchPrefix(query, selected);
            var candidates = new List<int>();
            for (int index = 0; index < snapshot.Rows.Count; index++)
            {
                CheckCancellation(index, cancellationToken);
                var row = snapshot.Rows[index];
                if (!MatchesBranch(row.Path, selected, prefix) ||
                    query.TypeName != null && !string.Equals(row.TypeName, query.TypeName, StringComparison.OrdinalIgnoreCase) ||
                    query.OnlyInvalid && row.IsValid || !filter.CanMeasure(row, sorting)) continue;
                if (filter.IsBranchFilter && filter.HasFilter &&
                    (!snapshot.BranchItemCounts.TryGetValue(row.Path, out var itemCount) || !filter.MatchesBranchCount(itemCount))) continue;
                candidates.Add(index);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return candidates.AsReadOnly();
        }

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

        private static string? BranchPrefix(PanelQueryOptions query, string? selected) => query.BranchPrefix && selected != null
            ? selected.EndsWith("}", StringComparison.Ordinal) ? selected.Substring(0, selected.Length - 1) + ";" : selected + ";"
            : null;

        private static bool MatchesBranch(string path, string? selected, string? prefix)
        {
            if (selected == null) return true;
            if (string.Equals(path, selected, StringComparison.Ordinal)) return true;
            // Prefix selection means descendants of {0;1}, not siblings like {0;10}.
            return prefix != null && path.StartsWith(prefix, StringComparison.Ordinal);
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

        // Own merge sort keeps cancellation exceptions unwrapped and never mutates snapshot data.
        private static IEnumerable<PanelQueryResult?> StableSort(int[] ids, IReadOnlyList<PanelRow> rows,
            IReadOnlyDictionary<int, string> sortText, bool descending,
            CancellationToken token, PanelQueryWork work)
        {
            if (ids.Length < 2) yield break;
            if (work.ShouldYield()) yield return null;
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
                        if (work.ShouldYield()) yield return null;
                        CheckCancellation(output, token);
                        destination[output] = left < middle && (right >= end ||
                            Compare(source[left], source[right], rows, sortText, descending) <= 0)
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
            if (!ReferenceEquals(source, ids)) Array.Copy(source, ids, ids.Length);
        }

        private static int Compare(int leftId, int rightId, IReadOnlyList<PanelRow> rows,
            IReadOnlyDictionary<int, string> sortText, bool descending)
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
                comparison = StringComparer.OrdinalIgnoreCase.Compare(sortText[leftId], sortText[rightId]);
            }
            if (comparison != 0) return descending ? -comparison : comparison;
            return leftId.CompareTo(rightId);
        }
    }
}
