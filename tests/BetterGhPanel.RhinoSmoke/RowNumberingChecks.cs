using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Forms = System.Windows.Forms;

internal static class RowNumberingChecks
{
    internal static async Task Run(Assembly assembly)
    {
        using var document = new GH_Document { Enabled = true };
        GH_Document.EnableSolutions = true;
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        var source = new Param_GenericObject();
        source.CreateAttributes();
        for (int i = 0; i < 120; i++)
            source.PersistentData.Append(new GH_Number(i * 37 % 120), new GH_Path(0, 2, i < 60 ? 4 : 5));
        document.AddObject(source, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        document.NewSolution(false, GH_SolutionMode.Silent);
        var session = PanelTestAdapter.Session(component);
        await Wait(session);
        var originalOutput = component.Params.Output[0].VolatileData.AllData(true).Cast<GH_Number>().Select(n => n.Value).ToArray();
        using var canvas = new GH_Canvas { Size = new Size(1100, 900) };
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        component.Attributes.Pivot = new PointF(50, 50);
        component.Attributes.PerformLayout();
        PanelTestAdapter.Units(component, PanelUnitConversion.None, 2);

        await Query(session, PanelSortMode.ValueAscending);
        using var ascending = Capture(component, canvas);
        Check(PanelTestAdapter.Tooltip(component, canvas, 117).Title == "Row 10 · {0;2;5} [57]",
            "Ascending row ten retains its original branch and item address");
        var selectedBounds = PanelTestAdapter.RowBounds(component, 117);
        Check(selectedBounds.Height == 17, "Native row numbering retains 17-unit row height");
        Click(component, canvas, selectedBounds);
        int gutterEdge = GutterEdge(ascending, component, 0);

        await Query(session, PanelSortMode.ValueDescending);
        using (var descending = Capture(component, canvas))
            Check(GutterEdge(descending, component, 107) == gutterEdge, "Asc and Desc retain the exact same painted gutter width");
        Check(PanelTestAdapter.SelectedPosition(component) == 117, "Sorting retains the selected source item");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Jump));
        CanvasEditorAdapter.Active(assembly).Submit("111");
        using (Capture(component, canvas)) { }
        Check(PanelTestAdapter.Offset(component) == 110 && PanelTestAdapter.Tooltip(component, canvas, 117).Title == "Row 111 · {0;2;5} [57]",
            "Row jump and scrolling use continuous view numbers beyond the viewport");
        Check(component.Params.Output[0].VolatileData.AllData(true).Cast<GH_Number>().Select(n => n.Value).SequenceEqual(originalOutput),
            "View numbering and sorting preserve the typed output order and values");

        await Query(session, PanelSortMode.ValueAscending, ">= 100");
        using (var filtered = Capture(component, canvas))
            Check(GutterEdge(filtered, component, 100) == gutterEdge, "Filtering across a digit boundary keeps the full-tree gutter capacity");
        Check(PanelTestAdapter.Tooltip(component, canvas, 100).Title == "Row 1 · {0;2;5} [40]",
            "Filtered rows restart at one and retain their pre-filter item index");
        await Query(session, PanelSortMode.SourceOrder);
        using (var sourceOrder = Capture(component, canvas))
            Check(GutterEdge(sourceOrder, component, 0) == gutterEdge, "Source order reserves the same numbering gutter");
        Check(PanelTestAdapter.Tooltip(component, canvas, 0).Title == "{0;2;4} [0]", "Source order keeps its original zero-based address");

        // Render the approved example through the real native component, not the HTML study.
        double[] values = { 42,18.25,76.8,9.5,54.1,31.6,88.9,23.45,61.2,12.75,47.35,35.8,95.4,67.65,
            7.25,11.3,16.2,27.4,33.75,40.2,48.3,50.6,58.9,70.2,82.4,91.8,99.2,105.6 };
        source.PersistentData.Clear();
        for (int i = 0; i < values.Length; i++)
            source.PersistentData.Append(new GH_Number(values[i]), new GH_Path(0, 2, i < 14 ? 4 : 5));
        source.ExpireSolution(false);
        document.NewSolution(false, GH_SolutionMode.Silent);
        await Wait(session);
        await Query(session, PanelSortMode.ValueAscending);
        using (Capture(component, canvas)) { }
        Click(component, canvas, PanelTestAdapter.RowBounds(component, 11));
        using (var preview = Capture(component, canvas)) Save(component, preview, "row-numbering");
        await Query(session, PanelSortMode.ValueDescending);
        using (var preview = Capture(component, canvas)) Save(component, preview, "row-numbering-desc");
        PanelTestAdapter.Resize(component, 300, 220);
        component.Attributes.PerformLayout();
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Display));
        using (var preview = Capture(component, canvas)) Save(component, preview, "row-numbering-half");
        Check(PanelTestAdapter.Tooltip(component, canvas, 27).Title == "Row 1 · {0;2;5} [13]" &&
            PanelTestAdapter.RowBounds(component, 27).Height == 17, "Minimum Half layout still draws a complete sorted row and address");
        component.DocumentContextChanged(document, GH_DocumentContext.Close);
    }

    private static Bitmap Capture(GH_Component component, GH_Canvas canvas)
    {
        var bounds = component.Attributes.Bounds;
        var bitmap = new Bitmap((int)(bounds.Width + 30) * 2, (int)(bounds.Height + 30) * 2);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(239, 240, 237));
        graphics.ScaleTransform(2, 2);
        graphics.TranslateTransform(15 - bounds.X, 15 - bounds.Y);
        PanelTestAdapter.Render(component, canvas, graphics);
        return bitmap;
    }

    private static int GutterEdge(Bitmap bitmap, GH_Component component, int sourcePosition)
    {
        var row = PanelTestAdapter.RowBounds(component, sourcePosition);
        var panel = component.Attributes.Bounds;
        int left = (int)((row.X - panel.X + 15) * 2), y = (int)((row.Y - panel.Y + 15) * 2);
        // Use the uninterrupted background at the top of the row, above all glyphs.
        int color = Color.FromArgb(238, 242, 236).ToArgb();
        int x = left + 2;
        while (x < bitmap.Width && bitmap.GetPixel(x, y).ToArgb() == color) x++;
        Check(x > left + 80 && x < left + 240, "Native numbering gutter paints without clipping its digits");
        return x - left;
    }

    private static void Save(GH_Component component, Bitmap bitmap, string name) =>
        bitmap.Save(Path.Combine(Path.GetDirectoryName(component.GetType().Assembly.Location)!, "rhino-smoke-" + name + ".png"));

    private static void Click(GH_Component component, GH_Canvas canvas, RectangleF bounds)
    {
        var point = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        component.Attributes.RespondToMouseDown(canvas, new GH_CanvasMouseEvent(Point.Empty, point, Forms.MouseButtons.Left, 1, 0));
    }

    private static async Task Query(object session, PanelSortMode sort, string search = "")
    {
        session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { new PanelQueryOptions { SortMode = sort, Search = search } });
        await Wait(session);
    }

    private static async Task Wait(object session)
    {
        var timer = Stopwatch.StartNew();
        while ((bool)session.GetType().GetProperty("IsBusy")!.GetValue(session)!)
        {
            if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Row numbering query did not finish");
            await Task.Delay(10);
        }
        if (session.GetType().GetProperty("Error")!.GetValue(session) is string error) throw new InvalidOperationException(error);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
