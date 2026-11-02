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
        private readonly PanelText _text, _copyText;
        internal PanelDisplayedValue(PanelText text, PanelText copyText, double? number)
        { _text = text; _copyText = copyText; Number = number; }
        public string Text => _text.ToString();
        public string CopyText => _copyText.ToString();
        public double? Number { get; }
        internal PanelText SemanticText => _text;

        internal static PanelDisplayedValue Capture(PanelSnapshot snapshot, PanelRow row, PanelQueryOptions query,
            PanelGeometryInfo? measured, IFormatProvider culture)
        {
            PanelText text, copy;
            bool numeric;
            if (query.DisplayFilterValue)
            {
                text = new PanelText(PanelFilterValue.Format(snapshot, row, query.SearchMode, query.DecimalPlaces, measured));
                copy = new PanelText(PanelFilterValue.Format(snapshot, row, query.SearchMode, query.DecimalPlaces, measured, includeUnits: false));
                numeric = !PanelProperties.IsProperty(query.SearchMode) || PanelProperties.Kind(query.SearchMode) == PanelPropertyKind.Number;
            }
            else
            {
                text = PanelValuePresentation.DisplayText(row, snapshot.UnitConversion, query.DecimalPlaces, measured, culture);
                numeric = !row.IsNull && row.IsValid && row.NumericValue.HasValue;
                copy = numeric ? new PanelText(PanelUnits.Format(row.NumericValue!.Value, PanelUnitConversion.None,
                    query.DecimalPlaces, CultureInfo.InvariantCulture)) : text;
            }
            double? number = numeric && double.TryParse(copy.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
                !double.IsNaN(parsed) && !double.IsInfinity(parsed) ? parsed : (double?)null;
            return new PanelDisplayedValue(text, copy, number);
        }
    }

    public sealed class PanelDisplaySetResult
    {
        internal PanelDisplaySetResult(Dictionary<int, PanelDisplayedValue> values, Dictionary<int, PanelSetOccurrences> occurrences,
            Dictionary<string, IReadOnlyList<string>> branchOccurrences, Dictionary<string, PanelSourceStatusCounts> branchSourceStatuses, bool isBranchSet,
            Dictionary<int, IReadOnlyList<int>> members, Dictionary<int, int> indices,
            Dictionary<string, IReadOnlyList<int>> branchMembers, Dictionary<string, int> branchIndices,
            Dictionary<int, int> memberIndices, List<int> allMembers)
        {
            IsBranchSet = isBranchSet;
            Values = new ReadOnlyDictionary<int, PanelDisplayedValue>(values);
            Occurrences = new ReadOnlyDictionary<int, PanelSetOccurrences>(occurrences);
            BranchOccurrences = new ReadOnlyDictionary<string, IReadOnlyList<string>>(branchOccurrences);
            BranchSourceStatuses = new ReadOnlyDictionary<string, PanelSourceStatusCounts>(branchSourceStatuses);
            Members = new ReadOnlyDictionary<int, IReadOnlyList<int>>(members);
            Indices = new ReadOnlyDictionary<int, int>(indices);
            BranchMembers = new ReadOnlyDictionary<string, IReadOnlyList<int>>(branchMembers);
            BranchIndices = new ReadOnlyDictionary<string, int>(branchIndices);
            MemberIndices = new ReadOnlyDictionary<int, int>(memberIndices);
            AllMembers = allMembers.AsReadOnly();
        }
        public IReadOnlyDictionary<int, PanelDisplayedValue> Values { get; }
        public IReadOnlyDictionary<int, IReadOnlyList<int>> Members { get; }
        public IReadOnlyDictionary<int, int> Indices { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<int>> BranchMembers { get; }
        public IReadOnlyDictionary<string, int> BranchIndices { get; }
        public IReadOnlyDictionary<int, int> MemberIndices { get; }
        public IReadOnlyList<int> AllMembers { get; }

        public IReadOnlyList<int> ExpandMembers(PanelSnapshot snapshot, IReadOnlyList<int> positions, IReadOnlyList<string>? branches = null)
        {
            var expanded = new List<int>();
            if (IsBranchSet)
            {
                var paths = branches == null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(branches, StringComparer.Ordinal);
                if (branches == null)
                    foreach (int position in positions)
                        paths.Add(snapshot.Rows[position].Path);
                foreach (string path in paths) expanded.AddRange(BranchMembers[path]);
            }
            else foreach (int position in positions) expanded.AddRange(Members[position]);
            // Preserve selected group order and each group's tree-order members;
            // expansion visits only selected members, without a whole-tree scan.
            return expanded.AsReadOnly();
        }

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
        private readonly PanelTextComparer _textComparer = new PanelTextComparer();
        private readonly Dictionary<string, Dictionary<PanelText, int>> _seen = new Dictionary<string, Dictionary<PanelText, int>>(StringComparer.Ordinal);
        private readonly Dictionary<int, PanelDisplayedValue> _values = new Dictionary<int, PanelDisplayedValue>();
        private readonly Dictionary<int, List<PanelSourceAddress>> _locations = new Dictionary<int, List<PanelSourceAddress>>();
        private readonly Dictionary<int, List<int>> _members = new Dictionary<int, List<int>>();
        private readonly Dictionary<int, int> _indices = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _memberIndices = new Dictionary<int, int>();
        private readonly List<int> _allMembers = new List<int>();
        private readonly Dictionary<string, List<int>> _branchMembers = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _branchIndices = new Dictionary<string, int>(StringComparer.Ordinal);
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
                _branchMembers.Add(path, new List<int>());
                _branchIndices.Add(path, _branchIndices.Count);
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
                _branchMembers[representative].Add(position);
                _memberIndices.Add(position, _branchIndices[representative]);
                _allMembers.Add(position);
                return included;
            }
            var value = PanelDisplayedValue.Capture(_snapshot, row, _query, measured, _culture);
            if (!_seen.TryGetValue(row.Path, out var branch))
            {
                branch = new Dictionary<PanelText, int>(_textComparer);
                _seen.Add(row.Path, branch);
            }
            var address = _snapshot.SourceAddressAt(position);
            _allMembers.Add(position);
            if (branch.TryGetValue(value.SemanticText, out int first))
            {
                _locations[first].Add(address);
                _members[first].Add(position);
                _memberIndices.Add(position, _indices[first]);
                _statuses[first] = _statuses[first].Add(row.Validity);
                return false;
            }
            _indices.Add(position, branch.Count);
            _memberIndices.Add(position, branch.Count);
            branch.Add(value.SemanticText, position);
            _values.Add(position, value);
            _members.Add(position, new List<int> { position });
            _locations.Add(position, new List<PanelSourceAddress> { address });
            _statuses.Add(position, default(PanelSourceStatusCounts).Add(row.Validity));
            return true;
        }

        internal IEnumerable<PanelDisplaySetResult?> Complete(PanelQueryWork work, CancellationToken token)
        {
            var occurrences = new Dictionary<int, PanelSetOccurrences>();
            var members = new Dictionary<int, IReadOnlyList<int>>();
            foreach (var pair in _locations)
            {
                if (work.ShouldYield()) yield return null;
                token.ThrowIfCancellationRequested();
                occurrences.Add(pair.Key, new PanelSetOccurrences(pair.Value, _statuses[pair.Key]));
                members.Add(pair.Key, _members[pair.Key].AsReadOnly());
            }
            var branchOccurrences = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var branchMembers = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
            foreach (var pair in _branchLocations)
            {
                if (work.ShouldYield()) yield return null;
                token.ThrowIfCancellationRequested();
                branchOccurrences.Add(pair.Key, pair.Value.AsReadOnly());
                branchMembers.Add(pair.Key, _branchMembers[pair.Key].AsReadOnly());
            }
            yield return new PanelDisplaySetResult(_values, occurrences, branchOccurrences, _branchStatuses, IsBranchSet,
                members, _indices, branchMembers, _branchIndices, _memberIndices, _allMembers);
        }
    }
}
