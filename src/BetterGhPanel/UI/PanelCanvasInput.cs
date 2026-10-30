using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI.Canvas;

// Grasshopper's canvas and editor are WinForms controls on Windows and Mac.
#pragma warning disable CA1416
namespace BetterGhPanel.UI
{
    internal interface IPanelInputTarget
    {
        bool OwnsCanvas(GH_Canvas canvas);
        bool ContainsCanvasPoint(PointF point);
        bool HandleWheel(GH_Canvas canvas, PointF point, int delta);
        bool HandleKey(GH_Canvas canvas, Keys keyData);
        void FocusReleased();
    }

    // Grasshopper always zooms on the wheel and routes keys to canvas navigation,
    // menu shortcuts and the Rhino command line. Panel+ claims the wheel and a few
    // keys only while one panel is explicitly focused (clicked into, drawn with a
    // focus ring). Everything else, and every gesture with that panel unfocused,
    // reaches Grasshopper's own handlers unchanged. A click elsewhere or Esc
    // releases focus. If the host handlers cannot be located safely, Panel+
    // leaves them untouched and these conveniences are simply unavailable.
    internal static class PanelCanvasInput
    {
        private static WeakReference<IPanelInputTarget>? _focused;
        private static readonly ConditionalWeakTable<GH_Canvas, object> HookedCanvases = new ConditionalWeakTable<GH_Canvas, object>();
        private static WeakReference<Control>? _hookedEditor;

        internal static void Focus(IPanelInputTarget target, GH_Canvas canvas)
        {
            if (TryGetFocused(out var current) && !ReferenceEquals(current, target))
            {
                _focused = null;
                current.FocusReleased();
            }
            _focused = new WeakReference<IPanelInputTarget>(target);
            HookCanvas(canvas);
            HookEditor();
        }

        internal static bool IsFocused(IPanelInputTarget target) => TryGetFocused(out var current) && ReferenceEquals(current, target);

        internal static void Release(IPanelInputTarget target)
        {
            if (!IsFocused(target)) return;
            _focused = null;
            target.FocusReleased();
        }

        private static bool TryGetFocused(out IPanelInputTarget target)
        {
            target = null!;
            return _focused != null && _focused.TryGetTarget(out target!) && target != null;
        }

        private static void ReleaseCurrent()
        {
            if (!TryGetFocused(out var current)) return;
            _focused = null;
            current.FocusReleased();
        }

        private static void HookCanvas(GH_Canvas canvas)
        {
            if (HookedCanvases.TryGetValue(canvas, out _)) return;
            HookedCanvases.Add(canvas, new object());
            canvas.MouseDown += CanvasMouseDown;
            canvas.DocumentChanged += (_, __) => ReleaseCurrent();
            canvas.Disposed += (_, __) => ReleaseCurrent();
            try
            {
                var method = typeof(GH_Canvas).GetMethod("Canvas_MouseWheel", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(object), typeof(MouseEventArgs) }, null);
                if (method == null) return;
                var original = (MouseEventHandler?)Delegate.CreateDelegate(typeof(MouseEventHandler), canvas, method, false);
                if (original == null || !Reroute(canvas, "MouseWheel", original)) return;
                canvas.MouseWheel += (sender, e) =>
                {
                    bool handled = false;
                    try { handled = RouteWheel(canvas, e); }
                    catch (Exception) { handled = false; }
                    if (!handled) original(sender, e);
                };
            }
            catch (Exception) { }
        }

        private static void HookEditor()
        {
            var editor = Instances.DocumentEditor;
            if (editor == null || _hookedEditor != null && _hookedEditor.TryGetTarget(out var hooked) && ReferenceEquals(hooked, editor)) return;
            try
            {
                var method = editor.GetType().GetMethod("EditorKeyDown", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(object), typeof(KeyEventArgs) }, null);
                if (method == null) return;
                var original = (KeyEventHandler?)Delegate.CreateDelegate(typeof(KeyEventHandler), editor, method, false);
                if (original == null || !Reroute(editor, "KeyDown", original)) return;
                _hookedEditor = new WeakReference<Control>(editor);
                editor.KeyDown += (sender, e) =>
                {
                    bool handled = false;
                    try { handled = RouteKey(e); }
                    catch (Exception) { handled = false; }
                    if (handled)
                    {
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        return;
                    }
                    original(sender, e);
                };
            }
            catch (Exception) { }
        }

        // Remove the host's own handler only when its presence and removal can be
        // verified, so the replacement can never run beside it.
        private static bool Reroute(Control control, string eventName, Delegate original)
        {
            if (!Subscribed(control, eventName, original, out bool before) || !before) return false;
            var info = typeof(Control).GetEvent(eventName);
            if (info == null) return false;
            info.RemoveEventHandler(control, original);
            if (Subscribed(control, eventName, original, out bool after) && !after) return true;
            info.AddEventHandler(control, original);
            return false;
        }

        private static bool Subscribed(Control control, string eventName, Delegate handler, out bool subscribed)
        {
            subscribed = false;
            var events = typeof(Component).GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(control) as EventHandlerList;
            // .NET Framework and .NET name the event keys differently.
            var key = (typeof(Control).GetField("Event" + eventName, BindingFlags.Static | BindingFlags.NonPublic) ??
                typeof(Control).GetField("s_" + char.ToLowerInvariant(eventName[0]) + eventName.Substring(1) + "Event", BindingFlags.Static | BindingFlags.NonPublic))?.GetValue(null);
            if (events == null || key == null) return false;
            subscribed = events[key]?.GetInvocationList().Contains(handler) == true;
            return true;
        }

        private static void CanvasMouseDown(object? sender, MouseEventArgs e)
        {
            if (sender is not GH_Canvas canvas || !TryGetFocused(out var target)) return;
            if (!target.OwnsCanvas(canvas) || !target.ContainsCanvasPoint(canvas.Viewport.UnprojectPoint(e.Location))) ReleaseCurrent();
        }

        private static bool RouteWheel(GH_Canvas canvas, MouseEventArgs e)
        {
            // Modifier + wheel always reaches Grasshopper, so zooming stays one key away.
            if (Control.ModifierKeys != Keys.None || !TryGetFocused(out var target)) return false;
            if (!target.OwnsCanvas(canvas)) { ReleaseCurrent(); return false; }
            if (canvas.ActiveInteraction != null) return false;
            return target.HandleWheel(canvas, canvas.Viewport.UnprojectPoint(e.Location), e.Delta);
        }

        private static bool RouteKey(KeyEventArgs e)
        {
            if (!TryGetFocused(out var target)) return false;
            var canvas = Instances.ActiveCanvas;
            if (canvas == null) return false;
            if (!target.OwnsCanvas(canvas)) { ReleaseCurrent(); return false; }
            // A text box (including Panel+'s own editors) keeps every key it receives.
            if (!canvas.Focused || canvas.ActiveInteraction != null) return false;
            return target.HandleKey(canvas, e.KeyData);
        }
    }
}
#pragma warning restore CA1416
