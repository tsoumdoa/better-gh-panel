using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;

internal static class IndexSelectionChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    internal static async Task Run(Assembly assembly)
    {
        using var document = new GH_Document { Enabled = true };
        GH_Document.EnableSolutions = true;
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        component.Attributes.Pivot = new PointF(50, 50);
        var source = new Param_GenericObject();
        source.CreateAttributes();
        for (int branch = 0; branch < 3; branch++)
            for (int index = 0; index < (branch == 2 ? 2 : 30); index++)
                source.PersistentData.Append(new GH_Point(new Point3d((branch + 1) * 100 + index, 0, 0)), new GH_Path(branch));
        source.PersistentData.EnsurePath(new GH_Path(3));
        document.AddObject(source, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        document.NewSolution(false, GH_SolutionMode.Silent);
        var session = PanelTestAdapter.Session(component);
        await Wait(session);
        var output = component.Params.Output[0].VolatileData.AllData(true).ToArray();
        var upstream = source.PersistentData.AllData(true).ToArray();
        using var canvas = new GH_Canvas { Size = new Size(1000, 900) };
        canvas.Document = document;
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        component.Attributes.PerformLayout();
        var viewport = RhinoDoc.ActiveDoc.Views.ActiveView.ActiveViewport;
        var camera = viewport.CameraLocation;
        var target = viewport.CameraTarget;
        var selection = Field<object>(component.Attributes, "_selection");
        try
        {
            Paint();
            Open(1);
            Check(Label() == "Select index 1 across branches", "Row menu names the zero-based branch index");
            Save("index-selection-menu");
            Choose();
            Expect(1, 31, 61);
            Check(Field<string>(component.Attributes, "_actionStatus") == "Index 1 · 3 selected · 3 branches",
                "Cross-branch selection reports index, item count and branch count");
            Check((int)Field<object>(component, "_highlight").GetType().GetProperty("Count", Instance)!.GetValue(Field<object>(component, "_highlight"))! == 3,
                "Index selection previews matching geometry including offscreen branches");
            Check(viewport.CameraLocation == camera && viewport.CameraTarget == target, "Index selection keeps the Rhino camera still");
            Save("index-selection");

            // A menu feature can move without changing its tooltip or dispatched action.
            Open(2);
            PanelTestAdapter.SwapRowMenuActions(component, 0, 3);
            var reorderedMenu = Field<RectangleF>(component.Attributes, "_rowMenuBounds");
            var reorderedPoint = new PointF(reorderedMenu.X + 10, reorderedMenu.Y + 10);
            var reorderedTooltip = (GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(GH_TooltipDisplayEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { canvas, Point.Round(reorderedPoint) }, null)!;
            component.Attributes.SetupTooltip(reorderedPoint, reorderedTooltip);
            Check(reorderedTooltip.Title == "Select index 2 across branches" &&
                reorderedTooltip.Description.StartsWith("Select the same zero-based item indices", StringComparison.Ordinal),
                "Reordered row actions retain their labels and explanations");
            Click(new RectangleF(reorderedMenu.X, reorderedMenu.Y, reorderedMenu.Width, 20));
            Expect(2, 32);
            Check(viewport.CameraLocation == camera && viewport.CameraTarget == target,
                "A moved index-selection action dispatches by identity instead of framing the selection");
            Open(1);
            Choose();
            Expect(1, 31, 61);

            // Alt state is local to this test thread; it never injects a key into another app.
            var keyboard = new byte[256];
            Check(GetKeyboardState(keyboard), "Read thread keyboard state for shortcut check");
            var alt = (byte[])keyboard.Clone();
            alt[(int)Forms.Keys.Menu] = alt[(int)Forms.Keys.LMenu] = 0x80;
            try
            {
                Check(SetKeyboardState(alt), "Set thread-local Alt modifier");
                Click(PanelTestAdapter.RowBounds(component, 2));
            }
            finally { SetKeyboardState(keyboard); }
            Expect(2, 32); // branch two is too short
            Check(component.Params.Output[0].VolatileData.AllData(true).SequenceEqual(output),
                "Index selection alone leaves output identities and order unchanged");

            await Query(new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
            Click(PanelTestAdapter.Bounds(component, CanvasControl.Jump));
            CanvasEditorAdapter.Active(assembly).Submit("61");
            Paint();
            Open(61);
            Choose();
            Expect(1, 31, 61); // selected items retain branch order even in Desc
            await Query(new PanelQueryOptions());
            selection.GetType().GetMethod("Select", Instance)!.Invoke(selection, new object[] { Result(), 0, true, false, false });
            Open(1);
            Check(Label() == "Select indices 0, 1 across branches", "Selected rows expand each distinct index once");
            Choose();
            Expect(0, 1, 30, 31, 60, 61);

            await Query(new PanelQueryOptions { Search = "101" });
            Open(1);
            Check(Label() == "Select index 1 across filtered results", "Filtered selection scope is explicit in the menu");
            Choose();
            Expect(1);
            await Query(new PanelQueryOptions { BranchPath = "{0}" });
            Open(1);
            Choose();
            Expect(1);

            await Query(new PanelQueryOptions());
            PanelTestAdapter.Resize(component, 300, 220);
            component.Attributes.PerformLayout();
            Click(PanelTestAdapter.Bounds(component, CanvasControl.Display));
            Paint();
            Open(0);
            var menu = Field<RectangleF>(component.Attributes, "_rowMenuBounds");
            Check(PanelTestAdapter.Bounds(component, CanvasControl.Panel).Contains(menu), "Four row actions fit inside the minimum Half panel");
            Save("index-selection-half");
            Choose();
            Expect(0, 30, 60);
            // A trailing double-click from the menu cannot clear the new group.
            var menuPoint = new PointF(menu.X + 10, menu.Bottom - 10);
            component.Attributes.RespondToMouseDoubleClick(canvas, new GH_CanvasMouseEvent(Point.Empty, menuPoint, Forms.MouseButtons.Left, 2, 0));
            Expect(0, 30, 60);

            Open(0);
            menu = Field<RectangleF>(component.Attributes, "_rowMenuBounds");
            session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { new PanelQueryOptions { Search = "does not match" } });
            Click(new RectangleF(menu.X, menu.Bottom - 20, menu.Width, 20));
            await Wait(session);
            Paint();
            Expect();
            Check(Field<object?>(component.Attributes, "_rowMenuResult") == null, "A stale index action is discarded after the query changes");
            await Query(new PanelQueryOptions());
            Open(0);
            menu = Field<RectangleF>(component.Attributes, "_rowMenuBounds");
            component.Locked = true;
            Click(new RectangleF(menu.X, menu.Bottom - 20, menu.Width, 20));
            Check(Selected().Length <= 1, "Disabled components cannot expand a captured row selection");
            component.Locked = false;
            component.ExpireSolution(true);
            await Wait(session);
            Paint();
            Expect();
            Check(source.PersistentData.AllData(true).SequenceEqual(upstream) &&
                component.Params.Output[0].VolatileData.AllData(true).Cast<GH_Point>().Select(point => point.Value)
                    .SequenceEqual(output.Cast<GH_Point>().Select(point => point.Value)),
                "Index actions preserve upstream identities and output geometry values/order through recomputation");
            await SelectAllChecks();
        }
        finally
        {
            canvas.Document = null;
            component.DocumentContextChanged(document, GH_DocumentContext.Close);
        }

        PanelQueryResult Result() => (PanelQueryResult)session.GetType().GetProperty("Result")!.GetValue(session)!;
        int[] Selected() => ((IReadOnlyList<int>)selection.GetType().GetMethod("OrderedPositions", Instance)!.Invoke(selection, new object[] { Result() })!).ToArray();
        void Expect(params int[] expected) => Check(Selected().SequenceEqual(expected), "Cross-branch positions: [" + string.Join(",", expected) + "]");
        string Label() => PanelTestAdapter.RowMenuLabel(component, 3);
        void Open(int position) => Click(PanelTestAdapter.RowBounds(component, position), Forms.MouseButtons.Right);
        void Choose()
        {
            var menu = Field<RectangleF>(component.Attributes, "_rowMenuBounds");
            Click(new RectangleF(menu.X, menu.Bottom - 20, menu.Width, 20));
        }
        void Click(RectangleF bounds, Forms.MouseButtons button = Forms.MouseButtons.Left)
        {
            var point = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            PanelTestAdapter.CanvasClick(canvas, point, button);
        }
        async Task Query(PanelQueryOptions options)
        {
            session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { options });
            await Wait(session);
            Paint();
        }
        void Paint() { using var bitmap = Capture(); }
        Bitmap Capture()
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
        void Save(string name)
        {
            using var bitmap = Capture();
            bitmap.Save(Path.Combine(Path.GetDirectoryName(assembly.Location)!, "rhino-smoke-" + name + ".png"));
        }
        async Task SelectAllChecks()
        {
            // Still in the minimum Half layout, with only one visible row.
            var all = PanelTestAdapter.Bounds(component, CanvasControl.SelectAll);
            Check(PanelTestAdapter.Bounds(component, CanvasControl.Panel).Contains(all) &&
                !all.IntersectsWith(PanelTestAdapter.Bounds(component, CanvasControl.Statistics)) &&
                !all.IntersectsWith(PanelTestAdapter.Bounds(component, CanvasControl.Resize)),
                "Select all fits minimum Half mode without overlapping Stats or resize");
            var currentOutput = component.Params.Output[0].VolatileData;
            Click(all);
            Check(Selected().SequenceEqual(Enumerable.Range(0, 62)) &&
                (int)Field<object>(component, "_highlight").GetType().GetProperty("Count", Instance)!.GetValue(Field<object>(component, "_highlight"))! == 62,
                "One Select all click selects and previews every matching row, including offscreen branches");
            Check(ReferenceEquals(currentOutput, component.Params.Output[0].VolatileData) &&
                viewport.CameraLocation == camera && viewport.CameraTarget == target,
                "Select all keeps output and the Rhino camera unchanged");
            Save("select-all-half");
            Click(all);
            component.Attributes.RespondToMouseDoubleClick(canvas, new GH_CanvasMouseEvent(Point.Empty,
                new PointF(all.X + all.Width / 2, all.Y + all.Height / 2), Forms.MouseButtons.Left, 2, 0));
            Check(Selected().Length == 62, "Repeated and double-click Select all gestures retain the complete selection");

            await Query(new PanelQueryOptions { BranchPath = "{1}", SortMode = PanelSortMode.ValueDescending });
            Click(all);
            Check(Selected().SequenceEqual(Result().RowPositions) && Selected().Length == 30 &&
                Selected().All(position => position >= 30 && position < 60),
                "Select all respects branch filters and the current sorted copy order");
            await Query(new PanelQueryOptions { Search = "101", TypeName = "Point" });
            Click(all);
            Expect(1);
            await Query(new PanelQueryOptions { OnlyInvalid = true });
            Click(all);
            Expect();
            Check(Result().RowPositions.Count == 0, "Select all on empty invalid-only results is a no-op");

            await Query(new PanelQueryOptions());
            component.Locked = true;
            Click(all);
            Expect();
            component.Locked = false;
            component.ExpireSolution(true);
            await Wait(session);
            Paint();
            Click(all);
            Check(Selected().Length == 62, "Select all remains usable after a disabled-state guard");
            session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] {
                new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "[" } });
            Click(all);
            Check((int)Field<object>(component, "_highlight").GetType().GetProperty("Count", Instance)!.GetValue(Field<object>(component, "_highlight"))! == 0,
                "Select all cannot restore stale preview during a pending query");
            var timer = Stopwatch.StartNew();
            while ((bool)session.GetType().GetProperty("IsBusy")!.GetValue(session)!)
            {
                if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Invalid query did not finish");
                await Task.Delay(10);
            }
            Paint();
            Click(all);
            Expect();
            Check(session.GetType().GetProperty("Error")!.GetValue(session) is string, "Select all is inert for a failed query");
            await Query(new PanelQueryOptions());

            // A completed but unpainted result must not use the prior frame's controls.
            session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { new PanelQueryOptions { BranchPath = "{0}" } });
            await Wait(session);
            Click(all);
            Expect();
            Paint();
            Click(all);
            Check(Selected().Length == 30, "Select all acts on the current result after its first paint");
            await Query(new PanelQueryOptions());
            Click(PanelTestAdapter.Bounds(component, CanvasControl.Display));
            Paint();
            all = PanelTestAdapter.Bounds(component, CanvasControl.SelectAll);
            Click(all);
            Save("select-all");
            Check(Selected().Length == 62, "Select all works in Full mode");
            PanelTestAdapter.Minimize(component, true);
            component.Attributes.PerformLayout();
            Check(PanelTestAdapter.Bounds(component, CanvasControl.SelectAll).IsEmpty,
                "The minimized capsule has no hidden Select all target");
            PanelTestAdapter.Minimize(component, false);
            component.Attributes.PerformLayout();
            Paint();
            Check(Selected().Length == 62, "Capsule restoration retains Select all selection");
            source.ExpireSolution(false);
            document.NewSolution(false, GH_SolutionMode.Silent);
            await Wait(session);
            Paint();
            Expect();
        }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static async Task Wait(object session)
    {
        var timer = Stopwatch.StartNew();
        while ((bool)session.GetType().GetProperty("IsBusy")!.GetValue(session)!)
        {
            if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Index selection query did not finish");
            await Task.Delay(10);
        }
        if (session.GetType().GetProperty("Error")!.GetValue(session) is string error) throw new InvalidOperationException(error);
    }
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
}
