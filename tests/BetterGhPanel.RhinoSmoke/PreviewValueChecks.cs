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
        Check(PropertyValue<PanelDotSize>(component, "DotSize") == PanelDotSize.Medium && PropertyValue<int>(component, "PreviewLimit") == 5000,
            "New panels default to Medium dots and a 5,000 item preview limit");
        var oldArchive = new GH_IO.Serialization.GH_LooseChunk("Panel");
        component.Write(oldArchive); oldArchive.RemoveItem("PanelDotSize"); oldArchive.RemoveItem("PanelPreviewLimit");
        var legacy = (GH_Component)Activator.CreateInstance(component.GetType())!;
        legacy.CreateAttributes();
        Check(legacy.Read(oldArchive) && PropertyValue<PanelDotSize>(legacy, "DotSize") == PanelDotSize.Medium &&
            PropertyValue<int>(legacy, "PreviewLimit") == 5000, "Old files default to Medium dots and a 5,000 item preview limit");
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
            var archive = new GH_IO.Serialization.GH_Archive();
            Check(archive.AppendObject(component, "Panel"), "Value-preview settings serialize through the component archive");
            var restored = (Grasshopper.Kernel.GH_Component)Activator.CreateInstance(component.GetType())!;
            restored.CreateAttributes();
            Check(archive.ExtractObject(restored, "Panel") && PropertyValue<bool>(restored, "ShowValues") &&
                PropertyValue<PanelValuePalette>(restored, "ValuePalette") == PanelValuePalette.Viridis &&
                PropertyValue<PanelDotPlacement>(restored, "DotPlacement") == PanelDotPlacement.BoxTop &&
                PropertyValue<PanelDotSize>(restored, "DotSize") == PanelDotSize.Large && PropertyValue<int>(restored, "PreviewLimit") == 20000,
                "Value-preview settings round trip with the file");
            Call(component, "ResetAll");
            Check(PropertyValue<bool>(component, "ShowValues") && PropertyValue<PanelValuePalette>(component, "ValuePalette") == PanelValuePalette.Viridis &&
                PropertyValue<PanelDotPlacement>(component, "DotPlacement") == PanelDotPlacement.BoxTop &&
                PropertyValue<PanelDotSize>(component, "DotSize") == PanelDotSize.Large && PropertyValue<int>(component, "PreviewLimit") == 20000, "Reset all keeps preview-value settings");
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
                (Func<int, PanelPreviewValue>)Sample, (Func<bool>)(() => true));
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
            Check(sampled.Take(2).SequenceEqual(new[] { 0, half }) && FieldValue<int>(labels, "_cursor") < total &&
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
        var clip = viewport.GetTransform(Rhino.DocObjects.CoordinateSystem.World, Rhino.DocObjects.CoordinateSystem.Clip);
        var screen = viewport.GetTransform(Rhino.DocObjects.CoordinateSystem.World, Rhino.DocObjects.CoordinateSystem.Screen);
        var model = Transform.Translation(new Vector3d(1, 2, 0));
        foreach (var point in new[] { origin, origin + new Vector3d(4, 3, 0), origin + new Vector3d(100000, 0, 0),
            viewport.CameraLocation + viewport.CameraDirection * -10 })
        {
            var world = model * point;
            var expected = viewport.WorldToClient(world);
            bool visible = viewport.IsVisible(world) && expected.X >= 0 && expected.Y >= 0 &&
                expected.X < viewport.Size.Width && expected.Y < viewport.Size.Height;
            object?[] args = { clip * model, screen * model, point, viewport.Size, null };
            bool actual = (bool)cached.Invoke(null, args)!;
            Check(actual == visible, "cached clip projection keeps native viewport visibility including points behind the camera");
            if (actual) Check(((Point2d)args[4]!).DistanceTo(expected) < 1e-7,
                "one-per-frame projection matrices match native WorldToClient with a model transform");
        }

    }

    // Component callbacks normally run before foreground labels. The older preview
    // safety conduit intentionally draws in Foreground to probe native lifetimes.
    private sealed class ValuePreviewConduit : DisplayConduit, IDisposable
    {
        private readonly GH_Component _component;
        private readonly GH_Document _document;
        private readonly DisplayMaterial _normal = new DisplayMaterial(Color.Red), _selected = new DisplayMaterial(Color.Lime);
        internal ValuePreviewConduit(GH_Component component, GH_Document document) { _component = component; _document = document; }
        protected override void PostDrawObjects(DrawEventArgs e)
        {
            var args = new ValuePreviewArguments(_document, e, _normal, _selected);
            _component.DrawViewportMeshes(args);
            _component.DrawViewportWires(args);
        }
        public void Dispose() { Enabled = false; _normal.Dispose(); _selected.Dispose(); }
    }
    private sealed class ValuePreviewArguments : IGH_PreviewArgs
    {
        private readonly DrawEventArgs _args;
        internal ValuePreviewArguments(GH_Document document, DrawEventArgs args, DisplayMaterial normal, DisplayMaterial selected)
        { Document = document; _args = args; ShadeMaterial = normal; ShadeMaterial_Selected = selected; }
        public int DefaultCurveThickness => 3;
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
