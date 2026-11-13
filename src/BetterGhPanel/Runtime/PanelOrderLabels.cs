using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Rhino.Display;
using Rhino.Collections;
using Rhino.DocObjects;
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
        internal PanelDotSize DotSize { get; set; } = PanelDotSize.Medium;
        private readonly Dictionary<Guid, ViewportFrame> _frames = new Dictionary<Guid, ViewportFrame>();
        private readonly Dictionary<string, int> _textIds = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<Label> _orderedLabels = new List<Label>();
        private readonly Dictionary<int, TextDot> _textDots = new Dictionary<int, TextDot>();
        private long _labelRevision, _frameUse;
        private bool _labelListDirty = true;
        internal int LayoutBuilds { get; private set; }
        private static readonly Color PlainFill = Color.FromArgb(245, 247, 244), Border = Color.FromArgb(39, 49, 43);

        private PanelQueryResult? _valueResult;
        private bool _dirty, _sentPartialPending;
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
        private readonly Dictionary<int, PanelPreviewValue> _values = new Dictionary<int, PanelPreviewValue>();
        private Dictionary<int, PanelPreviewValue> _colourValues = new Dictionary<int, PanelPreviewValue>();
        private readonly Queue<int> _prioritySamples = new Queue<int>();
        private readonly HashSet<int> _queuedSamples = new HashSet<int>();
        private Dictionary<string, PanelPreviewRange> _ranges = new Dictionary<string, PanelPreviewRange>();
        private Dictionary<int, PanelPreviewValue>? _buildingValues;
        private Dictionary<string, PanelPreviewRange>? _buildingRanges;
        private HashSet<int>? _buildingPending;
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
            if (!Gradient || !_colourValues.TryGetValue(position, out var value) || !value.Number.HasValue || !_ranges.TryGetValue(value.Key, out var range)) return 0;
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
                _values.Clear(); _colourValues.Clear(); _ranges.Clear(); _legend = Array.Empty<LegendRow>();
            }
            else InvalidateValues();
            if (changed) { RebuildLabels(); UpdateEnabled(); }
            return changed;
        }

        private void ClearBuild()
        {
            _buildingValues = null; _buildingRanges = null; _buildingPending = null;
            _prioritySamples.Clear(); _queuedSamples.Clear(); _sentPartialPending = false;
        }

        internal void InvalidateValues()
        {
            if ((!_settings.Values && !Gradient) || _valueResult == null) return;
            _dirty = true;
            foreach (var label in _labels.Values)
                if (_buildingValues == null || !_values.TryGetValue(label.Position, out var value) || value.Pending) Prioritise(label.Position);
            if (_refresh == null) { _refresh = new Timer { Interval = 16 }; _refresh.Tick += RefreshValues; }
            _refresh.Start();
        }

        private void Prioritise(int position)
        { if (_queuedSamples.Add(position)) _prioritySamples.Enqueue(position); }

        private void RefreshValues(object? sender, EventArgs args)
        {
            if (_current?.Invoke() != true || _valueResult == null || _sample == null) { _refresh?.Stop(); return; }
            if (_buildingValues == null)
            {
                _buildingValues = new Dictionary<int, PanelPreviewValue>(); _buildingRanges = new Dictionary<string, PanelPreviewRange>();
                _buildingPending = new HashSet<int>(); _cursor = 0; _dirty = false;
                foreach (var label in _labels.Values) Prioritise(label.Position);
            }
            long generation = _valueGeneration;
            var current = _current; var sample = _sample; var result = _valueResult; var positions = _valuePositions;
            long started = Stopwatch.GetTimestamp(); int sampled = 0, prioritised = 0, visited = 0;
            while (sampled < 256 && (_prioritySamples.Count > 0 || _cursor < positions.Count))
            {
                if (visited++ > 0 && (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency >= 4) break;
                // Start each slice with admitted labels; reserve scope work too so
                // repeated geometry completions cannot starve the stable range scan.
                bool priority = _prioritySamples.Count > 0 && (prioritised < 128 || _cursor >= positions.Count);
                int position = priority ? _prioritySamples.Dequeue() : positions[_cursor++];
                if (priority && !_queuedSamples.Remove(position)) continue;
                bool captured = _buildingValues.ContainsKey(position);
                if (captured && (!priority || _values.TryGetValue(position, out var shown) && !shown.Pending)) continue;
                var value = sample(position); sampled++; if (priority) prioritised++;
                if (generation != _valueGeneration || current?.Invoke() != true || !ReferenceEquals(result, _valueResult)) return;
                if (_values.TryGetValue(position, out var previous) || _colourValues.TryGetValue(position, out previous)) value = value.RetainNumber(previous);
                // Only admitted positions need the live text cache. Keep each
                // range sample immutable, so a pending label's later text refresh
                // cannot leave an old extremum in its completed colour snapshot.
                if (priority || _queuedSamples.Contains(position)) _values[position] = value;
                if (value.Pending) _buildingPending!.Add(position); else _buildingPending!.Remove(position);
                // Rescan only when the range sample is actually stale.
                if (captured) { if (!Same(_buildingValues[position], value)) _dirty = true; continue; }
                _buildingValues[position] = value;
                if (!value.Number.HasValue) continue;
                if (_buildingRanges!.TryGetValue(value.Key, out var range)) range.Add(value.Number.Value, value.Text);
                else _buildingRanges.Add(value.Key, new PanelPreviewRange(value));
            }
            if (_publishClock.ElapsedMilliseconds - _lastPublish < PublishInterval) return;
            _lastPublish = _publishClock.ElapsedMilliseconds;
            // Text is useful immediately. Colour values and ranges stay paired with
            // the previous complete scan until the whole scope has been sampled.
            bool complete = _cursor >= positions.Count && _prioritySamples.Count == 0;
            if (complete)
            {
                _colourValues = _buildingValues; _ranges = _buildingRanges!;
                static int Order(string metric) => metric == "Length" ? 0 : metric == "Area" ? 1 : metric == "Volume" ? 2 : 3;
                _legend = _ranges.Values.OrderBy(range => Order(range.Metric)).ThenBy(range => range.Metric, StringComparer.Ordinal)
                    .ThenBy(range => range.Unit, StringComparer.Ordinal).Select(range => new LegendRow(range)).ToArray();
                _legendScale = 0;
            }
            // Request measurements once early for the admitted labels, then once with
            // the full scope; every changed request restarts the measurement batch.
            if (complete || !_sentPartialPending)
            {
                _sentPartialPending = true;
                PendingChanged?.Invoke(result, _buildingPending!.ToArray());
                if (generation != _valueGeneration || current?.Invoke() != true) return;
            }
            if (complete) ClearBuild();
            RebuildLabels(); UpdateEnabled(); ValuesChanged?.Invoke();
            if (generation == _valueGeneration && current?.Invoke() == true && complete && !_dirty) _refresh?.Stop();
        }

        private static bool Same(PanelPreviewValue a, PanelPreviewValue b) =>
            a.Pending == b.Pending && a.Number == b.Number && a.Text == b.Text && a.Key == b.Key;

        private PanelPreviewValue Value(int position) => !_settings.Values && !Gradient ? default :
            _values.TryGetValue(position, out var value) || _colourValues.TryGetValue(position, out value) ? value : new PanelPreviewValue("", "", null, "", pending: true);

        private void Place(Label label)
        {
            label.Anchor = label.Centre;
            if (_settings.Placement == PanelDotPlacement.BoxTop) label.Anchor = new Point3d(label.Centre.X, label.Centre.Y, label.Bounds.Max.Z);
            if (_settings.Placement == PanelDotPlacement.BoxBottom) label.Anchor = new Point3d(label.Centre.X, label.Centre.Y, label.Bounds.Min.Z);
        }

        private void UpdateLabel(Label label)
        {
            var value = Value(label.Position);
            string before = label.Text; int colour = label.ColourId; var anchor = label.Anchor;
            label.Text = value.Label((_settings.Order || _valueResult == null) && PanelItemOrder.TryGetNumber(_result, label.Position, out int number) ? number : (int?)null, _settings.Values);
            label.ColourId = ColourId(label.Position);
            Place(label);
            int group = label.GroupKey;
            if (_settings.Order || _valueResult == null || value.Pending) label.GroupKey = ~label.Order;
            else
            {
                if (!_textIds.TryGetValue(label.Text, out int id)) { id = _textIds.Count + 1; _textIds.Add(label.Text, id); }
                label.GroupKey = id;
            }
            if (before != label.Text || colour != label.ColourId || anchor != label.Anchor || group != label.GroupKey) _labelRevision++;
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
            _labels.Clear(); _orderedLabels.Clear(); _textIds.Clear(); _frames.Clear(); _values.Clear();
            _prioritySamples.Clear(); _queuedSamples.Clear();
            _labelListDirty = true; _labelRevision++;
            UpdateEnabled();
        }
        internal void SuspendValues()
        {
            _valueGeneration++;
            _result = null; _valueResult = null; _sample = null; _current = null;
            _refresh?.Stop(); _refresh?.Dispose(); _refresh = null;
            ClearBuild();
            _valuePositions = Array.Empty<int>();
        }

        internal void Clear()
        {
            SuspendValues();
            ClearEntries(); SetResult(null);
            _values.Clear(); _colourValues.Clear(); _ranges.Clear(); _legend = Array.Empty<LegendRow>(); _lastPublish = -PublishInterval;
            foreach (var dot in _textDots.Values) dot.Dispose();
            _textDots.Clear();
            UpdateEnabled();
        }

        // Enabling a native display conduit can reenter. Point admission may
        // use its callback-free path only when Add will not enable it.
        internal bool CanAddWithoutEnabling => _result == null || Enabled;

        internal void Add(int viewIndex, int sourcePosition, Point3d anchor, BoundingBox bounds = default)
        {
            if (!anchor.IsValid || _result == null) return;
            var label = new Label(viewIndex, sourcePosition, "", anchor, bounds);
            UpdateLabel(label);
            _labels[viewIndex] = label; _labelListDirty = true; _labelRevision++;
            if ((_settings.Values || Gradient) && (_refresh?.Enabled == true ||
                !_colourValues.TryGetValue(sourcePosition, out var cached) || cached.Pending))
            { Prioritise(sourcePosition); _refresh?.Start(); }
            if (!Enabled) Enabled = true;
        }

        internal void Remove(int viewIndex)
        {
            if (_labels.TryGetValue(viewIndex, out var label))
            {
                _values.Remove(label.Position); _queuedSamples.Remove(label.Position);
                _labels.Remove(viewIndex); _labelListDirty = true; _labelRevision++;
            }
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
            float scale = (float)e.Display.DpiScale;
            if (scale <= 0 || float.IsNaN(scale) || float.IsInfinity(scale)) scale = 1;
            DrawLabels(e, scale);
            DrawLegend(e, scale);
        }

        private ViewportFrame LayoutFrame(DrawEventArgs args, float dpi)
        {
            Guid id = args.Viewport.Id;
            if (!_frames.TryGetValue(id, out var frame))
            {
                if (_frames.Count >= 4)
                {
                    Guid oldest = Guid.Empty; long use = long.MaxValue;
                    foreach (var pair in _frames) if (pair.Value.LastUse < use) { oldest = pair.Key; use = pair.Value.LastUse; }
                    _frames.Remove(oldest);
                }
                frame = new ViewportFrame(); _frames.Add(id, frame);
            }
            frame.LastUse = ++_frameUse;
            var clip = args.Viewport.GetTransform(CoordinateSystem.World, CoordinateSystem.Clip);
            var screen = args.Viewport.GetTransform(CoordinateSystem.World, CoordinateSystem.Screen);
            var dimensions = args.Viewport.Size;
            if (frame.Valid && frame.Clip == clip && frame.Screen == screen && frame.Model == _model && frame.Dpi == dpi &&
                frame.Size == dimensions && frame.LabelRevision == _labelRevision && frame.SelectionRevision == _membership.Revision &&
                frame.DotSize == DotSize && frame.Gradient == Gradient && frame.Values == _settings.Values && frame.SelectedColour == _selectedColor) return frame;
            if (_labelListDirty)
            {
                _orderedLabels.Clear(); foreach (var label in _labels.Values) _orderedLabels.Add(label);
                _labelListDirty = false;
            }
            frame.Ensure(_orderedLabels.Count); frame.Count = 0;
            double size = PanelDotSettings.Scale(DotSize), scale = dpi * size;
            var modelClip = clip * _model; var modelScreen = screen * _model;
            for (int k = 0; k < _orderedLabels.Count; k++)
            {
                var label = _orderedLabels[k];
                if (label.Text.Length == 0 || !TryProjectCached(modelClip, modelScreen, label.Anchor, dimensions, out var point)) continue;
                int i = frame.Count++;
                double width = (_settings.Values ? label.Text.Length * 8 + 12 : label.Text.Length * 9 + 14) * scale;
                frame.Dots[i] = new PanelDotInput(point.X, point.Y, width, 24 * scale, label.GroupKey, _membership.Contains(label.Position), label.Order);
                frame.Labels[i] = label;
                frame.WorldAnchors[i] = _model * label.Anchor;
            }
            frame.Layout.Calculate(frame.Dots, frame.Count, size, dpi);
            LayoutBuilds++;
            if (frame.Dpi != dpi) { frame.ActiveBuckets.Clear(); frame.Buckets.Clear(); }
            BuildSwatches(frame, dpi);
            frame.Clip = clip; frame.Screen = screen; frame.Model = _model; frame.Size = dimensions; frame.Dpi = dpi;
            frame.LabelRevision = _labelRevision; frame.SelectionRevision = _membership.Revision;
            frame.DotSize = DotSize; frame.Gradient = Gradient; frame.Values = _settings.Values; frame.SelectedColour = _selectedColor; frame.Valid = true;
            return frame;
        }

        private void DrawLabels(DrawEventArgs args, float dpi)
        {
            var frame = LayoutFrame(args, dpi);
            double size = PanelDotSettings.Scale(DotSize);
            args.Display.PushModelTransform(Transform.Identity);
            args.Display.PushDepthTesting(false);
            args.Display.PushDepthWriting(false);
            try
            {
                DrawSwatches(args.Display, frame, dpi);
                for (int k = frame.Count - 1; k >= 0; k--)
                {
                    int i = frame.Layout.PriorityIndex(k); var output = frame.Layout.Results[i];
                    if (output.Tier != PanelDotTier.Text && output.Tier != PanelDotTier.Full) continue;
                    var label = frame.Labels[i]; var anchor = frame.WorldAnchors[output.LabelAnchor];
                    // FontHeight maps to ON_TextDot.HeightInPoints, not physical
                    // pixels. Rhino applies DPI; multiplying by dpi here doubles it.
                    int height = Math.Max(1, (int)Math.Round(14 * size * output.TextScale));
                    if (!_textDots.TryGetValue(height, out var dot))
                    { dot = new TextDot("", Point3d.Origin) { FontHeight = height }; _textDots.Add(height, dot); }
                    dot.Text = label.Text;
                    if (frame.Dots[i].Selected) DrawDotHalo(args, anchor, dot, dpi);
                    var anchorLabel = frame.Labels[output.LabelAnchor];
                    var fill = Gradient ? _colours[anchorLabel.ColourId] : PlainFill;
                    dot.Point = anchor;
                    args.Display.DrawDot(dot, fill, Gradient ? _textColours[anchorLabel.ColourId] : Border, Border);
                }
            }
            finally
            {
                args.Display.PopDepthWriting(); args.Display.PopDepthTesting(); args.Display.PopModelTransform();
            }
        }

        private void BuildSwatches(ViewportFrame frame, float dpi)
        {
            foreach (var bucket in frame.ActiveBuckets) bucket.Points.Clear();
            frame.ActiveBuckets.Clear();
            for (int i = 0; i < frame.Count; i++)
            {
                var output = frame.Layout.Results[i]; var rep = frame.Layout.Results[output.Representative];
                bool text = rep.Tier == PanelDotTier.Text || rep.Tier == PanelDotTier.Full;
                if (output.GroupCount > 1 && !(text && output.LabelAnchor == i) && !(!text && output.Representative == i))
                    AddSwatch(frame, i, output.GroupSwatchRadius, dpi);
                if (output.Tier == PanelDotTier.Swatch) AddSwatch(frame, i, output.SwatchRadius, dpi);
            }
        }

        private void AddSwatch(ViewportFrame frame, int i, double radius, float dpi)
        {
            int quarterPixels = (int)Math.Floor(radius * 4);
            int colour = Gradient ? frame.Labels[i].ColourId : 0;
            bool selected = frame.Dots[i].Selected;
            int key = (selected ? int.MinValue : 0) | colour << 16 | quarterPixels;
            if (!frame.Buckets.TryGetValue(key, out var bucket))
            {
                bucket = new SwatchBucket(colour, Math.Max(1.5f * dpi, quarterPixels / 4f), selected);
                frame.Buckets.Add(key, bucket);
            }
            if (bucket.Points.Count == 0) frame.ActiveBuckets.Add(bucket);
            bucket.Points.Add(frame.WorldAnchors[i]);
        }

        private void DrawSwatches(DisplayPipeline display, ViewportFrame frame, float dpi)
        {
            // RhinoListHelpers.GetConstArray returns RhinoList's backing array:
            // no IEnumerable copy, and submissions depend on styles, not dot count.
            foreach (var bucket in frame.ActiveBuckets) if (!bucket.Selected) DrawSwatchBucket(display, bucket, dpi);
            foreach (var bucket in frame.ActiveBuckets) if (bucket.Selected)
                display.DrawPoints(bucket.Points, PointStyle.RoundSimple, _selectedColor, _selectedColor,
                    bucket.Radius + 2.5f * dpi, 0, 0, 0, true, false);
            foreach (var bucket in frame.ActiveBuckets) if (bucket.Selected) DrawSwatchBucket(display, bucket, dpi);
        }

        private void DrawSwatchBucket(DisplayPipeline display, SwatchBucket bucket, float dpi)
        {
            var fill = Gradient ? _colours[bucket.Colour] : PlainFill;
            display.DrawPoints(bucket.Points, PointStyle.RoundSimple, Border, fill, bucket.Radius,
                bucket.Radius >= 2.5f * dpi ? .8f * dpi : 0, 0, 0, true, false);
        }

        private void DrawDotHalo(DrawEventArgs args, Point3d anchor, TextDot dot, float scale)
        {
            if (!args.Viewport.GetWorldToScreenScale(anchor, out double pixelsPerUnit) ||
                pixelsPerUnit <= 0 || double.IsNaN(pixelsPerUnit) || double.IsInfinity(pixelsPerUnit)) return;
            double radius = 3 * scale / pixelsPerUnit;
            var x = args.Viewport.CameraX * radius; var y = args.Viewport.CameraY * radius;
            var diagonalUp = (x + y) * 0.70710678; var diagonalDown = (x - y) * 0.70710678;
            DrawHaloDot(args.Display, dot, anchor - x); DrawHaloDot(args.Display, dot, anchor + x);
            DrawHaloDot(args.Display, dot, anchor - y); DrawHaloDot(args.Display, dot, anchor + y);
            DrawHaloDot(args.Display, dot, anchor - diagonalUp); DrawHaloDot(args.Display, dot, anchor + diagonalUp);
            DrawHaloDot(args.Display, dot, anchor - diagonalDown); DrawHaloDot(args.Display, dot, anchor + diagonalDown);
        }

        private void DrawHaloDot(DisplayPipeline display, TextDot dot, Point3d point)
        {
            dot.Point = point;
            display.DrawDot(dot, _selectedColor, _selectedColor, _selectedColor);
        }

        internal static bool TryProjectCached(Transform clip, Transform screen, Point3d point, Size dimensions, out Point2d projected)
        {
            projected = Point2d.Unset;
            double w = clip.M30 * point.X + clip.M31 * point.Y + clip.M32 * point.Z + clip.M33;
            if (!(w > 0)) return false;
            double x = clip.M00 * point.X + clip.M01 * point.Y + clip.M02 * point.Z + clip.M03;
            double y = clip.M10 * point.X + clip.M11 * point.Y + clip.M12 * point.Z + clip.M13;
            double z = clip.M20 * point.X + clip.M21 * point.Y + clip.M22 * point.Z + clip.M23;
            if (x < -w || x > w || y < -w || y > w || z < -w || z > w) return false;
            double sw = screen.M30 * point.X + screen.M31 * point.Y + screen.M32 * point.Z + screen.M33;
            x = (screen.M00 * point.X + screen.M01 * point.Y + screen.M02 * point.Z + screen.M03) / sw;
            y = (screen.M10 * point.X + screen.M11 * point.Y + screen.M12 * point.Z + screen.M13) / sw;
            if (!(x >= 0 && y >= 0 && x < dimensions.Width && y < dimensions.Height)) return false;
            projected = new Point2d(x, y); return true;
        }

        private sealed class SwatchBucket
        {
            internal SwatchBucket(int colour, float radius, bool selected) { Colour = colour; Radius = radius; Selected = selected; }
            internal readonly int Colour;
            internal readonly float Radius;
            internal readonly bool Selected;
            internal readonly RhinoList<Point3d> Points = new RhinoList<Point3d>();
        }

        private sealed class ViewportFrame
        {
            internal readonly PanelDotLayout Layout = new PanelDotLayout();
            internal PanelDotInput[] Dots = Array.Empty<PanelDotInput>();
            internal Label[] Labels = Array.Empty<Label>();
            internal Point3d[] WorldAnchors = Array.Empty<Point3d>();
            internal int Count;
            internal bool Valid, Gradient, Values;
            internal long LabelRevision, SelectionRevision, LastUse;
            internal Transform Clip, Screen, Model;
            internal Size Size;
            internal float Dpi;
            internal PanelDotSize DotSize;
            internal Color SelectedColour;
            internal readonly Dictionary<int, SwatchBucket> Buckets = new Dictionary<int, SwatchBucket>();
            internal readonly List<SwatchBucket> ActiveBuckets = new List<SwatchBucket>();
            internal void Ensure(int count)
            {
                if (Dots.Length >= count) return;
                int capacity = Math.Max(count, Dots.Length * 2);
                Dots = new PanelDotInput[capacity]; Labels = new Label[capacity]; WorldAnchors = new Point3d[capacity];
            }
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
            internal Label(int order, int position, string text, Point3d anchor, BoundingBox bounds)
            { Order = order; Position = position; Centre = Anchor = anchor; Text = text; Bounds = bounds; }
            internal readonly int Order, Position;
            internal readonly Point3d Centre;
            internal readonly BoundingBox Bounds;
            internal Point3d Anchor;
            internal string Text;
            internal int ColourId, GroupKey;
        }
    }
}
