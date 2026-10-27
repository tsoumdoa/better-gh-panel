using System;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Rhino.Runtime;

internal static class AnalyticCurveDetailsChecks
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    internal static async Task Run(Assembly assembly)
    {
        var hints = assembly.GetType("BetterGhPanel.Runtime.GeometryHints", true)!;
        var prepare = hints.GetMethod("PrepareMeasurement", Static)!;
        object? Prepare(IGH_Goo goo) => prepare.Invoke(null, new object[] { goo, "mm", (int)Rhino.UnitSystem.Millimeters, 0.001 });
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("Capture")!;
        PanelSnapshot Capture(params IGH_Goo[] items)
        {
            var tree = new GH_Structure<IGH_Goo>(); tree.AppendRange(items, new GH_Path(0));
            return (PanelSnapshot)capture.Invoke(null, new object?[] { tree, null, PanelUnitConversion.None, "mm" })!;
        }

        using var line = new LineCurve(Point3d.Origin, new Point3d(3, 4, 0));
        using var arc = new ArcCurve(new Arc(Plane.WorldXY, 2, Math.PI / 2));
        using var circle = new ArcCurve(new Circle(Plane.WorldXY, 2));
        var lineHolder = new GH_Curve { Value = line };
        var arcHolder = new GH_Curve { Value = arc };
        var circleHolder = new GH_Curve { Value = circle };
        Check(line.UserData.Add(new PolylineSafetyUserData()) && arc.UserData.Add(new PolylineSafetyUserData()) &&
            circle.UserData.Add(new PolylineSafetyUserData()), "analytic sources have metadata before capture");
        var lineData = line.UserData.Find(typeof(PolylineSafetyUserData));
        Check(lineData != null, "analytic source metadata identity is present before capture");
        PolylineSafetyUserData.Reset(true);
        var snapshot = Capture(lineHolder, arcHolder, circleHolder, new GH_Circle(new Circle(Plane.WorldXY, 2)));
        Check(snapshot.Rows[0].GeometryInfo?.FormatDetails(PanelUnitConversion.None, -1).Contains("Shape: Linear") == true &&
            snapshot.Rows[1].GeometryInfo?.FormatDetails(PanelUnitConversion.None, -1).Contains("Shape: Arc") == true &&
            snapshot.Rows[2].GeometryInfo?.FormatDetails(PanelUnitConversion.None, -1).Contains("Shape: Circle") == true,
            "production capture includes analytic shape and topology details");
        Check(snapshot.Rows[0].DisplayValue == "Ln · L 5 mm" && snapshot.Rows[2].DisplayValue == "Cir · R 2 mm" &&
            snapshot.Rows[2].GeometryInfo?.MetricName == "Radius", "analytic details preserve compact line length and circle radius");
        Check(snapshot.Rows[2].GeometryInfo?.FormatDetails(PanelUnitConversion.None, -1).Contains("Length: 12.5664 mm") == true &&
            snapshot.Rows[3].GeometryInfo?.FormatDetails(PanelUnitConversion.MillimetersToMeters, -1).Contains("Length: 0.0125664 m") == true,
            "circle and circle-curve snapshots include unit-aware circumference details");
        var lineWork = Prepare(lineHolder) ?? throw new InvalidOperationException("Line details unavailable");
        var arcWork = Prepare(arcHolder) ?? throw new InvalidOperationException("Arc details unavailable");
        var circleWork = Prepare(circleHolder) ?? throw new InvalidOperationException("Circle details unavailable");
        var arcInfo = Run(arcWork); var circleInfo = Run(circleWork);
        Check(arcInfo?.Value is double arcLength && Math.Abs(arcLength - Math.PI) < 1e-10 &&
            arcInfo.Details.Contains("Topology: Open") && arcInfo.Details.Contains("Shape: Arc"),
            "prepared arc details report its analytic length and open shape");
        Check(circleInfo?.Value is double circumference && Math.Abs(circumference - 4 * Math.PI) < 1e-10 &&
            circleInfo.FormatDetails(PanelUnitConversion.None, -1).StartsWith("Length:", StringComparison.Ordinal) &&
            circleInfo.Details.Contains("Shape: Circle") && circleInfo.Details.Contains("Topology: Closed"),
            "prepared circle details report circumference and closed shape");
        Check(PolylineSafetyUserData.Callbacks == 0 && ReferenceEquals(lineData, line.UserData.Find(typeof(PolylineSafetyUserData))),
            "capture and preparation leave analytic source metadata attached and uncalled");
        PolylineSafetyUserData.Reset(false);
        line.Transform(Transform.Scale(Point3d.Origin, 2));
        PolylineSafetyUserData.Reset(true);
        var lineInfo = await Task.Run(() => Run(lineWork));
        Check(lineInfo?.Value == 5 && lineInfo.Details.Contains("Shape: Linear") && lineInfo.Details.Contains("Planarity: Planar") &&
            PolylineSafetyUserData.Callbacks == 0, "worker uses only captured analytic values after the source changes");
        PolylineSafetyUserData.Reset(false);

        var referenced = new GH_Curve { Value = arc, ReferenceID = Guid.NewGuid() };
        Check(referenced.IsReferencedGeometry && Prepare(referenced) == null && Capture(referenced).Rows[0].Validity == PanelValidity.Unverified,
            "an orphaned synthetic analytic reference remains unverified");
        using var hostile = new HostileLineCurve();
        HostileLineCurve.Calls = 0;
        Check(Prepare(new GH_Curve { Value = hostile }) == null && HostileLineCurve.Calls == 0,
            "a managed analytic subclass is rejected without invoking its validity override");
        using var disposed = new LineCurve(Point3d.Origin, new Point3d(1, 0, 0));
        disposed.Dispose();
        Check(Prepare(new GH_Curve { Value = disposed }) == null, "disposed analytic geometry is unavailable");
        using (var doc = Rhino.RhinoDoc.CreateHeadless(null))
        {
            var id = doc.Objects.AddCurve(arc);
            var controlled = (Curve)doc.Objects.FindId(id).Geometry;
            using var controlledWork = (IDisposable?)Prepare(new GH_Curve { Value = controlled });
            Check(controlled.IsDocumentControlled && controlledWork != null,
                "loaded document-controlled analytic geometry can use an owned duplicate");
        }
        bool corruption = CommonObject.PerformCorruptionTesting;
        try
        {
            CommonObject.PerformCorruptionTesting = true;
            Check(Prepare(arcHolder) == null && Capture(arcHolder).Rows[0].Validity == PanelValidity.Unverified,
                "corruption-testing mode gates analytic source validation and preparation");
        }
        finally { CommonObject.PerformCorruptionTesting = corruption; }

        await VerifyDocument(assembly);
        PolylineSafetyUserData.Reset(false);
    }

    private static async Task VerifyDocument(Assembly assembly)
    {
        var previous = Instances.DocumentEditor;
        using var owned = previous == null ? new GH_DocumentEditor() : null;
        var editor = previous ?? owned!;
        _ = editor.Handle;
        var setEditor = typeof(Instances).GetMethod("SetDocumentEditor", Static)!;
        setEditor.Invoke(null, new object?[] { editor });
        try
        {
            using var fixture = new OutputChecks.Fixture(assembly);
            fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
            fixture.Source.PersistentData.Append(new GH_Curve { Value = new LineCurve(Point3d.Origin, new Point3d(3, 4, 0)) }, new GH_Path(0));
            fixture.Source.PersistentData.Append(new GH_Curve { Value = new ArcCurve(new Circle(Plane.WorldXY, 2)) }, new GH_Path(0));
            fixture.Paired.PersistentData.Append(new GH_String("line"), new GH_Path(0));
            fixture.Paired.PersistentData.Append(new GH_String("circle"), new GH_Path(0));
            fixture.Solve();
            await fixture.Ready(() => fixture.Result.Snapshot.Rows.Count == 2);
            var snapshot = fixture.Result.Snapshot;
            Check(snapshot.Rows[0].GeometryInfo?.Details.Contains("Shape: Linear") == true &&
                snapshot.Rows[1].GeometryInfo?.Details.Contains("Shape: Circle") == true &&
                snapshot.Rows[0].GeometryInfo?.CanMeasure == false && snapshot.Rows[1].GeometryInfo?.CanMeasure == false,
                "real GH document captures analytic details without deferred native work");
            Check(fixture.Component.Params.Output[0].VolatileData.DataCount == 2 &&
                snapshot.Rows[0].DisplayValue == "Ln · L 5 mm" && snapshot.Rows[1].DisplayValue == "Cir · R 2 mm",
                "real component preserves typed output and existing compact analytic values");
        }
        finally { setEditor.Invoke(null, new object?[] { previous }); }
    }

    private static PanelGeometryInfo? Run(object work)
    {
        var arguments = new object?[] { null };
        Check((bool)work.GetType().GetMethod("TryRun", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(work, arguments)!,
            "analytic prepared work is claimed once");
        return (PanelGeometryInfo?)arguments[0];
    }

    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);

    private sealed class HostileLineCurve : LineCurve
    {
        internal static int Calls;
        public override bool IsValid { get { Calls++; throw new InvalidOperationException("Unexpected virtual validation"); } }
    }
}
