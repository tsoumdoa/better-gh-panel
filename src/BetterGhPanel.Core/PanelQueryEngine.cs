using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;

namespace BetterGhPanel.Core
{
    public static class PanelQueryEngine
    {
        public static bool HasItemFilter(PanelQueryOptions options) => PanelQueryScope.ItemFilter(options, new PanelSearchFilter(options));

        public static PanelQueryResult Execute(PanelSnapshot snapshot, PanelQueryOptions options,
            CancellationToken cancellationToken = default, IReadOnlyDictionary<int, PanelGeometryInfo>? measuredGeometry = null)
        {
            using var work = Start(snapshot, options, cancellationToken, measuredGeometry);
            return work.Complete();
        }

        // Live editors preview only searches that parse; a half-typed comparison or
        // regular expression keeps the previous view instead of flashing an error.
        public static bool IsValidSearch(PanelQueryOptions options)
        {
            if (options == null) return false;
            try
            {
                _ = new PanelSearchFilter(options);
                if (options.BranchPath != null) _ = NormalizeBranchPath(options.BranchPath);
                return true;
            }
            catch (ArgumentException) { return false; }
        }

        public static PanelQueryWork Start(PanelSnapshot snapshot, PanelQueryOptions options,
            CancellationToken cancellationToken = default, IReadOnlyDictionary<int, PanelGeometryInfo>? measuredGeometry = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (options == null) throw new ArgumentNullException(nameof(options));
            cancellationToken.ThrowIfCancellationRequested();
            // Capture options now, before either execution or handoff can be deferred.
            var query = options.Clone();
            return new PanelQueryWork(work => ExecuteSteps(snapshot, query, cancellationToken, measuredGeometry, work).GetEnumerator());
        }

        private static IEnumerable<PanelQueryResult?> ExecuteSteps(PanelSnapshot snapshot, PanelQueryOptions query,
            CancellationToken cancellationToken, IReadOnlyDictionary<int, PanelGeometryInfo>? measuredGeometry,
            PanelQueryWork work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var search = new PanelSearchFilter(query);
            var scope = new PanelQueryScope(snapshot, query, search);
            var set = query.CreateSet ? new PanelDisplaySet(snapshot, query) : null;
            // Grow with the work performed instead of allocating for a whole large
            // tree before the first opportunity to yield.
            var rowPositions = new List<int>();
            var outputValues = query.OutputValues && query.DisplayFilterValue && set == null ? new Dictionary<int, PanelPropertyValue?>() : null;
            var propertySortValues = query.ColumnSortKeys == null && set == null && query.SortMode != PanelSortMode.SourceOrder && query.DisplayFilterValue && ShowsMetric(query.SearchMode)
                ? new Dictionary<int, PanelDisplayedValue>() : null;
            // Sets and metric views already supply complete displayed sort keys.
            var sortText = query.ColumnSortKeys == null && query.SortMode != PanelSortMode.SourceOrder && set == null && propertySortValues == null
                ? new Dictionary<int, RowSortKey>() : null;
            var columnSortRows = query.ColumnSortKeys != null && query.SortMode != PanelSortMode.SourceOrder
                ? new Dictionary<int, PanelRow?>() : null;
            var branches = new HashSet<string>(StringComparer.Ordinal);
            var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var branchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var numericValues = new Dictionary<(double, string), ValueCount>();
            var stringValues = new Dictionary<PanelText, ValueCount>(new PanelTextComparer());
            var uniqueValues = new List<ValueCount>();
            var scalarCount = 0;
            var warnings = new PanelDataWarnings();
            var lengths = new Dictionary<int, int>();
            for (int i = 0; i < snapshot.Branches.Count; i++)
            {
                if (work.ShouldYield()) yield return null;
                CheckCancellation(i, cancellationToken);
                int length = snapshot.Branches[i].ItemCount;
                lengths.TryGetValue(length, out int frequency);
                lengths[length] = frequency + 1;
            }
            foreach (var entry in lengths)
            {
                if (work.ShouldYield()) yield return null;
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot.Branches.Count >= 4 && (long)entry.Value * 4 >= (long)snapshot.Branches.Count * 3 && entry.Value < snapshot.Branches.Count)
                { warnings.MajorityLength = entry.Key; warnings.OutlierBranches = snapshot.Branches.Count - entry.Value; }
            }

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

