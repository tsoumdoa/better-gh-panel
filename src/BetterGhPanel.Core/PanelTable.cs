using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BetterGhPanel.Core
{
    /// <summary>Frozen presentation keys. Missing cells sort last in either direction.</summary>
    public sealed class PanelColumnSortKeys
    {
        private readonly Func<int, PanelRow?> _rowAt;
        private readonly Func<PanelSnapshot, IReadOnlyList<int>, IEnumerable<PanelRow>>? _groupRows;
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
            Func<PanelSnapshot, IReadOnlyList<int>, IEnumerable<PanelRow>> groupRows)
        { _rowAt = rowAt; _groupRows = groupRows; }
        internal PanelRow? RowAt(int position) => _rowAt(position);
        internal IEnumerable<PanelRow> GroupRows(PanelSnapshot snapshot, IReadOnlyList<int> members) =>
            _groupRows != null ? _groupRows(snapshot, members) : members.Select(_rowAt).Where(row => row != null).Select(row => row!);
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
        private readonly int[] _offsets, _successors;
        public PanelTableIndex(PanelQueryResult result, int columns, IEnumerable<PanelTableExtra> extras)
        {
            var byPath = extras.GroupBy(extra => extra.Path).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            string? previous = null;
            void AppendExtras()
            {
                if (previous == null || !byPath.TryGetValue(previous, out var values)) return;
                var identities = new Dictionary<string, Entry>(StringComparer.Ordinal);
                foreach (var value in values.OrderBy(value => value.Order))
                {
                    if (!identities.TryGetValue(value.Identity, out var entry))
                    { entry = new Entry(-1, null, previous, columns); identities.Add(value.Identity, entry); _entries.Add(entry); }
                    entry.SetCell(value.Column, value.Position);
                }
            }
            for (int offset = 0; offset < result.ViewIndex.Count; offset++)
            {
                result.ViewIndex.TryGet(offset, out int item, out string? empty);
                string path = empty ?? result.Snapshot.Rows[result.RowPositions[item]].Path;
                if (previous != path) { AppendExtras(); previous = path; }
                _entries.Add(new Entry(item, empty, path, columns));
            }
            AppendExtras();
            _offsets = new int[result.RowPositions.Count]; _successors = new int[_entries.Count];
            int next = result.RowPositions.Count;
            for (int offset = _entries.Count - 1; offset >= 0; offset--)
            {
                int item = _entries[offset].Item;
                if (item >= 0) { _offsets[item] = offset; next = item; }
                _successors[offset] = next;
            }
        }
        public IReadOnlyList<Entry> Entries => _entries;
        public int OffsetOfItem(int item) => item >= 0 && item < _offsets.Length ? _offsets[item] : -1;
        public int ItemAtOrAfter(int offset) => Math.Max(0, offset) < _successors.Length ? _successors[Math.Max(0, offset)] : _offsets.Length;
    }

    internal static class PanelTableText
    {
        internal static string Summary(IReadOnlyList<string> preview, int count) => count == 0 ? "<empty set>" :
            string.Join(" · ", preview.Select(PanelValuePresentation.Visualize).Select(value => value.Length <= 120 ? value : value.Substring(0, 119) + "…")) +
            (count > preview.Count ? " … +" + (count - preview.Count) + " in this set" : "");
        internal static string Value(PanelRow row, PanelUnitConversion conversion, int decimals, double factor) =>
            !row.IsNull && row.IsValid && row.NumericValue.HasValue
                ? PanelUnits.Format(PanelUnits.Transform(row.NumericValue.Value, conversion, factor, decimals), PanelUnitConversion.None, decimals)
                : PanelValuePresentation.FormatDisplay(row, PanelUnitConversion.None, PanelUnits.AutomaticDecimals);
        internal static PanelRow PresentedRow(PanelRow row, PanelUnitConversion conversion, int decimals, double factor) =>
            new PanelRow(row.OriginalIndex, row.Path, row.TypeName, Value(row, conversion, decimals, factor),
                row.IsValid && row.NumericValue.HasValue ? PanelUnits.Transform(row.NumericValue.Value, conversion, factor, decimals) : (double?)null,
                isNull: row.IsNull, isValid: row.IsValid);
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
