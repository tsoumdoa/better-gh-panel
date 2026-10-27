using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

// The fixture itself creates the native objects. The candidate is NOT admitted
// for arbitrary Grasshopper inputs; exact managed wrappers do not prove native type.
internal static class GeometryNurbsCandidateChecks
{
    internal static async Task Run(Assembly assembly)
    {
        foreach (bool hostile in new[] { false, true })
        {
            await VerifyCurve(assembly, hostile);
            await VerifySurface(assembly, hostile);
            VerifyNestedBrepRejected(assembly, hostile);
            VerifyPreflight(assembly, hostile);
        }
        await VerifyPeriodicAndTwoDimensional(assembly);
        await VerifyUnitOnlyRetention(assembly);
    }

    private static async Task VerifyCurve(Assembly assembly, bool hostile)
    {
        using var source = RationalCurve();
        double expected = source.GetLength();
        Check(source.IsValid && source.IsRational && source.Domain.T0 == 2 && source.Domain.T1 == 5,
            "fixture creates a valid rational curve with nonuniform knots and a non-default domain");
        var data = new PolylineSafetyUserData();
        Check(source.UserData.Add(data) && source.HasUserData && source.UserData.Count == 1,
            "curve metadata was attached to the source before preparation");
        object? originalData = source.UserData.Find(typeof(PolylineSafetyUserData));
        Check(originalData != null, "curve metadata identity exists");
        PolylineSafetyUserData.Reset(hostile);
        object prepared = Prepare(assembly, "PrepareTrustedCurve", source) ?? throw new InvalidOperationException("trusted curve candidate unavailable");
        object unchanged = Prepare(assembly, "PrepareTrustedCurve", source) ?? throw new InvalidOperationException("repeated curve candidate unavailable");
        Check(Witness(prepared).Equals(Witness(unchanged)), "unchanged rational curve produces the same witness");
        ((IDisposable)unchanged).Dispose();
        object differentUnits = Prepare(assembly, "PrepareTrustedCurve", source, "cm") ?? throw new InvalidOperationException("context candidate unavailable");
        Check(!Witness(prepared).Equals(Witness(differentUnits)), "units participate in the curve witness");
        ((IDisposable)differentUnits).Dispose();
        object differentTolerance = Prepare(assembly, "PrepareTrustedCurve", source, tolerance: 0.002) ?? throw new InvalidOperationException("tolerance candidate unavailable");
        Check(!Witness(prepared).Equals(Witness(differentTolerance)), "tolerance participates in the curve witness");
        ((IDisposable)differentTolerance).Dispose();
        Check(PolylineSafetyUserData.Callbacks == 0 && source.HasUserData && source.UserData.Count == 1 &&
            ReferenceEquals(originalData, source.UserData.Find(typeof(PolylineSafetyUserData))), "curve preparation leaves metadata attached and uncalled");
        PolylineSafetyUserData.Reset(false);
        Check(source.Points.SetPoint(1, new Point4d(4, 6, 0, 2.5)), "changed only the homogeneous weight");
        PolylineSafetyUserData.Reset(hostile);
        object changed = Prepare(assembly, "PrepareTrustedCurve", source) ?? throw new InvalidOperationException("changed curve candidate unavailable");
        Check(!Witness(prepared).Equals(Witness(changed)), "a weight change changes the copied-value witness");
        ((IDisposable)changed).Dispose();
        PolylineSafetyUserData.Reset(false);
        Check(source.Points.SetPoint(1, new Point4d(4, 6, 0, 2)), "restore original control point");
        source.Knots[2] = 4;
        PolylineSafetyUserData.Reset(hostile);
        object changedKnot = Prepare(assembly, "PrepareTrustedCurve", source) ?? throw new InvalidOperationException("changed knot candidate unavailable");
        Check(!Witness(prepared).Equals(Witness(changedKnot)), "an interior knot change changes the witness");
        ((IDisposable)changedKnot).Dispose();
        var result = await Task.Run(() => RunWork(prepared));
        Check(result?.Value is double measured && Math.Abs(measured - expected) <= Math.Max(1e-10, expected * 1e-10) &&
            result.Details.Contains("Degree: 2") && PolylineSafetyUserData.Callbacks == 0 && source.HasUserData &&
            ReferenceEquals(originalData, source.UserData.Find(typeof(PolylineSafetyUserData))),
            "worker length matches the original curve and is isolated from source edits and userdata");
        PolylineSafetyUserData.Reset(false);
    }

