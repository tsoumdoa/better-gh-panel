using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGhPanel.Core
{
    [Flags]
    public enum PanelStatKinds { Count = 1, Sum = 2, Avg = 4, Median = 8, Min = 16, Max = 32, Range = 64, StdDev = 128 }

    /// <summary>Statistics of the filtered, presented inputs, before display sorting.</summary>
    public sealed class PanelBranchStats
    {
        public const PanelStatKinds Default = PanelStatKinds.Sum | PanelStatKinds.Avg | PanelStatKinds.Min | PanelStatKinds.Max;
        public const PanelStatKinds All = (PanelStatKinds)255;
        private readonly List<double> _numbers = new List<double>();
        private readonly SortedSet<string> _metrics = new SortedSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _skipped = new Dictionary<string, int>();
        private readonly Dictionary<PanelStatKinds, double?> _values = new Dictionary<PanelStatKinds, double?>();
        private int _min = -1, _max = -1;
        private double _minimum = double.PositiveInfinity, _maximum = double.NegativeInfinity;
        private string _units = "";
        private bool _hasUnits, _geometryNumbers, _scalarNumbers;
        private int Precision => _geometryNumbers && !_scalarNumbers ? 6 : 12;
        public bool Measuring { get; private set; }
        public int Skipped { get; private set; }
        public bool Mixed => _metrics.Count > 1;
        public string MixedLabel => "mixed " + string.Join(" / ", new[] { "L", "A", "V" }.Where(_metrics.Contains));
        public string Note => Mixed ? MixedLabel : Skipped > 0 ? Skipped + " skipped" : "";
        public string Details => Mixed ? "Pick Length, Area or Volume in Search to use one metric." :
            string.Join(", ", _skipped.Select(pair => pair.Value + " " + pair.Key)) + ": left out of the stats.";
        public static PanelStatKinds Normalize(PanelStatKinds picks) => (picks & All) == 0 ? Default : picks & All;
        public static IEnumerable<PanelStatKinds> Picks(PanelStatKinds picks) => Enum.GetValues(typeof(PanelStatKinds)).Cast<PanelStatKinds>().Where(kind => (Normalize(picks) & kind) != 0);
        public static string Name(PanelStatKinds kind) => kind == PanelStatKinds.StdDev ? "Std dev" : kind.ToString();
        public double? Value(PanelStatKinds kind) => _values.TryGetValue(kind, out var value) ? value : null;
        public int Source(PanelStatKinds kind) => Mixed || Measuring ? -1 : kind == PanelStatKinds.Min ? _min : kind == PanelStatKinds.Max ? _max : -1;
        public IEnumerable<(PanelStatKinds Kind, double? Number)> Output(PanelStatKinds picks, ISet<int>? selection = null)
        {
            if (Measuring) yield break;
            foreach (var kind in Picks(picks))
                if (selection == null || Source(kind) >= 0 && selection.Contains(Source(kind)))
                    yield return (kind, Value(kind));
        }

        // Table columns follow D's filtered membership and index pairing. Formula belongs to D alone.
        internal static IReadOnlyDictionary<string, PanelBranchStats> Column(PanelQueryResult data, PanelSnapshot snapshot,
            Func<int, int?> partner, PanelQueryOptions options, IReadOnlyDictionary<int, PanelGeometryInfo?>? measurements,
            System.Threading.CancellationToken token)
        {
            var stats = data.BranchPaths.ToDictionary(path => path, path => new PanelBranchStats(), StringComparer.Ordinal);
            var query = options.Clone(); query.Formula = null;
            query.SearchMode = PanelSearchMode.Text; query.DisplayFilterValue = false;
            var converted = new PanelSnapshot(snapshot.Rows, snapshot.Branches, data.Snapshot.UnitConversion);
            foreach (int position in data.RowPositions)
            {
                token.ThrowIfCancellationRequested();
                if (partner(position) is not int item) continue;
                var row = snapshot.Rows[item];
                PanelGeometryInfo? measured = null;
                if (measurements?.TryGetValue(item, out measured) == true && measured == null)
                    measured = row.GeometryInfo?.WithUnavailableMeasurement();
                if (row.NumericValue.HasValue && row.IsValid)
                    row = new PanelRow(row.OriginalIndex, row.Path, row.TypeName, row.DisplayValue,
                        PanelUnits.Convert(row.RawNumericValue!.Value, converted.UnitConversion));
                stats[data.Snapshot.Rows[position].Path].Add(converted, row, item, query, measured);
            }
            foreach (var branch in stats.Values) { token.ThrowIfCancellationRequested(); branch.Complete(query); }
            return stats;
        }
        internal static IReadOnlyList<int> ColumnItems(PanelQueryResult data, Func<int, int?> partner) =>
            data.RowPositions.Select(partner).Where(item => item.HasValue).Select(item => item!.Value).Distinct().ToArray();

        public string Format(PanelStatKinds kind, int decimals, bool includeUnits = true) => Measuring ? "measuring…" :
            !Value(kind).HasValue ? "—" : kind == PanelStatKinds.Count ? Value(kind)!.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) :
            PanelUnits.Format(Value(kind)!.Value, PanelUnitConversion.None, decimals, Precision == 6 ? "G6" : "G12") + (includeUnits && _units.Length > 0 ? " " + _units : "");

        internal void Add(PanelSnapshot snapshot, PanelRow row, int position, PanelQueryOptions query, PanelGeometryInfo? measured)
        {
            void Skip(string reason) { Skipped++; _skipped.TryGetValue(reason, out int count); _skipped[reason] = count + 1; }
            if (row.IsNull) { Skip("null"); return; }
            if (!row.IsValid) { Skip(row.Validity == PanelValidity.Unverified ? "unverified" : "invalid"); return; }
            double number;
            string unit;
            if (row.NumericValue.HasValue)
            {
                _scalarNumbers = true;
                number = PanelValuePresentation.PresentedNumber(row, query.DecimalPlaces, query.RoundUpFactor, query.Formula);
                unit = PanelUnits.Symbol(snapshot.UnitConversion);
            }
            else
            {
                var geometry = measured ?? row.GeometryInfo;
                if (geometry == null) { Skip(row.StringValue != null || row.TypeName == "Text" ? "text" : "non-numeric"); return; }
                var mode = query.SearchMode >= PanelSearchMode.Length && query.SearchMode <= PanelSearchMode.Volume ? query.SearchMode : PanelSearchMode.Text;
                bool main = mode == PanelSearchMode.Text;
                if (!main && !PanelFilterValue.MatchesGeometry(mode, geometry)) { Skip("other metrics"); return; }
                if (geometry.CanMeasure && !geometry.MeasurementUnavailable && !geometry.Value.HasValue) { Measuring = true; return; }
                if (main)
                {
                    if (!geometry.Value.HasValue || geometry.Value < 0) { Skip("unavailable measurements"); return; }
                    number = PanelUnits.ConvertGeometry(geometry.Value.Value, geometry.Power, geometry.Units, snapshot.UnitConversion);
                    unit = PanelPropertyValue.Numeric(1, geometry.Power, geometry.Units).UnitSymbol(snapshot.UnitConversion);
                }
                else
                {
                    if (!PanelFilterValue.TryGeometryValue(row, mode, snapshot.UnitConversion, geometry, out number)) { Skip("unavailable measurements"); return; }
                    unit = PanelFilterValue.UnitSymbol(snapshot, row, mode, geometry);
                }
                number = PanelUnits.Transform(number, PanelUnitConversion.None, query.RoundUpFactor, query.DecimalPlaces, query.Formula);
                if (Finite(number)) _geometryNumbers = true;
                if (Finite(number)) _metrics.Add(main ? geometry.Power == 2 ? "A" : geometry.Power == 3 ? "V" : "L" : mode == PanelSearchMode.Area ? "A" : mode == PanelSearchMode.Volume ? "V" : "L");
            }
            if (!Finite(number)) { Skip("non-finite values"); return; }
            if (!_hasUnits) { _units = unit; _hasUnits = true; } else if (_units != unit) _units = "";
            if (query.DecimalPlaces < 0) number = PanelNumber.GeneralValue(number, row.NumericValue.HasValue ? 12 : 6);
            _numbers.Add(number);
            if (number < _minimum) { _minimum = number; _min = position; }
            if (number > _maximum) { _maximum = number; _max = position; }
        }

        internal void Complete(PanelQueryOptions query)
        {
            int count = Mixed ? 0 : _numbers.Count;
            _values[PanelStatKinds.Count] = count;
            if (count == 0 || Measuring) return;
            _numbers.Sort();
            double sum = 0, mean = 0;
            for (int i = 0; i < count; i++)
            {
                double value = _numbers[i]; sum += value;
                double delta = value - mean;
                mean = double.IsInfinity(delta) ? mean * (i / (i + 1.0)) + value / (i + 1.0) : mean + delta / (i + 1.0);
            }
            // Scaled sum of squares avoids overflowing ordinary large inputs.
            double scale = _numbers.Max(value => Math.Abs(value - mean));
            double sd = scale == 0 ? 0 : scale * Math.Sqrt(_numbers.Sum(value => Math.Pow((value - mean) / scale, 2)) / count);
            void Set(PanelStatKinds kind, double value)
            {
                // Inputs already include formula and factor. Only presentation precision applies again.
                value = query.DecimalPlaces < 0 ? PanelNumber.GeneralValue(value, Precision) :
                    PanelUnits.Transform(value, PanelUnitConversion.None, 0, query.DecimalPlaces);
                _values[kind] = Finite(value) ? value : (double?)null;
            }
            Set(PanelStatKinds.Sum, sum); Set(PanelStatKinds.Avg, mean);
            Set(PanelStatKinds.Median, count % 2 == 1 ? _numbers[count / 2] : _numbers[count / 2 - 1] / 2 + _numbers[count / 2] / 2);
            Set(PanelStatKinds.Min, _minimum); Set(PanelStatKinds.Max, _maximum);
            Set(PanelStatKinds.Range, _maximum - _minimum); Set(PanelStatKinds.StdDev, sd);
            _numbers.Clear();
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
