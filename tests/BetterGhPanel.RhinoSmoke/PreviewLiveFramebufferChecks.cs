using System;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;

internal static partial class PreviewSafetyChecks
{
    // This diagnostic forces a test-side flush before reading pixels. It does
    // not replace the required offscreen fixtures or prove a safe retirement
    // boundary for native display resources.
    private static async Task<Bitmap> CaptureLiveFramebuffer(Rhino.Display.RhinoView view, PreviewConduit conduit,
        GH_Component component)
    {
        var completion = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        conduit.OnDraw = args =>
        {
            try
            {
                if (args.Viewport.Id != view.ActiveViewport.Id) throw new InvalidOperationException("Unexpected viewport in live capture.");
                component.DrawViewportMeshes(args);
                component.DrawViewportWires(args);
                args.Display.Flush();
                completion.TrySetResult(LiveFramebufferCapture.Capture());
            }
            catch (Exception error) { completion.TrySetException(error); }
        };
        view.Redraw();
        if (await Task.WhenAny(completion.Task, Task.Delay(5000)) != completion.Task)
        {
            conduit.OnDraw = null;
            throw new TimeoutException("No viewport callback occurred during live readback.");
        }
        return await completion.Task;
    }

    internal static async Task RunLiveFramebufferDiagnostic(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified;
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Live preview framebuffer diagnostic", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document();
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes(); component.Attributes.Selected = true;
        using var conduit = new PreviewConduit(component, document);
        try
        {
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-4, -4, -1), new Point3d(4, 4, 1)));
            var highlight = Field<object>(component, "_highlight");
            var wrapper = new GH_ObjectWrapper(new Point3d(0, 0, 0));
            Set(highlight, new IGH_Goo?[] { wrapper }, new[] { 0 }, new[] { 0 });
            wrapper.Value = wrapper;
            conduit.Enabled = true;
            using (var bitmap = await CaptureLiveFramebuffer(view, conduit, component))
                Check(GreenPixels(bitmap) > 0 && Box(highlight).Contains(Point3d.Origin),
                    "a cyclic wrapper's admitted point draws in a live Rhino frame");

            var box = new GH_Box(new Box(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1))));
            var group = new GH_GeometryGroup(); group.Objects.Add(box);
            Set(highlight, new IGH_Goo?[] { box, group }, new[] { 0, 1 }, new[] { 0 });
            await WaitForPreviewRetirement(highlight);
            long bytes = Property<long>(highlight, "RetainedBytes");
            foreach (var mode in new[] { GH_PreviewMesh.LowQuality, GH_PreviewMesh.HighQuality, GH_PreviewMesh.Custom })
            {
                if (mode == GH_PreviewMesh.Custom) document.PreviewCustomMeshParameters = new MeshingParameters(0.4);
                document.PreviewMeshType = mode;
                int before = conduit.Draws;
                using var bitmap = await CaptureLiveFramebuffer(view, conduit, component);
                Check(GreenPixels(bitmap) > 0 && conduit.Draws > before && Count(highlight) == 2 &&
                    Property<long>(highlight, "RetainedBytes") == bytes && Box(highlight).Contains(new Point3d(2, 2, 1)),
                    "live wire and shaded callbacks retain bounded standalone and grouped boxes across mesh settings");
                await Task.Delay(25);
            }
            using var nativeBrep = new Box(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1))).ToBrep();
            var brep = new GH_Brep { Value = nativeBrep };
            var brepGroup = new GH_GeometryGroup(); brepGroup.Objects.Add(brep);
            Set(highlight, new IGH_Goo?[] { brep, brepGroup }, new[] { 0, 1 }, new[] { 0 });
            await WaitForPreviewRetirement(highlight);
            var leafType = assembly.GetType("BetterGhPanel.Runtime.PanelPreviewLeaf", true)!;
            var live = leafType.GetProperty("LiveGeneratedMeshes", Static)!;
            var builds = leafType.GetProperty("GeneratedMeshBuilds", Static)!;
            foreach (var mode in new[] { GH_PreviewMesh.LowQuality, GH_PreviewMesh.HighQuality, GH_PreviewMesh.Custom })
            {
                if (mode == GH_PreviewMesh.Custom) document.PreviewCustomMeshParameters = new MeshingParameters(0.35);
                document.PreviewMeshType = mode;
                int before = (int)builds.GetValue(null)!;
                using (var bitmap = await CaptureLiveFramebuffer(view, conduit, component))
                    Check(GreenPixels(bitmap) > 0 && Count(highlight) == 2 && (int)builds.GetValue(null)! > before &&
                        (int)live.GetValue(null)! > 0 && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
                        "live standalone and grouped Breps draw with bounded meshes after each mesh-setting change");
                int cachedBuilds = (int)builds.GetValue(null)!;
                int cachedMeshes = (int)live.GetValue(null)!;
                using (var bitmap = await CaptureLiveFramebuffer(view, conduit, component))
                    Check(GreenPixels(bitmap) > 0 && (int)builds.GetValue(null)! == cachedBuilds && (int)live.GetValue(null)! == cachedMeshes,
                        "a later live frame reuses Brep meshes when mesh settings are unchanged");
            }
            var oldBounds = Box(highlight);
            Check(nativeBrep.Transform(Transform.Translation(30, 0, 0)) && Box(highlight).Equals(oldBounds),
                "live Brep bounds remain on the admitted generation after source mutation");
            using (var bitmap = await CaptureLiveFramebuffer(view, conduit, component))
                Check(GreenPixels(bitmap) > 0 && Count(highlight) == 2 && Box(highlight).Equals(oldBounds),
                    "a later live frame draws the old Brep generation before source expiration");
            Set(highlight, new IGH_Goo?[] { brep, brepGroup }, new[] { 0, 1 }, new[] { 0 });
            Check(Box(highlight).Min.X > oldBounds.Max.X, "rebuilding moves the live Brep bounds to the changed source");
            view.ActiveViewport.ZoomBoundingBox(Box(highlight));
            using (var bitmap = await CaptureLiveFramebuffer(view, conduit, component))
                Check(GreenPixels(bitmap) > 0 && Count(highlight) == 2,
                    "a later live frame draws the rebuilt Brep generation after expiration");
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            await WaitForPreviewRetirement(highlight);
            Check(Property<long>(highlight, "RetainedBytes") == 0 && (int)live.GetValue(null)! == 0,
                "live preview resources retire after the observed frames and idle");
        }
        finally
        {
            conduit.Enabled = false;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
    }
}
