using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace BetterGhPanel.Core
{
    /// <summary>A lightweight presentation record. It never contains a live Rhino object.</summary>
    public sealed class PanelRow
    {
        public PanelRow(int originalIndex, string path, string typeName,
            string displayValue, double? numericValue, bool isNull = false, bool isValid = true,
            PanelGeometryInfo? geometryInfo = null, string invalidReason = "")
        {
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
        public PanelBranch(string path, int itemCount)
        {
            if (itemCount < 0) throw new ArgumentOutOfRangeException(nameof(itemCount));
            Path = path ?? string.Empty;
            ItemCount = itemCount;
        }

        public string Path { get; }
        public int ItemCount { get; }
    }

    /// <summary>Owns immutable copies so a query cannot observe changes to the input lists.</summary>
    public sealed class PanelSnapshot
    {
        public static PanelSnapshot Empty { get; } =
            new PanelSnapshot(Array.Empty<PanelRow>(), Array.Empty<PanelBranch>());

        public PanelSnapshot(IReadOnlyList<PanelRow> rows, IReadOnlyList<PanelBranch> branches,
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
            UnitConversion = unitConversion;
            Rows = Array.AsReadOnly(rowCopy);
            Branches = Array.AsReadOnly(branchCopy);
            TypeCounts = new ReadOnlyDictionary<string, int>(typeCounts);
        }

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

    public enum PanelSearchMode
    {
        Text,
        Regex,
        Value,
        Length,
        Area,
        Volume
    }

    public sealed class PanelQueryOptions
    {
        public string Search { get; set; } = string.Empty;
        public PanelSearchMode SearchMode { get; set; } = PanelSearchMode.Text;
        public string? TypeName { get; set; }
        public string? BranchPath { get; set; }
        public bool BranchPrefix { get; set; }
        public bool OnlyInvalid { get; set; }
        public PanelSortMode SortMode { get; set; } = PanelSortMode.SourceOrder;

        public PanelQueryOptions Clone() => new PanelQueryOptions
        {
            Search = Search,
            SearchMode = SearchMode,
            TypeName = TypeName,
            BranchPath = BranchPath,
            BranchPrefix = BranchPrefix,
            OnlyInvalid = OnlyInvalid,
            SortMode = SortMode
        };
    }

    public sealed class PanelStatistics
    {
        internal PanelStatistics(int itemCount, int branchCount, int numericCount, int nullCount,
            int invalidCount, double? minimum, double? maximum, double? mean,
            IReadOnlyDictionary<string, int> typeCounts)
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
    }

    public sealed class PanelQueryResult
    {
        internal PanelQueryResult(PanelSnapshot snapshot, int[] rowPositions, PanelStatistics statistics)
        {
            Snapshot = snapshot;
            RowPositions = Array.AsReadOnly(rowPositions);
            Statistics = statistics;
        }

        public PanelSnapshot Snapshot { get; }
        /// <summary>Positions in Snapshot.Rows, retaining each row's original tree address.</summary>
        public IReadOnlyList<int> RowPositions { get; }
        public PanelStatistics Statistics { get; }
    }
}
