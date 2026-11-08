using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace BetterGhPanel.Core
{
    /// <summary>Frozen presentation keys. Missing cells sort last in either direction.</summary>
    public sealed class PanelColumnSortKeys
    {
        private readonly Func<int, PanelRow?> _rowAt;
        private readonly Func<PanelSnapshot, IReadOnlyList<int>, IEnumerable<PanelRow?>>? _groupRows;
        public PanelColumnSortKeys(IEnumerable<PanelRow?> rows)
        {
            var captured = rows.ToArray();
            _rowAt = position => position >= 0 && position < captured.Length ? captured[position] : null;
        }
        internal PanelColumnSortKeys(IReadOnlyDictionary<int, PanelRow?> rows) =>
            _rowAt = position => rows.TryGetValue(position, out var row) ? row : null;
        // Runtime providers close over immutable snapshots and pairing identities only.
        // They are evaluated by the cooperative query, never by header input handlers.
        internal PanelColumnSortKeys(Func<int, PanelRow?> rowAt,
            Func<PanelSnapshot, IReadOnlyList<int>, IEnumerable<PanelRow?>> groupRows)
        { _rowAt = rowAt; _groupRows = groupRows; }
        internal PanelRow? RowAt(int position) => _rowAt(position);
        internal IEnumerable<PanelRow?> GroupRows(PanelSnapshot snapshot, IReadOnlyList<int> members) =>
            _groupRows != null ? _groupRows(snapshot, members) : members.Select(_rowAt);
        internal int Compare(int left, int right, bool descending)
        {
            var a = _rowAt(left);
            var b = _rowAt(right);
            bool missingA = a == null || a.IsNull, missingB = b == null || b.IsNull;
            if (missingA != missingB) return missingA ? 1 : -1;
            if (missingA) return left.CompareTo(right);
            bool numericA = Finite(a!), numericB = Finite(b!);
            if (numericA != numericB) return numericA ? -1 : 1;
            int comparison = numericA ? a!.NumericValue!.Value.CompareTo(b!.NumericValue!.Value) :
                StringComparer.OrdinalIgnoreCase.Compare(a!.DisplayValue, b!.DisplayValue);
            return comparison == 0 ? left.CompareTo(right) : descending ? -comparison : comparison;
        }
        private static bool Finite(PanelRow row) => row.IsValid && row.NumericValue.HasValue &&
            !double.IsNaN(row.NumericValue.Value) && !double.IsInfinity(row.NumericValue.Value);
    }

    /// <summary>A non-D row's identity shared across independently paired columns.</summary>
    public sealed class PanelTableExtra
    {
        public PanelTableExtra(string path, string identity, int column, int position, long order = 0)
        { Path = path; Identity = identity; Column = column; Position = position; Order = order; }
        public string Path { get; }
        public string Identity { get; }
        public int Column { get; }
        public int Position { get; }
        public long Order { get; }
    }

    /// <summary>One augmented index for the whole table, with aligned extra cells.</summary>
    public sealed class PanelTableIndex
    {
        public sealed class Entry
        {
            internal Entry(int item, string? empty, string path, int columns)
            {
                Item = item; Empty = empty; Path = path;
                _cells = item < 0 && empty == null ? Enumerable.Repeat(-1, columns).ToArray() : Array.Empty<int>();
                Cells = _cells.Length == 0 ? (IReadOnlyList<int>)Array.Empty<int>() : Array.AsReadOnly(_cells);
            }
            public int Item { get; }
            public string? Empty { get; }
            public string Path { get; }
            private readonly int[] _cells;
            public IReadOnlyList<int> Cells { get; }
            internal void SetCell(int column, int position) => _cells[column] = position;
            public bool Extra => Item < 0 && Empty == null;
        }
        private readonly List<Entry> _entries = new List<Entry>();
        private int[] _offsets = Array.Empty<int>(), _successors = Array.Empty<int>();
        private PanelTableIndex() { }
        internal static IEnumerable<PanelTableIndex?> Build(PanelQueryResult result, int columns,
            IEnumerable<PanelTableExtra?> extras, PanelQueryWork work, CancellationToken token)
        {
            var index = new PanelTableIndex();
            foreach (var _ in index.BuildSteps(result, columns, extras, work, token)) yield return null;
            yield return index;
        }
        private IEnumerable<bool> BuildSteps(PanelQueryResult result, int columns, IEnumerable<PanelTableExtra?> extras,
            PanelQueryWork? work, CancellationToken token)
        {
            var byPath = new Dictionary<string, SortedDictionary<long, List<PanelTableExtra>>>(StringComparer.Ordinal);
            foreach (var extra in extras)
            {
                if (work?.ShouldYield() == true) yield return true;
                token.ThrowIfCancellationRequested();
                if (extra == null) continue;
                if (!byPath.TryGetValue(extra.Path, out var orders)) byPath.Add(extra.Path, orders = new SortedDictionary<long, List<PanelTableExtra>>());
                if (!orders.TryGetValue(extra.Order, out var cells)) orders.Add(extra.Order, cells = new List<PanelTableExtra>());
                cells.Add(extra);
            }
            string? previous = null;
            IEnumerable<bool> AppendExtras()
            {
                if (previous == null || !byPath.TryGetValue(previous, out var orders)) yield break;
                var identities = new Dictionary<string, Entry>(StringComparer.Ordinal);
                foreach (var cells in orders.Values)
                    foreach (var value in cells)
                    {
                        if (work?.ShouldYield() == true) yield return true;
                        token.ThrowIfCancellationRequested();
                        if (!identities.TryGetValue(value.Identity, out var entry))
                        { entry = new Entry(-1, null, previous, columns); identities.Add(value.Identity, entry); _entries.Add(entry); }
                        entry.SetCell(value.Column, value.Position);
                    }
            }
            for (int offset = 0; offset < result.ViewIndex.Count; offset++)
            {
                if (work?.ShouldYield() == true) yield return true;
                token.ThrowIfCancellationRequested();
                result.ViewIndex.TryGet(offset, out int item, out string? empty);
                string path = empty ?? result.Snapshot.Rows[result.RowPositions[item]].Path;
                if (previous != path) { foreach (var step in AppendExtras()) yield return step; previous = path; }
                _entries.Add(new Entry(item, empty, path, columns));
            }
            foreach (var step in AppendExtras()) yield return step;
            _offsets = new int[result.RowPositions.Count]; _successors = new int[_entries.Count];
            int next = result.RowPositions.Count;
            for (int offset = _entries.Count - 1; offset >= 0; offset--)
            {
                if (work?.ShouldYield() == true) yield return true;
                token.ThrowIfCancellationRequested();
                int item = _entries[offset].Item;
                if (item >= 0) { _offsets[item] = offset; next = item; }
                _successors[offset] = next;
            }
        }
        public IReadOnlyList<Entry> Entries => _entries;
        public int OffsetOfItem(int item) => item >= 0 && item < _offsets.Length ? _offsets[item] : -1;
        public int ItemAtOrAfter(int offset) => Math.Max(0, offset) < _successors.Length ? _successors[Math.Max(0, offset)] : _offsets.Length;
    }

    // Immutable adapter callbacks enumerate managed identities, including null budget checkpoints.
    internal sealed class PanelTableSource
    {
        internal PanelTableSource(int columns, Func<PanelQueryResult, IReadOnlyList<int>, bool, IEnumerable<PanelTableExtra?>>? extras,
            IReadOnlyList<PanelTableSummarySource>? summaries = null)
        { Columns = columns; Extras = extras; Summaries = summaries ?? Array.Empty<PanelTableSummarySource>(); }
        internal int Columns { get; }
        internal Func<PanelQueryResult, IReadOnlyList<int>, bool, IEnumerable<PanelTableExtra?>>? Extras { get; }
        internal IReadOnlyList<PanelTableSummarySource> Summaries { get; }
    }

    internal sealed class PanelTableSummarySource
    {
        internal PanelTableSummarySource(Guid id, Func<int, int?> itemAt,
            Func<PanelQueryResult, IReadOnlyList<int>, bool, IEnumerable<(int Item, string Path)?>> branchItems,
            Func<string, IEnumerable<int>> extraCounts)
        { Id = id; ItemAt = itemAt; BranchItems = branchItems; ExtraCounts = extraCounts; }
        internal Guid Id { get; }
        internal Func<int, int?> ItemAt { get; }
        internal Func<PanelQueryResult, IReadOnlyList<int>, bool, IEnumerable<(int Item, string Path)?>> BranchItems { get; }
        internal Func<string, IEnumerable<int>> ExtraCounts { get; }
    }
    internal sealed class PanelTableColumnSummary
    {
        internal readonly Dictionary<int, PanelTableMemberSummary> Sets = new Dictionary<int, PanelTableMemberSummary>();
        internal readonly Dictionary<string, PanelTableMemberSummary> Branches = new Dictionary<string, PanelTableMemberSummary>(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> Extras = new Dictionary<string, int>(StringComparer.Ordinal);
    }
    internal static class PanelTableSummaries
    {
        internal static IEnumerable<bool> Build(PanelQueryResult result, IReadOnlyList<int> members, PanelTableSource source,
            bool includeEmpty, PanelQueryWork work, CancellationToken token)
        {
            foreach (var column in source.Summaries)
            {
                if (work.ShouldYield()) yield return true;
                token.ThrowIfCancellationRequested();
                var summary = new PanelTableColumnSummary();
                var set = result.DisplaySet;
                if (set == null)
                {
                    foreach (var step in column.BranchItems(result, members, includeEmpty))
                    {
                        if (work.ShouldYield()) yield return true;
                        token.ThrowIfCancellationRequested();
                        if (!step.HasValue) continue;
                        var cell = step.Value;
                        if (!summary.Branches.TryGetValue(cell.Path, out var branch)) summary.Branches.Add(cell.Path, branch = new PanelTableMemberSummary());
                        branch.Add(cell.Item);
                    }
                }
                else
                {
                    var groups = set.IsBranchSet ? set.BranchMembers.Select(group => (Item: -1, Path: group.Key, Members: group.Value)) :
                        set.Members.Select(group => (Item: group.Key, Path: result.Snapshot.Rows[group.Key].Path, Members: group.Value));
                    foreach (var group in groups)
                    {
                        if (work.ShouldYield()) yield return true;
                        token.ThrowIfCancellationRequested();
                        var cell = new PanelTableMemberSummary();
                        foreach (int position in group.Members)
                        {
                            if (work.ShouldYield()) yield return true;
                            token.ThrowIfCancellationRequested();
                            cell.Add(column.ItemAt(position) ?? -1);
                        }
                        if (set.IsBranchSet) summary.Branches.Add(group.Path, cell);
                        else summary.Sets.Add(group.Item, cell);
                    }
                }
                if (set == null || set.IsBranchSet)
                    foreach (string path in result.BranchPaths)
                    {
                        if (work.ShouldYield()) yield return true;
                        token.ThrowIfCancellationRequested();
                        int count = 0;
                        var paths = set?.IsBranchSet == true ? set.BranchOccurrences[path] : (IReadOnlyList<string>)new[] { path };
                        foreach (string original in paths)
                        {
                            if (work.ShouldYield()) yield return true;
                            token.ThrowIfCancellationRequested();
                            foreach (int extra in column.ExtraCounts(original))
                            {
                                if (work.ShouldYield()) yield return true;
                                token.ThrowIfCancellationRequested();
                                count += extra;
                            }
                        }
                        summary.Extras.Add(path, count);
                    }
                result.TableSummaries.Add(column.Id, summary);
            }
        }
    }

    internal static class PanelTableInputIdentity
    {
        internal static Guid Resolve(Guid saved, IReadOnlyList<Guid> current, int index)
        {
            if (current.Contains(saved)) return saved;
            return index >= 0 && index < current.Count ? current[index] : Guid.Empty;
        }
    }

    // Display summaries follow set-member indices, independently of wire-output expansion.
    internal sealed class PanelTableMemberSummary
    {
        internal readonly List<int> Preview = new List<int>(4);
        internal int Count { get; private set; }
        internal void Add(int item) { Count++; if (Preview.Count < 4) Preview.Add(item); }
    }

    internal static class PanelTableText
    {
        internal static string HeaderTitle(IReadOnlyList<string?> titles, int index, string fallback) =>
            index >= 0 && index < titles.Count && !string.IsNullOrWhiteSpace(titles[index]) ? titles[index]! : fallback;

        internal const float MinimumColumnWidth = 48, MaximumColumnWidth = 320;
        internal static string LengthBadge(int total, int dataTotal) => total > dataTotal
            ? "+" + (total - dataTotal) + " extra" : total + " of " + dataTotal;
        internal static bool NumericColumn(PanelSnapshot? snapshot) => snapshot?.TypeCounts.Count > 0 &&
            snapshot.TypeCounts.Keys.All(type => type == "Number" || type == "Integer");
        internal static bool ShowCellTypes(PanelSnapshot? snapshot) => snapshot != null &&
            (snapshot.TypeCounts.Count > 1 || snapshot.TypeCounts.Keys.Any(type =>
                type != "Text" && type != "String" && type != "Number" && type != "Integer" && type != "Boolean"));
        internal static string Summary(IReadOnlyList<string> preview, int count, bool set = true, bool compact = false) => count == 0 ? set ? "<empty set>" : "<empty branch>" :
            string.Join(" · ", preview.Select(PanelValuePresentation.Visualize).Select(value => value.Length <= 120 ? value : value.Substring(0, 119) + "…")) +
            (count > preview.Count ? " … +" + (count - preview.Count) + (compact ? "" : set ? " in this set" : " items") : "");
        internal static string Value(PanelRow row, PanelUnitConversion conversion, int decimals, double factor) =>
            !row.IsNull && row.IsValid && row.NumericValue.HasValue
                ? PanelUnits.Format(PanelUnits.Transform(row.NumericValue.Value, conversion, factor, decimals), PanelUnitConversion.None, decimals)
                : PanelValuePresentation.FormatDisplay(row, PanelUnitConversion.None, PanelUnits.AutomaticDecimals);
        internal static PanelRow PresentedRow(PanelRow row, PanelUnitConversion conversion, int decimals, double factor) =>
            new PanelRow(row.OriginalIndex, row.Path, row.TypeName, Value(row, conversion, decimals, factor),
                row.IsValid && row.NumericValue.HasValue ? PanelUnits.Transform(row.NumericValue.Value, conversion, factor, decimals) : (double?)null,
                isNull: row.IsNull, isValid: row.IsValid);

        // Geometry columns compare the primary displayed metric, never their type or hints.
        // A metric column keeps every other kind (including ordinary numbers) last.
        internal static PanelRow MetricSortRow(PanelRow row, PanelGeometryInfo? geometry,
            PanelUnitConversion conversion, int decimals, double factor, bool homogeneous = false)
        {
            double? value = null;
            if (row.IsValid && !row.IsNull && geometry?.Value is double metric && !geometry.MeasurementUnavailable &&
                metric >= 0 && !double.IsNaN(metric) && !double.IsInfinity(metric))
            {
                double presented = PanelUnits.Transform(PanelUnits.ConvertGeometry(metric, geometry.Power, geometry.Units, conversion),
                    PanelUnitConversion.None, factor, decimals);
                if (!double.IsNaN(presented) && !double.IsInfinity(presented)) value = presented;
            }
            return new PanelRow(row.OriginalIndex, row.Path, row.TypeName,
                GeometryCell(row, geometry, homogeneous, false, conversion, decimals, factor), value, isNull: !value.HasValue);
        }

        internal static bool HasGeometryMetric(PanelGeometryInfo? geometry) => geometry != null &&
            (geometry.CanMeasure || geometry.Value.HasValue || geometry.MeasurementUnavailable);

        internal static string GeometryCell(PanelRow row, PanelGeometryInfo? geometry, bool homogeneous, bool measuring,
            PanelUnitConversion conversion, int decimals, double factor)
        {
            if (row.IsNull || geometry == null) return Value(row, conversion, decimals, factor);
            if (!row.IsValid) return row.Validity == PanelValidity.VerifiedInvalid ? "invalid" : "unverified";
            if (!HasGeometryMetric(geometry)) return Value(row, conversion, decimals, factor);
            if (measuring) return "measuring…";
            string number = geometry!.Value.HasValue && !geometry.MeasurementUnavailable
                ? PanelUnits.FormatGeometry(geometry.Value.Value, geometry.Power, geometry.Units, conversion, decimals,
                    includeUnits: !homogeneous, roundUpFactor: factor) : "unavailable";
            return homogeneous ? number : StockTypeLabels.ShortName(row.TypeName) +
                (geometry.MetricLabel.Length == 0 ? "" : " " + geometry.MetricLabel) + " " + number;
        }

        internal static PanelGeometryInfo? CommonGeometryMetric(PanelSnapshot snapshot)
        {
            PanelGeometryInfo? first = null;
            foreach (var row in snapshot.Rows)
            {
                var geometry = row.GeometryInfo;
                if (!row.IsValid || row.IsNull || geometry == null || (!geometry.CanMeasure && !geometry.Value.HasValue)) continue;
                if (first != null && (first.Power != geometry.Power || first.MetricName != geometry.MetricName ||
                    first.MetricLabel != geometry.MetricLabel || first.Units != geometry.Units)) return null;
                first = geometry;
            }
            return first;
        }
        internal static string GeometryHeaderUnit(PanelGeometryInfo? metric, PanelUnitConversion conversion) =>
            metric != null && metric.Power > 0 ? PanelUnits.GeometryUnitSymbol(metric.Power, metric.Units, conversion) : "";

        internal static string GeometryHeaderSuffix(PanelGeometryInfo? metric, PanelUnitConversion conversion)
        {
            if (metric == null || metric.MetricLabel.Length == 0) return "";
            string unit = GeometryHeaderUnit(metric, conversion);
            return " · " + metric.MetricLabel + (unit.Length == 0 ? "" : " · " + unit);
        }

    }

    public static class PanelTableTsv
    {
        /// <summary>Quotes embedded tabs, newlines and quotes using spreadsheet TSV rules.</summary>
        public static string Build(IEnumerable<string> titles, IEnumerable<(string Path, int Index, IReadOnlyList<string> Values)> rows)
        {
            var text = new StringBuilder();
            void Line(IEnumerable<string> values) => text.Append(string.Join("\t", values.Select(Escape))).Append(Environment.NewLine);
            Line(new[] { "Path", "Index" }.Concat(titles));
            foreach (var row in rows) Line(new[] { row.Path, row.Index.ToString(CultureInfo.InvariantCulture) }.Concat(row.Values));
            return text.ToString();
        }
        private static string Escape(string value) => value.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) < 0 ? value :
            "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
