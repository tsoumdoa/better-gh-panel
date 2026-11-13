using static PanelTestAdapter;
using static SmokeAssertions;
using System;
using System.Drawing;
using System.IO;
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
