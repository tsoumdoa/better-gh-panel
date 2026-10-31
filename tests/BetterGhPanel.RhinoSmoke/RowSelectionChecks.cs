using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Forms = System.Windows.Forms;

internal static class RowSelectionChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static Task Run(Assembly assembly) => PanelHostFixture.RunWithEditor(assembly, Verify);
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);

    private static async Task Verify(Assembly assembly)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        foreach (int i in Enumerable.Range(0, 80))
            fixture.Source.PersistentData.Append(new GH_Number(i), new GH_Path(0));
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 80);
        var component = fixture.Component;
        var attributes = component.Attributes;
        using var canvas = new GH_Canvas { Document = fixture.Document, Size = new Size(1000, 700) };
        _ = canvas.Handle;
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = Point.Empty;
        PanelTestAdapter.Resize(component, 500, 360);

        void Render(RectangleF? clip = null, RectangleF? hole = null)
        {
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            using var graphics = Graphics.FromImage(bitmap);
            canvas.Viewport.ApplyProjection(graphics);
            if (clip.HasValue) graphics.SetClip(clip.Value, CombineMode.Intersect);
            if (hole.HasValue) graphics.SetClip(hole.Value, CombineMode.Exclude);
            PanelTestAdapter.Render(component, canvas, graphics);
        }
        void Invalidate() => attributes.GetType().GetMethod("InvalidatePaintedHits", Instance)!
            .Invoke(attributes, new object[] { true });
        int Field(string name) => (int)attributes.GetType().GetField(name, Instance)!.GetValue(attributes)!;

        foreach (float zoom in new[] { 0.73f, 1f, 1.27f })
        foreach (float pan in new[] { 0f, 0.5f })
        foreach (bool atEdge in new[] { false, true })
        {
            canvas.Viewport.Zoom = zoom;
            canvas.Viewport.ComputeProjection();
            var visible = canvas.Viewport.VisibleRegion;
            attributes.Pivot = new PointF(visible.Left + (atEdge ? -30 : 30) + pan / zoom, visible.Top + 30 + pan / zoom);
            attributes.PerformLayout();
            Invalidate(); Render();
            int expected = Field("_lastShownItem") - Field("_firstShownItem") + 1;
            Console.WriteLine($"Row coverage: zoom={zoom}, pan={pan}, viewportEdge={atEdge}, hits={PanelTestAdapter.Hits(component, "_rowHits")}/{expected}");
            Check(expected > 2 && PanelTestAdapter.Hits(component, "_rowHits") == expected,
                $"Every visible row has a hit after a full paint at zoom {zoom}, offset {pan}px");

            var list = PanelTestAdapter.Bounds(component, CanvasControl.List);
            var row = PanelTestAdapter.RowBounds(component, 1);
            // Model a device clip whose inverse transform rounds inward by half a pixel.
            var roundedClip = RectangleF.FromLTRB(list.Left + 0.5f / zoom, list.Top,
                list.Right - 0.5f / zoom, list.Bottom);
            Invalidate(); Render(roundedClip);
            Check(PanelTestAdapter.Hits(component, "_rowHits") == expected,
                $"Subpixel clip edges retain every row hit at zoom {zoom}, offset {pan}px");
            PanelTestAdapter.CanvasClick(canvas, new PointF(row.X + 80, row.Y + row.Height / 2));
            Check(PanelTestAdapter.SelectedPosition(component) == 1,
                "A normal canvas click selects the row after subpixel clipping");

            Invalidate(); Render(hole: new RectangleF(row.X + 70, row.Y + 3, 20, row.Height - 6));
            Check(PanelTestAdapter.Hits(component, "_rowHits") == Field("_lastShownItem") - Field("_firstShownItem") &&
                !PanelTestAdapter.TryRowBounds(component, 1, out _),
                "A clip hole inside a row prevents its registration despite covered outer bounds");

            // A genuine partial repaint cannot publish the rows it did not cover.
            Invalidate(); Render(new RectangleF(list.X, row.Y, list.Width, row.Height));
            Check(PanelTestAdapter.Hits(component, "_rowHits") == 1 &&
                PanelTestAdapter.TryRowBounds(component, 1, out _),
                "A partial repaint registers only the covered row");
            Render(new RectangleF(list.X + 30, row.Y + 2, 20, 5));
            Check(PanelTestAdapter.Hits(component, "_rowHits") == 1,
                "An unchanged partial repaint preserves earlier painted targets");
            fixture.ClearSelection();
        }

        var savedPivot = attributes.Pivot;
        float savedZoom = canvas.Viewport.Zoom;
        canvas.Viewport.Zoom = 0.73f; canvas.Viewport.ComputeProjection();
        attributes.PerformLayout(); Invalidate(); Render();
        var bottomRow = PanelTestAdapter.RowBounds(component, 1);
        float shift = canvas.Viewport.VisibleRegion.Bottom - 1.2f / 0.73f - bottomRow.Top;
        attributes.Pivot = new PointF(attributes.Pivot.X, attributes.Pivot.Y + shift);
        attributes.PerformLayout(); bottomRow.Offset(0, shift);
        Invalidate(); Render(new RectangleF(bottomRow.X, bottomRow.Y - 20 / 0.73f, bottomRow.Width, 18 / 0.73f));
        Check(!PanelTestAdapter.TryRowBounds(component, 1, out _),
            "A repaint ending above a 1.2-device-pixel viewport-bottom row cannot register that unpainted row at zoom 0.73");
        attributes.Pivot = savedPivot;
        canvas.Viewport.Zoom = savedZoom; canvas.Viewport.ComputeProjection(); attributes.PerformLayout();

        // Selection publication keeps the same query result, so a second normal
        // click must work without waiting for another paint.
        Render();
        var paintedResult = fixture.Result;
        PointF PointOn(int position)
        {
            var bounds = PanelTestAdapter.RowBounds(component, position);
            return new PointF(bounds.X + 80, bounds.Y + bounds.Height / 2);
        }
        PanelTestAdapter.CanvasClick(canvas, PointOn(2));
        await fixture.Ready(() => fixture.Values(1) == "2");
        PanelTestAdapter.CanvasClick(canvas, PointOn(3));
        Check(ReferenceEquals(paintedResult, fixture.Result) && PanelTestAdapter.SelectedPosition(component) == 3,
            "A selection-only solution retains current hits for the next normal click");

        attributes.GetType().GetMethod("SuppressTrailingDoubleClick", Instance)!.Invoke(attributes, Array.Empty<object>());
        var point = PointOn(4);
        PanelTestAdapter.CanvasClick(canvas, point);
        Check(PanelTestAdapter.SelectedPosition(component) == 4,
            "A fresh single click is accepted during an earlier gesture's double-click suppression interval");
        var doubleClick = new GH_CanvasMouseEvent(canvas.Viewport,
            new Forms.MouseEventArgs(Forms.MouseButtons.Left, 2,
                (int)canvas.Viewport.ProjectPoint(point).X, (int)canvas.Viewport.ProjectPoint(point).Y, 0));
        attributes.RespondToMouseDoubleClick(canvas, doubleClick);
        Check(fixture.SelectedCount == 0, "A real double-click retains the existing explicit deselection behavior");

        attributes.GetType().GetMethod("SetOffset", Instance)!.Invoke(attributes, new object[] { canvas, 5 });
        PanelTestAdapter.CanvasClick(canvas, point);
        Check(fixture.SelectedCount == 0, "An offset change cannot select a row using its old painted address");
        Render();
        point = PointOn(6);
        fixture.Query(new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
        await fixture.Ready(() => !ReferenceEquals(paintedResult, fixture.Result));
        PanelTestAdapter.CanvasClick(canvas, point);
        Check(fixture.SelectedCount == 0, "A changed result cannot select a row using its old painted address");
    }
}
