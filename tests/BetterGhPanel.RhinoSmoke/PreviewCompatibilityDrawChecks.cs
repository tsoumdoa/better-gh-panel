using static PanelTestAdapter;
using static SmokeAssertions;
using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;


internal static partial class PreviewSafetyChecks
{
    internal static async Task RunCompatibilityDraw(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified; var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Compatibility preview drawing", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        using var first = LongNurbs(-6); using var second = LongNurbs(-2);
        using var third = LongPolyline(2); using var fourth = LongPolyline(6);
        using var rebuiltMaterial = new Rhino.Display.DisplayMaterial(Color.Cyan);
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        using var conduit = new PreviewConduit(component, document);
        Rhino.Display.RhinoViewport? capturedViewport = null;
        try
        {
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-10, -9, -1), new Point3d(10, 9, 1)));
            document.Properties.KeepOpen = true;
            document.AddObject(component, false);
            var highlight = FieldValue<object>(component, "_highlight");
            var nurbsGroup = new GH_GeometryGroup(); nurbsGroup.Objects.Add(new GH_Curve { Value = second });
            var polyGroup = new GH_GeometryGroup(); polyGroup.Objects.Add(new GH_Curve { Value = fourth });
            var source = new IGH_Goo?[] { new GH_Curve { Value = first }, nurbsGroup, new GH_Curve { Value = third }, polyGroup };
            var snapshot = new PanelSnapshot(Enumerable.Range(0, 4).Select(i => new PanelRow(i, "{0}", "Curve", "curve", null)).ToArray(),
                new[] { new PanelBranch("{0}", 4) });
            var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
            Scope(highlight, source, result, new[] { 0, 2 });
            var oldBounds = Box(highlight);
            bool initial = Count(highlight) == 4 && PropertyValue<int>(highlight, "Count") == 2;
            bool changed = false, oldCoherent = false, rebuilt = false;
            string? error = null;
            conduit.OnDraw = args =>
            {
                try
                {
                    // DrawToBitmap uses its own projection. Pixel regions must
                    // use the viewport from this draw, not the restored live view.
                    capturedViewport = new Rhino.Display.RhinoViewport(args.Viewport);
                    changed = first.Transform(Transform.Translation(0, 1, 0)) && second.Transform(Transform.Translation(0, 1, 0)) &&
                        third.Transform(Transform.Translation(0, 1, 0)) && fourth.Transform(Transform.Translation(0, 1, 0));
                    oldCoherent = changed && Box(highlight).Equals(oldBounds) && Count(highlight) == 4;
                    component.DrawViewportWires(args);
                    Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());
                    Scope(highlight, source, result, new[] { 0, 2 });
                    component.ExpirePreview(false);
                    rebuilt = Count(highlight) == 4 && PropertyValue<int>(highlight, "Count") == 2 && Box(highlight).Min.Y > oldBounds.Min.Y &&
                        PropertyValue<long>(highlight, "RetainedBytes") <= 64L * 1024 * 1024;
                    component.DrawViewportWires(new ColoredPreviewArgs(args, rebuiltMaterial, Color.Cyan));
                }
                catch (Exception ex) { error = ex.ToString(); }
            };
            conduit.Enabled = true;
            using var bitmap = CapturePreview(view, highlight, "compatibility curves", conduit);
            Check(error == null && initial && oldCoherent && rebuilt,
                "selected and filtered compatibility curves retain old bounds until source expiration and rebuild: " + error);
            double[] ys = { -6, -2, 2, 6 };
            for (int i = 0; i < ys.Length; i++)
            {
                var old = new BoundingBox(new Point3d(-8, ys[i] - 0.2, 0), new Point3d(8, ys[i] + 0.3, 0));
                var current = new BoundingBox(new Point3d(-8, ys[i] + 0.8, 0), new Point3d(8, ys[i] + 1.3, 0));
                Check(RegionHasColor(bitmap, capturedViewport!, old, i % 2 == 0 ? Color.Lime : Color.Red) &&
                    RegionHasColor(bitmap, capturedViewport!, current, Color.Cyan),
                    "compatibility curve " + i + " actually draws with selected/filtered colors before and after rebuild");
            }
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            await WaitForPreviewRetirement(highlight);
            Check(PropertyValue<long>(highlight, "RetainedBytes") == 0, "compatibility preview snapshots are released after drawing");
        }
        finally
        {
            capturedViewport?.Dispose();
            conduit.Enabled = false; document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
    }

    private static NurbsCurve LongNurbs(double y)
    {
        var curve = new NurbsCurve(3, false, 2, 1025);
        for (int i = 0; i < curve.Knots.Count; i++) curve.Knots[i] = i;
        for (int i = 0; i < curve.Points.Count; i++) curve.Points.SetPoint(i, new Point3d(-8 + 16.0 * i / 1024, y + 0.1 * (i % 2), 0));
        return curve;
    }
    private static PolylineCurve LongPolyline(double y) => new PolylineCurve(Enumerable.Range(0, 2049)
        .Select(i => new Point3d(-8 + 16.0 * i / 2048, y + 0.1 * (i % 2), 0)));

}
