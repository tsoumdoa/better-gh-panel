using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Linq;
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
        private TextDot? _haloDot;

        private PanelQueryResult? _valueResult;
        private bool _dirty;
        private (bool Order, bool Values, PanelDotPlacement Placement, PanelValuePalette Palette, bool Reverse,
            PanelSearchMode Search, int Decimals, double Factor) _settings;
        private readonly Stopwatch _publishClock = Stopwatch.StartNew();
        private long _lastPublish = -PublishInterval;
        private const int PublishInterval = 250;
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
        private List<int>? _buildingPending;
        private bool _buildComplete;
        private LegendRow[] _legend = Array.Empty<LegendRow>();
        private float _legendWidth, _legendNameWidth, _legendMinWidth, _legendScale, _legendRowHeight;
        private int _legendFontHeight;
        private const float LegendPadding = 7, LegendGap = 8, LegendBarWidth = 90;
        private readonly Color[] _colours = new Color[PanelPreviewValues.ColourCount];
        private readonly Color[] _textColours = new Color[PanelPreviewValues.ColourCount];
        private static readonly Dictionary<Guid, int> LegendOffsets = new Dictionary<Guid, int>();
        internal Action? ValuesChanged { get; set; }
        internal Action<PanelQueryResult, IReadOnlyList<int>>? PendingChanged { get; set; }
        internal bool Gradient => _settings.Palette != PanelValuePalette.Off;
        internal bool HasValuePreview => _settings.Values || Gradient;
        internal Color Colour(int id) => _colours[id];
        internal int ColourId(int position)
        {
            if (!Gradient || !_values.TryGetValue(position, out var value) || !value.Number.HasValue || !_ranges.TryGetValue(value.Key, out var range)) return 0;
            int bucket = range.Bucket(value.Number.Value);
            if (_settings.Reverse) bucket = PanelPreviewValues.Buckets - 1 - bucket;
            return 1 + ((int)_settings.Palette - 1) * PanelPreviewValues.Buckets + bucket;
        }

        internal bool Configure(PanelQueryResult result, bool order, bool values, PanelDotPlacement placement,
            PanelValuePalette palette, bool reverse, PanelSearchMode search, int decimals, double factor,
            IReadOnlyList<int> scope, Func<int, PanelPreviewValue> sample, Func<bool> current)
        {
            _valueGeneration++;
            var settings = (order, values, placement, palette, reverse, search, decimals, factor);
            var positions = palette != PanelValuePalette.Off ? result.DisplaySet?.AllMembers ?? result.RowPositions : values ? scope : Array.Empty<int>();
            bool changed = !_settings.Equals(settings) || !ReferenceEquals(result, _valueResult);
            _sample = sample; _current = current;
            if (!changed && ReferenceEquals(positions, _valuePositions)) return false;
            if (!ReferenceEquals(result, _valueResult)) _lastPublish = -PublishInterval;
            _settings = settings; _valueResult = result; _valuePositions = positions;
            SetResult(order || values ? result : null);
            ClearBuild();
            if (!values && !Gradient)
            {
                _refresh?.Stop();
                _values.Clear(); _ranges.Clear(); _legend = Array.Empty<LegendRow>();
            }
            else InvalidateValues();
            if (changed) { RebuildLabels(); UpdateEnabled(); }
            return changed;
        }

        private void ClearBuild()
        {
            _buildingValues = null; _buildingRanges = null; _buildingPending = null; _buildComplete = false;
        }

        internal void InvalidateValues()
        {
            if ((!_settings.Values && !Gradient) || _valueResult == null) return;
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
                _buildingPending = new List<int>(); _cursor = 0; _dirty = false;
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
                if (_values.TryGetValue(position, out var previous)) value = value.RetainNumber(previous);
                _buildingValues[position] = value;
                if (value.Pending) _buildingPending!.Add(position);
                if (!value.Number.HasValue) continue;
                if (_buildingRanges!.TryGetValue(value.Key, out var range)) range.Add(value.Number.Value, value.Text);
                else _buildingRanges.Add(value.Key, new PanelPreviewRange(value));
            }
            if (_cursor < positions.Count) return;
            if (!_buildComplete)
            {
                _buildComplete = true;
                PendingChanged?.Invoke(result, _buildingPending!.ToArray());
                if (generation != _valueGeneration || current?.Invoke() != true) return;
            }
            if (_publishClock.ElapsedMilliseconds - _lastPublish < PublishInterval) return;
            _lastPublish = _publishClock.ElapsedMilliseconds;
            _values = _buildingValues; _ranges = _buildingRanges!;
            ClearBuild();
            static int Order(string metric) => metric == "Length" ? 0 : metric == "Area" ? 1 : metric == "Volume" ? 2 : 3;
            _legend = _ranges.Values.OrderBy(range => Order(range.Metric)).ThenBy(range => range.Metric, StringComparer.Ordinal)
                .ThenBy(range => range.Unit, StringComparer.Ordinal).Select(range => new LegendRow(range)).ToArray();
            _legendScale = 0;
            RebuildLabels(); UpdateEnabled(); ValuesChanged?.Invoke();
            if (!_dirty) _refresh?.Stop();
        }

        private PanelPreviewValue Value(int position) => !_settings.Values && !Gradient ? default :
            _values.TryGetValue(position, out var value) ? value : _sample?.Invoke(position) ?? default;

        private void Place(Label label)
        {
            label.Anchor = label.Centre;
            if (_settings.Placement == PanelDotPlacement.BoxTop) label.Anchor = new Point3d(label.Centre.X, label.Centre.Y, label.Bounds.Max.Z);
            if (_settings.Placement == PanelDotPlacement.BoxBottom) label.Anchor = new Point3d(label.Centre.X, label.Centre.Y, label.Bounds.Min.Z);
        }

        private void UpdateLabel(Label label)
        {
            var value = Value(label.Position);
            label.Text = value.Label((_settings.Order || _valueResult == null) && PanelItemOrder.TryGetNumber(_result, label.Position, out int number) ? number : (int?)null, _settings.Values);
            label.ColourId = ColourId(label.Position);
            Place(label);
        }

        private void RebuildLabels()
        {
            long generation = _valueGeneration;
            var current = _current;
            foreach (var label in _labels.Values)
            {
                UpdateLabel(label);
                if (generation != _valueGeneration || current?.Invoke() != true) return;
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
            _armed = false;
            _labels.Clear();
            UpdateEnabled();
        }
        internal void SuspendValues()
        {
            _valueGeneration++;
            _valueResult = null; _sample = null; _current = null;
            _refresh?.Stop(); _refresh?.Dispose(); _refresh = null;
            ClearBuild();
            _valuePositions = Array.Empty<int>();
        }

        internal void Clear()
        {
            SuspendValues();
            ClearEntries(); SetResult(null);
            _values.Clear(); _ranges.Clear(); _legend = Array.Empty<LegendRow>(); _lastPublish = -PublishInterval;
            _haloDot?.Dispose(); _haloDot = null;
            UpdateEnabled();
        }

        // Enabling a native display conduit can reenter. Point admission may
        // use its callback-free path only when Add will not enable it.
        internal bool CanAddWithoutEnabling => _result == null || Enabled;

        internal void Add(int viewIndex, int sourcePosition, Point3d anchor, BoundingBox bounds = default)
        {
            if (!anchor.IsValid || _result == null) return;
            var label = new Label(sourcePosition, "", anchor, bounds);
            UpdateLabel(label);
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
            if (Gradient)
            {
                // Halo TextDots use world coordinates; the top dots use screen coordinates.
                e.Display.PushModelTransform(Transform.Identity);
                try { DrawLabels(e, occupied, scale, _selectedColor, true); }
                finally { e.Display.PopModelTransform(); }
            }
            else DrawLabels(e, occupied, scale, _selectedColor, true);
            DrawLabels(e, occupied, scale, Color.FromArgb(245, 247, 244), false);
            DrawLegend(e, scale);
        }

        private void DrawLabels(DrawEventArgs args, LabelCollisionGrid occupied, float scale, Color fill, bool selected)
        {
            var foreground = fill.GetBrightness() < 0.45f ? Color.White : Color.FromArgb(35, 41, 37);
            var cameraX = Gradient && selected ? args.Viewport.CameraX : Vector3d.Zero;
            var cameraY = Gradient && selected ? args.Viewport.CameraY : Vector3d.Zero;
            foreach (var label in _labels.Values)
            {
                if (label.Text.Length == 0 || _membership.Contains(label.Position) != selected) continue;
                if (!TryProject(args.Viewport, _model, label.Anchor, out var screen)) continue;
                // Native dots choose their own font. Reserve a conservative constant-pixel
                // footprint including padding, instead of measuring or allocating text per frame.
                float width = (_settings.Values ? label.Text.Length * 8 + 12 : label.Text.Length * 9 + 14) * scale, height = 24 * scale;
                var bounds = new RectangleF((float)screen.X - width / 2, (float)screen.Y - height / 2, width, height);
                if (!occupied.TryAdd(bounds)) continue;
                if (Gradient && selected) DrawDotHalo(args, label, scale, cameraX, cameraY);
                args.Display.DrawDot((float)screen.X, (float)screen.Y, label.Text,
                    Gradient ? _colours[label.ColourId] : fill, Gradient ? _textColours[label.ColourId] : foreground);
            }
        }

        private void DrawDotHalo(DrawEventArgs args, Label label, float scale, Vector3d cameraX, Vector3d cameraY)
        {
            var anchor = label.Anchor; anchor.Transform(_model);
            if (!args.Viewport.GetWorldToScreenScale(anchor, out double pixelsPerUnit) ||
                pixelsPerUnit <= 0 || double.IsNaN(pixelsPerUnit) || double.IsInfinity(pixelsPerUnit)) return;
            _haloDot ??= new TextDot("", Point3d.Origin);
            _haloDot.Text = label.Text;
            double radius = 3 * scale / pixelsPerUnit;
            var x = cameraX * radius; var y = cameraY * radius;
            var diagonalUp = (x + y) * 0.70710678; var diagonalDown = (x - y) * 0.70710678;
            DrawHaloDot(args.Display, anchor - x);
            DrawHaloDot(args.Display, anchor + x);
            DrawHaloDot(args.Display, anchor - y);
            DrawHaloDot(args.Display, anchor + y);
            DrawHaloDot(args.Display, anchor - diagonalUp);
            DrawHaloDot(args.Display, anchor + diagonalUp);
            DrawHaloDot(args.Display, anchor - diagonalDown);
            DrawHaloDot(args.Display, anchor + diagonalDown);
        }

        private void DrawHaloDot(DisplayPipeline display, Point3d point)
        {
            _haloDot!.Point = point;
            display.DrawDot(_haloDot, _selectedColor, _selectedColor, _selectedColor);
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

        // Use the renderer's own rectangles and cache them until content or DPI changes.
        private void LayoutLegend(DisplayPipeline display, float scale)
        {
            _legendScale = scale;
            _legendNameWidth = _legendMinWidth = _legendWidth = 0;
            _legendFontHeight = Math.Max(1, (int)(12 * scale));
            _legendRowHeight = 24 * scale;
            float maxWidth = 0; bool hasRange = false;
            foreach (var row in _legend)
            {
                row.NameBounds = display.Measure2dText(row.Name, Point2d.Origin, false, 0, _legendFontHeight, "Arial");
                row.MinBounds = display.Measure2dText(row.Min, Point2d.Origin, false, 0, _legendFontHeight, "Arial");
                row.MaxBounds = display.Measure2dText(row.Max, Point2d.Origin, false, 0, _legendFontHeight, "Arial");
                _legendNameWidth = Math.Max(_legendNameWidth, row.NameBounds.Width);
                _legendMinWidth = Math.Max(_legendMinWidth, row.MinBounds.Width);
                _legendRowHeight = Math.Max(_legendRowHeight, Math.Max(row.NameBounds.Height, Math.Max(row.MinBounds.Height, row.MaxBounds.Height)) + 10 * scale);
                if (!row.Single) { maxWidth = Math.Max(maxWidth, row.MaxBounds.Width); hasRange = true; }
            }
            _legendWidth = (2 * LegendPadding + 2 * LegendGap) * scale + _legendNameWidth + _legendMinWidth +
                (hasRange ? (LegendBarWidth + LegendGap) * scale + maxWidth : 16 * scale);
        }

        private void DrawLegendText(DrawEventArgs args, string text, Rectangle bounds, float left, float top)
        {
            // Offset the measured rectangle into its row, rather than assuming a text baseline.
            var point = new Point2d(left - bounds.Left, top + (_legendRowHeight - bounds.Height) / 2 - bounds.Top);
            args.Display.Draw2dText(text, Color.FromArgb(35, 41, 37), point, false, _legendFontHeight, "Arial");
        }

        private void DrawLegend(DrawEventArgs args, float scale)
        {
            if (!Gradient || _legend.Length == 0) return;
            if (_legendScale != scale) LayoutLegend(args.Display, scale);
            LegendOffsets.TryGetValue(args.Viewport.Id, out int offset);
            int width = (int)Math.Ceiling(_legendWidth);
            int height = (int)Math.Ceiling(_legend.Length * _legendRowHeight + 2 * LegendPadding * scale);
            int x = Math.Max(4, args.Viewport.Size.Width - width - (int)(16 * scale));
            int top = args.Viewport.Size.Height - (int)(16 * scale) - offset - height;
            args.Display.Draw2dRectangle(new Rectangle(x, top, width, height), Color.FromArgb(170, 180, 170), 1, Color.FromArgb(238, 245, 247, 244));
            float minRight = x + (LegendPadding + LegendGap) * scale + _legendNameWidth + _legendMinWidth;
            for (int index = 0; index < _legend.Length; index++)
            {
                var row = _legend[index];
                float y = top + LegendPadding * scale + index * _legendRowHeight;
                DrawLegendText(args, row.Name, row.NameBounds, x + LegendPadding * scale, y);
                DrawLegendText(args, row.Min, row.MinBounds, minRight - row.MinBounds.Width, y);
                int barX = (int)(minRight + LegendGap * scale), barY = (int)(y + (_legendRowHeight - 10 * scale) / 2);
                if (row.Single)
                    args.Display.Draw2dRectangle(new Rectangle(barX, barY, (int)(16 * scale), (int)(10 * scale)), Color.Transparent, 0,
                        _colours[1 + ((int)_settings.Palette - 1) * PanelPreviewValues.Buckets + PanelPreviewValues.Buckets / 2]);
                else
                {
                    for (int bucket = 0; bucket < PanelPreviewValues.Buckets; bucket++)
                        args.Display.Draw2dRectangle(new Rectangle(barX + (int)(bucket * LegendBarWidth / PanelPreviewValues.Buckets * scale), barY,
                            (int)Math.Ceiling(LegendBarWidth / PanelPreviewValues.Buckets * scale), (int)(10 * scale)), Color.Transparent, 0,
                            _colours[1 + ((int)_settings.Palette - 1) * PanelPreviewValues.Buckets + (_settings.Reverse ? PanelPreviewValues.Buckets - 1 - bucket : bucket)]);
                    DrawLegendText(args, row.Max, row.MaxBounds, barX + (LegendBarWidth + LegendGap) * scale, y);
                }
            }
            LegendOffsets[args.Viewport.Id] = offset + height + (int)(6 * scale);
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
            internal Rectangle NameBounds, MinBounds, MaxBounds;
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
