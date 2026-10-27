using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;

namespace BetterGhPanel.UI
{
    internal enum CanvasMenuKind { None, Type, Sort, Branch, Units, SearchMode, Comparison, Preview, Statistics }
    internal enum PanelPreviewMode { Selected, FilteredResults, Off }
    internal enum CanvasComparison { GreaterThan, AtLeast, LessThan, AtMost, Equal, NotEqual }
    internal enum CanvasDecimalChoice { Automatic, Custom }

    internal readonly struct CanvasChoice<T>
    {
        internal CanvasChoice(T value, string label) { Value = value; Label = label; }
        internal T Value { get; }
        internal string Label { get; }
    }

    internal readonly struct CanvasPreviewChoice
    {
        internal CanvasPreviewChoice(PanelPreviewMode? mode, string label) { Mode = mode; Label = label; }
        internal PanelPreviewMode? Mode { get; }
        internal bool IsItemOrder => !Mode.HasValue;
        internal string Label { get; }
    }

    internal readonly struct CanvasUnitChoice
    {
        internal CanvasUnitChoice(PanelUnitConversion conversion) { Conversion = conversion; Decimals = null; }
        internal CanvasUnitChoice(CanvasDecimalChoice decimals) { Conversion = null; Decimals = decimals; }
        internal PanelUnitConversion? Conversion { get; }
        internal CanvasDecimalChoice? Decimals { get; }
    }

    internal readonly struct CanvasComparisonChoice
    {
        internal CanvasComparisonChoice(CanvasComparison? comparison, string label) { Comparison = comparison; Label = label; }
        internal CanvasComparison? Comparison { get; }
        internal bool IsDisplayValue => !Comparison.HasValue;
        internal string Label { get; }
    }

