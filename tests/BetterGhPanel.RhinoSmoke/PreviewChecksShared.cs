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
    private static object Copy(Assembly assembly, IEnumerable<IGH_Goo?> values) => assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!
        .GetMethod("CopyGeometry", FlagsNonPublicPublicStatic)!.Invoke(null, new object[] { values, RhinoDoc.ActiveDoc.ModelUnitSystem, RhinoDoc.ActiveDoc.ModelAbsoluteTolerance })!;
    private static File3dm ClipboardArchive()
    {
        object data = Forms.Clipboard.GetData("Rhino 8 3DM Geometry")!;
        byte[] bytes;
        if (data is byte[] array) bytes = array;
        else if (data is Stream input)
        {
            using var output = new MemoryStream(); input.Position = 0; input.CopyTo(output); bytes = output.ToArray();
        }
        else throw new InvalidOperationException("Native geometry clipboard has no byte payload.");
        return File3dm.FromByteArray(bytes) ?? throw new InvalidOperationException("Invalid geometry clipboard archive.");
    }
    private static object Frame(Assembly assembly, IEnumerable<IGH_Goo?> values) => assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!
        .GetMethod("Frame", FlagsNonPublicPublicStatic)!.Invoke(null, new object[] { values })!;
    private static BoundingBox Bounds(Assembly assembly, IGH_Goo value) => (BoundingBox)assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!
        .GetMethod("GetBounds", FlagsNonPublicPublicStatic)!.Invoke(null, new object[] { value })!;
    private static void Set(object highlight, IGH_Goo?[] values, int[] positions, int[] selected) =>
        Method(highlight, "Set").Invoke(highlight, new object[] { values, positions, selected });
    private static void Scope(object highlight, IGH_Goo?[] values, PanelQueryResult result, int[] selected) =>
        Method(highlight, "SetScope").Invoke(highlight, new object[] { values, result, true, selected });
    private static BoundingBox Box(object highlight) => ReflectProperty403875335<BoundingBox>(highlight, "ClippingBox");
    private static int Count(object highlight) => ReflectProperty403875335<int>(highlight, "PreviewCount");
    private static string Status(object highlight) => ReflectProperty403875335<string>(highlight, "Status");
    private static int GreenPixels(Bitmap bitmap)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
        { var color = bitmap.GetPixel(x, y); if (color.G > color.R + 40 && color.G > color.B + 40) count++; }
        return count;
    }
    private static bool RegionColors(Bitmap bitmap, Rhino.Display.RhinoViewport viewport, BoundingBox bounds, Color shade, Color wire)
    {
        var p = viewport.WorldToClient(new Point3d(bounds.Min.X, bounds.Min.Y, bounds.Max.Z));
        var q = viewport.WorldToClient(new Point3d(bounds.Max.X, bounds.Max.Y, bounds.Max.Z));
        var size = viewport.Size;
        double sx = bitmap.Width / (double)Math.Max(1, size.Width), sy = bitmap.Height / (double)Math.Max(1, size.Height);
        int x0 = Math.Max(0, (int)(Math.Min(p.X, q.X) * sx) - 5), x1 = Math.Min(bitmap.Width, (int)(Math.Max(p.X, q.X) * sx) + 6);
        int y0 = Math.Max(0, (int)(Math.Min(p.Y, q.Y) * sy) - 5), y1 = Math.Min(bitmap.Height, (int)(Math.Max(p.Y, q.Y) * sy) + 6);
        bool Matches(Color c, Color target) => target == Color.Blue ? c.B > c.R + 15 && c.B > c.G + 15 :
            target == Color.Yellow ? c.R > c.B + 15 && c.G > c.B + 15 :
            target == Color.Magenta ? c.R > c.G + 15 && c.B > c.G + 15 :
            target == Color.Red ? c.R > c.G + 25 && c.R > c.B + 25 :
            target == Color.Lime ? c.G > c.R + 25 && c.G > c.B + 25 : c.G > c.R + 15 && c.B > c.R + 15;
        int shadePixels = 0, wirePixels = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
        { var color = bitmap.GetPixel(x, y); if (Matches(color, shade)) shadePixels++; if (Matches(color, wire)) wirePixels++; }
        Console.WriteLine($"Preview region {bounds}: {shade.Name}={shadePixels}, {wire.Name}={wirePixels}, pixels={x0},{y0}-{x1},{y1}");
        return shadePixels > 20 && wirePixels > 2;
    }
    private static bool RegionHasColor(Bitmap bitmap, Rhino.Display.RhinoViewport viewport, BoundingBox bounds, Color target)
    {
        var p = viewport.WorldToClient(bounds.Min); var q = viewport.WorldToClient(bounds.Max); var size = viewport.Size;
        double sx = bitmap.Width / (double)Math.Max(1, size.Width), sy = bitmap.Height / (double)Math.Max(1, size.Height);
        int x0 = Math.Max(0, (int)(Math.Min(p.X, q.X) * sx) - 4), x1 = Math.Min(bitmap.Width, (int)(Math.Max(p.X, q.X) * sx) + 5);
        int y0 = Math.Max(0, (int)(Math.Min(p.Y, q.Y) * sy) - 4), y1 = Math.Min(bitmap.Height, (int)(Math.Max(p.Y, q.Y) * sy) + 5);
        int count = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
        {
            var c = bitmap.GetPixel(x, y);
            bool match = target == Color.Lime ? c.G > c.R + 25 && c.G > c.B + 25 :
                target == Color.Red ? c.R > c.G + 25 && c.R > c.B + 25 : c.G > c.R + 25 && c.B > c.R + 25;
            if (match) count++;
        }
        Console.WriteLine($"Compatibility region {bounds}: {target.Name}={count}, pixels={x0},{y0}-{x1},{y1}");
        return count > 10;
    }
    private static MethodInfo Method(object value, string name) => value.GetType().GetMethod(name, FlagsInstanceNonPublicPublic)!;

    private sealed class HostileLineCurve : LineCurve
    {
        internal static int Calls;
        public override bool IsValid { get { Calls++; throw new InvalidOperationException("Unexpected hostile validity"); } }
    }

    private sealed class HostileCurveHolder : GH_Curve
    {
        internal static int Calls;
        public override Curve Value { get { Calls++; throw new InvalidOperationException(); } set { Calls++; throw new InvalidOperationException(); } }
        public override bool IsValid { get { Calls++; throw new InvalidOperationException(); } }
        public override object ScriptVariable() { Calls++; throw new InvalidOperationException(); }
    }

    private sealed class StandalonePointPreview : GH_Point, IGH_PreviewData
    {
        internal int BoundsCalls;
        internal Action? OnBounds;
        internal Action? OnWire, OnMesh;
        BoundingBox IGH_PreviewData.ClippingBox
        {
            get
            {
                BoundsCalls++;
                var callback = OnBounds; OnBounds = null; callback?.Invoke();
                return new BoundingBox(new Point3d(7, 0, 0), new Point3d(7, 0, 0));
            }
        }
        void IGH_PreviewData.DrawViewportWires(GH_PreviewWireArgs args)
        { var callback = OnWire; OnWire = null; callback?.Invoke(); }
        void IGH_PreviewData.DrawViewportMeshes(GH_PreviewMeshArgs args)
        { var callback = OnMesh; OnMesh = null; callback?.Invoke(); }
    }

    private sealed class ColoredPreviewArgs : IGH_PreviewArgs
    {
        private readonly IGH_PreviewArgs _inner;
        internal ColoredPreviewArgs(IGH_PreviewArgs inner, Rhino.Display.DisplayMaterial material, Color wire)
        { _inner = inner; ShadeMaterial = ShadeMaterial_Selected = material; WireColour = WireColour_Selected = wire; }
        public int DefaultCurveThickness => _inner.DefaultCurveThickness;
        public Color WireColour { get; }
        public Color WireColour_Selected { get; }
        public Rhino.Display.DisplayMaterial ShadeMaterial { get; }
        public Rhino.Display.DisplayMaterial ShadeMaterial_Selected { get; }
        public Rhino.Display.RhinoViewport Viewport => _inner.Viewport;
        public Rhino.Display.DisplayPipeline Display => _inner.Display;
        public GH_Document Document => _inner.Document;
        public MeshingParameters MeshingParameters => _inner.MeshingParameters;
    }

    private sealed class PreviewConduit : Rhino.Display.DisplayConduit, IDisposable
    {
        private readonly GH_Component _component;
        private readonly GH_Document _document;
        private readonly Rhino.Display.DisplayMaterial _normal;
        private readonly Rhino.Display.DisplayMaterial _selected;
        internal PreviewConduit(GH_Component component, GH_Document document, Color? normalShade = null, Color? selectedShade = null)
        {
            _component = component; _document = document;
            _normal = new Rhino.Display.DisplayMaterial(normalShade ?? Color.Red);
            _selected = new Rhino.Display.DisplayMaterial(selectedShade ?? Color.Lime);
        }
        internal int Draws;
        internal int MeshesStarted, MeshesCompleted, WiresStarted, WiresCompleted;
        internal Action<IGH_PreviewArgs>? OnDraw;
        protected override void DrawForeground(Rhino.Display.DrawEventArgs e)
        {
            Draws++;
            var args = new Arguments(_document, e, _normal, _selected);
            if (OnDraw != null) { var callback = OnDraw; OnDraw = null; callback(args); return; }
            MeshesStarted++; _component.DrawViewportMeshes(args); MeshesCompleted++;
            WiresStarted++; _component.DrawViewportWires(args); WiresCompleted++;
        }
        public void Dispose() { Enabled = false; _normal.Dispose(); _selected.Dispose(); }

        private sealed class Arguments : IGH_PreviewArgs
        {
            private readonly Rhino.Display.DrawEventArgs _args;
            internal Arguments(GH_Document document, Rhino.Display.DrawEventArgs args,
                Rhino.Display.DisplayMaterial normal, Rhino.Display.DisplayMaterial selected)
            { Document = document; _args = args; ShadeMaterial = normal; ShadeMaterial_Selected = selected; }
            public int DefaultCurveThickness => 3;
            public Color WireColour => Color.Red;
            public Color WireColour_Selected => Color.Lime;
            public Rhino.Display.DisplayMaterial ShadeMaterial { get; }
            public Rhino.Display.DisplayMaterial ShadeMaterial_Selected { get; }
            public Rhino.Display.RhinoViewport Viewport => _args.Viewport;
            public Rhino.Display.DisplayPipeline Display => _args.Display;
            public GH_Document Document { get; }
            public MeshingParameters MeshingParameters => Document.PreviewCurrentMeshParameters();
        }
    }

    private sealed class StockCurveConduit : Rhino.Display.DisplayConduit, IDisposable
    {
        internal Action<Rhino.Display.DisplayPipeline>? OnDraw;
        protected override void DrawForeground(Rhino.Display.DrawEventArgs e)
        { var callback = OnDraw; OnDraw = null; callback?.Invoke(e.Display); }
        public void Dispose() { Enabled = false; }
    }
}
