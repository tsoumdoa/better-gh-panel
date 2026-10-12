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
using Rhino.Geometry;
using Forms = System.Windows.Forms;

internal static class StatisticsUiChecks
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
        source.PersistentData.Append(new GH_Number(1), new GH_Path(0));
        source.PersistentData.Append(new GH_Integer(1), new GH_Path(0));
        source.PersistentData.Append(new GH_Number(1.0000000000000002), new GH_Path(0));
        foreach (string value in new[] { "1", "Alpha", "Alpha", "alpha", "", new string('x', 180) + "\n\t" })
            source.PersistentData.Append(new GH_String(value), new GH_Path(0));
        source.PersistentData.Append(new GH_Number(20), new GH_Path(1));
        source.PersistentData.Append(new GH_Point(Point3d.Origin), new GH_Path(1));
        source.PersistentData.EnsurePath(new GH_Path(2));
        document.AddObject(source, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        using var canvas = new GH_Canvas { Size = new Size(1100, 900) };
        component.Attributes.Pivot = new PointF(50, 50);
        try
        {
            document.NewSolution(false, GH_SolutionMode.Silent);
            var session = PanelTestAdapter.Session(component);
            await Wait(session);
            var result = Result(session);
            Check(result.Statistics.ItemCount == 11 && result.Statistics.ScalarCount == 10 && result.Statistics.UniqueValues.Count == 8,
                "Native Number/Integer/Text capture produces exact distinct frequencies without geometry summaries");
            Check(result.Snapshot.Rows[4].StringValue == "Alpha" && result.Snapshot.Rows[8].StringValue!.EndsWith("\n\t"),
                "Text scalar provenance retains its full original value");
            var output = component.Params.Output[0].VolatileData.AllData(true).ToArray();
            component.Attributes.PerformLayout();
            Paint(component, canvas);
            Click(component, canvas, PanelTestAdapter.RowBounds(component, 0));
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Statistics));
            Check(PanelTestAdapter.MenuOpen(component), "Stats opens from its dedicated footer affordance");
            Paint(component, canvas, "statistics");
            var bounds = PanelTestAdapter.MenuBounds(component);
            Click(component, canvas, new RectangleF(bounds.X + 5, bounds.Y + 2, bounds.Width - 10, 16));
            Check(PanelTestAdapter.MenuOpen(component) && PanelTestAdapter.SelectedPosition(component) == 0 &&
                component.Params.Output[0].VolatileData.AllData(true).SequenceEqual(output),
                "Inspecting a statistics row preserves selection and typed output");

            Query(session, new PanelQueryOptions { Search = "Alpha", SearchMode = PanelSearchMode.Regex });
            Check(!PanelTestAdapter.MenuOpen(component), "Changing a query immediately dismisses Stats on the same snapshot");
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Statistics));
            Check(!PanelTestAdapter.MenuOpen(component), "Stats cannot reopen on a pending query's old aggregates");
            await Wait(session);
            Check(ReferenceEquals(Result(session).Snapshot, result.Snapshot) && Result(session).Statistics.ItemCount == 3 &&
                Result(session).Statistics.UniqueValues.Count == 2 && Result(session).Statistics.BranchItemCounts["{0}"] == 3,
                "Filtered counts and case-sensitive frequencies publish together on the original snapshot");
            Paint(component, canvas);
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Statistics));
            Paint(component, canvas, "statistics-filtered");
            Query(session, new PanelQueryOptions { Search = "[", SearchMode = PanelSearchMode.Regex });
            await Wait(session, allowError: true);
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Statistics));
            Check(!PanelTestAdapter.MenuOpen(component), "A failed query cannot show stale statistics");

            Query(session, new PanelQueryOptions());
            await Wait(session);
            PanelTestAdapter.Units(component, PanelUnitConversion.MillimetersToMeters, 0);
            await Wait(session);
            Check(Result(session).Statistics.UniqueValues[0].NumericValue == 0.001 &&
                Result(session).Statistics.UniqueValues[0].Count == 2 && Result(session).Statistics.UniqueValues.Count == 8,
                "Unit conversion updates numeric identities while zero-decimal display leaves exact frequencies intact");
            PanelTestAdapter.Resize(component, 300, 220);
            component.Attributes.PerformLayout();
            Paint(component, canvas);
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Display));
            Paint(component, canvas);
            var panel = PanelTestAdapter.Bounds(component, CanvasControl.Panel);
            var stats = PanelTestAdapter.Bounds(component, CanvasControl.Statistics);
            Check(PanelTestAdapter.Bounds(component, CanvasControl.List).Bottom <= stats.Top &&
                stats.Bottom <= PanelTestAdapter.Bounds(component, CanvasControl.Units).Top,
                "Minimum Half retains a complete row with separate Stats and tree-tool hit regions");
            Click(component, canvas, stats);
            Check(panel.Contains(PanelTestAdapter.MenuBounds(component)), "Minimum Half Stats stays within the panel");
            Paint(component, canvas, "statistics-half");
            var menu = component.Attributes.GetType().GetField("_menu", Instance)!.GetValue(component.Attributes)!;
            int menuCount = Property<int>(menu, "Count");
            for (int i = 0; i < menuCount; i++)
            {
                bounds = PanelTestAdapter.MenuBounds(component);
                Click(component, canvas, new RectangleF(bounds.Right - 20, bounds.Bottom - 18, 16, 16));
            }
            Check(Property<int>(menu, "Offset") + Property<int>(menu, "Visible") == menuCount,
                "Native minimum Half paging reaches the final distinct value");
            Paint(component, canvas, "statistics-half-last");
            component.Locked = true;
            Paint(component, canvas);
            Click(component, canvas, stats);
            Check(!PanelTestAdapter.MenuOpen(component), "Disabled components dismiss Stats and block stale reopening");
        }
        finally { component.DocumentContextChanged(document, GH_DocumentContext.Close); }
    }

    private static void Paint(GH_Component component, GH_Canvas canvas, string? name = null)
    {
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = System.Drawing.Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        using var bitmap = new Bitmap(1100, 900);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        canvas.Viewport.ApplyProjection(graphics);
        PanelTestAdapter.Render(component, canvas, graphics);
        if (name != null)
        {
            bitmap.Save(Path.Combine(Path.GetDirectoryName(component.GetType().Assembly.Location)!, "rhino-smoke-" + name + ".png"));
        }
    }

    private static void Click(GH_Component component, GH_Canvas canvas, RectangleF bounds)
    {
        var point = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var mouse = new GH_CanvasMouseEvent(System.Drawing.Point.Empty, point, Forms.MouseButtons.Left, 1, 0);
        component.Attributes.RespondToMouseDown(canvas, mouse);
        component.Attributes.RespondToMouseUp(canvas, mouse);
    }

    private static void Query(object session, PanelQueryOptions options) =>
        session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { options });
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static PanelQueryResult Result(object session) => Property<PanelQueryResult>(session, "Result");
    private static async Task Wait(object session, bool allowError = false)
    {
        var clock = Stopwatch.StartNew();
        while (Property<bool>(session, "IsBusy"))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new InvalidOperationException("Statistics query timed out");
            await Task.Delay(20);
        }
        if (!allowError) Check(Property<string?>(session, "Error") == null, "Statistics query completed successfully");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
