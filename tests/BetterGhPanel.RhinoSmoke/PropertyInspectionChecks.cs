using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

internal static class PropertyInspectionChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static Task Run(Assembly assembly) => PanelHostFixture.RunWithEditor(assembly, Verify);

    private static PanelSnapshot Capture(Assembly assembly, params IGH_Goo[] inputs)
    {
        var tree = new GH_Structure<IGH_Goo>(); tree.AppendRange(inputs, new GH_Path(0));
        return (PanelSnapshot)assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("Capture")!
            .Invoke(null, new object?[] { tree, null, PanelUnitConversion.None, "mm" })!;
    }
    private static void Number(PanelSnapshot snapshot, int index, PanelSearchMode mode, double expected)
    {
        Check(snapshot.SupportsSearchMode(mode), mode + " is advertised for its input");
        Check(PanelProperties.TryGet(snapshot.Rows[index], mode, null, out var value) && value != null && value.Kind == PanelPropertyKind.Number &&
            Math.Abs(value.Number - expected) < 1e-9, mode + " captures the expected value at " + index);
    }
    private static void Text(PanelSnapshot snapshot, int index, PanelSearchMode mode, string expected) =>
        Check(PanelFilterValue.Format(snapshot, snapshot.Rows[index], mode, -1, includeUnits: false) == expected, mode + " displays the expected value at " + index);

    private static async Task Verify(Assembly assembly)
    {
        var plane = new Plane(new Point3d(-10, 20, 30), Vector3d.ZAxis);
        var box = new Box(Plane.WorldXY, new Interval(0, 2), new Interval(0, 3), new Interval(0, 4));
        var group = new GH_GeometryGroup { Name = "001" }; group.Objects.Add(new GH_Point(Point3d.Origin));
        var snapshot = Capture(assembly, new GH_Vector(new Vector3d(3, 4, 0)), new GH_Colour(Color.FromArgb(40, 10, 20, 30)),
            new GH_Interval(new Interval(-2, 8)), new GH_Interval2D(new UVInterval(new Interval(-1, 2), new Interval(3, 4))),
            new GH_Time(new DateTime(2026, 10, 29, 12, 34, 56)), new GH_Boolean(false), new GH_String("001"),
            new GH_Plane(plane), new GH_Rectangle(new Rectangle3d(Plane.WorldXY, 2, 3)), new GH_Box(box),
            new GH_Arc(new Arc(Plane.WorldXY, 5, Math.PI / 2)), new GH_Circle(new Circle(5)),
            new GH_ComplexNumber(new Complex(3, 4)), new GH_Transform(Transform.Translation(2, 3, 4)), group);
        Check(snapshot.Rows.All(row => row.IsValid), "all scalar property fixtures are verified valid");
        // One representative property per capture handler; query/formatting policy
        // is covered in Core. Keep native admission and freshness checks below.
        Number(snapshot, 0, PanelSearchMode.Magnitude, 5);
        Number(snapshot, 1, PanelSearchMode.Red, 10);
        Number(snapshot, 2, PanelSearchMode.DomainStart, -2);
        Number(snapshot, 3, PanelSearchMode.VEnd, 4);
        Number(snapshot, 4, PanelSearchMode.Year, 2026);
        Text(snapshot, 5, PanelSearchMode.BooleanValue, "False");
        Text(snapshot, 6, PanelSearchMode.TextValue, "001");
        Number(snapshot, 7, PanelSearchMode.OriginX, -10);
        Number(snapshot, 8, PanelSearchMode.Width, 2);
        Check(PanelFilterValue.TryGeometryValue(snapshot.Rows[9], PanelSearchMode.Area, PanelUnitConversion.None, null, out double area) && area == 52, "box surface area is selectable");
        Number(snapshot, 10, PanelSearchMode.Angle, Math.PI / 2);
        Number(snapshot, 11, PanelSearchMode.Radius, 5);
        Number(snapshot, 12, PanelSearchMode.Magnitude, 5);
        Number(snapshot, 13, PanelSearchMode.Determinant, 1);
        Text(snapshot, 14, PanelSearchMode.GroupName, "001");

        using var mesh = Mesh.CreateFromBox(box, 1, 1, 1);
        using var brep = box.ToBrep();
        using var surface = new PlaneSurface(Plane.WorldXY, new Interval(0, 2), new Interval(0, 3));
        using var polyline = new PolylineCurve(new[] { Point3d.Origin, new Point3d(2, 0, 0), new Point3d(2, 2, 0) });
        using var subd = SubD.CreateFromMesh(mesh);
        using var profile = new Circle(2).ToNurbsCurve();
        using var extrusion = Extrusion.Create(profile, 5, true);
        using var cloud = new PointCloud(new[] { Point3d.Origin, new Point3d(1, 2, 3) });
        snapshot = Capture(assembly, new GH_Mesh(mesh), new GH_Brep(brep), new GH_Surface(surface), new GH_Curve(polyline), new GH_SubD(subd), new GH_Extrusion(extrusion), new GH_PointCloud(cloud));
        Check(snapshot.Rows.All(row => row.IsValid), "all geometry property fixtures are verified valid");
        Number(snapshot, 0, PanelSearchMode.FaceCount, 6);
        Text(snapshot, 1, PanelSearchMode.Solid, "True");
        Text(snapshot, 2, PanelSearchMode.Trimmed, "False");
        Number(snapshot, 3, PanelSearchMode.SegmentCount, 2);
        Number(snapshot, 4, PanelSearchMode.VertexCount, subd.Vertices.Count);
        Number(snapshot, 5, PanelSearchMode.Height, 5);
        Number(snapshot, 6, PanelSearchMode.PointCount, 2);
        var rejected = Capture(assembly, new HostileSubD(), new HostileComplex());
        Check(rejected.Rows.All(row => row.Validity == PanelValidity.Unverified && row.Properties.Count == 0 && row.GeometryInfo == null),
            "new property handlers never invoke subclass validity or metadata callbacks");
        var complexHolder = new GH_ComplexNumber(new Complex(3, 4));
        var subdHolder = new GH_SubD(subd);
        var captureTree = new GH_Structure<IGH_Goo>(); captureTree.AppendRange(new IGH_Goo[] { complexHolder, subdHolder }, new GH_Path(0));
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!;
        var captureArgs = new object?[] { captureTree, null, null, null, PanelUnitConversion.None, "mm", null, null, null, null, null };
        var frozen = (PanelSnapshot)capture.GetMethod("CaptureForMeasurements", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, captureArgs)!;
        complexHolder.Value = new Complex(9, 4); subdHolder.Value = null;
        Number(frozen, 0, PanelSearchMode.Real, 3);
        Check(!(bool)capture.GetMethod("GraphSourcesCurrent", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { captureArgs[3] })!,
            "changing a complex value invalidates its captured witness");
        Check(!(bool)capture.GetMethod("MeasurementSourcesCurrent", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { captureArgs[1], captureArgs[2] })!,
            "replacing a SubD holder invalidates its captured witness");
        using var owned = polyline.DuplicateCurve();
        var measured = (PanelGeometryInfo)assembly.GetType("BetterGhPanel.Runtime.GeometryHints", true)!.GetMethod("Measure", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { owned, false, "mm" })!;
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.Planar, Search = "= True" },
            measuredGeometry: new Dictionary<int, PanelGeometryInfo> { [3] = measured });
        Check(result.RowPositions.SequenceEqual(new[] { 3 }), "deferred planar property filters measured geometry and excludes unavailable rows");

        using var fixture = new PanelHostFixture.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Curve(polyline), new GH_Path(0));
        fixture.Source.PersistentData.Append(new GH_Mesh(mesh), new GH_Path(0));
        fixture.Paired.PersistentData.AppendRange(new IGH_Goo[] { new GH_Integer(10), new GH_Integer(20) }, new GH_Path(0));
        fixture.Solve(); await fixture.Ready(() => fixture.Result.Snapshot.Rows.Count == 2);
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Planar, Search = "= True" });
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 0 }) && fixture.Sinks[0].VolatileData.DataCount == 1);
        Check(fixture.Sinks[0].VolatileData.AllData(false).Single() is GH_Curve, "property comparison retains typed geometry on D");
        Check(fixture.Values(3) == "10", "paired data follows property filters");
        fixture.Component.GetType().GetMethod("SetTreeOperations", Instance)!.Invoke(fixture.Component, new object[] { 0, false, false, false, true });
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.FaceCount, DisplayFilterValue = true, CreateSet = true, TypeName = "Mesh" });
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.AllData(false).SingleOrDefault() is GH_Number number && number.Value == 6);
        Check(fixture.Values(3) == "20", "property sets retain paired source provenance");
        string copied = (string)fixture.Component.GetType().GetMethod("CopyValue", Instance)!.Invoke(fixture.Component, new object[] { fixture.Result.Snapshot, 1 })!;
        Check(copied == "6", "Copy values uses selected property numbers");

        var chunk = new GH_IO.Serialization.GH_LooseChunk("Panel"); Check(fixture.Component.Write(chunk), "property settings write");
        var restored = (GH_Component)Activator.CreateInstance(fixture.Component.GetType())!;
        restored.CreateAttributes(); Check(restored.Read(chunk), "property settings read");
        var saved = (PanelQueryOptions)restored.GetType().GetField("_savedOptions", Instance)!.GetValue(restored)!;
        Check(saved.SearchMode == PanelSearchMode.FaceCount && saved.DisplayFilterValue, "property display survives save/load");
        restored.DocumentContextChanged(fixture.Document, GH_DocumentContext.Close);
    }
    private sealed class HostileSubD : GH_SubD
    {
        public override bool IsValid => throw new InvalidOperationException("Subclass validity must not run");
        public override string TypeName => throw new InvalidOperationException("Subclass metadata must not run");
    }
    private sealed class HostileComplex : GH_ComplexNumber
    {
        public override bool IsValid => throw new InvalidOperationException("Subclass validity must not run");
        public override string TypeName => throw new InvalidOperationException("Subclass metadata must not run");
    }
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
}
