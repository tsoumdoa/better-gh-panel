using System;
using System.Drawing;
using System.Reflection;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Forms = System.Windows.Forms;

internal static class PanelHandleChecks
{
    internal static void Run(Assembly assembly)
    {
        using var document = new GH_Document();
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        component.Attributes.Pivot = new PointF(50, 50);
        document.AddObject(component, false);
        using var canvas = new GH_Canvas { Size = new Size(1000, 900), Document = document };
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        PanelTestAdapter.Resize(component, 300, 220);
        component.Attributes.PerformLayout();

        var move = PanelTestAdapter.Bounds(component, CanvasControl.Move);
        Check(move.Width >= 70 && move.Height >= 22 && !move.IntersectsWith(PanelTestAdapter.Bounds(component, CanvasControl.Preview)),
            "The minimum panel reserves a broad move handle separate from header controls");
        var start = new PointF(move.Right - 2, move.Bottom - 2);
        var pivot = component.Attributes.Pivot;
        var size = PanelTestAdapter.Size(component);
        var inputGrip = component.Params.Input[0].Attributes.InputGrip;
        var outputGrip = component.Params.Output[0].Attributes.OutputGrip;
        CanvasEvent("OnMouseMove", start, Forms.MouseButtons.None);
        Check(canvas.Cursor == Forms.Cursors.SizeAll, "Native header hover retains the move cursor");
        canvas.Viewport.Zoom = 0.4f;
        canvas.Viewport.ComputeProjection();
        foreach (var hiddenHandle in new[] { move, PanelTestAdapter.Bounds(component, CanvasControl.Resize) })
        {
            var point = new PointF(hiddenHandle.Right - 2, hiddenHandle.Bottom - 2);
            Check(component.Attributes.RespondToMouseMove(canvas, Mouse(point)) == GH_ObjectResponse.Ignore,
                "Hidden controls leave low-zoom mouse movement to Grasshopper");
            CanvasEvent("OnMouseMove", point, Forms.MouseButtons.None);
            Check(canvas.Cursor != Forms.Cursors.SizeAll && canvas.Cursor != Forms.Cursors.SizeNWSE,
                "Native low-zoom hover does not advertise invisible move or resize controls");
        }
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        CanvasEvent("OnMouseMove", start, Forms.MouseButtons.None);
        Check(canvas.Cursor == Forms.Cursors.SizeAll, "Zooming back in restores the move cursor");
        CanvasEvent("OnMouseDown", start);
        CanvasEvent("OnMouseMove", new PointF(start.X + 12, start.Y + 8));
        CanvasEvent("OnMouseMove", new PointF(start.X + 32, start.Y + 20));
        CanvasEvent("OnMouseUp", new PointF(start.X + 32, start.Y + 20));
        Check(component.Attributes.Pivot != pivot, "Dragging the padded header through native canvas dispatch moves the component");
        component.Attributes.PerformLayout();
        var delta = new SizeF(component.Attributes.Pivot.X - pivot.X, component.Attributes.Pivot.Y - pivot.Y);
        Check(PanelTestAdapter.Size(component) == size && component.Params.Input[0].Attributes.InputGrip == inputGrip + delta &&
            component.Params.Output[0].Attributes.OutputGrip == outputGrip + delta,
            "Native movement translates both wire grips and preserves the saved panel size");

        foreach (bool half in new[] { false, true })
        {
            PanelTestAdapter.Resize(component, 300, 220);
            component.Attributes.PerformLayout();
            if (half)
            {
                var display = PanelTestAdapter.Bounds(component, CanvasControl.Display);
                component.Attributes.RespondToMouseDown(canvas, Mouse(new PointF(display.X + 5, display.Y + 5)));
                component.Attributes.PerformLayout();
            }
            for (int edge = 0; edge < 4; edge++)
            {
                var panel = PanelTestAdapter.Bounds(component, CanvasControl.Panel);
                var resize = PanelTestAdapter.Bounds(component, CanvasControl.Resize);
                var units = PanelTestAdapter.Bounds(component, CanvasControl.Units);
                Check(!resize.IntersectsWith(units) && panel.Contains(resize), "The enlarged resize handle stays inside the panel and clear of Units");
                var point = new PointF(edge % 2 == 0 ? resize.Left + 1 : resize.Right - 1,
                    edge < 2 ? resize.Top + 1 : resize.Bottom - 1);
                CanvasEvent("OnMouseMove", point, Forms.MouseButtons.None);
                Check(canvas.Cursor == Forms.Cursors.SizeNWSE, "Native resize hover retains the diagonal cursor");
                var before = PanelTestAdapter.Size(component);
                Check(component.Attributes.RespondToMouseDown(canvas, Mouse(point)) == GH_ObjectResponse.Capture,
                    "Every corner of the resize target starts a resize gesture");
                var end = new PointF(point.X + 12, point.Y + 12);
                component.Attributes.RespondToMouseMove(canvas, Mouse(end));
                component.Attributes.RespondToMouseUp(canvas, Mouse(end));
                var after = PanelTestAdapter.Bounds(component, CanvasControl.Panel);
                Check(Math.Abs(after.Right - panel.Right - 12) < 0.01 && Math.Abs(after.Bottom - panel.Bottom - 12) < 0.01 &&
                    PanelTestAdapter.Size(component).Width == before.Width + 12,
                    "Full and Half resize handles follow the cursor from their padded edges");
            }
        }
        var activeResize = PanelTestAdapter.Bounds(component, CanvasControl.Resize);
        var activeStart = new PointF(activeResize.Right - 2, activeResize.Bottom - 2);
        var activeSize = PanelTestAdapter.Size(component);
        Check(component.Attributes.RespondToMouseDown(canvas, Mouse(activeStart)) == GH_ObjectResponse.Capture,
            "A visible resize target captures before the canvas zoom changes");
        canvas.Viewport.Zoom = 0.4f;
        canvas.Viewport.ComputeProjection();
        var activeEnd = new PointF(activeStart.X + 12, activeStart.Y + 12);
        Check(component.Attributes.RespondToMouseMove(canvas, Mouse(activeEnd)) == GH_ObjectResponse.Capture &&
            PanelTestAdapter.Size(component).Width == activeSize.Width + 12,
            "An active resize continues across the control visibility threshold");
        Check(component.Attributes.RespondToMouseUp(canvas, Mouse(activeEnd)) == GH_ObjectResponse.Release,
            "An active resize still releases normally after zooming out");
        component.DocumentContextChanged(document, GH_DocumentContext.Close);

        void CanvasEvent(string name, PointF point, Forms.MouseButtons button = Forms.MouseButtons.Left)
        {
            var location = Point.Round(canvas.Viewport.ProjectPoint(point));
            var args = new Forms.MouseEventArgs(button, name == "OnMouseMove" ? 0 : 1, location.X, location.Y, 0);
            typeof(Forms.Control).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, new object[] { args });
        }
    }

    private static GH_CanvasMouseEvent Mouse(PointF point) => new GH_CanvasMouseEvent(Point.Empty, point, Forms.MouseButtons.Left, 1, 0);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
