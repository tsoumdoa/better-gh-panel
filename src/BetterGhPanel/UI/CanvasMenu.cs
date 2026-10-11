using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;

namespace BetterGhPanel.UI
{
    internal enum CanvasMenuKind { None, Type, Sort, Branch, Units, SearchMode, Comparison }

    // One owner for menu lifetime, snapshot validity, layout, and paging.
    internal sealed class CanvasMenu
    {
        private static readonly string[] SortLabels = { "Source order", "Value ascending", "Value descending" };
        private static readonly string[] SearchLabels = { "Text search", "Text · regular expression", "Numeric value", "Geometry · length", "Geometry · area", "Geometry · volume" };
        private static readonly string[] ComparisonLabels = { "Greater than  >", "At least  ≥", "Less than  <", "At most  ≤", "Equal to  =", "Not equal to  ≠" };
        private static readonly string[] Comparisons = { ">", ">=", "<", "<=", "=", "!=" };
        private PanelSnapshot _snapshot = PanelSnapshot.Empty;
        private string[] _types = Array.Empty<string>();
        internal CanvasMenuKind Kind { get; private set; }
        internal RectangleF Bounds { get; private set; }
        internal int Offset { get; private set; }
        internal int HoveredIndex { get; private set; } = -1;
        internal int Count => Kind == CanvasMenuKind.Type ? _types.Length + 2 :
            Kind == CanvasMenuKind.Sort ? SortLabels.Length : Kind == CanvasMenuKind.Branch ? _snapshot.Branches.Count + 2 :
            Kind == CanvasMenuKind.Units ? PanelUnits.ConversionCount + 2 :
            Kind == CanvasMenuKind.SearchMode ? SearchLabels.Length : Kind == CanvasMenuKind.Comparison ? ComparisonLabels.Length : 0;
        internal int Visible => Math.Min(Count, Math.Max(1, (int)(Bounds.Height / 20) - (Count * 20 > Bounds.Height ? 1 : 0)));

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

        internal void Close() { Kind = CanvasMenuKind.None; HoveredIndex = -1; }
        internal bool IsCurrent(PanelSnapshot snapshot) => ReferenceEquals(_snapshot, snapshot);

        internal void Toggle(CanvasMenuKind kind, PanelSnapshot snapshot, PanelQueryOptions options)
        {
            Kind = Kind == kind ? CanvasMenuKind.None : kind;
            _snapshot = snapshot;
            Offset = 0;
            HoveredIndex = -1;
            if (Kind == CanvasMenuKind.Type)
            {
                var keys = snapshot.TypeCounts.Keys;
                var active = options.TypeName;
                _types = keys.Concat(active != null && !keys.Contains(active) ? new[] { active } : Array.Empty<string>())
                    .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            }
        }

        internal void Layout(RectangleF panel, RectangleF type, RectangleF sort, RectangleF branch, RectangleF units = default, RectangleF search = default)
        {
            if (Kind == CanvasMenuKind.None) return;
            var anchor = Kind == CanvasMenuKind.Type ? type : Kind == CanvasMenuKind.Sort ? sort : Kind == CanvasMenuKind.Units ? units :
                Kind == CanvasMenuKind.SearchMode || Kind == CanvasMenuKind.Comparison ? search : branch;
            float width = Math.Min(panel.Width - 18, Kind == CanvasMenuKind.Sort ? 138 :
                Kind == CanvasMenuKind.Units ? 156 : Kind == CanvasMenuKind.Type || Kind == CanvasMenuKind.SearchMode || Kind == CanvasMenuKind.Comparison ? 180 : 235);
            if (Kind == CanvasMenuKind.Units)
            {
                int rows = Math.Max(2, Math.Min(Count, (int)((anchor.Top - panel.Top - 31) / 20)));
                int height = rows * 20;
                Bounds = new RectangleF(Math.Min(anchor.Right, panel.Right - 9) - width, anchor.Top - height - 3, width, height);
                Offset = Math.Max(0, Math.Min(Math.Max(0, Count - Visible), Offset));
                return;
            }
            int visible = Math.Max(2, Math.Min(8, (int)((panel.Bottom - 9 - anchor.Bottom - 19) / 20)));
            Bounds = new RectangleF(Math.Min(anchor.X, panel.Right - width - 9), anchor.Bottom + 2, width,
                Math.Min(visible, Count) * 20 + (Count > visible ? 20 : 0));
            Offset = Math.Max(0, Math.Min(Math.Max(0, Count - Visible), Offset));
        }

