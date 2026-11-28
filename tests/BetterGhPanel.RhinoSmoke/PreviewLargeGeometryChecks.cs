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
            VerifySharedBudget(assembly);
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
        }
        finally { Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>()); }
    }

    private static void VerifySharedBudget(Assembly assembly)
    {
        object first = NewHighlight(assembly), second = NewHighlight(assembly);
        try
        {
            using var brep = GridBrep(10, 10);
            Set(first, new IGH_Goo?[] { new GH_Brep(brep) }, new[] { 0 }, new[] { 0 });
            long used = PropertyValue<long>(first, "RetainedBytes");
            Check(Count(first) == 1 && used > 0, "the first panel holds a preview");
            PreviewLimits.SetRetainedBytes(assembly, used + 1024);
            Set(second, new IGH_Goo?[] { new GH_Brep(brep) }, new[] { 0 }, new[] { 0 });
            Check(Count(second) == 0 && Status(second).Contains("partial"),
                "a second panel cannot exceed the budget another panel already uses: " + Status(second));
            Method(first, "Clear").Invoke(first, Array.Empty<object>());
            Set(second, new IGH_Goo?[] { new GH_Brep(brep) }, new[] { 0 }, new[] { 0 });
            Check(Count(second) == 1, "the budget is available again once the other panel clears");
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

        // A red Rhino object 0.05 units above the coloured surface. The old 0.1%
        // push covered it from 50 units away and hid the surface's own dot.
        using var marker = Mesh.CreateFromPlane(new Plane(new Point3d(5, 5, .05), Vector3d.ZAxis), new Interval(-2, 2), new Interval(-2, 2), 1, 1);
        var attributes = new ObjectAttributes { ObjectColor = Color.Red, ColorSource = ObjectColorSource.ColorFromObject };
        var markerId = rhino.Objects.AddMesh(marker, attributes);
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Panel+ coloured fill depth", DefinedViewportProjection.Top, new Rectangle(0, 0, 1000, 720), false);
        try
        {
            rhino.Views.ActiveView = view;
            var bounds = new BoundingBox(new Point3d(-12, -12, -1), new Point3d(12, 12, 1));
            using var conduit = new ValuePreviewConduit(component, fixture.Document) { Enabled = true, Bounds = bounds };
            PreviewMode(component, "FilteredResults");
            Call(component, "SetShowValues", true);
            Call(component, "SetValuePalette", PanelValuePalette.Viridis);
            Call(component, "HighlightRows", fixture.Result.Snapshot, Array.Empty<int>());
            var labels = FieldValue<object>(FieldValue<object>(component, "_highlight"), "_orderLabels");
            await fixture.Ready(() => FieldValue<IDictionary>(labels, "_labels").Count == 1 && FieldValue<Array>(labels, "_legend").Length == 1);
            var viewport = view.ActiveViewport;
            var shaded = DisplayModeDescription.FindByName("Shaded");
            viewport.DisplayMode = shaded;
            foreach (double height in new[] { 50.0, 500.0 })
            {
                viewport.SetCameraLocations(Point3d.Origin, new Point3d(0, 0, height));
                viewport.ZoomBoundingBox(bounds);
                var visible = CaptureDotFrame(view, shaded, labels, true);
                Check(visible.Contains(0), $"a coloured Top-facing surface keeps its own dot with Hide dots behind objects on (camera {height})");
                using var image = view.CaptureToBitmap(new Size(1000, 720), shaded);
                var pixel = PixelAt(image, viewport, new Point3d(5, 5, .05));
                Check(pixel.R > pixel.G + 60 && pixel.R > pixel.B + 60,
                    $"a Rhino object 0.05 units in front of the coloured fill stays visible (camera {height}, pixel {pixel})");
                var fill = PixelAt(image, viewport, new Point3d(-5, -5, 0));
                Check(!(fill.R > fill.G + 60 && fill.R > fill.B + 60) && Math.Abs(fill.G - fill.B) + Math.Abs(fill.R - fill.G) > 30,
                    $"the coloured fill still draws where nothing covers it (camera {height}, pixel {fill})");
            }
        }
        finally
        {
            rhino.Objects.Delete(markerId, true);
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close();
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
