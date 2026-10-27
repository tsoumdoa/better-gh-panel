using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Types;
using Rhino.DocObjects.Custom;
using Rhino.FileIO;
using Rhino.Geometry;

// This fixture exercises plugin-controlled preparation directly, separately
// from any Grasshopper collection or output processing performed by the host.
internal static class GeometryPolylineSafetyChecks
{
    internal static async Task Run(Assembly assembly)
    {
        foreach (bool hostile in new[] { false, true }) await VerifyUserData(assembly, hostile);
        foreach (bool hostile in new[] { false, true }) VerifyUnsupportedNestedData(assembly, hostile);
        VerifyPreflight(assembly);
        await VerifyParameterization(assembly);
        VerifyAmbiguousZero(assembly);
    }

    private static async Task VerifyParameterization(Assembly assembly)
    {
        foreach (bool arcLength in new[] { false, true })
        {
            using var source = new PolylineCurve(new[] { Point3d.Origin, new Point3d(3, 0, 0), new Point3d(3, 4, 0) });
            if (arcLength) source.SetArcLengthParameterization(1e-9);
            else { source.SetParameter(0, -5); source.SetParameter(1, 2); source.SetParameter(2, 20); }
            Check(source.IsValid && (arcLength ? source.Parameter(2) != 2 : source.Parameter(0) == -5 && source.Parameter(2) == 20),
                "fixture has a valid non-default " + (arcLength ? "arc-length" : "explicit") + " parameterization");
            var holder = new GH_Curve { Value = source };
            var first = Prepare(assembly, holder) ?? throw new InvalidOperationException("Reparameterized polyline candidate unavailable");
            var second = Prepare(assembly, holder) ?? throw new InvalidOperationException("Repeated parameterized candidate unavailable");
            Check(Witness(first).Equals(Witness(second)), "parameter values survive reconstruction in the complete witness");
            ((IDisposable)second).Dispose();
            var result = await Task.Run(() => Run(first));
            Check(result?.Value == 7, "parameterized worker preserves the original polyline length");
        }
    }

