using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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

        internal PanelOrderLabels(PanelPreviewMembership membership) { _membership = membership; }

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
        internal void Clear() { ClearEntries(); SetResult(null); }

        internal void Add(int viewIndex, int sourcePosition, Point3d anchor)
        {
            if (!anchor.IsValid || !PanelItemOrder.TryGetNumber(_result, sourcePosition, out int number)) return;
            _labels[viewIndex] = new Label(sourcePosition, number, anchor);
            Enabled = true;
        }

        internal void Remove(int viewIndex)
        {
            _labels.Remove(viewIndex);
            if (_labels.Count == 0) { Enabled = false; _armed = false; }
        }

        // The component's native preview callback is the authority on whether this
        // panel may draw. Arm only its current viewport frame; never retain preview args.
        internal void Arm(IGH_PreviewArgs args)
        {
            if (args.Display == null || _labels.Count == 0) return;
            _viewport = args.Viewport.Id;
            _model = args.Display.ModelTransform;
            _selectedColor = Color.FromArgb(255, args.WireColour_Selected);
            _armed = true;
        }

        protected override void PreDrawObjects(DrawEventArgs e) => _armed = false;

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
        }

        private void DrawLabels(DrawEventArgs args, LabelCollisionGrid occupied, float scale, Color fill, bool selected)
        {
            var foreground = fill.GetBrightness() < 0.45f ? Color.White : Color.FromArgb(35, 41, 37);
            foreach (var label in _labels.Values)
            {
                if (_membership.Contains(label.Position) != selected) continue;
                if (!TryProject(args.Viewport, _model, label.Anchor, out var screen)) continue;
                // Native dots choose their own font. Reserve a conservative constant-pixel
                // footprint including padding, instead of measuring or allocating text per frame.
                float width = (label.Text.Length * 9 + 14) * scale, height = 24 * scale;
                var bounds = new RectangleF((float)screen.X - width / 2, (float)screen.Y - height / 2, width, height);
                if (!occupied.TryAdd(bounds)) continue;
                args.Display.DrawDot((float)screen.X, (float)screen.Y, label.Text, fill, foreground);
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

        private readonly struct Label
        {
            internal Label(int position, int number, Point3d anchor)
            { Position = position; Anchor = anchor; Text = number.ToString(CultureInfo.InvariantCulture); }
            internal int Position { get; }
            internal Point3d Anchor { get; }
            internal string Text { get; }
        }
    }
}
