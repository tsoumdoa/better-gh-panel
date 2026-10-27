using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace BetterGhPanel.Core
{
    /// <summary>Whether capture could establish validity without invoking untrusted code.</summary>
    public enum PanelValidity { VerifiedValid, VerifiedInvalid, Unverified }

    /// <summary>Input-derived optional search modes. Text, regex and list length are always available.</summary>
    [Flags]
    public enum PanelSearchCapabilities { None = 0, Value = 1, Length = 2, Area = 4, Volume = 8 }

    /// <summary>Captured group name and direct count, with optional bounded formatted-text retention.</summary>
    public sealed class PanelGroupSummary
    {
        private readonly string _name;
        private readonly int _count;
        private readonly string? _retainedText;

        public PanelGroupSummary(string? name, int count, bool retainFormattedText)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            _name = name ?? string.Empty;
            _count = count;
            if (retainFormattedText) _retainedText = FormatCore();
        }

        public string Format() => _retainedText ?? FormatCore();
        private string FormatCore()
        {
            string label = _name.Length == 0 ? "" : " (" + _name + ")";
            return _count == 0 ? "Empty Group" + label : "Group" + label + " with " +
                _count.ToString(System.Globalization.CultureInfo.InvariantCulture) + (_count == 1 ? " object" : " objects");
        }
    }

    /// <summary>A lightweight presentation record. It never contains a live Rhino object.</summary>
    public sealed class PanelRow
    {
        public PanelRow(int originalIndex, string path, string typeName,
            string displayValue, double? numericValue, bool isNull = false, bool isValid = true,
            PanelGeometryInfo? geometryInfo = null, string invalidReason = "", string? stringValue = null,
            PanelSetOccurrences? setOccurrences = null)
            : this(originalIndex, path, typeName, displayValue, numericValue,
                isValid ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid,
                isNull: isNull, geometryInfo: geometryInfo, invalidReason: invalidReason,
                stringValue: stringValue, setOccurrences: setOccurrences) { }

        public PanelRow(int originalIndex, string path, string typeName, string displayValue, double? numericValue,
            PanelValidity validity, bool typeIdentityAvailable = true, PanelSearchCapabilities? searchCapabilities = null,
            bool isNull = false, PanelGeometryInfo? geometryInfo = null, string invalidReason = "", string? stringValue = null,
            PanelSetOccurrences? setOccurrences = null, PanelGroupSummary? groupSummary = null)
        {
            OriginalIndex = originalIndex;
            Path = path ?? string.Empty;
            TypeName = typeName ?? string.Empty;
            _displayValue = displayValue ?? string.Empty;
            _groupSummary = groupSummary;
            NumericValue = numericValue;
            IsNull = isNull;
            Validity = validity;
            GeometryInfo = geometryInfo;
            InvalidReason = invalidReason;
            StringValue = stringValue;
            SetOccurrences = setOccurrences;
            TypeIdentityAvailable = typeIdentityAvailable;
            SearchCapabilities = searchCapabilities ?? InferCapabilities(numericValue, geometryInfo);
        }

        public int OriginalIndex { get; }
        public string Path { get; }
        public string TypeName { get; }
        private readonly string _displayValue;
        private readonly PanelGroupSummary? _groupSummary;
        public string DisplayValue => _groupSummary?.Format() ?? _displayValue;
        public double? NumericValue { get; }
        public bool IsNull { get; }
        /// <summary>True only when validity was verified. Use Validity to distinguish invalid from unverified.</summary>
        public bool IsValid => Validity == PanelValidity.VerifiedValid;
        public PanelValidity Validity { get; }
        /// <summary>False if the original filter identity could not be determined without delegated metadata.</summary>
        public bool TypeIdentityAvailable { get; }
        public PanelSearchCapabilities SearchCapabilities { get; }
        public PanelGeometryInfo? GeometryInfo { get; }
        public string InvalidReason { get; }
        /// <summary>The exact captured string scalar, not an arbitrary goo's display summary.</summary>
        public string? StringValue { get; }
        public PanelSetOccurrences? SetOccurrences { get; }

        private static PanelSearchCapabilities InferCapabilities(double? numeric, PanelGeometryInfo? geometry)
        {
            var result = numeric.HasValue ? PanelSearchCapabilities.Value : PanelSearchCapabilities.None;
            if (geometry != null)
            {
                if (PanelFilterValue.MatchesGeometry(PanelSearchMode.Length, geometry)) result |= PanelSearchCapabilities.Length;
                if (PanelFilterValue.MatchesGeometry(PanelSearchMode.Area, geometry)) result |= PanelSearchCapabilities.Area;
                if (PanelFilterValue.MatchesGeometry(PanelSearchMode.Volume, geometry)) result |= PanelSearchCapabilities.Volume;
            }
            return result;
        }
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
        private readonly IReadOnlyList<PanelSourceAddress>? _sourceAddresses;
        public static PanelSnapshot Empty { get; } =
            new PanelSnapshot(Array.Empty<PanelRow>(), Array.Empty<PanelBranch>());

        public PanelSnapshot(IReadOnlyList<PanelRow> rows, IReadOnlyList<PanelBranch> branches,
            PanelUnitConversion unitConversion = PanelUnitConversion.None, IReadOnlyList<PanelSourceAddress>? sourceAddresses = null)
            : this(rows, branches, unitConversion, sourceAddresses, 0) { }

        public PanelSnapshot(IReadOnlyList<PanelRow> rows, IReadOnlyList<PanelBranch> branches,
            PanelUnitConversion unitConversion, IReadOnlyList<PanelSourceAddress>? sourceAddresses, int inspectionVisitCount)
            : this(rows, branches, unitConversion, sourceAddresses, inspectionVisitCount, 0, 0) { }

        public PanelSnapshot(IReadOnlyList<PanelRow> rows, IReadOnlyList<PanelBranch> branches,
            PanelUnitConversion unitConversion, IReadOnlyList<PanelSourceAddress>? sourceAddresses, int inspectionVisitCount,
            int retainedGroupSummaryBytes, int cachedGroupSummaryCount)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            if (branches == null) throw new ArgumentNullException(nameof(branches));
            if (sourceAddresses != null)
            {
                if (sourceAddresses.Count != rows.Count) throw new ArgumentException("Source addresses must match the rows.", nameof(sourceAddresses));
                _sourceAddresses = Array.AsReadOnly(new List<PanelSourceAddress>(sourceAddresses).ToArray());
            }
            var rowCopy = new PanelRow[rows.Count];
            var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            bool hasMeasurableGeometry = false;
            var capabilities = PanelSearchCapabilities.None;
            int unverifiedCount = 0, unavailableTypeIdentityCount = 0;
            for (var i = 0; i < rowCopy.Length; i++)
            {
                var row = rows[i] ?? throw new ArgumentException("Rows must not contain null records.", nameof(rows));
                rowCopy[i] = row;
                typeCounts.TryGetValue(row.TypeName, out var count);
                typeCounts[row.TypeName] = count + 1;
                if (row.IsValid && !row.IsNull && row.GeometryInfo?.CanMeasure == true) hasMeasurableGeometry = true;
                capabilities |= row.SearchCapabilities;
                if (row.Validity == PanelValidity.Unverified) unverifiedCount++;
                if (!row.TypeIdentityAvailable) unavailableTypeIdentityCount++;
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
            InspectionVisitCount = inspectionVisitCount;
            RetainedGroupSummaryBytes = retainedGroupSummaryBytes;
            CachedGroupSummaryCount = cachedGroupSummaryCount;
            HasMeasurableGeometry = hasMeasurableGeometry;
            SearchCapabilities = capabilities;
            UnverifiedCount = unverifiedCount;
            UnavailableTypeIdentityCount = unavailableTypeIdentityCount;
            Rows = Array.AsReadOnly(rowCopy);
            Branches = Array.AsReadOnly(branchCopy);
            TypeCounts = new ReadOnlyDictionary<string, int>(typeCounts);
            BranchItemCounts = new ReadOnlyDictionary<string, int>(branchCounts);
        }

        public PanelUnitConversion UnitConversion { get; }
        /// <summary>Whether any valid captured row can require a deferred geometry measurement.</summary>
        public bool HasMeasurableGeometry { get; }
        /// <summary>Union captured from transformed, unfiltered primary D; it does not change the saved query.</summary>
        public PanelSearchCapabilities SearchCapabilities { get; }
        /// <summary>Input-level diagnostics before any query filter or set projection.</summary>
        public int UnverifiedCount { get; }
        public int UnavailableTypeIdentityCount { get; }
        /// <summary>Bounded capture graph visits, for diagnostics and performance verification.</summary>
        public int InspectionVisitCount { get; }
        /// <summary>Estimated retained formatted group-text bytes and identity-cache entries for this capture.</summary>
        public int RetainedGroupSummaryBytes { get; }
        public int CachedGroupSummaryCount { get; }
        /// <summary>Reports choices for this input only. A caller must retain an already saved unavailable mode.</summary>
        public bool SupportsSearchMode(PanelSearchMode mode) => mode switch
        {
            PanelSearchMode.Text or PanelSearchMode.Regex or PanelSearchMode.ListLength => true,
            PanelSearchMode.Value => (SearchCapabilities & PanelSearchCapabilities.Value) != 0,
            PanelSearchMode.Length => (SearchCapabilities & PanelSearchCapabilities.Length) != 0,
            PanelSearchMode.Area => (SearchCapabilities & PanelSearchCapabilities.Area) != 0,
            PanelSearchMode.Volume => (SearchCapabilities & PanelSearchCapabilities.Volume) != 0,
            _ => false
        };
        public IReadOnlyList<PanelRow> Rows { get; }
        public IReadOnlyList<PanelBranch> Branches { get; }
        /// <summary>Source type counts for populating filters without another full-tree scan.</summary>
        public IReadOnlyDictionary<string, int> TypeCounts { get; }
        public IReadOnlyDictionary<string, int> BranchItemCounts { get; }
        public PanelSourceAddress SourceAddressAt(int position) => _sourceAddresses?[position] ??
            new PanelSourceAddress(Rows[position].Path, Rows[position].OriginalIndex);
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
        /// <summary>Inspect the selected numeric metric without applying a search predicate.</summary>
        public bool DisplayFilterValue { get; set; }
        /// <summary>Create a set of the current formatted values after filtering.</summary>
        public bool CreateSet { get; set; }
        /// <summary>Presentation precision for text/regex matching; numeric predicates retain full precision.</summary>
        public int DecimalPlaces { get; set; } = PanelUnits.AutomaticDecimals;
        public string? TypeName { get; set; }
        public string? BranchPath { get; set; }
        public bool BranchPrefix { get; set; }
        public bool OnlyInvalid { get; set; }
        /// <summary>Value modes sort within each branch while preserving the snapshot's branch order.</summary>
        public PanelSortMode SortMode { get; set; } = PanelSortMode.SourceOrder;

        public PanelQueryOptions Clone() => new PanelQueryOptions
        {
            Search = Search,
            SearchMode = SearchMode,
            DisplayFilterValue = DisplayFilterValue,
            CreateSet = CreateSet,
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
            IReadOnlyList<PanelValueFrequency> uniqueValues, int scalarCount, int unverifiedCount,
            PanelSourceStatusCounts filteredSourceStatuses)
        {
            ItemCount = itemCount;
            BranchCount = branchCount;
            NumericCount = numericCount;
            NullCount = nullCount;
            InvalidCount = invalidCount;
            UnverifiedCount = unverifiedCount;
            FilteredSourceStatuses = filteredSourceStatuses;
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
        /// <summary>Unverified projected rows, separate from verified invalid rows.</summary>
        public int UnverifiedCount { get; }
        /// <summary>Filtered input occurrences before Create set, independent of representatives.</summary>
        public PanelSourceStatusCounts FilteredSourceStatuses { get; }
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
        private readonly PanelResultPositionIndex _viewIndices;
        private readonly HashSet<string> _branchPaths;

        internal PanelQueryResult(PanelSnapshot snapshot, int[] rowPositions, PanelResultPositionIndex viewIndices,
            PanelStatistics statistics, List<string> orderedBranches, HashSet<string> branchPaths,
            PanelViewIndex viewIndex, PanelDisplaySetResult? displaySet = null)
        {
            Snapshot = snapshot;
            RowPositions = Array.AsReadOnly(rowPositions);
            _viewIndices = viewIndices;
            _branchPaths = branchPaths;
            Statistics = statistics;
            BranchPaths = orderedBranches.AsReadOnly();
            ViewIndex = viewIndex;
            DisplaySet = displaySet;
        }

        public PanelSnapshot Snapshot { get; }
        /// <summary>Positions in Snapshot.Rows, retaining each row's original tree address.</summary>
        public IReadOnlyList<int> RowPositions { get; }
        /// <summary>Finds a visible source position in this result's view order.</summary>
        public bool TryGetViewIndex(int position, out int index) => _viewIndices.TryGet(position, out index);
        public PanelStatistics Statistics { get; }
        /// <summary>Matching paths, including empty branches when no item filter is active.</summary>
        public IReadOnlyList<string> BranchPaths { get; }
        /// <summary>Whether a path, including a retained empty path, is present in this result.</summary>
        public bool ContainsBranchPath(string path) => _branchPaths.Contains(path);
        public PanelViewIndex ViewIndex { get; }
        public PanelDisplaySetResult? DisplaySet { get; }
        public PanelSetOccurrences? SetOccurrencesAt(int position) =>
            DisplaySet != null && DisplaySet.Occurrences.TryGetValue(position, out var occurrences) ? occurrences : Snapshot.Rows[position].SetOccurrences;
    }
}
