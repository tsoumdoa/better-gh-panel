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
using GH_IO.Serialization;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using Forms = System.Windows.Forms;

internal static class ItemOrderUiChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    internal static async Task Run(Assembly assembly)
    {
        using var document = new GH_Document { Enabled = true };
        GH_Document.EnableSolutions = true;
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        var source = new Param_GenericObject();
        source.CreateAttributes();
        source.Hidden = true;
        source.PersistentData.Append(new GH_Point(new Point3d(-8, 0, 0)), new GH_Path(0));
        source.PersistentData.Append(new GH_Number(7), new GH_Path(0));
        source.PersistentData.Append(new GH_Line(new Line(Point3d.Origin, new Point3d(4, 0, 0))), new GH_Path(0));
        source.PersistentData.Append(new GH_String("skip"), new GH_Path(0));
        source.PersistentData.Append(new GH_Surface(new PlaneSurface(new Plane(new Point3d(8, 0, 0), Vector3d.ZAxis),
            new Interval(-1, 1), new Interval(-1, 1))), new GH_Path(0));
        source.PersistentData.Append(new GH_Box(new Box(new BoundingBox(new Point3d(12, -1, -1), new Point3d(14, 1, 1)))), new GH_Path(1));
        source.PersistentData.Append(new GH_ObjectWrapper(new Point3d(-8, 6, 0)), new GH_Path(1));
        source.PersistentData.Append(new GH_Curve(), new GH_Path(1));
        source.PersistentData.Append(new GH_Point(new Point3d(-8, 0, 0)), new GH_Path(1));
        document.AddObject(source, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        using var canvas = new GH_Canvas { Size = new Size(1100, 900) };
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = System.Drawing.Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        component.Attributes.Pivot = new PointF(50, 50);
        try
        {
            document.NewSolution(false, GH_SolutionMode.Silent);
            var session = PanelTestAdapter.Session(component);
            await Wait(session);
            component.Attributes.PerformLayout();
            Paint(component, canvas);
            Check(!PanelTestAdapter.ShowItemOrder(component) && Labels(component).Count == 0, "New panels default to no viewport numbering");
            Select(component, session, 0, 5);
            var result = Result(session);
            var output = component.Params.Output[0].VolatileData;
            int solutions = 0;
            document.SolutionEnd += (_, __) => solutions++;
            int baselineSolutions = solutions;
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Preview));
            var menu = PanelTestAdapter.MenuBounds(component);
            Click(component, canvas, new RectangleF(menu.X + 4, menu.Y + 60, menu.Width - 8, 20));
            Check(PanelTestAdapter.ShowItemOrder(component) && Labels(component).Keys.SequenceEqual(new[] { "1", "6" }),
                "Preview's order toggle labels selected rows using global view numbers with non-geometric gaps");
            Check(Labels(component)["1"] == new Point3d(-8, 0, 0) && Labels(component)["6"] == new Point3d(13, 0, 0),
                "Point labels use their locations and volume labels use existing bounds centers");
            Paint(component, canvas);
            Check(PanelTestAdapter.Tooltip(component, canvas, 5).Title.StartsWith("View row 6 ·"),
                "Source-order row tooltips identify the global number shown in Rhino");
            document.UndoServer.PerformUndo();
            Check(!PanelTestAdapter.ShowItemOrder(component) && Labels(component).Count == 0 && PanelTestAdapter.SelectedPosition(component) == 0,
                "Undoing numbering preserves selected rows and removes labels");
            document.UndoServer.PerformRedo();
            Check(PanelTestAdapter.ShowItemOrder(component) && Labels(component).Keys.SequenceEqual(new[] { "1", "6" }) &&
                ReferenceEquals(result, Result(session)) && ReferenceEquals(output, component.Params.Output[0].VolatileData) && solutions == baselineSolutions,
                "Redo restores numbering without recomputing, changing output or losing selection");
            VerifyArchive(component, document);
            PanelTestAdapter.Minimize(component, true);
            component.Attributes.PerformLayout();
            Check(Labels(component).Keys.SequenceEqual(new[] { "1", "6" }), "Minimizing the panel retains active viewport labels");
            PanelTestAdapter.Minimize(component, false);
            component.Attributes.PerformLayout();

            PanelTestAdapter.PreviewMode(component, "FilteredResults");
            var labels = Labels(component);
            Check(labels.Count == 6 && labels["3"] == new Point3d(2, 0, 0) && labels["5"] == new Point3d(8, 0, 0) &&
                labels["7"] == new Point3d(-8, 6, 0) && !labels.ContainsKey("2") && !labels.ContainsKey("8"),
                "Filtered preview labels curves, surfaces and wrapped points while skipping scalar and invalid geometry");
            Query(session, new PanelQueryOptions { SortMode = PanelSortMode.ValueDescending });
            await Wait(session);
            int boxNumber = Result(session).RowPositions.ToList().IndexOf(5) + 1;
            Check(Labels(component)[boxNumber.ToString()] == new Point3d(13, 0, 0), "Sorting refreshes viewport numbering from the new result order");
            Query(session, new PanelQueryOptions { BranchPath = "{1}" });
            await Wait(session);
            Check(Labels(component).Keys.OrderBy(value => value).SequenceEqual(new[] { "1", "2", "4" }),
                "Filtering renumbers the result while unsupported rows still consume their positions");
            Query(session, new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "[" });
            Check(Labels(component).Count == 0, "Starting a query immediately releases stale numbering");
            await Wait(session, allowError: true);
            Check(Labels(component).Count == 0, "Failed queries cannot restore stale labels");
            Query(session, new PanelQueryOptions());
            await Wait(session);
            component.DocumentContextChanged(document, GH_DocumentContext.Unloaded);
            Check(Labels(component).Count == 0, "Inactive documents clear labels");
            component.DocumentContextChanged(document, GH_DocumentContext.Loaded);
            Check(Labels(component).Count == 6, "Returning to a document restores the current result labels");
            PanelTestAdapter.PreviewMode(component, "Off");
            Check(PanelTestAdapter.ShowItemOrder(component) && Labels(component).Count == 0, "Preview Off hides labels while retaining the user's toggle");
            PanelTestAdapter.PreviewMode(component, "FilteredResults");

            PanelTestAdapter.Resize(component, 300, 220);
            component.Attributes.PerformLayout();
            Paint(component, canvas);
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Display));
            Paint(component, canvas);
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Preview));
            Check(PanelTestAdapter.Bounds(component, CanvasControl.Panel).Contains(PanelTestAdapter.MenuBounds(component)),
                "The Preview menu including item order fits minimum Half mode");
            Paint(component, canvas, "item-order-menu-half");
            component.Locked = true;
            component.ExpireSolution(true);
            Check(Labels(component).Count == 0, "Disabling the panel releases cached label anchors");
        }
        finally { component.DocumentContextChanged(document, GH_DocumentContext.Close); }
    }

    internal static async Task CaptureViewport(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool previousModified = rhino.Modified;
        var previous = rhino.Views.ActiveView ?? rhino.Views.Add("Panel item-order checks", DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        var view = rhino.Views.Add("Panel item order", DefinedViewportProjection.Top, new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        var hidden = (GH_Component)Activator.CreateInstance(component.GetType())!;
        hidden.CreateAttributes();
        try
        {
            rhino.Views.ActiveView = view;
            document.Properties.KeepOpen = true;
            document.PreviewColourSelected = Color.FromArgb(100, 0, 210, 40);
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            var source = new Param_Geometry();
            source.CreateAttributes();
            source.Hidden = true;
            source.PersistentData.Append(new GH_Point(new Point3d(-4, 0, 0)), new GH_Path(0));
            source.PersistentData.Append(new GH_Curve(new LineCurve(new Point3d(-1, 0, 0), new Point3d(1, 0, 0))), new GH_Path(0));
            source.PersistentData.Append(new GH_Mesh(Mesh.CreateFromBox(new BoundingBox(new Point3d(3, -1, -1), new Point3d(5, 1, 1)), 1, 1, 1)), new GH_Path(0));
            source.PersistentData.Append(new GH_Point(new Point3d(-4, 0, 0)), new GH_Path(1));
            source.PersistentData.Append(new GH_Point(new Point3d(-4, 4, 0)), new GH_Path(1));
            document.AddObject(source, false);
            document.AddObject(component, false);
            component.Params.Input[0].AddSource(source);
            var hiddenSource = new Param_Geometry { Hidden = true };
            hiddenSource.CreateAttributes();
            hiddenSource.PersistentData.Append(new GH_Point(new Point3d(4, 4, 0)), new GH_Path(0));
            document.AddObject(hiddenSource, false);
            document.AddObject(hidden, false);
            hidden.Params.Input[0].AddSource(hiddenSource);
            document.NewSolution(false, GH_SolutionMode.Silent);
            var session = PanelTestAdapter.Session(component);
            await Wait(session);
            await Wait(PanelTestAdapter.Session(hidden));
            PanelTestAdapter.PreviewMode(hidden, "FilteredResults");
            PanelTestAdapter.ShowItemOrder(hidden, true);
            PanelTestAdapter.PreviewMode(component, "FilteredResults");
            Select(component, session, 3);
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-6, -2, -2), new Point3d(7, 6, 2)));
            var camera = view.ActiveViewport.CameraLocation;
            PanelTestAdapter.ShowItemOrder(component, true);
            view.Redraw();
            await Task.Delay(50);
            hidden.Hidden = true;
            Check(Labels(hidden).Count == 1, "Hidden preview retains its current cached anchor for unhide");
            view.Redraw();
            await Task.Delay(50);
            using var after = DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
            Check(after != null, "Rhino produces a native item-order capture");
            string path = Path.Combine(Path.GetDirectoryName(assembly.Location)!, "rhino-smoke-item-order-viewport.png");
            after!.Save(path);
            Check(view.ActiveViewport.CameraLocation == camera, "Order labels preserve the camera");
            int selectedPixels = 0;
            for (int y = 0; y < after.Height; y++)
                for (int x = 0; x < after.Width; x++)
                {
                    var color = after.GetPixel(x, y);
                    if (color.G > color.R + 60 && color.G > color.B + 60) selectedPixels++;
                }
            Check(selectedPixels > 80, "A selected coincident label wins over the earlier unselected item");
            Check(LightPixels(new Point3d(4, 0, 0)) > 100, "Surface-center labels remain opaque above shaded geometry");
            Check(LightPixels(new Point3d(4, 4, 0)) == 0, "Hiding a panel prevents its cached label from becoming a foreground ghost");
            var projector = assembly.GetType("BetterGhPanel.Runtime.PanelOrderLabels", true)!.GetMethod("TryProject", BindingFlags.Static | BindingFlags.NonPublic)!;
            Check(Projects(new Point3d(-4, 0, 0)) && !Projects(new Point3d(1e9, 1e9, 0)), "Anchor projection rejects offscreen centers");
            view.ActiveViewport.ChangeToPerspectiveProjection(true, 50);
            Check(!Projects(view.ActiveViewport.CameraLocation - view.ActiveViewport.CameraDirection * 100),
                "Anchor projection rejects centers behind a perspective camera");
            bool Projects(Point3d anchor) => (bool)projector.Invoke(null, new object[] { view.ActiveViewport, Transform.Identity, anchor, Point2d.Unset })!;
            int LightPixels(Point3d anchor)
            {
                var center = view.ActiveViewport.WorldToClient(anchor);
                int count = 0;
                for (int y = Math.Max(0, (int)center.Y - 22); y < Math.Min(after.Height, center.Y + 22); y++)
                    for (int x = Math.Max(0, (int)center.X - 26); x < Math.Min(after.Width, center.X + 26); x++)
                    {
                        var pixel = after.GetPixel(x, y);
                        if (pixel.R > 220 && pixel.G > 220 && pixel.B > 220) count++;
                    }
                return count;
            }
        }
        finally
        {
            document.Properties.KeepOpen = false;
            document.PreviewMode = GH_PreviewMode.Disabled;
            component.DocumentContextChanged(document, GH_DocumentContext.Close);
            hidden.DocumentContextChanged(document, GH_DocumentContext.Close);
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close();
            // View/camera edits mark the isolated Rhino document modified. Restore
            // its state so host shutdown does not wait for an invisible save prompt.
            rhino.Modified = previousModified;
        }
    }

    private static void VerifyArchive(GH_Component component, GH_Document document)
    {
        var chunk = new GH_LooseChunk("ItemOrder");
        component.Write(chunk);
        var restored = (GH_Component)Activator.CreateInstance(component.GetType())!;
        restored.CreateAttributes();
        Check(restored.Read(chunk) && PanelTestAdapter.ShowItemOrder(restored), "Saved definitions retain item-order visibility");
        chunk.RemoveItem("PanelShowItemOrder");
        Check(restored.Read(chunk) && !PanelTestAdapter.ShowItemOrder(restored), "Older definitions default to order labels off");
        restored.RemovedFromDocument(document);
    }

    private static Dictionary<string, Point3d> Labels(GH_Component component)
    {
        var labels = Field<object>(Field<object>(component, "_highlight"), "_orderLabels");
        return new[] { "_selected", "_normal" }.SelectMany(name => Field<IEnumerable>(labels, name).Cast<object>())
            .ToDictionary(label => Property<string>(label, "Text"), label => Property<Point3d>(label, "Anchor"));
    }

    private static void Select(GH_Component component, object session, params int[] positions)
    {
        var selection = Field<object>(component.Attributes, "_selection");
        selection.GetType().GetMethod("Clear", Instance)!.Invoke(selection, Array.Empty<object>());
        foreach (int position in positions)
            selection.GetType().GetMethod("Select", Instance)!.Invoke(selection, new object[] { Result(session), position, true, false, false });
        component.Attributes.GetType().GetMethod("OnQueryCompleted", Instance)!.Invoke(component.Attributes, Array.Empty<object>());
    }

    private static void Paint(GH_Component component, GH_Canvas canvas, string? name = null)
    {
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        canvas.Viewport.ApplyProjection(graphics);
        PanelTestAdapter.Render(component, canvas, graphics);
        if (name != null) bitmap.Save(Path.Combine(Path.GetDirectoryName(component.GetType().Assembly.Location)!, "rhino-smoke-" + name + ".png"));
    }
    private static void Click(GH_Component component, GH_Canvas canvas, RectangleF bounds) => component.Attributes.RespondToMouseDown(canvas,
        new GH_CanvasMouseEvent(System.Drawing.Point.Empty, new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), Forms.MouseButtons.Left, 1, 0));
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static PanelQueryResult Result(object session) => Property<PanelQueryResult>(session, "Result");
    private static void Query(object session, PanelQueryOptions options) => session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { options });
    private static async Task Wait(object session, bool allowError = false)
    {
        var timer = Stopwatch.StartNew();
        while (Property<bool>(session, "IsBusy"))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new InvalidOperationException("Order-label query timed out");
            await Task.Delay(20);
        }
        if (!allowError) Check(Property<string?>(session, "Error") == null, "Order-label query completes successfully");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
