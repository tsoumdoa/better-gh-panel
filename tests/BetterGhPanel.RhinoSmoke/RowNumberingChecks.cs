using System;
using System.Collections;
using System.Collections.Generic;
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
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

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
        PanelTestAdapter.Resize(component, 360, 480);
        component.Attributes.PerformLayout();
        PanelTestAdapter.Units(component, PanelUnitConversion.None, 2);

        await Query(session, PanelSortMode.ValueAscending);
        using var ascending = Capture(component, canvas);
        Check(PanelTestAdapter.Tooltip(component, canvas, 7).Title == "Row 10 · {0;2;4} [7]",
            "Ascending row ten retains its original branch and item address");
        var selectedBounds = PanelTestAdapter.RowBounds(component, 7);
        Check(selectedBounds.Height == 17, "Native row numbering retains 17-unit row height");
        Click(component, canvas, selectedBounds);
        int gutterEdge = GutterEdge(ascending, component, 0);

        await Query(session, PanelSortMode.ValueDescending);
        using (var descending = Capture(component, canvas))
            Check(GutterEdge(descending, component, 55) == gutterEdge, "Asc and Desc retain the exact same painted gutter width");
        Check(PanelTestAdapter.SelectedPosition(component) == 7, "Sorting retains the selected source item");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Jump));
        CanvasEditorAdapter.Active(assembly).Submit("116");
        using (Capture(component, canvas)) { }
        Check(PanelTestAdapter.Offset(component) == 115 && PanelTestAdapter.Tooltip(component, canvas, 117).Title == "Row 116 · {0;2;5} [57]" &&
            BranchPaths(component).SequenceEqual(new[] { "{0;2;5}" }),
            "Row jump uses continuous view numbers across branches and restores the scrolled branch heading");
        Check(component.Params.Output[0].VolatileData.AllData(true).Cast<GH_Number>().Select(n => n.Value).SequenceEqual(originalOutput),
            "View numbering and sorting preserve the typed output order and values");

        await Query(session, PanelSortMode.ValueAscending, ">= 100");
        using (var filtered = Capture(component, canvas))
            Check(GutterEdge(filtered, component, 6) == gutterEdge, "Filtering across a digit boundary keeps the full-tree gutter capacity");
        Check(PanelTestAdapter.Tooltip(component, canvas, 6).Title == "Row 1 · {0;2;4} [6]",
            "Filtered rows restart at one and retain their pre-filter item index");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Jump));
        CanvasEditorAdapter.Active(assembly).Submit("11");
        using (Capture(component, canvas)) { }
        Check(PanelTestAdapter.Tooltip(component, canvas, 100).Title == "Row 11 · {0;2;5} [40]",
            "The second filtered branch follows the first even when it contains a smaller value");
        await Query(session, PanelSortMode.SourceOrder);
        using (var sourceOrder = Capture(component, canvas))
            Check(GutterEdge(sourceOrder, component, 0) < gutterEdge, "Source order only reserves space for the original item index");
        Check(PanelTestAdapter.Tooltip(component, canvas, 0).Title == "{0;2;4} [0]", "Source order keeps its original zero-based address");

        await VerifyBranchOrderAndCopy(component, source, document, session, canvas);

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
        Check(PanelTestAdapter.Tooltip(component, canvas, 12).Title == "Row 1 · {0;2;4} [12]" &&
            PanelTestAdapter.RowBounds(component, 12).Height == 17 && BranchPaths(component).Length == 0,
            "Minimum Half layout draws a complete sorted row with an inline branch address");
        component.DocumentContextChanged(document, GH_DocumentContext.Close);
    }

    private static async Task VerifyBranchOrderAndCopy(GH_Component component, Param_GenericObject source,
        GH_Document document, object session, GH_Canvas canvas)
    {
        double[] values = { 40, 10, 30, 20, 5, 15 };
        source.PersistentData.Clear();
        for (int i = 0; i < values.Length; i++)
            source.PersistentData.Append(new GH_Number(values[i]), new GH_Path(0, 2, i < 3 ? 4 : 5));
        source.ExpireSolution(false);
        document.NewSolution(false, GH_SolutionMode.Silent);
        await Wait(session);
        var previousClipboard = Forms.Clipboard.GetDataObject();
        try
        {
            await Verify(PanelSortMode.ValueAscending, "", new[] { 1, 2, 0, 4, 5, 3 },
                new[] { "10.00", "30.00", "40.00", "5.00", "15.00", "20.00" });
            await Verify(PanelSortMode.ValueDescending, "", new[] { 0, 2, 1, 3, 5, 4 },
                new[] { "40.00", "30.00", "10.00", "20.00", "15.00", "5.00" });
            Check(component.Params.Output[0].VolatileData.AllData(true).Cast<GH_Number>().Select(n => n.Value).SequenceEqual(values),
                "Per-branch view sorting and copying preserve the original typed output order");
            await Verify(PanelSortMode.ValueAscending, ">= 15", new[] { 2, 0, 5, 3 },
                new[] { "30.00", "40.00", "15.00", "20.00" });
            await Verify(PanelSortMode.ValueDescending, ">= 15", new[] { 0, 2, 3, 5 },
                new[] { "40.00", "30.00", "20.00", "15.00" });
        }
        finally
        {
            if (previousClipboard == null) Forms.Clipboard.Clear();
            else Forms.Clipboard.SetDataObject(previousClipboard, true);
        }

        async Task Verify(PanelSortMode sort, string filter, int[] expectedPositions, string[] expectedValues)
        {
            await Query(session, sort, filter);
            using (Capture(component, canvas)) { }
            var result = (PanelQueryResult)session.GetType().GetProperty("Result")!.GetValue(session)!;
            Check(result.RowPositions.SequenceEqual(expectedPositions),
                $"Native {sort} keeps branch order and sorts each matching branch independently (filter '{filter}')");
            Check(BranchPaths(component).SequenceEqual(new[] { "{0;2;4}", "{0;2;5}" }),
                "Sorted canvas renders one heading per visible branch in tree order");
            for (int i = 0; i < expectedPositions.Length; i++)
            {
                int position = expectedPositions[i];
                Check(PanelTestAdapter.Tooltip(component, canvas, position).Title ==
                    $"Row {i + 1} · {{0;2;{(position < 3 ? 4 : 5)}}} [{position % 3}]",
                    "Sorted tooltip preserves its original branch index and continuous view position");
            }
            var firstBranch = Field<IEnumerable>(component.Attributes, "_branchHits").Cast<object>().First();
            Click(component, canvas, Property<RectangleF>(firstBranch, "Bounds"));
            Check(Selected(component, result).SequenceEqual(expectedPositions.Where(position => position < 3)),
                "Clicking a sorted branch heading selects exactly that branch in displayed order");
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.SelectAll));
            Check(Selected(component, result).SequenceEqual(expectedPositions),
                "Select all follows the per-branch displayed order, including filtered subsets");
            Click(component, canvas, PanelTestAdapter.RowBounds(component, expectedPositions[0]), Forms.MouseButtons.Right);
            using (var paintedMenu = Capture(component, canvas)) { }
            var menu = Field<RectangleF>(component.Attributes, "_rowMenuBounds");
            Check(!menu.IsEmpty && ReferenceEquals(Field<object>(component.Attributes, "_rowMenuResult"), result),
                "Sorted value copy opens a context menu for the current query");
            using (Capture(component, canvas)) { }
            string beforeCopy = RowActionChecks.ClipboardState();
            Click(component, canvas, new RectangleF(menu.X, menu.Y + 20, menu.Width, 20));
            var timer = Stopwatch.StartNew();
            while (Field<bool>(component.Attributes, "_copyPending"))
            {
                if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Sorted value copy did not finish");
                await Task.Delay(10);
            }
            string status = Field<string>(component.Attributes, "_actionStatus");
            string afterCopy = RowActionChecks.ClipboardState();
            Check(status == expectedPositions.Length + " values copied",
                $"Sorted value copy completes ({sort}, filter '{filter}', status '{status}', host PID {Environment.ProcessId}; before {beforeCopy}; after {afterCopy})");
            string actual = Forms.Clipboard.GetText();
            string expected = string.Join(Environment.NewLine, expectedValues);
            Check(actual == expected,
                $"Native Copy values follows exactly the selected per-branch display order ({sort}, filter '{filter}', actual length {actual.Length}, expected {expected.Length}; after copy {afterCopy}; after read {RowActionChecks.ClipboardState()})");
        }
    }

    private static string[] BranchPaths(GH_Component component) =>
        Field<IEnumerable>(component.Attributes, "_branchHits").Cast<object>().Select(hit => Property<string>(hit, "Path")).ToArray();

    private static IReadOnlyList<int> Selected(GH_Component component, PanelQueryResult result) =>
        (IReadOnlyList<int>)Field<object>(component.Attributes, "_selection").GetType().GetMethod("OrderedPositions", Instance)!
            .Invoke(Field<object>(component.Attributes, "_selection"), new object[] { result })!;

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;

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
        Check(x > left + 40 && x < left + 240, "Native numbering gutter paints without clipping its digits");
        return x - left;
    }

    private static void Save(GH_Component component, Bitmap bitmap, string name) =>
        bitmap.Save(Path.Combine(Path.GetDirectoryName(component.GetType().Assembly.Location)!, "rhino-smoke-" + name + ".png"));

    private static void Click(GH_Component component, GH_Canvas canvas, RectangleF bounds, Forms.MouseButtons button = Forms.MouseButtons.Left)
    {
        var point = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        component.Attributes.RespondToMouseDown(canvas, new GH_CanvasMouseEvent(Point.Empty, point, button, 1, 0));
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

    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
}
