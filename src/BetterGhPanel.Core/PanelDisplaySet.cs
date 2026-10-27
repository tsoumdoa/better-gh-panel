using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;

namespace BetterGhPanel.Core
{
    /// <summary>A frozen displayed value. Numeric values carry the displayed precision.</summary>
    public sealed class PanelDisplayedValue
    {
        internal PanelDisplayedValue(string text, string copyText, double? number)
        { Text = text; CopyText = copyText; Number = number; }
        public string Text { get; }
        public string CopyText { get; }
        public double? Number { get; }

        internal static PanelDisplayedValue Capture(PanelSnapshot snapshot, PanelRow row, PanelQueryOptions query,
            PanelGeometryInfo? measured, IFormatProvider culture)
        {
            string text, copy;
            bool numeric;
            if (query.DisplayFilterValue)
            {
                text = PanelFilterValue.Format(snapshot, row, query.SearchMode, query.DecimalPlaces, measured);
                copy = PanelFilterValue.Format(snapshot, row, query.SearchMode, query.DecimalPlaces, measured, includeUnits: false);
                numeric = true; // Status labels fail the finite-number parse below.
            }
            else
            {
                text = PanelValuePresentation.FormatDisplay(row, snapshot.UnitConversion, query.DecimalPlaces, measured, culture);
                numeric = !row.IsNull && row.IsValid && row.NumericValue.HasValue;
                copy = numeric ? PanelUnits.Format(row.NumericValue!.Value, PanelUnitConversion.None,
                    query.DecimalPlaces, CultureInfo.InvariantCulture) : text;
            }
            double? number = numeric && double.TryParse(copy, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
                !double.IsNaN(parsed) && !double.IsInfinity(parsed) ? parsed : (double?)null;
            return new PanelDisplayedValue(text, copy, number);
        }
    }

    public sealed class PanelDisplaySetResult
    {
        internal PanelDisplaySetResult(Dictionary<int, PanelDisplayedValue> values, Dictionary<int, PanelSetOccurrences> occurrences,
            Dictionary<string, IReadOnlyList<string>> branchOccurrences, Dictionary<string, PanelSourceStatusCounts> branchSourceStatuses, bool isBranchSet)
        {
            IsBranchSet = isBranchSet;
            Values = new ReadOnlyDictionary<int, PanelDisplayedValue>(values);
            Occurrences = new ReadOnlyDictionary<int, PanelSetOccurrences>(occurrences);
            BranchOccurrences = new ReadOnlyDictionary<string, IReadOnlyList<string>>(branchOccurrences);
            BranchSourceStatuses = new ReadOnlyDictionary<string, PanelSourceStatusCounts>(branchSourceStatuses);
        }
        public IReadOnlyDictionary<int, PanelDisplayedValue> Values { get; }
        public bool IsBranchSet { get; }
        public IReadOnlyDictionary<int, PanelSetOccurrences> Occurrences { get; }
        /// <summary>One list-length occurrence per matching branch, including empty branches.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> BranchOccurrences { get; }
        /// <summary>Source statuses across all filtered equal-length branches, keyed by representative path.</summary>
        public IReadOnlyDictionary<string, PanelSourceStatusCounts> BranchSourceStatuses { get; }
    }

    // Consumes only rows/branches that already passed the current filters.
    internal sealed class PanelDisplaySet
    {
        private readonly PanelSnapshot _snapshot;
        private readonly PanelQueryOptions _query;
        private readonly IFormatProvider _culture;
        private readonly Dictionary<string, Dictionary<string, int>> _seen = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        private readonly Dictionary<int, PanelDisplayedValue> _values = new Dictionary<int, PanelDisplayedValue>();
        private readonly Dictionary<int, List<PanelSourceAddress>> _locations = new Dictionary<int, List<PanelSourceAddress>>();
        private readonly Dictionary<int, PanelSourceStatusCounts> _statuses = new Dictionary<int, PanelSourceStatusCounts>();
        private readonly Dictionary<int, string> _lengths = new Dictionary<int, string>();
        private readonly Dictionary<string, string> _branchRepresentatives = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _branchLocations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, PanelSourceStatusCounts> _branchStatuses = new Dictionary<string, PanelSourceStatusCounts>(StringComparer.Ordinal);

        internal PanelDisplaySet(PanelSnapshot snapshot, PanelQueryOptions query, IFormatProvider culture)
        { _snapshot = snapshot; _query = query; _culture = culture; }
        internal bool IsBranchSet => _query.DisplayFilterValue && _query.SearchMode == PanelSearchMode.ListLength;
        internal IReadOnlyDictionary<int, PanelDisplayedValue> Values => _values;

        internal bool IncludeBranch(string path)
        {
            if (!IsBranchSet) return true;
            if (_branchRepresentatives.TryGetValue(path, out string representative)) return representative == path;
            int count = _snapshot.BranchItemCounts[path];
            if (!_lengths.TryGetValue(count, out representative))
            {
                representative = path;
                _lengths.Add(count, path);
                _branchLocations.Add(path, new List<string>());
                _branchStatuses.Add(path, default);
            }
            _branchRepresentatives.Add(path, representative);
            _branchLocations[representative].Add(path);
            return representative == path;
        }

        internal bool IncludeRow(int position, PanelGeometryInfo? measured)
        {
            var row = _snapshot.Rows[position];
            if (IsBranchSet)
            {
                bool included = IncludeBranch(row.Path);
                string representative = _branchRepresentatives[row.Path];
                _branchStatuses[representative] = _branchStatuses[representative].Add(row.Validity);
                return included;
            }
            var value = PanelDisplayedValue.Capture(_snapshot, row, _query, measured, _culture);
            if (!_seen.TryGetValue(row.Path, out var branch))
            {
                branch = new Dictionary<string, int>(StringComparer.Ordinal);
                _seen.Add(row.Path, branch);
            }
            var address = _snapshot.SourceAddressAt(position);
            if (branch.TryGetValue(value.Text, out int first))
            {
                _locations[first].Add(address);
                _statuses[first] = _statuses[first].Add(row.Validity);
                return false;
            }
            branch.Add(value.Text, position);
            _values.Add(position, value);
            _locations.Add(position, new List<PanelSourceAddress> { address });
            _statuses.Add(position, default(PanelSourceStatusCounts).Add(row.Validity));
            return true;
        }

        internal IEnumerable<PanelDisplaySetResult?> Complete(PanelQueryWork work, CancellationToken token)
        {
            var occurrences = new Dictionary<int, PanelSetOccurrences>();
            foreach (var pair in _locations)
            {
                if (work.ShouldYield()) yield return null;
                token.ThrowIfCancellationRequested();
                occurrences.Add(pair.Key, new PanelSetOccurrences(pair.Value, _statuses[pair.Key]));
            }
            var branchOccurrences = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var pair in _branchLocations)
            {
                if (work.ShouldYield()) yield return null;
                token.ThrowIfCancellationRequested();
                branchOccurrences.Add(pair.Key, pair.Value.AsReadOnly());
            }
            yield return new PanelDisplaySetResult(_values, occurrences, branchOccurrences, _branchStatuses, IsBranchSet);
        }
    }
}
