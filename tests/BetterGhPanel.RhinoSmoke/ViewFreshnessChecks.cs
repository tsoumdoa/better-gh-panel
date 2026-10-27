using System;
using System.Collections;
using System.Collections.Generic;
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
using Forms = System.Windows.Forms;

internal static class ViewFreshnessChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static async Task Run(Assembly assembly)
    {
        var previousEditor = Grasshopper.Instances.DocumentEditor;
        using var ownedEditor = previousEditor == null ? new Grasshopper.GUI.GH_DocumentEditor() : null;
        var editor = previousEditor ?? ownedEditor!;
        _ = editor.Handle;
        var setEditor = typeof(Grasshopper.Instances).GetMethod("SetDocumentEditor", BindingFlags.Static | BindingFlags.NonPublic)!;
        setEditor.Invoke(null, new object?[] { editor });
        using var document = new GH_Document { Enabled = true };
        GH_Document.EnableSolutions = true;
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        component.Attributes.Pivot = new PointF(50, 50);
        var source = new Param_GenericObject();
        var paired = new Param_GenericObject();
        source.CreateAttributes();
        paired.CreateAttributes();
        for (int branch = 0; branch < 5; branch++)
        {
            source.PersistentData.EnsurePath(new GH_Path(branch));
            paired.PersistentData.EnsurePath(new GH_Path(branch));
        }
        for (int item = 0; item < 5; item++) source.PersistentData.Append(new GH_Number(9), new GH_Path(1));
        source.PersistentData.Append(new GH_Integer(1), new GH_Path(1));
        source.PersistentData.Append(new GH_Number(2), new GH_Path(3));
        source.PersistentData.Append(new GH_Number(3), new GH_Path(3));
        for (int item = 0; item < 6; item++) paired.PersistentData.Append(new GH_String("first " + item), new GH_Path(1));
        for (int item = 0; item < 2; item++) paired.PersistentData.Append(new GH_String("second " + item), new GH_Path(3));
        document.AddObject(source, false);
        document.AddObject(paired, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        component.Params.Input[1].AddSource(paired);
        document.NewSolution(false, GH_SolutionMode.Silent);
        var session = PanelTestAdapter.Session(component);
        await Wait();
        using var canvas = new GH_Canvas { Size = new Size(800, 800), Document = document };
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        component.Attributes.PerformLayout();
        try
        {
            Paint();
            Check(BranchPaths().SequenceEqual(new[] { "{0}", "{1}", "{2}", "{3}", "{4}" }),
                "Full view paints retained empty paths before, between and after items");
            Check(component.Params.Output[0].VolatileData.PathCount == 5 &&
                component.Params.Output[0].VolatileData.AllData(true).Count() == 8 &&
                component.Params.Output[0].VolatileData.AllData(true).Any(value => value is GH_Integer) &&
                component.Params.Output[3].VolatileData.DataCount == 8,
                "The real solution retains empty branches, typed D and paired F");
            var first = PanelTestAdapter.RowBounds(component, 0);
            Click(first);
            Check(SelectedCount() == 1, "Native row click selects an item");
            await WaitFor(() => component.Params.Output[1].VolatileData.DataCount == 1);
            Check(component.Params.Output[1].VolatileData.AllData(true).Single() is GH_Number,
                "The real scheduled solution publishes the selected typed row");
            var outputs = Enumerable.Range(0, 4).Select(OutputFingerprint).ToArray();

            PanelTestAdapter.Resize(component, 300, 220);
            component.Attributes.PerformLayout();
            Click(PanelTestAdapter.Bounds(component, CanvasControl.Display));
            component.Attributes.PerformLayout();
            var list = PanelTestAdapter.Bounds(component, CanvasControl.List);
            Click(list);
            Check(SelectedCount() == 1, "Resize and Half layout reject row clicks before repaint");
            Paint();
            var seenPaths = new HashSet<string>();
            var seenRows = new HashSet<int>();
            int changes = 0;
            while (true)
            {
                foreach (string path in BranchPaths()) seenPaths.Add(path);
                foreach (var hit in Hits("_rowHits")) seenRows.Add(Property<int>(hit, "SourcePosition"));
                int oldOffset = PanelTestAdapter.Offset(component);
                var track = PanelTestAdapter.Bounds(component, CanvasControl.Track);
                Click(new RectangleF(track.X, track.Bottom - 1, track.Width, 1));
                if (PanelTestAdapter.Offset(component) == oldOffset) break;
                Check(++changes < 100, "Half scrolling advances in bounded steps");
                Click(list);
                Check(SelectedCount() == 1, "Scroll invalidates stale row and branch clicks before repaint");
                var point = new PointF(list.X + list.Width / 2, list.Y + list.Height / 2);
                var tooltip = (GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(GH_TooltipDisplayEventArgs),
                    BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { canvas, Point.Round(point) }, null)!;
                component.Attributes.SetupTooltip(point, tooltip);
                Check(!(tooltip.Title ?? "").StartsWith("Select branch", StringComparison.Ordinal) &&
                    !(tooltip.Description ?? "").Contains("Click to select without moving the camera"),
                    "Scroll rejects stale row and branch tooltips before repaint");
                Paint();
            }
            Check(new[] { "{0}", "{2}", "{4}" }.All(seenPaths.Contains) && seenRows.Count == 8,
                "Minimum Half scrolling reaches every empty path and every item");
            Check(outputs.SequenceEqual(Enumerable.Range(0, 4).Select(OutputFingerprint)),
                "Scrolling and layout preserve the already published D/S/B/F trees");

            Click(PanelTestAdapter.Bounds(component, CanvasControl.Jump));
            CanvasEditorAdapter.Active(assembly).Submit("2");
            Check(PanelTestAdapter.Offset(component) == Result().ViewIndex.OffsetOfItem(1),
                "Jump uses the second item rather than the second scroll coordinate");
            Click(list);
            Check(SelectedCount() == 1, "Jump rejects stale clicks until the new position is painted");
            Paint();
            var jumped = Hits("_rowHits").Single();
            Check(Property<int>(jumped, "ViewNumber") == 2 && Property<int>(jumped, "SourcePosition") == Result().RowPositions[1],
                "The painted jump target retains its item-based view number");
            Click(PanelTestAdapter.RowBounds(component, Result().RowPositions[1]));
            Check(PanelTestAdapter.SelectedPosition(component) == Result().RowPositions[1],
                "The new row becomes selectable after the jump is painted");

            var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = ">9" };
            session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { options });
            await Wait();
            Check(SelectedCount() == 0 && ReferenceEquals(Field<PanelQueryResult?>(component.Attributes, "_lastResult"), Result()),
                "Query completion prunes selection and synchronizes the result before painting");
            Click(list);
            Check(SelectedCount() == 0 && Hits("_rowHits").Count == 0, "Filtered results reject stale row clicks before repaint");
            await WaitFor(() => component.Params.Output[0].VolatileData.DataCount == 0 &&
                component.Params.Output[1].VolatileData.DataCount == 0 && component.Params.Output[3].VolatileData.DataCount == 0);
            Check(component.Params.Output[2].VolatileData.AllData(true).Cast<GH_Boolean>().All(value => !value.Value),
                "Filtered completion publishes D/S/B/F before another canvas paint");
            Paint();
            Check(Result().RowPositions.Count == 0 && Result().BranchPaths.Count == 0, "Item filters do not retain unrelated empty paths");

            session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { new PanelQueryOptions() });
            await Wait();
            session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[]
            { new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.ListLength } });
            PanelTestAdapter.TreeTools(component, 0, false, false, false, true);
            await Wait();
            await WaitFor(() => component.Params.Output[0].VolatileData.DataCount == 3);
            Paint();
            var zeroBranch = Hits("_branchHits").Single(hit => Property<string>(hit, "Path") == "{0}");
            Click(Property<RectangleF>(zeroBranch, "Bounds"));
            Check(SelectedCount() == 1, "Clicking the empty branch-set row selects its zero count");
            component.GetType().GetMethod("SetOutputScope", Instance)!.Invoke(component, new object[] { PanelOutputScope.Selected });
            await WaitFor(() => component.Params.Output[1].VolatileData.DataCount == 1 && component.Params.Output[3].VolatileData.PathCount == 1);
            Check(component.Params.Output[1].VolatileData.AllData(true).Single() is GH_Integer zero && zero.Value == 0 &&
                component.Params.Output[3].VolatileData.Paths[0].ToString() == "{0}" &&
                component.Params.Output[3].VolatileData.DataCount == 0 &&
                component.Params.Output[2].VolatileData.AllData(true).Cast<GH_Boolean>().All(value => !value.Value),
                "Empty branch-set selection emits zero on S, an empty paired path on F and no true B entries");
            component.GetType().GetMethod("SetOutputScope", Instance)!.Invoke(component, new object[] { PanelOutputScope.Filtered });
            PanelTestAdapter.TreeTools(component, 0, false, false, false, false);
            session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { new PanelQueryOptions() });
            await Wait();
            source.PersistentData.Clear();
            paired.PersistentData.Clear();
            for (int branch = 0; branch < 7; branch++) source.PersistentData.EnsurePath(new GH_Path(branch));
            for (int branch = 0; branch < 7; branch++) paired.PersistentData.EnsurePath(new GH_Path(branch));
            source.ExpireSolution(false);
            paired.ExpireSolution(false);
            document.NewSolution(false, GH_SolutionMode.Silent);
            await Wait();
            Paint();
            var allEmpty = new HashSet<string>();
            changes = 0;
            while (true)
            {
                foreach (string path in BranchPaths()) allEmpty.Add(path);
                int oldOffset = PanelTestAdapter.Offset(component);
                var track = PanelTestAdapter.Bounds(component, CanvasControl.Track);
                Click(new RectangleF(track.X, track.Bottom - 1, track.Width, 1));
                if (PanelTestAdapter.Offset(component) == oldOffset) break;
                Check(++changes < 100, "All-empty scrolling advances in bounded steps");
                Paint();
            }
            Check(allEmpty.SetEquals(Enumerable.Range(0, 7).Select(i => "{" + i + "}")),
                "An all-empty real tree exposes every path in minimum Half");
            Click(PanelTestAdapter.Bounds(component, CanvasControl.Display));
            component.Attributes.PerformLayout();
            Paint();
            var fullTrack = PanelTestAdapter.Bounds(component, CanvasControl.Track);
            Click(new RectangleF(fullTrack.X, fullTrack.Y, fullTrack.Width, 1));
            Paint();
            Check(BranchPaths().SequenceEqual(Enumerable.Range(0, 7).Select(i => "{" + i + "}")),
                "Full view paints headings for all empty branches");
        }
        finally
        {
            canvas.Document = null;
            component.DocumentContextChanged(document, GH_DocumentContext.Close);
            setEditor.Invoke(null, new object?[] { previousEditor });
        }

        PanelQueryResult Result() => Property<PanelQueryResult>(session, "Result");
        int SelectedCount() => Property<int>(Field<object>(component.Attributes, "_selection"), "Count");
        List<object> Hits(string name) => Field<IEnumerable>(component.Attributes, name).Cast<object>().ToList();
        string[] BranchPaths() => Hits("_branchHits").Select(hit => Property<string>(hit, "Path")).ToArray();
        string OutputFingerprint(int index)
        {
            var tree = component.Params.Output[index].VolatileData;
            return string.Join("|", tree.Paths.Select(path => path.ToString())) + ":" +
                string.Join("|", tree.AllData(true).Select(value => value == null ? "null" : value.GetType().FullName + ":" + value));
        }
        void Click(RectangleF bounds)
        {
            var point = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            var mouse = new GH_CanvasMouseEvent(Point.Empty, point, Forms.MouseButtons.Left, 1, 0);
            component.Attributes.RespondToMouseDown(canvas, mouse);
            component.Attributes.RespondToMouseUp(canvas, mouse);
        }
        void Paint()
        {
            using var bitmap = new Bitmap(800, 800);
            using var graphics = Graphics.FromImage(bitmap);
            canvas.Viewport.ApplyProjection(graphics);
            PanelTestAdapter.Render(component, canvas, graphics);
        }
        async Task Wait()
        {
            var timer = Stopwatch.StartNew();
            while (Property<bool>(session, "IsBusy"))
            {
                if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("View freshness query timed out");
                await Task.Delay(10);
            }
            if (Property<string?>(session, "Error") is string error) throw new InvalidOperationException(error);
        }
        async Task WaitFor(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition())
            {
                if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Scheduled view output timed out");
                await Task.Delay(10);
            }
        }
    }

    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static void Check(bool condition, string message)
    {
        SmokeAssertions.Check(condition, message);
    }
}
