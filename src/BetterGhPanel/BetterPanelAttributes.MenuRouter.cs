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
        private MenuRouter? _menuRouter;

        private bool MenusOpen => _menu.Kind != CanvasMenuKind.None || _rowMenuResult != null;

        // Grasshopper gives a click to the topmost object whose Bounds contain it,
        // so a later component would take clicks on a menu beyond the panel.
        // Widgets see input before any object; one routes it while a menu is open.
        private void RouteMenuInput(GH_Canvas canvas)
        {
            if (MenusOpen && _menuRouter?.Routes(canvas) != true) _menuRouter = new MenuRouter(this, canvas);
        }

        // Menus answer their own tooltips: Preview's item order, Stats rows and
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
            else if (_menu.Kind == CanvasMenuKind.Statistics)
            {
                if (!StatisticsAreCurrent()) { InvalidateStatistics(); return; }
                e.Title = "Tree statistics";
                if (row < _menu.Visible && index < _menu.Count)
                {
                    e.Text = _menu.StatisticsDetails(index, PanelOwner.DecimalPlaces);
                    e.Description = index == 6 || index == 7 || index >= CanvasStatistics.OverviewRows ? CanvasStatistics.UniqueScope :
                        index == 8 ? "Source counts after filters are measured before Create set removes duplicate occurrences. Valid, invalid and unverified are distinct." : CanvasStatistics.Scope;
                }
                else e.Description = "Use Previous and Next to inspect all values.";
            }
        }

        // Modal while a menu is open: input on the menu goes to the panel, and any
        // other click closes the menu and continues to its normal target.
        private sealed class MenuRouter : GH_Widget
        {
            private readonly BetterPanelAttributes _panel;
            private readonly Timer _timer;
            private GH_CanvasMouseEvent? _trailing;
            private bool _routing = true;

            internal MenuRouter(BetterPanelAttributes panel, GH_Canvas canvas)
            {
                _panel = panel;
                Owner = canvas;
                // The host enumerates widgets while it dispatches input. Removal and
                // the trailing-click guard wait for a tick, as CanvasDoubleClickGuard does.
                _timer = new Timer { Interval = 100 };
                _timer.Tick += Tick;
                canvas.Disposed += Stop;
                canvas.DocumentChanged += DocumentChanged;
                canvas.Widgets.Add(this);
                _timer.Start();
            }

            internal bool Routes(GH_Canvas canvas) => _routing && ReferenceEquals(Owner, canvas);

            public override bool Visible { get => _routing && _panel.MenusOpen; set => _routing = value; }
            public override string Name => "Panel+ menu";
            public override string Description => string.Empty;
            public override Bitmap Icon_24x24 => null!;
            public override void Render(GH_Canvas canvas) { }
            public override bool Contains(Point control, PointF canvas) => Visible;
            public override bool IsTooltipRegion(PointF canvas) => Visible && _panel.MenuContains(canvas);
            public override void SetupTooltip(PointF canvasPoint, GH_TooltipDisplayEventArgs e) => _panel.SetupTooltip(canvasPoint, e);

            public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
            {
                bool onMenu = _panel.MenuContains(e.CanvasLocation);
                if (onMenu) _panel.RespondToMouseDown(sender, e);
                // Clicks on the panel itself keep its own menu handling.
                else if (ReferenceEquals(sender.Document?.FindAttribute(e.CanvasLocation, false), _panel)) return GH_ObjectResponse.Ignore;
                else
                {
                    _panel._menu.Close();
                    _panel.CloseRowMenu();
                    _panel.InvalidatePaintedHits();
                    sender.Invalidate();
                }
                if (!_panel.MenusOpen)
                {
                    _routing = false;
                    _trailing = e;
                    _timer.Interval = 1;
                }
                return onMenu ? GH_ObjectResponse.Handled : GH_ObjectResponse.Ignore;
            }

            public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
            {
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
                _panel.MenuContains(e.CanvasLocation) ? GH_ObjectResponse.Handled : GH_ObjectResponse.Ignore;

            private void Tick(object? sender, EventArgs e)
            {
                if (Visible) return;
                var trailing = _trailing;
                Stop(sender, e);
                if (trailing != null && !Owner.IsDisposed) CanvasDoubleClickGuard.Start(Owner, trailing);
            }

            private void DocumentChanged(GH_Canvas canvas, GH_CanvasDocumentChangedEventArgs e)
            {
                _panel._menu.Close();
                _panel.CloseRowMenu();
                Stop(null, EventArgs.Empty);
            }

            private void Stop(object? sender, EventArgs e)
            {
                _routing = false;
                _timer.Stop();
                _timer.Dispose();
                Owner.Disposed -= Stop;
                Owner.DocumentChanged -= DocumentChanged;
                Owner.Widgets.Remove(this);
            }
        }
    }
}
#pragma warning restore CA1416
