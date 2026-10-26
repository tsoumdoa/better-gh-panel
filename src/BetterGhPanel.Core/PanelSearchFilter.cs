using System;
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
        private readonly string _text;
        private readonly Func<double, bool>? _comparison;
        private readonly Regex? _regex;
        private readonly Stopwatch? _regexBudget;
        private readonly int _decimals;
        private readonly IFormatProvider _culture;

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
            if (_mode == PanelSearchMode.Regex)
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
        internal bool IsGeometry => _mode >= PanelSearchMode.Length && _mode <= PanelSearchMode.Volume;
        internal bool IsBranchFilter => _mode == PanelSearchMode.ListLength;
        internal bool UsesDisplayText => HasFilter && !IsBranchFilter && (_regex != null || _comparison == null);
        internal static bool IsNumericComparison(string text) => ParseComparison(text.Trim(), false) != null;

        internal bool MatchesBranchCount(int itemCount) => !HasFilter || !IsBranchFilter || _comparison!(itemCount);

        internal bool CanMeasure(PanelRow row, bool forSorting = false)
        {
            var geometry = row.GeometryInfo;
            if (!row.IsValid || row.IsNull || geometry?.CanMeasure != true) return false;
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
            if (_regex != null)
            {
                token.ThrowIfCancellationRequested();
                if (_regexBudget!.ElapsedMilliseconds >= 2000) throw RegexTooSlow();
                try { return _regex.IsMatch(PanelValuePresentation.FormatDisplay(row, conversion, _decimals, measured, _culture)); }
                catch (RegexMatchTimeoutException) { throw RegexTooSlow(); }
            }
            if (_comparison == null)
                return Contains(PanelValuePresentation.FormatDisplay(row, conversion, _decimals, measured, _culture), _text) ||
                    Contains(row.Path, _text) || Contains(row.TypeName, _text);
            if (!IsGeometry)
                return !row.IsNull && row.IsValid && row.NumericValue.HasValue && IsFinite(row.NumericValue.Value) && _comparison(row.NumericValue.Value);
            return PanelFilterValue.TryGeometryValue(row, _mode, conversion, measured, out double value) && _comparison(value);
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
