using System;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;

namespace BetterGhPanel.UI
{
    internal enum CanvasMenuKind { None, Type, Sort, Branch, Units }

    // One owner for menu lifetime, snapshot validity, layout, and paging.
    internal sealed class CanvasMenu
    {
        private static readonly string[] SortLabels = { "Source order", "Value ascending", "Value descending" };
        private PanelSnapshot _snapshot = PanelSnapshot.Empty;
        private string[] _types = Array.Empty<string>();
        internal CanvasMenuKind Kind { get; private set; }
        internal RectangleF Bounds { get; private set; }
        internal int Offset { get; private set; }
        internal int Count => Kind == CanvasMenuKind.Type ? _types.Length + 2 :
            Kind == CanvasMenuKind.Sort ? SortLabels.Length : Kind == CanvasMenuKind.Branch ? _snapshot.Branches.Count + 2 :
            Kind == CanvasMenuKind.Units ? PanelUnits.ConversionCount + 2 : 0;
        internal int Visible => Math.Min(Count, Math.Max(2, (int)(Bounds.Height / 20) - (Count * 20 > Bounds.Height ? 1 : 0)));

        internal static string TypeHint(PanelQueryResult result, PanelQueryOptions options)
        {
            if (options.OnlyInvalid) return "Invalid";
            if (options.TypeName != null) return ShortTypeName(options.TypeName);
            var types = result.Statistics.TypeCounts.Count > 0 ? result.Statistics.TypeCounts : result.Snapshot.TypeCounts;
            if (types.Count == 0) return "No data";
            var names = types.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(2).Select(pair => ShortTypeName(pair.Key));
            return string.Join("/", names) + (types.Count > 2 ? " +" + (types.Count - 2) : "");
        }

        internal static string TypeDetails(PanelQueryResult result)
        {
            var types = result.Statistics.TypeCounts.Count > 0 ? result.Statistics.TypeCounts : result.Snapshot.TypeCounts;
            return string.Join(", ", types.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}: {pair.Value:N0}"));
        }

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

        internal void Close() => Kind = CanvasMenuKind.None;
        internal bool IsCurrent(PanelSnapshot snapshot) => ReferenceEquals(_snapshot, snapshot);

        internal void Toggle(CanvasMenuKind kind, PanelSnapshot snapshot, PanelQueryOptions options)
        {
            Kind = Kind == kind ? CanvasMenuKind.None : kind;
            _snapshot = snapshot;
            Offset = 0;
            if (Kind == CanvasMenuKind.Type)
            {
                var keys = snapshot.TypeCounts.Keys;
                var active = options.TypeName;
                _types = keys.Concat(active != null && !keys.Contains(active) ? new[] { active } : Array.Empty<string>())
                    .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            }
        }

        internal void Layout(RectangleF panel, RectangleF type, RectangleF sort, RectangleF branch, RectangleF units = default)
        {
            if (Kind == CanvasMenuKind.None) return;
            var anchor = Kind == CanvasMenuKind.Type ? type : Kind == CanvasMenuKind.Sort ? sort : Kind == CanvasMenuKind.Units ? units : branch;
            float width = Math.Min(panel.Width - 18, Kind == CanvasMenuKind.Sort ? 145 : 235);
            if (Kind == CanvasMenuKind.Units)
            {
                int rows = Math.Max(2, Math.Min(Count, (int)((anchor.Top - panel.Top - 31) / 20)));
                int height = rows * 20;
                Bounds = new RectangleF(panel.Right - width - 9, anchor.Top - height - 2, width, height);
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

        internal string TypeNameAt(int index) => _types[index - 2];
        internal string BranchPathAt(int index) => _snapshot.Branches[index - 2].Path;

        internal string Label(int index, bool onlyInvalid, PanelUnitConversion conversion = PanelUnitConversion.None,
            int decimals = PanelUnits.AutomaticDecimals)
        {
            if (Kind == CanvasMenuKind.Units)
            {
                if (index < PanelUnits.ConversionCount)
                    return (index == (int)conversion ? "✓ " : "") + PanelUnits.Label((PanelUnitConversion)index);
                if (index == PanelUnits.ConversionCount) return (decimals < 0 ? "✓ " : "") + "Decimals: automatic";
                return "Decimal places…" + (decimals >= 0 ? "  (" + decimals + ")" : "");
            }
            if (Kind == CanvasMenuKind.Type)
            {
                if (index == 0) return "All types";
                if (index == 1) return onlyInvalid ? "✓ Invalid only" : "Invalid only";
                string name = TypeNameAt(index), hint = ShortTypeName(name);
                return hint == name ? name : hint + " · " + name;
            }
            if (Kind == CanvasMenuKind.Sort) return SortLabels[index];
            if (index == 0) return "All branches";
            if (index == 1) return "Jump to exact path…";
            var branch = _snapshot.Branches[index - 2];
            var path = branch.Path.Length <= 150 ? branch.Path : branch.Path.Substring(0, 149) + "…";
            return path + "   ·   " + branch.ItemCount.ToString("N0");
        }
    }
}
