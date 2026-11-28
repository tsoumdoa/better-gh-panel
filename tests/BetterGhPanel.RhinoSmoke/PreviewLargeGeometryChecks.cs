using static PanelTestAdapter;
using static SmokeAssertions;
using System;
using System.Collections;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

// The shipped preview limits, the shared budget, and the depth bias for coloured
// fills. Every other fixture runs with the legacy per-panel limits.
internal static partial class PreviewSafetyChecks
{
    internal static async Task RunLargeGeometry(Assembly assembly)
    {
        PreviewLimits.UseDefaults(assembly);
        var rhino = RhinoDoc.ActiveDoc;
        bool previousModified = rhino.Modified;
        try
        {
            VerifyLargeAdmission(assembly);
            await VerifySharedBudget(assembly);
            await VerifyColouredFillDepth(assembly, rhino);
            await VerifyMatchLegendWithoutPalette(assembly, rhino);
        }
        finally { PreviewLimits.UseDefaults(assembly); rhino.Modified = previousModified; }
    }

    private static object NewHighlight(Assembly assembly) =>
        Activator.CreateInstance(assembly.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!, true)!;

    private static Brep GridBrep(double size, int count)
    {
        using var grid = Mesh.CreateFromPlane(Plane.WorldXY, new Interval(0, size), new Interval(0, size), count, count);
        return Brep.CreateFromMesh(grid, false) ?? throw new InvalidOperationException("Grid Brep fixture failed");
    }

