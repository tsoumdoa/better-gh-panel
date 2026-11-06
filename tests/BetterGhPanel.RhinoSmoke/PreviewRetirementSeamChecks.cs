using static PanelTestAdapter;
using static SmokeAssertions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;
using Forms = System.Windows.Forms;


internal static partial class PreviewSafetyChecks
{
    internal static Task RunRetirementSeam(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified;
        bool meshEdges = Grasshopper.CentralSettings.PreviewMeshEdges;
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Preview retirement seam", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        using var brep = new Box(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1))).ToBrep();
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        using var conduit = new PreviewConduit(component, document);
        Rhino.Display.RhinoViewport? capturedViewport = null;
        try
        {
            Grasshopper.CentralSettings.PreviewMeshEdges = true;
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-4, -4, -2), new Point3d(4, 4, 2)));
            document.Properties.KeepOpen = true; document.PreviewMode = GH_PreviewMode.Shaded;
            document.AddObject(component, false);
            object highlight = ReflectField2106128985<object>(component, "_highlight");
            var leafType = assembly.GetType("BetterGhPanel.Runtime.PanelPreviewLeaf", true)!;
            var live = leafType.GetProperty("LiveGeneratedMeshes", FlagsNonPublicPublicStatic)!;
            var holder = new GH_Brep { Value = brep };
            var group = new GH_GeometryGroup(); group.Objects.Add(holder);
            GeometryBase? wireOwned = null, meshOwned = null, resetOwned = null;
            Mesh[] resetMeshes = Array.Empty<Mesh>();
            bool wireCalled = false, wireGuarded = false, wireFrameHeld = false;
            bool meshCalled = false, meshGuarded = false, meshFrameHeld = false, resetHeld = false;
            bool postCallbackChecks = false;
            long heldBytes = 0;
            string? error = null;
            conduit.OnDraw = args =>
            {
                try
                {
                    capturedViewport = new Rhino.Display.RhinoViewport(args.Viewport);
                    VerifyPostCallbackRetirement(component, highlight, args);
                    VerifyRejectedMeshing(component, highlight, args);
                    VerifyFailedDisplayFlush(component, highlight, args);
                    postCallbackChecks = true;
                    var customWire = new StandalonePointPreview();
                    Set(highlight, new IGH_Goo?[] { group, customWire }, new[] { 0, 1 }, new[] { 0, 1 });
                    wireOwned = OwnedGeometry(highlight).OfType<Brep>().Single();
                    customWire.OnWire = () =>
                    {
                        wireCalled = true;
                        Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());
                        int calls = ReflectProperty403875335<int>(highlight, "DrawWireCalls");
                        Method(highlight, "ReleaseOnIdle").Invoke(highlight, new object?[] { null, EventArgs.Empty });
                        component.DrawViewportWires(args);
                        wireGuarded = !wireOwned.Disposed && ReflectProperty403875335<bool>(highlight, "RetirementPending") &&
                            ReflectProperty403875335<int>(highlight, "DrawWireCalls") == calls;
                    };
                    component.DrawViewportWires(args);
                    wireFrameHeld = !wireOwned.Disposed;

                    Set(highlight, new IGH_Goo?[] { group }, new[] { 0 }, new[] { 0 });
                    resetOwned = OwnedGeometry(highlight).OfType<Brep>().Single();
                    document.PreviewMeshType = GH_PreviewMesh.LowQuality;
                    component.DrawViewportMeshes(args);
                    var firstEntry = ((IEnumerable)ReflectField2106128985<object>(highlight, "_entries")).Cast<object>().Single();
                    object firstValue = firstEntry.GetType().GetProperty("Value")!.GetValue(firstEntry)!;
                    object firstLeaf = ((IEnumerable)ReflectField2106128985<object>(firstValue, "Leaves")).Cast<object>().Single();
                    resetMeshes = ReflectField2106128985<Mesh[]?>(firstLeaf, "_generatedMeshes") ?? Array.Empty<Mesh>();
                    document.PreviewMeshType = GH_PreviewMesh.HighQuality;
                    component.DrawViewportMeshes(args);
                    resetHeld = resetMeshes.Length > 0 && resetMeshes.All(mesh => !mesh.Disposed) &&
                        !resetOwned.Disposed && ReflectProperty403875335<bool>(highlight, "RetirementPending");

                    var customMesh = new StandalonePointPreview();
                    Set(highlight, new IGH_Goo?[] { group, customMesh }, new[] { 0, 1 }, new[] { 0, 1 });
                    meshOwned = OwnedGeometry(highlight).OfType<Brep>().Single();
                    customMesh.OnMesh = () =>
                    {
                        meshCalled = true;
                        int calls = ReflectProperty403875335<int>(highlight, "DrawMeshCalls");
                        component.DocumentContextChanged(document, GH_DocumentContext.Close);
                        Method(highlight, "ReleaseOnIdle").Invoke(highlight, new object?[] { null, EventArgs.Empty });
                        component.DrawViewportMeshes(args);
                        heldBytes = ReflectProperty403875335<long>(highlight, "RetainedBytes");
                        meshGuarded = !wireOwned.Disposed && !resetOwned.Disposed && !meshOwned.Disposed &&
                            resetMeshes.All(mesh => !mesh.Disposed) && (int)live.GetValue(null)! > 0 &&
                            ReflectProperty403875335<bool>(highlight, "RetirementPending") && ReflectProperty403875335<int>(highlight, "DrawMeshCalls") == calls &&
                            heldBytes > 0 && heldBytes <= 16L * 1024 * 1024;
                    };
                    component.DrawViewportMeshes(args);
                    meshFrameHeld = !wireOwned.Disposed && !resetOwned.Disposed && !meshOwned.Disposed &&
                        resetMeshes.All(mesh => !mesh.Disposed);
                }
                catch (Exception ex) { error = ex.ToString(); }
            };
            conduit.Enabled = true;
            using var bitmap = CapturePreview(view, highlight, "retirement seam", conduit);
            Check(error == null && bitmap != null && GreenPixels(bitmap) > 0,
                "retirement seam executes an actual shaded draw: " + error);
            Check(postCallbackChecks, "post-component retirement, negative meshing cache and failed-flush regressions execute inside the enclosing display callback");
            int regionIndex = 0;
            foreach (var region in ConsumptionRegions())
                Check(RegionColors(bitmap!, capturedViewport!, region, Color.Lime, Color.Cyan),
                    "consumption region " + regionIndex++ + " retains shading and wires after in-callback retirement and native capture return");
            Check(wireCalled && wireGuarded && wireFrameHeld && wireOwned != null && !wireOwned.Disposed,
                "simulated idle and nested wire draw cannot release a cleared generation during its active callback");
            Check(resetHeld && meshCalled && meshGuarded && meshFrameHeld && meshOwned != null && !meshOwned.Disposed,
                "mesh reset and document close retain drawn geometry through callback and capture return, including simulated reentrant idle");
            int releases = ReflectProperty403875335<int>(highlight, "IdleReleases");
            Method(highlight, "ReleaseOnIdle").Invoke(highlight, new object?[] { null, EventArgs.Empty });
            Check(wireOwned!.Disposed && resetOwned!.Disposed && meshOwned!.Disposed && resetMeshes.All(mesh => mesh.Disposed) &&
                (int)live.GetValue(null)! == 0 && ReflectProperty403875335<long>(highlight, "RetainedBytes") == 0 &&
                !ReflectProperty403875335<bool>(highlight, "RetirementPending") && ReflectProperty403875335<int>(highlight, "IdleReleases") == releases + 1,
                "the explicit post-capture retirement seam releases all retired resources and clears its pending subscription");
            Method(highlight, "ReleaseOnIdle").Invoke(highlight, new object?[] { null, EventArgs.Empty });
            Check((int)live.GetValue(null)! == 0 && ReflectProperty403875335<long>(highlight, "RetainedBytes") == 0,
                "repeated retirement does not dispose or account for the same resources twice");
        }
        finally
        {
            capturedViewport?.Dispose();
            conduit.Enabled = false;
            Grasshopper.CentralSettings.PreviewMeshEdges = meshEdges;
            document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
        return Task.CompletedTask;
    }

}