    private static async Task VerifySurface(Assembly assembly, bool hostile)
    {
        using var source = RationalSurface();
        using var originalMass = AreaMassProperties.Compute(source);
        double expected = originalMass?.Area ?? throw new InvalidOperationException("surface fixture has no area");
        Check(source.IsValid && source.IsRational && source.Domain(0).T0 == 2 && source.Domain(1).T0 == 7,
            "fixture creates a rational surface with non-default domains");
        var data = new PolylineSafetyUserData();
        Check(source.UserData.Add(data) && source.HasUserData && source.UserData.Count == 1,
            "surface metadata was attached before preparation");
        object? originalData = source.UserData.Find(typeof(PolylineSafetyUserData));
        PolylineSafetyUserData.Reset(hostile);
        object prepared = Prepare(assembly, "PrepareTrustedSurface", source) ?? throw new InvalidOperationException("trusted surface candidate unavailable");
        object unchanged = Prepare(assembly, "PrepareTrustedSurface", source) ?? throw new InvalidOperationException("repeated surface candidate unavailable");
        Check(Witness(prepared).Equals(Witness(unchanged)), "unchanged surface has an equal complete witness");
        ((IDisposable)unchanged).Dispose();
        Check(PolylineSafetyUserData.Callbacks == 0 && source.HasUserData &&
            ReferenceEquals(originalData, source.UserData.Find(typeof(PolylineSafetyUserData))), "surface preparation leaves metadata attached and uncalled");
        PolylineSafetyUserData.Reset(false);
        Check(source.Points.SetPoint(1, 1, new Point4d(4, 6, 0, 2)), "source surface changed after preparation");
        PolylineSafetyUserData.Reset(hostile);
        object changed = Prepare(assembly, "PrepareTrustedSurface", source) ?? throw new InvalidOperationException("changed surface candidate unavailable");
        Check(!Witness(prepared).Equals(Witness(changed)), "surface point changes the witness");
        ((IDisposable)changed).Dispose();
        var result = await Task.Run(() => RunWork(prepared));
        Check(result?.Value is double measured && Math.Abs(measured - expected) <= Math.Max(1e-8, expected * 1e-8) &&
            result.Details.Contains("Degree (U/V): 1/1") && result.Details.Contains("Planarity: Planar") &&
            PolylineSafetyUserData.Callbacks == 0 && source.HasUserData &&
            ReferenceEquals(originalData, source.UserData.Find(typeof(PolylineSafetyUserData))),
            "worker area matches the original surface and leaves source userdata intact");
        PolylineSafetyUserData.Reset(false);
    }

    private static async Task VerifyPeriodicAndTwoDimensional(Assembly assembly)
    {
        using var periodic = NurbsCurve.Create(true, 3, new[] { new Point3d(0, 0, 0), new Point3d(2, 0, 0),
            new Point3d(3, 1, 0), new Point3d(2, 3, 0), new Point3d(0, 3, 0), new Point3d(-1, 1, 0) });
        Check(periodic != null && periodic.IsValid && periodic.IsPeriodic, "periodic curve fixture is valid");
        double length = periodic!.GetLength();
        object prepared = Prepare(assembly, "PrepareTrustedCurve", periodic) ?? throw new InvalidOperationException("periodic candidate unavailable");
        var result = await Task.Run(() => RunWork(prepared));
        Check(result?.Value is double measured && Math.Abs(measured - length) <= Math.Max(1e-10, length * 1e-10) &&
            result.Details.Contains("Closed"), "periodic curve length and closure are preserved");

        using var curve2d = new NurbsCurve(2, false, 2, 2);
        curve2d.Knots[0] = -3; curve2d.Knots[1] = 9;
        Check(curve2d.Points.SetPoint(0, new Point4d(0, 0, 0, 1)) &&
            curve2d.Points.SetPoint(1, new Point4d(3, 4, 0, 1)) && curve2d.IsValid, "2D curve fixture is valid");
        prepared = Prepare(assembly, "PrepareTrustedCurve", curve2d) ?? throw new InvalidOperationException("2D candidate unavailable");
        result = await Task.Run(() => RunWork(prepared));
        Check(result?.Value is double twoDimensional && Math.Abs(twoDimensional - 5) < 1e-10, "2D curve length is preserved");
    }

