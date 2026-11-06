using static PanelTestAdapter;
using static SmokeAssertions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;


internal static partial class PreviewSafetyChecks
{
    internal static async Task RunReentrantDraw(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified;
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Reentrant preview fixture", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        try
        {
            rhino.Views.ActiveView = view;
            document.Properties.KeepOpen = true;
            document.PreviewMeshType = GH_PreviewMesh.LowQuality;
            var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            component.CreateAttributes(); document.AddObject(component, false);
            object highlight = FieldValue<object>(component, "_highlight");
            using var conduit = new PreviewConduit(component, document) { Enabled = true };
            using var polyline = new PolylineCurve(new[] { Point3d.Origin, new Point3d(2, 0, 0), new Point3d(2, 3, 0) });
            var group = new GH_GeometryGroup(); group.Objects.Add(new GH_Curve { Value = polyline });
            var custom = new StandalonePointPreview();
            Set(highlight, new IGH_Goo?[] { group, custom }, new[] { 0, 1 }, new[] { 0, 1 });
            var ownedCurve = OwnedGeometry(highlight).OfType<PolylineCurve>().Single();
            bool wireCalled = false, wireHeld = false;
            custom.OnWire = () =>
            {
                wireCalled = true;
                Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());
                wireHeld = !ownedCurve.Disposed;
            };
            view.Redraw();
            using (var bitmap = CapturePreview(view, highlight, "reentrant wire", conduit)) { }
            await WaitForPreviewRetirement(highlight);
            Check(wireCalled && wireHeld && ownedCurve.Disposed && Count(highlight) == 0,
                $"clearing during a standalone wire callback keeps grouped geometry alive until the draw exits " +
                $"(called={wireCalled}, held={wireHeld}, disposed={ownedCurve.Disposed}, count={Count(highlight)}, draws={conduit.Draws})");

            using var brep = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
            group = new GH_GeometryGroup(); group.Objects.Add(new GH_Brep { Value = brep });
            Set(highlight, new IGH_Goo?[] { group, custom }, new[] { 0, 1 }, new[] { 0, 1 });
            var ownedBrep = OwnedGeometry(highlight).OfType<Brep>().Single();
            var live = assembly.GetType("BetterGhPanel.Runtime.PanelPreviewLeaf", true)!.GetProperty("LiveGeneratedMeshes", AnyStatic)!;
            bool meshCalled = false, meshHeld = false;
            custom.OnMesh = () =>
            {
                meshCalled = true;
                document.PreviewMeshType = GH_PreviewMesh.HighQuality;
                Set(highlight, new IGH_Goo?[] { new GH_Point(new Point3d(50, 0, 0)) }, new[] { 0 }, new[] { 0 });
                component.ExpirePreview(false);
                meshHeld = !ownedBrep.Disposed && (int)live.GetValue(null)! > 0;
            };
            view.Redraw();
            using (var bitmap = CapturePreview(view, highlight, "reentrant mesh", conduit)) { }
            await WaitForPreviewRetirement(highlight);
            Check(meshCalled && meshHeld && ownedBrep.Disposed && (int)live.GetValue(null)! == 0 &&
                Count(highlight) == 1 && Box(highlight).Contains(new Point3d(50, 0, 0)),
                $"meshing reset and replacement defer grouped Brep and generated mesh disposal until shading exits " +
                $"(called={meshCalled}, held={meshHeld}, disposed={ownedBrep.Disposed}, live={(int)live.GetValue(null)!}, " +
                $"count={Count(highlight)}, bounds={Box(highlight)}, draws={conduit.Draws})");
            Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());

