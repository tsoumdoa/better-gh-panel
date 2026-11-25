using static PanelTestAdapter;
using static SmokeAssertions;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

internal static partial class PreviewSafetyChecks
{
    private static async Task VerifyValueDots(Assembly assembly, RhinoDoc rhino)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        VerifyProgressiveDotValues(assembly);
        var component = fixture.Component;
        Check(PropertyValue<PanelDotSize>(component, "DotSize") == PanelDotSize.Medium && PropertyValue<int>(component, "PreviewLimit") == 5000 &&
            PropertyValue<bool>(component, "HideOccludedDots"), "New panels default to Medium dots, a 5,000 item preview limit and occlusion");
        var oldArchive = new GH_IO.Serialization.GH_LooseChunk("Panel");
        component.Write(oldArchive); oldArchive.RemoveItem("PanelDotSize"); oldArchive.RemoveItem("PanelPreviewLimit");
        oldArchive.RemoveItem("PanelHideOccludedDots");
        var legacy = (GH_Component)Activator.CreateInstance(component.GetType())!;
        legacy.CreateAttributes();
        Check(legacy.Read(oldArchive) && PropertyValue<PanelDotSize>(legacy, "DotSize") == PanelDotSize.Medium &&
            PropertyValue<int>(legacy, "PreviewLimit") == 5000 && PropertyValue<bool>(legacy, "HideOccludedDots"),
            "Old files default to Medium dots, a 5,000 item preview limit and occlusion");
        fixture.Source.Hidden = fixture.Paired.Hidden = true;
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        var path = new GH_Path(0);
        var origin = new Point3d(10000, 10000, 10000);
        Point3d P(double x, double y, double z) => origin + new Vector3d(x, y, z);
        fixture.Source.PersistentData.Append(new GH_Line(new Line(P(-8, 0, 0), P(-2, 5, 2))), path);
        fixture.Source.PersistentData.Append(new GH_Line(new Line(P(-7, 8, 0), P(3, 8, 3))), path);
        using var surface = new PlaneSurface(new Plane(origin, Vector3d.ZAxis), new Interval(1, 6), new Interval(0, 5));
        fixture.Source.PersistentData.Append(new GH_Surface(surface), path);
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 3);
        var previousView = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Panel+ value dots review", DefinedViewportProjection.Perspective, new Rectangle(0, 0, 1000, 720), false);
        try
        {
            rhino.Views.ActiveView = view;
            using var conduit = new ValuePreviewConduit(component, fixture.Document) { Enabled = true };
            PreviewMode(component, "FilteredResults");
            Call(component, "SetShowValues", true);
            Call(component, "HighlightRows", fixture.Result.Snapshot, Array.Empty<int>());
            var highlight = FieldValue<object>(component, "_highlight");
            var labels = FieldValue<object>(highlight, "_orderLabels");
            await fixture.Ready(() => FieldValue<Array>(labels, "_legend").Length == 2);
            Check(PropertyValue<bool>(component, "ShowValues"), "Value dots are enabled through the real component setting");
            view.ActiveViewport.SetProjection(DefinedViewportProjection.Perspective, "Value dots", true);
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(P(-9, -2, -1), P(8, 11, 5)));
            VerifyDotProjection(assembly, view.ActiveViewport, origin);
            string? directory = Environment.GetEnvironmentVariable("PANEL_UX_SCREENSHOTS");
            void SaveViewport(string name)
            {
                if (string.IsNullOrWhiteSpace(directory)) return;
                Directory.CreateDirectory(directory);
                view.Redraw();
                using var image = DisplayPipeline.DrawToBitmap(view.ActiveViewport, 1000, 720);
                image.Save(Path.Combine(directory, name));
            }
            SaveViewport("value-dots-curves-surfaces.png");
            Call(component, "SetValuePalette", PanelValuePalette.Viridis);
            fixture.Select(0);
            Call(component, "HighlightRows", fixture.Result.Snapshot, new[] { 0 });
            await fixture.Ready(() => (int)Call(labels, "ColourId", 0)! > 0);
            int colour = (int)Call(labels, "ColourId", 0)!;
            fixture.Select(1);
            Call(component, "HighlightRows", fixture.Result.Snapshot, new[] { 1 });
            Check((int)Call(labels, "ColourId", 0)! == colour, "Changing selection does not change the all-Results gradient range");
            SaveViewport("value-dots-gradient-selection.png");
            Call(component, "SetDotPlacement", PanelDotPlacement.BoxTop);
            SaveViewport("value-dots-box-top.png");

            Call(component, "SetDotSize", PanelDotSize.Large);
            fixture.Document.UndoServer.PerformUndo();
            Check(PropertyValue<PanelDotSize>(component, "DotSize") == PanelDotSize.Medium, "Dot size supports Undo");
            fixture.Document.UndoServer.PerformRedo();
            Check(PropertyValue<PanelDotSize>(component, "DotSize") == PanelDotSize.Large, "Dot size supports Redo");
            Call(component, "SetPreviewLimit", 20000);
            fixture.Document.UndoServer.PerformUndo();
            Check(PropertyValue<int>(component, "PreviewLimit") == 5000, "Preview limit supports Undo");
            fixture.Document.UndoServer.PerformRedo();
            Check(PropertyValue<int>(component, "PreviewLimit") == 20000, "Preview limit supports Redo");
            Call(component, "SetHideOccludedDots", false);
            fixture.Document.UndoServer.PerformUndo();
            Check(PropertyValue<bool>(component, "HideOccludedDots"), "Hiding occluded dots supports Undo");
            fixture.Document.UndoServer.PerformRedo();
            var archive = new GH_IO.Serialization.GH_Archive();
            Check(archive.AppendObject(component, "Panel"), "Value-preview settings serialize through the component archive");
            var restored = (Grasshopper.Kernel.GH_Component)Activator.CreateInstance(component.GetType())!;
            restored.CreateAttributes();
            Check(archive.ExtractObject(restored, "Panel") && PropertyValue<bool>(restored, "ShowValues") &&
                PropertyValue<PanelValuePalette>(restored, "ValuePalette") == PanelValuePalette.Viridis &&
                PropertyValue<PanelDotPlacement>(restored, "DotPlacement") == PanelDotPlacement.BoxTop &&
                PropertyValue<PanelDotSize>(restored, "DotSize") == PanelDotSize.Large && PropertyValue<int>(restored, "PreviewLimit") == 20000 &&
                !PropertyValue<bool>(restored, "HideOccludedDots"),
                "Value-preview settings round trip with the file");
            Call(component, "ResetAll");
            Check(PropertyValue<bool>(component, "ShowValues") && PropertyValue<PanelValuePalette>(component, "ValuePalette") == PanelValuePalette.Viridis &&
                PropertyValue<PanelDotPlacement>(component, "DotPlacement") == PanelDotPlacement.BoxTop &&
                PropertyValue<PanelDotSize>(component, "DotSize") == PanelDotSize.Large && PropertyValue<int>(component, "PreviewLimit") == 20000 &&
                !PropertyValue<bool>(component, "HideOccludedDots"), "Reset all keeps preview-value settings");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                using var canvas = new GH_Canvas { Document = fixture.Document, Size = new Size(1100, 850) };
                var menuKind = assembly.GetType("BetterGhPanel.UI.CanvasMenuKind", true)!;
                void SaveMenu(string page, string name)
                {
                    Call(component.Attributes, "OpenMenu", Enum.Parse(menuKind, page), canvas);
                    component.Attributes.PerformLayout();
                    using var image = new Bitmap(900, 700);
                    using var graphics = Graphics.FromImage(image);
                    graphics.Clear(Color.FromArgb(238, 238, 238));
                    Render(component, canvas, graphics);
                    image.Save(Path.Combine(directory, name));
                    Call(FieldValue<object>(component.Attributes, "_menu"), "Close");
                }
                SaveMenu("Preview", "value-dots-preview-menu.png");
                SaveMenu("ValuePalette", "value-dots-palette-list.png");
                SaveMenu("DotSize", "value-dots-size-list.png");
                SaveMenu("PreviewLimit", "value-dots-limit-list.png");
            }
            PreviewMode(component, "Off");
            Check(PropertyValue<int>(highlight, "PreviewCount") == 0, "Preview Off clears value dots and the legend owner");
        }
        finally { if (previousView != null) rhino.Views.ActiveView = previousView; view.Close(); }
        await VerifySolidDotOcclusion(assembly, rhino);
        await VerifyDotDepthPrecision(assembly, rhino);
    }

    private static async Task VerifySolidDotOcclusion(Assembly assembly, RhinoDoc rhino)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        var component = fixture.Component;
        fixture.Document.PreviewMode = GH_PreviewMode.Shaded;
        fixture.Source.Hidden = fixture.Paired.Hidden = true;
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        var path = new GH_Path(0);
        fixture.Source.PersistentData.Append(new GH_Line(new Line(new Point3d(-15, -5, 0), new Point3d(-7, -5, 0))), path);
        fixture.Source.PersistentData.Append(new GH_Point(new Point3d(8, 0, 0)), path);
        fixture.Source.PersistentData.Append(new GH_Box(new Box(new BoundingBox(new Point3d(-9, -2, -2), new Point3d(-4, 3, 2)))), path);
        fixture.Source.PersistentData.Append(new GH_Box(new Box(new BoundingBox(new Point3d(6, -2, -2), new Point3d(10, 2, 1)))), path);
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 4);

        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Panel+ solid dot occlusion", DefinedViewportProjection.Perspective, new Rectangle(0, 0, 1000, 720), false);
        using var cover = Mesh.CreateFromBox(new BoundingBox(new Point3d(4, -6, 4), new Point3d(12, 6, 8)), 1, 1, 1);
        var coverId = rhino.Objects.AddMesh(cover);
        try
        {
            rhino.Views.ActiveView = view;
            var bounds = new BoundingBox(new Point3d(-16, -8, -2), new Point3d(13, 8, 8));
            using var conduit = new ValuePreviewConduit(component, fixture.Document) { Enabled = true, WireThickness = 8, Bounds = bounds };
            PreviewMode(component, "FilteredResults");
            ShowItemOrder(component, true);
            fixture.Select(0);
            Call(component, "HighlightRows", fixture.Result.Snapshot, new[] { 0 });
            var labels = FieldValue<object>(FieldValue<object>(component, "_highlight"), "_orderLabels");
            await fixture.Ready(() => FieldValue<System.Collections.IDictionary>(labels, "_labels").Count == 4);

            var viewport = view.ActiveViewport;
            viewport.SetCameraLocations(Point3d.Origin, new Point3d(0, -14, 60));
            viewport.ZoomBoundingBox(bounds);
            var shaded = DisplayModeDescription.FindByName("Shaded");
            viewport.DisplayMode = shaded;
            string? directory = Environment.GetEnvironmentVariable("PANEL_UX_SCREENSHOTS");
            int[] Capture(string name, bool expectDepth) => CaptureDotFrame(view, shaded, labels, expectDepth, directory, name);

            var centre = Capture("solid-centre.png", true);
            Check(centre.Contains(0) && !centre.Contains(1), "Depth excludes a covered GH point and retains the selected thick wire's own dot");
            Check(centre.Contains(2) && !centre.Contains(3),
                $"A shaded GH box retains its centre dot while a covered box loses its dot (visible positions {string.Join(", ", centre)})");
            Call(component, "SetDotPlacement", PanelDotPlacement.BoxBottom);
            var bottom = Capture("solid-bottom.png", true);
            Check(bottom.Contains(2) && !bottom.Contains(3), "The visible box retains its Box bottom dot");
            Call(component, "SetHideOccludedDots", false);
            var through = Capture("solid-behind-box-hide-off.png", false);
            Check(through.Contains(1) && through.Contains(3), "The covered GH point and box reappear with occlusion off");
        }
        finally
        {
            rhino.Objects.Delete(coverId, true);
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close();
        }
    }

    private static int[] CaptureDotFrame(RhinoView view, DisplayModeDescription mode, object labels,
        bool expectDepth, string? directory = null, string? name = null)
    {
        var id = view.ActiveViewport.Id;
        var cache = FieldValue<System.Collections.IDictionary>(labels, "_frames");
        cache.Remove((id, true));
        cache.Remove((id, false));
        using var image = view.CaptureToBitmap(new Size(1000, 720), mode);
        var frame = cache[(id, true)] ?? cache[(id, false)];
        int projected = frame == null ? 0 : FieldValue<int>(frame, "ProjectedCount");
        bool? hadDepth = frame == null ? null : FieldValue<bool>(frame, "HadDepth");
        Check(image != null && frame != null && projected > 0 && hadDepth == expectDepth,
            $"A fresh shaded capture projects dots and uses the expected depth path (projected={projected}, depth={hadDepth}, expected={expectDepth})");
        if (!string.IsNullOrWhiteSpace(directory) && name != null)
        {
            Directory.CreateDirectory(directory);
            image!.Save(Path.Combine(directory, name));
        }
        return FieldValue<Array>(frame!, "Labels").Cast<object>().Take(FieldValue<int>(frame!, "Count"))
            .Select(label => FieldValue<int>(label, "Position")).ToArray();
    }

    private static async Task VerifyDotDepthPrecision(Assembly assembly, RhinoDoc rhino)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        fixture.Document.PreviewMode = GH_PreviewMode.Shaded;
        fixture.Source.Hidden = fixture.Paired.Hidden = true;
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        var path = new GH_Path(0);
        var plane = new Plane(new Point3d(10, 0, .5678), Vector3d.ZAxis);
        using var surface = Mesh.CreateFromPlane(plane, new Interval(-4, 4), new Interval(-4, 4), 2, 2);
        fixture.Source.PersistentData.Append(new GH_Point(new Point3d(-10, 0, .1234)), path);
        fixture.Source.PersistentData.Append(new GH_Mesh(surface), path);
        fixture.Source.PersistentData.Append(new GH_Point(plane.Origin - Vector3d.ZAxis), path);
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 3);

        var previous = rhino.Views.ActiveView;
        var units = rhino.ModelUnitSystem;
        double tolerance = rhino.ModelAbsoluteTolerance;
        var view = rhino.Views.Add("Panel+ dot precision", DefinedViewportProjection.Top, new Rectangle(0, 0, 1000, 720), false);
        try
        {
            rhino.ModelUnitSystem = UnitSystem.Millimeters;
            rhino.ModelAbsoluteTolerance = .001;
            rhino.Views.ActiveView = view;
            var bounds = new BoundingBox(new Point3d(-15, -5, -2), new Point3d(15, 5, 2));
            using var conduit = new ValuePreviewConduit(fixture.Component, fixture.Document) { Enabled = true, Bounds = bounds };
            PreviewMode(fixture.Component, "FilteredResults");
            ShowItemOrder(fixture.Component, true);
            var viewport = view.ActiveViewport;
            var shaded = DisplayModeDescription.FindByName("Shaded");
            viewport.DisplayMode = shaded;
            viewport.ZoomBoundingBox(bounds);
            var labels = FieldValue<object>(FieldValue<object>(fixture.Component, "_highlight"), "_orderLabels");
            await fixture.Ready(() => FieldValue<System.Collections.IDictionary>(labels, "_labels").Count == 3);
            var wide = new BoundingBox(new Point3d(-15, -5, -25000), new Point3d(15, 5, 25000));
            foreach (var depthBounds in new[] { bounds, wide })
            {
                conduit.Bounds = depthBounds;
                foreach (double z in new[] { 48000.123, 49600.638, 50000.312, 52100.714 })
                {
                    viewport.SetCameraLocations(Point3d.Origin, new Point3d(0, 0, z));
                    var visible = CaptureDotFrame(view, shaded, labels, true);
                    Check(visible.Contains(0) && visible.Contains(1) && !visible.Contains(2),
                        $"The unselected point and Top-facing plane retain their dots at camera depth {z} and bounds {depthBounds.Diagonal.Z}; the point one unit behind stays hidden (visible: {string.Join(", ", visible)})");
                }
            }
            conduit.Bounds = bounds;
            foreach (double z in new[] { 999999.714, 1000000.123, 1000000.638 })
            {
                viewport.SetCameraLocations(Point3d.Origin, new Point3d(0, 0, z));
                var visible = CaptureDotFrame(view, shaded, labels, true);
                Check(visible.Contains(0) && visible.Contains(1) && !visible.Contains(2),
                    $"The point and Top-facing plane retain their dots at camera depth {z} (visible: {string.Join(", ", visible)})");
            }

            var survey = new Point3d(1e9, -1e9, 1e9);
            fixture.Source.PersistentData.Clear();
            fixture.Source.PersistentData.Append(new GH_Point(survey), path);
            fixture.Solve();
            await fixture.Ready(() => FieldValue<System.Collections.IDictionary>(labels, "_labels").Count == 1);
            var padding = new Vector3d(10, 10, 10);
            conduit.Bounds = new BoundingBox(survey - padding, survey + padding);
            viewport.SetProjection(DefinedViewportProjection.Perspective, "Survey", true);
            viewport.SetCameraLocations(survey, survey + new Vector3d(15, -30, 65));
            Check(CaptureDotFrame(view, shaded, labels, true).Contains(0),
                "The real viewport projects and retains the GH point at billion-unit coordinates with depth enabled");
        }
        finally
        {
            rhino.ModelUnitSystem = units;
            rhino.ModelAbsoluteTolerance = tolerance;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close();
        }
    }

    private static void VerifyProgressiveDotValues(Assembly assembly)
    {
        const int half = 5000, total = half * 2;
        var snapshot = new PanelSnapshot(Enumerable.Range(0, total).Select(i =>
            new PanelRow(i, i < half ? "{0}" : "{1}", "Number", "42", 42)).ToArray(),
            new[] { new PanelBranch("{0}", half), new PanelBranch("{1}", half) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        var membershipType = typeof(PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.PanelPreviewMembership", true)!;
        var membership = Activator.CreateInstance(membershipType, AnyInstance, null, new object?[] { null }, null)!;
        var type = assembly.GetType("BetterGhPanel.Runtime.PanelOrderLabels", true)!;
        var labels = Activator.CreateInstance(type, AnyInstance, null, new[] { membership }, null)!;
        var labelSource = Enum.Parse(typeof(PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.PanelLabelSource", true)!, "Data");
        var sampled = new List<int>(); bool changed = false;
        PanelPreviewValue Sample(int position)
        {
            sampled.Add(position); double number = changed ? position == half ? 84 : 0 : 42;
            return new PanelPreviewValue("Length", "m", number, number + " m");
        }
        void Configure(bool order, int decimals)
        {
            Call(labels, "Configure", result, order, true, PanelDotPlacement.Centre,
                PanelValuePalette.Viridis, false, PanelSearchMode.Value, decimals, 0d, new[] { 0, half },
                (Func<int, bool, PanelPreviewValue>)((position, _) => Sample(position)), (Func<bool>)(() => true), labelSource, false, false,
                Enum.Parse(labelSource.GetType().Assembly.GetType("BetterGhPanel.Core.PanelNoMatchStyle", true)!, "Grey"));
            // This probe advances ticks explicitly; native reentry must not also
            // advance the sampling timer while the fixture checks intermediate state.
            var timer = FieldValue<System.Windows.Forms.Timer>(labels, "_refresh");
            timer.Stop(); timer.Dispose(); type.GetField("_refresh", AnyInstance)!.SetValue(labels, null);
        }
        void Tick()
        {
            type.GetField("_lastPublish", AnyInstance)!.SetValue(labels, -250L);
            Call(labels, "RefreshValues", null, EventArgs.Empty);
        }
        var dots = FieldValue<System.Collections.IDictionary>(labels, "_labels");
        try
        {
            Configure(false, 2);
            Call(labels, "Add", 0, 0, Point3d.Origin, BoundingBox.Empty);
            Call(labels, "Add", half, half, new Point3d(1, 0, 0), BoundingBox.Empty);
            Check(FieldValue<string>(dots[0]!, "Text") == "…" && FieldValue<string>(dots[half]!, "Text") == "…" &&
                FieldValue<int>(dots[0]!, "GroupKey") != FieldValue<int>(dots[half]!, "GroupKey"), "pending value dots never share a group key");
            for (int tick = 0; tick < 4 && FieldValue<string>(dots[half]!, "Text") == "…"; tick++) Tick();
            Check(sampled.Take(2).SequenceEqual(new[] { 0, half }) && sampled.Count < total &&
                FieldValue<string>(dots[half]!, "Text") == "42 m", "admitted labels sample and publish before the all-Results colour scan completes");
            Check(FieldValue<System.Collections.IDictionary>(labels, "_ranges").Count == 0, "partial text publication does not publish partial colour ranges");
            Configure(true, 2);
            Check(FieldValue<string>(dots[0]!, "Text") == FieldValue<string>(dots[half]!, "Text") &&
                FieldValue<int>(dots[0]!, "GroupKey") != FieldValue<int>(dots[half]!, "GroupKey"), "equal item-order strings from different branches never group");
            Configure(false, 2);
            for (int tick = 0; tick < 1000; tick++)
            { Tick(); if (FieldValue<object?>(labels, "_buildingValues") == null) break; }
            var ranges = FieldValue<object>(labels, "_ranges"); int colour = (int)Call(labels, "ColourId", half)!;
            Check(FieldValue<object?>(labels, "_buildingValues") == null && colour > 0, "a full scan publishes the paired colour snapshot");
            changed = true; Configure(false, 3);
            for (int tick = 0; tick < 4 && FieldValue<string>(dots[half]!, "Text") != "84 m"; tick++) Tick();
            Check(FieldValue<string>(dots[half]!, "Text") == "84 m" && ReferenceEquals(ranges, FieldValue<object>(labels, "_ranges")) &&
                (int)Call(labels, "ColourId", half)! == colour, "early text refresh retains the last complete colour values and range");
        }
        finally { Call(labels, "Clear"); }
    }

    private static void VerifyDotProjection(Assembly assembly, RhinoViewport viewport, Point3d origin)
    {
        var labelsType = assembly.GetType("BetterGhPanel.Runtime.PanelOrderLabels", true)!;
        var cached = labelsType.GetMethod("TryProjectCached", AnyStatic)!;
        var clip = viewport.GetTransform(Rhino.DocObjects.CoordinateSystem.Camera, Rhino.DocObjects.CoordinateSystem.Clip);
        var camera = viewport.GetTransform(Rhino.DocObjects.CoordinateSystem.World, Rhino.DocObjects.CoordinateSystem.Camera);

        Point2d? Project(Transform view, Point3d point)
        {
            object?[] parameters = { clip, view * point, viewport.Size, null };
            return (bool)cached.Invoke(null, parameters)! ? (Point2d)parameters[3]! : null;
        }
        var model = Transform.Translation(new Vector3d(1, 2, 0));
        foreach (var point in new[] { origin, origin + new Vector3d(4, 3, 0), origin + new Vector3d(100000, 0, 0),
            viewport.CameraLocation + viewport.CameraDirection * -10 })
        {
            var world = model * point;
            var expected = viewport.WorldToClient(world);
            bool visible = viewport.IsVisible(world) && expected.X >= 0 && expected.Y >= 0 &&
                expected.X < viewport.Size.Width && expected.Y < viewport.Size.Height;
            var actual = Project(camera, world);
            Check(actual.HasValue == visible, "camera-space clip projection agrees with native viewport visibility");
            if (actual.HasValue) Check(actual.Value.DistanceTo(expected) < 1e-7,
                "camera-space projection matches native WorldToClient");
        }

    }

    // Component callbacks normally run before foreground labels. The older preview
    // safety conduit intentionally draws in Foreground to probe native lifetimes.
    private sealed class ValuePreviewConduit : DisplayConduit, IDisposable
    {
        private readonly GH_Component _component;
        private readonly GH_Document _document;
        private readonly DisplayMaterial _normal = new DisplayMaterial(Color.Red), _selected = new DisplayMaterial(Color.Lime);
        internal int WireThickness { get; set; } = 3;
        internal BoundingBox Bounds { get; set; } = BoundingBox.Empty;
        internal ValuePreviewConduit(GH_Component component, GH_Document document) { _component = component; _document = document; }
        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            if (Bounds.IsValid) e.IncludeBoundingBox(Bounds);
        }
        protected override void PostDrawObjects(DrawEventArgs e)
        {
            var args = new ValuePreviewArguments(_document, e, _normal, _selected, WireThickness);
            _component.DrawViewportMeshes(args);
            _component.DrawViewportWires(args);
        }
        public void Dispose() { Enabled = false; _normal.Dispose(); _selected.Dispose(); }
    }
    private sealed class ValuePreviewArguments : IGH_PreviewArgs
    {
        private readonly DrawEventArgs _args;
        internal ValuePreviewArguments(GH_Document document, DrawEventArgs args, DisplayMaterial normal, DisplayMaterial selected, int thickness)
        { Document = document; _args = args; ShadeMaterial = normal; ShadeMaterial_Selected = selected; DefaultCurveThickness = thickness; }
        public int DefaultCurveThickness { get; }
        public Color WireColour => Color.Red;
        public Color WireColour_Selected => Color.Lime;
        public DisplayMaterial ShadeMaterial { get; }
        public DisplayMaterial ShadeMaterial_Selected { get; }
        public RhinoViewport Viewport => _args.Viewport;
        public DisplayPipeline Display => _args.Display;
        public GH_Document Document { get; }
        public MeshingParameters MeshingParameters => Document.PreviewCurrentMeshParameters();
    }
}
