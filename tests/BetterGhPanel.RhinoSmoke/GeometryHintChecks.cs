using System;
using System.Linq;
using System.Reflection;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

internal static class GeometryHintChecks
{
    internal static void Run(Assembly assembly)
    {
        var hints = assembly.GetType("BetterGhPanel.Runtime.GeometryHints", true)!;
        string? Details(IGH_Goo goo) => ((PanelGeometryInfo?)hints.GetMethod("Measure", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { goo, "mm" }))?.FormatDetails(PanelUnitConversion.None, -1);
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("Capture")!;
        PanelSnapshot Capture(params IGH_Goo[] items)
        {
            var tree = new GH_Structure<IGH_Goo>();
            tree.AppendRange(items, new GH_Path(0));
            var arguments = new object?[] { tree, null, PanelUnitConversion.None, "mm" };
            var snapshot = (PanelSnapshot)capture.Invoke(null, arguments)!;
            Check(((IGH_Goo?[])arguments[1]!).SequenceEqual(items), "Capture preserves source goo references");
            return snapshot;
        }

        var line = new Line(Point3d.Origin, new Point3d(3, 4, 0));
        var lineCurve = new GH_Curve(new LineCurve(line));
        var linearNurbs = new GH_Curve(new LineCurve(line).ToNurbsCurve());
        var arc = new Arc(Plane.WorldXY, 2, Math.PI / 2);
        var circle = new Circle(Plane.WorldXY, 2);
        var rectangle = new Rectangle3d(Plane.WorldXY, 3, 4);
        var box = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4)));
        var mesh = Mesh.CreateFromBox(box, 1, 1, 1);
        var surface = new GH_Surface(new PlaneSurface(Plane.WorldXY, new Interval(0, 10), new Interval(0, 10)).ToBrep());
        var solid = new GH_Brep(box.ToBrep());
        var unknown = new UnformattedGoo();
        var snapshot = Capture(new GH_Line(line), lineCurve, linearNurbs, new GH_Arc(arc), new GH_Circle(circle),
            new GH_Rectangle(rectangle), new GH_Box(box), new GH_Mesh(mesh), surface, solid, unknown, new GH_Curve());
        Check(snapshot.Rows[0].DisplayValue == "Ln · L 5 mm" && snapshot.Rows[1].DisplayValue == "Ln · L 5 mm",
            "Lines and line curves show analytic length in model units");
        Check(snapshot.Rows[2].DisplayValue == "Crv · degree 1 · open",
            "General curves capture stored properties without length integration or linearity tests");
        Check(snapshot.Rows[3].DisplayValue.StartsWith("Arc · L 3.14159 mm", StringComparison.Ordinal) &&
            snapshot.Rows[4].DisplayValue == "Cir · R 2 mm", "Arcs and circles show cheap analytic measurements");
        Check(snapshot.Rows[5].DisplayValue == "Rect · A 12 mm²" && snapshot.Rows[6].DisplayValue == "Box · V 24 mm³",
            "Primitive areas and volumes use squared and cubed model units");
        Check(snapshot.Rows[7].DisplayValue == "Mesh · " + mesh.Vertices.Count + " vertices · 6 faces",
            "Mesh hints show stored vertex and face counts");
        Check(snapshot.Rows[8].DisplayValue == "Srf · untrimmed" && snapshot.Rows[9].DisplayValue == "Brep · closed · 6 faces",
            "Surface and Brep capture omits mass calculations");
        Check(snapshot.Rows[10].DisplayValue == "<Widget>" && !snapshot.Rows[11].IsValid,
            "Third-party and invalid goo retain safe fallback summaries without formatting");
        Check(!string.IsNullOrWhiteSpace(snapshot.Rows[11].InvalidReason) && snapshot.Rows[11].GeometryInfo == null,
            "Invalid geometry captures its validity reason and cannot be measured");
        Check(Details(lineCurve) == "Linear · open\nLength: 5 mm" && Details(linearNurbs) == "Linear · open\nLength: 5 mm",
            "Explicit measurement detects linear NURBS as well as line curves");
        Check(Details(new GH_Curve(new ArcCurve(circle)))!.StartsWith("Circle · closed\nLength:", StringComparison.Ordinal),
            "Circle measurement reports closed shape and circumference");
        var curved = new GH_Curve(NurbsCurve.Create(false, 2, new[] { Point3d.Origin, new Point3d(5, 5, 0), new Point3d(10, 0, 0) }));
        Check(Details(curved)!.StartsWith("Curve · open\nLength:", StringComparison.Ordinal), "Curved NURBS are not labeled linear");
        Check(Details(surface) == "Srf\nArea: 100 mm²" && Details(solid) == "Brep · closed\nVolume: 24 mm³",
            "Explicit measurement computes surface area and closed Brep volume");
        var triangle = Brep.CreatePlanarBreps(new PolylineCurve(new[] { Point3d.Origin, new Point3d(10, 0, 0),
            new Point3d(0, 10, 0), Point3d.Origin }), 1e-8).Single();
        Check(Details(new GH_Surface(triangle)) == "Srf\nArea: 50 mm²" && Details(new GH_Brep(triangle)) == "Brep · open\nArea: 50 mm²",
            "Surface and open Brep area respects trimming instead of measuring the underlying rectangle");
        Check(Details(unknown) == null, "Measurement never formats third-party goo");
        VerifyCache(assembly, snapshot);
    }

    private static void VerifyCache(Assembly assembly, PanelSnapshot snapshot)
    {
        var type = assembly.GetType("BetterGhPanel.Runtime.PanelSession", true)!;
        using var lifetime = (IDisposable)Activator.CreateInstance(type, new object[] { (Action)(() => { }) })!;
        var get = type.GetMethod("GetGeometryDetails", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var set = type.GetMethod("SetGeometryDetailResolver", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var attach = type.GetMethod("AttachSnapshot")!;
        var measure = type.GetMethod("MeasureGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!;
        PanelGeometryInfo? Get(PanelSnapshot source, int position) => (PanelGeometryInfo?)get.Invoke(lifetime, new object[] { source, position });
        void Measure(PanelSnapshot source, int position) => measure.Invoke(lifetime, new object[] { source, position });
        int calls = 0;
        set.Invoke(lifetime, new object[] { (Func<int, PanelGeometryInfo?>)(position =>
        {
            calls++;
            if (position == 8) throw new InvalidOperationException("Unavailable geometry");
            if (position == 9) return null;
            return new PanelGeometryInfo("measured " + position, position, units: "mm");
        }) });
        attach.Invoke(lifetime, new object[] { snapshot });
        Check(calls == 0, "Attaching a snapshot never invokes geometry measurements");
        Check(Get(snapshot, 2) == null && Get(snapshot, 2) == null && calls == 0, "Hover reads cached details without calculating anything");
        Measure(snapshot, 2);
        Check(calls == 1 && Get(snapshot, 2)?.Description == "measured 2", "Click completes measurement synchronously");
        Measure(snapshot, 2);
        Check(Get(snapshot, 2)?.Value == 2 && calls == 1, "Repeated clicks and reads reuse the raw measurement");
        Measure(snapshot, 8); Measure(snapshot, 8); Measure(snapshot, 9); Measure(snapshot, 9);
        Check(Get(snapshot, 8) == null && Get(snapshot, 9) == null && calls == 3, "Failures and unavailable results are cached without repeated calculation");
        Measure(snapshot, -1); Measure(snapshot, 100); Measure(snapshot, 11); Measure(snapshot, 0);
        Check(calls == 3, "Invalid rows, positions and already analytic metrics never request calculations");
        var newer = new PanelSnapshot(snapshot.Rows, snapshot.Branches);
        attach.Invoke(lifetime, new object[] { newer });
        Measure(snapshot, 2);
        Check(Get(snapshot, 2) == null && calls == 3, "Stale snapshots cannot measure current source geometry");
        Check(Get(newer, 2) == null, "A new solution invalidates cached measurements");
        Measure(newer, 2);
        Check(Get(newer, 2)?.Value == 2 && calls == 4, "New geometry can be explicitly measured again");
        var converted = new PanelSnapshot(snapshot.Rows, snapshot.Branches, PanelUnitConversion.MillimetersToMeters);
        type.GetMethod("AttachConvertedSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(lifetime, new object[] { converted, (Func<int, bool>)(position => position == 2) });
        Check(Get(converted, 2)?.FormatCompact(converted.UnitConversion, -1) == "measured 2 · L 0.002 m" && calls == 4,
            "Unit-only changes retain and reformat raw metrics without remeasurement");
        lifetime.Dispose();
        Measure(converted, 2);
        Check(Get(converted, 2) == null && calls == 4, "Disposed sessions cannot measure geometry");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }

    private sealed class UnformattedGoo : GH_Goo<string>
    {
        public override bool IsValid => true;
        public override string TypeName => "Widget";
        public override string TypeDescription => "Test third-party data";
        public override IGH_Goo Duplicate() => new UnformattedGoo();
        public override string ToString() => throw new InvalidOperationException("Do not format third-party goo during capture or hover");
    }
}
