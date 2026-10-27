using System;
using System.Diagnostics;
using System.Drawing;
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
using EtoForms = Eto.Forms;
using Forms = System.Windows.Forms;

internal static class FullValueChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Title = "Panel+ · Full value";

    internal static void VerifyPaintClipping(Assembly assembly)
    {
        var method = assembly.GetType("BetterGhPanel.BetterPanelAttributes", true)!
            .GetMethod("FullyVisible", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var bitmap = new Bitmap(600, 400);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TranslateTransform(40, 30);
        var bounds = new RectangleF(20, 20, 200, 20);
        bool Visible() => (bool)method.Invoke(null, new object[] { graphics, bounds })!;
        Check(Visible(), "A fully painted row is eligible for interaction");
        graphics.SetClip(new RectangleF(20, 20, 100, 20));
        Check(!Visible(), "A partially clipped menu row is not eligible for interaction");
        graphics.SetClip(new RectangleF(19, 19, 202, 22));
        Check(Visible(), "A repainted row becomes interactive under a translated graphics transform");
    }

    internal static async Task Run(Assembly assembly)
    {
        const string tail = "শেষ 🧪 漢字 ∑𐐷";
        int lines = Math.Max(350, (int)Math.Ceiling(EtoForms.Screen.FromPoint(EtoForms.Mouse.Position).WorkingArea.Height / 8) + 50);
        string value = string.Join("\r\n", Enumerable.Range(0, lines).Select(i => $"Line {i:D3} — 中文 🧪")) + "\n" + tail;
        using var document = new GH_Document { Enabled = true };
        GH_Document.EnableSolutions = true;
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        component.Attributes.Pivot = new PointF(50, 50);
        var source = new Param_GenericObject();
        source.CreateAttributes();
        source.PersistentData.Append(new GH_String(value), new GH_Path(0));
        source.PersistentData.Append(new GH_String("another selected value"), new GH_Path(0));
        source.PersistentData.Append(new GH_String(""), new GH_Path(0));
        document.AddObject(source, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        document.NewSolution(false, GH_SolutionMode.Silent);
        var session = PanelTestAdapter.Session(component);
        await WaitForQuery(session);
        var result = (PanelQueryResult)session.GetType().GetProperty("Result")!.GetValue(session)!;
        string copiedValue = PanelTestAdapter.CopyValue(component, result.Snapshot, 0);
        var selection = Field<object>(component.Attributes, "_selection");
        var select = selection.GetType().GetMethod("Select", Instance)!;
        select.Invoke(selection, new object[] { result, 0, false, false, false });
        select.Invoke(selection, new object[] { result, 1, true, false, false });
        int[] selected = Selected();
        Check(selected.SequenceEqual(new[] { 0, 1 }), "Viewer fixture starts with a multi-row selection");
        using var canvas = new GH_Canvas { Size = new Size(1000, 900), Document = document };
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        component.Attributes.PerformLayout();
        try
        {
            Paint();
            // Selection schedules a separate Grasshopper solution. Let the
            // attached canvas process it before taking the viewer's baseline.
            var outputTimeout = Stopwatch.StartNew();
            while (component.Params.Output[1].VolatileData.AllData(true).Count() != 2)
            {
                if (outputTimeout.Elapsed.TotalSeconds > 20) throw new TimeoutException("Selected output did not settle before opening the full-value viewer");
                await Task.Delay(10);
            }
            var output = component.Params.Output.Select(param => param.VolatileData.AllData(true).ToArray()).ToArray();
            Open();
            var first = MenuItem(3); // the full-panel viewer has not been painted yet
            Check(Tooltip(first).Title != "View full value", "Unpainted row actions do not supply a tooltip");
            Click(first);
            Check(Windows().Length == 0 && Selected().SequenceEqual(selected), "An unpainted viewer action cannot open a window or alter selection");

            Open();
            Paint();
            int viewIndex = Find("View full value");
            Check(Tooltip(MenuItem(viewIndex)).Title == "View full value", "The painted context action has a clear tooltip");
            Click(MenuItem(viewIndex));
            var window = Windows().Single();
            var text = (EtoForms.TextArea)window.Content;
            await Task.Delay(150); // allow the WPF control to lay out in the real host
            Check(text.ReadOnly && text.Text == value && text.Wrap, "The window preserves the whole multiline Unicode value in a read-only text area");
            var work = window.Screen.WorkingArea;
            var bounds = window.Bounds;
            Check(bounds.Left >= work.Left && bounds.Top >= work.Top && bounds.Right <= work.Right && bounds.Bottom <= work.Bottom,
                "The initial viewer window fits the current screen's working area");
            var native = FindTextBox((System.Windows.DependencyObject)text.ControlObject);
            Check(native != null && native.IsReadOnly && native.ExtentHeight > native.ViewportHeight && native.ExtentHeight > work.Height,
                "The native text view is read-only and its content is taller than the screen");
            text.ScrollToEnd();
            await Task.Delay(150);
            native!.UpdateLayout();
            var finalIndex = native.Text.LastIndexOf("𐐷", StringComparison.Ordinal);
            Check(finalIndex >= 0, "The final supplementary Unicode character remains in the native text");
            var finalRect = native.GetRectFromCharacterIndex(finalIndex);
            native.Select(finalIndex, "𐐷".Length);
            Check(native.VerticalOffset > 0 && !finalRect.IsEmpty && finalRect.Top < native.ActualHeight &&
                finalRect.Bottom > 0 && native.SelectedText == "𐐷", "Scrolling reaches and displays the final supplementary Unicode character");
            window.Close();
            bool sameSelection = Selected().SequenceEqual(selected);
            bool sameCopy = PanelTestAdapter.CopyValue(component, result.Snapshot, 0) == copiedValue && copiedValue == value;
            var sameOutputs = component.Params.Output.Select((param, i) => param.VolatileData.AllData(true).SequenceEqual(output[i])).ToArray();
            Check(sameSelection && sameCopy && sameOutputs.All(ok => ok),
                "Viewing the clicked row preserves multi-selection, copy formatting and all outputs " +
                $"(selection {sameSelection}, copy {sameCopy}, outputs {string.Join(",", sameOutputs)})");

            Paint(); // the scheduled selection solution can invalidate hit regions while the window is open
            Open(2);
            Paint();
            Click(MenuItem(Find("View full value")));
            var emptyWindow = Windows().Single();
            Check(((EtoForms.TextArea)emptyWindow.Content).Text == "" && PanelTestAdapter.CopyValue(component, result.Snapshot, 2) == "",
                "An empty text value opens as empty and keeps its semantic copy value");
            emptyWindow.Close();
            select.Invoke(selection, new object[] { result, 0, false, false, false });
            select.Invoke(selection, new object[] { result, 1, true, false, false });

            PanelTestAdapter.Resize(component, 300, 220);
            component.Attributes.PerformLayout();
            Click(PanelTestAdapter.Bounds(component, CanvasControl.Display));
            Paint();
            Open();
            Paint();
            Check(Find("View full value") >= 0 && Find("More actions…") >= 0 &&
                PanelTestAdapter.Bounds(component, CanvasControl.Panel).Contains(Field<RectangleF>(component.Attributes, "_rowMenuBounds")),
                "The first compact page contains the viewer and fits the minimum Half panel");
            Click(MenuItem(Find("More actions…")));
            var pending = MenuItem(2); // Previous on the new page; viewer was here on the old page
            Check(Tooltip(pending).Title != "View full value" && Tooltip(pending).Title != "Previous actions…",
                "Neither old nor new page tooltips are exposed before painting");
            Click(pending);
            Check(Windows().Length == 0 && Field<object?>(component.Attributes, "_rowMenuResult") == null && Selected().SequenceEqual(selected),
                "Clicking an unpainted page invokes neither the old viewer nor the new action");
            Paint();
            Open();
            Paint();
            Click(MenuItem(Find("More actions…")));
            PaintClip(MenuItem(0));
            Check(PanelTestAdapter.RowMenuLabel(component, 0) == "Copy geometry for Rhino" &&
                Tooltip(MenuItem(1)).Title != "Select indices 0, 1 across branches",
                "A partial repaint enables only the page row actually painted");
            Paint();
            Check(Find("Copy geometry for Rhino") >= 0 && Find("Select indices 0, 1 across branches") >= 0 && Find("Previous actions…") >= 0 &&
                PanelTestAdapter.Bounds(component, CanvasControl.Panel).Contains(Field<RectangleF>(component.Attributes, "_rowMenuBounds")),
                "The painted second compact page keeps both remaining actions reachable");
            Click(MenuItem(Find("Previous actions…")));
            Check(Tooltip(MenuItem(2)).Title != "View full value", "Returning to the first page also waits for paint");
            Paint();
            Check(Find("View full value") >= 0 && Selected().SequenceEqual(selected), "The first page becomes usable after repaint without changing selection");
        }
        finally
        {
            foreach (var window in Windows()) window.Close();
            canvas.Document = null;
            component.DocumentContextChanged(document, GH_DocumentContext.Close);
        }

        EtoForms.Form[] Windows() => EtoForms.Application.Instance.Windows.OfType<EtoForms.Form>().Where(w => w.Title == Title).ToArray();
        int[] Selected() => ((System.Collections.Generic.IReadOnlyList<int>)selection.GetType().GetMethod("OrderedPositions", Instance)!
            .Invoke(selection, new object[] { result })!).ToArray();
        void Open(int position = 0) => Click(PanelTestAdapter.RowBounds(component, position), Forms.MouseButtons.Right);
        void Click(RectangleF bounds, Forms.MouseButtons button = Forms.MouseButtons.Left)
        {
            var point = new PointF(bounds.X + Math.Min(45, bounds.Width / 2), bounds.Y + bounds.Height / 2);
            component.Attributes.RespondToMouseDown(canvas, new GH_CanvasMouseEvent(Point.Empty, point, button, 1, 0));
        }
        RectangleF MenuItem(int index)
        {
            var bounds = Field<RectangleF>(component.Attributes, "_rowMenuBounds");
            return new RectangleF(bounds.X, bounds.Y + index * 20, bounds.Width, 20);
        }
        int Find(string label)
        {
            var actions = Field<Array>(component.Attributes, "_rowMenuActions");
            for (int i = 0; i < actions.Length; i++) if (PanelTestAdapter.RowMenuLabel(component, i) == label) return i;
            throw new InvalidOperationException("Menu action not painted: " + label);
        }
        GH_TooltipDisplayEventArgs Tooltip(RectangleF bounds)
        {
            var point = new PointF(bounds.X + 10, bounds.Y + bounds.Height / 2);
            var args = (GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(GH_TooltipDisplayEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { canvas, Point.Round(point) }, null)!;
            component.Attributes.SetupTooltip(point, args);
            return args;
        }
        void Paint()
        {
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            using var graphics = Graphics.FromImage(bitmap);
            canvas.Viewport.ApplyProjection(graphics);
            PanelTestAdapter.Render(component, canvas, graphics);
        }
        void PaintClip(RectangleF clip)
        {
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            using var graphics = Graphics.FromImage(bitmap);
            canvas.Viewport.ApplyProjection(graphics);
            graphics.SetClip(clip);
            PanelTestAdapter.Render(component, canvas, graphics);
        }
    }

    private static System.Windows.Controls.TextBox? FindTextBox(System.Windows.DependencyObject root)
    {
        if (root is System.Windows.Controls.TextBox text) return text;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindTextBox(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static async Task WaitForQuery(object session)
    {
        var timeout = Stopwatch.StartNew();
        while ((bool)session.GetType().GetProperty("IsBusy")!.GetValue(session)!)
        {
            if (timeout.Elapsed.TotalSeconds > 20) throw new TimeoutException("Full-value fixture query did not finish");
            await Task.Delay(10);
        }
        if (session.GetType().GetProperty("Error")!.GetValue(session) is string error) throw new InvalidOperationException(error);
    }
    private static void Check(bool condition, string message)
    {
        SmokeAssertions.Check(condition, message);
    }
}
