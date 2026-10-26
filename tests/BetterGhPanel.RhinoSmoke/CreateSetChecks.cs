using System;
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

using Forms = System.Windows.Forms;

internal static class CreateSetChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void VerifyTree(Assembly assembly)
    {
        var tools = assembly.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!;
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("CaptureWithSources", BindingFlags.Static | BindingFlags.NonPublic)!;
        var project = assembly.GetType("BetterGhPanel.Runtime.PanelOutputOperations", true)!.GetMethod("DisplaySet", BindingFlags.Static | BindingFlags.NonPublic)!;
        var source = new GH_Structure<IGH_Goo>();
        source.AppendRange(new IGH_Goo[] { new GH_Number(2.004), new GH_Number(2.003), new GH_String("alpha"), new GH_String("alpha") }, new GH_Path(7, 0));
        source.AppendRange(new IGH_Goo[] { new GH_Number(2.004), new GH_Number(3) }, new GH_Path(7, 1));
        source.EnsurePath(new GH_Path(7, 2));
        var arguments = new object?[] { source, 0, false, false, false, null };
        var transformed = (GH_Structure<IGH_Goo>)tools.GetMethod("ApplyWithSources", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments)!;
        var addresses = (System.Collections.Generic.IReadOnlyList<PanelSourceAddress>)arguments[5]!;
        var snapshot = (PanelSnapshot)capture.Invoke(null, new object?[] { transformed, null, PanelUnitConversion.None, "mm", addresses })!;
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { CreateSet = true, DecimalPlaces = 2 });
        var output = (GH_Structure<IGH_Goo>)project.Invoke(null, new object[] { transformed, result, result.RowPositions, result.BranchPaths })!;
        Check(output.PathCount == 3 && output.DataCount == 4 && output.Branches[0][0] is GH_Number number && number.Value == 2 &&
            output.Branches[0][1] is GH_String text && text.Value == "alpha" && output.Branches[2].Count == 0,
            "Create set projects displayed numbers/text, compares after formatting and preserves branch structure");
        Check(result.SetOccurrencesAt(0)!.Count == 2 && result.SetOccurrencesAt(0)!.Locations[1].ItemIndex == 1 &&
            result.SetOccurrencesAt(0)!.Locations[0].Path == "{7;0}" && source.DataCount == 6 && ((GH_Number)source.Branches[0][0]).Value == 2.004,
            "Displayed sets retain original occurrence addresses and never mutate input values");
    }
    internal static async Task Run(Assembly assembly)
    {
        VerifyTree(assembly);
        using var document = new GH_Document { Enabled = true };
        GH_Document.EnableSolutions = true;
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        var source = new Param_GenericObject();
        source.CreateAttributes();
        source.PersistentData.AppendRange(new IGH_Goo[] { new GH_Number(2), new GH_Number(1), new GH_Integer(2),
            new GH_String("alpha"), new GH_String("alpha") }, new GH_Path(0));
        source.PersistentData.AppendRange(new IGH_Goo[] { new GH_Number(2), new GH_Number(2), new GH_Number(3) }, new GH_Path(1));
        source.PersistentData.EnsurePath(new GH_Path(2));
        document.AddObject(source, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        using var canvas = new GH_Canvas { Size = new Size(650, 500) };
        component.Attributes.Pivot = new PointF(50, 50);
        try
        {
            document.NewSolution(false, GH_SolutionMode.Silent);
            var session = PanelTestAdapter.Session(component);
            await Wait(session);
            component.Attributes.PerformLayout();
            document.UndoServer.Clear();
            Click(component, canvas, CanvasControl.CreateSet);
            await Wait(session);
            Check(PanelTestAdapter.TreeFlag(component, "CreateSet") && component.Params.Output[0].VolatileData.DataCount == 5,
                "One canvas click enables Create set and removes duplicates on the output wire");
            Check(source.PersistentData.DataCount == 8, "Canvas Create set preserves upstream data");
            Paint(component, canvas, "create-set");
            document.UndoServer.PerformUndo();
            await Wait(session);
            Check(!PanelTestAdapter.TreeFlag(component, "CreateSet") && component.Params.Output[0].VolatileData.DataCount == 8,
                "Undo restores the original output after Create set");
            document.UndoServer.PerformRedo();
            await Wait(session);
            Check(PanelTestAdapter.TreeFlag(component, "CreateSet") && component.Params.Output[0].VolatileData.DataCount == 5,
                "Redo reapplies Create set");

            var archive = new GH_LooseChunk("Panel");
            Check(component.Write(archive), "Create set fixture saves");
            var restored = (GH_Component)Activator.CreateInstance(component.GetType())!;
            restored.CreateAttributes();
            try
            {
                Check(restored.Read(archive) && PanelTestAdapter.TreeFlag(restored, "CreateSet"), "Create set survives save/load");
                archive.RemoveItem("PanelCreateSet");
                Check(restored.Read(archive) && !PanelTestAdapter.TreeFlag(restored, "CreateSet"), "Older definitions default Create set off");
            }
            finally { restored.RemovedFromDocument(document); }

            Query(session, new PanelQueryOptions { BranchPath = "{1}", BranchPrefix = true, SortMode = PanelSortMode.ValueDescending });
            await Wait(session);
            Click(component, canvas, CanvasControl.CreateSet);
            await Wait(session);
            Click(component, canvas, CanvasControl.CreateSet);
            await Wait(session);
            Check(Property<PanelQueryOptions>(session, "Options").BranchPath == "{1}" &&
                Property<PanelQueryOptions>(session, "Options").BranchPrefix && component.Params.Output[0].VolatileData.DataCount == 2,
                "Create set preserves branch filters and sorting while filtering transformed output");
            Click(component, canvas, CanvasControl.Flatten);
            await Wait(session);
            Check(PanelTestAdapter.TreeFlag(component, "CreateSet") && component.Params.Output[0].VolatileData.DataCount == 4 &&
                component.Params.Output[0].VolatileData.PathCount == 1, "Flatten retains Create set and produces one global set");
            Click(component, canvas, CanvasControl.Reverse);
            await Wait(session);
            Check(PanelTestAdapter.TreeFlag(component, "CreateSet") &&
                component.Params.Output[0].VolatileData.get_Branch(0)[0] is GH_Number first && first.Value == 3,
                "Reverse retains Create set and changes the retained occurrence order");

            PanelTestAdapter.Resize(component, 300, 220);
            Click(component, canvas, CanvasControl.Display);
            component.Attributes.PerformLayout();
            var set = PanelTestAdapter.Bounds(component, CanvasControl.CreateSet);
            Check(PanelTestAdapter.Bounds(component, CanvasControl.Panel).Contains(set) &&
                set.Left > PanelTestAdapter.Bounds(component, CanvasControl.Reverse).Right &&
                set.Right < PanelTestAdapter.Bounds(component, CanvasControl.Units).Left &&
                !set.IntersectsWith(PanelTestAdapter.Bounds(component, CanvasControl.Resize)),
                "Create set fits beside Reverse without overlapping Units or Resize in minimum Half layout");
            Paint(component, canvas, "create-set-half");
            Click(component, canvas, CanvasControl.CreateSet);
            await Wait(session);
            Check(!PanelTestAdapter.TreeFlag(component, "CreateSet"), "Create set remains clickable in minimum Half layout");
            Click(component, canvas, CanvasControl.CreateSet);
            await Wait(session);
            Click(component, canvas, CanvasControl.Reset);
            await Wait(session);
            Check(!PanelTestAdapter.TreeFlag(component, "CreateSet") && component.Params.Output[0].VolatileData.DataCount == 8,
                "Reset clears Create set and restores all input values");
            document.UndoServer.PerformUndo();
            await Wait(session);
            Check(PanelTestAdapter.TreeFlag(component, "CreateSet"), "Undo Reset restores Create set");

        }
        finally { component.DocumentContextChanged(document, GH_DocumentContext.Close); }
    }

    private static void Click(GH_Component component, GH_Canvas canvas, CanvasControl control)
    {
        var bounds = PanelTestAdapter.Bounds(component, control);
        var point = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var mouse = new GH_CanvasMouseEvent(System.Drawing.Point.Empty, point, Forms.MouseButtons.Left, 1, 0);
        component.Attributes.RespondToMouseDown(canvas, mouse);
        component.Attributes.RespondToMouseUp(canvas, mouse);
    }

    private static void Paint(GH_Component component, GH_Canvas canvas, string name)
    {
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = System.Drawing.Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        using var bitmap = new Bitmap(650, 500);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        canvas.Viewport.ApplyProjection(graphics);
        PanelTestAdapter.Render(component, canvas, graphics);
        bitmap.Save(Path.Combine(Path.GetDirectoryName(component.GetType().Assembly.Location)!, "rhino-smoke-" + name + ".png"));
    }

    private static void Query(object session, PanelQueryOptions options) =>
        session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { options });
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static async Task Wait(object session)
    {
        var clock = Stopwatch.StartNew();
        while (Property<bool>(session, "IsBusy"))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new InvalidOperationException("Create set query timed out");
            await Task.Delay(20);
        }
        Check(Property<string?>(session, "Error") == null, "Create set query completed successfully");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
