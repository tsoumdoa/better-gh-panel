using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper.Kernel;
using Rhino.Display;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    internal sealed class PanelOrderLabels : DisplayConduit
    {
        private PanelQueryResult? _result;
        private readonly SortedDictionary<int, Label> _labels = new SortedDictionary<int, Label>();
        private readonly PanelPreviewMembership _membership;
        private bool _armed;
        private Guid _viewport;
        private Transform _model;
        private Color _selectedColor;

        private PanelQueryResult? _valueResult;
        private bool _showOrder, _showValues, _reverse, _dirty;
        private PanelDotPlacement _placement;
        private PanelValuePalette _palette;
        private int _decimals;
        private double _factor;
        private PanelSearchMode _search;
        private IReadOnlyList<int> _valuePositions = Array.Empty<int>();
        private Func<int, PanelPreviewValue>? _sample;
        private Func<bool>? _current;
        private Timer? _refresh;
        private int _cursor;
        private long _valueGeneration;
        private Dictionary<int, PanelPreviewValue> _values = new Dictionary<int, PanelPreviewValue>();
        private Dictionary<string, PanelPreviewRange> _ranges = new Dictionary<string, PanelPreviewRange>();
        private Dictionary<int, PanelPreviewValue>? _buildingValues;
        private Dictionary<string, PanelPreviewRange>? _buildingRanges;
        private LegendRow[] _legend = Array.Empty<LegendRow>();
        private readonly Color[] _colours = new Color[1 + 3 * PanelPreviewValues.Buckets];
        private readonly Color[] _textColours = new Color[1 + 3 * PanelPreviewValues.Buckets];
        private static readonly Dictionary<Guid, int> LegendOffsets = new Dictionary<Guid, int>();
        internal Action? ValuesChanged { get; set; }
        internal bool Gradient => _palette != PanelValuePalette.Off;
        internal Color Colour(int id) => _colours[id];
        internal int ColourId(int position)
        {
            if (!Gradient || !_values.TryGetValue(position, out var value) || !value.Number.HasValue || !_ranges.TryGetValue(value.Key, out var range)) return 0;
            int bucket = range.Bucket(value.Number.Value);
            if (_reverse) bucket = PanelPreviewValues.Buckets - 1 - bucket;
            return 1 + ((int)_palette - 1) * PanelPreviewValues.Buckets + bucket;
        }

        internal void Configure(PanelQueryResult result, bool order, bool values, PanelDotPlacement placement,
            PanelValuePalette palette, bool reverse, PanelSearchMode search, int decimals, double factor,
            IReadOnlyList<int> scope, Func<int, PanelPreviewValue> sample, Func<bool> current)
        {
            _valueGeneration++;
            var positions = palette != PanelValuePalette.Off ? result.DisplaySet?.AllMembers ?? result.RowPositions : scope;
            bool changed = !ReferenceEquals(result, _valueResult) || _search != search || _decimals != decimals || _factor != factor;
            bool scopeChanged = !ReferenceEquals(positions, _valuePositions);
            bool demandChanged = changed || _showValues != values || _palette != palette || scopeChanged;
            if (scopeChanged) { _buildingValues = null; _buildingRanges = null; }
            _valuePositions = positions;
            _valueResult = result; _showOrder = order; _showValues = values; _placement = placement;
            _palette = palette; _reverse = reverse; _search = search; _decimals = decimals; _factor = factor;
            _sample = sample; _current = current;
            SetResult(order || values ? result : null);
            if (changed)
            {
                _values.Clear(); _ranges.Clear(); _legend = Array.Empty<LegendRow>();
                _buildingValues = null; _buildingRanges = null;
            }
            if (!values && !Gradient)
            {
                _refresh?.Stop(); _buildingValues = null; _buildingRanges = null;
                _values.Clear(); _ranges.Clear(); _legend = Array.Empty<LegendRow>();
            }
            else if (demandChanged) InvalidateValues();
            RebuildLabels();
            UpdateEnabled();
        }

        internal void InvalidateValues()
        {
            if ((!_showValues && !Gradient) || _valueResult == null) return;
            _dirty = true;
            if (_refresh == null) { _refresh = new Timer { Interval = 16 }; _refresh.Tick += RefreshValues; }
            _refresh.Start();
        }

        private void RefreshValues(object? sender, EventArgs args)
        {
            if (_current?.Invoke() != true || _valueResult == null || _sample == null) { _refresh?.Stop(); return; }
            if (_buildingValues == null)
            {
                _buildingValues = new Dictionary<int, PanelPreviewValue>(); _buildingRanges = new Dictionary<string, PanelPreviewRange>();
                _cursor = 0; _dirty = false;
            }
            long generation = _valueGeneration;
            var current = _current;
            var sample = _sample;
            var result = _valueResult;
            var positions = _valuePositions;
            int end = Math.Min(positions.Count, _cursor + 256);
            for (; _cursor < end; _cursor++)
            {
                int position = positions[_cursor]; var value = sample(position);
                if (generation != _valueGeneration || current?.Invoke() != true || !ReferenceEquals(result, _valueResult)) return;
                _buildingValues[position] = value;
                if (!value.Number.HasValue) continue;
                if (_buildingRanges!.TryGetValue(value.Key, out var range)) range.Add(value.Number.Value, value.Text);
                else _buildingRanges.Add(value.Key, new PanelPreviewRange(value));
            }
            if (_cursor < positions.Count) return;
            _values = _buildingValues; _ranges = _buildingRanges!;
            _buildingValues = null; _buildingRanges = null;
            var legend = new List<LegendRow>();
            foreach (var range in _ranges.Values)
            {
                legend.Add(new LegendRow(range));
            }
            _legend = legend.ToArray();
            RebuildLabels(); UpdateEnabled(); ValuesChanged?.Invoke();
            if (!_dirty) _refresh?.Stop();
        }

        private void RebuildLabels()
        {
            long generation = _valueGeneration;
            var current = _current;
            foreach (var label in _labels.Values)
            {
                var value = !_showValues && !Gradient ? default : _values.TryGetValue(label.Position, out var known) ? known : _sample?.Invoke(label.Position) ?? default;
                if (generation != _valueGeneration || current?.Invoke() != true) return;
                label.Text = value.Label(_showOrder && PanelItemOrder.TryGetNumber(_result, label.Position, out int number) ? number : (int?)null, _showValues);
                label.ColourId = ColourId(label.Position);
                label.Anchor = label.Centre;
                if (_placement == PanelDotPlacement.BoxTop) label.Anchor = new Point3d(label.Centre.X, label.Centre.Y, label.Bounds.Max.Z);
                if (_placement == PanelDotPlacement.BoxBottom) label.Anchor = new Point3d(label.Centre.X, label.Centre.Y, label.Bounds.Min.Z);
            }
        }
        private void UpdateEnabled() { Enabled = _labels.Count > 0 || Gradient && _legend.Length > 0; }

        internal PanelOrderLabels(PanelPreviewMembership membership)
        {
            _membership = membership;
            for (int id = 0; id < _colours.Length; id++)
            {
                _colours[id] = PanelPreviewValues.Colour(id == 0 ? PanelValuePalette.Off : (PanelValuePalette)((id - 1) / PanelPreviewValues.Buckets + 1), id == 0 ? 0 : (id - 1) % PanelPreviewValues.Buckets, false);
                _textColours[id] = PanelPreviewValues.TextColour(_colours[id]);
            }
        }

        internal void SetResult(PanelQueryResult? result)
        {
            if (ReferenceEquals(result, _result)) return;
            _result = result;
        }

        internal void ClearEntries()
        {
            Enabled = false;
            _armed = false;
            _labels.Clear();
        }
        internal void Clear()
        {
            _valueGeneration++;
            ClearEntries(); SetResult(null); _valueResult = null; _sample = null; _current = null;
            _refresh?.Stop(); _refresh?.Dispose(); _refresh = null;
            _values.Clear(); _ranges.Clear(); _legend = Array.Empty<LegendRow>();
            _buildingValues = null; _buildingRanges = null;
            _valuePositions = Array.Empty<int>();
        }

        // Enabling a native display conduit can reenter. Point admission may
        // use its callback-free path only when Add will not enable it.
        internal bool CanAddWithoutEnabling => _result == null || Enabled;

        internal void Add(int viewIndex, int sourcePosition, Point3d anchor, BoundingBox bounds = default)
        {
            if (!anchor.IsValid || _result == null) return;
            var value = !_showValues && !Gradient ? default : _values.TryGetValue(sourcePosition, out var known) ? known : _sample?.Invoke(sourcePosition) ?? default;
            int? order = (_showOrder || _valueResult == null) && PanelItemOrder.TryGetNumber(_result, sourcePosition, out int number) ? number : (int?)null;
            var label = new Label(sourcePosition, value.Label(order, _showValues), anchor, bounds);
            label.ColourId = ColourId(sourcePosition);
            if (_placement == PanelDotPlacement.BoxTop) label.Anchor = new Point3d(anchor.X, anchor.Y, bounds.Max.Z);
            if (_placement == PanelDotPlacement.BoxBottom) label.Anchor = new Point3d(anchor.X, anchor.Y, bounds.Min.Z);
            _labels[viewIndex] = label;
            if (!Enabled) Enabled = true;
        }

        internal void Remove(int viewIndex)
        {
            _labels.Remove(viewIndex);
            UpdateEnabled();
        }

        // The component's native preview callback is the authority on whether this
        // panel may draw. Arm only its current viewport frame; never retain preview args.
        internal void Arm(IGH_PreviewArgs args)
        {
            if (args.Display == null || !Enabled) return;
            _viewport = args.Viewport.Id;
            _model = args.Display.ModelTransform;
            _selectedColor = Color.FromArgb(255, args.WireColour_Selected);
            _armed = true;
        }

        protected override void PreDrawObjects(DrawEventArgs e)
        {
            _armed = false;
            if (LegendOffsets.Count > 64) LegendOffsets.Clear();
            LegendOffsets[e.Viewport.Id] = 0;
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (!_armed || _viewport != e.Viewport.Id) return;
            _armed = false;
            var occupied = new LabelCollisionGrid();
            float scale = (float)e.Display.DpiScale;
            if (scale <= 0 || float.IsNaN(scale) || float.IsInfinity(scale)) scale = 1;
            // Selected labels win collisions. Each list retains current view order; hidden
            // labels keep their original numbers and can reappear as the view separates them.
            DrawLabels(e, occupied, scale, _selectedColor, true);
            DrawLabels(e, occupied, scale, Color.FromArgb(245, 247, 244), false);
            DrawLegend(e, scale);
        }

        private void DrawLabels(DrawEventArgs args, LabelCollisionGrid occupied, float scale, Color fill, bool selected)
        {
            var foreground = fill.GetBrightness() < 0.45f ? Color.White : Color.FromArgb(35, 41, 37);
            foreach (var label in _labels.Values)
            {
                if (label.Text.Length == 0 || _membership.Contains(label.Position) != selected) continue;
                if (!TryProject(args.Viewport, _model, label.Anchor, out var screen)) continue;
                // Native dots choose their own font. Reserve a conservative constant-pixel
                // footprint including padding, instead of measuring or allocating text per frame.
                float width = (label.Text.Length * 9 + 14) * scale, height = 24 * scale;
                var bounds = new RectangleF((float)screen.X - width / 2, (float)screen.Y - height / 2, width, height);
                if (!occupied.TryAdd(bounds)) continue;
                if (Gradient && selected)
                    args.Display.DrawRoundedRectangle(new PointF((float)screen.X, (float)screen.Y), width + 4 * scale, height + 4 * scale, 5 * scale,
                        _selectedColor, 2 * scale, _selectedColor);
                args.Display.DrawDot((float)screen.X, (float)screen.Y, label.Text,
                    Gradient ? _colours[label.ColourId] : fill, Gradient ? _textColours[label.ColourId] : foreground);
            }
        }

        internal static bool TryProject(RhinoViewport viewport, Transform model, Point3d anchor, out Point2d screen)
        {
            screen = Point2d.Unset;
            anchor.Transform(model);
            // Frustum testing rejects points outside the view and behind perspective cameras.
            if (!anchor.IsValid || !viewport.IsVisible(anchor)) return false;
            screen = viewport.WorldToClient(anchor);
            return screen.IsValid && screen.X >= 0 && screen.Y >= 0 && screen.X < viewport.Size.Width && screen.Y < viewport.Size.Height;
        }

        private void DrawLegend(DrawEventArgs args, float scale)
        {
            if (!Gradient || _legend.Length == 0) return;
            LegendOffsets.TryGetValue(args.Viewport.Id, out int offset);
            int height = (int)(24 * scale), width = (int)(390 * scale);
            int x = Math.Max(4, args.Viewport.Size.Width - width - (int)(16 * scale));
            int bottom = args.Viewport.Size.Height - (int)(16 * scale) - offset;
            foreach (var row in _legend)
            {
                int y = bottom - height; bottom = y;
                args.Display.Draw2dRectangle(new Rectangle(x, y, width, height), Color.Transparent, 0, Color.FromArgb(238, 245, 247, 244));
                args.Display.Draw2dText(row.Name, Color.FromArgb(35, 41, 37), new Point2d(x + 7 * scale, y + 5 * scale), false, (int)(12 * scale));
                args.Display.Draw2dText(row.Min, Color.FromArgb(35, 41, 37), new Point2d(x + 150 * scale, y + 5 * scale), false, (int)(12 * scale));
                int barX = x + (int)(224 * scale), barY = y + (int)(7 * scale);
                if (row.Single)
                    args.Display.Draw2dRectangle(new Rectangle(barX, barY, (int)(16 * scale), (int)(10 * scale)), Color.Transparent, 0,
                        _colours[1 + ((int)_palette - 1) * PanelPreviewValues.Buckets + PanelPreviewValues.Buckets / 2]);
                else
                {
                    for (int bucket = 0; bucket < PanelPreviewValues.Buckets; bucket++)
                        args.Display.Draw2dRectangle(new Rectangle(barX + (int)(bucket * scale), barY, (int)Math.Ceiling(scale), (int)(10 * scale)), Color.Transparent, 0,
                            _colours[1 + ((int)_palette - 1) * PanelPreviewValues.Buckets + (_reverse ? PanelPreviewValues.Buckets - 1 - bucket : bucket)]);
                    args.Display.Draw2dText(row.Max, Color.FromArgb(35, 41, 37), new Point2d(x + 300 * scale, y + 5 * scale), false, (int)(12 * scale));
                }
            }
            LegendOffsets[args.Viewport.Id] = offset + _legend.Length * height + (int)(6 * scale);
        }

        private sealed class LegendRow
        {
            internal LegendRow(PanelPreviewRange range)
            {
                Name = range.Metric + (range.Unit.Length == 0 ? "" : " · " + range.Unit);
                Min = range.MinText;
                Max = range.MaxText;
                Single = range.Min == range.Max;
            }
            internal readonly string Name, Min, Max;
            internal readonly bool Single;
        }

        private sealed class Label
        {
            internal Label(int position, string text, Point3d anchor, BoundingBox bounds)
            { Position = position; Centre = Anchor = anchor; Text = text; Bounds = bounds; }
            internal readonly int Position;
            internal readonly Point3d Centre;
            internal readonly BoundingBox Bounds;
            internal Point3d Anchor;
            internal string Text;
            internal int ColourId;
        }
    }
}