    // One owner for menu lifetime, snapshot validity, layout, and paging.
    internal sealed class CanvasMenu
    {
        internal const string ItemOrderDescription = "Show zero-based item indices within each branch, after tree tools and before filtering or sorting. " +
            "Each branch starts at 0; filters and selection preserve the original indices. Points use their locations; other geometry uses bounding-box centers. " +
            "Labels follow the Preview scope and remain active in a minimized panel. Overlapping labels are hidden, with selected rows taking priority; zoom in or select fewer items to separate them.";
        private static readonly CanvasChoice<PanelSortMode>[] SortChoices =
        {
            new CanvasChoice<PanelSortMode>(PanelSortMode.SourceOrder, "Source order"),
            new CanvasChoice<PanelSortMode>(PanelSortMode.ValueAscending, "Value ascending"),
            new CanvasChoice<PanelSortMode>(PanelSortMode.ValueDescending, "Value descending")
        };
        private static readonly CanvasPreviewChoice[] PreviewChoices =
        {
            new CanvasPreviewChoice(PanelPreviewMode.Selected, "Selected"),
            new CanvasPreviewChoice(PanelPreviewMode.FilteredResults, "Filtered results"),
            new CanvasPreviewChoice(PanelPreviewMode.Off, "Off"),
            new CanvasPreviewChoice(null, "Show item order")
        };
        private static readonly CanvasChoice<PanelSearchMode>[] SearchChoices =
        {
            new CanvasChoice<PanelSearchMode>(PanelSearchMode.Text, "Text search"),
            new CanvasChoice<PanelSearchMode>(PanelSearchMode.Regex, "Text · regular expression"),
            new CanvasChoice<PanelSearchMode>(PanelSearchMode.Value, "Numeric value"),
            new CanvasChoice<PanelSearchMode>(PanelSearchMode.Length, "Geometry · length"),
            new CanvasChoice<PanelSearchMode>(PanelSearchMode.Area, "Geometry · area"),
            new CanvasChoice<PanelSearchMode>(PanelSearchMode.Volume, "Geometry · volume"),
            new CanvasChoice<PanelSearchMode>(PanelSearchMode.ListLength, "List length · per branch")
        };
        private static readonly CanvasComparisonChoice[] ComparisonChoices =
        {
            new CanvasComparisonChoice(CanvasComparison.GreaterThan, "Greater than  >"),
            new CanvasComparisonChoice(CanvasComparison.AtLeast, "At least  ≥"),
            new CanvasComparisonChoice(CanvasComparison.LessThan, "Less than  <"),
            new CanvasComparisonChoice(CanvasComparison.AtMost, "At most  ≤"),
            new CanvasComparisonChoice(CanvasComparison.Equal, "Equal to  ="),
            new CanvasComparisonChoice(CanvasComparison.NotEqual, "Not equal to  ≠"),
            new CanvasComparisonChoice(null, "Display value")
        };
        private static readonly CanvasUnitChoice[] UnitChoices =
        {
            new CanvasUnitChoice(PanelUnitConversion.None),
            new CanvasUnitChoice(PanelUnitConversion.MillimetersToMeters),
            new CanvasUnitChoice(PanelUnitConversion.MetersToMillimeters),
            new CanvasUnitChoice(PanelUnitConversion.SquareMillimetersToSquareMeters),
            new CanvasUnitChoice(PanelUnitConversion.SquareMetersToSquareMillimeters),
            new CanvasUnitChoice(PanelUnitConversion.RadiansToDegrees),
            new CanvasUnitChoice(PanelUnitConversion.DegreesToRadians),
            new CanvasUnitChoice(PanelUnitConversion.CubicMillimetersToCubicMeters),
            new CanvasUnitChoice(PanelUnitConversion.CubicMetersToCubicMillimeters),
            new CanvasUnitChoice(CanvasDecimalChoice.Automatic),
            new CanvasUnitChoice(CanvasDecimalChoice.Custom)
        };
        private PanelSnapshot _snapshot = PanelSnapshot.Empty;
        private PanelQueryResult? _statisticsResult;
        private string[] _types = Array.Empty<string>();
        private CanvasChoice<PanelSearchMode>[] _searchChoices = Array.Empty<CanvasChoice<PanelSearchMode>>();
        private CanvasMenuKind _paintedKind;
        private RectangleF _paintedBounds;
        private int _paintedOffset = -1, _paintedVisible = -1;
        private bool[] _paintedRows = Array.Empty<bool>();
        private bool _paintedPrevious, _paintedNext;
        internal CanvasMenuKind Kind { get; private set; }
        internal RectangleF Bounds { get; private set; }
        internal int Offset { get; private set; }
        internal int HoveredIndex { get; private set; } = -1;
        internal int Count => Kind == CanvasMenuKind.Type ? _types.Length + 2 :
            Kind == CanvasMenuKind.Sort ? SortChoices.Length : Kind == CanvasMenuKind.Branch ? _snapshot.Branches.Count + 2 :
            Kind == CanvasMenuKind.Units ? UnitChoices.Length :
            Kind == CanvasMenuKind.SearchMode ? _searchChoices.Length : Kind == CanvasMenuKind.Comparison ? ComparisonChoices.Length :
            Kind == CanvasMenuKind.Preview ? PreviewChoices.Length :
            Kind == CanvasMenuKind.Statistics && _statisticsResult != null ? CanvasStatistics.OverviewRows + _statisticsResult.Statistics.UniqueValues.Count : 0;
        internal int Visible => Math.Min(Count, Math.Max(1, (int)(Bounds.Height / 20) - (Count * 20 > Bounds.Height ? 1 : 0)));
        internal bool HasPaintedTargets => Kind != CanvasMenuKind.None && _paintedKind == Kind &&
            _paintedBounds == Bounds && _paintedOffset == Offset && _paintedVisible == Visible &&
            (_paintedRows.Any(painted => painted) || _paintedPrevious || _paintedNext);

        // Hit testing is valid only for the page and layout the user actually saw.
        internal void BeginPaint(bool preserve = false)
        {
            if (preserve && _paintedKind == Kind && _paintedBounds == Bounds &&
                _paintedOffset == Offset && _paintedVisible == Visible) return;
            _paintedKind = Kind;
            _paintedBounds = Bounds;
            _paintedOffset = Offset;
            _paintedVisible = Visible;
            _paintedRows = new bool[Visible];
            _paintedPrevious = _paintedNext = false;
        }

