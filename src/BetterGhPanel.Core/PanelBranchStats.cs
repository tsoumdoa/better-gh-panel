using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BetterGhPanel.Core
{
    [Flags]
    public enum PanelStatKinds { Count = 1, Sum = 2, Avg = 4, Median = 8, Min = 16, Max = 32, Range = 64, StdDev = 128 }

    /// <summary>Statistics of filtered shown numbers. Only Median retains input values.</summary>
    public sealed class PanelBranchStats
    {
        public const PanelStatKinds Default = PanelStatKinds.Sum | PanelStatKinds.Avg | PanelStatKinds.Min | PanelStatKinds.Max;
        public const PanelStatKinds All = (PanelStatKinds)255;
        private readonly PanelStatKinds _picks;
        private List<double>? _median;
        private int _count, _kinds, _precision = 6, _min = -1, _max = -1;
        private int _nulls, _invalid, _unverified, _notNumbers, _unavailable, _nonfinite;
        private double _sum, _mean, _deviation, _medianValue;
        private double _minimum = double.PositiveInfinity, _maximum = double.NegativeInfinity;
        private string _units = "";
        private bool _hasUnits;
        internal PanelBranchStats(PanelStatKinds picks = Default) => _picks = Normalize(picks);
        private bool Wants(PanelStatKinds kind) => (_picks & kind) != 0;
        public bool Measuring { get; private set; }
        public int Skipped => _nulls + _invalid + _unverified + _notNumbers + _unavailable + _nonfinite;
        public bool Mixed => (_kinds & (_kinds - 1)) != 0;
        private static readonly string[] Kinds = { "N", "L", "A", "V", "Angle" };
        private IEnumerable<string> KindNames => Kinds.Where((_, index) => (_kinds & (1 << index)) != 0);
        public string MixedLabel => "mixed " + string.Join(" / ", KindNames);
        public string Note => Mixed ? MixedLabel : Skipped > 0 ? Skipped + " skipped" : "";
        public string HeaderSuffix => Mixed || !_hasUnits ? "" : (_kinds == 1 ? "" : " · " + string.Join(" / ", KindNames)) + (_units.Length > 0 ? " · " + _units : "");
        internal static string CommonHeaderSuffix(IEnumerable<PanelBranchStats> branches)
        {
            string? suffix = null;
            foreach (var branch in branches)
            {
                if (!branch._hasUnits || branch.Mixed) continue;
                if (suffix == null) suffix = branch.HeaderSuffix;
                else if (suffix != branch.HeaderSuffix) return "";
            }
            return suffix ?? "";
        }
        public string Details => Skipped == 0 && !Mixed ? "Stats use the shown input numbers." : Mixed ? "Rows show different kinds of numbers. Choose one numeric Search mode, such as Length, Area or Volume, to show one kind." :
            string.Join(", ", SkipReasons()) + ": not numbers; left out of the stats.";
        private IEnumerable<string> SkipReasons()
        {
            if (_nulls > 0) yield return _nulls + " null";
            if (_invalid > 0) yield return _invalid + " invalid";
            if (_unverified > 0) yield return _unverified + " unverified";
            if (_notNumbers > 0) yield return _notNumbers + " not numbers";
            if (_unavailable > 0) yield return _unavailable + " unavailable measurements";
            if (_nonfinite > 0) yield return _nonfinite + " non-finite values";
        }
        public static PanelStatKinds Normalize(PanelStatKinds picks) => (picks & All) == 0 ? Default : picks & All;
        public static IEnumerable<PanelStatKinds> Picks(PanelStatKinds picks)
        {
            int mask = (int)Normalize(picks);
            for (int bit = 1; bit <= 128; bit <<= 1) if ((mask & bit) != 0) yield return (PanelStatKinds)bit;
        }
        public static string Name(PanelStatKinds kind) => kind == PanelStatKinds.StdDev ? "Std dev" : kind.ToString();
        public double? Value(PanelStatKinds kind)
        {
            if (kind == PanelStatKinds.Count) return Mixed ? 0 : _count;
            if (!Wants(kind) || Mixed || Measuring || _count == 0) return null;
            double value = kind switch {
                PanelStatKinds.Sum => _sum, PanelStatKinds.Avg => _mean, PanelStatKinds.Median => _medianValue,
                PanelStatKinds.Min => _minimum, PanelStatKinds.Max => _maximum, PanelStatKinds.Range => _range,
                PanelStatKinds.StdDev => _deviation, _ => double.NaN };
            return Finite(value) ? value : (double?)null;
        }
        public int Source(PanelStatKinds kind) => Mixed || Measuring || !Wants(kind) ? -1 : kind == PanelStatKinds.Min ? _min : kind == PanelStatKinds.Max ? _max : -1;
        public IEnumerable<(PanelStatKinds Kind, double? Number)> Output(PanelStatKinds picks, ISet<int>? selection = null)
        {
            if (Measuring) yield break;
            foreach (var kind in Picks(picks))
                if (selection == null || Source(kind) >= 0 && selection.Contains(Source(kind))) yield return (kind, Value(kind));
        }
        public string Format(PanelStatKinds kind, int decimals, bool includeUnits = true) => Measuring ? "measuring…" :
            !Value(kind).HasValue ? "—" : kind == PanelStatKinds.Count ? Value(kind)!.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) :
            PanelUnits.Format(Value(kind)!.Value, PanelUnitConversion.None, decimals, _precision == 6 ? "G6" : "G12") + (includeUnits && _units.Length > 0 ? " " + _units : "");

        // Columns show their own primary values, with D's units/rounding and no D formula.
        internal static IReadOnlyDictionary<string, PanelBranchStats> Column(PanelQueryResult data, PanelSnapshot snapshot,
            Func<int, int?> partner, PanelQueryOptions options, IReadOnlyDictionary<int, PanelGeometryInfo?>? measurements, CancellationToken token)
        {
            var query = options.Clone(); query.Formula = null; query.SearchMode = PanelSearchMode.Text; query.DisplayFilterValue = false;
            var stats = data.BranchPaths.ToDictionary(path => path, path => new PanelBranchStats(query.StatKinds), StringComparer.Ordinal);
            foreach (int position in data.RowPositions)
            {
                token.ThrowIfCancellationRequested();
                if (partner(position) is not int item) continue;
                var row = snapshot.Rows[item];
                PanelGeometryInfo? measured = null;
                if (measurements?.TryGetValue(item, out measured) == true && measured == null) measured = row.GeometryInfo?.WithUnavailableMeasurement();
                stats[data.Snapshot.Rows[position].Path].Add(snapshot, row, item, query, measured, data.Snapshot.UnitConversion);
            }
            foreach (var branch in stats.Values) branch.Complete(query, token);
            return stats;
        }
        internal static IReadOnlyList<int> ColumnItems(PanelQueryResult data, Func<int, int?> partner) =>
            data.RowPositions.Select(partner).Where(item => item.HasValue).Select(item => item!.Value).Distinct().ToArray();

        // Rows show the Search value only with Show values; otherwise they show each row's main metric.
        internal static bool UsesMainMetric(PanelQueryOptions query) => !query.DisplayFilterValue || query.SearchMode == PanelSearchMode.Text || query.SearchMode == PanelSearchMode.Regex;
        internal static bool NeedsMeasurement(PanelRow row, PanelQueryOptions query)
        {
            var geometry = row.GeometryInfo;
            if (row.IsNull || !row.IsValid || geometry?.CanMeasure != true || geometry.MeasurementUnavailable) return false;
            if (UsesMainMetric(query)) return !row.NumericValue.HasValue && geometry.MetricName.Length > 0 && !geometry.Value.HasValue;
            var mode = query.SearchMode;
            if (PanelProperties.IsProperty(mode)) return PanelProperties.Kind(mode) == PanelPropertyKind.Number &&
                PanelProperties.TryGet(row, mode, null, out var property) && property == null;
            return PanelFilterValue.MatchesGeometry(mode, geometry) && !(mode == PanelSearchMode.Area && geometry.HasArea ? geometry.AreaValue : geometry.Value).HasValue;
        }

        internal void Add(PanelSnapshot snapshot, PanelRow row, int position, PanelQueryOptions query, PanelGeometryInfo? measured,
            PanelUnitConversion? columnConversion = null)
        {
            if (row.IsNull) { _nulls++; return; }
            if (!row.IsValid) { if (row.Validity == PanelValidity.Unverified) _unverified++; else _invalid++; return; }
            double number; string unit; int power, precision;
            var geometry = measured ?? row.GeometryInfo;
            if (UsesMainMetric(query))
            {
                if (!row.NumericValue.HasValue && (geometry == null || geometry.MetricName.Length == 0)) { _notNumbers++; return; }
                if (!PanelFilterValue.TryFormulaInput(snapshot, row, query, measured, out number, out unit, columnConversion))
                {
                    if (NeedsMeasurement(row, query) && measured == null) Measuring = true;
                    else if (geometry?.MetricName.Length > 0 && geometry.MeasurementUnavailable) _unavailable++;
                    else _notNumbers++;
                    return;
                }
                bool scalar = row.NumericValue.HasValue;
                power = scalar ? 0 : geometry!.Power; precision = scalar ? 12 : 6;
                if (scalar && columnConversion.HasValue) { number = PanelUnits.Convert(number, columnConversion.Value); unit = PanelUnits.Symbol(columnConversion.Value); }
                number = PanelUnits.Transform(number, PanelUnitConversion.None, query.RoundUpFactor, query.DecimalPlaces, query.Formula);
            }
            else
            {
                var mode = query.SearchMode;
                var value = PanelFilterValue.OutputValue(snapshot, row, mode, measured, query.DecimalPlaces, query.RoundUpFactor, query.Formula);
                if (value?.Kind != PanelPropertyKind.Number)
                {
                    if (NeedsMeasurement(row, query) && measured == null) Measuring = true;
                    else if (value == null && geometry?.MeasurementUnavailable == true && PanelFilterValue.MatchesGeometry(mode, geometry)) _unavailable++;
                    else _notNumbers++;
                    return;
                }
                number = value.Number; unit = PanelFilterValue.UnitSymbol(snapshot, row, mode, measured);
                bool metric = mode >= PanelSearchMode.Length && mode <= PanelSearchMode.Volume;
                power = metric ? mode == PanelSearchMode.Area ? 2 : geometry!.Power : mode >= PanelSearchMode.X && mode <= PanelSearchMode.Z ? 1 : 0;
                if (PanelProperties.IsProperty(mode) && PanelProperties.TryGet(row, mode, measured, out var property)) power = property?.Power ?? 0;
                precision = metric ? 6 : 12;
            }
            if (!Finite(number)) { _nonfinite++; return; }
            if (query.DecimalPlaces < 0) number = PanelNumber.GeneralValue(number, precision);
            _precision = Math.Max(_precision, precision); _kinds |= power == 1 ? 2 : power == 2 ? 4 : power == 3 ? 8 : power == -1 ? 16 : 1;
            if (!_hasUnits) { _units = unit; _hasUnits = true; } else if (_units != unit) _units = "";
            _count++;
            if (Mixed) { _median = null; return; }
            if (Wants(PanelStatKinds.Sum)) _sum += number;
            if (Wants(PanelStatKinds.Avg | PanelStatKinds.StdDev))
            {
                double delta = number - _mean;
                double step = double.IsInfinity(delta) ? number / _count - _mean / _count : delta / _count;
                _mean = double.IsInfinity(delta) ? _mean * ((_count - 1.0) / _count) + number / _count : _mean + step;
                if (Wants(PanelStatKinds.StdDev))
                {
                    double a = Math.Abs(_deviation * Math.Sqrt((_count - 1.0) / _count)), b = Math.Abs(step * Math.Sqrt(_count - 1.0));
                    double scale = Math.Max(a, b), small = Math.Min(a, b);
                    _deviation = scale == 0 ? 0 : scale * Math.Sqrt(1 + (small / scale) * (small / scale));
                }
            }
            if (Wants(PanelStatKinds.Min | PanelStatKinds.Range) && number < _minimum) { _minimum = number; _min = position; }
            if (Wants(PanelStatKinds.Max | PanelStatKinds.Range) && number > _maximum) { _maximum = number; _max = position; }
            if (Wants(PanelStatKinds.Median)) (_median ??= new List<double>()).Add(number);
        }

        internal void Complete(PanelQueryOptions query, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (_count == 0 || Mixed || Measuring) { _median = null; return; }
            if (_median != null && !Mixed && !Measuring)
            {
                int comparisons = 0;
                try { _median.Sort((left, right) => { if ((comparisons++ & 1023) == 0) token.ThrowIfCancellationRequested(); return left.CompareTo(right); }); }
                catch (InvalidOperationException error) when (error.InnerException is OperationCanceledException) { throw new OperationCanceledException(token); }
                token.ThrowIfCancellationRequested();
                _medianValue = _count % 2 == 1 ? _median[_count / 2] : _median[_count / 2 - 1] / 2 + _median[_count / 2] / 2;
            }
            _median = null;
            double Round(double value) => !Finite(value) ? value : query.DecimalPlaces < 0 ? PanelNumber.GeneralValue(value, _precision) : PanelUnits.Transform(value, PanelUnitConversion.None, 0, query.DecimalPlaces);
            if (Wants(PanelStatKinds.Sum)) _sum = Round(_sum);
            if (Wants(PanelStatKinds.Avg)) _mean = Round(_mean);
            if (Wants(PanelStatKinds.Median)) _medianValue = Round(_medianValue);
            if (Wants(PanelStatKinds.Min)) _minimum = Round(_minimum);
            if (Wants(PanelStatKinds.Max)) _maximum = Round(_maximum);
            if (Wants(PanelStatKinds.StdDev)) _deviation = Round(_deviation);
            // Range is a difference of the presented extrema.
            if (Wants(PanelStatKinds.Range)) _range = Round(_maximum - _minimum);
        }
        private double _range;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
