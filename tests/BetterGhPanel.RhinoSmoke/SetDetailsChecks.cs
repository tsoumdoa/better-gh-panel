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
        // Scheduled selection output needs the host editor's native handle even
        // though this fixture renders to an isolated, hidden canvas.
        var previous = Grasshopper.Instances.DocumentEditor;
        using var owned = previous == null ? new Grasshopper.GUI.GH_DocumentEditor() : null;
        var editor = previous ?? owned!;
        _ = editor.Handle;
        var setEditor = typeof(Grasshopper.Instances).GetMethod("SetDocumentEditor", BindingFlags.Static | BindingFlags.NonPublic)!;
        setEditor.Invoke(null, new object?[] { editor });
        try { await RunWithEditor(assembly); }
        finally { setEditor.Invoke(null, new object?[] { previous }); }
    }

    private static async Task RunWithEditor(Assembly assembly)
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
            Check(Result().RowPositions.Select(position => Result().SetOccurrencesAt(position)!.Count).SequenceEqual(new[] { 9, 1, 2, 1 }),
                "Set rows show original per-branch counts instead of counting only retained output rows");
            var output = component.Params.Output[0].VolatileData.AllData(true).ToArray();
            var inputGrip = component.Params.Input[0].Attributes.InputGrip;
            var outputGrip = component.Params.Output[0].Attributes.OutputGrip;
            var textRow = PanelTestAdapter.RowBounds(component, 2);
            PanelTestAdapter.CanvasClick(canvas, new PointF(textRow.X + 42, textRow.Y + 8));
            Check(PanelTestAdapter.SelectedPosition(component) == 2, "Set fixture selects a different row before inspecting occurrences");
            await WaitForSelectedOutput();
            Check(component.Params.Output[1].VolatileData.AllData(true).Single() is GH_String selected && selected.Value == "alpha",
                "The selected set value is published before occurrence inspection begins");
            Paint();
            var firstBadge = CountBounds(0);
            RawClick(firstBadge);
            Check(!Bounds("Bounds").IsEmpty && Property<int>(details, "SourcePosition") == 0,
                "Native count badge expands an occurrence table below the panel");
            DirectClick(firstBadge);
            Check(Property<bool>(details, "IsOpen"), "A badge cannot toggle twice before its new state is painted");
            Paint();
            var unchangedOutputs = Enumerable.Range(0, 4).Select(OutputFingerprint).ToArray();
            var partialNext = Bounds("Next");
            var partialTable = Bounds("Table");
            var occurrencePoint = new PointF(partialTable.X + 5, partialTable.Y + 8);
            component.Attributes.ExpireLayout();
            component.Attributes.PerformLayout();
            PaintClip(new RectangleF(firstBadge.X, firstBadge.Y, firstBadge.Width / 2, firstBadge.Height));
            DirectClick(firstBadge);
            DirectClick(partialNext);
            Check(Property<bool>(details, "IsOpen") && Property<int>(details, "Offset") == 0 &&
                Tooltip(firstBadge).Title != "Collapse occurrences" && TooltipAtPoint(occurrencePoint).Title != "Original input locations",
                "Partially painted badges, detail controls and occurrence tooltips remain inactive");
            Check(unchangedOutputs.SequenceEqual(Enumerable.Range(0, 4).Select(OutputFingerprint)),
                "Rejected clipped badge and drawer gestures preserve D/S/B/F");
            PaintClip(firstBadge);
            Check(Tooltip(firstBadge).Title == "Collapse occurrences", "A fully painted badge has its own live tooltip");
            DirectClick(firstBadge);
            Check(!Property<bool>(details, "IsOpen"), "A fully painted badge remains actionable when its row is clipped");
            Paint();
            DirectClick(CountBounds(0));
            Paint();
            var badge = CountBounds(0);
            PanelTestAdapter.CanvasClick(canvas, new PointF(badge.X + badge.Width / 2, badge.Y + badge.Height / 2), clicks: 2);
            Check(!Bounds("Bounds").IsEmpty && PanelTestAdapter.SelectedPosition(component) == 2,
                "A trailing double-click neither toggles the drawer twice nor clears row selection");
            Check(component.Attributes.Bounds.Contains(Bounds("Bounds")) && component.Params.Input[0].Attributes.InputGrip == inputGrip &&
                component.Params.Output[0].Attributes.OutputGrip == outputGrip, "Expanded table participates in hit testing without moving wire grips");
            Paint("set-occurrences");
            var nextPage = Bounds("Next");
            RawClick(nextPage);
            Check(Property<int>(details, "Offset") == 4, "Native dispatch reaches Next below the original panel bounds");
            DirectClick(nextPage);
            Check(Property<int>(details, "Offset") == 4, "A second page click before repaint cannot use stale occurrence targets");
            var staleTable = Bounds("Table");
            var stalePoint = new PointF(staleTable.X + 5, staleTable.Y + 8);
            PaintClip(new RectangleF(staleTable.X, staleTable.Y, staleTable.Width / 2, 17));
            DirectClick(nextPage);
            Check(Property<int>(details, "Offset") == 4, "A partial occurrence-page paint cannot publish its pager");
            var staleTooltip = (GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(GH_TooltipDisplayEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { canvas, Point.Round(stalePoint) }, null)!;
            component.Attributes.SetupTooltip(stalePoint, staleTooltip);
            Check(staleTooltip.Title != "Original input locations", "Occurrence tooltips wait for the new page to be painted");
            Paint();
            Click(Bounds("Next"));
            Check(Property<int>(details, "Offset") == 8 && Property<int>(details, "Visible") == 1, "Occurrence paging reaches the last original location");
            var table = Bounds("Table");
            var point = new PointF(table.X + 5, table.Y + 8);
            var tooltip = (GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(GH_TooltipDisplayEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { canvas, Point.Round(point) }, null)!;
            component.Attributes.SetupTooltip(point, tooltip);
            Check(tooltip.Text == "{4;0} [9]", "Occurrence tooltip exposes the complete original path and zero-based item index");
            Check(PanelTestAdapter.SelectedPosition(component) == 2 && ReferenceEquals(Result().Snapshot, snapshot) &&
                component.Params.Output[0].VolatileData.AllData(true).SequenceEqual(output) &&
                viewport.CameraLocation == camera && viewport.CameraTarget == target && source.PersistentData.DataCount == 13,
                "Inspecting counts and paging preserves row selection, solution, typed output, camera and source data");
            Click(Bounds("Collapse"));
            Check(Bounds("Bounds").IsEmpty, "Drawer header collapses the mini table");
            Click(CountBounds(0));
            Click(CountBounds(0));
            Check(!Property<bool>(details, "IsOpen"), "Clicking the same count badge again collapses the mini table");

            Click(CountBounds(2));
            Check(Bounds("Previous").IsEmpty && Bounds("Next").IsEmpty && Bounds("Bounds").Bottom - Bounds("Table").Bottom == 4,
                "Singleton occurrences show their original address without an empty paging footer");
            Click(Bounds("Collapse"));
            Click(CountBounds(0));
            await Query(new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
            Check(Property<int>(details, "SourcePosition") == 0 && !Bounds("Bounds").IsEmpty,
                "Sorting retains the expanded value and its original locations");
            await Query(new PanelQueryOptions { Search = ">8", SortMode = PanelSortMode.ValueDescending });
            Check(Result().SetOccurrencesAt(0)!.Count == 9 && !Bounds("Bounds").IsEmpty,
                "Filtering retained values does not reduce their original occurrence count");
            await Query(new PanelQueryOptions { Search = "alpha" });
            Check(!Property<bool>(details, "IsOpen"), "Filtering out the expanded value dismisses its locations");
            await Query(new PanelQueryOptions());

            PanelTestAdapter.Units(component, PanelUnitConversion.MillimetersToMeters, 0);
            await Wait();
            Paint();
            Check(Result().Snapshot.Rows[0].NumericValue == 0.009 && Result().SetOccurrencesAt(0)!.Count == 9 &&
                Result().SetOccurrencesAt(0)!.Locations[0].ItemIndex == 0,
                "Unit conversion and rounded display retain original counts and addresses");
            PanelTestAdapter.Units(component, PanelUnitConversion.None, -1);
            PanelTestAdapter.TreeTools(component, 0, true, true, false, true);
            await Wait();
            PanelTestAdapter.Resize(component, 300, 220);
            component.Attributes.PerformLayout();
            Click(PanelTestAdapter.Bounds(component, CanvasControl.Display));
            Paint();
            Click(CountBounds(0));
            var global = Result().SetOccurrencesAt(0)!;
            Check(global.Count == 11 && global.BranchCount == 2 && global.Locations[0].Path == "{4;1}" && global.Locations[0].ItemIndex == 2,
                "Flatten plus Reverse shows all eleven occurrences and their original branch addresses");
            Check(!Bounds("Bounds").IsEmpty && Bounds("Bounds").Top > PanelTestAdapter.Bounds(component, CanvasControl.Panel).Bottom,
                "Occurrence table remains readable below minimum Half layout");
            var oldBadge = CountBounds(0);
            var oldCollapse = Bounds("Collapse");
            var track = PanelTestAdapter.Bounds(component, CanvasControl.Track);
            int oldOffset = PanelTestAdapter.Offset(component);
            DirectClick(new RectangleF(track.X, track.Bottom - 1, track.Width, 1));
            Check(PanelTestAdapter.Offset(component) > oldOffset, "A Half scroll changes the visible set rows");
            DirectClick(oldBadge);
            DirectClick(oldCollapse);
            Check(Property<bool>(details, "IsOpen"), "Scrolling rejects old badge and occurrence targets until repaint");
            Paint();
            DirectClick(PanelTestAdapter.Bounds(component, CanvasControl.Jump));
            CanvasEditorAdapter.Active(assembly).Submit("1");
            DirectClick(Bounds("Collapse"));
            Check(Property<bool>(details, "IsOpen"), "Jumping rejects occurrence targets until the new viewport is painted");
            Paint();
            oldCollapse = Bounds("Collapse");
            PanelTestAdapter.Resize(component, 310, 220);
            component.Attributes.PerformLayout();
            DirectClick(oldCollapse);
            Check(Property<bool>(details, "IsOpen"), "Resizing rejects the previous occurrence target before repaint");
            Paint();
            PanelTestAdapter.Resize(component, 300, 220);
            component.Attributes.PerformLayout();
            Paint("set-occurrences-half");
            canvas.Viewport.Zoom = 0.4f;
            canvas.Viewport.ComputeProjection();
            var staleBadgePoint = new PointF(CountBounds(0).X + 5, CountBounds(0).Y + 8);
            var hiddenTooltip = (GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(GH_TooltipDisplayEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { canvas, Point.Round(staleBadgePoint) }, null)!;
            component.Attributes.SetupTooltip(staleBadgePoint, hiddenTooltip);
            Check(hiddenTooltip.Title != "Collapse occurrences", "A zoom change invalidates badge tooltips before repaint");
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
            Check(!Property<bool>(details, "IsOpen") && Result().SetOccurrencesAt(0)!.Count == 2 &&
                Result().SetOccurrencesAt(0)!.Locations[0].Path == "{8}", "New input clears expanded state and replaces all old provenance");
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
            RawClick(bounds);
            Paint();
        }
        void RawClick(RectangleF bounds) =>
            PanelTestAdapter.CanvasClick(canvas, new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        void DirectClick(RectangleF bounds)
        {
            var point = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            var mouse = new GH_CanvasMouseEvent(Point.Empty, point, System.Windows.Forms.MouseButtons.Left, 1, 0);
            component.Attributes.RespondToMouseDown(canvas, mouse);
            component.Attributes.RespondToMouseUp(canvas, mouse);
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
        void PaintClip(RectangleF clip)
        {
            using var bitmap = new Bitmap(750, 800);
            using var graphics = Graphics.FromImage(bitmap);
            canvas.Viewport.ApplyProjection(graphics);
            graphics.SetClip(clip);
            PanelTestAdapter.Render(component, canvas, graphics);
        }
        GH_TooltipDisplayEventArgs Tooltip(RectangleF bounds) => TooltipAtPoint(new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        GH_TooltipDisplayEventArgs TooltipAtPoint(PointF point)
        {
            var value = (GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(GH_TooltipDisplayEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { canvas, Point.Round(point) }, null)!;
            component.Attributes.SetupTooltip(point, value);
            return value;
        }
        string OutputFingerprint(int index)
        {
            var tree = component.Params.Output[index].VolatileData;
            return string.Join("|", tree.Paths.Select(path => path.ToString())) + ":" +
                string.Join("|", tree.AllData(true).Select(value => value == null ? "null" : value.GetType().FullName + ":" + value));
        }
        async Task WaitForSelectedOutput()
        {
            var clock = Stopwatch.StartNew();
            while (component.Params.Output[1].VolatileData.DataCount != 1)
            {
                if (clock.Elapsed.TotalSeconds > 20)
                    throw new TimeoutException($"Selected set output did not finish: position={PanelTestAdapter.SelectedPosition(component)}, " +
                        $"counts={string.Join(",", component.Params.Output.Select(output => output.VolatileData.DataCount))}, " +
                        $"selectionVersion={Field<long>(component, "_selectionVersion")}, publishedVersion={Field<long>(component, "_publishedSelectionVersion")}, " +
                        $"current={Property<bool>(session, "HasCurrentResult")}, busy={Property<bool>(session, "IsBusy")}, canvasDocument={ReferenceEquals(canvas.Document, document)}");
                await Task.Delay(10);
            }
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
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
}