        internal void MarkRowPainted(int row) { if (row >= 0 && row < _paintedRows.Length) _paintedRows[row] = true; }
        internal void MarkPagePainted(bool previous) { if (previous) _paintedPrevious = true; else _paintedNext = true; }
        internal void InvalidatePaint() { _paintedKind = CanvasMenuKind.None; }

        // Policy tests can simulate a complete paint without a Graphics instance.
        internal void MarkPainted()
        {
            BeginPaint();
            for (int row = 0; row < Visible; row++) MarkRowPainted(row);
            if (Count > Visible) { MarkPagePainted(true); MarkPagePainted(false); }
        }

        internal bool IsPaintedAt(PointF point)
        {
            if (!HasPaintedTargets || !Bounds.Contains(point)) return false;
            int row = (int)((point.Y - Bounds.Y) / 20);
            if (row >= 0 && row < Visible) return _paintedRows[row];
            return Count > Visible && row == Visible &&
                (point.X < Bounds.X + Bounds.Width / 2 ? _paintedPrevious : _paintedNext);
        }

        internal static string TypeHint(PanelQueryResult result, PanelQueryOptions options)
        {
            if (options.OnlyInvalid) return "Invalid";
            if (options.TypeName != null) return ShortTypeName(options.TypeName);
            var types = TypeCounts(result);
            if (types.Count == 0) return "No data";
            var names = OrderedTypes(types)
                .Take(2).Select(pair => ShortTypeName(pair.Key));
            return string.Join("/", names) + (types.Count > 2 ? " +" + (types.Count - 2) : "");
        }

        internal static string TypeDetails(PanelQueryResult result) =>
            string.Join(", ", OrderedTypes(TypeCounts(result)).Select(pair => $"{pair.Key}: {pair.Value:N0}"));

        private static IReadOnlyDictionary<string, int> TypeCounts(PanelQueryResult result) =>
            result.Statistics.TypeCounts.Count > 0 ? result.Statistics.TypeCounts : result.Snapshot.TypeCounts;

        private static IOrderedEnumerable<KeyValuePair<string, int>> OrderedTypes(IReadOnlyDictionary<string, int> types) =>
            types.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal);

        // Abbreviations are presentation only; filters and saved settings keep the full goo type name.
        internal static string ShortTypeName(string name) => name switch
        {
            "Number" => "Num",
            "Integer" => "Int",
            "Boolean" => "Bool",
            "Point" => "Pt",
            "Vector" => "Vec",
            "Plane" => "Pln",
            "Line" => "Ln",
            "Curve" => "Crv",
            "Circle" => "Cir",
            "Rectangle" => "Rect",
            "Surface" => "Srf",
            "Extrusion" => "Extr",
            "Mesh Face" => "Face",
            "Point Cloud" => "Cloud",
            "Transform" => "Xform",
            "Geometry Group" => "Geo group",
            "Colour" => "Col",
            "Domain" => "Dom",
            "Domain²" => "Dom²",
            "Complex Number" => "Cplx",
            // Mesh, Brep, SubD, Arc, Box, annotations, and third-party goo retain their own names.
            _ => name
        };

        internal void Close()
        {
            Kind = CanvasMenuKind.None;
            InvalidatePaint();
            HoveredIndex = -1;
            _statisticsResult = null;
            _snapshot = PanelSnapshot.Empty;
            _types = Array.Empty<string>();
            _searchChoices = Array.Empty<CanvasChoice<PanelSearchMode>>();
        }
        internal bool IsCurrent(PanelSnapshot snapshot) => ReferenceEquals(_snapshot, snapshot);
        internal bool IsCurrent(PanelQueryResult result) => Kind != CanvasMenuKind.Statistics || ReferenceEquals(_statisticsResult, result);

