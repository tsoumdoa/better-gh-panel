using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace BetterGhPanel.Core
{
    /// <summary>A lightweight presentation record. It never contains a live Rhino object.</summary>
    public sealed class PanelRow
    {
        public PanelRow(int id, int branchIndex, int originalIndex, string path, string typeName,
            string displayValue, double? numericValue, bool isNull = false, bool isValid = true,
            PanelGeometryInfo? geometryInfo = null, string invalidReason = "")
        {
            Id = id;
            BranchIndex = branchIndex;
            OriginalIndex = originalIndex;
            Path = path ?? string.Empty;
            TypeName = typeName ?? string.Empty;
            DisplayValue = displayValue ?? string.Empty;
            NumericValue = numericValue;
            IsNull = isNull;
            IsValid = isValid;
            GeometryInfo = geometryInfo;
            InvalidReason = invalidReason;
        }

        public int Id { get; }
        public int BranchIndex { get; }
        public int OriginalIndex { get; }
        public string Path { get; }
        public string TypeName { get; }
        public string DisplayValue { get; }
        public double? NumericValue { get; }
        public bool IsNull { get; }
        public bool IsValid { get; }
        public PanelGeometryInfo? GeometryInfo { get; }
        public string InvalidReason { get; }
    }

    public sealed class PanelBranch
    {
        public PanelBranch(int index, string path, int itemCount)
        {
            if (itemCount < 0) throw new ArgumentOutOfRangeException(nameof(itemCount));
            Index = index;
            Path = path ?? string.Empty;
            ItemCount = itemCount;
        }

        public int Index { get; }
        public string Path { get; }
        public int ItemCount { get; }
    }

    /// <summary>Owns immutable copies so a query cannot observe changes to the input lists.</summary>
    public sealed class PanelSnapshot
    {
        public static PanelSnapshot Empty { get; } =
            new PanelSnapshot(0, Array.Empty<PanelRow>(), Array.Empty<PanelBranch>());

        public PanelSnapshot(long version, IReadOnlyList<PanelRow> rows, IReadOnlyList<PanelBranch> branches,
            PanelUnitConversion unitConversion = PanelUnitConversion.None)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            if (branches == null) throw new ArgumentNullException(nameof(branches));
            var rowCopy = new PanelRow[rows.Count];
            var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rowCopy.Length; i++)
            {
                var row = rows[i] ?? throw new ArgumentException("Rows must not contain null records.", nameof(rows));
                rowCopy[i] = row;
                typeCounts.TryGetValue(row.TypeName, out var count);
                typeCounts[row.TypeName] = count + 1;
            }
            var branchCopy = new PanelBranch[branches.Count];
            for (var i = 0; i < branchCopy.Length; i++)
                branchCopy[i] = branches[i] ?? throw new ArgumentException("Branches must not contain null records.", nameof(branches));
            Version = version;
            UnitConversion = unitConversion;
            Rows = Array.AsReadOnly(rowCopy);
            Branches = Array.AsReadOnly(branchCopy);
            TypeCounts = new ReadOnlyDictionary<string, int>(typeCounts);
        }

        public long Version { get; }
        public PanelUnitConversion UnitConversion { get; }
        public IReadOnlyList<PanelRow> Rows { get; }
        public IReadOnlyList<PanelBranch> Branches { get; }
        /// <summary>Source type counts for populating filters without another full-tree scan.</summary>
        public IReadOnlyDictionary<string, int> TypeCounts { get; }
    }

    public enum PanelSortMode
    {
        SourceOrder,
        ValueAscending,
        ValueDescending
    }

    public sealed class PanelQueryOptions
    {
        public string Search { get; set; } = string.Empty;
        public string? TypeName { get; set; }
        public string? BranchPath { get; set; }
        public bool BranchPrefix { get; set; }
        public bool OnlyInvalid { get; set; }
        public PanelSortMode SortMode { get; set; } = PanelSortMode.SourceOrder;

        public PanelQueryOptions Clone() => new PanelQueryOptions
        {
            Search = Search,
            TypeName = TypeName,
            BranchPath = BranchPath,
            BranchPrefix = BranchPrefix,
            OnlyInvalid = OnlyInvalid,
            SortMode = SortMode
        };
    }

    public sealed class PanelStatistics
    {
        private readonly Lazy<IReadOnlyList<int>> _histogram;

        internal PanelStatistics(int itemCount, int branchCount, int numericCount, int nullCount,
            int invalidCount, double? minimum, double? maximum, double? mean,
            IReadOnlyDictionary<string, int> typeCounts, Func<IReadOnlyList<int>> histogram)
        {
            ItemCount = itemCount;
            BranchCount = branchCount;
            NumericCount = numericCount;
            NullCount = nullCount;
            InvalidCount = invalidCount;
            Minimum = minimum;
            Maximum = maximum;
            Mean = mean;
            TypeCounts = typeCounts;
            _histogram = new Lazy<IReadOnlyList<int>>(histogram);
        }

        public int ItemCount { get; }
        public int BranchCount { get; }
        public int NumericCount { get; }
        public int NullCount { get; }
        public int InvalidCount { get; }
        public double? Minimum { get; }
        public double? Maximum { get; }
        public double? Mean { get; }
        public IReadOnlyDictionary<string, int> TypeCounts { get; }
        /// <summary>Computed once on demand; native views only request scalar statistics.</summary>
        public IReadOnlyList<int> Histogram => _histogram.Value;
    }

    public sealed class PanelQueryResult
    {
        internal PanelQueryResult(PanelSnapshot snapshot, int[] rowIds, PanelStatistics statistics,
            IReadOnlyDictionary<string, int> branchCounts)
        {
            Snapshot = snapshot;
            RowIds = Array.AsReadOnly(rowIds);
            Statistics = statistics;
            BranchCounts = branchCounts;
        }

        public PanelSnapshot Snapshot { get; }
        /// <summary>Positions in Snapshot.Rows, retaining each row's original tree address.</summary>
        public IReadOnlyList<int> RowIds { get; }
        public PanelStatistics Statistics { get; }
        public IReadOnlyDictionary<string, int> BranchCounts { get; }

        /// <summary>Retrieves only the requested window, including a partial last page.</summary>
        public IReadOnlyList<PanelRow> GetRows(int offset, int count)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            var length = Math.Min(count, Math.Max(0, RowIds.Count - offset));
            if (length == 0) return Array.Empty<PanelRow>();
            var rows = new PanelRow[length];
            for (var i = 0; i < length; i++) rows[i] = Snapshot.Rows[RowIds[offset + i]];
            return Array.AsReadOnly(rows);
        }
    }
}
