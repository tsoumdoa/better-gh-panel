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
        private WeakReference<GH_Canvas>? _canvas;
        private RectangleF _canvasBounds;
        private Rectangle _screenBounds;

        private CanvasTextInput(Action<string> accepted) { _accepted = accepted; }

        internal static void Show(GH_Canvas canvas, RectangleF canvasBounds, string current, Action<string> accepted)
        {
            CloseActive();
            var editor = new CanvasTextInput(accepted)
            {
                Bounds = Rectangle.Round(canvas.Viewport.ProjectRectangle(canvasBounds)),
                _canvas = new WeakReference<GH_Canvas>(canvas),
                _canvasBounds = canvasBounds
            };
            editor._screenBounds = editor.Bounds;
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

        internal static void CloseActive()
        {
            if (_active != null && _active.TryGetTarget(out var current))
            {
                current._accepted = null;
                current.HideTextInputBox();
            }
            _active = null;
        }
    }
}