        // Null retains the legacy all-modes menu until the caller supplies input capabilities.
        internal void Toggle(CanvasMenuKind kind, PanelSnapshot snapshot, PanelQueryOptions options, PanelQueryResult? result = null,
            IEnumerable<PanelSearchMode>? supportedSearchModes = null)
        {
            Kind = Kind == kind ? CanvasMenuKind.None : kind;
            InvalidatePaint();
            _snapshot = snapshot;
            _statisticsResult = Kind == CanvasMenuKind.Statistics && ReferenceEquals(result?.Snapshot, snapshot) ? result : null;
            if (Kind == CanvasMenuKind.Statistics && _statisticsResult == null) Kind = CanvasMenuKind.None;
            Offset = 0;
            HoveredIndex = -1;
            if (Kind == CanvasMenuKind.Type)
            {
                var keys = snapshot.TypeCounts.Keys;
                var active = options.TypeName;
                _types = keys.Concat(active != null && !keys.Contains(active) ? new[] { active } : Array.Empty<string>())
                    .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            }
            if (Kind == CanvasMenuKind.SearchMode)
            {
                var supported = supportedSearchModes == null ? null : new HashSet<PanelSearchMode>(supportedSearchModes);
                _searchChoices = SearchChoices.Where(choice => supported == null || supported.Contains(choice.Value) || choice.Value == options.SearchMode).ToArray();
                // A saved mode must remain visible even if a future caller cannot advertise it.
                if (!_searchChoices.Any(choice => choice.Value == options.SearchMode))
                    _searchChoices = _searchChoices.Concat(new[] { new CanvasChoice<PanelSearchMode>(options.SearchMode, options.SearchMode.ToString()) }).ToArray();
            }
            if (Kind == CanvasMenuKind.None) Close();
        }

        internal void Layout(RectangleF panel, RectangleF type, RectangleF sort, RectangleF branch, RectangleF units = default, RectangleF search = default, RectangleF preview = default, RectangleF statistics = default)
        {
            if (Kind == CanvasMenuKind.None) return;
            var anchor = Kind == CanvasMenuKind.Statistics ? statistics : Kind == CanvasMenuKind.Preview ? preview : Kind == CanvasMenuKind.Type ? type : Kind == CanvasMenuKind.Sort ? sort : Kind == CanvasMenuKind.Units ? units :
                Kind == CanvasMenuKind.SearchMode || Kind == CanvasMenuKind.Comparison ? search : branch;
            float width = Math.Min(panel.Width - 28, Kind == CanvasMenuKind.Statistics ? 360 : Kind == CanvasMenuKind.Sort ? 138 :
                Kind == CanvasMenuKind.Units || Kind == CanvasMenuKind.Preview ? 156 : Kind == CanvasMenuKind.Type || Kind == CanvasMenuKind.SearchMode || Kind == CanvasMenuKind.Comparison ? 180 : 235);
            if (Kind == CanvasMenuKind.Units || Kind == CanvasMenuKind.Statistics)
            {
                int rows = Math.Max(2, Math.Min(Count, (int)((anchor.Top - panel.Top - 31) / 20)));
                int height = rows * 20;
                float right = Kind == CanvasMenuKind.Statistics ? panel.Right - 14 : Math.Min(anchor.Right, panel.Right - 14);
                right = Math.Max(panel.Left + 14 + width, right);
                Bounds = new RectangleF(right - width, anchor.Top - height - 3, width, height);
                Offset = Math.Max(0, Math.Min(Math.Max(0, Count - Visible), Offset));
                return;
            }
            int visible = Math.Max(2, Math.Min(8, (int)((panel.Bottom - 9 - anchor.Bottom - 19) / 20)));
            Bounds = new RectangleF(Math.Max(panel.Left + 14, Math.Min(anchor.X, panel.Right - width - 14)), anchor.Bottom + 2, width,
                Math.Min(visible, Count) * 20 + (Count > visible ? 20 : 0));
            Offset = Math.Max(0, Math.Min(Math.Max(0, Count - Visible), Offset));
        }

        internal bool TrySelect(PointF point, out int index)
        {
            index = -1;
            if (!Bounds.Contains(point)) { Close(); return false; }
            if (!IsPaintedAt(point)) return false;
            int row = (int)((point.Y - Bounds.Y) / 20);
            if (Count > Visible && row >= Visible)
            {
                Offset = Math.Max(0, Math.Min(Count - Visible,
                    Offset + (point.X < Bounds.X + Bounds.Width / 2 ? -Visible : Visible)));
                HoveredIndex = -1;
                return false;
            }
            index = Offset + row;
            return index >= 0 && index < Count;
        }

