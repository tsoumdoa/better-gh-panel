using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;
using Forms = System.Windows.Forms;
using DrawingPoint = System.Drawing.Point;

internal static class RowActionChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string GeometryFormat = "Rhino 8 3DM Geometry";

    internal static async Task Run(Assembly assembly)
    {
        // This suite owns only geometry added to the isolated smoke process. Keep
        // the user's clipboard intact even when a native assertion fails.
        var previousClipboard = Forms.Clipboard.GetDataObject();
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        var previousUnits = rhino.ModelUnitSystem;
        bool previousModified = rhino.Modified;
        var existingIds = rhino.Objects.Select(item => item.Id).ToHashSet();
        rhino.ModelUnitSystem = UnitSystem.Millimeters;
        var view = rhino.Views.ActiveView ?? rhino.Views.Add("Panel row checks",
            Rhino.Display.DefinedViewportProjection.Perspective, new Rectangle(0, 0, 640, 480), false);
        rhino.Views.ActiveView = view;
        Check(view != null, "Row actions have an active native Rhino viewport");
        try
        {
            using var document = new GH_Document();
            GH_Document.EnableSolutions = true;
            document.Enabled = true;
            var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            component.CreateAttributes();
            var source = new Param_GenericObject();
            source.CreateAttributes();
            var box = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4)));
            using var subdMesh = Mesh.CreateFromBox(box, 1, 1, 1);
            var surface = new PlaneSurface(Plane.WorldXY, new Interval(0, 10), new Interval(0, 10)).ToBrep();
            var solid = box.ToBrep();
            var subd = SubD.CreateFromMesh(subdMesh);
            Check(subd != null && subd.IsValid, "Native SubD fixture is valid");
            var items = new IGH_Goo?[]
            {
                new GH_Number(1234.56789), new GH_Integer(42), new GH_String("full\ntext"), null,
                new GH_Point(new Point3d(20, 30, 40)),
                new GH_Line(new Line(new Point3d(100, 100, 0), new Point3d(103, 104, 0))),
                new GH_Rectangle(new Rectangle3d(Plane.WorldXY, 3, 4)), new GH_Box(box),
                new GH_Surface(surface), new GH_Brep(solid), new GH_Curve(),
                new GH_Vector(new Vector3d(1, 2, 3)), new GH_SubD(subd)
            };
            for (int i = 0; i < items.Length; i++) source.PersistentData.Append(items[i]!, new GH_Path(0));
            document.AddObject(source, false);
            document.AddObject(component, false);
            component.Params.Input[0].AddSource(source);
            document.NewSolution(false, GH_SolutionMode.Silent);
            var session = PanelTestAdapter.Session(component);
            await WaitForQuery(session);
            Check(Snapshot(session).Rows.Count == items.Length, "Row action fixture retains mixed and null rows");
            uint surfaceCrc = surface.DataCRC(0), solidCrc = solid.DataCRC(0), subdCrc = subd!.DataCRC(0);

            VerifyHighlightDrawing(assembly, document, rhino);
            await VerifyValues(component, session);
            await VerifyCanvasAndQueryActions(component, session, rhino);
            await VerifyBranchHighlight(component, session, document, rhino);
            await VerifyGeometryClipboard(component, session, rhino, items.Length);
            await VerifyCopyDuringDeselection(component, session);
            await VerifyCopyDuringDocumentSwitch(component, session, document);
            await VerifyFailuresAndCancellation(component, session);

            Check(source.PersistentData.Branches[0].SequenceEqual(items), "Row actions preserve upstream goo identities and positions");
            Check(surface.IsValid && surface.DataCRC(0) == surfaceCrc && solid.IsValid && solid.DataCRC(0) == solidCrc &&
                subd.IsValid && subd.DataCRC(0) == subdCrc, "Framing and both clipboard modes never transform or dispose source geometry");
            Check(component.Params.Input[0].VolatileData.DataCount == items.Length &&
                component.Params.Output[0].VolatileData.DataCount == items.Length &&
                ((GH_Number)component.Params.Output[0].VolatileData.get_Branch(new GH_Path(0))[0]!).Value == 1234.56789,
                "Row actions preserve output tree values and full numeric precision");
            component.DocumentContextChanged(document, GH_DocumentContext.Close);
        }
        finally
        {
            foreach (var item in rhino.Objects.ToArray())
                if (!existingIds.Contains(item.Id)) rhino.Objects.Delete(item.Id, true);
            rhino.ModelUnitSystem = previousUnits;
            rhino.Modified = previousModified;
            if (previousClipboard == null) Forms.Clipboard.Clear();
            else Forms.Clipboard.SetDataObject(previousClipboard, true);
        }
    }

    internal static async Task RunOverlap(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        var previousUnits = rhino.ModelUnitSystem;
        bool previousModified = rhino.Modified;
        GH_Document.EnableSolutions = true;
        rhino.ModelUnitSystem = UnitSystem.Millimeters;
        try { await VerifyNativePreviewOverlap(assembly, rhino); }
        finally { rhino.ModelUnitSystem = previousUnits; rhino.Modified = previousModified; }
    }

    private static async Task VerifyNativePreviewOverlap(Assembly assembly, RhinoDoc rhino)
    {
        var previousView = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Panel highlight overlap", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document();
        try
        {
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            document.Enabled = true;
            // The public KeepOpen option enables this isolated document's native
            // preview without opening or replacing the user's Grasshopper canvas.
            document.Properties.KeepOpen = true;
            document.PreviewColour = Color.FromArgb(100, 220, 40, 20);
            document.PreviewColourSelected = Color.FromArgb(100, 0, 210, 40);
            var components = new List<GH_Component>();
            foreach (bool upstreamLast in new[] { false, true })
            {
                double bottom = upstreamLast ? -5 : 1;
                var source = new Param_Geometry();
                source.CreateAttributes();
                source.PersistentData.Append(new GH_Curve(new LineCurve(new Point3d(-4, bottom, 0), new Point3d(-4, bottom + 4, 0))), new GH_Path(0));
                source.PersistentData.Append(new GH_Curve(new LineCurve(new Point3d(4, bottom, 0), new Point3d(4, bottom + 4, 0))), new GH_Path(0));
                source.PersistentData.Append(new GH_Mesh(Mesh.CreateFromBox(new BoundingBox(
                    new Point3d(-3, bottom + 1, -1), new Point3d(-1, bottom + 3, 1)), 1, 1, 1)), new GH_Path(0));
                source.PersistentData.Append(new GH_Mesh(Mesh.CreateFromBox(new BoundingBox(
                    new Point3d(1, bottom + 1, -1), new Point3d(3, bottom + 3, 1)), 1, 1, 1)), new GH_Path(0));
                var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
                component.CreateAttributes();
                if (upstreamLast) { document.AddObject(component, false); document.AddObject(source, false); }
                else { document.AddObject(source, false); document.AddObject(component, false); }
                component.Params.Input[0].AddSource(source);
                components.Add(component);
                Check(!component.Attributes.Selected && !source.Attributes.Selected && !source.Hidden,
                    "Overlap fixture retains an unselected panel and the upstream component's usual preview");
            }
            document.NewSolution(false, GH_SolutionMode.Silent);
            foreach (var component in components)
            {
                var session = PanelTestAdapter.Session(component);
                await WaitForQuery(session);
                Method(component, "HighlightRows").Invoke(component, new object[] { Snapshot(session), new[] { 0, 2 } });
            }
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-6, -6, -2), new Point3d(6, 6, 2)));
            view.Redraw();
            using var capture = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
            Check(capture != null, "Rhino renders an offscreen viewport capture with native Grasshopper previews");
            string path = Path.Combine(Path.GetDirectoryName(assembly.Location)!, "rhino-smoke-highlight-overlap.png");
            capture!.Save(path);
            // Both insertion orders share one native capture: top pair is panel-last,
            // bottom pair is upstream-last. The tests tolerate antialiasing and DPI.
            for (int half = 0; half < 2; half++)
            {
                string order = half == 0 ? "panel-last" : "upstream-last";
                int green = 0, red = 0, meshGreen = 0, meshRed = 0;
                for (int y = half * capture.Height / 2; y < (half + 1) * capture.Height / 2; y++)
                    for (int x = 0; x < capture.Width; x++)
                    {
                        var color = capture.GetPixel(x, y);
                        bool meshRegion = x > capture.Width * 0.30 && x < capture.Width * 0.70;
                        if (meshRegion && color.G > color.R + 10 && color.G > color.B + 30) meshGreen++;
                        if (!meshRegion && color.G > color.R + 50 && color.G > color.B + 50)
                        {
                            green++;
                        }
                        if (color.R > color.G + 50 && color.R > color.B + 50)
                        {
                            if (meshRegion) meshRed++; else red++;
                        }
                    }
                Console.WriteLine("Native highlight overlap " + order + ": curves " + green + " green / " + red +
                    " red; meshes " + meshGreen + " green / " + meshRed + " red; " + path);
                Check(green > 50 && red > 50,
                    "Green selection and normal red geometry remain visible with upstream preview enabled (" + order + ")");
                // GH's translucent selected material blends with the upstream red
                // preview into an olive green, unlike the saturated wire color.
                // The hue still distinguishes it from both gray background and red
                // unselected faces without depending on exact rendered RGB values.
                Check(meshGreen > 50 && meshRed > 50,
                    "Shaded mesh selections retain a distinct green tint alongside normal red meshes (" + order + ")");
            }
            foreach (var component in components) component.DocumentContextChanged(document, GH_DocumentContext.Close);
        }
        finally
        {
            document.Properties.KeepOpen = false;
            document.PreviewMode = GH_PreviewMode.Disabled;
            if (previousView != null) rhino.Views.ActiveView = previousView;
            view.Close();
        }
    }

    private static async Task VerifyBranchHighlight(GH_Component component, object session, GH_Document document, RhinoDoc rhino)
    {
        var highlight = Field<object>(component, "_highlight");
        var viewport = rhino.Views.ActiveView.ActiveViewport;
        var sentinel = rhino.Objects.AddPoint(new Point3d(-200, -200, -200));
        rhino.Objects.FindId(sentinel).Select(true);
        int objectCount = rhino.Objects.Count;
        var selectedIds = rhino.Objects.GetSelectedObjects(false, false).Select(item => item.Id).ToArray();
        var inspectedTarget = new Point3d(250, 300, 350);
        viewport.SetCameraTarget(inspectedTarget, true);
        Branch("{0}");
        await WaitForQuery(session);
        Check(Property<int>(highlight, "PreviewCount") == 0 && viewport.CameraTarget == inspectedTarget,
            "Choosing a branch only filters rows in Selected preview mode");
        var options = Property<PanelQueryOptions>(session, "Options");
        options.SortMode = PanelSortMode.ValueAscending;
        Query(session, options);
        await WaitForQuery(session);
        Check(Property<int>(highlight, "Count") == 0 && viewport.CameraTarget.DistanceTo(inspectedTarget) < 1e-8,
            "Sorting a branch filter neither selects geometry nor moves the camera");
        Branch(null);
        await WaitForQuery(session);
        Check(Property<int>(highlight, "Count") == 0 && viewport.CameraTarget.DistanceTo(inspectedTarget) < 1e-8,
            "All branches clears the branch highlight without zooming the entire model");

        Query(session, new PanelQueryOptions { TypeName = "Point" });
        await WaitForQuery(session);
        Branch("{0}");
        await WaitForQuery(session);
        Check(Property<int>(highlight, "Count") == 0 && viewport.CameraTarget == inspectedTarget,
            "Combining branch and type filters does not implicitly select or frame geometry");
        Branch(null);
        await WaitForQuery(session);
        Query(session, new PanelQueryOptions());
        await WaitForQuery(session);
        Method(component, "HighlightRows").Invoke(component, new object[] { Snapshot(session), new[] { 4 } });
        component.ExpireSolution(true);
        Check(Property<int>(highlight, "PreviewCount") == 0, "A new solution clears old geometry highlights immediately");
        await WaitForQuery(session);
        Check(Property<int>(highlight, "PreviewCount") == 0, "A new snapshot cannot restore a stale row highlight");

        Branch("{0}");
        component.DocumentContextChanged(document, GH_DocumentContext.Unloaded);
        await WaitForQuery(session);
        Check(Property<int>(highlight, "PreviewCount") == 0, "A query completing after a document unload cannot revive its highlight");
        component.DocumentContextChanged(document, GH_DocumentContext.Loaded);
        Branch(null);
        await WaitForQuery(session);
        Check(rhino.Objects.Count == objectCount && selectedIds.SequenceEqual(rhino.Objects.GetSelectedObjects(false, false).Select(item => item.Id)),
            "Row and branch highlighting never bakes objects or modifies Rhino's native object selection");
        // Run's finally removes this sentinel together with the clipboard fixtures.

        void Branch(string? path) => Method(component.Attributes, "SetBranchFilter").Invoke(component.Attributes, new object?[] { path });
    }

    private static void VerifyHighlightDrawing(Assembly assembly, GH_Document document, RhinoDoc rhino)
    {
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        component.Attributes.Selected = true;
        var normal = new PreviewProbe(new Point3d(-10, 0, 0));
        var selected = new PreviewProbe(new Point3d(10, 0, 0));
        var throwing = new PreviewProbe(new Point3d(0, 10, 0)) { ThrowOnDraw = true };
        var items = new IGH_Goo?[] { normal, throwing, selected, new GH_Number(5), new GH_Vector(Vector3d.XAxis),
            new GH_ObjectWrapper(new Point3d(30, 0, 0)), new GH_Curve(), null };
        var highlight = Field<object>(component, "_highlight");
        using var args = new PreviewArguments(document, rhino.Views.ActiveView.ActiveViewport);
        Method(highlight, "Set").Invoke(highlight, new object[] { items, Enumerable.Range(0, items.Length).ToArray(), new[] { 1, 2 } });
        component.DrawViewportWires(args);
        component.DrawViewportMeshes(args);
        Check(normal.WireColor == args.WireColour && selected.WireColor == args.WireColour_Selected &&
            normal.MeshMaterial == args.ShadeMaterial && selected.MeshMaterial == args.ShadeMaterial_Selected &&
            selected.WireThickness == args.DefaultCurveThickness,
            "Selected rows use Grasshopper selected wire and face colors; other geometry stays normal even when the component is selected");
        Check(selected.WireCalls == 1 && selected.MeshCalls == 1 && Property<int>(highlight, "Count") == 2,
            "One throwing third-party preview cannot prevent other selected rows from drawing");
        component.Hidden = true;
        component.DrawViewportWires(args);
        component.DrawViewportMeshes(args);
        component.Hidden = false;
        component.Locked = true;
        component.DrawViewportWires(args);
        component.DrawViewportMeshes(args);
        component.Locked = false;
        Check(selected.WireCalls == 1 && selected.MeshCalls == 1,
            "Hidden or disabled components cannot draw stale row highlights");
        Check(Property<int>(highlight, "PreviewCount") == 0, "Expiring component data clears borrowed geometry highlights");
        Method(highlight, "Set").Invoke(highlight, new object[] { items, Enumerable.Range(0, items.Length).ToArray(), new[] { 1, 2 } });
        Check(component.IsPreviewCapable && component.ClippingBox.Contains(normal.Value) && component.ClippingBox.Contains(new Point3d(30, 0, 0)),
            "Highlight preview clipping retains unselected geometry and wrapped geometric values");
        Method(highlight, "Set").Invoke(highlight, new object[] { items, new[] { 3, 4, 5, 6, 7 }, new[] { 3, 4, 5, 6, 7 } });
        Check(Property<int>(highlight, "Count") == 1,
            "Highlight accepts wrapped geometry and excludes numbers, vectors, nulls and invalid geometry");
        Method(highlight, "Set").Invoke(highlight, new object[] { items, new[] { 3 }, new[] { 3 } });
        component.DrawViewportWires(args);
        Check(Property<int>(highlight, "PreviewCount") == 0 &&
            selected.WireCalls == 1 && !component.ClippingBox.IsValid,
            "Selecting only numeric rows draws no geometry and has no clipping bounds");
        Method(highlight, "Set").Invoke(highlight, new object[] { items, new[] { 2 }, new[] { 2 } });
        component.DrawViewportWires(args);
        component.DrawViewportMeshes(args);
        Check(normal.WireCalls == 1 && normal.MeshCalls == 1 && selected.WireCalls == 2 && selected.MeshCalls == 2 &&
            !component.ClippingBox.Contains(normal.Value) && component.ClippingBox.Contains(selected.Value),
            "Selected preview draws and clips only its selected geometry");
        Method(highlight, "Set").Invoke(highlight, new object[] { items, new[] { 0 }, Array.Empty<int>() });
        component.DrawViewportWires(args);
        component.DrawViewportMeshes(args);
        Check(normal.WireCalls == 2 && normal.MeshCalls == 2 && normal.WireColor == args.WireColour &&
            selected.WireCalls == 2 && !component.ClippingBox.Contains(selected.Value),
            "Filtered preview draws only matching geometry in normal colors without a row selection");
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
        Check(Property<int>(highlight, "PreviewCount") == 0 && !Property<BoundingBox>(highlight, "ClippingBox").IsValid,
            "Clearing a highlight releases all borrowed previews and clipping bounds");
    }

    private sealed class PreviewProbe : GH_Point, IGH_PreviewData
    {
        internal PreviewProbe(Point3d point) : base(point) { }
        internal bool ThrowOnDraw;
        internal Color WireColor;
        internal int WireThickness, WireCalls, MeshCalls;
        internal Rhino.Display.DisplayMaterial? MeshMaterial;
        BoundingBox IGH_PreviewData.ClippingBox => Boundingbox;
        void IGH_PreviewData.DrawViewportWires(GH_PreviewWireArgs args)
        {
            if (ThrowOnDraw) throw new InvalidOperationException("Third-party preview failure");
            WireCalls++;
            WireColor = args.Color;
            WireThickness = args.Thickness;
        }
        void IGH_PreviewData.DrawViewportMeshes(GH_PreviewMeshArgs args)
        {
            if (ThrowOnDraw) throw new InvalidOperationException("Third-party preview failure");
            MeshCalls++;
            MeshMaterial = args.Material;
        }
    }

    internal sealed class PreviewArguments : IGH_PreviewArgs, IDisposable
    {
        internal PreviewArguments(GH_Document document, Rhino.Display.RhinoViewport viewport)
        {
            Document = document;
            Viewport = viewport;
            ShadeMaterial = new Rhino.Display.DisplayMaterial(WireColour);
            ShadeMaterial_Selected = new Rhino.Display.DisplayMaterial(WireColour_Selected);
        }
        public int DefaultCurveThickness => 3;
        public Color WireColour => Color.FromArgb(200, 80, 50);
        public Color WireColour_Selected => Color.FromArgb(40, 220, 70);
        public Rhino.Display.DisplayMaterial ShadeMaterial { get; }
        public Rhino.Display.DisplayMaterial ShadeMaterial_Selected { get; }
        public Rhino.Display.RhinoViewport Viewport { get; }
        public Rhino.Display.DisplayPipeline Display => null!; // Recording goo does not issue native drawing commands.
        public GH_Document Document { get; }
        public MeshingParameters MeshingParameters => MeshingParameters.FastRenderMesh;
        public void Dispose() { ShadeMaterial.Dispose(); ShadeMaterial_Selected.Dispose(); }
    }

    private static async Task VerifyValues(GH_Component component, object session)
    {
        var snapshot = Snapshot(session);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string beforeNumeric = ClipboardState();
            string numericStatus = await CopyValues(component, snapshot, 1, 0, 1);
            string afterNumeric = ClipboardState();
            Check(numericStatus == "2 values copied",
                $"Invariant numeric copy completes: {numericStatus}; host PID {Environment.ProcessId}; before {beforeNumeric}; after {afterNumeric}");
            string numericText = ClipboardText();
            string expectedNumeric = "1234.56789" + Environment.NewLine + "42";
            Check(numericText == expectedNumeric,
                $"Copy values uses invariant numeric text in query order and deduplicates selected positions (actual length {numericText.Length}, expected {expectedNumeric.Length}; after copy {afterNumeric}; after read {ClipboardState()})");
        }
        finally { CultureInfo.CurrentCulture = culture; }

        await CopyValues(component, snapshot, 3, 2);
        Check(ClipboardText() == "full\ntext" + Environment.NewLine + "<null>",
            "Text and null values retain full content in a mixed selection");
        string coordinateExpected = PanelTestAdapter.DisplayValue(component, snapshot, 4) + Environment.NewLine +
            PanelTestAdapter.DisplayValue(component, snapshot, 11);
        string coordinateCopy = await CopyValues(component, snapshot, 4, 11);
        string afterCopyState = ClipboardState();
        Check(coordinateCopy == "2 values copied", "Point/vector clipboard write completes: " + coordinateCopy);
        string coordinateText = ClipboardText();
        Check(coordinateText == coordinateExpected,
            "Point and vector value copies preserve the displayed coordinate text instead of switching to live goo formatting" +
            $" (clipboard length {coordinateText.Length}, expected {coordinateExpected.Length}; " +
            $"after copy {afterCopyState}; read {ClipboardState()})");
        await CopyValues(component, snapshot, 10);
        Check(ClipboardText() == PanelTestAdapter.DisplayValue(component, snapshot, 10) &&
            ClipboardText().StartsWith("Invalid ·", StringComparison.Ordinal),
            "Copying an invalid value preserves the visible invalid marker");
        await CopyValues(component, snapshot, 5, 6, 7, 8, 9);
        Check(ClipboardText() == string.Join(Environment.NewLine, new[] { "5", "12", "24", "100", "24" }),
            "Copy selected geometry values awaits deferred area and volume without compact descriptions or units");

        PanelTestAdapter.Units(component, PanelUnitConversion.None, 2);
        await CopyValues(component, Snapshot(session), 0);
        Check(ClipboardText() == "1234.57", "Copy value honors chosen decimal formatting without changing output precision");
        PanelTestAdapter.Units(component, PanelUnitConversion.MillimetersToMeters, -1);
        await WaitForQuery(session);
        await CopyValues(component, Snapshot(session), 5, 6);
        Check(ClipboardText() == "0.005" + Environment.NewLine + "12",
            "Length conversion changes copied length while preserving area dimensions");
        PanelTestAdapter.Units(component, PanelUnitConversion.SquareMillimetersToSquareMeters, -1);
        await WaitForQuery(session);
        await CopyValues(component, Snapshot(session), 6, 8);
        var areas = ClipboardText().Split(new[] { Environment.NewLine }, StringSplitOptions.None)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        Check(areas.Length == 2 && Math.Abs(areas[0] - 0.000012) < 1e-12 && Math.Abs(areas[1] - 0.0001) < 1e-12,
            "Area conversion applies to copied analytic and deferred metrics");
        PanelTestAdapter.Units(component, PanelUnitConversion.None, -1);
        await WaitForQuery(session);
    }

    private static async Task VerifyCanvasAndQueryActions(GH_Component component, object session, RhinoDoc rhino)
    {
        using var canvas = new GH_Canvas { Size = new Size(1000, 1000) };
        component.Attributes.Pivot = new PointF(50, 50);
        PanelTestAdapter.Resize(component, 500, 800);
        component.Attributes.PerformLayout();
        Paint(component, canvas);
        var viewport = rhino.Views.ActiveView.ActiveViewport;
        var cameraLocation = viewport.CameraLocation;
        var cameraTarget = viewport.CameraTarget;
        Click(component, canvas, PanelTestAdapter.RowBounds(component, 4));
        Check(Property<int>(Field<object>(component, "_highlight"), "Count") == 1,
            "Clicking a geometry row installs its native Grasshopper highlight");
        Check(viewport.CameraTarget == cameraTarget && viewport.CameraLocation == cameraLocation,
            "Clicking a geometry row leaves the active Rhino camera unchanged");
        Click(component, canvas, PanelTestAdapter.RowBounds(component, 0));
        Check(viewport.CameraTarget == cameraTarget, "Clicking numeric rows leaves the active camera unchanged");
        Check(Property<int>(Field<object>(component, "_highlight"), "Count") == 0,
            "Clicking a numeric row clears the previously highlighted geometry");

        var selection = Field<object>(component.Attributes, "_selection");
        Select(selection, Result(session), 4);
        Select(selection, Result(session), 5, toggle: true);
        Click(component, canvas, PanelTestAdapter.RowBounds(component, 4));
        Check(Selected(selection, Result(session)).SequenceEqual(new[] { 4, 5 }),
            "Clicking a selected row preserves a multirow group");
        Check(Property<int>(Field<object>(component, "_highlight"), "Count") == 2,
            "A multirow click highlights all selected geometry");
        Check(viewport.CameraTarget == cameraTarget && viewport.CameraLocation == cameraLocation,
            "Selecting a multirow group leaves the active Rhino camera unchanged");
        var unlockedCamera = viewport.CameraLocation;
        var unlockedTarget = viewport.CameraTarget;
        try
        {
            viewport.LockedProjection = true;
            var failure = (string?)Method(component, "FrameRows").Invoke(component, new object[] { Snapshot(session), new[] { 4 } });
            Check(failure != null && failure.Contains("locked") && viewport.CameraLocation == unlockedCamera &&
                viewport.CameraTarget == unlockedTarget, "Locked model viewports reject framing without changing their camera");
        }
        finally { viewport.LockedProjection = false; }
        Click(component, canvas, PanelTestAdapter.RowBounds(component, 4), Forms.MouseButtons.Right);
        SaveMenuPreview(component, canvas);
        var geometryMenu = Field<RectangleF>(component.Attributes, "_rowMenuBounds");
        Click(component, canvas, new RectangleF(geometryMenu.X, geometryMenu.Y, geometryMenu.Width, 20));
        Check(InsideFrustum(viewport, new Point3d(20, 30, 40), new Point3d(100, 100, 0), new Point3d(103, 104, 0)),
            "Explicit Frame selection fits the complete selected geometry group");

        Select(selection, Result(session), 0);
        Select(selection, Result(session), 1, toggle: true);
        Query(session, new PanelQueryOptions { SortMode = PanelSortMode.ValueAscending });
        await WaitForQuery(session);
        Paint(component, canvas);
        Check(Selected(selection, Result(session)).SequenceEqual(new[] { 1, 0 }), "Native canvas retains row identities across sorting");
        var row = PanelTestAdapter.RowBounds(component, 1);
        Click(component, canvas, row, Forms.MouseButtons.Right);
        Paint(component, canvas);
        var menu = Field<RectangleF>(component.Attributes, "_rowMenuBounds");
        Paint(component, canvas);
        Click(component, canvas, new RectangleF(menu.X, menu.Y + 20, menu.Width, 20));
        await WaitUntil(() => !Field<bool>(component.Attributes, "_copyPending"));
        Check(ClipboardText() == "42" + Environment.NewLine + "1234.56789",
            "Right-click Copy values preserves the selected group and follows current sorted order");

        Query(session, new PanelQueryOptions { TypeName = "Number" });
        await WaitForQuery(session);
        Paint(component, canvas);
        var filteredPositions = Selected(selection, Result(session));
        Check(filteredPositions.All(Result(session).RowPositions.Contains), "Filtered-out rows leave native selection");
        Forms.Clipboard.SetText("keep filtered clipboard");
        await CopyValues(component, Snapshot(session), 4);
        Check(ClipboardText() == "keep filtered clipboard", "Actions reject positions excluded by the current filter");

        Query(session, new PanelQueryOptions());
        await WaitForQuery(session);
        Paint(component, canvas);
        var staleRowBounds = PanelTestAdapter.RowBounds(component, 4);
        var staleTarget = viewport.CameraTarget;
        Query(session, new PanelQueryOptions { Search = "full" });
        Check(Property<int>(Field<object>(component, "_highlight"), "PreviewCount") == 0,
            "Starting a query clears stale highlights before the canvas paints its next result");
        Click(component, canvas, staleRowBounds);
        Check(viewport.CameraTarget == staleTarget, "An old painted row cannot move the camera while a query is pending");
        await WaitForQuery(session);
        Query(session, new PanelQueryOptions());
        await WaitForQuery(session);
        VerifyDeselection(component, canvas, session, rhino);
        await VerifyMenuGestures(component, canvas, session, rhino);
    }

    private static void VerifyDeselection(GH_Component component, GH_Canvas canvas, object session, RhinoDoc rhino)
    {
        canvas.Document = component.OnPingDocument();
        Paint(component, canvas);
        var selection = Field<object>(component.Attributes, "_selection");
        var highlight = Field<object>(component, "_highlight");
        var result = Result(session);
        var output = component.Params.Output[0].VolatileData;
        var viewport = rhino.Views.ActiveView.ActiveViewport;
        var camera = viewport.CameraLocation;
        var target = viewport.CameraTarget;
        var row = PanelTestAdapter.RowBounds(component, 4);
        var rowPoint = new PointF(row.X + 45, row.Y + row.Height / 2);
        Forms.Clipboard.SetText("keep deselection clipboard");

        Select(selection, result, 4);
        Select(selection, result, 5, toggle: true);
        MouseDown(component, canvas, rowPoint, 1);
        TrailingDoubleClick(component, canvas, rowPoint);
        Check(Selected(selection, result).Count == 0 && Property<int>(highlight, "PreviewCount") == 0 &&
            ClipboardText() == "keep deselection clipboard", "Double-click clears a multirow selection and its preview without copying values");

        var list = PanelTestAdapter.Bounds(component, CanvasControl.List);
        var branchPoint = new PointF(list.X + 45, list.Y + 4);
        MouseDown(component, canvas, branchPoint, 1);
        Check(Selected(selection, result).Count == result.RowPositions.Count, "Branch click still selects every matching row");
        TrailingDoubleClick(component, canvas, branchPoint);
        Check(Selected(selection, result).Count == 0, "Double-clicking a branch heading clears its selection");

        Click(component, canvas, row);
        var blankList = new PointF(list.X + 45, list.Bottom - 3);
        MouseDown(component, canvas, blankList, 1);
        Check(Selected(selection, result).Count == 0, "Clicking empty table space clears rows");
        TrailingDoubleClick(component, canvas, blankList);
        Check(!CanvasEditorAdapter.IsActive(component.GetType().Assembly), "Double-clicking empty table space does not open the search editor");

        Click(component, canvas, row);
        var panel = PanelTestAdapter.Bounds(component, CanvasControl.Panel);
        MouseDown(component, canvas, new PointF(panel.Left + 2, list.Top + 4), 1);
        Check(Selected(selection, result).Count == 0, "Clicking an empty panel margin clears rows");
        Click(component, canvas, row);
        var move = PanelTestAdapter.Bounds(component, CanvasControl.Move);
        var response = component.Attributes.RespondToMouseDown(canvas,
            new GH_CanvasMouseEvent(DrawingPoint.Empty, new PointF(move.Left + 2, move.Top + 7), Forms.MouseButtons.Left, 1, 0));
        Check(Selected(selection, result).Count == 1 && response == GH_ObjectResponse.Ignore,
            "The dedicated move handle preserves rows and leaves component dragging to Grasshopper");

        Click(component, canvas, row);
        Check(Field<GH_Canvas?>(component.Attributes, "_selectionCanvas") == canvas, "Row selection observes its owning canvas only while selected");
        PanelTestAdapter.CanvasClick(canvas, new PointF(panel.Right + 80, panel.Bottom - 40), Forms.MouseButtons.Right);
        Check(Selected(selection, result).Count == 1, "Panning on empty canvas keeps the row selection");
        // Use an unrelated native object to prove this is background clearing,
        // rather than observing every mouse event outside the panel rectangle.
        var other = new Param_Number();
        other.CreateAttributes();
        other.Attributes.Pivot = new PointF(panel.Right + 70, panel.Top + 100);
        canvas.Document.AddObject(other, false);
        other.Attributes.PerformLayout();
        var otherBounds = other.Attributes.Bounds;
        PanelTestAdapter.CanvasClick(canvas, new PointF(otherBounds.X + otherBounds.Width / 2, otherBounds.Y + otherBounds.Height / 2));
        Check(Selected(selection, result).Count == 1, "Clicking another Grasshopper object does not masquerade as empty canvas");
        canvas.Document.RemoveObject(other, false);
        PanelTestAdapter.CanvasClick(canvas, new PointF(panel.Right + 80, panel.Bottom - 40));
        Check(Selected(selection, result).Count == 0 && Field<GH_Canvas?>(component.Attributes, "_selectionCanvas") == null,
            "A host-dispatched blank-canvas click clears rows and detaches its observer");

        Click(component, canvas, row);
        Method(component.Attributes, "SuppressTrailingDoubleClick").Invoke(component.Attributes, Array.Empty<object>());
        PanelTestAdapter.CanvasClick(canvas, new PointF(panel.Right + 80, panel.Bottom - 40), clicks: 2);
        Check(Selected(selection, result).Count == 1,
            "A dismissed control's trailing click cannot clear selection when it lands on newly exposed canvas");
        PanelTestAdapter.CanvasClick(canvas, new PointF(panel.Right + 80, panel.Bottom - 40));
        Check(Selected(selection, result).Count == 0, "A fresh blank-canvas click still clears selection during the prior gesture's interval");

        PanelTestAdapter.PreviewMode(component, "FilteredResults");
        Click(component, canvas, row);
        int previewCount = Property<int>(highlight, "PreviewCount");
        PanelTestAdapter.CanvasClick(canvas, new PointF(panel.Right + 80, panel.Bottom - 40));
        Check(previewCount > 0 && Property<int>(highlight, "PreviewCount") == previewCount && Property<int>(highlight, "Count") == 0,
            "Clearing rows retains the complete filtered-result preview without selected highlights");
        PanelTestAdapter.PreviewMode(component, "Selected");

        Click(component, canvas, row);
        using (var otherDocument = new GH_Document())
        {
            canvas.Document = otherDocument;
            Check(Selected(selection, result).Count == 0 && Field<GH_Canvas?>(component.Attributes, "_selectionCanvas") == null,
                "Switching canvas documents clears selection and detaches the observer");
            canvas.Document = component.OnPingDocument();
        }
        Check(ReferenceEquals(Result(session), result) && ReferenceEquals(component.Params.Output[0].VolatileData, output) &&
            viewport.CameraLocation == camera && viewport.CameraTarget == target && ClipboardText() == "keep deselection clipboard",
            "All deselection gestures preserve the query, typed output, camera and clipboard");
    }

    private static async Task VerifyMenuGestures(GH_Component component, GH_Canvas canvas, object session, RhinoDoc rhino)
    {
        var selection = Field<object>(component.Attributes, "_selection");
        var viewport = rhino.Views.ActiveView.ActiveViewport;
        Query(session, new PanelQueryOptions { TypeName = "Point" });
        await WaitForQuery(session);
        Paint(component, canvas);
        Method(selection, "Clear").Invoke(selection, Array.Empty<object>());
        Forms.Clipboard.SetText("keep menu gesture clipboard");
        var target = viewport.CameraTarget;
        var pointRow = PanelTestAdapter.RowBounds(component, 4);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Sort));
        var sort = PanelTestAdapter.MenuBounds(component);
        var overlap = new PointF(sort.X + 45, Math.Max(sort.Top, pointRow.Top) + 0.5f);
        Check(pointRow.Contains(overlap) && overlap.Y < sort.Top + 20,
            "Unchanged Source order menu entry overlaps a live geometry row for the regression");
        MouseDown(component, canvas, overlap, 1);
        TrailingDoubleClick(component, canvas, overlap);
        Check(Selected(selection, Result(session)).Count == 0 && viewport.CameraTarget == target &&
            ClipboardText() == "keep menu gesture clipboard",
            "Selecting unchanged sort order blocks the same gesture from framing or copying the row below");

        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Sort));
        var rowPoint = new PointF(pointRow.X + 45, pointRow.Y + pointRow.Height / 2);
        MouseDown(component, canvas, rowPoint, 1);
        TrailingDoubleClick(component, canvas, rowPoint);
        Check(Selected(selection, Result(session)).Count == 0 && ClipboardText() == "keep menu gesture clipboard",
            "Dismissing a dropdown over a row consumes the gesture without selecting or copying it");

        Query(session, new PanelQueryOptions());
        await WaitForQuery(session);
        Paint(component, canvas);
        Method(selection, "Clear").Invoke(selection, Array.Empty<object>());
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Display));
        Paint(component, canvas);
        var halfDisplay = PanelTestAdapter.Bounds(component, CanvasControl.Display);
        var displayPoint = new PointF(halfDisplay.X + halfDisplay.Width / 2, halfDisplay.Y + halfDisplay.Height / 2);
        MouseDown(component, canvas, displayPoint, 1);
        Paint(component, canvas);
        Check(PanelTestAdapter.RowBounds(component, 8).Contains(displayPoint),
            "Expanded display places a measurable surface under the old half-display control");
        target = viewport.CameraTarget;
        TrailingDoubleClick(component, canvas, displayPoint);
        Check(Selected(selection, Result(session)).Count == 0 && viewport.CameraTarget == target &&
            ClipboardText() == "keep menu gesture clipboard",
            "Expanding the panel blocks trailing double-clicks from copying or framing newly exposed rows");

        var number = PanelTestAdapter.RowBounds(component, 0);
        var numberPoint = new PointF(number.X + 45, number.Y + number.Height / 2);
        MouseDown(component, canvas, numberPoint, 1);
        TrailingDoubleClick(component, canvas, numberPoint);
        Check(Selected(selection, Result(session)).Count == 0 && ClipboardText() == "keep menu gesture clipboard",
            "A fresh intentional double-click immediately after display dismissal still clears its row selection");

        Click(component, canvas, number, Forms.MouseButtons.Right);
        Paint(component, canvas);
        var menu = Field<RectangleF>(component.Attributes, "_rowMenuBounds");
        Paint(component, canvas);
        Click(component, canvas, new RectangleF(menu.X, menu.Y, menu.Width, 20));
        Check(Field<string>(component.Attributes, "_actionStatus").Contains("No valid geometry"),
            "Explicit Frame selection explains why a numeric selection cannot move the camera");
    }

    private static async Task VerifyGeometryClipboard(GH_Component component, object session, RhinoDoc rhino, int rowCount)
    {
        var snapshot = Snapshot(session);
        var sentinelId = rhino.Objects.AddPoint(new Point3d(-100, -100, -100));
        rhino.Objects.FindId(sentinelId).Select(true);
        int objectCount = rhino.Objects.Count;
        var selectedIds = rhino.Objects.GetSelectedObjects(false, false).Select(item => item.Id).ToArray();
        Forms.Clipboard.SetText("old numeric clipboard");
        Check(CopyGeometry(component, snapshot, Enumerable.Range(0, rowCount).ToArray(), out var status) &&
            status.Contains("7 geometries") && status.Contains("6 skipped"),
            "Geometry copy includes supported mixed rows and reports skipped numeric, text, null, vector and invalid data");
        Check(!Forms.Clipboard.ContainsText(), "Geometry clipboard replacement cannot leave stale copied text behind");
        using (var file = ReadClipboardArchive())
        {
            Check(file.Objects.Count == 7 && file.Settings.ModelUnitSystem == UnitSystem.Millimeters,
                "Geometry clipboard carries seven independent objects and source model units");
            Check(file.Objects.Count(item => item.Geometry is Rhino.Geometry.Point) == 1 &&
                file.Objects.Any(item => item.Geometry is SubD) && file.Objects.Count(item => item.Geometry is Brep) >= 3,
                "Native clipboard preserves SubD and surface geometry without coercing vectors or numbers into points");
        }
        Check(rhino.Objects.Count == objectCount && selectedIds.SequenceEqual(rhino.Objects.GetSelectedObjects(false, false).Select(item => item.Id)),
            "Geometry copy adds no objects to the Rhino document and leaves its selection intact");

        var beforePaste = rhino.Objects.Select(item => item.Id).ToHashSet();
        Check(RhinoApp.RunScript(rhino.RuntimeSerialNumber, "_Paste", false), "Rhino accepts native Paste for the geometry clipboard");
        // Native commands can purge deleted object-table entries. Verify the
        // actual newly pasted live objects instead of table bookkeeping counts.
        await WaitUntil(() => rhino.Objects.Count(item => !beforePaste.Contains(item.Id)) == 7);
        var pasted = rhino.Objects.Where(item => !beforePaste.Contains(item.Id)).ToArray();
        Check(pasted.Length == 7 && pasted.Any(item => item.Geometry is SubD) &&
            pasted.Select(item => item.Geometry).OfType<Rhino.Geometry.Point>().Single().Location == new Point3d(20, 30, 40),
            "Native Rhino Paste restores selected geometry, locations and SubD type");
        foreach (var item in pasted) rhino.Objects.Delete(item.Id, true);

        Forms.Clipboard.SetText("keep nongeometry clipboard");
        Check(!CopyGeometry(component, snapshot, new[] { 0, 1, 2, 3, 10, 11 }, out _) &&
            ClipboardText() == "keep nongeometry clipboard", "An unsupported geometry selection preserves the previous clipboard");
        Check(!CopyGeometry(component, snapshot, new[] { -1, 4 }, out _) &&
            ClipboardText() == "keep nongeometry clipboard", "Invalid row positions reject the whole geometry action");
    }

    private static async Task VerifyFailuresAndCancellation(GH_Component component, object session)
    {
        var stale = Snapshot(session);
        component.ExpireSolution(true);
        await WaitForQuery(session);
        Forms.Clipboard.SetText("keep stale clipboard");
        await CopyValues(component, stale, 0);
        Check(!CopyGeometry(component, stale, new[] { 4 }, out _) && ClipboardText() == "keep stale clipboard",
            "A replaced solution rejects both copy modes without changing the clipboard");

        using var gate = new ManualResetEventSlim(false);
        int measurements = 0;
        Method(session, "SetGeometryDetailResolver").Invoke(session, new object[]
        {
            (Func<int, Func<PanelGeometryInfo?>?>)(position => () =>
            {
                Interlocked.Increment(ref measurements);
                if (!gate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Row action measurement gate timed out");
                return new PanelGeometryInfo("Srf", 100, 2, "mm", "Area", "A");
            })
        });
        var snapshot = Snapshot(session);
        try
        {
            var pending = CopyValues(component, snapshot, 8);
            await WaitUntil(() => Volatile.Read(ref measurements) == 1);
            Check(CopyGeometry(component, snapshot, new[] { 4 }, out _), "A new geometry copy can replace an in-flight measurement copy");
            var expectedClipboard = ReadClipboardBytes(out var expectedTransport);
            Check(IsExpectedClipboardPoint(expectedClipboard),
                "The new geometry clipboard contains the expected point with source model units: " + expectedTransport);
            gate.Set();
            string pendingStatus = await pending;
            Check(pendingStatus == "Copy cancelled", "Superseded value copy reports cancellation: " + pendingStatus);
            // Cancelling the copy can complete before its measurement worker.
            // Inspect the clipboard only after the late UI callback has run.
            await WaitUntil(() => !PanelTestAdapter.GeometryRunning(session));
            var currentClipboard = ReadClipboardBytes(out var currentTransport);
            if (!expectedClipboard.SequenceEqual(currentClipboard))
            {
                int commonPrefix = expectedClipboard.Zip(currentClipboard).TakeWhile(pair => pair.First == pair.Second).Count();
                bool extraZeros = expectedClipboard.Skip(commonPrefix).All(value => value == 0) &&
                    currentClipboard.Skip(commonPrefix).All(value => value == 0);
                Console.WriteLine($"Geometry payload representation changed: before [{expectedTransport}], after [{currentTransport}]; " +
                    $"common prefix {commonPrefix}, remaining bytes all zero={extraZeros}; " +
                    $"expected point before={IsExpectedClipboardPoint(expectedClipboard)}, after={IsExpectedClipboardPoint(currentClipboard)}.");
            }
            Check(expectedClipboard.SequenceEqual(currentClipboard),
                "An obsolete asynchronous value copy cannot overwrite the newer geometry clipboard");
            Check(!Forms.Clipboard.ContainsText() && IsExpectedClipboardPoint(currentClipboard),
                "The superseding clipboard contains only the expected point with source model units");
            var rhino = RhinoDoc.ActiveDoc;
            var beforePaste = rhino.Objects.Select(item => item.Id).ToHashSet();
            Check(RhinoApp.RunScript(rhino.RuntimeSerialNumber, "_Paste", false), "Rhino accepts the superseding single-point geometry clipboard");
            await WaitUntil(() => rhino.Objects.Count(item => !beforePaste.Contains(item.Id)) == 1);
            var pasted = rhino.Objects.Where(item => !beforePaste.Contains(item.Id)).ToArray();
            Check(pasted.Length == 1 && pasted[0].Geometry is Rhino.Geometry.Point point && point.Location == new Point3d(20, 30, 40),
                "The superseding clipboard still pastes exactly the requested point at its source coordinates");
            foreach (var item in pasted) rhino.Objects.Delete(item.Id, true);
        }
        finally
        {
            gate.Set();
            await WaitUntil(() => !PanelTestAdapter.GeometryRunning(session));
        }

        component.ExpireSolution(true);
        await WaitForQuery(session);
        Method(session, "SetGeometryDetailResolver").Invoke(session, new object[]
        {
            (Func<int, Func<PanelGeometryInfo?>?>)(_ => () => throw new InvalidOperationException("Measurement unavailable in test"))
        });
        Forms.Clipboard.SetText("keep failed measurement clipboard");
        await CopyValues(component, Snapshot(session), 0, 8);
        Check(ClipboardText() == "keep failed measurement clipboard", "Measurement failure cannot publish a partial multirow clipboard");
    }

    private static async Task VerifyCopyDuringDeselection(GH_Component component, object session)
    {
        component.ExpireSolution(true);
        await WaitForQuery(session);
        using var gate = new ManualResetEventSlim(false);
        var restoreResolver = PanelTestAdapter.SaveGeometryResolver(session);
        Method(session, "SetGeometryDetailResolver").Invoke(session, new object[]
        {
            (Func<int, Func<PanelGeometryInfo?>?>)(_ => () =>
            {
                if (!gate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
                return new PanelGeometryInfo("Srf", 100, 2, "mm", "Area", "A");
            })
        });
        try
        {
            using var canvas = new GH_Canvas { Size = new Size(1000, 1000) };
            Paint(component, canvas);
            Forms.Clipboard.SetText("keep cancelled selection clipboard");
            var row = PanelTestAdapter.RowBounds(component, 8);
            Click(component, canvas, row, Forms.MouseButtons.Right);
            Paint(component, canvas);
            var menu = Field<RectangleF>(component.Attributes, "_rowMenuBounds");
            Paint(component, canvas);
            Click(component, canvas, new RectangleF(menu.X, menu.Y + 20, menu.Width, 20));
            await WaitUntil(() => PanelTestAdapter.GeometryRunning(session));
            Check(Field<bool>(component.Attributes, "_copyPending"), "Context-menu value copy is pending during geometry measurement");
            Paint(component, canvas);
            var point = new PointF(row.X + 45, row.Y + row.Height / 2);
            MouseDown(component, canvas, point, 1);
            TrailingDoubleClick(component, canvas, point);
            gate.Set();
            await WaitUntil(() => !PanelTestAdapter.GeometryRunning(session));
            Check(!Field<bool>(component.Attributes, "_copyPending") && PanelTestAdapter.SelectedPosition(component) < 0 &&
                ClipboardText() == "keep cancelled selection clipboard", "Deselecting cancels an in-flight value copy and rejects its late clipboard publication");
        }
        finally
        {
            gate.Set();
            restoreResolver();
        }
    }

    private static async Task VerifyCopyDuringDocumentSwitch(GH_Component component, object session, GH_Document document)
    {
        component.ExpireSolution(true);
        await WaitForQuery(session);
        using var gate = new ManualResetEventSlim(false);
        var restoreResolver = PanelTestAdapter.SaveGeometryResolver(session);
        Method(session, "SetGeometryDetailResolver").Invoke(session, new object[]
        {
            (Func<int, Func<PanelGeometryInfo?>?>)(_ => () =>
            {
                if (!gate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
                return new PanelGeometryInfo("Srf", 100, 2, "mm", "Area", "A");
            })
        });
        try
        {
            using var canvas = new GH_Canvas { Size = new Size(1000, 1000) };
            component.Attributes.Pivot = new PointF(50, 50);
            PanelTestAdapter.Resize(component, 500, 800);
            component.Attributes.PerformLayout();
            Paint(component, canvas);
            Forms.Clipboard.SetText("keep document-switch clipboard");
            Click(component, canvas, PanelTestAdapter.RowBounds(component, 8), Forms.MouseButtons.Right);
            Paint(component, canvas);
            var menu = Field<RectangleF>(component.Attributes, "_rowMenuBounds");
            Paint(component, canvas);
            Click(component, canvas, new RectangleF(menu.X, menu.Y + 20, menu.Width, 20));
            await WaitUntil(() => PanelTestAdapter.GeometryRunning(session));
            Check(Field<bool>(component.Attributes, "_copyPending"), "Value copy is pending while its geometry is measured");
            component.DocumentContextChanged(document, GH_DocumentContext.Unloaded);
            gate.Set();
            await WaitUntil(() => !PanelTestAdapter.GeometryRunning(session));
            component.DocumentContextChanged(document, GH_DocumentContext.Loaded);
            Paint(component, canvas);
            Check(!Field<bool>(component.Attributes, "_copyPending") && ClipboardText() == "keep document-switch clipboard",
                "Switching documents cancels the copy and clears its pending status after returning");
            Check(PanelTestAdapter.SelectedPosition(component) < 0,
                "A cancelled document-switch copy cannot revive its old row selection");
        }
        finally
        {
            gate.Set();
            restoreResolver();
        }
    }

    private static Task<string> CopyValues(GH_Component component, PanelSnapshot snapshot, params int[] positions) =>
        (Task<string>)Method(component, "CopyValuesAsync").Invoke(component, new object[] { snapshot, positions })!;

    private static bool CopyGeometry(GH_Component component, PanelSnapshot snapshot, int[] positions, out string status)
    {
        var arguments = new object?[] { snapshot, positions, null };
        bool copied = (bool)Method(component, "TryCopyGeometry").Invoke(component, arguments)!;
        status = (string)arguments[2]!;
        return copied;
    }

    private static File3dm ReadClipboardArchive() =>
        File3dm.FromByteArray(ReadClipboardBytes()) ?? throw new InvalidOperationException("Invalid geometry clipboard archive");

    private static bool IsExpectedClipboardPoint(byte[] bytes)
    {
        using var file = File3dm.FromByteArray(bytes);
        if (file == null || file.Objects.Count != 1 || file.Settings.ModelUnitSystem != UnitSystem.Millimeters) return false;
        // Enumerate the table: Rhino 8's IList indexer uses component indices
        // that object tables do not have. LINQ Single's IList fast path returns null.
        return file.Objects.Select(item => item.Geometry).Single() is Rhino.Geometry.Point point &&
            point.Location == new Point3d(20, 30, 40);
    }

    private static byte[] ReadClipboardBytes() => ReadClipboardBytes(out _);

    private static byte[] ReadClipboardBytes(out string transport)
    {
        var data = Forms.Clipboard.GetData(GeometryFormat);
        // An in-process WPF/Eto data object can expose its original byte array;
        // native providers expose the same payload as a clipboard stream.
        byte[] bytes;
        if (data is byte[] array) bytes = array;
        else if (data is Stream input)
        {
            using var output = new MemoryStream();
            input.Position = 0;
            input.CopyTo(output);
            bytes = output.ToArray();
        }
        else throw new InvalidOperationException("Unexpected Rhino clipboard payload: " + (data?.GetType().FullName ?? "null"));
        int header = Encoding.ASCII.GetString(bytes).IndexOf("3D Geometry File Format ", StringComparison.Ordinal);
        transport = $"{data.GetType().Name}, {bytes.Length} bytes, 3DM header at {header}, {ClipboardState()}";
        Check(bytes.Length > 0, "Clipboard exposes a binary native Rhino geometry payload");
        return bytes;
    }

    internal static string ClipboardState()
    {
        IntPtr owner = GetClipboardOwner();
        GetWindowThreadProcessId(owner, out uint processId);
        return $"sequence {GetClipboardSequenceNumber()}, owner PID {processId}";
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private static string ClipboardText() => Forms.Clipboard.GetText();
    private static PanelSnapshot Snapshot(object session) => Property<PanelSnapshot>(session, "Snapshot");
    private static PanelQueryResult Result(object session) => Property<PanelQueryResult>(session, "Result");
    private static void Query(object session, PanelQueryOptions options) => Method(session, "UpdateQuery").Invoke(session, new object[] { options });
    private static MethodInfo Method(object owner, string name) => owner.GetType().GetMethod(name, Instance)!;
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static void Select(object selection, PanelQueryResult result, int position, bool toggle = false) =>
        Method(selection, "Select").Invoke(selection, new object[] { result, position, toggle, false, false });
    private static IReadOnlyList<int> Selected(object selection, PanelQueryResult result) =>
        (IReadOnlyList<int>)Method(selection, "OrderedPositions").Invoke(selection, new object[] { result })!;

    private static void Paint(GH_Component component, GH_Canvas canvas)
    {
        canvas.Viewport.Size = canvas.Size;
        canvas.Viewport.Target = DrawingPoint.Empty;
        canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(bitmap);
        canvas.Viewport.ApplyProjection(graphics);
        PanelTestAdapter.Render(component, canvas, graphics);
    }

    private static bool InsideFrustum(Rhino.Display.RhinoViewport viewport, params Point3d[] points)
    {
        // IsVisible relies on native display state that remains unavailable for
        // the hidden smoke window, even after a redraw and UI message pump.
        // Check both screen bounds and near/far depth against the actual camera.
        if (viewport.Size.Width <= 0 || viewport.Size.Height <= 0 ||
            !viewport.GetFrustum(out _, out _, out _, out _, out double near, out double far)) return false;
        return points.All(point =>
        {
            var screen = viewport.WorldToClient(point);
            return screen.X > 0 && screen.X < viewport.Size.Width && screen.Y > 0 && screen.Y < viewport.Size.Height &&
                viewport.GetDepth(point, out double depth) && depth > near && depth < far;
        });
    }

    private static void SaveMenuPreview(GH_Component component, GH_Canvas canvas)
    {
        var bounds = component.Attributes.Bounds;
        using var bitmap = new Bitmap((int)(bounds.Width + 30) * 2, (int)(bounds.Height + 30) * 2);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(239, 240, 237));
        graphics.ScaleTransform(2, 2);
        graphics.TranslateTransform(15 - bounds.X, 15 - bounds.Y);
        PanelTestAdapter.Render(component, canvas, graphics);
        string path = Path.Combine(Path.GetDirectoryName(component.GetType().Assembly.Location)!, "rhino-smoke-row-actions.png");
        bitmap.Save(path);
        Console.WriteLine("Actual multirow geometry action menu preview: " + path);
    }

    private static void MouseDown(GH_Component component, GH_Canvas canvas, PointF point, int clicks) =>
        component.Attributes.RespondToMouseDown(canvas,
            new GH_CanvasMouseEvent(DrawingPoint.Empty, point, Forms.MouseButtons.Left, clicks, 0));

    private static void TrailingDoubleClick(GH_Component component, GH_Canvas canvas, PointF point)
    {
        MouseDown(component, canvas, point, 2);
        component.Attributes.RespondToMouseDoubleClick(canvas,
            new GH_CanvasMouseEvent(DrawingPoint.Empty, point, Forms.MouseButtons.Left, 2, 0));
    }

    private static void Click(GH_Component component, GH_Canvas canvas, RectangleF bounds, Forms.MouseButtons button = Forms.MouseButtons.Left)
    {
        var point = new PointF(bounds.X + Math.Min(45, bounds.Width / 2), bounds.Y + bounds.Height / 2);
        var mouse = new GH_CanvasMouseEvent(DrawingPoint.Empty, point, button, 1, 0);
        Check(component.Attributes.RespondToMouseDown(canvas, mouse) == GH_ObjectResponse.Handled, "Row action handles a native canvas mouse event");
    }

    private static async Task WaitForQuery(object session)
    {
        await WaitUntil(() => !Property<bool>(session, "IsBusy"));
        Check(Property<object?>(session, "Error") == null, "Row action query completed without errors");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed.TotalSeconds > 15) throw new TimeoutException("Native row action did not complete");
            await Task.Delay(10);
        }
    }

    private static void Check(bool condition, string description) => SmokeAssertions.Check(condition, description);
}
