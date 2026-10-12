using System;
using System.Drawing;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Widgets;

namespace BetterGhPanel.UI
{
    // A layout change can expose another object or empty canvas beneath a second
    // click. Dispatch this one trailing gesture before Grasshopper hit-tests it.
    internal sealed class CanvasDoubleClickGuard : GH_Widget
    {
        private readonly Rectangle _region;
        private readonly DateTime _until;
        private readonly Timer _timer;

        private CanvasDoubleClickGuard(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            Owner = canvas;
            var size = SystemInformation.DoubleClickSize;
            _region = new Rectangle(e.ControlX - size.Width / 2, e.ControlY - size.Height / 2,
                size.Width + 1, size.Height + 1);
            _until = DateTime.UtcNow.AddMilliseconds(SystemInformation.DoubleClickTime);
            _timer = new Timer { Interval = Math.Max(1, SystemInformation.DoubleClickTime) };
            _timer.Tick += Stop;
            canvas.Disposed += Stop;
            canvas.DocumentChanged += DocumentChanged;
            canvas.Widgets.Add(this);
            _timer.Start();
        }

        internal static void Start(GH_Canvas canvas, GH_CanvasMouseEvent e) => new CanvasDoubleClickGuard(canvas, e);

        public override bool Visible { get; set; } = true;
        public override string Name => "Panel+ presentation gesture";
        public override string Description => string.Empty;
        public override Bitmap Icon_24x24 => null!;
        public override bool TooltipEnabled => false;
        public override void Render(GH_Canvas canvas) { }
        public override bool Contains(Point control, PointF canvas) => Visible && DateTime.UtcNow < _until && _region.Contains(control);

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (Matches(e)) return GH_ObjectResponse.Handled;
            // Do not modify the widget list while the host enumerates it. The
            // timer removes this guard; disabling it lets fresh input pass now.
            Visible = false;
            return GH_ObjectResponse.Ignore;
        }

        public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e) =>
            Matches(e) ? GH_ObjectResponse.Handled : GH_ObjectResponse.Ignore;

        private bool Matches(GH_CanvasMouseEvent e) => e.Button == MouseButtons.Left && e.Clicks > 1 &&
            Contains(e.ControlLocation, e.CanvasLocation);

        private void DocumentChanged(GH_Canvas canvas, GH_CanvasDocumentChangedEventArgs e) => Stop(null, EventArgs.Empty);

        private void Stop(object? sender, EventArgs e)
        {
            Visible = false;
            _timer.Stop();
            _timer.Dispose();
            Owner.Disposed -= Stop;
            Owner.DocumentChanged -= DocumentChanged;
            Owner.Widgets.Remove(this);
        }
    }
}
