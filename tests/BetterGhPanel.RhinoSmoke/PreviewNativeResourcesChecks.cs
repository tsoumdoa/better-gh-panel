using static PanelTestAdapter;
using static SmokeAssertions;
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
    internal static async Task RunNativeResources(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified;
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Preview resources fixture", Rhino.Display.DefinedViewportProjection.Top, new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        try
        {
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            document.Properties.KeepOpen = true; document.PreviewColourSelected = Color.FromArgb(255, 0, 230, 40);
            var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            component.CreateAttributes(); document.AddObject(component, false);
            var highlight = FieldValue<object>(component, "_highlight");
            var wrappedPoint = new GH_ObjectWrapper(new Point3d(0, 0, 0));
            Set(highlight, new IGH_Goo?[] { wrappedPoint }, new[] { 0 }, new[] { 0 });
            wrappedPoint.Value = wrappedPoint; // Drawing must use the admitted snapshot, not the now-cyclic source.
            component.ExpirePreview(false);
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-4, -4, -1), new Point3d(4, 4, 1)));
            view.Redraw();
            using var pointBitmap = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
            int pointGreen = 0;
            for (int y = 0; y < pointBitmap.Height; y++) for (int x = 0; x < pointBitmap.Width; x++)
            { var c = pointBitmap.GetPixel(x, y); if (c.G > c.R + 40 && c.G > c.B + 40) pointGreen++; }
            Check(pointGreen > 0 && Box(highlight).Contains(Point3d.Origin),
                "an object-wrapped point draws from its admitted generation after its wrapper becomes cyclic");

            var box = new GH_Box(new Box(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1))));
            var boxGroup = new GH_GeometryGroup(); boxGroup.Objects.Add(box);
            Set(highlight, new IGH_Goo?[] { box, boxGroup }, new[] { 0, 1 }, new[] { 0 });
            long bytes = PropertyValue<long>(highlight, "RetainedBytes");
            foreach (var mode in new[] { GH_PreviewMesh.LowQuality, GH_PreviewMesh.HighQuality, GH_PreviewMesh.Custom })
            {
                if (mode == GH_PreviewMesh.Custom) document.PreviewCustomMeshParameters = new MeshingParameters(0.4);
                document.PreviewMeshType = mode;
                component.ExpirePreview(false);
                view.Redraw();
                using var meshBitmap = CapturePreview(view, highlight, "native boxes " + mode, null);
                Check(meshBitmap != null && Count(highlight) == 2 && PropertyValue<long>(highlight, "RetainedBytes") == bytes &&
                    Box(highlight).Contains(new Point3d(2, 2, 1)),
                    "mesh-setting changes retain bounded standalone and grouped box snapshots without regenerating caches");
            }
            using var nativeBrep = new Box(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1))).ToBrep();
            var brep = new GH_Brep { Value = nativeBrep };
            var brepGroup = new GH_GeometryGroup(); brepGroup.Objects.Add(brep);
            Set(highlight, new IGH_Goo?[] { brep, brepGroup }, new[] { 0, 1 }, new[] { 0 });
            var leafType = assembly.GetType("BetterGhPanel.Runtime.PanelPreviewLeaf", true)!;
            var live = leafType.GetProperty("LiveGeneratedMeshes", AnyStatic)!;
            var builds = leafType.GetProperty("GeneratedMeshBuilds", AnyStatic)!;
            var shaded = Rhino.Display.DisplayModeDescription.FindByName("Shaded");
            if (shaded != null) view.ActiveViewport.DisplayMode = shaded;
            document.PreviewMode = GH_PreviewMode.Shaded;
            foreach (var mode in new[] { GH_PreviewMesh.LowQuality, GH_PreviewMesh.HighQuality, GH_PreviewMesh.Custom })
            {
                if (mode == GH_PreviewMesh.Custom) document.PreviewCustomMeshParameters = new MeshingParameters(0.35);
                document.PreviewMeshType = mode;
                component.ExpirePreview(false);
                int before = (int)builds.GetValue(null)!;
                view.Redraw();
                using var bitmap = CapturePreview(view, highlight, "native Breps " + mode, null);
                Check(bitmap != null && Count(highlight) == 2 && (int)builds.GetValue(null)! > before &&
                    (int)live.GetValue(null)! > 0 && PropertyValue<long>(highlight, "RetainedBytes") <= 64L * 1024 * 1024,
                    "standalone and grouped Breps regenerate bounded shaded meshes after a meshing setting change");
                int cachedBuilds = (int)builds.GetValue(null)!;
                int cachedMeshes = (int)live.GetValue(null)!;
                view.Redraw();
                using var repeat = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
                Check(repeat != null && (int)builds.GetValue(null)! == cachedBuilds && (int)live.GetValue(null)! == cachedMeshes,
                    "unchanged meshing settings reuse owned meshes instead of regenerating them every frame");
            }
            var oldBounds = Box(highlight);
            Check(nativeBrep.Transform(Transform.Translation(30, 0, 0)), "the live Brep mutates after preview admission");
            Check(Box(highlight).Equals(oldBounds), "standalone and grouped Brep bounds remain on the old generation without expiration");
            component.ExpirePreview(false); view.Redraw();
            using (var oldImage = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480))
                Check(oldImage != null && GreenPixels(oldImage) > 0 && Count(highlight) == 2 && Box(highlight).Equals(oldBounds),
                    "the old Brep generation still draws with its original bounds after source mutation");
            Set(highlight, new IGH_Goo?[] { brep, brepGroup }, new[] { 0, 1 }, new[] { 0 });
            Check(Box(highlight).Min.X > oldBounds.Max.X, "a rebuilt Brep generation uses the new source geometry and bounds");
            view.ActiveViewport.ZoomBoundingBox(Box(highlight));
            component.ExpirePreview(false); view.Redraw();
            using (var newImage = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480))
                Check(newImage != null && GreenPixels(newImage) > 0 && Count(highlight) == 2,
                    "the rebuilt Brep generation draws after expiration");
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            Check(Count(highlight) == 0 && !Box(highlight).IsValid, "clearing a preview immediately detaches its geometry and bounds");
            await WaitForPreviewRetirement(highlight);
            Check((int)live.GetValue(null)! == 0 && PropertyValue<long>(highlight, "RetainedBytes") == 0,
                "clearing a preview eventually disposes every generated Brep mesh and releases its accounting");
            component.DocumentContextChanged(document, GH_DocumentContext.Close);
        }
        finally
        {
            document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
    }

}