            string? numericUnits = null;
            var numericCount = 0;
            var nullCount = 0;
            var invalidCount = 0;
            var unverifiedCount = 0;
            var filteredSourceStatuses = default(PanelSourceStatusCounts);
            var minimum = double.PositiveInfinity;
            var maximum = double.NegativeInfinity;
            var mean = 0.0;
            for (var i = 0; i < snapshot.Rows.Count; i++)
            {
                if (work.ShouldYield()) yield return null;
                CheckCancellation(i, cancellationToken);
                var row = snapshot.Rows[i];
                if (row.IsNull) warnings.NullCount++;
                else if (row.Validity == PanelValidity.VerifiedInvalid) warnings.InvalidCount++;
                else if (row.Validity == PanelValidity.Unverified) warnings.UnverifiedCount++;
                if (!scope.MatchesRow(row)) continue;
                PanelGeometryInfo? measured = null;
                measuredGeometry?.TryGetValue(i, out measured);
                if (!search.Matches(row, snapshot.UnitConversion, measured, cancellationToken)) continue;
                filteredSourceStatuses = filteredSourceStatuses.Add(row.Validity);
                if (set != null && !set.IncludeRow(i, measured)) continue;

                rowPositions.Add(i);
                if (columnSortRows != null && set == null) columnSortRows.Add(i, query.ColumnSortKeys!.RowAt(i));
                var metric = set == null && query.DisplayFilterValue
                    ? PanelFilterValue.OutputValue(snapshot, row, query.SearchMode, measured, query.DecimalPlaces, query.RoundUpFactor, query.Formula) : null;
                outputValues?.Add(i, metric);
                if (propertySortValues != null) propertySortValues.Add(i, PanelDisplayedValue.Capture(snapshot, row, query, measured));
                if (sortText != null && !row.IsNull)
                    sortText.Add(i, new RowSortKey(row, measured, snapshot.UnitConversion, query));
                branches.Add(row.Path);
                Increment(branchCounts, row.Path);
                if (set?.IsBranchSet == true) continue;
                var projected = set == null ? null : set.Values[i];
                double? numeric = projected?.Number;
                if (projected == null)
                    numeric = query.DisplayFilterValue
                        ? metric?.Kind == PanelPropertyKind.Number ? metric.Number : (double?)null
                        : !row.IsNull && row.IsValid && row.NumericValue.HasValue ? PanelValuePresentation.PresentedNumber(row, query.DecimalPlaces, query.RoundUpFactor, query.Formula) : (double?)null;
                if (numeric.HasValue && (double.IsNaN(numeric.Value) || double.IsInfinity(numeric.Value))) numeric = null;
                PanelText? text = projected != null ? projected.IsNumeric || projected.Unavailable ? (PanelText?)null : projected.SemanticText :
                    row.StringValue != null ? new PanelText(row.StringValue) : (PanelText?)null;
                Increment(typeCounts, projected == null ? row.TypeName : projected.IsNumeric ? "Number" : "Text");
                if (projected == null && row.IsNull) nullCount++;
                if (projected == null && row.Validity == PanelValidity.VerifiedInvalid) invalidCount++;
                if (projected == null && row.Validity == PanelValidity.Unverified) unverifiedCount++;
                if (projected != null || !row.IsNull && row.IsValid)
                {
                    if (numeric.HasValue)
                    {
                        // Numeric equality deliberately unifies integer/number values and signed zero.
                        var number = numeric.Value == 0 ? 0 : numeric.Value;
                        string unitsKey = query.DisplayFilterValue ? PanelFilterValue.UnitSymbol(snapshot, row, query.SearchMode, measured) : PanelUnits.Symbol(snapshot.UnitConversion);
                        int precision = query.DisplayFilterValue && query.SearchMode >= PanelSearchMode.Length && query.SearchMode <= PanelSearchMode.Volume ? 6 : 12;
                        var groupKey = (query.DecimalPlaces < 0 ? PanelNumber.GeneralKey(number, precision) : number, unitsKey);
                        if (!numericValues.TryGetValue(groupKey, out var frequency))
                        {
                            frequency = new ValueCount(number, null) { Units = unitsKey, Precision = precision };
                            numericValues.Add(groupKey, frequency);
                            uniqueValues.Add(frequency);
                        }
                        else if (frequency.Number != number)
                        {
                            if (frequency.DistinctNumbers == null) frequency.DistinctNumbers = new HashSet<double> { frequency.Number!.Value };
                            if (frequency.DistinctNumbers.Add(number)) frequency.DistinctCount++;
                        }
                        frequency.Count++;
                        scalarCount++;
                    }
                    else if (text.HasValue)
                    {
                        if (!stringValues.TryGetValue(text.Value, out var frequency))
                        {
                            frequency = new ValueCount(null, text);
                            stringValues.Add(text.Value, frequency);
                            uniqueValues.Add(frequency);
                        }
                        frequency.Count++;
                        scalarCount++;
                    }
                }
                if (!numeric.HasValue || double.IsNaN(numeric.Value) || double.IsInfinity(numeric.Value)) continue;
                string units = query.DisplayFilterValue ? PanelFilterValue.UnitSymbol(snapshot, row, query.SearchMode, measured) : PanelUnits.Symbol(snapshot.UnitConversion);
                numericUnits = numericUnits == null ? units : numericUnits == units ? units : "";
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
            if (columnSortRows != null && set != null && !set.IsBranchSet)
                foreach (int position in positions)
                {
                    var preview = new List<string>(4);
                    int count = 0, visits = 0;
                    bool hasPartner = false;
                    foreach (var row in query.ColumnSortKeys!.GroupRows(snapshot, set.MembersAt(position)))
                    {
                        if (work.ShouldYield()) yield return null;
                        CheckCancellation(visits++, cancellationToken);
                        hasPartner |= row != null;
                        count++;
                        if (preview.Count < 4) preview.Add(row?.DisplayValue ?? "—");
                    }
                    columnSortRows.Add(position, !hasPartner ? null : new PanelRow(snapshot.Rows[position].OriginalIndex,
                        snapshot.Rows[position].Path, "Text", PanelTableText.Summary(preview, count), null));
                }
            var columnKeys = columnSortRows == null ? null : new PanelColumnSortKeys(columnSortRows);
            if (query.SortMode != PanelSortMode.SourceOrder && set?.IsBranchSet != true)
                foreach (var step in SortBranches(positions, snapshot, branchCounts, sortText!,
                    query.SortMode == PanelSortMode.ValueDescending, cancellationToken, work, set?.Values ?? propertySortValues, columnKeys))
                    yield return step;
            // Build the scroll index as part of the cooperative query, not in a
            // paint or gesture. The first row of the next retained non-empty
            // branch anchors each empty path without changing item numbering.
            var firstRows = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < positions.Length; index++)
            {
                if (work.ShouldYield()) yield return null;
                CheckCancellation(index, cancellationToken);
                string path = snapshot.Rows[positions[index]].Path;
                if (!firstRows.ContainsKey(path)) firstRows.Add(path, index);
            }
            var orderedBranches = new List<string>();
            for (int index = 0; index < snapshot.Branches.Count; index++)
            {
                if (work.ShouldYield()) yield return null;
                CheckCancellation(index, cancellationToken);
                string path = snapshot.Branches[index].Path;
                if (branches.Contains(path)) orderedBranches.Add(path);
            }
            var reverseEmptyPaths = new List<string>();
            var reverseEmptyOffsets = new List<int>();
            int nextRow = positions.Length;
            for (int index = orderedBranches.Count - 1; index >= 0; index--)
            {
                if (work.ShouldYield()) yield return null;
                CheckCancellation(index, cancellationToken);
                string path = orderedBranches[index];
                if (firstRows.TryGetValue(path, out int first)) nextRow = first;
                else { reverseEmptyPaths.Add(path); reverseEmptyOffsets.Add(nextRow); }
            }
            var emptyPaths = new List<string>();
            var emptyOffsets = new List<int>();
            int previousOffset = 0;
            for (int index = reverseEmptyPaths.Count - 1; index >= 0; index--)
            {
                if (work.ShouldYield()) yield return null;
                CheckCancellation(index, cancellationToken);
                // Actual tree rows are branch-ordered. Preserve item order even
                // for a synthetic snapshot with interleaved branch records.
                previousOffset = Math.Max(previousOffset, reverseEmptyOffsets[index]);
                emptyPaths.Add(reverseEmptyPaths[index]);
                emptyOffsets.Add(previousOffset);
            }
            var viewIndex = new PanelViewIndex(positions.Length, emptyOffsets, emptyPaths);
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
                frequencies[index] = new PanelValueFrequency(value.Number, value.Text, value.Count, null, value.DistinctCount, value.Units, value.Precision, query.DecimalPlaces);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var statistics = new PanelStatistics(set?.IsBranchSet == true ? branches.Count : positions.Length, branches.Count, numericCount, nullCount,
                invalidCount, numericCount == 0 ? (double?)null : minimum,
                numericCount == 0 ? (double?)null : maximum, numericCount == 0 ? (double?)null : mean,
                new ReadOnlyDictionary<string, int>(typeCounts), new ReadOnlyDictionary<string, int>(branchCounts),
                Array.AsReadOnly(frequencies), scalarCount, unverifiedCount, filteredSourceStatuses, numericUnits ?? "", query.DisplayFilterValue && query.SearchMode >= PanelSearchMode.Length && query.SearchMode <= PanelSearchMode.Volume ? "G6" : "G12");
            PanelDisplaySetResult? displaySet = null;
            if (set != null)
                foreach (var step in set.Complete(work, cancellationToken))
                    if (step == null) yield return null;
                    else displaySet = step;
            if (work.ShouldYield()) yield return null;
            cancellationToken.ThrowIfCancellationRequested();
            var viewIndices = new PanelResultPositionIndex(snapshot.Rows.Count, positions.Length);
            for (int index = 0; index < positions.Length; index++)
            {
                if (work.ShouldYield()) yield return null;
                CheckCancellation(index, cancellationToken);
                viewIndices.Add(positions[index], index);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = new PanelQueryResult(snapshot, positions, viewIndices, statistics, orderedBranches, branches, viewIndex, displaySet, outputValues, warnings);
            if (query.TableSource?.Extras != null && displaySet == null && !(query.DisplayFilterValue && query.SearchMode == PanelSearchMode.ListLength))
                foreach (var index in PanelTableIndex.Build(result, query.TableSource.Columns,
                    query.TableSource.Extras(result, rowPositions, !HasItemFilter(query)), work, cancellationToken))
                    if (index == null) yield return null;
                    else result.TableIndex = index;
            if (query.TableSource != null && (displaySet != null || query.DisplayFilterValue && query.SearchMode == PanelSearchMode.ListLength))
                foreach (var step in PanelTableSummaries.Build(result, rowPositions, query.TableSource, !HasItemFilter(query), work, cancellationToken))
                    if (step) yield return null;
            yield return result;
        }

