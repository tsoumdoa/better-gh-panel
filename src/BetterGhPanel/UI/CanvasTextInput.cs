using System;
using System.Drawing;
using Grasshopper.GUI.Base;
using Grasshopper.GUI.Canvas;

namespace BetterGhPanel.UI
{
    // Uses the same host-owned text overlay as Grasshopper's native sliders.
    // Bounds are projected once when the editor opens; the host owns keyboard focus.
    internal sealed class CanvasTextInput : GH_TextBoxInputBase
    {
        private static WeakReference<CanvasTextInput>? _active;
        private Action<string>? _accepted;
        private object? _owner;
        private WeakReference<GH_Canvas>? _canvas;
        private RectangleF _canvasBounds;
        private Rectangle _screenBounds;

        private CanvasTextInput(Action<string> accepted) { _accepted = accepted; }

        internal static void Show(GH_Canvas canvas, object owner, RectangleF canvasBounds, string current, Action<string> accepted)
        {
            CloseActive();
            var projected = Rectangle.Round(canvas.Viewport.ProjectRectangle(canvasBounds));
            var bounds = projected;
            // The host reserves 40 UI-scaled pixels for accept/cancel. Compact
            // buttons still need a readable editor, including when zoomed out.
            bounds.Width = Math.Min(canvas.ClientSize.Width, Math.Max(bounds.Width, Grasshopper.Global_Proc.UiAdjust(160)));
            bounds.Height = Math.Min(canvas.ClientSize.Height, Math.Max(bounds.Height, Grasshopper.Global_Proc.UiAdjust(24)));
            bounds.X = Math.Max(0, Math.Min(bounds.X, canvas.ClientSize.Width - bounds.Width));
            bounds.Y = Math.Max(0, Math.Min(bounds.Y, canvas.ClientSize.Height - bounds.Height));
            var editor = new CanvasTextInput(accepted)
            {
                Bounds = bounds,
                _owner = owner,
                _canvas = new WeakReference<GH_Canvas>(canvas),
                _canvasBounds = canvasBounds,
                _screenBounds = projected
            };
            _active = new WeakReference<CanvasTextInput>(editor);
            editor.ShowTextInputBox(canvas, current ?? string.Empty, true, true);
        }

        protected override void HandleTextInputAccepted(string text)
        {
            if (_active != null && _active.TryGetTarget(out var current) && ReferenceEquals(current, this)) _active = null;
            var accepted = _accepted;
            _accepted = null;
            accepted?.Invoke(text);
        }

        internal static void Maintain(GH_Canvas canvas)
        {
            if (_active == null || !_active.TryGetTarget(out var current)) return;
            if (!current.IsTextInput) { current._accepted = null; _active = null; return; }
            if (current._canvas != null && current._canvas.TryGetTarget(out var ownerCanvas) && ReferenceEquals(ownerCanvas, canvas) &&
                current._screenBounds != Rectangle.Round(canvas.Viewport.ProjectRectangle(current._canvasBounds))) CloseActive();
        }

        internal static void CloseActive(object? owner = null)
        {
            if (_active != null && _active.TryGetTarget(out var current))
            {
                if (owner != null && !ReferenceEquals(current._owner, owner)) return;
                current._accepted = null;
                current.HideTextInputBox();
            }
            _active = null;
        }
    }
}
