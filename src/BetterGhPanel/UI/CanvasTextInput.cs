using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Grasshopper.GUI.Base;
using Grasshopper.GUI.Canvas;

// Grasshopper's text overlay is a WinForms control on Windows and Mac.
#pragma warning disable CA1416
namespace BetterGhPanel.UI
{
    // Uses the same host-owned text overlay as Grasshopper's native sliders.
    // Bounds are projected once when the editor opens; the host owns keyboard focus.
    // Optional live editors report debounced edits, then exactly one outcome:
    // Enter accepts; Escape, focus loss or a programmatic close abandons.
    internal sealed class CanvasTextInput : GH_TextBoxInputBase
    {
        private const int LiveDelay = 250;
        private static WeakReference<CanvasTextInput>? _active;
        private Action<string>? _accepted;
        private Action<string>? _changed;
        private Action? _abandoned;
        private Timer? _timer;
        private bool _completed;
        private object? _owner;
        private WeakReference<GH_Canvas>? _canvas;
        private RectangleF _canvasBounds;
        private Rectangle _screenBounds;
        private bool _accepting;

        private CanvasTextInput(Action<string> accepted) { _accepted = accepted; }

        internal static CanvasTextInput Show(GH_Canvas canvas, object owner, RectangleF canvasBounds, string current, Action<string> accepted,
            Action<string>? changed = null, Action? abandoned = null)
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
            if (changed != null || abandoned != null) editor.AttachLive(canvas, changed, abandoned);
            return editor;
        }

        private void AttachLive(GH_Canvas canvas, Action<string>? changed, Action? abandoned)
        {
            // The host adds one panel containing its TextBox on top of the canvas.
            var host = canvas.Controls.Cast<Control>().LastOrDefault(control => control.Controls.OfType<TextBox>().Any());
            var box = host?.Controls.OfType<TextBox>().FirstOrDefault();
            if (host == null || box == null) return;
            _changed = changed;
            _abandoned = abandoned;
            if (changed != null)
            {
                _timer = new Timer { Interval = LiveDelay };
                _timer.Tick += (_, __) =>
                {
                    _timer?.Stop();
                    if (!_completed && _accepted != null && !box.IsDisposed) _changed?.Invoke(box.Text);
                };
                box.TextChanged += (_, __) =>
                {
                    if (_completed || _timer == null) return;
                    _timer.Stop();
                    _timer.Start();
                };
            }
            void Hidden(object? sender, EventArgs e)
            {
                if (host.Parent != null) return;
                host.ParentChanged -= Hidden;
                _timer?.Stop();
                _timer?.Dispose();
                _timer = null;
                // The host hides the box before it reports Enter, so decide afterwards.
                void Decide()
                {
                    if (_completed) return;
                    _completed = true;
                    var callback = _abandoned;
                    _abandoned = null;
                    _changed = null;
                    callback?.Invoke();
                }
                if (canvas.IsHandleCreated && !canvas.IsDisposed) canvas.BeginInvoke((Action)Decide);
                else Decide();
            }
            host.ParentChanged += Hidden;
        }

        protected override void HandleTextInputAccepted(string text)
        {
            if (_active != null && _active.TryGetTarget(out var current) && ReferenceEquals(current, this)) _active = null;
            var accepted = _accepted;
            _accepted = null;
            _completed = true;
            _changed = null;
            _abandoned = null;
            _accepting = true;
            try { accepted?.Invoke(text); }
            finally { _accepting = false; }
        }

        internal static bool IsCurrent(CanvasTextInput? editor) => editor != null &&
            (editor._accepting || editor._accepted != null && editor.IsTextInput && _active != null &&
                _active.TryGetTarget(out var current) && ReferenceEquals(current, editor));

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

        // Do not close a newer editor that replaced this one on the shared canvas.
        internal static void Close(CanvasTextInput? editor)
        {
            if (editor != null && _active != null && _active.TryGetTarget(out var current) && ReferenceEquals(current, editor))
                CloseActive();
        }
    }
}
#pragma warning restore CA1416
