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
    internal static Task RunAdditionalGeometryDraw(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Additional geometry drawing needs a Rhino document.");
        bool modified = rhino.Modified; var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Additional geometry preview", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        using var cloud = new PointCloud(); cloud.Add(new Point3d(-15, 0, 0)); cloud.Add(new Point3d(-14.5, 0.5, 0));
        using var groupedCloud = new PointCloud(); groupedCloud.Add(new Point3d(-10, 0, 0)); groupedCloud.Add(new Point3d(-9.5, 0.5, 0));
        using var coloredCloud = new PointCloud();
        coloredCloud.Add(new Point3d(20, 0, 0), Color.Cyan); coloredCloud.Add(new Point3d(23, 0, 0), Color.Cyan);
        coloredCloud[1].Hidden = true;
        using var extrusion = HollowPreviewExtrusion(new Point3d(-5, -1, 0));
        using var groupExtrusion = HollowPreviewExtrusion(new Point3d(0, -1, 0));
        using var normalExtrusion = HollowPreviewExtrusion(new Point3d(-5, 5, 0));
        using var selectedGroupExtrusion = HollowPreviewExtrusion(new Point3d(0, 5, 0));
        var cloudGroup = new GH_GeometryGroup(); cloudGroup.Objects.Add(new GH_PointCloud { Value = groupedCloud });
        var extrusionGroup = new GH_GeometryGroup(); extrusionGroup.Objects.Add(new GH_Extrusion { Value = groupExtrusion });
        var selectedExtrusionGroup = new GH_GeometryGroup(); selectedExtrusionGroup.Objects.Add(new GH_Extrusion { Value = selectedGroupExtrusion });
        var bbox = new BoundingBox(new Point3d(5, -1, 0), new Point3d(7, 1, 2));
        var sphere = new Sphere(new Plane(new Point3d(10, 0, 0), Vector3d.ZAxis), 1);
        var polyline = new Polyline(new[] { new Point3d(14, -1, 0), new Point3d(15, 1, 0), new Point3d(16, -1, 0) });
        var source = new IGH_Goo?[] { new GH_PointCloud { Value = cloud }, cloudGroup, new GH_Extrusion { Value = extrusion },
            extrusionGroup, new GH_ObjectWrapper(bbox), new GH_ObjectWrapper(sphere), new GH_ObjectWrapper(polyline),
            new GH_PointCloud { Value = coloredCloud }, new GH_Extrusion { Value = normalExtrusion }, selectedExtrusionGroup };
        var snapshot = new PanelSnapshot(Enumerable.Range(0, source.Length).Select(i => new PanelRow(i, "{0}", "Geometry", "geometry", null)).ToArray(),
            new[] { new PanelBranch("{0}", source.Length) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        using var conduit = new PreviewConduit(component, document, Color.Blue, Color.Yellow);
        try
        {
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-18, -4, -1), new Point3d(26, 11, 3)));
            var shaded = Rhino.Display.DisplayModeDescription.FindByName("Shaded");
            if (shaded != null) view.ActiveViewport.DisplayMode = shaded;
            document.Properties.KeepOpen = true; document.PreviewMode = GH_PreviewMode.Shaded; document.AddObject(component, false);
            object highlight = Field<object>(component, "_highlight");
            Scope(highlight, source, result, new[] { 0, 2, 4, 6, 7, 9 });
            component.ExpirePreview(false);
            Method(document, "CreatePreviewCaches").Invoke(document, Array.Empty<object>());
            var bounds = Box(highlight); var hostBounds = document.PreviewBoundingBox;
            Check(Count(highlight) == 10 && Property<int>(highlight, "Count") == 6 && hostBounds.Contains(bounds.Min) && hostBounds.Contains(bounds.Max),
                "all additional standalone and group previews enter coherent component/document bounds");
            bool called = false;
            conduit.OnDraw = args => { called = true; component.DrawViewportMeshes(args); component.DrawViewportWires(args); };
            conduit.Enabled = true;
            using var bitmap = CapturePreview(view, highlight, "additional geometry", conduit);
            Check(called && Property<int>(highlight, "DrawWireCalls") > 0 && Property<int>(highlight, "DrawMeshCalls") > 0,
                "an actual native capture executes additional geometry wire and shaded callbacks");
            var regions = new[] {
                new BoundingBox(new Point3d(-15.5,-1,0),new Point3d(-14,1,0)),
                new BoundingBox(new Point3d(-10.5,-1,0),new Point3d(-9,1,0)),
                extrusion.GetBoundingBox(false), groupExtrusion.GetBoundingBox(false), bbox, sphere.BoundingBox,
                new BoundingBox(new Point3d(14,-1,0),new Point3d(16,1,0)) };
            for (int i = 0; i < regions.Length; i++)
                Check(RegionHasColor(bitmap, view.ActiveViewport, regions[i], i % 2 == 0 ? Color.Lime : Color.Red),
                    "additional preview " + i + " produces actual selected or filtered viewport pixels");
            var extrusions = new[] { extrusion, groupExtrusion, normalExtrusion, selectedGroupExtrusion };
            for (int i = 0; i < extrusions.Length; i++)
            {
                bool selected = i == 0 || i == 3;
                Color shade = selected ? Color.Yellow : Color.Blue;
                Color wire = selected ? Color.Lime : Color.Red;
                var ext = extrusions[i]; var box = ext.GetBoundingBox(false);
                Check(ext.IsValid && ext.IsCappedAtBottom && ext.IsCappedAtTop && ext.ProfileCount == 2 &&
                    RegionColors(bitmap, view.ActiveViewport, box, shade, wire),
                    "extrusion " + i + " has distinct shaded and wire pixels for selected/normal standalone/group geometry");
                var cap = new BoundingBox(new Point3d(box.Min.X + 0.2, box.Min.Y + 0.2, box.Max.Z),
                    new Point3d(box.Min.X + 0.8, box.Min.Y + 2.8, box.Max.Z));
                var hole = new BoundingBox(new Point3d(box.Min.X + 1.2, box.Min.Y + 1.2, box.Max.Z),
                    new Point3d(box.Min.X + 1.8, box.Min.Y + 1.8, box.Max.Z));
                Check(InteriorShadePixels(bitmap, view.ActiveViewport, cap, shade) > 10 &&
                    InteriorShadePixels(bitmap, view.ActiveViewport, hole, shade) == 0,
                    "extrusion " + i + " shades its cap interior while preserving its inner-profile hole");
            }
            Check(RegionHasColor(bitmap, view.ActiveViewport, new BoundingBox(new Point3d(19.5, -0.5, 0), new Point3d(20.5, 0.5, 0)), Color.Cyan) &&
                !RegionHasColor(bitmap, view.ActiveViewport, new BoundingBox(new Point3d(22.5, -0.5, 0), new Point3d(23.5, 0.5, 0)), Color.Cyan) &&
                coloredCloud[1].Hidden,
                "selected colored clouds retain source colors and omit hidden-point pixels without changing the source");
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            Method(highlight, "ReleaseOnIdle").Invoke(highlight, new object?[] { null, EventArgs.Empty });
            Check(Property<long>(highlight, "RetainedBytes") == 0, "explicit post-capture cleanup releases additional preview resources");
        }
        finally
        {
            conduit.Enabled = false; document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
        return Task.CompletedTask;
    }

    private static Extrusion HollowPreviewExtrusion(Point3d origin)
    {
        using var outer = new Rectangle3d(Plane.WorldXY, 3, 3).ToNurbsCurve();
        using var inner = new Rectangle3d(Plane.WorldXY, new Interval(1, 2), new Interval(1, 2)).ToNurbsCurve();
        inner.Reverse();
        var extrusion = Extrusion.Create(outer, 2, true) ?? throw new InvalidOperationException("Extrusion drawing fixture failed.");
        if (!extrusion.AddInnerProfile(inner) || !extrusion.Translate((Vector3d)origin))
        { extrusion.Dispose(); throw new InvalidOperationException("Extrusion inner-profile fixture failed."); }
        return extrusion;
    }

    private static int InteriorShadePixels(Bitmap bitmap, Rhino.Display.RhinoViewport viewport, BoundingBox region, Color shade)
    {
        var p = viewport.WorldToClient(region.Min); var q = viewport.WorldToClient(region.Max);
        double sx = bitmap.Width / (double)Math.Max(1, viewport.Size.Width), sy = bitmap.Height / (double)Math.Max(1, viewport.Size.Height);
        int x0 = Math.Max(0, (int)Math.Ceiling(Math.Min(p.X, q.X) * sx)), x1 = Math.Min(bitmap.Width, (int)Math.Floor(Math.Max(p.X, q.X) * sx));
        int y0 = Math.Max(0, (int)Math.Ceiling(Math.Min(p.Y, q.Y) * sy)), y1 = Math.Min(bitmap.Height, (int)Math.Floor(Math.Max(p.Y, q.Y) * sy));
        int count = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
        {
            var color = bitmap.GetPixel(x, y);
            if (shade == Color.Blue ? color.B > color.R + 15 && color.B > color.G + 15 : color.R > color.B + 15 && color.G > color.B + 15) count++;
        }
        Console.WriteLine($"Extrusion interior {region}: {shade.Name}={count}, pixels={x0},{y0}-{x1},{y1}");
        return count;
    }

}
