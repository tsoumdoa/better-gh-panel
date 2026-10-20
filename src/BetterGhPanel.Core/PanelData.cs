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
            PanelGeometryInfo? geometryInfo = null, string invalidReason = "", string? stringValue = null,
            PanelSetOccurrences? setOccurrences = null)
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
            StringValue = stringValue;
            SetOccurrences = setOccurrences;
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
        /// <summary>The exact captured string scalar, not an arbitrary goo's display summary.</summary>
        public string? StringValue { get; }
        public PanelSetOccurrences? SetOccurrences { get; }
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
            bool hasMeasurableGeometry = false;
            for (var i = 0; i < rowCopy.Length; i++)
            {
                var row = rows[i] ?? throw new ArgumentException("Rows must not contain null records.", nameof(rows));
                rowCopy[i] = row;
                typeCounts.TryGetValue(row.TypeName, out var count);
                typeCounts[row.TypeName] = count + 1;
                if (row.IsValid && !row.IsNull && row.GeometryInfo?.CanMeasure == true) hasMeasurableGeometry = true;
            }
            var branchCopy = new PanelBranch[branches.Count];
            var branchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < branchCopy.Length; i++)
            {
                var branch = branches[i] ?? throw new ArgumentException("Branches must not contain null records.", nameof(branches));
                branchCopy[i] = branch;
                branchCounts[branch.Path] = branch.ItemCount;
            }
            UnitConversion = unitConversion;
            HasMeasurableGeometry = hasMeasurableGeometry;
            Rows = Array.AsReadOnly(rowCopy);
            Branches = Array.AsReadOnly(branchCopy);
            TypeCounts = new ReadOnlyDictionary<string, int>(typeCounts);
            BranchItemCounts = new ReadOnlyDictionary<string, int>(branchCounts);
        }

        public PanelUnitConversion UnitConversion { get; }
        /// <summary>Whether any valid captured row can require a deferred geometry measurement.</summary>
        public bool HasMeasurableGeometry { get; }
        public IReadOnlyList<PanelRow> Rows { get; }
        public IReadOnlyList<PanelBranch> Branches { get; }
        /// <summary>Source type counts for populating filters without another full-tree scan.</summary>
        public IReadOnlyDictionary<string, int> TypeCounts { get; }
        public IReadOnlyDictionary<string, int> BranchItemCounts { get; }
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
        Volume,
        // Append modes so saved definitions retain their existing values.
        ListLength
    }

    public sealed class PanelQueryOptions
    {
        public string Search { get; set; } = string.Empty;
        public PanelSearchMode SearchMode { get; set; } = PanelSearchMode.Text;
        /// <summary>Presentation precision for text/regex matching; numeric predicates retain full precision.</summary>
        public int DecimalPlaces { get; set; } = PanelUnits.AutomaticDecimals;
        public string? TypeName { get; set; }
        public string? BranchPath { get; set; }
        public bool BranchPrefix { get; set; }
        public bool OnlyInvalid { get; set; }
        public PanelSortMode SortMode { get; set; } = PanelSortMode.SourceOrder;

        public PanelQueryOptions Clone() => new PanelQueryOptions
        {
            Search = Search,
            SearchMode = SearchMode,
            DecimalPlaces = DecimalPlaces,
            TypeName = TypeName,
            BranchPath = BranchPath,
            BranchPrefix = BranchPrefix,
            OnlyInvalid = OnlyInvalid,
            SortMode = SortMode
        };
    }

    /// <summary>One exact numeric or string value, ordered by its first occurrence in the source.</summary>
    public sealed class PanelValueFrequency
    {
        internal PanelValueFrequency(double? numericValue, string? stringValue, int count)
        {
            NumericValue = numericValue;
            StringValue = stringValue;
            Count = count;
        }

        public double? NumericValue { get; }
        public string? StringValue { get; }
        public int Count { get; }
    }

    public sealed class PanelStatistics
    {
        internal PanelStatistics(int itemCount, int branchCount, int numericCount, int nullCount,
            int invalidCount, double? minimum, double? maximum, double? mean,
            IReadOnlyDictionary<string, int> typeCounts, IReadOnlyDictionary<string, int> branchItemCounts,
            IReadOnlyList<PanelValueFrequency> uniqueValues, int scalarCount)
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
            BranchItemCounts = branchItemCounts;
            UniqueValues = uniqueValues;
            ScalarCount = scalarCount;
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
        /// <summary>Counts in this filtered result, including retained empty branches.</summary>
        public IReadOnlyDictionary<string, int> BranchItemCounts { get; }
        /// <summary>Valid finite numbers and exact captured strings; these kinds never compare equal.</summary>
        public IReadOnlyList<PanelValueFrequency> UniqueValues { get; }
        public int ScalarCount { get; }
        public int UniqueExcludedCount => ItemCount - ScalarCount;
    }

    public sealed class PanelQueryResult
    {
        internal PanelQueryResult(PanelSnapshot snapshot, int[] rowPositions, PanelStatistics statistics, IEnumerable<string> branchPaths)
        {
            Snapshot = snapshot;
            RowPositions = Array.AsReadOnly(rowPositions);
            Statistics = statistics;
            BranchPaths = Array.AsReadOnly(new List<string>(branchPaths).ToArray());
        }

        public PanelSnapshot Snapshot { get; }
        /// <summary>Positions in Snapshot.Rows, retaining each row's original tree address.</summary>
        public IReadOnlyList<int> RowPositions { get; }
        public PanelStatistics Statistics { get; }
        /// <summary>Matching paths, including empty branches when no item filter is active.</summary>
        public IReadOnlyList<string> BranchPaths { get; }
    }
}