    private static void VerifyNestedBrepRejected(Assembly assembly, bool hostile)
    {
        using var boundary = new PolylineCurve(new[] { Point3d.Origin, new Point3d(10, 0, 0),
            new Point3d(0, 10, 0), Point3d.Origin });
        using var triangle = Brep.CreatePlanarBreps(boundary, 1e-8).Single();
        var holder = new GH_Surface(triangle);
        var source = holder.Value;
        var nestedCurve = source.Curves3D[0]; var nestedSurface = source.Surfaces[0];
        var nestedTrim = source.Trims[0]; var nestedFace = source.Faces[0];
        Check(source.UserData.Add(new PolylineSafetyUserData()) && nestedCurve.UserData.Add(new PolylineSafetyUserData()) &&
            nestedSurface.UserData.Add(new PolylineSafetyUserData()) && nestedTrim.UserData.Add(new PolylineSafetyUserData()) &&
            nestedFace.UserData.Add(new PolylineSafetyUserData()), "trimmed Brep and all probed nested data were attached");
        Check(source.HasUserData && nestedCurve.HasUserData && nestedSurface.HasUserData && nestedTrim.HasUserData && nestedFace.HasUserData,
            "root, curve, surface, trim and face data are present on their original objects");
        PolylineSafetyUserData.Reset(hostile);
        var prepare = assembly.GetType("BetterGhPanel.Runtime.GeometryHints", true)!.GetMethod("PrepareMeasurement", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check(prepare.Invoke(null, new object[] { holder, "mm", (int)Rhino.UnitSystem.Millimeters, 0.001 }) == null &&
            PolylineSafetyUserData.Callbacks == 0 && source.HasUserData && nestedCurve.HasUserData && nestedSurface.HasUserData &&
            nestedTrim.HasUserData && nestedFace.HasUserData, "trimmed Brep remains unavailable without invoking nested data");
        PolylineSafetyUserData.Reset(false);
    }

    private static void VerifyPreflight(Assembly assembly, bool hostile)
    {
        using var curve = new NurbsCurve(3, false, 2, 1025);
        using var surface = NurbsSurface.Create(3, false, 2, 2, 65, 64)!;
        Check(curve.UserData.Add(new PolylineSafetyUserData()) && surface.UserData.Add(new PolylineSafetyUserData()) &&
            curve.HasUserData && surface.HasUserData, "oversized fixtures have root metadata");
        PolylineSafetyUserData.Reset(hostile);
        Check(Prepare(assembly, "PrepareTrustedCurve", curve) == null && Prepare(assembly, "PrepareTrustedSurface", surface) == null &&
            PolylineSafetyUserData.Callbacks == 0 && curve.HasUserData && surface.HasUserData,
            "point-count preflight rejects oversized values before snapshot allocation and preserves metadata");
        PolylineSafetyUserData.Reset(false);
    }

    private static async Task VerifyUnitOnlyRetention(Assembly assembly)
    {
        using var source = RationalCurve();
        var row = new PanelRow(0, "{0}", "Curve", "Crv", null,
            geometryInfo: new PanelGeometryInfo("Crv", canMeasure: true));
        var snapshot = new PanelSnapshot(new[] { row }, new[] { new PanelBranch("{0}", 1) });
        var sessionType = assembly.GetType("BetterGhPanel.Runtime.PanelSession", true)!;
        using var session = (IDisposable)Activator.CreateInstance(sessionType, new object[] { (Action)(() => { }) })!;
        var candidate = CandidateMethod(assembly, "PrepareTrustedCurve");
        var counter = new Counter();
        var resolverFactory = typeof(GeometryNurbsCandidateChecks).GetMethod("CreateResolver", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(candidate.ReturnType);
        var resolver = resolverFactory.Invoke(null, new object[] { assembly, source, counter })!;
        sessionType.GetMethod("SetGeometryPreparationResolver", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, new[] { resolver });
        var attach = sessionType.GetMethod("AttachSnapshot")!;
        var convert = sessionType.GetMethod("AttachConvertedSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var request = sessionType.GetMethod("RequestGeometryMeasurements", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var has = sessionType.GetMethod("HasGeometryMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var get = sessionType.GetMethod("GetGeometryDetails", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool Has(PanelSnapshot value) => (bool)has.Invoke(session, new object[] { value, 0 })!;
        void Request(PanelSnapshot value) => request.Invoke(session, new object[] { value, new[] { 0 } });
        attach.Invoke(session, new object[] { snapshot });
        Request(snapshot);
        await WaitUntil(() => Has(snapshot));
        double before = ((PanelGeometryInfo)get.Invoke(session, new object[] { snapshot, 0 })!).Value!.Value;
        var converted = new PanelSnapshot(snapshot.Rows, snapshot.Branches, PanelUnitConversion.MillimetersToMeters);
        convert.Invoke(session, new object[] { converted, (Func<int, bool>)(_ => true) });
        Check(Has(converted) && counter.Calls == 2, "unchanged unit-only solve retains the actual copied-value witness");
        Check(source.Points.SetPoint(1, new Point4d(8, 12, 0, 2)), "curve changed before second unit-only solve");
        var changed = new PanelSnapshot(snapshot.Rows, snapshot.Branches);
        convert.Invoke(session, new object[] { changed, (Func<int, bool>)(_ => true) });
        Check(!Has(changed) && counter.Calls == 3, "changed values invalidate the real curve witness");
        Request(changed);
        await WaitUntil(() => Has(changed));
        double after = ((PanelGeometryInfo)get.Invoke(session, new object[] { changed, 0 })!).Value!.Value;
        Check(counter.Calls == 4 && Math.Abs(before - after) > 1e-7, "changed curve is measured again");
    }

    private static Func<int, T?> CreateResolver<T>(Assembly assembly, NurbsCurve source, Counter counter) where T : class =>
        _ => { counter.Calls++; return (T?)Prepare(assembly, "PrepareTrustedCurve", source); };
    private sealed class Counter { internal int Calls; }

    private static NurbsCurve RationalCurve()
    {
        var curve = new NurbsCurve(3, true, 3, 4);
        double[] knots = { 2, 2, 3, 5, 5 };
        for (int i = 0; i < knots.Length; i++) curve.Knots[i] = knots[i];
        Check(curve.Points.SetPoint(0, new Point4d(0, 0, 0, 1)) && curve.Points.SetPoint(1, new Point4d(4, 6, 0, 2)) &&
            curve.Points.SetPoint(2, new Point4d(10, 4, 0, 2)) && curve.Points.SetPoint(3, new Point4d(9, 0, 0, 1)), "rational control points set");
        return curve;
    }

    private static NurbsSurface RationalSurface()
    {
        var surface = NurbsSurface.Create(3, true, 2, 2, 2, 2) ?? throw new InvalidOperationException("surface allocation failed");
        surface.KnotsU[0] = 2; surface.KnotsU[1] = 5; surface.KnotsV[0] = 7; surface.KnotsV[1] = 11;
        Check(surface.Points.SetPoint(0, 0, new Point4d(0, 0, 0, 1)) && surface.Points.SetPoint(1, 0, new Point4d(6, 0, 0, 2)) &&
            surface.Points.SetPoint(0, 1, new Point4d(0, 6, 0, 2)) && surface.Points.SetPoint(1, 1, new Point4d(3, 3, 0, 1)),
            "rational surface control points set");
        return surface;
    }

    private static MethodInfo CandidateMethod(Assembly assembly, string name) => assembly.GetType("BetterGhPanel.Runtime.NurbsMeasurementCandidate", true)!
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static object? Prepare(Assembly assembly, string name, GeometryBase source, string units = "mm", double tolerance = 0.001) =>
        CandidateMethod(assembly, name).Invoke(null, new object[] { source, units, (int)Rhino.UnitSystem.Millimeters, tolerance });
    private static object Witness(object prepared) => prepared.GetType().GetProperty("Witness", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(prepared)!;
    private static PanelGeometryInfo? RunWork(object prepared)
    {
        var args = new object?[] { null };
        Check((bool)prepared.GetType().GetMethod("TryRun", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(prepared, args)!, "worker claims candidate");
        return (PanelGeometryInfo?)args[0];
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("NURBS fixture work did not complete");
            await Task.Delay(10);
        }
    }
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
}
