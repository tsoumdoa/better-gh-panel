using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;

namespace BetterGhPanel.Core
{
    // Prepared once per scan; only immutable snapshot data enters this predicate.
    internal sealed class PanelSearchFilter
    {
        private readonly PanelSearchMode _mode;
        private readonly PanelPropertyFilter? _propertyFilter;
        private readonly string _text;
        private readonly Func<double, bool>? _comparison;
        private readonly Regex? _regex;
        private readonly Stopwatch? _regexBudget;
        private readonly int _decimals;
        private readonly IFormatProvider _culture;
        private readonly Dictionary<PanelGroupKey, bool> _groupMatches = new Dictionary<PanelGroupKey, bool>();
        private readonly Queue<PanelGroupKey> _groupMatchOrder = new Queue<PanelGroupKey>();
        private const int MaxCachedGroupMatches = 16384;
        internal int GroupEvaluationCount { get; private set; }

        internal PanelSearchFilter(PanelQueryOptions options, IFormatProvider? culture = null)
        {
            _mode = options.SearchMode;
            _decimals = options.DecimalPlaces;
            _culture = culture ?? CultureInfo.CurrentCulture;
            if (!Enum.IsDefined(typeof(PanelSearchMode), _mode)) throw new ArgumentException("Unknown search mode.");
            _text = options.DisplayFilterValue && _mode >= PanelSearchMode.Value ? string.Empty : options.Search ?? string.Empty;
            // Spaces in a regular expression are significant.
            if (_mode != PanelSearchMode.Regex) _text = _text.Trim();
            if (_text.Length == 0) return;
            if (PanelProperties.IsProperty(_mode))
            {
                _propertyFilter = new PanelPropertyFilter(_mode, _text);
            }
            else if (_mode == PanelSearchMode.Regex)
            {
                try { _regex = new Regex(_text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException) { throw new ArgumentException("Invalid regular expression. Check the pattern and try again."); }
                _regexBudget = Stopwatch.StartNew();
            }
            else
            {
                _comparison = ParseComparison(_text, _mode != PanelSearchMode.Text);
                if (_comparison == null && _mode != PanelSearchMode.Text)
                    throw new ArgumentException("Enter a finite number with >, >=, <, <=, = or != (for example, > 25).");
            }
        }

        internal bool HasFilter => _text.Length != 0;
        internal bool IsGeometry => (_mode >= PanelSearchMode.Length && _mode <= PanelSearchMode.Volume) || PanelProperties.IsProperty(_mode);
        internal bool IsBranchFilter => _mode == PanelSearchMode.ListLength;
        internal bool UsesDisplayText => HasFilter && !IsBranchFilter && !PanelProperties.IsProperty(_mode) && (_regex != null || _comparison == null);
        internal static bool IsNumericComparison(string text) => ParseComparison(text.Trim(), false) != null;

        internal bool MatchesBranchCount(int itemCount) => !HasFilter || !IsBranchFilter || _comparison!(itemCount);

        internal bool CanMeasure(PanelRow row, bool forSorting = false, bool forPresentation = false)
        {
            var geometry = row.GeometryInfo;
            if (!row.IsValid || row.IsNull || geometry?.CanMeasure != true) return false;
            if (forPresentation) return true;
            if (PanelProperties.IsProperty(_mode))
                return geometry.Properties.TryGetValue(_mode, out var property) && property == null;
            if (HasFilter)
            {
                if (UsesDisplayText) return true;
                if (IsGeometry) return PanelFilterValue.MatchesGeometry(_mode, geometry);
                // Scalar comparisons already have their exact numeric value; geometry
                // cannot affect membership or numeric sorting for this predicate.
                if (!IsBranchFilter) return false;
            }
            return forSorting;
        }

        internal bool Matches(PanelRow row, PanelUnitConversion conversion, PanelGeometryInfo? measured, CancellationToken token)
        {
            if (!HasFilter || IsBranchFilter) return true;
            if (_propertyFilter != null)
                return PanelProperties.TryGet(row, _mode, measured, out var property) && property != null && _propertyFilter.Matches(property, conversion);
            if (_regex != null)
            {
                token.ThrowIfCancellationRequested();
                if (_regexBudget!.ElapsedMilliseconds >= 2000) throw RegexTooSlow();
                var value = PanelValuePresentation.DisplayText(row, conversion, _decimals, measured, _culture);
                if (TryGroupMatch(value, out bool cached)) return cached;
                try
                {
                    bool match = _regex.IsMatch(value.ToString());
                    RememberGroupMatch(value, match);
                    return match;
                }
                catch (RegexMatchTimeoutException) { throw RegexTooSlow(); }
            }
            if (_comparison == null)
            {
                var value = PanelValuePresentation.DisplayText(row, conversion, _decimals, measured, _culture);
                bool matches;
                if (TryGroupMatch(value, out bool cached)) matches = cached;
                else { matches = value.Contains(_text); RememberGroupMatch(value, matches); }
                return matches ||
                    Contains(row.Path, _text) || Contains(row.TypeName, _text);
            }
            if (!IsGeometry)
                return PanelFilterValue.TryScalarValue(row, _mode, conversion, out double scalarValue) && _comparison(scalarValue);
            return PanelFilterValue.TryGeometryValue(row, _mode, conversion, measured, out double geometryValue) && _comparison(geometryValue);
        }

        private void RememberGroupMatch(PanelText value, bool match)
        {
            var key = value.GroupKey;
            if (!key.HasValue) return;
            GroupEvaluationCount++;
            if (_groupMatches.Count == MaxCachedGroupMatches) _groupMatches.Remove(_groupMatchOrder.Dequeue());
            _groupMatches.Add(key.Value, match);
            _groupMatchOrder.Enqueue(key.Value);
        }

        private bool TryGroupMatch(PanelText value, out bool match)
        {
            var key = value.GroupKey;
            if (key.HasValue && _groupMatches.TryGetValue(key.Value, out match)) return true;
            match = false;
            return false;
        }

        private static ArgumentException RegexTooSlow() => new ArgumentException("Regular expression took too long. Simplify the pattern and try again.");
        private static bool Contains(string value, string search) => value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static Func<double, bool>? ParseComparison(string text, bool implicitEquality)
        {
            var operatorLength = text.StartsWith(">=", StringComparison.Ordinal) || text.StartsWith("<=", StringComparison.Ordinal) ||
                text.StartsWith("==", StringComparison.Ordinal) || text.StartsWith("!=", StringComparison.Ordinal) ? 2 :
                text.StartsWith(">", StringComparison.Ordinal) || text.StartsWith("<", StringComparison.Ordinal) ||
                text.StartsWith("=", StringComparison.Ordinal) ? 1 : 0;
            if (operatorLength == 0 && !implicitEquality ||
                !double.TryParse(text.Substring(operatorLength).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) ||
                !IsFinite(threshold)) return null;
            switch (text.Substring(0, operatorLength))
            {
                case ">": return value => value > threshold;
                case ">=": return value => value >= threshold;
                case "<": return value => value < threshold;
                case "<=": return value => value <= threshold;
                case "!=": return value => value != threshold;
                default: return value => value == threshold;
            }
        }
    }
}