    private static void VerifyAmbiguousZero(Assembly assembly)
    {
        using var zero = new LineCurve(Point3d.Origin, Point3d.Origin);
        var method = assembly.GetType("BetterGhPanel.Runtime.GeometryHints", true)!
            .GetMethod("Measure", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check(method.Invoke(null, new object[] { zero, false, "mm" }) == null,
            "zero from the native length API is not reported as a measured length");
    }

    private static async Task VerifyUserData(Assembly assembly, bool hostile)
    {
        using var native = new PolylineCurve(new[] { Point3d.Origin, new Point3d(3, 0, 0), new Point3d(3, 4, 0) });
        var goo = new GH_Curve(native);
        var source = (PolylineCurve)goo.Value;
        var data = new PolylineSafetyUserData();
        Check(source.UserData.Add(data) && source.HasUserData, "instrumented data attached to exact source polyline");
        var originalData = source.UserData.Find(typeof(PolylineSafetyUserData));
        Check(originalData != null && source.UserData.Count == 1, "source metadata exists before plugin operation");
        PolylineSafetyUserData.Reset(hostile);

        object first = Prepare(assembly, goo) ?? throw new InvalidOperationException("Polyline candidate unavailable");
        object same = Prepare(assembly, goo) ?? throw new InvalidOperationException("Repeated candidate unavailable");
        object witness = Witness(first);
        Check(witness.Equals(Witness(same)), "equal copied polylines produce complete equal witnesses");
        ((IDisposable)same).Dispose();
        Check(source.HasUserData && source.UserData.Count == 1 && ReferenceEquals(originalData, source.UserData.Find(typeof(PolylineSafetyUserData))) &&
            PolylineSafetyUserData.Callbacks == 0, "preparation and witness preserve attached data without callbacks");

        // Change the live source only after the old worker copy has been prepared.
        PolylineSafetyUserData.Reset(false);
        source.SetPoint(1, new Point3d(6, 0, 0));
        Check(source.HasUserData && source.UserData.Count == 1, "source mutation preserves attached metadata");
        PolylineSafetyUserData.Reset(hostile);
        object changed = Prepare(assembly, goo) ?? throw new InvalidOperationException("Changed candidate unavailable");
        Check(!witness.Equals(Witness(changed)), "mutation changes the current copied-value witness");
        ((IDisposable)changed).Dispose();
        var result = await Task.Run(() => Run(first));
        Check(result?.Value == 7 && source.HasUserData && source.UserData.Count == 1 &&
            ReferenceEquals(originalData, source.UserData.Find(typeof(PolylineSafetyUserData))) &&
            PolylineSafetyUserData.Callbacks == 0,
            (hostile ? "Hostile" : "Benign") + " UserData stays attached and uncalled through copy, witness and worker measurement");
        PolylineSafetyUserData.Reset(false);
    }

    private static void VerifyPreflight(Assembly assembly)
    {
        using var native = new PolylineCurve(Enumerable.Range(0, 2049).Select(index => new Point3d(index, index % 2, 0)));
        var goo = new GH_Curve(native);
        var source = (PolylineCurve)goo.Value;
        var data = new PolylineSafetyUserData();
        Check(source.UserData.Add(data) && source.UserData.Count == 1, "oversize source has instrumented metadata");
        PolylineSafetyUserData.Reset(true);
        Check(Prepare(assembly, goo) == null && source.UserData.Count == 1 && PolylineSafetyUserData.Callbacks == 0,
            "oversize polyline is rejected at the count preflight without callbacks or metadata loss");
        PolylineSafetyUserData.Reset(false);
    }

    private static void VerifyUnsupportedNestedData(Assembly assembly, bool hostile)
    {
        using var outer = new PolyCurve();
        Check(outer.Append(new LineCurve(Point3d.Origin, new Point3d(1, 0, 0))) &&
            outer.Append(new LineCurve(new Point3d(1, 0, 0), new Point3d(2, 1, 0))), "polycurve segments created");
        var curveGoo = new GH_Curve(outer);
        var curve = (PolyCurve)curveGoo.Value;
        var segment = curve.SegmentCurve(0);
        Check(!curve.HasUserData && segment.UserData.Add(new PolylineSafetyUserData()) && segment.HasUserData,
            "nested segment contains data while outer polycurve does not");
        PolylineSafetyUserData.Reset(hostile);
        Check(Prepare(assembly, curveGoo) == null && !curve.HasUserData && segment.HasUserData &&
            PolylineSafetyUserData.Callbacks == 0, "unsupported nested polycurve is unavailable without callbacks");
        PolylineSafetyUserData.Reset(false);

        using var nativeBrep = new Box(new BoundingBox(Point3d.Origin, new Point3d(1, 2, 3))).ToBrep();
        var brepGoo = new GH_Brep(nativeBrep);
        var brep = brepGoo.Value;
        var embeddedCurve = brep.Curves3D[0];
        var embeddedSurface = brep.Surfaces[0];
        Check(!brep.HasUserData && embeddedCurve.UserData.Add(new PolylineSafetyUserData()) &&
            embeddedSurface.UserData.Add(new PolylineSafetyUserData()) && embeddedCurve.HasUserData && embeddedSurface.HasUserData,
            "Brep embedded curve and surface contain data while outer Brep does not");
        PolylineSafetyUserData.Reset(hostile);
        Check(Prepare(assembly, brepGoo) == null && !brep.HasUserData && embeddedCurve.HasUserData && embeddedSurface.HasUserData &&
            PolylineSafetyUserData.Callbacks == 0, "unsupported nested Brep is unavailable without callbacks");
        PolylineSafetyUserData.Reset(false);
    }

    private static object? Prepare(Assembly assembly, IGH_Goo goo) =>
        assembly.GetType("BetterGhPanel.Runtime.GeometryHints", true)!
            .GetMethod("PrepareMeasurement", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { goo, "mm", (int)Rhino.UnitSystem.Millimeters, 0.001 });

    private static object Witness(object prepared) => prepared.GetType()
        .GetProperty("Witness", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(prepared)!;

    private static PanelGeometryInfo? Run(object prepared)
    {
        var arguments = new object?[] { null };
        bool claimed = (bool)prepared.GetType().GetMethod("TryRun", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(prepared, arguments)!;
        Check(claimed, "worker claims prepared polyline exactly once");
        return (PanelGeometryInfo?)arguments[0];
    }

    private static void Check(bool condition, string message)
    {
        SmokeAssertions.Check(condition, message);
    }
}

[Guid("a8276ef8-dd15-430c-ae69-d66cf53788ea")]
public sealed class PolylineSafetyUserData : UserData
{
    private static int _callbacks;
    private static bool _hostile;
    internal static int Callbacks => Volatile.Read(ref _callbacks);
    internal static void Reset(bool hostile) { Volatile.Write(ref _callbacks, 0); _hostile = hostile; }
    private static void Called()
    {
        Interlocked.Increment(ref _callbacks);
        if (_hostile) throw new InvalidOperationException("Unexpected UserData callback");
    }
    public override string Description { get { Called(); return "Panel+ polyline safety probe"; } }
    public override bool ShouldWrite { get { Called(); return true; } }
    protected override bool Write(BinaryArchiveWriter archive) { Called(); return false; }
    protected override bool Read(BinaryArchiveReader archive) { Called(); return false; }
    protected override void OnDuplicate(UserData source) { Called(); }
    protected override void OnTransform(Transform transform) { Called(); }
}
