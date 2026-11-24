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
    internal sealed class PanelOrderLabels
    {
        private PanelQueryResult? _result;
        private readonly SortedDictionary<int, Label> _labels = new SortedDictionary<int, Label>();
        private readonly PanelPreviewMembership _membership;
        private bool _drawing, _registered;
        private static readonly ForegroundConduit Foreground = new ForegroundConduit();
        private Color _selectedColor;
        internal PanelDotSize DotSize { get; set; } = PanelDotSize.Medium;
        internal bool HideOccludedDots { get; set; } = true;
        private readonly Dictionary<(Guid Viewport, bool Capture), ViewportFrame> _frames = new Dictionary<(Guid Viewport, bool Capture), ViewportFrame>();
        private readonly Dictionary<(string Text, bool Failed), int> _textIds = new Dictionary<(string, bool), int>();
        private readonly List<Label> _orderedLabels = new List<Label>();
        private readonly Dictionary<int, TextDot> _textDots = new Dictionary<int, TextDot>();
        private long _labelRevision, _identityRevision, _frameUse;
        private bool _labelListDirty = true;
        private static readonly Color PlainFill = Color.FromArgb(245, 247, 244), Border = Color.FromArgb(39, 49, 43);

        private PanelQueryResult? _valueResult;
        private PanelQueryResult? _orderRangeResult;
        private (double Min, double Max) _orderRange;
        private bool _dirty, _sentPartialPending;
        private (bool Order, bool Values, PanelDotPlacement Placement, PanelValuePalette Palette, bool Reverse,
            PanelSearchMode Search, int Decimals, double Factor, PanelLabelSource LabelSource, bool ColourByPaired, bool PassFail) _settings;
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
        private Dictionary<string, int> _categories = new Dictionary<string, int>(StringComparer.Ordinal);
        private Dictionary<int, PanelPreviewValue>? _buildingValues;
        private Dictionary<string, PanelPreviewRange>? _buildingRanges;
        private HashSet<int>? _buildingPending;
        private LegendRow[] _legend = Array.Empty<LegendRow>();
        private LegendRow? _passFailLegend;
        private float _legendWidth, _legendNameWidth, _legendMinWidth, _legendScale, _legendRowHeight;
        private int _legendFontHeight;
        private const float LegendPadding = 7, LegendGap = 8, LegendBarWidth = 90;
        private readonly Color[] _colours = new Color[PanelPreviewValues.ColourCount];
        private readonly Color[] _textColours = new Color[PanelPreviewValues.ColourCount];
        internal Action? ValuesChanged { get; set; }
        internal Action<PanelQueryResult, IReadOnlyList<int>>? PendingChanged { get; set; }
        internal Func<int, bool>? IsAdmitted { get; set; }
        internal bool Gradient => _settings.Palette != PanelValuePalette.Off;
        internal bool ColourGeometry => Gradient || _settings.PassFail;
        internal bool HasValuePreview => _settings.Values || Gradient;
        private bool BoundPairedValues => _settings.ColourByPaired || _settings.LabelSource >= PanelLabelSource.Paired;
        internal Color Colour(int id) => _colours[id];
        internal int ColourId(int position)
        {
            if (Failed(position)) return PanelPreviewValues.FailedColour;
            if (!Gradient) return _settings.PassFail ? PanelPreviewValues.PassedColour : 0;
            if (!_colourValues.TryGetValue(position, out var value)) return 0;
            if (_settings.ColourByPaired && value.Category)
                return _categories.TryGetValue(value.Text, out int id) ? id : PanelPreviewValues.OtherCategory;
            if (!value.Number.HasValue || !_ranges.TryGetValue(value.Key, out var range)) return 0;
            int bucket = range.Bucket(value.Number.Value);
            if (_settings.Reverse) bucket = PanelPreviewValues.Buckets - 1 - bucket;
            return 1 + ((int)_settings.Palette - 1) * PanelPreviewValues.Buckets + bucket;
        }
        private bool Failed(int position) => _settings.PassFail && _valueResult != null && !_valueResult.TryGetPreviewIndex(position, out _);

        internal bool Configure(PanelQueryResult result, bool order, bool values, PanelDotPlacement placement,
            PanelValuePalette palette, bool reverse, PanelSearchMode search, int decimals, double factor,
            IReadOnlyList<int> scope, Func<int, PanelPreviewValue> sample, Func<bool> current,
            PanelLabelSource labelSource, bool colourByPaired, bool passFail)
        {
            _valueGeneration++;
            var settings = (order, values, placement, palette, reverse, search, decimals, factor, labelSource, colourByPaired, passFail);
            var positions = palette != PanelValuePalette.Off ? result.DisplaySet?.AllMembers ?? result.RowPositions :
                values && labelSource < PanelLabelSource.Paired ? scope : Array.Empty<int>();
            bool changed = !_settings.Equals(settings) || !ReferenceEquals(result, _valueResult);
            _sample = sample; _current = current;
            if (!changed && ReferenceEquals(positions, _valuePositions)) return false;
            if (!ReferenceEquals(result, _valueResult)) _lastPublish = -PublishInterval;
            if (_settings.PassFail != passFail || !ReferenceEquals(result, _valueResult))
            {
                int passes = (result.DisplaySet?.AllMembers ?? result.RowPositions).Count;
                _passFailLegend = passFail ? new LegendRow(System.FormattableString.Invariant($"✓ {passes:N0} pass · ✗ {result.Snapshot.Rows.Count - passes:N0} fail")) : null;
                _legendScale = 0;
            }
            if (_settings.ColourByPaired != colourByPaired || _settings.LabelSource != labelSource)
            {
                _values.Clear(); _colourValues.Clear(); _ranges.Clear(); _categories.Clear(); _legend = Array.Empty<LegendRow>();
            }
            if (changed) _labelRevision++;
            if (!ReferenceEquals(result, _valueResult) || _settings.Order != order || _settings.Values != values ||
                _settings.Placement != placement || _settings.Search != search || _settings.Decimals != decimals || _settings.Factor != factor ||
                _settings.LabelSource != labelSource || _settings.PassFail != passFail)
            { _identityRevision++; _labelListDirty = true; }
            _settings = settings; _valueResult = result; _valuePositions = positions;
            SetResult(order || values ? result : null);
            ClearBuild();
            if (!values && !Gradient)
            {
                _refresh?.Stop();
                _values.Clear(); _colourValues.Clear(); _ranges.Clear(); _categories.Clear(); _legend = Array.Empty<LegendRow>();
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

        internal void PreviewAdmissionChanged(int? removed = null)
        {
            if (!BoundPairedValues) return;
            if (removed.HasValue)
            {
                _colourValues.Remove(removed.Value);
                _buildingValues?.Remove(removed.Value);
            }
            if (_valueResult == null || !HasValuePreview) return;
            _dirty = true;
            _refresh?.Start();
        }

        internal void ClearPreviewAdmissions()
        {
            if (!BoundPairedValues) return;
            _colourValues.Clear();
            _categories.Clear();
            ClearBuild();
            PreviewAdmissionChanged();
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
                // Numeric ranges use all Results. Per-position F samples are needed
                // only for admitted geometry, which also bounds the category legend.
                if (!BoundPairedValues || IsAdmitted?.Invoke(position) == true) _buildingValues[position] = value;
                if (!value.Number.HasValue || Failed(position)) continue;
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
                var legend = _ranges.Values.OrderBy(range => Order(range.Metric)).ThenBy(range => range.Metric, StringComparer.Ordinal)
                    .ThenBy(range => range.Unit, StringComparer.Ordinal).Select(range => new LegendRow(range)).ToList();
                if (_settings.ColourByPaired && Gradient) AppendCategories(result, legend);
                _legend = legend.ToArray();
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
            a.Pending == b.Pending && a.Number == b.Number && a.Text == b.Text && a.Key == b.Key && a.LabelText == b.LabelText && a.Category == b.Category;

        private void AppendCategories(PanelQueryResult result, List<LegendRow> legend)
        {
            _categories.Clear();
            var names = new List<string>(PanelPreviewValues.CategoryCount);
            var counts = new int[PanelPreviewValues.CategoryCount];
            var others = new HashSet<string>(StringComparer.Ordinal);
            int otherItems = 0;
            foreach (var sample in _colourValues.Where(pair => pair.Value.Category && !Failed(pair.Key))
                .OrderBy(pair => result.DisplaySet != null ? pair.Key : result.TryGetViewIndex(pair.Key, out int index) ? index : int.MaxValue))
            {
                string key = sample.Value.Text;
                if (_categories.TryGetValue(key, out int id)) counts[id - PanelPreviewValues.CategoryFirst]++;
                else if (names.Count < PanelPreviewValues.CategoryCount)
                {
                    _categories.Add(key, PanelPreviewValues.CategoryFirst + names.Count);
                    counts[names.Count] = 1;
                    names.Add(key);
                }
                else { others.Add(key); otherItems++; }
            }
            for (int index = 0; index < names.Count; index++)
            {
                string label = PanelValuePresentation.Visualize(names[index]);
                legend.Add(new LegendRow(label.Length <= 48 ? label : label.Substring(0, 47) + "…", counts[index], PanelPreviewValues.CategoryFirst + index));
            }
            if (otherItems > 0) legend.Add(new LegendRow(
                System.FormattableString.Invariant($"+{others.Count:N0} others · {otherItems:N0} items"), "", PanelPreviewValues.OtherCategory));
        }

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
            string before = label.Text; int colour = label.ColourId, band = label.Band; var anchor = label.Anchor;
            double previousValue = label.Value;
            int? itemNumber = null;
            if (_settings.Order || _valueResult == null)
            {
                if (PanelItemOrder.TryGetNumber(_result, label.Position, out int number)) itemNumber = number;
                else if (_settings.PassFail && _result != null) itemNumber = _result.Snapshot.Rows[label.Position].OriginalIndex;
            }
            label.Text = value.Label(itemNumber, _settings.Values);
            label.ColourId = ColourId(label.Position);
            label.Value = (_settings.Values ? value.Number : itemNumber) ?? double.NaN;
            if (_settings.Values)
                label.Band = value.Number.HasValue && _ranges.TryGetValue(value.Key, out var range)
                    ? PanelDotLayout.ValueBand(label.Value, range.Min, range.Max) : 0;
            Place(label);
            int group = label.GroupKey;
            if (_settings.Order || _valueResult == null || value.Pending) label.GroupKey = ~label.Order;
            else
            {
                var identity = (label.Text, label.ColourId == PanelPreviewValues.FailedColour);
                if (!_textIds.TryGetValue(identity, out int id)) { id = _textIds.Count + 1; _textIds.Add(identity, id); }
                label.GroupKey = id;
            }
            if (before != label.Text || colour != label.ColourId || anchor != label.Anchor || group != label.GroupKey ||
                band != label.Band || !previousValue.Equals(label.Value)) _labelRevision++;
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

        private bool Visible => _labels.Count > 0 || _passFailLegend != null || Gradient && _legend.Length > 0;
        private void UpdateEnabled()
        {
            bool visible = Visible;
            if (_registered == visible) return;
            _registered = visible;
            Foreground.Register(this, visible);
        }

        internal PanelOrderLabels(PanelPreviewMembership membership)
        {
            _membership = membership;
            for (int id = 0; id < _colours.Length; id++)
            {
                _colours[id] = id >= PanelPreviewValues.CategoryFirst ? PanelPreviewValues.SpecialColour(id) :
                    PanelPreviewValues.Colour(id == 0 ? PanelValuePalette.Off : (PanelValuePalette)((id - 1) / PanelPreviewValues.Buckets + 1), id == 0 ? 0 : (id - 1) % PanelPreviewValues.Buckets, false);
                _textColours[id] = PanelPreviewValues.TextColour(_colours[id]);
            }
        }

        internal void SetResult(PanelQueryResult? result)
        {
            if (ReferenceEquals(result, _result)) return;
            _result = result; _orderRangeResult = null; _identityRevision++;
        }

        internal void ClearEntries()
        {
            _labels.Clear(); _orderedLabels.Clear(); _textIds.Clear(); _frames.Clear(); _values.Clear();
            _prioritySamples.Clear(); _queuedSamples.Clear();
            _labelListDirty = true; _labelRevision++;
            UpdateEnabled();
        }
        internal void SuspendValues()
        {
            _valueGeneration++;
            _result = null; _orderRangeResult = null; _valueResult = null; _sample = null; _current = null;
            _refresh?.Stop(); _refresh?.Dispose(); _refresh = null;
            ClearBuild();
            _valuePositions = Array.Empty<int>();
        }

        internal void Clear()
        {
            SuspendValues();
            _passFailLegend = null;
            ClearEntries(); SetResult(null);
            _values.Clear(); _colourValues.Clear(); _ranges.Clear(); _categories.Clear(); _legend = Array.Empty<LegendRow>(); _lastPublish = -PublishInterval;
            foreach (var dot in _textDots.Values) dot.Dispose();
            _textDots.Clear();
            UpdateEnabled();
        }

        // Enabling a native display conduit can reenter. Point admission may
        // use its callback-free path only when Add will not enable it.
        internal bool CanAddWithoutEnabling => _result == null || Foreground.Enabled;

        internal void Add(int viewIndex, int sourcePosition, Point3d anchor, BoundingBox bounds = default)
        {
            if (!anchor.IsValid || _result == null) return;
            var label = new Label(viewIndex, sourcePosition, "", anchor, bounds);
            UpdateLabel(label);
            if (!_labels.TryGetValue(viewIndex, out var old) || old.Position != sourcePosition) _identityRevision++;
            _labels[viewIndex] = label; _labelListDirty = true; _labelRevision++;
            if ((_settings.Values || Gradient) && (_refresh?.Enabled == true ||
                !_colourValues.TryGetValue(sourcePosition, out var cached) || cached.Pending))
            { Prioritise(sourcePosition); _refresh?.Start(); }
            UpdateEnabled();
        }

        internal void Remove(int viewIndex)
        {
            if (_labels.TryGetValue(viewIndex, out var label))
            {
                _values.Remove(label.Position); _queuedSamples.Remove(label.Position);
                _labels.Remove(viewIndex); _labelListDirty = true; _labelRevision++; _identityRevision++;
            }
            UpdateEnabled();
        }

        // Grasshopper's preview callback decides whether this panel can draw.
        internal void Arm(IGH_PreviewArgs args)
        {
            if (args.Display == null || !Visible || _drawing) return;
            // GH preview model transforms are assumed to be identity. Only
            // the selected-row wire bias contributes to the item's depth bound.
            var selected = _membership.Count > 0 ? PanelGeometryHighlight.WireDepthBias(args.Viewport) : Transform.Identity;
            Foreground.Arm(args.Viewport, args.Display, this, Color.FromArgb(255, args.WireColour_Selected), selected);
        }
        // One conduit owns each draw pass, and its Pass object is the frame
        // epoch for that viewport. Removing it from the pending
        // stack before drawing gives nested captures their own participants,
        // depth snapshot and legend offset, even when they share a viewport ID.
        private sealed class ForegroundConduit : DisplayConduit
        {
            [ThreadStatic] private static List<Pass>? _pending;
            private int _registeredCount;

            internal void Register(PanelOrderLabels labels, bool visible)
            {
                if (visible)
                {
                    if (++_registeredCount == 1)
                    {
                        Rhino.RhinoApp.Idle -= DisableWhenIdle;
                        if (!Enabled) Enabled = true;
                    }
                    return;
                }
                if (_pending != null)
                    foreach (var pass in _pending) pass.Panels.RemoveAll(panel => ReferenceEquals(panel.Labels, labels));
                if (--_registeredCount == 0)
                {
                    _pending?.Clear();
                    // A preview can be retired during a display callback.
                    // Let Rhino finish dispatching before deleting the conduit.
                    Rhino.RhinoApp.Idle += DisableWhenIdle;
                }
            }

            private void DisableWhenIdle(object? sender, EventArgs args)
            {
                Rhino.RhinoApp.Idle -= DisableWhenIdle;
                if (_registeredCount == 0)
                {
                    Enabled = false;
                    PanelSceneDepth.ReleaseIdleResources();
                }
            }
            private sealed class Pass
            {
                internal Pass(DrawEventArgs args)
                {
                    Viewport = args.Viewport.Id; Capture = args.Display.IsInViewCapture;
                    NestLevel = args.Display.NestLevel; RenderPass = args.Display.RenderPass;
                }
                internal readonly Guid Viewport;
                internal readonly bool Capture;
                private readonly int NestLevel, RenderPass;
                internal readonly List<Participant> Panels = new List<Participant>();
                internal bool Matches(Guid viewport, DisplayPipeline display) =>
                    Viewport == viewport && Capture == display.IsInViewCapture &&
                    NestLevel == display.NestLevel && RenderPass == display.RenderPass;
            }

            internal sealed class Participant
            {
                internal Participant(PanelOrderLabels labels, Color colour, Transform selected)
                { Labels = labels; Colour = colour; Selected = selected; }
                internal readonly PanelOrderLabels Labels;
                internal readonly Color Colour;
                internal readonly Transform Selected;
                internal ViewportFrame? Frame;
            }

            protected override void PreDrawObjects(DrawEventArgs e)
            {
                if (_registeredCount == 0) return;
                var pending = _pending ??= new List<Pass>();
                // Bound passes abandoned by Rhino before reaching foreground.
                if (pending.Count == 16) pending.RemoveAt(0);
                pending.Add(new Pass(e));
            }

            private static int FindPass(Guid viewport, DisplayPipeline display)
            {
                if (_pending != null)
                    for (int i = _pending.Count - 1; i >= 0; i--)
                        if (_pending[i].Matches(viewport, display)) return i;
                return -1;
            }

            internal void Arm(RhinoViewport viewport, DisplayPipeline display, PanelOrderLabels labels, Color colour, Transform selected)
            {
                int index = FindPass(viewport.Id, display);
                if (index < 0) return;
                var panels = _pending![index].Panels;
                for (int i = 0; i < panels.Count; i++)
                    if (ReferenceEquals(panels[i].Labels, labels)) return;
                panels.Add(new Participant(labels, colour, selected));
            }

            protected override void DrawForeground(DrawEventArgs e)
            {
                int index = FindPass(e.Viewport.Id, e.Display);
                if (index < 0) return;
                var pass = _pending![index];
                _pending.RemoveAt(index);
                if (pass.Panels.Count == 0) return;

                float dpi = e.Display.DpiScale;
                if (dpi <= 0 || float.IsNaN(dpi) || float.IsInfinity(dpi)) dpi = 1;
                var camera = e.Viewport.GetTransform(CoordinateSystem.World, CoordinateSystem.Camera);
                var dimensions = e.Viewport.Size;
                if (e.Viewport.GetScreenPort(out int left, out int right, out int bottom, out int top, out _, out _))
                    dimensions = new Size(Math.Abs(right - left), Math.Abs(bottom - top));
                var clip = DisplayClipTransform(e);
                var key = (pass.Viewport, pass.Capture || dimensions != e.Viewport.Size);

                Rectangle bounds = Rectangle.Empty;
                try
                {
                    foreach (var panel in pass.Panels)
                    {
                        var labels = panel.Labels;
                        if (labels._drawing || !labels.Visible) continue;
                        panel.Frame = labels.PrepareFrame(key, clip, camera, panel.Selected, dimensions, dpi);
                        labels._drawing = true;
                        if (labels.HideOccludedDots && panel.Frame.ProjectedCount != 0)
                            bounds = bounds.IsEmpty ? panel.Frame.Bounds : Rectangle.Union(bounds, panel.Frame.Bounds);
                    }
                    using var depth = bounds.IsEmpty ? null : PanelSceneDepth.Capture(e.Display, e.Viewport, dimensions, bounds, clip);
                    foreach (var panel in pass.Panels)
                    {
                        if (panel.Frame == null || !panel.Labels.Visible) continue;
                        panel.Labels.FinishFrame(panel.Frame, panel.Labels.HideOccludedDots ? depth : null);
                    }
                    DrawPanels(e, dpi, pass.Panels);
                }
                finally
                {
                    foreach (var panel in pass.Panels)
                        if (panel.Frame != null) panel.Labels._drawing = false;
                }
            }
        }

        private static void DrawPanels(DrawEventArgs args, float dpi, List<ForegroundConduit.Participant> panels)
        {
            var display = args.Display;
            display.PushModelTransform(Transform.Identity);
            display.PushDepthTesting(false);
            display.PushDepthWriting(false);
            try
            {
                bool swatches = false;
                foreach (var panel in panels)
                {
                    if (panel.Frame == null || !panel.Labels.Visible || panel.Frame.ActiveBuckets.Count == 0) continue;
                    panel.Labels.DrawSwatches(display, panel.Frame, dpi);
                    swatches = true;
                }
                // The next panel's deferred point batches must never cover
                // an earlier panel's immediate text or halo.
                if (swatches)
                {
                    display.Flush();
                    bool halos = false;
                    foreach (var panel in panels)
                    {
                        if (panel.Frame == null || !panel.Labels.Visible) continue;
                        foreach (var bucket in panel.Frame.ActiveBuckets)
                        {
                            if (!bucket.Selected) continue;
                            display.DrawPoints(bucket.Points, PointStyle.Circle, panel.Colour, panel.Colour,
                                bucket.Radius + 2.5f * dpi, 0, bucket.Radius, 0, true, false);
                            halos = true;
                        }
                    }
                    if (halos) display.Flush();
                }
                int legendOffset = 0;
                foreach (var panel in panels)
                {
                    if (panel.Frame == null || !panel.Labels.Visible) continue;
                    panel.Labels._selectedColor = panel.Colour;
                    panel.Labels.DrawText(args, dpi, panel.Frame);
                    panel.Labels.DrawLegend(args, dpi, ref legendOffset);
                }
            }
            finally
            {
                display.PopDepthWriting(); display.PopDepthTesting(); display.PopModelTransform();
            }
        }

        private ViewportFrame PrepareFrame((Guid Viewport, bool Capture) id, Transform clip, Transform camera, Transform selected, Size dimensions, float dpi)
        {
            if (!_frames.TryGetValue(id, out var frame))
            {
                if (_frames.Count >= 8)
                {
                    var oldest = id; long use = long.MaxValue;
                    foreach (var pair in _frames) if (pair.Value.LastUse < use) { oldest = pair.Key; use = pair.Value.LastUse; }
                    _frames.Remove(oldest);
                }
                frame = new ViewportFrame(); _frames.Add(id, frame);
            }
            frame.LastUse = ++_frameUse;
            bool sameIdentity = frame.IdentityRevision == _identityRevision && frame.DotSize == DotSize &&
                frame.HideOccludedDots == HideOccludedDots;
            if (sameIdentity && frame.LabelRevision == _labelRevision && frame.Clip == clip && frame.Camera == camera && frame.SelectedBias == selected &&
                frame.Dpi == dpi && frame.Size == dimensions && frame.SelectionRevision == _membership.Revision) return frame;
            if (!sameIdentity) frame.Layout.ResetStability();
            if (_labelListDirty)
            {
                _orderedLabels.Clear(); foreach (var label in _labels.Values) _orderedLabels.Add(label);
                _orderedLabels.Sort((a, b) => a.Hash.CompareTo(b.Hash));
                if (!_settings.Values)
                {
                    var range = OrderRange();
                    foreach (var label in _orderedLabels) label.Band = PanelDotLayout.ValueBand(label.Value, range.Min, range.Max);
                }
                _labelListDirty = false;
            }
            frame.Ensure(_orderedLabels.Count);
            double scale = dpi * PanelDotSettings.Scale(DotSize);
            int count = 0, left = dimensions.Width, top = dimensions.Height, right = 0, bottom = 0;
            foreach (var label in _orderedLabels)
            {
                if (label.Text.Length == 0) continue;
                var anchor = camera * label.Anchor;
                if (!TryProjectCached(clip, anchor, dimensions, out var point)) continue;
                bool isSelected = _membership.Contains(label.Position);
                double nearest = anchor.Z;
                if (HideOccludedDots)
                {
                    if (isSelected && !selected.IsIdentity)
                        nearest = Math.Max(nearest, (camera * (selected * label.Anchor)).Z);
                    if (label.Bounds.IsValid)
                    {
                        // Camera Z is affine in world position. This corner
                        // maximises Z over all eight corners (nearest to us).
                        var min = label.Bounds.Min; var max = label.Bounds.Max;
                        var nearCorner = new Point3d(camera.M20 >= 0 ? max.X : min.X,
                            camera.M21 >= 0 ? max.Y : min.Y, camera.M22 >= 0 ? max.Z : min.Z);
                        nearest = Math.Max(nearest, (camera * nearCorner).Z);
                    }
                }
                frame.ProjectedDepth[count] = nearest;
                // Rhino draws a 14 pt "43.5" dot 41 × 18 px at 100% scale. An
                // oversized box made the overlap allowance hide dots with visible gaps.
                double width = (label.Text.Length * 7.5 + 11) * scale;
                frame.ProjectedDots[count] = new PanelDotInput(point.X, point.Y, width, 18 * scale, label.GroupKey,
                    isSelected, label.Position, label.Value, label.Band);
                frame.ProjectedLabels[count++] = label;
                left = Math.Min(left, (int)point.X); right = Math.Max(right, (int)point.X + 1);
                top = Math.Min(top, (int)point.Y); bottom = Math.Max(bottom, (int)point.Y + 1);
            }
            frame.ProjectedCount = count;
            frame.Bounds = count == 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right, bottom);
            if (frame.Dpi != dpi) { frame.ActiveBuckets.Clear(); frame.Buckets.Clear(); }
            frame.Clip = clip; frame.Camera = camera; frame.SelectedBias = selected; frame.Size = dimensions; frame.Dpi = dpi;
            frame.LabelRevision = _labelRevision; frame.IdentityRevision = _identityRevision;
            frame.SelectionRevision = _membership.Revision;
            frame.DotSize = DotSize; frame.HideOccludedDots = HideOccludedDots; frame.Valid = false;
            return frame;
        }

        private (double Min, double Max) OrderRange()
        {
            if (_result == null || ReferenceEquals(_result, _orderRangeResult)) return _orderRange;
            int min = int.MaxValue, max = int.MinValue;
            void Include(int number) { min = Math.Min(min, number); max = Math.Max(max, number); }
            if (_result.DisplaySet != null)
                foreach (int number in _result.DisplaySet.MemberIndices.Values) Include(number);
            else
                foreach (int position in _result.RowPositions) Include(_result.Snapshot.Rows[position].OriginalIndex);
            _orderRangeResult = _result;
            return _orderRange = (min, max);
        }

        private void FinishFrame(ViewportFrame frame, PanelSceneDepth? depth)
        {
            bool hadDepth = depth.HasValue;
            if (frame.Valid && !hadDepth && !frame.HadDepth) return;
            var scene = depth.GetValueOrDefault();
            bool same = frame.Valid;
            int count = 0;
            for (int i = 0; i < frame.ProjectedCount; i++)
            {
                ref readonly var dot = ref frame.ProjectedDots[i];
                var label = frame.ProjectedLabels[i];
                if (hadDepth &&
                    !scene.IsVisible(new Point2d(dot.X, dot.Y), frame.ProjectedDepth[i], label.Bounds.Min == label.Bounds.Max)) continue;
                int slot = count++;
                if (same && slot < frame.Count && ReferenceEquals(frame.Labels[slot], label)) continue;
                same = false;
                frame.Dots[slot] = dot;
                frame.Labels[slot] = label;
            }
            frame.HadDepth = hadDepth;
            if (same && count == frame.Count) return;
            frame.Count = count;
            double size = PanelDotSettings.Scale(frame.DotSize);
            frame.Layout.Calculate(frame.Dots, count, size, frame.Dpi, 14 * size);
            BuildSwatches(frame, frame.Dpi);
            frame.Valid = true;
        }
        private void DrawText(DrawEventArgs args, float dpi, ViewportFrame frame)
        {
            double size = PanelDotSettings.Scale(frame.DotSize);
            for (int k = frame.Layout.TextCount - 1; k >= 0; k--)
            {
                int i = frame.Layout.TextIndex(k); var output = frame.Layout.Results[i];
                var label = frame.Labels[i]; var anchorLabel = frame.Labels[output.LabelAnchor];
                var anchor = anchorLabel.Anchor;
                // FontHeight maps to ON_TextDot.HeightInPoints, not physical
                // pixels. Rhino applies DPI; multiplying by dpi here doubles it.
                int height = Math.Max(1, (int)Math.Round(14 * size * output.TextScale));
                if (!_textDots.TryGetValue(height, out var dot))
                { dot = new TextDot("", Point3d.Origin) { FontHeight = height }; _textDots.Add(height, dot); }
                dot.Text = label.Text;
                if (frame.Dots[i].Selected) DrawDotHalo(args, anchor, dot, dpi);
                bool failed = anchorLabel.ColourId == PanelPreviewValues.FailedColour;
                var fill = failed ? Color.FromArgb(253, 230, 227) : Gradient ? _colours[anchorLabel.ColourId] : PlainFill;
                dot.Point = anchor;
                args.Display.DrawDot(dot, fill, failed ? Color.FromArgb(122, 29, 22) : Gradient ? _textColours[anchorLabel.ColourId] : Border,
                    failed ? _colours[PanelPreviewValues.FailedColour] : Border);
            }
        }

        private void BuildSwatches(ViewportFrame frame, float dpi)
        {
            foreach (var bucket in frame.ActiveBuckets) bucket.Points.Clear();
            frame.ActiveBuckets.Clear();
            for (int i = 0; i < frame.Count; i++)
            {
                double radius = frame.Layout.Results[i].SwatchRadius;
                if (radius > 0) AddSwatch(frame, i, radius, dpi);
            }
        }

        private void AddSwatch(ViewportFrame frame, int i, double radius, float dpi)
        {
            int quarterPixels = (int)Math.Floor(radius * 4);
            int colour = ColourGeometry ? frame.Labels[i].ColourId : 0;
            bool selected = frame.Dots[i].Selected;
            int key = (selected ? int.MinValue : 0) | colour << 16 | quarterPixels;
            if (!frame.Buckets.TryGetValue(key, out var bucket))
            {
                bucket = new SwatchBucket(colour, Math.Max(1.5f * dpi, quarterPixels / 4f), selected);
                frame.Buckets.Add(key, bucket);
            }
            if (bucket.Points.Count == 0) frame.ActiveBuckets.Add(bucket);
            bucket.Points.Add(frame.Labels[i].Anchor);
        }

        private void DrawSwatches(DisplayPipeline display, ViewportFrame frame, float dpi)
        {
            // RhinoListHelpers.GetConstArray returns RhinoList's backing array:
            // no IEnumerable copy, and submissions depend on styles, not dot count.
            foreach (var bucket in frame.ActiveBuckets) if (!bucket.Selected) DrawSwatchBucket(display, bucket, dpi);
            foreach (var bucket in frame.ActiveBuckets) if (bucket.Selected) DrawSwatchBucket(display, bucket, dpi);
        }

        private void DrawSwatchBucket(DisplayPipeline display, SwatchBucket bucket, float dpi)
        {
            bool failed = bucket.Colour == PanelPreviewValues.FailedColour;
            var fill = failed ? Color.FromArgb(253, 230, 227) : Gradient ? _colours[bucket.Colour] : PlainFill;
            display.DrawPoints(bucket.Points, PointStyle.RoundSimple, failed ? _colours[PanelPreviewValues.FailedColour] : Border, fill, bucket.Radius,
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

        private static Transform DisplayClipTransform(DrawEventArgs args)
        {
            if (args.Display.IsOpenGL)
            {
                // The display pipeline can refit near/far for the current draw
                // while the viewport still reports the previous depth interval.
                var m = args.Display.GetOpenGLCameraToClip();
                if (m[3] != 0 || m[7] != 0 || m[11] != 0 || m[15] != 0)
                    return new Transform
                    {
                        M00 = m[0], M01 = m[4], M02 = m[8], M03 = m[12],
                        M10 = m[1], M11 = m[5], M12 = m[9], M13 = m[13],
                        M20 = m[2], M21 = m[6], M22 = m[10], M23 = m[14],
                        M30 = m[3], M31 = m[7], M32 = m[11], M33 = m[15]
                    };
            }
            return args.Viewport.GetTransform(CoordinateSystem.Camera, CoordinateSystem.Clip);
        }

        internal static bool TryProjectCached(Transform clip, Point3d point, Size dimensions, out Point2d projected)
        {
            projected = Point2d.Unset;
            double w = clip.M30 * point.X + clip.M31 * point.Y + clip.M32 * point.Z + clip.M33;
            if (!(w > 0)) return false;
            double x = clip.M00 * point.X + clip.M01 * point.Y + clip.M02 * point.Z + clip.M03;
            double y = clip.M10 * point.X + clip.M11 * point.Y + clip.M12 * point.Z + clip.M13;
            double z = clip.M20 * point.X + clip.M21 * point.Y + clip.M22 * point.Z + clip.M23;
            if (x < -w || x > w || y < -w || y > w || z < -w || z > w) return false;
            x = (x / w + 1) * .5 * dimensions.Width;
            y = (1 - y / w) * .5 * dimensions.Height;
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
            internal PanelDotInput[] ProjectedDots = Array.Empty<PanelDotInput>();
            internal Label[] ProjectedLabels = Array.Empty<Label>();
            internal double[] ProjectedDepth = Array.Empty<double>();
            internal Rectangle Bounds;
            internal PanelDotInput[] Dots = Array.Empty<PanelDotInput>();
            internal Label[] Labels = Array.Empty<Label>();
            internal int Count, ProjectedCount;
            internal bool Valid, HideOccludedDots, HadDepth;
            internal long LabelRevision, IdentityRevision, SelectionRevision, LastUse;
            internal Transform Clip, Camera, SelectedBias;
            internal Size Size;
            internal float Dpi;
            internal PanelDotSize DotSize;
            internal readonly Dictionary<int, SwatchBucket> Buckets = new Dictionary<int, SwatchBucket>();
            internal readonly List<SwatchBucket> ActiveBuckets = new List<SwatchBucket>();
            internal void Ensure(int count)
            {
                if (Dots.Length >= count) return;
                int capacity = Math.Max(count, Dots.Length * 2);
                Dots = new PanelDotInput[capacity]; Labels = new Label[capacity];
                ProjectedDots = new PanelDotInput[capacity]; ProjectedLabels = new Label[capacity]; ProjectedDepth = new double[capacity];
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
            // Rows beyond this are counted in the overflow line, even on large viewports.
            foreach (var row in _legend.Take(64))
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
            if (_passFailLegend is LegendRow status)
            {
                status.NameBounds = display.Measure2dText(status.Name, Point2d.Origin, false, 0, _legendFontHeight, "Arial");
                _legendWidth = Math.Max(_legendWidth, status.NameBounds.Width + 2 * LegendPadding * scale);
            }
        }

        private void DrawLegendText(DrawEventArgs args, string text, Rectangle bounds, float left, float top)
        {
            // Offset the measured rectangle into its row, rather than assuming a text baseline.
            var point = new Point2d(left - bounds.Left, top + (_legendRowHeight - bounds.Height) / 2 - bounds.Top);
            args.Display.Draw2dText(text, Color.FromArgb(35, 41, 37), point, false, _legendFontHeight, "Arial");
        }

        private void DrawLegend(DrawEventArgs args, float scale, ref int offset)
        {
            if (_passFailLegend == null && (!Gradient || _legend.Length == 0)) return;
            if (_legendScale != scale) LayoutLegend(args.Display, scale);
            int width = (int)Math.Ceiling(_legendWidth);
            int statusRows = _passFailLegend == null ? 0 : 1;
            int available = Math.Max(1, Math.Min(64, (int)((args.Viewport.Size.Height - offset - 36 * scale) / _legendRowHeight) - statusRows));
            bool overflow = _legend.Length > available;
            int shown = Math.Min(_legend.Length, overflow ? available - 1 : available);
            int height = (int)Math.Ceiling((shown + (overflow ? 1 : 0) + statusRows) * _legendRowHeight + 2 * LegendPadding * scale);
            int x = Math.Max(4, args.Viewport.Size.Width - width - (int)(16 * scale));
            int top = args.Viewport.Size.Height - (int)(16 * scale) - offset - height;
            args.Display.Draw2dRectangle(new Rectangle(x, top, width, height), Color.FromArgb(170, 180, 170), 1, Color.FromArgb(238, 245, 247, 244));
            if (_passFailLegend is LegendRow status)
                DrawLegendText(args, status.Name, status.NameBounds, x + LegendPadding * scale, top + LegendPadding * scale);
            float minRight = x + (LegendPadding + LegendGap) * scale + _legendNameWidth + _legendMinWidth;
            for (int index = 0; index < shown; index++)
            {
                var row = _legend[index];
                float y = top + LegendPadding * scale + (index + statusRows) * _legendRowHeight;
                DrawLegendText(args, row.Name, row.NameBounds, x + LegendPadding * scale, y);
                DrawLegendText(args, row.Min, row.MinBounds, minRight - row.MinBounds.Width, y);
                int barX = (int)(minRight + LegendGap * scale), barY = (int)(y + (_legendRowHeight - 10 * scale) / 2);
                if (row.Single)
                    args.Display.Draw2dRectangle(new Rectangle(barX, barY, (int)(16 * scale), (int)(10 * scale)), Color.Transparent, 0,
                        _colours[row.ColourId ?? (1 + ((int)_settings.Palette - 1) * PanelPreviewValues.Buckets + PanelPreviewValues.Buckets / 2)]);
                else
                {
                    for (int bucket = 0; bucket < PanelPreviewValues.Buckets; bucket++)
                        args.Display.Draw2dRectangle(new Rectangle(barX + (int)(bucket * LegendBarWidth / PanelPreviewValues.Buckets * scale), barY,
                            (int)Math.Ceiling(LegendBarWidth / PanelPreviewValues.Buckets * scale), (int)(10 * scale)), Color.Transparent, 0,
                            _colours[1 + ((int)_settings.Palette - 1) * PanelPreviewValues.Buckets + (_settings.Reverse ? PanelPreviewValues.Buckets - 1 - bucket : bucket)]);
                    DrawLegendText(args, row.Max, row.MaxBounds, barX + (LegendBarWidth + LegendGap) * scale, y);
                }
            }
            if (overflow)
            {
                string remaining = System.FormattableString.Invariant($"{_legend.Length - shown:N0} more values");
                var bounds = args.Display.Measure2dText(remaining, Point2d.Origin, false, 0, _legendFontHeight, "Arial");
                DrawLegendText(args, remaining, bounds, x + LegendPadding * scale, top + LegendPadding * scale + (shown + statusRows) * _legendRowHeight);
            }
            offset += height + (int)(6 * scale);
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
            internal LegendRow(string name, int count, int colour)
                : this(name, System.FormattableString.Invariant($"{count:N0}"), colour) { }
            internal LegendRow(string name, string count, int colour)
            { Name = name; Min = count; Max = ""; Single = true; ColourId = colour; }
            internal LegendRow(string name) { Name = name; Min = Max = ""; Single = true; }
            internal readonly string Name, Min, Max;
            internal Rectangle NameBounds, MinBounds, MaxBounds;
            internal readonly bool Single;
            internal readonly int? ColourId;
        }

        private sealed class Label
        {
            internal Label(int order, int position, string text, Point3d anchor, BoundingBox bounds)
            { Order = order; Position = position; Hash = PanelDotLayout.ItemHash(position); Centre = Anchor = anchor; Text = text; Bounds = bounds; }
            internal readonly int Order, Position;
            internal readonly uint Hash;
            internal readonly Point3d Centre;
            internal readonly BoundingBox Bounds;
            internal Point3d Anchor;
            internal string Text;
            internal int ColourId, GroupKey, Band;
            internal double Value = double.NaN;
        }
    }
}