        internal bool Hover(PointF point)
        {
            int row = (int)((point.Y - Bounds.Y) / 20);
            int index = IsPaintedAt(point) && row >= 0 && row < Visible ? Offset + row : -1;
            if (HoveredIndex == index) return false;
            HoveredIndex = index;
            return true;
        }

        internal bool IsSelected(int index, PanelQueryOptions options, PanelUnitConversion conversion, int decimals, PanelPreviewMode preview = PanelPreviewMode.Selected, bool showItemOrder = false)
        {
            if (Kind == CanvasMenuKind.Statistics) return false;
            if (Kind == CanvasMenuKind.Preview)
            {
                var choice = PreviewChoiceAt(index);
                return choice.IsItemOrder ? showItemOrder : choice.Mode == preview;
            }
            if (Kind == CanvasMenuKind.Units)
            {
                var choice = UnitChoiceAt(index);
                return choice.Conversion.HasValue ? choice.Conversion == conversion :
                    choice.Decimals == CanvasDecimalChoice.Automatic ? decimals < 0 : decimals >= 0;
            }
            if (Kind == CanvasMenuKind.Sort) return SortModeAt(index) == options.SortMode;
            if (Kind == CanvasMenuKind.SearchMode) return SearchModeAt(index) == options.SearchMode;
            if (Kind == CanvasMenuKind.Comparison)
            {
                var choice = ComparisonChoiceAt(index);
                if (choice.IsDisplayValue) return options.DisplayFilterValue;
                if (options.DisplayFilterValue) return false;
                string comparison = ComparisonPrefix(options.Search);
                if (comparison.Length == 0 && IsComparisonMode(options.SearchMode) && IsFiniteNumber(options.Search))
                    comparison = "=";
                return ComparisonOperator(choice.Comparison!.Value) == comparison;
            }
            if (Kind == CanvasMenuKind.Type) return index == 0 ? options.TypeName == null && !options.OnlyInvalid :
                index == 1 ? options.OnlyInvalid : TypeNameAt(index) == options.TypeName;
            return index == 0 ? options.BranchPath == null : index >= 2 && BranchPathAt(index) == options.BranchPath;
        }

        internal string TypeNameAt(int index) => _types[index - 2];
        internal string BranchPathAt(int index) => _snapshot.Branches[index - 2].Path;
        internal PanelSortMode SortModeAt(int index) => SortChoices[index].Value;
        internal PanelSearchMode SearchModeAt(int index) => _searchChoices[index].Value;
        internal CanvasPreviewChoice PreviewChoiceAt(int index) => PreviewChoices[index];
        internal CanvasUnitChoice UnitChoiceAt(int index) => UnitChoices[index];
        internal CanvasComparisonChoice ComparisonChoiceAt(int index) => ComparisonChoices[index];
        internal bool IsSectionStart(int index) =>
            Kind == CanvasMenuKind.Units && UnitChoiceAt(index).Decimals == CanvasDecimalChoice.Automatic ||
            Kind == CanvasMenuKind.Statistics && index == CanvasStatistics.OverviewRows ||
            Kind == CanvasMenuKind.Preview && PreviewChoiceAt(index).IsItemOrder ||
            Kind == CanvasMenuKind.Comparison && ComparisonChoiceAt(index).IsDisplayValue;
        internal static string SearchModeLabel(PanelSearchMode mode) =>
            SearchChoices.FirstOrDefault(choice => choice.Value == mode).Label ?? mode.ToString();
        internal static string PreviewLabel(PanelPreviewMode mode) =>
            PreviewChoices.FirstOrDefault(choice => choice.Mode == mode).Label ?? mode.ToString();
        internal static bool IsComparisonMode(PanelSearchMode mode) =>
            mode == PanelSearchMode.Value || mode == PanelSearchMode.Length || mode == PanelSearchMode.Area ||
            mode == PanelSearchMode.Volume || mode == PanelSearchMode.ListLength;
        internal static string SearchHint(PanelQueryOptions options)
        {
            string prefix = options.SearchMode == PanelSearchMode.Regex ? ".* " :
                options.SearchMode == PanelSearchMode.Text ? "" :
                options.SearchMode == PanelSearchMode.ListLength ? "List " : options.SearchMode + " ";
            if (options.DisplayFilterValue) return prefix + "· display";
            return prefix + (string.IsNullOrEmpty(options.Search) ?
                options.SearchMode == PanelSearchMode.Text ? "Search…" : options.SearchMode == PanelSearchMode.Regex ? "Pattern…" : "filter…" : options.Search);
        }

