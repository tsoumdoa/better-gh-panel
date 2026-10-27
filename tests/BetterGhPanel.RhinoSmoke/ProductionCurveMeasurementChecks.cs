using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Rhino.Runtime;

// Exercises the actual component, GH document, capture, session and resolver.
// No measurement resolver is replaced in this fixture.
internal static class ProductionCurveMeasurementChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    internal static async Task Run(Assembly assembly)
    {
        var previous = Instances.DocumentEditor;
        using var owned = previous == null ? new GH_DocumentEditor() : null;
        var editor = previous ?? owned!;
        _ = editor.Handle;
        var setEditor = typeof(Instances).GetMethod("SetDocumentEditor", Static)!;
        setEditor.Invoke(null, new object?[] { editor });
        try
        {
            VerifyGates(assembly);
            await VerifyCaptureAndRetention(assembly);
            await VerifyReplacementAndDisposal(assembly);
            await VerifyCapacityAcrossPanels(assembly);
        }
        finally { PolylineSafetyUserData.Reset(false); setEditor.Invoke(null, new object?[] { previous }); }
    }

    private static void VerifyGates(Assembly assembly)
    {
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("Capture")!;
        var prepare = assembly.GetType("BetterGhPanel.Runtime.GeometryHints", true)!.GetMethod("PrepareMeasurement", Static)!;
        PanelSnapshot Capture(IGH_Goo value)
        {
            var tree = new GH_Structure<IGH_Goo>(); tree.Append(value, new GH_Path(0));
            return (PanelSnapshot)capture.Invoke(null, new object?[] { tree, null, PanelUnitConversion.None, "mm" })!;
        }
        object? Prepare(IGH_Goo value) => prepare.Invoke(null, new object[] { value, "mm", (int)Rhino.UnitSystem.Millimeters, 0.001 });

        using var curve = Polyline();
        var referenced = new GH_Curve { Value = curve, ReferenceID = Guid.NewGuid() };
        Check(referenced.IsReferencedGeometry && Capture(referenced).Rows[0].Validity == PanelValidity.Unverified && Prepare(referenced) == null,
            "an orphaned synthetic reference cannot establish its document context");

        using var doc = Rhino.RhinoDoc.CreateHeadless(null);
        var id = doc.Objects.AddCurve(curve);
        var controlled = (Curve)doc.Objects.FindId(id).Geometry;
        Check(controlled.IsDocumentControlled, "document-controlled curve fixture was created");
        var controlledHolder = new GH_Curve { Value = controlled };
        using (var controlledWork = (IDisposable?)Prepare(controlledHolder))
            Check(Capture(controlledHolder).Rows[0].Validity == PanelValidity.VerifiedValid && controlledWork != null,
                "loaded document-controlled sources can be prepared as owned copies");

        var local = new GH_Curve { Value = curve };
        using var localNurbs = Nurbs();
        Check(curve.UserData.Add(new PolylineSafetyUserData()) && localNurbs.UserData.Add(new PolylineSafetyUserData()),
            "bounded sources have metadata before direct capture");
        PolylineSafetyUserData.Reset(true);
        foreach (var holder in new[] { local, new GH_Curve { Value = localNurbs } })
        {
            Check(Capture(holder).Rows[0].GeometryInfo?.CanMeasure == true,
                "bounded metadata-bearing source remains eligible after capture");
            using var prepared = (IDisposable?)Prepare(holder);
            Check(prepared != null, "metadata-bearing source reconstructs without a source duplicate");
        }
        Check(PolylineSafetyUserData.Callbacks == 0, "candidate capture and reconstruction do not invoke managed UserData");
        PolylineSafetyUserData.Reset(false);
        using var oversized = new PolylineCurve(Enumerable.Range(0, 2049).Select(i => new Point3d(i, i % 2, 0)));
        using var tooManyPoints = new NurbsCurve(3, false, 2, 1025);
        using var tooHighOrder = new NurbsCurve(3, false, 13, 13);
        Check(tooManyPoints.UserData.Add(new PolylineSafetyUserData()) && tooHighOrder.UserData.Add(new PolylineSafetyUserData()),
            "invalid NURBS sources have metadata before capture");
        PolylineSafetyUserData.Reset(true);
        foreach (Curve rejected in new Curve[] { tooManyPoints, tooHighOrder })
        {
            var holder = new GH_Curve { Value = rejected };
            Check(Capture(holder).Rows[0].Validity != PanelValidity.VerifiedValid && Prepare(holder) == null,
                "invalid NURBS sources remain unavailable");
        }
        Check(PolylineSafetyUserData.Callbacks == 0, "invalid source inspection leaves metadata uncalled");
        PolylineSafetyUserData.Reset(false);
        using (var oversizeWork = (IDisposable?)Prepare(new GH_Curve { Value = oversized }))
            Check(Capture(new GH_Curve { Value = oversized }).Rows[0].Validity == PanelValidity.VerifiedValid && oversizeWork != null,
                "valid curves above the value-provider limit use compatibility duplication");
        bool prior = CommonObject.PerformCorruptionTesting;
        try
        {
            CommonObject.PerformCorruptionTesting = true;
            Check(Capture(local).Rows[0].Validity == PanelValidity.Unverified && Prepare(local) == null,
                "corruption-testing mode gates source validation and reconstruction");
        }
        finally { CommonObject.PerformCorruptionTesting = prior; }
    }

    private static async Task VerifyCaptureAndRetention(Assembly assembly)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        var polyline = Polyline();
        polyline.SetParameter(0, -5); polyline.SetParameter(1, 2); polyline.SetParameter(2, 20);
        var nurbs = Nurbs();
        Check(polyline.UserData.Add(new PolylineSafetyUserData()) && nurbs.UserData.Add(new PolylineSafetyUserData()),
            "both GH input sources have metadata before document collection and capture");
        PolylineSafetyUserData.Reset(false); // GH collection may itself duplicate data.
        fixture.Source.PersistentData.Append(new GH_Curve { Value = polyline }, new GH_Path(0));
        fixture.Source.PersistentData.Append(new GH_Curve { Value = nurbs }, new GH_Path(0));
        fixture.Paired.PersistentData.Append(new GH_String("polyline"), new GH_Path(0));
        fixture.Paired.PersistentData.Append(new GH_String("nurbs"), new GH_Path(0));
        fixture.Solve();
        await fixture.Ready(() => Snapshot(fixture).Rows.Count == 2);
        var snapshot = Snapshot(fixture);
        Check(snapshot.Rows.All(row => row.IsValid && row.GeometryInfo?.CanMeasure == true),
            "production GH capture admits bounded polyline and NURBS rows for deferred work");
        var sources = Sources(fixture);
        var capturedPolyline = (PolylineCurve)((GH_Curve)sources[0]!).Value;
        var capturedNurbs = (NurbsCurve)((GH_Curve)sources[1]!).Value;
        var firstData = capturedPolyline.UserData.Find(typeof(PolylineSafetyUserData));
        var secondData = capturedNurbs.UserData.Find(typeof(PolylineSafetyUserData));
        Check(firstData != null && secondData != null, "metadata is present on both exact captured sources before measurement");
        var output = fixture.Component.Params.Output[0].VolatileData.AllData(false).ToArray();
        Check(output.Length == 2 && ReferenceEquals(output[0], sources[0]) && ReferenceEquals(output[1], sources[1]),
            "normal D output retains both captured typed holders and their identities");
        PolylineSafetyUserData.Reset(true);
        RequestVisible(fixture, snapshot, 0);
        await Wait(() => Has(fixture, snapshot, 0));
        var offscreen = Measure(fixture, snapshot, CancellationToken.None, 1);
        Check(await Result(offscreen), "offscreen NURBS measurement uses the production queue");
        var measuredPolyline = Details(fixture, snapshot, 0);
        var measuredNurbs = Details(fixture, snapshot, 1);
        Check(measuredPolyline?.Value == 7 && measuredPolyline.Details.Contains("Segments: 2") &&
            measuredNurbs?.Value is double length && length > 0 && measuredNurbs.Details.Contains("Degree: 2"),
            "visible and offscreen workers return lengths and construction metadata");
        Check(PolylineSafetyUserData.Callbacks == 0 && ReferenceEquals(firstData, capturedPolyline.UserData.Find(typeof(PolylineSafetyUserData))) &&
            ReferenceEquals(secondData, capturedNurbs.UserData.Find(typeof(PolylineSafetyUserData))),
            "production capture and workers preserve the source metadata without calling it");
        PolylineSafetyUserData.Reset(false);

        PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.MillimetersToMeters, -1);
        await fixture.Ready(() => !ReferenceEquals(snapshot, Snapshot(fixture)));
        var converted = Snapshot(fixture);
        Check(Has(fixture, converted, 0) && Has(fixture, converted, 1) && Details(fixture, converted, 0)?.Value == 7,
            "unit-only solve retains unchanged copied-value witnesses and raw lengths");

        // Mutate all host-owned copies before the next unit-only solve; no resolver
        // is injected and the new capture must compare its own witness.
        foreach (var holder in fixture.Source.PersistentData.AllData(false).OfType<GH_Curve>()
            .Concat(fixture.Source.VolatileData.AllData(false).OfType<GH_Curve>())
            .Concat(Sources(fixture).OfType<GH_Curve>()))
            if (holder.Value is PolylineCurve value) value.SetPoint(1, new Point3d(6, 0, 0));
        PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.None, -1);
        await fixture.Ready(() => !ReferenceEquals(converted, Snapshot(fixture)));
        var changed = Snapshot(fixture);
        Check(!Has(fixture, changed, 0), "a changed curve invalidates retention during a unit-only solve");
        Check(await Result(Measure(fixture, changed, CancellationToken.None, 0)) && Details(fixture, changed, 0)?.Value == 11,
            "the changed curve is remeasured from its new captured values");
    }

    private static async Task VerifyReplacementAndDisposal(Assembly assembly)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        OneCurve(fixture, Polyline());
        fixture.Solve();
        await fixture.Ready(() => Snapshot(fixture).Rows.Count == 1);
        var before = Snapshot(fixture);
        Check(await Result(Measure(fixture, before, CancellationToken.None, 0)) && Details(fixture, before, 0)?.Value == 7,
            "replacement fixture starts with a measured captured curve");
        var capturedHolder = (GH_Curve)Sources(fixture)[0]!;
        capturedHolder.Value = new PolylineCurve(new[] { Point3d.Origin, new Point3d(10, 0, 0), new Point3d(10, 7, 0) });
        Check(!Has(fixture, before, 0) && Details(fixture, before, 0) == null,
            "replacing a captured holder immediately hides the old result");
        await Wait(() => !ReferenceEquals(before, Snapshot(fixture)));
        await fixture.Ready(() => Snapshot(fixture).Rows.Count == 1);
        var replacement = Snapshot(fixture);
        Check(!ReferenceEquals(replacement, before) && await Result(Measure(fixture, replacement, CancellationToken.None, 0)),
            "holder replacement invalidates and recaptures before another result is published");
        var oldHolder = (GH_Curve)Sources(fixture)[0]!;
        oldHolder.Value.Dispose();
        // Ensure the upstream input is usable even if GH shared the same holder.
        fixture.Source.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Curve { Value = Polyline() }, new GH_Path(0));
        Check(!Has(fixture, replacement, 0), "disposing the captured source hides its cached result");
        await Wait(() => !ReferenceEquals(replacement, Snapshot(fixture)));
        Check(!ReferenceEquals(replacement, Snapshot(fixture)), "a disposed source invalidates its snapshot generation");
    }

    private static async Task VerifyCapacityAcrossPanels(Assembly assembly)
    {
        using var first = new OutputChecks.Fixture(assembly);
        using var second = new OutputChecks.Fixture(assembly);
        OneCurve(first, Polyline()); OneCurve(second, Nurbs());
        first.Solve(); second.Solve();
        await first.Ready(() => Snapshot(first).Rows.Count == 1);
        await second.Ready(() => Snapshot(second).Rows.Count == 1);
        var firstSnapshot = Snapshot(first); var secondSnapshot = Snapshot(second);
        var prepare = assembly.GetType("BetterGhPanel.Runtime.GeometryHints", true)!.GetMethod("PrepareMeasurement", Static)!;
        var reserve = assembly.GetType("BetterGhPanel.Runtime.NurbsCandidateLimits", true)!.GetMethods(Static)
            .Single(method => method.Name == "TryReserve" && method.GetParameters().Length == 1);
        var held = new List<IDisposable>();
        try
        {
            bool sawPolyline = false, sawNurbs = false;
            for (int i = 0; ; i++)
            {
                var source = i % 2 == 0 ? Sources(first)[0]! : Sources(second)[0]!;
                var work = (IDisposable?)prepare.Invoke(null, new object[] { source, "mm", (int)Rhino.UnitSystem.Millimeters, 0.001 });
                if (work == null) break;
                sawPolyline |= i % 2 == 0; sawNurbs |= i % 2 != 0; held.Add(work);
            }
            IDisposable? remainder;
            while ((remainder = (IDisposable?)reserve.Invoke(null, new object[] { 1 })) != null) held.Add(remainder);
            Check(sawPolyline && sawNurbs && held.Count > 2, "both real candidate providers fill the same process-wide budget");
            RequestVisible(first, firstSnapshot, 0);
            using var cancellation = new CancellationTokenSource();
            var canceled = Measure(second, secondSnapshot, cancellation.Token, 0);
            await Wait(() => WaitingForCapacity(first) && WaitingForCapacity(second));
            Check(!Has(first, firstSnapshot, 0) && !canceled.IsCompleted,
                "cross-panel capacity exhaustion remains pending rather than becoming unavailable");
            cancellation.Cancel();
            Check(!await Result(canceled), "a capacity-blocked production request honors cancellation");
            var replaced = Measure(second, secondSnapshot, CancellationToken.None, 0);
            await Wait(() => WaitingForCapacity(second));
            OneCurve(second, new PolylineCurve(new[] { Point3d.Origin, new Point3d(2, 0, 0), new Point3d(2, 2, 0) }));
            second.Solve();
            Check(!await Result(replaced), "replacement cancels an old capacity-blocked production request");
            foreach (var item in held) item.Dispose();
            held.Clear();
            await Wait(() => Has(first, firstSnapshot, 0));
            Check(Details(first, firstSnapshot, 0)?.Value == 7, "releasing shared capacity asynchronously resumes the other panel");
            await second.Ready(() => !ReferenceEquals(secondSnapshot, Snapshot(second)));
            var latest = Snapshot(second);
            Check(await Result(Measure(second, latest, CancellationToken.None, 0)) && Details(second, latest, 0)?.Value == 4,
                "the replacement generation can measure after capacity is returned");
        }
        finally { foreach (var item in held) item.Dispose(); }
    }

    private static PolylineCurve Polyline() => new PolylineCurve(new[] { Point3d.Origin, new Point3d(3, 0, 0), new Point3d(3, 4, 0) });
    private static NurbsCurve Nurbs() => NurbsCurve.Create(false, 2, new[] { Point3d.Origin, new Point3d(5, 5, 0), new Point3d(10, 0, 0) });
    private static void OneCurve(OutputChecks.Fixture fixture, Curve curve)
    {
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Curve { Value = curve }, new GH_Path(0));
        fixture.Paired.PersistentData.Append(new GH_String("paired"), new GH_Path(0));
    }
    private static PanelSnapshot Snapshot(OutputChecks.Fixture fixture) =>
        (PanelSnapshot)fixture.Session.GetType().GetProperty("Snapshot")!.GetValue(fixture.Session)!;
    private static IGH_Goo?[] Sources(OutputChecks.Fixture fixture) =>
        (IGH_Goo?[])fixture.Component.GetType().GetField("_sourceItems", Private)!.GetValue(fixture.Component)!;
    private static bool Has(OutputChecks.Fixture fixture, PanelSnapshot snapshot, int position) =>
        (bool)fixture.Session.GetType().GetMethod("HasGeometryMeasurement", Private)!.Invoke(fixture.Session, new object[] { snapshot, position })!;
    private static PanelGeometryInfo? Details(OutputChecks.Fixture fixture, PanelSnapshot snapshot, int position) =>
        (PanelGeometryInfo?)fixture.Session.GetType().GetMethod("GetGeometryDetails", Private)!.Invoke(fixture.Session, new object[] { snapshot, position });
    private static void RequestVisible(OutputChecks.Fixture fixture, PanelSnapshot snapshot, params int[] positions) =>
        fixture.Session.GetType().GetMethod("RequestGeometryMeasurements", Private)!.Invoke(fixture.Session, new object[] { snapshot, positions });
    private static Task<bool> Measure(OutputChecks.Fixture fixture, PanelSnapshot snapshot, CancellationToken token, params int[] positions) =>
        (Task<bool>)fixture.Session.GetType().GetMethod("MeasureGeometryAsync", Private)!.Invoke(fixture.Session, new object[] { snapshot, positions, token })!;
    private static bool WaitingForCapacity(OutputChecks.Fixture fixture)
    {
        var queue = fixture.Session.GetType().GetField("_geometry", Private)!.GetValue(fixture.Session)!;
        return queue.GetType().GetField("_capacityWait", Private)!.GetValue(queue) != null;
    }
    private static async Task<bool> Result(Task<bool> task) { await Wait(() => task.IsCompleted); return await task; }
    private static async Task Wait(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Production curve fixture did not finish");
            await Task.Delay(10);
        }
    }
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
}