            var originalPoint = new GH_Point(new Point3d(5, 0, 0));
            var wrapper = new GH_GeometricGooWrapper(originalPoint);
            var wrappedValue = typeof(GH_GeometricGooWrapper).GetField("m_goo", AnyInstance)!;
            var capturedGroup = new GH_GeometryGroup(); capturedGroup.Objects.Add(new GH_Curve { Value = polyline }); capturedGroup.Objects.Add(wrapper);
            GeometryBase? capturedOwned = null;
            bool replacedAndRestored = false;
            Action<int, object> observer = (index, leaf) =>
            {
                if (index != 0) return;
                capturedOwned = FieldValue<GeometryBase?>(leaf, "_owned");
                wrappedValue.SetValue(wrapper, new GH_Point(new Point3d(300, 0, 0)));
                capturedGroup.Objects[1] = new GH_Point(new Point3d(400, 0, 0));
                capturedGroup.Objects[1] = wrapper; wrappedValue.SetValue(wrapper, originalPoint);
                replacedAndRestored = true;
            };
            highlight.GetType().GetField("_admissionObserver", AnyInstance)!.SetValue(highlight, observer);
            Set(highlight, new IGH_Goo?[] { capturedGroup }, new[] { 0 }, new[] { 0 });
            highlight.GetType().GetField("_admissionObserver", AnyInstance)!.SetValue(highlight, null);
            Check(replacedAndRestored && capturedOwned != null && !capturedOwned.Disposed && Count(highlight) == 1 &&
                Box(highlight).Contains(new Point3d(5, 0, 0)) && !Box(highlight).Contains(new Point3d(300, 0, 0)),
                "preview construction uses captured leaves after a group replacement and wrapper retarget are restored");
            var entries = (IEnumerable)FieldValue<object>(highlight, "_entries");
            var pair = entries.Cast<object>().Single();
            var entry = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            var leaves = ((IEnumerable)FieldValue<object>(entry, "Leaves")).Cast<object>().ToArray();
            Check(FieldValue<object>(leaves[1], "_value") is Point3d point && point == new Point3d(5, 0, 0),
                "the drawable point and captured bounds come from the same graph generation");
            component.ExpirePreview(false);
            Method(document, "CreatePreviewCaches").Invoke(document, Array.Empty<object>());
            Check(document.PreviewBoundingBox.Contains(new Point3d(5, 0, 0)) &&
                !document.PreviewBoundingBox.Contains(new Point3d(300, 0, 0)), "host bounds refresh to the captured generation");
            int previousDraws = conduit.Draws; view.Redraw();
            using (var bitmap = CapturePreview(view, highlight, "reentrant captured generation", conduit)) { }
            Check(conduit.Draws > previousDraws, "the captured generation reaches the actual viewport draw callback");
            Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());
            await WaitForPreviewRetirement(highlight);
            Check(capturedOwned!.Disposed && !Box(highlight).IsValid, "clearing the captured generation disposes its owned geometry");
        }
        finally
        {
            document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
    }

    private static IEnumerable<GeometryBase> OwnedGeometry(object highlight)
    {
        foreach (object pair in (IEnumerable)FieldValue<object>(highlight, "_entries"))
        {
            object entry = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            foreach (object leaf in (IEnumerable)FieldValue<object>(entry, "Leaves"))
                if (FieldValue<object?>(leaf, "_owned") is GeometryBase owned) yield return owned;
        }
    }

    private static IEnumerable<Mesh> OwnedBoxMeshes(object highlight)
    {
        foreach (object pair in (IEnumerable)FieldValue<object>(highlight, "_entries"))
        {
            object entry = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            foreach (object leaf in (IEnumerable)FieldValue<object>(entry, "Leaves"))
                if (FieldValue<object?>(leaf, "_boxMesh") is Mesh mesh) yield return mesh;
        }
    }

    private static async Task WaitForPreviewRetirement(object highlight)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (PropertyValue<bool>(highlight, "RetirementPending"))
        { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Preview resources did not retire on Rhino idle."); await Task.Delay(25); }
    }

    private static Bitmap CapturePreview(Rhino.Display.RhinoView view, object highlight, string phase, PreviewConduit? conduit)
    {
        string State() => $"{phase}: entries={Count(highlight)}, bounds={Box(highlight)}, bytes={PropertyValue<long>(highlight, "RetainedBytes")}, " +
            $"wire={PropertyValue<int>(highlight, "DrawWireCalls")}, mesh={PropertyValue<int>(highlight, "DrawMeshCalls")}, " +
            $"retirementPending={PropertyValue<bool>(highlight, "RetirementPending")}, idleReleases={PropertyValue<int>(highlight, "IdleReleases")}, " +
            $"conduit={conduit?.Draws ?? 0}, mesh callbacks={conduit?.MeshesCompleted ?? 0}/{conduit?.MeshesStarted ?? 0}, " +
            $"wire callbacks={conduit?.WiresCompleted ?? 0}/{conduit?.WiresStarted ?? 0}, status={Status(highlight)}";
        Console.WriteLine("Before preview capture: " + State());
        Console.Out.Flush();
        try { return Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480); }
        catch (Exception ex) { Console.WriteLine("Preview capture failed: " + State() + "; " + ex); Console.Out.Flush(); throw; }
    }

    // This is an explicit handler seam, not evidence that Rhino's real idle
    // event is an end-of-frame notification.
}