        internal bool TrySelect(PointF point, out int index)
        {
            index = -1;
            if (!Bounds.Contains(point)) { Close(); return false; }
            int row = (int)((point.Y - Bounds.Y) / 20);
            if (Count > Visible && row >= Visible)
            {
                Offset = Math.Max(0, Math.Min(Count - Visible,
                    Offset + (point.X < Bounds.X + Bounds.Width / 2 ? -Visible : Visible)));
                return false;
            }
            index = Offset + row;
            return index >= 0 && index < Count;
        }

        internal bool Hover(PointF point)
        {
            int row = (int)((point.Y - Bounds.Y) / 20);
            int index = Kind != CanvasMenuKind.None && Bounds.Contains(point) && row < Visible ? Offset + row : -1;
            if (HoveredIndex == index) return false;
            HoveredIndex = index;
            return true;
        }

        internal bool IsSelected(int index, PanelQueryOptions options, PanelUnitConversion conversion, int decimals)
        {
            if (Kind == CanvasMenuKind.Units)
                return index < PanelUnits.ConversionCount ? index == (int)conversion :
                    index == PanelUnits.ConversionCount ? decimals < 0 : decimals >= 0;
            if (Kind == CanvasMenuKind.Sort) return index == (int)options.SortMode;
            if (Kind == CanvasMenuKind.SearchMode) return index == (int)options.SearchMode;
            if (Kind == CanvasMenuKind.Comparison) return ComparisonAt(index) == ComparisonPrefix(options.Search);
            if (Kind == CanvasMenuKind.Type) return index == 0 ? options.TypeName == null && !options.OnlyInvalid :
                index == 1 ? options.OnlyInvalid : TypeNameAt(index) == options.TypeName;
            return index == 0 ? options.BranchPath == null : index >= 2 && BranchPathAt(index) == options.BranchPath;
        }

        internal string TypeNameAt(int index) => _types[index - 2];
        internal string BranchPathAt(int index) => _snapshot.Branches[index - 2].Path;
        internal static string ComparisonAt(int index) => Comparisons[index];
        internal static string SearchModeLabel(PanelSearchMode mode) => SearchLabels[(int)mode];
        internal static string SearchHint(PanelQueryOptions options)
        {
            string prefix = options.SearchMode == PanelSearchMode.Regex ? ".* " :
                options.SearchMode == PanelSearchMode.Text ? "" : options.SearchMode + " ";
            return prefix + (string.IsNullOrEmpty(options.Search) ?
                options.SearchMode == PanelSearchMode.Text ? "Search…" : options.SearchMode == PanelSearchMode.Regex ? "Pattern…" : "filter…" : options.Search);
        }

        internal static string WithComparison(string search, int index)
        {
            string value = (search ?? "").Trim();
            string prefix = ComparisonPrefix(value);
            if (prefix.Length > 0) value = value.Substring(value.StartsWith("==", StringComparison.Ordinal) ? 2 : prefix.Length).Trim();
            // Switching from text or regex starts with an empty threshold; an
            // existing numeric threshold survives an operator change.
            if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) ||
                double.IsNaN(number) || double.IsInfinity(number)) value = "";
            return ComparisonAt(index) + " " + value;
        }

        private static string ComparisonPrefix(string search)
        {
            string value = (search ?? "").TrimStart();
            foreach (string comparison in new[] { ">=", "<=", "!=", "==", ">", "<", "=" })
                if (value.StartsWith(comparison, StringComparison.Ordinal)) return comparison == "==" ? "=" : comparison;
            return "";
        }

        internal string Label(int index, int decimals = PanelUnits.AutomaticDecimals)
        {
            if (Kind == CanvasMenuKind.Units)
            {
                if (index < PanelUnits.ConversionCount)
                    return PanelUnits.Label((PanelUnitConversion)index);
                if (index == PanelUnits.ConversionCount) return "Decimals: automatic";
                return "Decimal places…" + (decimals >= 0 ? " (" + decimals + ")" : "");
            }
            if (Kind == CanvasMenuKind.Type)
            {
                if (index == 0) return "All types";
                if (index == 1) return "Invalid only";
                string name = TypeNameAt(index), hint = ShortTypeName(name);
                return hint == name ? name : hint + " · " + name;
            }
            if (Kind == CanvasMenuKind.Sort) return SortLabels[index];
            if (Kind == CanvasMenuKind.SearchMode) return SearchLabels[index];
            if (Kind == CanvasMenuKind.Comparison) return ComparisonLabels[index];
            if (index == 0) return "All branches";
            if (index == 1) return "Jump to exact path…";
            var branch = _snapshot.Branches[index - 2];
            var path = branch.Path.Length <= 150 ? branch.Path : branch.Path.Substring(0, 149) + "…";
            return path + "   ·   " + branch.ItemCount.ToString("N0");
        }
    }
}
