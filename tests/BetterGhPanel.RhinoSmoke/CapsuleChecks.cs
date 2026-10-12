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

internal static class CapsuleChecks
{
    internal static async Task Run(Assembly assembly)
    {
        using var document = new GH_Document { Enabled = true };
        GH_Document.EnableSolutions = true;
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        var source = new Param_GenericObject();
        source.CreateAttributes();
        for (int i = 0; i < 120; i++) source.PersistentData.Append(new GH_Number(i), new GH_Path(i / 40));
        document.AddObject(source, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        document.NewSolution(false, GH_SolutionMode.Silent);
        var session = PanelTestAdapter.Session(component);
        await Wait(session);
        using var canvas = new GH_Canvas { Size = new Size(1000, 800) };
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = Point.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        component.Attributes.Pivot = new PointF(50, 50);
        PanelTestAdapter.Resize(component, 380, 420);
        component.Attributes.PerformLayout();
        int solutions = 0;
        document.SolutionEnd += (_, __) => solutions++;

        foreach (bool half in new[] { false, true })
        {
            if (half) Click(component, canvas, CanvasControl.Display);
            Paint(component, canvas);
            Click(component, canvas, CanvasControl.Jump);
            CanvasEditorAdapter.Active(assembly).Submit("31");
            Paint(component, canvas);
            Click(component, canvas, PanelTestAdapter.RowBounds(component, 30));
            var panel = PanelTestAdapter.Bounds(component, CanvasControl.Panel);
            var pivot = component.Attributes.Pivot;
            var input = component.Params.Input[0].Attributes.InputGrip;
            var output = component.Params.Output[0].Attributes.OutputGrip;
            var result = Property<PanelQueryResult>(session, "Result");
            var outputData = component.Params.Output[0].VolatileData;
            var options = Property<PanelQueryOptions>(session, "Options");
            var preview = PanelTestAdapter.PreviewMode(component);
            int previousSolutions = solutions;
            document.UndoServer.Clear();
            Click(component, canvas, CanvasControl.Type);
            Check(PanelTestAdapter.MenuOpen(component), "Type menu opens before minimizing");
            Click(component, canvas, CanvasControl.Close);
            Paint(component, canvas, half ? "capsule-from-half" : "capsule");
            var capsule = PanelTestAdapter.Bounds(component, CanvasControl.Panel);
            Check(PanelTestAdapter.Minimized(component) && capsule.Size == new SizeF(216, 32), "Close shrinks both dimensions to a capsule");
            Check(component.Attributes.Pivot == pivot && PanelTestAdapter.Size(component) == new SizeF(380, 420), "Capsule retains canonical pivot and saved dimensions");
            Check(component.Attributes.Bounds.Width == capsule.Width + 12 && component.Attributes.Bounds.Height == capsule.Height,
                "Capsule has no invisible full-panel bounds");
            Check(component.Params.Input[0].Attributes.InputGrip == new PointF(capsule.Left, capsule.Top + 16) &&
                component.Params.Output[0].Attributes.OutputGrip == new PointF(capsule.Right, capsule.Top + 16), "Native wire grips lie on capsule edges");
            Check(!PanelTestAdapter.MenuOpen(component) && PanelTestAdapter.Hits(component, "_rowHits") == 0 && PanelTestAdapter.Hits(component, "_branchHits") == 0,
                "Capsule closes menus and clears hidden row and branch targets");
            foreach (CanvasControl control in Enum.GetValues(typeof(CanvasControl)))
                if (control != CanvasControl.Panel && control != CanvasControl.Restore)
                    Check(PanelTestAdapter.Bounds(component, control).IsEmpty, "Capsule clears hidden " + control + " target");
            Check(PanelTestAdapter.CapsuleSummary(component) == "120 items · 3 branches", "Capsule shows the current result counts");
            Check(Tooltip(component, canvas, CanvasControl.Restore).Title == "Restore panel", "Capsule restore arrow has an explicit tooltip");

            document.UndoServer.PerformUndo();
            component.Attributes.PerformLayout();
            Check(!PanelTestAdapter.Minimized(component) && PanelTestAdapter.Bounds(component, CanvasControl.Panel) == panel, "Undo restores the preceding Full/Half rectangle");
            document.UndoServer.PerformRedo();
            component.Attributes.PerformLayout();
            Check(PanelTestAdapter.Minimized(component), "Redo returns to the capsule");
            VerifyArchive(component, document, panel);
            var restoreTarget = PanelTestAdapter.Bounds(component, CanvasControl.Restore);
            Click(component, canvas, CanvasControl.Restore);
            var trailing = new GH_CanvasMouseEvent(Point.Empty, new PointF(restoreTarget.X + restoreTarget.Width / 2,
                restoreTarget.Y + restoreTarget.Height / 2), Forms.MouseButtons.Left, 2, 0);
            component.Attributes.RespondToMouseDown(canvas, trailing);
            component.Attributes.RespondToMouseDoubleClick(canvas, trailing);
            Check(!PanelTestAdapter.MenuOpen(component), "Restore consumes trailing double-click events before exposed controls can activate");
            Paint(component, canvas, half ? "capsule-restored-half" : "capsule-restored-full");
            Check(PanelTestAdapter.Bounds(component, CanvasControl.Panel) == panel && component.Attributes.Pivot == pivot &&
                component.Params.Input[0].Attributes.InputGrip == input && component.Params.Output[0].Attributes.OutputGrip == output,
                "Restore returns the exact preceding rectangle, pivot and grip positions");
            Check(PanelTestAdapter.Offset(component) == 30 && PanelTestAdapter.SelectedPosition(component) == 30,
                "Minimize, undo, redo and restore retain scrolling and row selection");
            Check(ReferenceEquals(result, Property<PanelQueryResult>(session, "Result")) && ReferenceEquals(outputData, component.Params.Output[0].VolatileData) &&
                Property<PanelQueryOptions>(session, "Options").Search == options.Search && PanelTestAdapter.PreviewMode(component) == preview && solutions == previousSolutions,
                "Capsule gestures and undo leave query, preview and typed output intact without solving");

            Click(component, canvas, CanvasControl.Search);
            var draft = CanvasEditorAdapter.Active(assembly);
            Click(component, canvas, CanvasControl.Close);
            draft.AcceptAgain("discard this draft");
            Check(Property<PanelQueryOptions>(session, "Options").Search == options.Search, "Minimize discards its owned draft editor");
            Click(component, canvas, CanvasControl.Restore);
        }

        var other = (GH_Component)Activator.CreateInstance(component.GetType())!;
        other.CreateAttributes();
        other.Attributes.Pivot = new PointF(500, 50);
        other.Attributes.PerformLayout();
        Click(other, canvas, CanvasControl.Search);
        var otherDraft = CanvasEditorAdapter.Active(assembly);
        PanelTestAdapter.Minimize(component, true);
        otherDraft.Submit("other panel draft");
        Check(Property<PanelQueryOptions>(PanelTestAdapter.Session(other), "Options").Search == "other panel draft",
            "Minimize preserves another panel's active editor");
        other.RemovedFromDocument(document);

        // Invalidated or failed input must never reuse the prior count label.
        PanelTestAdapter.Minimize(component, true);
        component.Attributes.PerformLayout();
        component.ClearData();
        Paint(component, canvas);
        Check(PanelTestAdapter.CapsuleSummary(component) == "Recomputing…", "Capsule hides stale counts while waiting for a new solution");
        component.Locked = true;
        Paint(component, canvas);
        Check(PanelTestAdapter.CapsuleSummary(component) == "Disabled", "Disabled capsule has an explicit status");
        component.Locked = false;
        document.NewSolution(false, GH_SolutionMode.Silent);
        await Wait(session);
        session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "[" } });
        await Wait(session);
        Paint(component, canvas);
        Check(PanelTestAdapter.CapsuleSummary(component) == "Filter needs attention", "Failed capsule shows an error status instead of stale counts");
        component.DocumentContextChanged(document, GH_DocumentContext.Close);
    }

    private static void VerifyArchive(GH_Component component, GH_Document document, RectangleF expanded)
    {
        var archive = new GH_Archive();
        var restored = (GH_Component)Activator.CreateInstance(component.GetType())!;
        restored.CreateAttributes();
        Check(archive.AppendObject(component, "Capsule") && archive.ExtractObject(restored, "Capsule"), "Capsule settings serialize");
        restored.Attributes.PerformLayout();
        Check(PanelTestAdapter.Minimized(restored) && PanelTestAdapter.Size(restored) == PanelTestAdapter.Size(component), "Saved capsule retains canonical dimensions");
        PanelTestAdapter.Minimize(restored, false);
        restored.Attributes.PerformLayout();
        Check(PanelTestAdapter.Bounds(restored, CanvasControl.Panel) == expanded, "Saved capsule restores its original Full/Half rectangle");
        var legacy = new GH_LooseChunk("Legacy");
        component.Write(legacy);
        legacy.RemoveItem("PanelMinimized");
        legacy.RemoveItem("PanelDisplay");
        legacy.SetInt32("PanelDisplay", 2);
        Check(restored.Read(legacy), "Legacy collapsed document loads");
        restored.Attributes.PerformLayout();
        Check(!PanelTestAdapter.Minimized(restored) && PanelTestAdapter.Bounds(restored, CanvasControl.Panel).Height == 420,
            "Legacy PanelDisplay=2 still restores Full, without opting into the new capsule");
        restored.RemovedFromDocument(document);
    }

    private static void Paint(GH_Component component, GH_Canvas canvas, string? name = null)
    {
        var bounds = component.Attributes.Bounds;
        using var bitmap = new Bitmap((int)(bounds.Width + 30) * 2, (int)(bounds.Height + 30) * 2);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(239, 240, 237));
        graphics.ScaleTransform(2, 2);
        graphics.TranslateTransform(15 - bounds.X, 15 - bounds.Y);
        PanelTestAdapter.Render(component, canvas, graphics);
        if (name != null) bitmap.Save(Path.Combine(Path.GetDirectoryName(component.GetType().Assembly.Location)!, "rhino-smoke-" + name + ".png"));
    }

    private static GH_TooltipDisplayEventArgs Tooltip(GH_Component component, GH_Canvas canvas, CanvasControl control)
    {
        var bounds = PanelTestAdapter.Bounds(component, control);
        var point = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var args = (GH_TooltipDisplayEventArgs)Activator.CreateInstance(typeof(GH_TooltipDisplayEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { canvas, Point.Round(point) }, null)!;
        component.Attributes.SetupTooltip(point, args);
        return args;
    }

    private static void Click(GH_Component component, GH_Canvas canvas, CanvasControl control) => Click(component, canvas, PanelTestAdapter.Bounds(component, control));
    private static void Click(GH_Component component, GH_Canvas canvas, RectangleF bounds) =>
        component.Attributes.RespondToMouseDown(canvas, new GH_CanvasMouseEvent(Point.Empty, new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), Forms.MouseButtons.Left, 1, 0));
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name)!.GetValue(owner)!;
    private static async Task Wait(object session)
    {
        var timer = Stopwatch.StartNew();
        while (Property<bool>(session, "IsBusy"))
        {
            if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Capsule query did not finish");
            await Task.Delay(10);
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