    private static void VerifyLargeAdmission(Assembly assembly)
    {
        object highlight = NewHighlight(assembly);
        try
        {
            using var brep = GridBrep(20, 20);
            Check(brep.Faces.Count == 400 && brep.Trims.Count > 256, "the Brep fixture exceeds the old 256-item collection limit");
            Set(highlight, new IGH_Goo?[] { new GH_Brep(brep) }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 1 && string.IsNullOrEmpty(Status(highlight)), "a 400-face Brep is admitted for preview: " + Status(highlight));
            var leaf = PreviewLeaves(highlight).Single();
            Method(leaf, "PrepareMeshes").Invoke(leaf, new object[] { MeshingParameters.Default, (Func<bool>)(() => true) });
            Check(FieldValue<Mesh[]?>(leaf, "_generatedMeshes")?.Length > 0, "the 400-face Brep gets a shaded display mesh");

            using var dense = Mesh.CreateFromPlane(Plane.WorldXY, new Interval(0, 50), new Interval(0, 50), 150, 150);
            Check(dense.Faces.Count == 22500, "the mesh fixture exceeds the old 8,192-face limit");
            Set(highlight, new IGH_Goo?[] { new GH_Mesh(dense) }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 1 && string.IsNullOrEmpty(Status(highlight)), "a 22,500-face mesh is admitted for preview: " + Status(highlight));

            // A 1 km sphere at a 1 mm tolerance is 1.7 million tolerances across.
            using var sphere = new Sphere(Point3d.Origin, 500).ToBrep();
            Set(highlight, new IGH_Goo?[] { new GH_Brep(sphere) }, new[] { 0 }, new[] { 0 });
            leaf = PreviewLeaves(highlight).Single();
            using var fine = new MeshingParameters(MeshingParameters.Default) { Tolerance = .001 };
            Method(leaf, "PrepareMeshes").Invoke(leaf, new object[] { fine, (Func<bool>)(() => true) });
            Check(FieldValue<Mesh[]?>(leaf, "_generatedMeshes")?.Length > 0 && string.IsNullOrEmpty(Status(highlight)),
                "an over-fine tolerance on very large geometry is coarsened instead of dropping the shaded preview: " + Status(highlight));

            var huge = new GH_GeometryGroup { Name = "many" };
            for (int i = 0; i < 70_000; i++) huge.Objects.Add(new GH_Point(new Point3d(i, 0, 0)));
            Set(highlight, new IGH_Goo?[] { huge }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 0 && PropertyValue<int>(highlight, "LastVisitedNodes") <= 262_144 && Status(highlight).Contains("partial"),
                "a group beyond the per-root leaf limit is unavailable rather than partially dispatched");
        }
        finally { Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>()); }
    }

    private static async Task VerifySharedBudget(Assembly assembly)
    {
        object first = NewHighlight(assembly), second = NewHighlight(assembly);
        try
        {
            using var brep = GridBrep(10, 10);
            Set(first, new IGH_Goo?[] { new GH_Brep(brep) }, new[] { 0 }, new[] { 0 });
            long used = PropertyValue<long>(first, "RetainedBytes");
            Check(Count(first) == 1 && used > 0, "the first panel holds a preview");
            PreviewLimits.SetRetainedBytes(assembly, used + 1024);
            var values = new IGH_Goo?[] { new GH_Brep(brep) };
            var snapshot = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Brep", "brep", null) }, new[] { new PanelBranch("{0}", 1) });
            Scope(second, values, PanelQueryEngine.Execute(snapshot, new PanelQueryOptions()), Array.Empty<int>());
            Check(Count(second) == 0 && Status(second).Contains("partial"),
                "a second panel cannot exceed the budget another panel already uses: " + Status(second));
            Method(first, "Clear").Invoke(first, Array.Empty<object>());
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Count(second) == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);
            Check(Count(second) == 1, "the waiting panel admits its preview on its own once the other panel frees the budget: " + Status(second));
        }
        finally
        {
            PreviewLimits.UseDefaults(assembly);
            Method(first, "Clear").Invoke(first, Array.Empty<object>());
            Method(second, "Clear").Invoke(second, Array.Empty<object>());
        }
    }

    private static Color PixelAt(Bitmap bitmap, RhinoViewport viewport, Point3d point)
    {
        var client = viewport.WorldToClient(point);
        var size = viewport.Size;
        int x = (int)(client.X * bitmap.Width / Math.Max(1.0, size.Width)), y = (int)(client.Y * bitmap.Height / Math.Max(1.0, size.Height));
        return bitmap.GetPixel(Math.Max(0, Math.Min(bitmap.Width - 1, x)), Math.Max(0, Math.Min(bitmap.Height - 1, y)));
    }

    private static async Task VerifyColouredFillDepth(Assembly assembly, RhinoDoc rhino)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        var component = fixture.Component;
        fixture.Document.PreviewMode = GH_PreviewMode.Shaded;
        fixture.Source.Hidden = fixture.Paired.Hidden = true;
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        using var surface = new PlaneSurface(Plane.WorldXY, new Interval(-10, 10), new Interval(-10, 10));
        fixture.Source.PersistentData.Append(new GH_Surface(surface), new GH_Path(0));
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 1);

        // A red Rhino object 0.01 units in front of the coloured surface: the old
        // push (0.1% of the view height or camera distance, at least 0.024 here)
        // covered it and hid the surface's own dot. A blue Rhino object exactly
        // coplanar with the surface must still lose to the Panel+ colour.
        using var marker = Mesh.CreateFromPlane(new Plane(new Point3d(5, 5, .01), Vector3d.ZAxis), new Interval(-2, 2), new Interval(-2, 2), 1, 1);
        using var coplanar = Mesh.CreateFromPlane(new Plane(new Point3d(-5, -5, 0), Vector3d.ZAxis), new Interval(-2, 2), new Interval(-2, 2), 1, 1);
        var markerId = rhino.Objects.AddMesh(marker, new ObjectAttributes { ObjectColor = Color.Red, ColorSource = ObjectColorSource.ColorFromObject });
        var coplanarId = rhino.Objects.AddMesh(coplanar, new ObjectAttributes { ObjectColor = Color.Blue, ColorSource = ObjectColorSource.ColorFromObject });
        var previous = rhino.Views.ActiveView;
        var top = rhino.Views.Add("Panel+ coloured fill depth top", DefinedViewportProjection.Top, new Rectangle(0, 0, 1000, 720), false);
        var perspective = rhino.Views.Add("Panel+ coloured fill depth perspective", DefinedViewportProjection.Perspective, new Rectangle(0, 0, 1000, 720), false);
        try
        {
            var bounds = new BoundingBox(new Point3d(-12, -12, -1), new Point3d(12, 12, 1));
            using var conduit = new ValuePreviewConduit(component, fixture.Document) { Enabled = true, Bounds = bounds };
            PreviewMode(component, "FilteredResults");
            Call(component, "SetShowValues", true);
            Call(component, "SetValuePalette", PanelValuePalette.Viridis);
            Call(component, "HighlightRows", fixture.Result.Snapshot, Array.Empty<int>());
            var labels = FieldValue<object>(FieldValue<object>(component, "_highlight"), "_orderLabels");
            await fixture.Ready(() => FieldValue<IDictionary>(labels, "_labels").Count == 1 && FieldValue<Array>(labels, "_legend").Length == 1);
            var shaded = DisplayModeDescription.FindByName("Shaded");
            static bool Red(Color c) => c.R > c.G + 60 && c.R > c.B + 60;
            static bool Blue(Color c) => c.B > c.R + 60 && c.B > c.G + 60;
            // The last view is about 80 degrees off vertical, where a slope-scaled bias is largest.
            foreach (var (view, height, slope) in new[] { (top, 50.0, .3), (perspective, 50.0, .3), (perspective, 500.0, .3), (perspective, 8.0, 5.7) })
            {
                rhino.Views.ActiveView = view;
                var viewport = view.ActiveViewport;
                viewport.DisplayMode = shaded;
                viewport.SetCameraLocations(Point3d.Origin, new Point3d(0, -slope * height, height));
                if (viewport.IsParallelProjection) viewport.ZoomBoundingBox(bounds);
                string name = $"{(viewport.IsParallelProjection ? "Top" : "Perspective")} at {height}, slope {slope}";
                var visible = CaptureDotFrame(view, shaded, labels, true);
                Check(visible.Contains(0), $"a coloured surface keeps its own dot with Hide dots behind objects on ({name})");
                using var image = view.CaptureToBitmap(new Size(1000, 720), shaded);
                var front = PixelAt(image, viewport, new Point3d(5, 5, .01));
                Check(Red(front), $"a Rhino object 0.01 units in front of the coloured fill stays visible ({name}, pixel {front})");
                var tie = PixelAt(image, viewport, new Point3d(-5, -5, 0));
                Check(!Blue(tie) && !Red(tie), $"the coloured fill wins over a coplanar object ({name}, pixel {tie})");
            }
        }
        finally
        {
            rhino.Objects.Delete(markerId, true);
            rhino.Objects.Delete(coplanarId, true);
            if (previous != null) rhino.Views.ActiveView = previous;
            top.Close(); perspective.Close();
        }
    }

    // Match / No match with Show values on and Colour by value off used to index
    // the palette at -32 in the legend, every frame, inside the shared conduit.
    private static async Task VerifyMatchLegendWithoutPalette(Assembly assembly, RhinoDoc rhino)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        var component = fixture.Component;
        fixture.Document.PreviewMode = GH_PreviewMode.Shaded;
        fixture.Source.Hidden = fixture.Paired.Hidden = true;
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        var path = new GH_Path(0);
        fixture.Source.PersistentData.Append(new GH_Line(new Line(new Point3d(-6, 0, 0), new Point3d(-1, 0, 0))), path);
        fixture.Source.PersistentData.Append(new GH_Line(new Line(new Point3d(1, 0, 0), new Point3d(8, 0, 0))), path);
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 2);
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Panel+ match legend", DefinedViewportProjection.Top, new Rectangle(0, 0, 1000, 720), false);
        try
        {
            rhino.Views.ActiveView = view;
            var bounds = new BoundingBox(new Point3d(-8, -3, -1), new Point3d(10, 3, 1));
            using var conduit = new ValuePreviewConduit(component, fixture.Document) { Enabled = true, Bounds = bounds };
            var setStyle = Method(component, "SetNoMatchStyle");
            setStyle.Invoke(component, new[] { Enum.Parse(setStyle.GetParameters()[0].ParameterType, "Grey") });
            Call(component, "SetShowValues", true);
            Call(component, "SetValuePalette", PanelValuePalette.Off);
            Check(PreviewMode(component) == "PassFail", "the panel previews Match / No match");
            Call(component, "HighlightRows", fixture.Result.Snapshot, Array.Empty<int>());
            var labels = FieldValue<object>(FieldValue<object>(component, "_highlight"), "_orderLabels");
            await fixture.Ready(() => FieldValue<Array>(labels, "_legend").Length == 1);
            Check(PropertyValue<bool>(labels, "HasMatchLegend"), "the Match legend and a Length range are both present");
            var viewport = view.ActiveViewport;
            viewport.ZoomBoundingBox(bounds);
            using var probe = new LegendProbe(labels) { Enabled = true };
            using (view.CaptureToBitmap(new Size(1000, 720), DisplayModeDescription.FindByName("Shaded"))) { }
            Check(probe.Ran && probe.Error == null, "the Match legend draws without a palette: " + probe.Error);
        }
        finally
        {
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close();
        }
    }

    private sealed class LegendProbe : DisplayConduit, IDisposable
    {
        private readonly object _labels;
        internal bool Ran { get; private set; }
        internal Exception? Error { get; private set; }
        internal LegendProbe(object labels) { _labels = labels; }
        protected override void DrawForeground(DrawEventArgs e)
        {
            Ran = true;
            try { Method(_labels, "DrawLegend").Invoke(_labels, new object[] { e, 1f, 0 }); }
            catch (TargetInvocationException error) { Error = error.InnerException; }
        }
        public void Dispose() => Enabled = false;
    }
}
