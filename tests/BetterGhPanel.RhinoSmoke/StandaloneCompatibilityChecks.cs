using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.DocObjects.Custom;
using Rhino.FileIO;
using Rhino.Geometry;

// Real GH solutions through the production capture, preparation and worker queue.
internal static class StandaloneCompatibilityChecks
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
            await MetricsAndOutput(assembly);
            await UnitOnlyAndReentrantReplacement(assembly);
            await CallbackException(assembly);
            await GeometryContextChanges(assembly);
            await LoadedReference(assembly);
            MetadataRoundTrip(assembly);
        }
        finally { CompatibilityUserData.Hook = null; setEditor.Invoke(null, new object?[] { previous }); }
    }

    private static async Task GeometryContextChanges(Assembly assembly)
    {
        var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("An active Rhino document is required");
        var oldUnits = doc.ModelUnitSystem;
        double oldTolerance = doc.ModelAbsoluteTolerance;
        bool oldModified = doc.Modified;
        try
        {
            using var fixture = new OutputChecks.Fixture(assembly);
            Clear(fixture); Append(fixture, new GH_Brep(Triangle())); fixture.Solve();
            await fixture.Ready(() => Snapshot(fixture).Rows.Count == 1);
            var snapshot = Snapshot(fixture);
            var source = ((GH_Brep)Sources(fixture)[0]!).Value;
            Check(source.UserData.Add(new CompatibilityUserData { Tag = 1 }), "context callback fixture attached");
            int callbacks = 0;
            CompatibilityUserData.Hook = () =>
            {
                CompatibilityUserData.Hook = null; callbacks++;
                doc.ModelAbsoluteTolerance = oldTolerance * 2;
            };
            var pending = Measure(fixture, snapshot, 0);
            Check(!await Result(pending) && callbacks == 1 && !Has(fixture, snapshot, 0),
                "changing model tolerance during legacy preparation rejects the captured context");
            doc.ModelAbsoluteTolerance = oldTolerance;
            fixture.Solve();
            await fixture.Ready(() => !ReferenceEquals(snapshot, Snapshot(fixture)) && Snapshot(fixture).Rows.Count == 1);
            var stable = Snapshot(fixture);
            Check(await Result(Measure(fixture, stable, 0)) && Has(fixture, stable, 0), "unchanged model context measures normally");
            doc.ModelUnitSystem = oldUnits == Rhino.UnitSystem.Millimeters ? Rhino.UnitSystem.Meters : Rhino.UnitSystem.Millimeters;
            Check(!Has(fixture, stable, 0) && Details(fixture, stable, 0) == null,
                "changing model units after completion hides the result before an output-only reuse");
            fixture.Select(0);
            await Wait(() => !ReferenceEquals(stable, Snapshot(fixture)));
            Check(!ReferenceEquals(stable, Snapshot(fixture)), "an output-only request recaptures instead of reusing an obsolete model context");
        }
        finally
        {
            CompatibilityUserData.Hook = null;
            doc.ModelUnitSystem = oldUnits;
            doc.ModelAbsoluteTolerance = oldTolerance;
            doc.Modified = oldModified;
        }
    }

    private static async Task MetricsAndOutput(Assembly assembly)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        Clear(fixture);
        var poly = Poly();
        var triangle = Triangle();
        var plane = new PlaneSurface(Plane.WorldXY, new Interval(0, 10), new Interval(0, 10)).ToBrep();
        var solid = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
        var huge = new PolylineCurve(Enumerable.Range(0, 2049).Select(i => new Point3d(i, i % 2, 0)));
        var hugeNurbs = NurbsCurve.Create(false, 2, Enumerable.Range(0, 1025).Select(i => new Point3d(i, i % 2, 0)));
        Append(fixture, new GH_Curve(Nurbs()));
        Append(fixture, new GH_Curve(poly));
        Append(fixture, new GH_Surface(plane));
        Append(fixture, new GH_Surface(triangle));
        Append(fixture, new GH_Brep(triangle.DuplicateBrep()));
        Append(fixture, new GH_Brep(solid));
        Append(fixture, new GH_Curve(huge));
        Append(fixture, new GH_Curve(hugeNurbs));
        fixture.Solve();
        await fixture.Ready(() => Snapshot(fixture).Rows.Count == 8);
        var snapshot = Snapshot(fixture);
        Check(snapshot.Rows.All(row => row.IsValid && row.GeometryInfo?.CanMeasure == true), "ordinary and oversized standalone geometry is eligible");
        var sources = Sources(fixture);
        var capturedSolid = ((GH_Brep)sources[5]!).Value;
        var capturedPoly = (PolyCurve)((GH_Curve)sources[1]!).Value;
        Check(capturedSolid.UserData.Add(new CompatibilityUserData { Tag = 1 }) &&
            capturedPoly.SegmentCurve(0).UserData.Add(new CompatibilityUserData { Tag = 2 }), "captured root and nested metadata attached");
        var rootData = capturedSolid.UserData.Find(typeof(CompatibilityUserData));
        var nestedData = capturedPoly.SegmentCurve(0).UserData.Find(typeof(CompatibilityUserData));
        Check(await Result(Measure(fixture, snapshot, 0, 1, 2, 3, 4, 5, 6, 7)), "production queue measures all requested shapes");
        Check(Details(fixture, snapshot, 0)?.Value > 10 && Details(fixture, snapshot, 0)!.Details.Contains("Degree: 2") &&
            Near(Details(fixture, snapshot, 1)?.Value, 7), "NURBS and polycurve lengths retain degree and topology details");
        Check(Near(Details(fixture, snapshot, 2)?.Value, 100) && Details(fixture, snapshot, 2)!.Details.Contains("Trimming: Untrimmed") &&
            Near(Details(fixture, snapshot, 3)?.Value, 50) && Details(fixture, snapshot, 3)!.Details.Contains("Trimming: Trimmed") &&
            Near(Details(fixture, snapshot, 4)?.Value, 50) && Details(fixture, snapshot, 4)!.Details.Contains("Topology: Open") &&
            Near(Details(fixture, snapshot, 5)?.Value, 24) && Details(fixture, snapshot, 5)!.Details.Contains("Faces: 6"),
            "surface, trimmed/open Brep and solid metrics retain beta.7 details");
        Check(Near(Details(fixture, snapshot, 6)?.Value, 2048 * Math.Sqrt(2)) && Details(fixture, snapshot, 7)?.Value > 1000,
            "oversized valid polyline and NURBS curves use fallback length");
        var output = fixture.Component.Params.Output[0].VolatileData.AllData(false).ToArray();
        Check(output.Length == sources.Length && output.Select((item, i) => ReferenceEquals(item, sources[i])).All(x => x) &&
            ReferenceEquals(rootData, capturedSolid.UserData.Find(typeof(CompatibilityUserData))) &&
            ReferenceEquals(nestedData, capturedPoly.SegmentCurve(0).UserData.Find(typeof(CompatibilityUserData))),
            "explicit D output preserves captured holders and their original root and nested metadata");
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Length, Search = "> 1000" });
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 6, 7 }) && fixture.Component.Params.Output[0].VolatileData.DataCount == 2);
        Check(fixture.Component.Params.Output[0].VolatileData.AllData(false).SequenceEqual(new[] { sources[6], sources[7] }),
            "the oversized fallback length participates in filtering and retains the typed source output");
    }

    private static async Task UnitOnlyAndReentrantReplacement(Assembly assembly)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        Clear(fixture); Append(fixture, new GH_Brep(Triangle())); fixture.Solve();
        await fixture.Ready(() => Snapshot(fixture).Rows.Count == 1);
        var initial = Snapshot(fixture);
        Check(await Result(Measure(fixture, initial, 0)) && Near(Details(fixture, initial, 0)?.Value, 50), "initial fallback area is measured");
        // Change every host-held copy while preserving each holder. A unit-only
        // solve must not reuse a witnessless value even if GH reuses a holder.
        foreach (var holder in fixture.Source.PersistentData.AllData(false).OfType<GH_Brep>()
            .Concat(fixture.Source.VolatileData.AllData(false).OfType<GH_Brep>()).Concat(Sources(fixture).OfType<GH_Brep>()).Distinct())
            holder.Value.Transform(Transform.Scale(Point3d.Origin, 2));
        PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.SquareMillimetersToSquareMeters, -1);
        await fixture.Ready(() => !ReferenceEquals(initial, Snapshot(fixture)));
        var converted = Snapshot(fixture);
        Check(!Has(fixture, converted, 0), "unit-only snapshot drops the old unwitnessed result even for reused holders");
        Check(await Result(Measure(fixture, converted, 0)) && !Near(Details(fixture, converted, 0)?.Value, 50),
            "unit-only request remeasures changed geometry");

        var holderNow = (GH_Brep)Sources(fixture)[0]!;
        Check(holderNow.Value.UserData.Add(new CompatibilityUserData { Tag = 1 }), "reentrant callback fixture attached");
        PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.None, -1);
        await fixture.Ready(() => !ReferenceEquals(converted, Snapshot(fixture)));
        var before = Snapshot(fixture);
        var captured = (GH_Brep)Sources(fixture)[0]!;
        if (captured.Value.UserData.Find(typeof(CompatibilityUserData)) == null)
            Check(captured.Value.UserData.Add(new CompatibilityUserData { Tag = 1 }), "new captured holder has reentrant callback data");
        int callbacks = 0;
        CompatibilityUserData.Hook = () => { CompatibilityUserData.Hook = null; callbacks++; captured.Value = Triangle(); };
        var stale = Measure(fixture, before, 0);
        Check(!await Result(stale) && callbacks == 1 && !Has(fixture, before, 0) && Details(fixture, before, 0) == null,
            "reentrant holder replacement cancels the old measurement and cannot publish it");
        await Wait(() => !ReferenceEquals(before, Snapshot(fixture)));
        await fixture.Ready(() => Snapshot(fixture).Rows.Count == 1);
        var next = Snapshot(fixture);
        Check(await Result(Measure(fixture, next, 0)) && Details(fixture, next, 0)?.Value != null,
            "the recaptured generation can measure after reentrant replacement");
    }

    private static async Task CallbackException(Assembly assembly)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        Clear(fixture); Append(fixture, new GH_Brep(Triangle())); fixture.Solve();
        await fixture.Ready(() => Snapshot(fixture).Rows.Count == 1);
        var snapshot = Snapshot(fixture);
        var geometry = ((GH_Brep)Sources(fixture)[0]!).Value;
        Check(geometry.UserData.Add(new CompatibilityUserData { Tag = 1 }), "throwing callback fixture attached");
        var original = geometry.UserData.Find(typeof(CompatibilityUserData));
        int callbacks = 0;
        CompatibilityUserData.Hook = () => { CompatibilityUserData.Hook = null; callbacks++; throw new InvalidOperationException("fixture callback failure"); };
        await Result(Measure(fixture, snapshot, 0));
        Check(callbacks == 1 && ReferenceEquals(original, geometry.UserData.Find(typeof(CompatibilityUserData))),
            "callback exception leaves source metadata intact and does not strand the queue");
        PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.SquareMillimetersToSquareMeters, -1);
        await fixture.Ready(() => !ReferenceEquals(snapshot, Snapshot(fixture)));
        var next = Snapshot(fixture);
        Check(await Result(Measure(fixture, next, 0)) && Details(fixture, next, 0)?.Value != null,
            "a later generation recovers admission after a callback exception");
    }

    private static async Task LoadedReference(Assembly assembly)
    {
        var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("An active Rhino document is required");
        VerifyTemporaryDocumentCleanup(doc);
        using var temporary = new TemporaryDocumentObjects(doc);
        using var poly = Poly();
        using var triangle = Triangle();
        using var solid = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
        using var boundary = NurbsCurve.Create(true, 3, new[] { new Point3d(0, 0, 0), new Point3d(2, 0, 0),
            new Point3d(3, 1, 0), new Point3d(2, 3, 0), new Point3d(0, 3, 0), new Point3d(-1, 1, 0) });
        using var extrusion = Extrusion.Create(boundary, 4, true) ?? throw new InvalidOperationException("Referenced edge extrusion unavailable");
        using var edgeBrep = extrusion.ToBrep();
        int edgeIndex = -1;
        for (int i = 0; i < edgeBrep.Edges.Count; i++)
        {
            using var edgeCurve = edgeBrep.Edges[i].DuplicateCurve();
            if (edgeCurve?.GetType() == typeof(NurbsCurve) && edgeCurve.Degree > 1 && !edgeCurve.IsLinear()) { edgeIndex = i; break; }
        }
        Check(edgeIndex >= 0 && edgeBrep.Edges.Count > 1, "document edge fixture contains a nonanalytic edge and another valid edge");
        int otherEdge = (edgeIndex + 1) % edgeBrep.Edges.Count;
        var id = temporary.AddCurve(poly);
        var otherCurveId = temporary.AddCurve(poly);
        var surfaceId = temporary.AddBrep(triangle);
        var solidId = temporary.AddBrep(solid);
        var edgeId = temporary.AddBrep(edgeBrep);
        Check(id != Guid.Empty && otherCurveId != Guid.Empty && otherCurveId != id && surfaceId != Guid.Empty && solidId != Guid.Empty && edgeId != Guid.Empty,
            "all referenced geometry was added to the Rhino document");
        {
            var loadedCurve = new GH_Curve(id);
            var loadedSurface = new GH_Surface { ReferenceID = surfaceId };
            var loadedSolid = new GH_Brep { ReferenceID = solidId };
            var loadedEdge = new GH_Curve(edgeId, edgeIndex);
            Check(loadedCurve.LoadGeometry(doc) && loadedSurface.LoadGeometry(doc) && loadedSolid.LoadGeometry(doc) && loadedEdge.LoadGeometry(doc),
                "referenced holders explicitly load existing objects from the active document");
            // Grasshopper reloads references during collection, so the document
            // itself must contain the nonanalytic edge under test.
            double expectedEdgeLength = loadedEdge.Value.GetLength();
            Check(loadedEdge.Value.GetType() == typeof(NurbsCurve) && expectedEdgeLength > 0,
                "the actual loaded document edge is a measurable NURBS curve");
            using var fixture = new OutputChecks.Fixture(assembly);
            Clear(fixture);
            Append(fixture, loadedCurve);
            Append(fixture, loadedSurface);
            Append(fixture, loadedSolid);
            Append(fixture, loadedEdge);
            fixture.Solve();
            await fixture.Ready(() => Snapshot(fixture).Rows.Count == 4);
            var snapshot = Snapshot(fixture);
            var captured = Sources(fixture);
            Check(((GH_Curve)captured[0]!).ReferenceID == id && ((GH_Surface)captured[1]!).ReferenceID == surfaceId &&
                ((GH_Brep)captured[2]!).ReferenceID == solidId && ((GH_Curve)captured[3]!).ReferenceID == edgeId &&
                ((GH_Curve)captured[3]!).ReferenceEdge == edgeIndex && doc.Objects.FindId(id) != null &&
                doc.Objects.FindId(otherCurveId) != null && doc.Objects.FindId(surfaceId) != null &&
                doc.Objects.FindId(solidId) != null && doc.Objects.FindId(edgeId) != null,
                "the solved holders refer to existing loaded document objects");
            bool allValid = snapshot.Rows.All(row => row.IsValid);
            bool measured = false;
            try { measured = allValid && await Result(Measure(fixture, snapshot, 0, 1, 2, 3)); }
            finally { LogReferencedRows(fixture, snapshot, captured, allValid, measured); }
            Check(allValid, "all loaded referenced geometry rows are verified valid");
            Check(measured, "the production queue completes the loaded reference batch");
            Check(Near(Details(fixture, snapshot, 0)?.Value, 7), "the loaded referenced polycurve has length 7");
            Check(Near(Details(fixture, snapshot, 1)?.Value, 50), "the loaded referenced trimmed surface has area 50");
            Check(Near(Details(fixture, snapshot, 2)?.Value, 24), "the loaded referenced solid has volume 24");
            Check(((GH_Curve)captured[3]!).Value.GetType() == typeof(NurbsCurve) &&
                Near(Details(fixture, snapshot, 3)?.Value, expectedEdgeLength), "the loaded referenced NURBS edge retains its measured length");
            var edgeHolder = (GH_Curve)captured[3]!;
            Check(Has(fixture, snapshot, 3) && Has(fixture, snapshot, 0), "unchanged loaded references retain their measured values");
            var sameEdge = edgeHolder.Value;
            edgeHolder.ReferenceEdge = otherEdge;
            Check(ReferenceEquals(edgeHolder.Value, sameEdge) && !Has(fixture, snapshot, 3) && Details(fixture, snapshot, 3) == null,
                "changing only a loaded reference edge hides the old measurement even when its value object is unchanged");
            await Wait(() => !ReferenceEquals(snapshot, Snapshot(fixture)) && Snapshot(fixture).Rows.Count == 4);
            await fixture.Ready(() => Snapshot(fixture).Rows.Count == 4);
            var recovered = Snapshot(fixture);
            Check(await Result(Measure(fixture, recovered, 0)) && Has(fixture, recovered, 0),
                "the unchanged loaded curve is measured again after the edge retarget");
            var curveHolder = (GH_Curve)Sources(fixture)[0]!;
            var sameCurve = curveHolder.Value;
            curveHolder.ReferenceID = otherCurveId;
            Check(ReferenceEquals(curveHolder.Value, sameCurve) && !Has(fixture, recovered, 0) && Details(fixture, recovered, 0) == null,
                "retargeting a loaded reference hides the old measurement even when its value object is unchanged");
        }
    }

    private static void LogReferencedRows(OutputChecks.Fixture fixture, PanelSnapshot snapshot, IGH_Goo?[] sources, bool allValid, bool measured)
    {
        static string? Number(double? value) => value?.ToString("G17", CultureInfo.InvariantCulture);
        Console.WriteLine("REFERENCE_BATCH " + System.Text.Json.JsonSerializer.Serialize(new { allValid, measured }));
        for (int i = 0; i < snapshot.Rows.Count; i++)
        {
            var source = sources[i];
            var geometric = source as IGH_GeometricGoo;
            GeometryBase? value = source switch { GH_Curve curve => curve.Value, GH_Surface surface => surface.Value, GH_Brep brep => brep.Value, _ => null };
            var row = snapshot.Rows[i];
            var details = Details(fixture, snapshot, i);
            Console.WriteLine("REFERENCE_ROW " + System.Text.Json.JsonSerializer.Serialize(new
            {
                index = i, holder = source?.GetType().FullName, value = value?.GetType().FullName,
                referenceId = geometric?.ReferenceID, referenced = geometric?.IsReferencedGeometry, loaded = geometric?.IsGeometryLoaded,
                edge = (source as GH_Curve)?.ReferenceEdge, validity = row.Validity.ToString(),
                description = row.GeometryInfo?.Description, canMeasure = row.GeometryInfo?.CanMeasure,
                capturedValue = Number(row.GeometryInfo?.Value), capturedUnavailable = row.GeometryInfo?.MeasurementUnavailable,
                hasMeasurement = Has(fixture, snapshot, i), measuredValue = Number(details?.Value),
                measuredMetric = details?.MetricName, measuredUnavailable = details?.MeasurementUnavailable, measuredDetails = details?.Details
            }));
        }
        Console.Out.Flush();
    }

    private static void VerifyTemporaryDocumentCleanup(Rhino.RhinoDoc doc)
    {
        bool originalModified = doc.Modified;
        try
        {
            foreach (bool modified in new[] { false, true })
            {
                doc.Modified = modified;
                Guid id = Guid.Empty;
                try
                {
                    using var temporary = new TemporaryDocumentObjects(doc);
                    using var line = new LineCurve(Point3d.Origin, new Point3d(1, 0, 0));
                    id = temporary.AddCurve(line);
                    Console.WriteLine("DOCUMENT_CLEANUP before=" + modified + " afterAdd=" + doc.Modified);
                    Check(id != Guid.Empty && doc.Objects.FindId(id) != null, "failure-cleanup probe adds a real document object");
                    throw new ExpectedFixtureFailure();
                }
                catch (ExpectedFixtureFailure) { }
                var remaining = doc.Objects.FindId(id);
                Check(doc.Modified == modified && (remaining == null || remaining.IsDeleted),
                    "failure cleanup deletes temporary geometry and restores document modified=" + modified);
            }
        }
        finally { doc.Modified = originalModified; }
    }

    private sealed class ExpectedFixtureFailure : Exception { }

    private sealed class TemporaryDocumentObjects : IDisposable
    {
        private readonly Rhino.RhinoDoc _document;
        private readonly bool _modified;
        private readonly List<Guid> _ids = new List<Guid>();
        internal TemporaryDocumentObjects(Rhino.RhinoDoc document) { _document = document; _modified = document.Modified; }
        internal Guid AddCurve(Curve curve) => Track(_document.Objects.AddCurve(curve));
        internal Guid AddBrep(Brep brep) => Track(_document.Objects.AddBrep(brep));
        private Guid Track(Guid id) { if (id != Guid.Empty) _ids.Add(id); return id; }
        public void Dispose()
        {
            List<Exception>? errors = null;
            try
            {
                foreach (var id in _ids)
                {
                    try { if (!_document.Objects.Delete(id, true)) throw new InvalidOperationException("Could not delete temporary document object " + id); }
                    catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
                }
            }
            finally { _document.Modified = _modified; }
            if (errors != null) throw new AggregateException("Temporary document object cleanup failed", errors);
        }
    }

    private static void MetadataRoundTrip(Assembly assembly)
    {
        using var poly = Poly();
        Check(poly.UserData.Add(new CompatibilityUserData { Tag = 1 }) &&
            poly.SegmentCurve(0).UserData.Add(new CompatibilityUserData { Tag = 2 }), "root and nested serializable metadata attached");
        var root = poly.UserData.Find(typeof(CompatibilityUserData));
        var nested = poly.SegmentCurve(0).UserData.Find(typeof(CompatibilityUserData));
        CompatibilityUserData.RootDuplicates = CompatibilityUserData.NestedDuplicates = 0;
        var duplicate = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!.GetMethod("DuplicateGeometry", Static)!;
        using var copy = (PolyCurve?)duplicate.Invoke(null, new object[] { new GH_Curve { Value = poly } });
        Check(copy != null && CompatibilityUserData.RootDuplicates > 0 && CompatibilityUserData.NestedDuplicates > 0 &&
            ReferenceEquals(root, poly.UserData.Find(typeof(CompatibilityUserData))) &&
            ReferenceEquals(nested, poly.SegmentCurve(0).UserData.Find(typeof(CompatibilityUserData))),
            "production geometry-copy preparation invokes root and nested callbacks and preserves source metadata");
        using var file = new File3dm();
        Guid archiveId = file.Objects.Add(copy!, null);
        Check(archiveId != Guid.Empty, "copied geometry enters the archive");
        CompatibilityUserData.Writes = CompatibilityUserData.Reads = 0;
        byte[] bytes = file.ToByteArray(new File3dmWriteOptions { Version = 8 });
        Check(bytes != null && bytes.Length > 0, "the metadata archive was written");
        using var restored = File3dm.FromByteArray(bytes!);
        Console.WriteLine("METADATA_ARCHIVE bytes=" + bytes!.Length + " writes=" + CompatibilityUserData.Writes +
            " reads=" + CompatibilityUserData.Reads + " restored=" + (restored != null));
        Console.Out.Flush();
        Check(restored != null, "the metadata archive is readable");
        // File3dm's object table implements IList, but model geometry is not
        // indexed; LINQ First() may use its indexer and return null. Use the
        // exact archived object identity instead.
        var archivedObject = restored!.Objects.FindId(archiveId);
        Check(archivedObject?.Geometry is PolyCurve, "the archived polycurve is found by its object identity");
        var roundTrip = (PolyCurve)archivedObject!.Geometry;
        Check(CompatibilityUserData.Writes >= 2 && CompatibilityUserData.Reads >= 2 &&
            ((CompatibilityUserData?)roundTrip.UserData.Find(typeof(CompatibilityUserData)))?.Tag == 1 &&
            ((CompatibilityUserData?)roundTrip.SegmentCurve(0).UserData.Find(typeof(CompatibilityUserData)))?.Tag == 2,
            "representative serializable root and nested metadata roundtrip through a native archive");
    }

    private static void Clear(OutputChecks.Fixture f) { f.Source.PersistentData.Clear(); f.Paired.PersistentData.Clear(); }
    private static void Append(OutputChecks.Fixture f, IGH_Goo value)
    { f.Source.PersistentData.Append(value, new GH_Path(0)); f.Paired.PersistentData.Append(new GH_String("paired"), new GH_Path(0)); }
    private static PolyCurve Poly()
    {
        var poly = new PolyCurve();
        if (!poly.Append(new Line(Point3d.Origin, new Point3d(3, 0, 0))) ||
            !poly.Append(new Line(new Point3d(3, 0, 0), new Point3d(3, 4, 0)))) throw new InvalidOperationException("polycurve fixture");
        return poly;
    }
    private static NurbsCurve Nurbs() => NurbsCurve.Create(false, 2, new[] { Point3d.Origin, new Point3d(5, 5, 0), new Point3d(10, 0, 0) });
    private static Brep Triangle() => Brep.CreatePlanarBreps(new PolylineCurve(new[] { Point3d.Origin, new Point3d(10, 0, 0),
        new Point3d(0, 10, 0), Point3d.Origin }), 1e-8).Single();
    private static bool Near(double? actual, double expected) => actual.HasValue && Math.Abs(actual.Value - expected) < Math.Max(1e-7, Math.Abs(expected) * 1e-7);
    private static PanelSnapshot Snapshot(OutputChecks.Fixture f) => (PanelSnapshot)f.Session.GetType().GetProperty("Snapshot")!.GetValue(f.Session)!;
    private static IGH_Goo?[] Sources(OutputChecks.Fixture f) => (IGH_Goo?[])f.Component.GetType().GetField("_sourceItems", Private)!.GetValue(f.Component)!;
    private static bool Has(OutputChecks.Fixture f, PanelSnapshot s, int i) => (bool)f.Session.GetType().GetMethod("HasGeometryMeasurement", Private)!.Invoke(f.Session, new object[] { s, i })!;
    private static PanelGeometryInfo? Details(OutputChecks.Fixture f, PanelSnapshot s, int i) => (PanelGeometryInfo?)f.Session.GetType().GetMethod("GetGeometryDetails", Private)!.Invoke(f.Session, new object[] { s, i });
    private static Task<bool> Measure(OutputChecks.Fixture f, PanelSnapshot s, params int[] positions) =>
        (Task<bool>)f.Session.GetType().GetMethod("MeasureGeometryAsync", Private)!.Invoke(f.Session, new object[] { s, positions, CancellationToken.None })!;
    private static async Task<bool> Result(Task<bool> task) { await Wait(() => task.IsCompleted); return await task; }
    private static async Task Wait(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Standalone compatibility fixture timed out"); await Task.Delay(10); }
    }
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
}

[Guid("8ac6c41f-2bd7-4d13-b2bb-309efda3a5ef")]
public sealed class CompatibilityUserData : UserData
{
    public int Tag;
    internal static int RootDuplicates, NestedDuplicates, Writes, Reads;
    internal static Action? Hook;
    public override string Description => "Serializable compatibility fixture";
    public override bool ShouldWrite => true;
    protected override bool Write(BinaryArchiveWriter archive) { Interlocked.Increment(ref Writes); archive.WriteInt(Tag); return true; }
    protected override bool Read(BinaryArchiveReader archive) { Interlocked.Increment(ref Reads); Tag = archive.ReadInt(); return true; }
    protected override void OnDuplicate(UserData source)
    {
        Tag = ((CompatibilityUserData)source).Tag;
        if (Tag == 1) Interlocked.Increment(ref RootDuplicates); else Interlocked.Increment(ref NestedDuplicates);
        Hook?.Invoke();
    }
}
