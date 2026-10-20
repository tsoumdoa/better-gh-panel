using System;
using System.Collections;
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
using Rhino;

internal static class SetDetailsChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static async Task Run(Assembly assembly)
    {
        using var document = new GH_Document { Enabled = true };
        GH_Document.EnableSolutions = true;
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        component.Attributes.Pivot = new PointF(50, 50);
        var source = new Param_GenericObject();
        source.CreateAttributes();
        for (int i = 0; i < 10; i++) source.PersistentData.Append(i == 2 ? new GH_String("alpha") : new GH_Number(9), new GH_Path(4, 0));
        source.PersistentData.AppendRange(new IGH_Goo[] { new GH_Integer(9), new GH_Number(3), new GH_Number(9) }, new GH_Path(4, 1));
        document.AddObject(source, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        document.NewSolution(false, GH_SolutionMode.Silent);
        var session = PanelTestAdapter.Session(component);
        PanelTestAdapter.TreeTools(component, 0, false, false, false, true);
        await Wait();
        using var canvas = new GH_Canvas { Size = new Size(750, 800), Document = document };
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        component.Attributes.PerformLayout();
        var details = Field<object>(component.Attributes, "_setDetails");
        var viewport = RhinoDoc.ActiveDoc.Views.ActiveView.ActiveViewport;
        var camera = viewport.CameraLocation;
        var target = viewport.CameraTarget;
        try
        {
            Paint();
            var snapshot = Result().Snapshot;
            Check(snapshot.Rows.Select(row => row.SetOccurrences!.Count).SequenceEqual(new[] { 9, 1, 2, 1 }),
                "Set rows show original per-branch counts instead of counting only retained output rows");
            var output = component.Params.Output[0].VolatileData.AllData(true).ToArray();
            var inputGrip = component.Params.Input[0].Attributes.InputGrip;
            var outputGrip = component.Params.Output[0].Attributes.OutputGrip;
            var textRow = PanelTestAdapter.RowBounds(component, 1);
            PanelTestAdapter.CanvasClick(canvas, new PointF(textRow.X + 42, textRow.Y + 8));
            Check(PanelTestAdapter.SelectedPosition(component) == 1, "Set fixture selects a different row before inspecting occurrences");
            Click(CountBounds(0));
            Check(!Bounds("Bounds").IsEmpty && Property<int>(details, "SourcePosition") == 0,
                "Native count badge expands an occurrence table below the panel");
            var badge = CountBounds(0);
            PanelTestAdapter.CanvasClick(canvas, new PointF(badge.X + badge.Width / 2, badge.Y + badge.Height / 2), clicks: 2);
            Check(!Bounds("Bounds").IsEmpty && PanelTestAdapter.SelectedPosition(component) == 1,
                "A trailing double-click neither toggles the drawer twice nor clears row selection");
            Check(component.Attributes.Bounds.Contains(Bounds("Bounds")) && component.Params.Input[0].Attributes.InputGrip == inputGrip &&
                component.Params.Output[0].Attributes.OutputGrip == outputGrip, "Expanded table participates in hit testing without moving wire grips");
            Paint("set-occurrences");
            Click(Bounds("Next"));
            Check(Property<int>(details, "Offset") == 4, "Native dispatch reaches Next below the original panel bounds");
            Click(Bounds("Next"));
            Check(Property<int>(details, "Offset") == 8 && Property<int>(details, "Visible") == 1, "Occurrence paging reaches the last original location");
            var table = Bounds("Table");
            var point = new PointF(table.X + 5, table.Y + 8);
            var tooltip = (GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(GH_TooltipDisplayEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { canvas, Point.Round(point) }, null)!;
            component.Attributes.SetupTooltip(point, tooltip);
            Check(tooltip.Text == "{4;0} [9]", "Occurrence tooltip exposes the complete original path and zero-based item index");
            Check(PanelTestAdapter.SelectedPosition(component) == 1 && ReferenceEquals(Result().Snapshot, snapshot) &&
                component.Params.Output[0].VolatileData.AllData(true).SequenceEqual(output) &&
                viewport.CameraLocation == camera && viewport.CameraTarget == target && source.PersistentData.DataCount == 13,
                "Inspecting counts and paging preserves row selection, solution, typed output, camera and source data");
            Click(Bounds("Collapse"));
            Check(Bounds("Bounds").IsEmpty, "Drawer header collapses the mini table");
            Click(CountBounds(0));
            Click(CountBounds(0));
            Check(!Property<bool>(details, "IsOpen"), "Clicking the same count badge again collapses the mini table");

            Click(CountBounds(1));
            Check(Bounds("Previous").IsEmpty && Bounds("Next").IsEmpty && Bounds("Bounds").Bottom - Bounds("Table").Bottom == 4,
                "Singleton occurrences show their original address without an empty paging footer");
            Click(Bounds("Collapse"));
            Click(CountBounds(0));
            await Query(new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
            Check(Property<int>(details, "SourcePosition") == 0 && !Bounds("Bounds").IsEmpty,
                "Sorting retains the expanded value and its original locations");
            await Query(new PanelQueryOptions { Search = ">8", SortMode = PanelSortMode.ValueDescending });
            Check(Result().Snapshot.Rows[0].SetOccurrences!.Count == 9 && !Bounds("Bounds").IsEmpty,
                "Filtering retained values does not reduce their original occurrence count");
            await Query(new PanelQueryOptions { Search = "alpha" });
            Check(!Property<bool>(details, "IsOpen"), "Filtering out the expanded value dismisses its locations");
            await Query(new PanelQueryOptions());

            PanelTestAdapter.Units(component, PanelUnitConversion.MillimetersToMeters, 0);
            await Wait();
            Paint();
            Check(Result().Snapshot.Rows[0].NumericValue == 0.009 && Result().Snapshot.Rows[0].SetOccurrences!.Count == 9 &&
                Result().Snapshot.Rows[0].SetOccurrences!.Locations[0].ItemIndex == 0,
                "Unit conversion and rounded display retain original counts and addresses");
            PanelTestAdapter.Units(component, PanelUnitConversion.None, -1);
            PanelTestAdapter.TreeTools(component, 0, true, true, false, true);
            await Wait();
            PanelTestAdapter.Resize(component, 300, 220);
            component.Attributes.PerformLayout();
            Click(PanelTestAdapter.Bounds(component, CanvasControl.Display));
            Paint();
            Click(CountBounds(0));
            var global = Result().Snapshot.Rows[0].SetOccurrences!;
            Check(global.Count == 11 && global.BranchCount == 2 && global.Locations[0].Path == "{4;1}" && global.Locations[0].ItemIndex == 2,
                "Flatten plus Reverse shows all eleven occurrences and their original branch addresses");
            Check(!Bounds("Bounds").IsEmpty && Bounds("Bounds").Top > PanelTestAdapter.Bounds(component, CanvasControl.Panel).Bottom,
                "Occurrence table remains readable below minimum Half layout");
            Paint("set-occurrences-half");
            canvas.Viewport.Zoom = 0.4f;
            canvas.Viewport.ComputeProjection();
            Paint();
            Check(((ICollection)Field<object>(component.Attributes, "_setHits")).Count == 0,
                "Low zoom clears hidden set count hit targets");
            Check(Bounds("Bounds").IsEmpty && component.Attributes.Bounds.Bottom == PanelTestAdapter.Bounds(component, CanvasControl.Panel).Bottom,
                "Low zoom removes the invisible drawer from component hit testing");
            canvas.Viewport.Zoom = 1;
            canvas.Viewport.ComputeProjection();
            Paint();
            Check(!Bounds("Bounds").IsEmpty, "Zooming back in restores the inspected occurrence table");
            Click(PanelTestAdapter.Bounds(component, CanvasControl.Close));
            Check(Bounds("Bounds").IsEmpty && Property<bool>(details, "IsOpen"), "Minimizing hides and retains the inspected value");
            Click(PanelTestAdapter.Bounds(component, CanvasControl.Restore));
            Paint();
            Check(!Bounds("Bounds").IsEmpty, "Restoring the capsule also restores the occurrence table");

            component.Locked = true;
            Paint();
            Check(Bounds("Bounds").IsEmpty, "Disabled components never show stale occurrence locations");
            component.Locked = false;
            component.ExpireSolution(true);
            await Wait();
            Paint();
            Click(CountBounds(0));
            session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "[" } });
            Paint();
            Check(Bounds("Bounds").IsEmpty, "Pending queries hide the previous occurrence table immediately");
            await Wait(true);
            Paint();
            Check(Bounds("Bounds").IsEmpty, "Failed queries keep old occurrence details hidden");
            await Query(new PanelQueryOptions());

            source.PersistentData.Clear();
            source.PersistentData.AppendRange(new IGH_Goo[] { new GH_Number(7), new GH_Number(7) }, new GH_Path(8));
            source.ExpireSolution(false);
            document.NewSolution(false, GH_SolutionMode.Silent);
            await Wait();
            Paint();
            Check(!Property<bool>(details, "IsOpen") && Result().Snapshot.Rows[0].SetOccurrences!.Count == 2 &&
                Result().Snapshot.Rows[0].SetOccurrences!.Locations[0].Path == "{8}", "New input clears expanded state and replaces all old provenance");
            Click(CountBounds(0));
            Click(PanelTestAdapter.Bounds(component, CanvasControl.Reset));
            await Wait();
            Paint();
            Check(!Property<bool>(details, "IsOpen") && Result().Snapshot.Rows.All(row => row.SetOccurrences == null) &&
                ((ICollection)Field<object>(component.Attributes, "_setHits")).Count == 0,
                "Reset restores ordinary rows and removes all set count controls");
        }
        finally { canvas.Document = null; component.DocumentContextChanged(document, GH_DocumentContext.Close); }

        PanelQueryResult Result() => Property<PanelQueryResult>(session, "Result");
        RectangleF Bounds(string name) => Property<RectangleF>(details, name);
        RectangleF CountBounds(int position)
        {
            foreach (var hit in Field<IEnumerable>(component.Attributes, "_setHits"))
                if (Property<int>(hit, "SourcePosition") == position) return Property<RectangleF>(hit, "Bounds");
            throw new InvalidOperationException("Set count is not visible: " + position);
        }
        void Click(RectangleF bounds)
        {
            PanelTestAdapter.CanvasClick(canvas, new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
            Paint();
        }
        void Paint(string? name = null)
        {
            using var bitmap = new Bitmap(750, 800);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.White);
            canvas.Viewport.ApplyProjection(graphics);
            PanelTestAdapter.Render(component, canvas, graphics);
            if (name != null) bitmap.Save(Path.Combine(Path.GetDirectoryName(assembly.Location)!, "rhino-smoke-" + name + ".png"));
        }
        async Task Query(PanelQueryOptions options)
        {
            session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { options });
            await Wait();
            Paint();
        }
        async Task Wait(bool allowError = false)
        {
            var clock = Stopwatch.StartNew();
            while (Property<bool>(session, "IsBusy"))
            {
                if (clock.Elapsed.TotalSeconds > 20) throw new TimeoutException("Set occurrence query did not finish");
                await Task.Delay(10);
            }
            if (!allowError && Property<string?>(session, "Error") is string error) throw new InvalidOperationException(error);
        }
    }

    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
