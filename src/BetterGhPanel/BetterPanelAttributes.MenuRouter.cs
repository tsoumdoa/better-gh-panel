using System;
using System.Drawing;
using System.Windows.Forms;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Widgets;

#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private bool PreviewMenuRowEnabled(int index) => _menu.RowEnabled(index, PanelOwner.ShowItemOrder, PanelOwner.ShowValues, PanelOwner.ValuePalette);

        private void ChoosePreviewMenu(int index, GH_Canvas canvas)
        {
            if (!PreviewMenuRowEnabled(index)) return;
            var page = _menu.Kind;
            var next = page;
            if (page == CanvasMenuKind.Preview)
            {
                if (index < CanvasPreviewRows.ScopeCount) { PanelOwner.SetPreviewMode(_menu.PreviewChoiceAt(index).Mode!.Value); _menu.Close(); canvas.Invalidate(); return; }
                if (index == CanvasPreviewRows.ItemOrder) PanelOwner.SetShowItemOrder(!PanelOwner.ShowItemOrder);
                if (index == CanvasPreviewRows.Values) PanelOwner.SetShowValues(!PanelOwner.ShowValues);
                if (index == CanvasPreviewRows.Placement) next = CanvasMenuKind.DotPlacement;
                if (index == CanvasPreviewRows.Colour) next = CanvasMenuKind.ValuePalette;
            }
            else if (index == CanvasPreviewRows.Back) next = CanvasMenuKind.Preview;
            else if (page == CanvasMenuKind.DotPlacement) { PanelOwner.SetDotPlacement((PanelDotPlacement)(index - CanvasPreviewRows.FirstChoice)); next = CanvasMenuKind.Preview; }
            else if (index == CanvasPreviewRows.Reverse) PanelOwner.SetReversePalette(!PanelOwner.ReversePalette);
            else PanelOwner.SetValuePalette((PanelValuePalette)(index - CanvasPreviewRows.FirstChoice));
            if (next != page)
            {
                _menu.Close();
                OpenMenu(next, canvas);
            }
            else _menu.InvalidatePaint();
            canvas.Invalidate();
        }

        private MenuRouter? _menuRouter;

        // Set while the router forwards a menu click. PanelCanvasInput checks focus
        // after the whole dispatch, when a chosen item has already closed its menu.
        private bool _menuClickForwarded;

        private bool MenusOpen => _menu.Kind != CanvasMenuKind.None || _rowMenuResult != null;

        // Grasshopper gives a click to the topmost object whose Bounds contain it,
        // so a later component would take clicks on a menu beyond the panel.
        // Widgets see input after the active interaction and active object and
        // before FindAttribute; one routes it while a menu is open.
        private void RouteMenuInput(GH_Canvas canvas)
        {
            if (!MenusOpen || _menuRouter?.Routes(canvas) == true) return;
            _menuClickForwarded = false;
            if (_menuRouter != null && !ReferenceEquals(_menuRouter.Owner, canvas)) _menuRouter.Stop();
            _menuRouter = new MenuRouter(this, canvas);
        }

        // Menus answer their own tooltips: Preview's item order and
        // row actions. Other menu rows stay silent rather than show the panel's.
        private void MenuTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            if (_rowMenuResult != null && _rowMenuBounds.Contains(point)) { RowActionTooltip(point, e); return; }
            if (!PaintedViewIsCurrent() || !_menu.IsPaintedAt(point)) return;
            int row = (int)((point.Y - _menu.Bounds.Y) / 20);
            int index = _menu.Offset + row;
            if (_menu.Kind == CanvasMenuKind.Preview && row < _menu.Visible && index < _menu.Count && _menu.PreviewChoiceAt(index).IsItemOrder)
            {
                e.Title = "Show item order";
                e.Text = PanelOwner.ShowItemOrder ? "On" : "Off";
                e.Description = CanvasMenu.ItemOrderDescription;
            }

        }

        // Modal while a menu is open: input on the menu goes to the panel, and any
        // other click closes the menu and continues to its normal target. After a
        // left click closes the menu, the router absorbs only that click's trailing
        // double-click, as CanvasDoubleClickGuard does.
        private sealed class MenuRouter : GH_Widget
        {
            private enum State { Routing, Guarding, Stopping }

            private readonly BetterPanelAttributes _panel;
            private readonly Timer _timer;
            private State _state = State.Routing;
            private Rectangle _guard;
            private DateTime _guardUntil;
            private bool _guardKeepsFocus;
            // The closing click must still reach empty canvas (deselect, box select),
            // so the guard rectangle only starts containing points after its mouse-up.
            private bool _guardArmed;
            private bool _captured;
            private bool _stopped;

            internal MenuRouter(BetterPanelAttributes panel, GH_Canvas canvas)
            {
                _panel = panel;
                Owner = canvas;
                // The host enumerates widgets while it dispatches input, so removal
                // waits for a timer tick.
                _timer = new Timer { Interval = 100 };
                _timer.Tick += Tick;
                canvas.Disposed += Stopped;
                canvas.DocumentChanged += DocumentChanged;
                canvas.MouseUp += ArmGuard;
                canvas.Widgets.Add(this);
                _timer.Start();
            }

            internal bool Routes(GH_Canvas canvas) => _state == State.Routing && ReferenceEquals(Owner, canvas);

            private bool Routing => _state == State.Routing && _panel.MenusOpen;
            private bool Guarding => _state == State.Guarding && DateTime.UtcNow < _guardUntil;

            public override bool Visible { get => Routing || Guarding; set { if (!value) _state = State.Stopping; } }
            public override string Name => "Panel+ menu";
            public override string Description => string.Empty;
            public override Bitmap Icon_24x24 => null!;
            public override void Render(GH_Canvas canvas) { }
            // A visible widget that contains the point also suppresses the right-click
            // context menu and empty-canvas selection. Before the first paint of a menu
            // only its own rectangle counts, so a double-click there cannot fall through.
            public override bool Contains(Point control, PointF canvas) =>
                Routing && (ReferenceEquals(_panel._paintedCanvas, Owner) || _panel.MenuContains(canvas)) ||
                Guarding && _guardArmed && _guard.Contains(control);
            public override bool IsTooltipRegion(PointF canvas) => Routing && _panel.MenuContains(canvas);
            public override void SetupTooltip(PointF canvasPoint, GH_TooltipDisplayEventArgs e) => _panel.SetupTooltip(canvasPoint, e);
            // The host asks here when the mouse-up of the right click that opened a
            // row menu lands off the panel or on an object that ignores it; the
            // panel then never sees that mouse-up, so its flag ends here.
            public override void AppendToMenu(ToolStripDropDownMenu menu) => _panel._rowContextHandled = false;

            // Never modify the widget list in a handler: the host is enumerating it.
            // A stopped router that is still the host's active widget releases itself.
            public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
            {
                if (_stopped) return GH_ObjectResponse.Release;
                _panel._menuClickForwarded = false;
                if (_state == State.Guarding)
                {
                    if (Matches(e)) { _panel._menuClickForwarded = _guardKeepsFocus; return GH_ObjectResponse.Handled; }
                    Finish();
                    return GH_ObjectResponse.Ignore;
                }
                if (!Routing) return GH_ObjectResponse.Ignore;
                if (_panel.MenuContains(e.CanvasLocation))
                {
                    _panel._menuClickForwarded = true;
                    _panel.RespondToMouseDown(sender, e);
                    // Keep the right button's mouse-up from the object or canvas beneath,
                    // which would show its own context menu.
                    if (e.Button == MouseButtons.Right) { _captured = true; return GH_ObjectResponse.Capture; }
                    if (!_panel.MenusOpen) Guard(e, keepFocus: true);
                    return GH_ObjectResponse.Handled;
                }
                // Clicks on the panel itself keep its own menu handling.
                if (ReferenceEquals(sender.Document?.FindAttribute(e.CanvasLocation, false), _panel)) return GH_ObjectResponse.Ignore;
                _panel._menu.Close();
                _panel.CloseRowMenu();
                _panel.InvalidatePaintedHits();
                sender.Invalidate();
                // A right click continues to the real context menu, so nothing may contain it.
                if (e.Button == MouseButtons.Left) Guard(e, keepFocus: false); else Finish();
                return GH_ObjectResponse.Ignore;
            }

            public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
            {
                if (_stopped) return GH_ObjectResponse.Release;
                if (!_captured || e.Button != MouseButtons.Right) return GH_ObjectResponse.Ignore;
                _captured = false;
                // The router, not the panel, received this mouse-up.
                _panel._rowContextHandled = false;
                if (!_panel.MenusOpen) Finish();
                return GH_ObjectResponse.Release;
            }

            public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
            {
                if (_stopped) return GH_ObjectResponse.Release;
                _panel._menuClickForwarded = false;
                if (_captured) return GH_ObjectResponse.Capture;
                if (!Routing) return GH_ObjectResponse.Ignore;
                if (_panel.MenuContains(e.CanvasLocation))
                {
                    _panel.RespondToMouseMove(sender, e);
                    return GH_ObjectResponse.Handled;
                }
                // Leaving the menu clears its hover, whichever object takes the move.
                bool changed = _panel._menu.Kind != CanvasMenuKind.None && _panel._menu.Hover(e.CanvasLocation);
                if (_panel._rowMenuHovered >= 0) { _panel._rowMenuHovered = -1; changed = true; }
                if (changed) sender.Invalidate();
                return GH_ObjectResponse.Ignore;
            }

            public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e) =>
                _stopped ? GH_ObjectResponse.Release :
                Matches(e) || Routing && _panel.MenuContains(e.CanvasLocation) ? GH_ObjectResponse.Handled : GH_ObjectResponse.Ignore;

            // As CanvasDoubleClickGuard.Matches: only the second click of a double-click
            // inside the system double-click rectangle around the closing click.
            private bool Matches(GH_CanvasMouseEvent e) => e.Button == MouseButtons.Left && e.Clicks > 1 &&
                Guarding && _guard.Contains(e.ControlLocation);

            private void Guard(GH_CanvasMouseEvent e, bool keepFocus)
            {
                _guardKeepsFocus = keepFocus;
                var size = SystemInformation.DoubleClickSize;
                _guard = new Rectangle(e.ControlX - size.Width / 2, e.ControlY - size.Height / 2, size.Width + 1, size.Height + 1);
                _guardUntil = DateTime.UtcNow.AddMilliseconds(SystemInformation.DoubleClickTime);
                // A choosing click was consumed, so its guard arms at once (its
                // double-click precedes its mouse-up). A closing click must first
                // continue through Grasshopper, so its guard arms after mouse-up.
                _guardArmed = keepFocus;
                _state = State.Guarding;
                // The tick lands after the guard expires, so the widget leaves promptly.
                Restart(SystemInformation.DoubleClickTime + 20);
            }

            private void ArmGuard(object? sender, MouseEventArgs e)
            {
                if (_state == State.Guarding) _guardArmed = true;
            }

            private void Finish()
            {
                _state = State.Stopping;
                Restart(1);
            }

            private void Restart(int interval)
            {
                _timer.Stop();
                _timer.Interval = interval;
                _timer.Start();
            }

            private void Tick(object? sender, EventArgs e)
            {
                // A capture whose mouse-up never arrived (lost capture) no longer holds the router.
                if (_captured && (System.Windows.Forms.Control.MouseButtons & MouseButtons.Right) == 0) _captured = false;
                if (!_captured && !Routing && !Guarding) Stop();
            }

            private void DocumentChanged(GH_Canvas canvas, GH_CanvasDocumentChangedEventArgs e)
            {
                _panel._menu.Close();
                _panel.CloseRowMenu();
                Stop();
            }

            private void Stopped(object? sender, EventArgs e) => Stop();

            internal void Stop()
            {
                if (_stopped) return;
                _stopped = true;
                _state = State.Stopping;
                _timer.Stop();
                _timer.Dispose();
                Owner.Disposed -= Stopped;
                Owner.DocumentChanged -= DocumentChanged;
                Owner.MouseUp -= ArmGuard;
                Owner.Widgets.Remove(this);
                // A lost right-button capture would otherwise leave the host dispatching to a removed widget.
                if (ReferenceEquals(Owner.ActiveWidget, this)) Owner.ActiveWidget = null;
                if (!ReferenceEquals(_panel._menuRouter, this)) return;
                _panel._menuRouter = null;
                _panel._menuClickForwarded = false;
            }
        }
    }
}
#pragma warning restore CA1416
