using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Point = System.Drawing.Point;

internal static class GeometryHintChecks
{
    internal static async Task Run(Assembly assembly)
    {
        var hints = assembly.GetType("BetterGhPanel.Runtime.GeometryHints", true)!;
        Func<PanelGeometryInfo?>? Prepare(IGH_Goo goo)
        {
            var prepared = hints.GetMethod("PrepareMeasurement", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { goo, "mm", (int)Rhino.UnitSystem.Millimeters, 0.001 });
            if (prepared == null) return null;
            return () =>
            {
                var arguments = new object?[] { null };
                var claimed = (bool)prepared.GetType().GetMethod("TryRun", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(prepared, arguments)!;
                return claimed ? (PanelGeometryInfo?)arguments[0] : null;
            };
        }
        string? Details(IGH_Goo goo) => Prepare(goo)?.Invoke()?.FormatDetails(PanelUnitConversion.None, -1);
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
        var lineDetails = Details(lineCurve)!;
        var nurbsDetails = Details(linearNurbs)!;
        Check(lineDetails.StartsWith("Length: 5 mm\n", StringComparison.Ordinal) && lineDetails.Contains("Shape: Linear") &&
            nurbsDetails.Contains("Shape: Linear") && nurbsDetails.Contains("Degree: 1") && nurbsDetails.Contains("Topology: Open"),
            "Deferred linearity adds shape information without losing curve degree or topology");
        var circleDetails = Details(new GH_Curve(new ArcCurve(circle)))!;
        Check(circleDetails.StartsWith("Length:", StringComparison.Ordinal) && circleDetails.Contains("Shape: Circle") && circleDetails.Contains("Topology: Closed"),
            "Circle measurement reports closed shape and circumference");
        var curved = new GH_Curve(NurbsCurve.Create(false, 2, new[] { Point3d.Origin, new Point3d(5, 5, 0), new Point3d(10, 0, 0) }));
        Check(Details(curved)!.Contains("Shape: Curve") && Details(curved)!.Contains("Degree: 2"), "Curved NURBS retain their degree and are not labeled linear");
        var surfaceDetails = Details(surface)!;
        var solidDetails = Details(solid)!;
        Check(surfaceDetails.StartsWith("Area: 100 mm²\n", StringComparison.Ordinal) && surfaceDetails.Contains("Trimming: Untrimmed") &&
            surfaceDetails.Contains("Planarity: Planar") && surfaceDetails.Contains("Degree (U/V): 1/1"),
            "Surface measurements retain trimming and degree and add planarity in labeled details");
        Check(solidDetails.StartsWith("Volume: 24 mm³\n", StringComparison.Ordinal) && solidDetails.Contains("Topology: Closed solid") &&
            solidDetails.Contains("Faces: 6") && solidDetails.Contains("Edges: 12") && solidDetails.Contains("Vertices: 8"),
            "Solid volume keeps the Brep topology, face count and additional construction metadata");
        Check(Prepare(linearNurbs)!()!.Description.StartsWith(snapshot.Rows[2].GeometryInfo!.Description + " ·", StringComparison.Ordinal) &&
            Prepare(solid)!()!.Description == snapshot.Rows[9].GeometryInfo!.Description,
            "Metrics extend captured curve and Brep summaries instead of replacing them");
        var polyline = new GH_Curve(new PolylineCurve(new[] { Point3d.Origin, new Point3d(3, 0, 0), new Point3d(3, 4, 0) }));
        var polylineInfo = Prepare(polyline)!()!;
        Check(polylineInfo.Description.Contains("2 segments") && polylineInfo.Description.Contains("open") && polylineInfo.Value == 7 &&
            polylineInfo.Details.Contains("Segments: 2"), "Polyline length keeps its segment count and open topology");
        var triangle = Brep.CreatePlanarBreps(new PolylineCurve(new[] { Point3d.Origin, new Point3d(10, 0, 0),
            new Point3d(0, 10, 0), Point3d.Origin }), 1e-8).Single();
        var trimmedDetails = Details(new GH_Surface(triangle))!;
        var openDetails = Details(new GH_Brep(triangle))!;
        Check(trimmedDetails.StartsWith("Area: 50 mm²\n", StringComparison.Ordinal) && trimmedDetails.Contains("Trimming: Trimmed") &&
            trimmedDetails.Contains("Planarity: Planar") && openDetails.StartsWith("Area: 50 mm²\n", StringComparison.Ordinal) && openDetails.Contains("Faces: 1"),
            "Trimmed planar area keeps metadata and measures the trimmed region instead of the underlying rectangle");
        var sphere = new GH_Surface(new Sphere(Point3d.Origin, 2).ToBrep());
        Check(Details(sphere)!.Contains("Planarity: Nonplanar"), "Curved surfaces are not incorrectly labeled planar");
        var sphereSolid = Details(new GH_Brep(sphere.Value))!;
        Check(sphereSolid.StartsWith("Volume:", StringComparison.Ordinal) && sphereSolid.Contains("Planarity: Nonplanar") &&
            sphereSolid.Contains("Faces: 1"), "Single-face solids retain planarity and face count when their volume is measured");
        Check(Details(unknown) == null, "Measurement never formats third-party goo");
        var copiedCurve = Prepare(linearNurbs)!;
        linearNurbs.Value.Transform(Transform.Scale(Point3d.Origin, 2));
        var curveResult = await Task.Run(copiedCurve);
        Check(curveResult?.Value == 5 && linearNurbs.Value.GetLength() == 10,
            "Background measurement owns a deep curve copy unaffected by later source edits");
        var copiedSurface = Prepare(surface)!;
        surface.Value.Transform(Transform.Scale(Point3d.Origin, 2));
        Check((await Task.Run(copiedSurface))?.Value == 100,
            "Background area uses an owned trimmed Brep copy instead of live source goo");
        await VerifyCache(assembly, snapshot);
        await VerifyInactiveDocument(assembly, snapshot);
    }

    private static async Task VerifyCache(Assembly assembly, PanelSnapshot snapshot)
    {
        var type = assembly.GetType("BetterGhPanel.Runtime.PanelSession", true)!;
        using var lifetime = (IDisposable)Activator.CreateInstance(type, new object[] { (Action)(() => { }) })!;
        var get = type.GetMethod("GetGeometryDetails", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var set = type.GetMethod("SetGeometryPreparationResolver", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var attach = type.GetMethod("AttachSnapshot")!;
        var request = type.GetMethod("RequestGeometryMeasurements", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var has = type.GetMethod("HasGeometryMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        PanelGeometryInfo? Get(PanelSnapshot source, int position) => (PanelGeometryInfo?)get.Invoke(lifetime, new object[] { source, position });
        bool Has(PanelSnapshot source, int position) => (bool)has.Invoke(lifetime, new object[] { source, position })!;
        void Request(PanelSnapshot source, params int[] positions) => request.Invoke(lifetime, new object[] { source, positions });
        int calls = 0, active = 0, maximumActive = 0, prepareThread = 0, computeThread = 0;
        int uiThread = Environment.CurrentManagedThreadId;
        var prepared = new List<int>();
        using var gate = new ManualResetEventSlim(false);
        ManualResetEventSlim? hold = gate;
        set.Invoke(lifetime, new[] { HostMeasurementPreparation.Resolver(assembly, position =>
        {
            prepareThread = Environment.CurrentManagedThreadId;
            prepared.Add(position);
            var capturedHold = hold;
            return () =>
            {
                computeThread = Environment.CurrentManagedThreadId;
                int running = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, running);
                Interlocked.Increment(ref calls);
                try
                {
                    if (capturedHold != null && !capturedHold.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test gate timed out");
                    if (position == 8) throw new InvalidOperationException("Unavailable geometry");
                    if (position == 9) return null;
                    return new PanelGeometryInfo("measured " + position, position, units: "mm");
                }
                finally { Interlocked.Decrement(ref active); }
            };
        }) });
        attach.Invoke(lifetime, new object[] { snapshot });
        Check(calls == 0, "Attaching a snapshot never invokes geometry measurements");
        Check(Get(snapshot, 2) == null && Get(snapshot, 2) == null && calls == 0, "Hover reads cached details without calculating anything");
        Request(snapshot, 2, 2, -1, 100, 11, 0);
        Check(prepared.Count == 0 && calls == 0, "Viewport requests never copy or calculate geometry inline during paint");
        await WaitUntil(() => Volatile.Read(ref calls) == 1);
        Check(prepareThread == uiThread && computeThread != uiThread && Get(snapshot, 2) == null && !Has(snapshot, 2),
            "Preparation stays on the UI thread while calculation runs on a worker and the UI remains responsive");
        Request(snapshot, 2, 2);
        var converted = new PanelSnapshot(snapshot.Rows, snapshot.Branches, PanelUnitConversion.MillimetersToMeters);
        type.GetMethod("AttachConvertedSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(lifetime, new object[] { converted, (Func<int, bool>)(position => position == 2) });
        hold = null;
        gate.Set();
        await WaitUntil(() => Has(converted, 2));
        Check(Get(converted, 2)?.FormatCompact(converted.UnitConversion, -1) == "measured 2 · L 0.002 m" && calls == 1,
            "Unit-only changes retain in-flight work and reformat its raw metric without remeasurement");
        Request(converted, 2, 8, 8, 9, 9, -1, 100, 11, 0);
        await WaitUntil(() => Has(converted, 8) && Has(converted, 9));
        Request(converted, 2, 8, 9);
        await Task.Delay(80);
        Check(Get(converted, 2)?.Value == 2 && Get(converted, 8) == null && Get(converted, 9) == null && calls == 3,
            "Successful, failed and unavailable results are cached; invalid and analytic rows never start work");

        var newer = new PanelSnapshot(snapshot.Rows, snapshot.Branches);
        attach.Invoke(lifetime, new object[] { newer });
        Check(Get(newer, 2) == null && Get(converted, 2) == null, "A new solution invalidates old measurements");
        Request(converted, 2);
        await Task.Delay(80);
        Check(calls == 3, "An obsolete view cannot request measurements of new geometry");
        gate.Reset();
        hold = gate;
        Request(newer, 2, 8, 9);
        await WaitUntil(() => Volatile.Read(ref calls) == 4);
        Request(newer, 9);
        hold = null;
        gate.Set();
        await WaitUntil(() => Has(newer, 9));
        Check(calls == 5 && !Has(newer, 8) && Get(newer, 2)?.Value == 2,
            "Scrolling replaces pending work and skips rows that left the viewport");

        var obsolete = new PanelSnapshot(snapshot.Rows, snapshot.Branches);
        attach.Invoke(lifetime, new object[] { obsolete });
        gate.Reset();
        hold = gate;
        Request(obsolete, 2);
        await WaitUntil(() => Volatile.Read(ref calls) == 6);
        var current = new PanelSnapshot(snapshot.Rows, snapshot.Branches);
        attach.Invoke(lifetime, new object[] { current });
        Request(current, 2);
        await Task.Delay(80);
        Check(Get(current, 2) == null && calls == 6, "New solutions reject stale publication and wait for the existing worker");
        hold = null;
        gate.Set();
        await WaitUntil(() => Has(current, 2));
        Check(Get(current, 2)?.Value == 2 && calls == 7 && maximumActive == 1,
            "Only one owned geometry operation runs per session across solution changes");

        gate.Reset();
        hold = gate;
        Request(current, 8, 9);
        await WaitUntil(() => Volatile.Read(ref calls) == 8);
        lifetime.Dispose();
        gate.Set();
        await WaitUntil(() => Volatile.Read(ref active) == 0);
        Request(current, 9);
        await Task.Delay(80);
        Check(Get(current, 2) == null && !Has(current, 8) && calls == 8,
            "Disposal rejects in-flight results and prevents queued geometry work from starting");
    }

    private static async Task VerifyInactiveDocument(Assembly assembly, PanelSnapshot snapshot)
    {
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        using var document = new GH_Document();
        var session = PanelTestAdapter.Session(component);
        var type = session.GetType();
        var request = type.GetMethod("RequestGeometryMeasurements", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var has = type.GetMethod("HasGeometryMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool Has(int position) => (bool)has.Invoke(session, new object[] { snapshot, position })!;
        int calls = 0;
        using var gate = new ManualResetEventSlim(false);
        type.GetMethod("SetGeometryDetailResolver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, new object[] { (Func<int, Func<PanelGeometryInfo?>?>)(position => () =>
            {
                Interlocked.Increment(ref calls);
                if (!gate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test gate timed out");
                return new PanelGeometryInfo("measured " + position, position, units: "mm");
            }) });
        type.GetMethod("AttachSnapshot")!.Invoke(session, new object[] { snapshot });
        try
        {
            request.Invoke(session, new object[] { snapshot, new[] { 2, 8, 9 } });
            component.DocumentContextChanged(document, GH_DocumentContext.Unloaded);
            await Task.Delay(160);
            Check(calls == 0, "Switching tabs before the deferred callback prevents geometry preparation entirely");
            request.Invoke(session, new object[] { snapshot, new[] { 2, 8, 9 } });
            await WaitUntil(() => Volatile.Read(ref calls) == 1);
            component.DocumentContextChanged(document, GH_DocumentContext.Unloaded);
            gate.Set();
            await WaitUntil(() => Has(2));
            await Task.Delay(160);
            Check(calls == 1 && !Has(8) && !Has(9), "Switching document tabs stops queued offscreen measurements");
            Check(ReferenceEquals(snapshot, type.GetProperty("Snapshot")!.GetValue(session)),
                "Tab switches retain the snapshot and the completed raw measurement");
            request.Invoke(session, new object[] { snapshot, new[] { 2, 8 } });
            await WaitUntil(() => Has(8));
            Check(calls == 2 && Has(2), "Rendering the panel again resumes visible work and reuses cached measurements");
        }
        finally
        {
            gate.Set();
            component.DocumentContextChanged(document, GH_DocumentContext.Close);
        }
    }

    internal static async Task VerifyCanvasViewport(Assembly assembly)
    {
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        component.Attributes.Pivot = new PointF(40, 40);
        component.Attributes.ExpireLayout();
        component.Attributes.PerformLayout();
        using var document = new GH_Document();
        using var canvas = new GH_Canvas { Size = new Size(500, 500) };
        var session = PanelTestAdapter.Session(component);
        var type = session.GetType();
        var attach = type.GetMethod("AttachSnapshot")!;
        var has = type.GetMethod("HasGeometryMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var rows = Enumerable.Range(0, 3).Select(position => new PanelRow(position, "{0}", "Surface", "Srf", null,
            geometryInfo: new PanelGeometryInfo("Srf", canMeasure: true))).ToArray();
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", rows.Length) });
        bool Has(int position) => (bool)has.Invoke(session, new object[] { snapshot, position })!;
        var prepared = new List<int>();
        int calls = 0;
        using var gate = new ManualResetEventSlim(false);
        type.GetMethod("SetGeometryDetailResolver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, new object[] { (Func<int, Func<PanelGeometryInfo?>?>)(position =>
            {
                prepared.Add(position);
                return () =>
                {
                    Interlocked.Increment(ref calls);
                    if (!gate.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test gate timed out");
                    return new PanelGeometryInfo("Srf", position, 2, "mm", "Area", "A");
                };
            }) });
        void Paint(Point target, Size size, float zoom = 1)
        {
            canvas.Size = size;
            canvas.Viewport.Size = size;
            canvas.Viewport.Target = target;
            canvas.Viewport.Zoom = zoom;
            canvas.Viewport.ComputeProjection();
            using var bitmap = new Bitmap(size.Width, size.Height);
            using var graphics = Graphics.FromImage(bitmap);
            canvas.Viewport.ApplyProjection(graphics);
            PanelTestAdapter.Render(component, canvas, graphics);
        }
        var fullSize = new Size(500, 500);
        var hiddenTarget = new Point(-1000, -1000);
        try
        {
            attach.Invoke(session, new object[] { snapshot });
            await WaitUntil(() => !(bool)type.GetProperty("IsBusy")!.GetValue(session)!);
            Paint(hiddenTarget, fullSize);
            await Task.Delay(160);
            Check(prepared.Count == 0 && calls == 0, "Offscreen panel rendering never prepares or measures geometry");

            Paint(Point.Empty, fullSize);
            var first = PanelTestAdapter.RowBounds(component, 0);
            var second = PanelTestAdapter.RowBounds(component, 1);
            Paint(hiddenTarget, fullSize);
            await Task.Delay(160);
            Check(prepared.Count == 0, "Panning out before the deferred callback clears queued geometry entirely");

            Paint(new Point(0, -(int)second.Y), new Size(500, (int)second.Height));
            await WaitUntil(() => Volatile.Read(ref calls) == 1);
            Check(prepared.SequenceEqual(new[] { 1 }) && !Has(0) && !Has(2),
                "A partially visible panel measures only rows intersecting the canvas viewport");
            Check(PanelTestAdapter.RowBounds(component, 0) == first && PanelTestAdapter.RowBounds(component, 1) == second &&
                PanelTestAdapter.Offset(component) == 0, "Canvas clipping preserves row layout and the panel scroll position");
            Paint(Point.Empty, fullSize);
            Paint(hiddenTarget, fullSize);
            gate.Set();
            await WaitUntil(() => Has(1));
            await Task.Delay(160);
            Check(prepared.SequenceEqual(new[] { 1 }) && !Has(0) && !Has(2),
                "Panning away during a measurement stops queued rows and retains the finished metric");

            Paint(Point.Empty, fullSize);
            await WaitUntil(() => Has(0) && Has(2));
            Check(calls == 3 && prepared.SequenceEqual(new[] { 1, 0, 2 }),
                "Panning back resumes visible measurements and reuses the cached metric");

            snapshot = new PanelSnapshot(rows, snapshot.Branches);
            attach.Invoke(session, new object[] { snapshot });
            await WaitUntil(() => !(bool)type.GetProperty("IsBusy")!.GetValue(session)!);
            Paint(new Point(0, -40), new Size(500, 50));
            await Task.Delay(160);
            Check(calls == 3, "Seeing only a panel header never queues geometry below the viewport");
            Paint(Point.Empty, fullSize);
            Paint(Point.Empty, fullSize, 0.25f);
            await Task.Delay(160);
            Check(calls == 3, "Zooming below the row display threshold clears queued geometry");
        }
        finally
        {
            gate.Set();
            component.DocumentContextChanged(document, GH_DocumentContext.Close);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Asynchronous geometry work did not complete");
            await Task.Delay(10);
        }
    }

    private static void Check(bool condition, string description) => SmokeAssertions.Check(condition, description);

    private sealed class UnformattedGoo : GH_Goo<string>
    {
        public override bool IsValid => true;
        public override string TypeName => "Widget";
        public override string TypeDescription => "Test third-party data";
        public override IGH_Goo Duplicate() => new UnformattedGoo();
        public override string ToString() => throw new InvalidOperationException("Do not format third-party goo during capture or hover");
    }
}
