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
    // Esc, Cancel and deliberate Panel+ closes abandon. Enter and OK submit.
    // Every other close accepts only changed, valid text. Each editor completes once.
    internal sealed class CanvasTextInput : GH_TextBoxInputBase
    {
        private const int LiveDelay = 250;
        private static WeakReference<CanvasTextInput>? _active;
        private static CanvasTextInput? _pending;
        private Action? _decision;
        private bool _submit;
        private string _startingText = string.Empty;
        private Func<string, bool>? _valid;
        private Grasshopper.Kernel.GH_Document? _document;
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
        private bool _escaped;
        private bool _programmaticClose;

        private CanvasTextInput(Action<string> accepted) { _accepted = accepted; }

        internal static CanvasTextInput Show(GH_Canvas canvas, object owner, RectangleF canvasBounds, string current, Action<string> accepted,
            Action<string>? changed = null, Action? abandoned = null, Func<string, bool>? valid = null, bool alignRight = false)
        {
            CompletePending();
            if (_active != null && _active.TryGetTarget(out var previous)) previous.HideTextInputBox();
            CompletePending();
            var projected = Rectangle.Round(canvas.Viewport.ProjectRectangle(canvasBounds));
            var bounds = projected;
            // The host reserves 40 UI-scaled pixels for accept/cancel. Compact
            // buttons still need a readable editor, including when zoomed out.
            bounds.Width = Math.Min(canvas.ClientSize.Width, Math.Max(bounds.Width, Grasshopper.Global_Proc.UiAdjust(160)));
            bounds.Height = Math.Min(canvas.ClientSize.Height, Math.Max(bounds.Height, Grasshopper.Global_Proc.UiAdjust(24)));
            if (alignRight)
            {
                bounds.X = projected.Right - bounds.Width;
                bounds.Y = projected.Bottom - bounds.Height;
            }
            bounds.X = Math.Max(0, Math.Min(bounds.X, canvas.ClientSize.Width - bounds.Width));
            bounds.Y = Math.Max(0, Math.Min(bounds.Y, canvas.ClientSize.Height - bounds.Height));
            var editor = new CanvasTextInput(accepted)
            {
                Bounds = bounds,
                _owner = owner,
                _startingText = current ?? string.Empty,
                _document = canvas.Document,
                _valid = valid,
                _canvas = new WeakReference<GH_Canvas>(canvas),
                _canvasBounds = canvasBounds,
                _screenBounds = projected
            };
            _active = new WeakReference<CanvasTextInput>(editor);
            editor.ShowTextInputBox(canvas, current ?? string.Empty, true, true);
            editor.AttachLive(canvas, changed, abandoned);
            return editor;
        }

        private void AttachLive(GH_Canvas canvas, Action<string>? changed, Action? abandoned)
        {
            // The host adds one panel containing its TextBox on top of the canvas.
            var host = canvas.Controls.Cast<Control>().LastOrDefault(control => control.Controls.OfType<TextBox>().Any());
            var box = host?.Controls.OfType<TextBox>().FirstOrDefault();
            if (host == null || box == null) return;
            // KeyDown still runs after the host hides the box, before deferred Decide.
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Cancel) _escaped = true;
            };
            // Grasshopper paints Cancel in the rightmost 20 UI-scaled pixels.
            bool OnCancel(Point point) => point.X >= host.ClientSize.Width - Grasshopper.Global_Proc.UiAdjust(20)
                && host.ClientRectangle.Contains(point);
            host.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left && OnCancel(e.Location)) _escaped = true; };
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
                string text = box.Text;
                if (_active != null && _active.TryGetTarget(out var active) && ReferenceEquals(active, this)) _active = null;
                // The host hides the box before it reports Enter, so decide afterwards.
                _pending = this;
                _decision = () => Finish(text);
                if (canvas.IsHandleCreated && !canvas.IsDisposed) canvas.BeginInvoke((Action)(() => CompleteDecision()));
                else CompleteDecision();
            }
            host.ParentChanged += Hidden;
        }

        internal static void CompletePending() => _pending?.CompleteDecision();

        private void CompleteDecision()
        {
            var decision = _decision;
            _decision = null;
            if (ReferenceEquals(_pending, this)) _pending = null;
            decision?.Invoke();
        }

        // The host reports Enter/OK after hiding. Decide only after all host events,
        // so Escape and Cancel can mark abandonment before any callback runs.
        protected override void HandleTextInputAccepted(string text) => _submit = true;

        private void Finish(string text)
        {
            if (_completed) return;
            _completed = true;
            bool live = _canvas != null && _canvas.TryGetTarget(out var canvas) && !canvas.IsDisposed && canvas.Enabled &&
                _document?.Enabled == true && ReferenceEquals(canvas.Document, _document) &&
                (!(_owner is BetterPanelComponent panel) || !panel.Locked && ReferenceEquals(panel.OnPingDocument(), _document));
            var accepted = _accepted;
            var abandoned = _abandoned;
            _accepted = null;
            _abandoned = null;
            _changed = null;
            if (_escaped || _programmaticClose || !live || (!_submit && (string.Equals(text, _startingText, StringComparison.Ordinal) || _valid != null && !_valid(text))))
            {
                abandoned?.Invoke();
                return;
            }
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
            if (!current.IsTextInput) return;
            if (current._canvas != null && current._canvas.TryGetTarget(out var ownerCanvas) && ReferenceEquals(ownerCanvas, canvas) &&
                current._screenBounds != Rectangle.Round(canvas.Viewport.ProjectRectangle(current._canvasBounds))) current.HideTextInputBox();
        }

        internal static void CloseActive(object? owner = null)
        {
            if (_pending != null && (owner == null || ReferenceEquals(_pending._owner, owner))) _pending._programmaticClose = true;
            if (_active != null && _active.TryGetTarget(out var current))
            {
                if (owner != null && !ReferenceEquals(current._owner, owner)) return;
                current._programmaticClose = true;
                current._accepted = null;
                current.HideTextInputBox();
            }
            _active = null;
        }

        // Do not close a newer editor that replaced this one on the shared canvas.
        internal static void Close(CanvasTextInput? editor)
        {
            if (editor != null) editor._programmaticClose = true;
            if (editor != null && _active != null && _active.TryGetTarget(out var current) && ReferenceEquals(current, editor))
                CloseActive();
        }
    }
}
#pragma warning restore CA1416