        internal static string ComparisonOperator(CanvasComparison comparison) => comparison switch
        {
            CanvasComparison.GreaterThan => ">",
            CanvasComparison.AtLeast => ">=",
            CanvasComparison.LessThan => "<",
            CanvasComparison.AtMost => "<=",
            CanvasComparison.Equal => "=",
            CanvasComparison.NotEqual => "!=",
            _ => throw new ArgumentOutOfRangeException(nameof(comparison))
        };

        internal static string WithComparison(string search, CanvasComparison comparison)
        {
            string value = (search ?? "").Trim();
            string prefix = ComparisonPrefix(value);
            if (prefix.Length > 0) value = value.Substring(value.StartsWith("==", StringComparison.Ordinal) ? 2 : prefix.Length).Trim();
            // Switching from text or regex starts with an empty threshold; an
            // existing numeric threshold survives an operator change.
            if (!IsFiniteNumber(value)) value = "";
            return ComparisonOperator(comparison) + " " + value;
        }

        internal static string ApplyComparison(string text, string comparison)
        {
            string value = (text ?? "").Trim();
            // The editor initially shows only the threshold. Accept an explicit
            // operator too, so pasted comparisons mean exactly what they say.
            return value.Length == 0 ? string.Empty :
                ComparisonPrefix(value).Length > 0 ? value : comparison + " " + value;
        }

        private static bool IsFiniteNumber(string value) =>
            double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) &&
            !double.IsNaN(number) && !double.IsInfinity(number);

        private static string ComparisonPrefix(string search)
        {
            string value = (search ?? "").TrimStart();
            foreach (string comparison in new[] { ">=", "<=", "!=", "==", ">", "<", "=" })
                if (value.StartsWith(comparison, StringComparison.Ordinal)) return comparison == "==" ? "=" : comparison;
            return "";
        }

        internal string Label(int index, int decimals = PanelUnits.AutomaticDecimals)
        {
            if (Kind == CanvasMenuKind.Statistics) return CanvasStatistics.Label(_statisticsResult!, index, decimals);
            if (Kind == CanvasMenuKind.Preview) return PreviewChoiceAt(index).Label;
            if (Kind == CanvasMenuKind.Units)
            {
                var choice = UnitChoiceAt(index);
                if (choice.Conversion.HasValue) return PanelUnits.Label(choice.Conversion.Value);
                if (choice.Decimals == CanvasDecimalChoice.Automatic) return "Decimals: automatic";
                return "Decimal places…" + (decimals >= 0 ? " (" + decimals + ")" : "");
            }
            if (Kind == CanvasMenuKind.Type)
            {
                if (index == 0) return "All types";
                if (index == 1) return "Invalid only";
                string name = TypeNameAt(index), hint = ShortTypeName(name);
                return hint == name ? name : hint + " · " + name;
            }
            if (Kind == CanvasMenuKind.Sort) return SortChoices[index].Label;
            if (Kind == CanvasMenuKind.SearchMode) return _searchChoices[index].Label;
            if (Kind == CanvasMenuKind.Comparison) return ComparisonChoiceAt(index).Label;
            if (index == 0) return "All branches";
            if (index == 1) return "Jump to exact path…";
            var branch = _snapshot.Branches[index - 2];
            var path = branch.Path.Length <= 150 ? branch.Path : branch.Path.Substring(0, 149) + "…";
            return path + "   ·   " + branch.ItemCount.ToString("N0");
        }

        internal string StatisticsDetails(int index, int decimals) =>
            CanvasStatistics.Label(_statisticsResult!, index, decimals, full: true);
    }
}