        public static bool RequiresGeometryMeasurements(PanelQueryOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (UsesDisplayedText(options)) return true;
            if (PresentationNeedsGeometry(options)) return true;
            bool hasSearch = !(options.DisplayFilterValue && options.SearchMode >= PanelSearchMode.Value) && !string.IsNullOrWhiteSpace(options.Search);
            if (hasSearch && (options.SearchMode == PanelSearchMode.Length ||
                options.SearchMode == PanelSearchMode.Area || options.SearchMode == PanelSearchMode.Volume || PanelProperties.IsProperty(options.SearchMode))) return true;
            // Active scalar comparisons cannot return geometry or need geometry sort keys.
            return options.ColumnSortKeys == null && options.SortMode != PanelSortMode.SourceOrder &&
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
            var sorting = query.ColumnSortKeys == null && query.SortMode != PanelSortMode.SourceOrder;
            bool setGeometry = PresentationNeedsGeometry(query);
            if (!sorting && !setGeometry && (!filter.HasFilter || !filter.IsGeometry && !filter.UsesDisplayText)) return Array.Empty<int>();
            var scope = new PanelQueryScope(snapshot, query, filter);
            var candidates = new List<int>();
            for (int index = 0; index < snapshot.Rows.Count; index++)
            {
                CheckCancellation(index, cancellationToken);
                var row = snapshot.Rows[index];
                bool forSet = setGeometry && (!query.DisplayFilterValue || row.GeometryInfo != null && PanelFilterValue.MatchesGeometry(query.SearchMode, row.GeometryInfo));
                bool propertySummary = PanelProperties.IsProperty(query.SearchMode) && !query.DisplayFilterValue && (sorting || forSet);
                if (!scope.MatchesRow(row) || !filter.CanMeasure(row, sorting || forSet, propertySummary)) continue;
                candidates.Add(index);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return candidates.AsReadOnly();
        }

        private static bool PresentationNeedsGeometry(PanelQueryOptions query) => (query.CreateSet || query.OutputValues && query.DisplayFilterValue) &&
            (!query.DisplayFilterValue || query.SearchMode >= PanelSearchMode.Length && query.SearchMode <= PanelSearchMode.Volume || PanelProperties.IsProperty(query.SearchMode));

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

        private static bool ShowsMetric(PanelSearchMode mode) => PanelProperties.IsProperty(mode) ||
            mode >= PanelSearchMode.Length && mode <= PanelSearchMode.Volume || mode >= PanelSearchMode.X && mode <= PanelSearchMode.Z;

        // Non-scalar rows sort by their measurement when they have one, so 9 precedes 10.
        private readonly struct RowSortKey
        {
            internal RowSortKey(PanelRow row, PanelGeometryInfo? measured, PanelUnitConversion conversion, PanelQueryOptions query)
            {
                var geometry = row.IsValid ? measured ?? row.GeometryInfo : null;
                double scalar = row.IsValid && row.NumericValue.HasValue ? PanelValuePresentation.PresentedNumber(row, query.DecimalPlaces, query.RoundUpFactor, query.Formula) : double.NaN;
                Number = !double.IsNaN(scalar) && !double.IsInfinity(scalar) ? scalar : (double?)null;
                Text = Number.HasValue ? default : PanelValuePresentation.DisplayText(row, conversion, query.DecimalPlaces, measured, query.RoundUpFactor, query.Formula);
                double value = geometry?.Value is double raw && raw >= 0 ? PanelUnits.Transform(PanelUnits.ConvertGeometry(raw, geometry.Power, geometry.Units, conversion), PanelUnitConversion.None, query.RoundUpFactor, query.DecimalPlaces, query.Formula) : double.NaN;
                Metric = !double.IsNaN(value) && !double.IsInfinity(value) ? geometry!.MetricName : null;
                Value = value;
            }
            internal PanelText Text { get; }
            internal double? Number { get; }
            internal string? Metric { get; }
            internal double Value { get; }
        }

        private sealed class ValueCount
        {
            internal HashSet<double>? DistinctNumbers;
            internal int DistinctCount = 1;
            internal string Units = "";
            internal int Precision = 12;
            internal ValueCount(double? number, PanelText? text) { Number = number; Text = text; }
            internal double? Number { get; }
            internal PanelText? Text { get; }
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
            IReadOnlyDictionary<string, int> branchCounts, IReadOnlyDictionary<int, RowSortKey> sortText,
            bool descending, CancellationToken token, PanelQueryWork work, IReadOnlyDictionary<int, PanelDisplayedValue>? displayedValues = null, PanelColumnSortKeys? columnKeys = null)
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
                    snapshot.Rows, sortText, descending, token, work, displayedValues, columnKeys)) yield return step;
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
            IReadOnlyDictionary<int, RowSortKey> sortText, bool descending,
            CancellationToken token, PanelQueryWork work, IReadOnlyDictionary<int, PanelDisplayedValue>? displayedValues, PanelColumnSortKeys? columnKeys)
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
                            Compare(source[left], source[right], rows, sortText, descending, displayedValues, columnKeys) <= 0)
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
            IReadOnlyDictionary<int, RowSortKey> sortText, bool descending, IReadOnlyDictionary<int, PanelDisplayedValue>? displayedValues, PanelColumnSortKeys? columnKeys)
        {
            if (columnKeys != null) return columnKeys.Compare(leftId, rightId, descending);
            if (displayedValues != null)
            {
                var leftValue = displayedValues[leftId];
                var rightValue = displayedValues[rightId];
                if (leftValue.Unavailable != rightValue.Unavailable) return leftValue.Unavailable ? 1 : -1;
                if (leftValue.Number.HasValue != rightValue.Number.HasValue) return leftValue.Number.HasValue ? -1 : 1;
                int order = leftValue.Number.HasValue ? leftValue.Number.Value.CompareTo(rightValue.Number!.Value) :
                    leftValue.SemanticText.Compare(rightValue.SemanticText, StringComparison.OrdinalIgnoreCase);
                return order == 0 ? leftId.CompareTo(rightId) : descending ? -order : order;
            }
            var left = rows[leftId];
            var right = rows[rightId];
            if (left.IsNull != right.IsNull) return left.IsNull ? 1 : -1;
            if (left.IsNull) return leftId.CompareTo(rightId);
            RowSortKey leftKey = sortText[leftId], rightKey = sortText[rightId];
            var leftNumeric = leftKey.Number.HasValue;
            var rightNumeric = rightKey.Number.HasValue;
            if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
            int comparison;
            if (leftNumeric) comparison = leftKey.Number!.Value.CompareTo(rightKey.Number!.Value);
            else
            {
                comparison = StringComparer.OrdinalIgnoreCase.Compare(left.TypeName, right.TypeName);
                if (comparison != 0) return comparison;
                if ((leftKey.Metric == null) != (rightKey.Metric == null)) return leftKey.Metric != null ? -1 : 1;
                comparison = leftKey.Metric == null ? leftKey.Text.Compare(rightKey.Text, StringComparison.OrdinalIgnoreCase)
                    : StringComparer.OrdinalIgnoreCase.Compare(leftKey.Metric, rightKey.Metric);
                if (comparison == 0 && leftKey.Metric != null) comparison = leftKey.Value.CompareTo(rightKey.Value);
            }
            if (comparison != 0) return descending ? -comparison : comparison;
            return leftId.CompareTo(rightId);
        }
    }
}
