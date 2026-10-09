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
        string? Details(IGH_Goo goo) => (string?)hints.GetMethod("Details", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { goo, "mm" });
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("Capture")!;
        PanelSnapshot Capture(params IGH_Goo[] items)
        {
            var tree = new GH_Structure<IGH_Goo>();
            tree.AppendRange(items, new GH_Path(0));
            var arguments = new object?[] { tree, 1L, null, PanelUnitConversion.None, "mm" };
            var snapshot = (PanelSnapshot)capture.Invoke(null, arguments)!;
            Check(((IGH_Goo?[])arguments[2]!).SequenceEqual(items), "Capture preserves source goo references");
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
        Check(Details(lineCurve) == "Linear · open\nLength: 5 mm" && Details(linearNurbs) == "Linear · open\nLength: 5 mm",
            "Hover detects linear NURBS as well as line curves");
        Check(Details(new GH_Curve(new ArcCurve(circle)))!.StartsWith("Circle · closed\nLength:", StringComparison.Ordinal),
            "Circle curve hover reports closed shape and circumference");
        var curved = new GH_Curve(NurbsCurve.Create(false, 2, new[] { Point3d.Origin, new Point3d(5, 5, 0), new Point3d(10, 0, 0) }));
        Check(Details(curved)!.StartsWith("Curve · open\nLength:", StringComparison.Ordinal), "Curved NURBS are not labeled linear");
        Check(Details(surface) == "Area: 100 mm²" && Details(solid) == "Volume: 24 mm³",
            "Hover measures surface area and closed Brep volume");
        var triangle = Brep.CreatePlanarBreps(new PolylineCurve(new[] { Point3d.Origin, new Point3d(10, 0, 0),
            new Point3d(0, 10, 0), Point3d.Origin }), 1e-8).Single();
        Check(Details(new GH_Surface(triangle)) == "Area: 50 mm²" && Details(new GH_Brep(triangle)) == "Area: 50 mm²",
            "Surface and open Brep area respects trimming instead of measuring the underlying rectangle");
        Check(Details(unknown) == null, "Hover never formats or measures third-party goo");
        VerifyCache(assembly, snapshot);
    }

    private static void VerifyCache(Assembly assembly, PanelSnapshot snapshot)
    {
        var type = assembly.GetType("BetterGhPanel.Runtime.PanelSession", true)!;
        using var lifetime = (IDisposable)Activator.CreateInstance(type, new object[] { (Action)(() => { }) })!;
        var get = type.GetMethod("GetGeometryDetails", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var set = type.GetMethod("SetGeometryDetailResolver", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var attach = type.GetMethod("AttachSnapshot")!;
        string Get(PanelSnapshot source, int position) => (string)get.Invoke(lifetime, new object[] { source, position })!;
        int calls = 0;
        set.Invoke(lifetime, new object[] { (Func<int, string?>)(position =>
        {
            calls++;
            if (position == 1) throw new InvalidOperationException("Unavailable geometry");
            return "measured " + position;
        }) });
        attach.Invoke(lifetime, new object[] { snapshot });
        Check(calls == 0, "Attaching a snapshot never invokes geometry measurements");
        Check(Get(snapshot, 0) == "measured 0" && Get(snapshot, 0) == "measured 0" && calls == 1,
            "Repeated hover measures a row only once");
        Check(Get(snapshot, 1) == "" && Get(snapshot, 1) == "" && calls == 2,
            "Failed measurements are cached without tooltip exceptions or repeated work");
        Check(Get(snapshot, -1) == "" && Get(snapshot, 100) == "" && Get(snapshot, 11) == "" && calls == 2,
            "Invalid rows and positions never request geometry measurements");
        var newer = new PanelSnapshot(2, snapshot.Rows, snapshot.Branches);
        attach.Invoke(lifetime, new object[] { newer });
        Check(Get(snapshot, 0) == "" && calls == 2, "Stale snapshots cannot measure current source geometry");
        Check(Get(newer, 0) == "measured 0" && calls == 3, "A new solution invalidates cached measurements");
        lifetime.Dispose();
        Check(Get(newer, 0) == "" && calls == 3, "Disposed sessions cannot measure geometry");
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
