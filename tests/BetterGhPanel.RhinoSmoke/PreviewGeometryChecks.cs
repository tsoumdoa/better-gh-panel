using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.FileIO;
using Rhino.DocObjects.Custom;
using Rhino.Geometry;
using Forms = System.Windows.Forms;

// Plugin-controlled fixtures. Host collection and display are checked separately.
internal static class PreviewGeometryChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    internal static async Task Run(Assembly assembly)
    {
        FixtureUserData.EnsureRegistered();
        object highlight = Activator.CreateInstance(assembly.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!, true)!;
        var clipboard = Forms.Clipboard.GetDataObject();
        try
        {
            VerifyOrdinaryGeometry(assembly, highlight);
            VerifyNurbsUserData(assembly, highlight);
            VerifyNestedMetadata(assembly, highlight);
            VerifySizeAndEviction(assembly, highlight);
            VerifyLoadedReference(assembly, highlight);
            await VerifyMetadataPaste(assembly);
        }
        finally
        {
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            if (clipboard == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(clipboard, true);
            PolylineSafetyUserData.Reset(false);
        }
    }

    private static void VerifyOrdinaryGeometry(Assembly assembly, object highlight)
    {
        using var nurbs = RationalCurve();
        nurbs.UserDictionary.Name = "Panel preview test";
        nurbs.UserDictionary.Version = 7;
        nurbs.UserDictionary.Set("source", "curve metadata");
        nurbs.UserDictionary.Set("revision", 42);
        using var poly = new PolyCurve();
        Check(poly.Append(new Line(Point3d.Origin, new Point3d(3, 0, 0))) &&
            poly.Append(new Line(new Point3d(3, 0, 0), new Point3d(4, 2, 0))), "ordinary polycurve is valid");
        using var surface = new PlaneSurface(Plane.WorldXY, new Interval(0, 4), new Interval(0, 5)).ToBrep();
        using var solid = new Box(new BoundingBox(new Point3d(10, 0, 0), new Point3d(12, 3, 4))).ToBrep();
        using var control = Mesh.CreateFromBox(new BoundingBox(new Point3d(20, 0, 0), new Point3d(22, 2, 2)), 1, 1, 1);
        using var subd = SubD.CreateFromMesh(control) ?? throw new InvalidOperationException("SubD fixture could not be constructed");
        Check(nurbs.IsValid && surface.IsValid && solid.IsValid && subd.IsValid, "ordinary NURBS, surface, solid and SubD fixtures are valid");
        var curve = new GH_Curve { Value = nurbs };
        var polyHolder = new GH_Curve { Value = poly };
        var surfaceHolder = new GH_Surface { Value = surface };
        var solidHolder = new GH_Brep { Value = solid };
        var subdHolder = new GH_SubD { Value = subd };
        var values = new IGH_Goo?[] { curve, polyHolder, surfaceHolder, solidHolder, subdHolder };
        Set(highlight, values, new[] { 0, 1, 2, 3, 4 }, new[] { 0, 2 });
        Check(Count(highlight) == 5 && Box(highlight).Contains(subd.GetBoundingBox(false).Center) &&
            Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
            "ordinary NURBS, polycurve, surface, Brep and SubD receive bounded owned previews");
        var oldBounds = Box(highlight);
        Check(nurbs.Points.SetPoint(1, new Point4d(40, 60, 0, 2)), "live NURBS changes after admission");
        Check(Box(highlight).Equals(oldBounds), "in-place NURBS mutation leaves the admitted preview bounds unchanged");
        Set(highlight, values, new[] { 0, 1, 2, 3, 4 }, new[] { 0 });
        Check(!Box(highlight).Equals(oldBounds), "a new generation rebuilds the NURBS snapshot and bounds together");

        object copied = Copy(assembly, values);
        if (!Property<bool>(copied, "Success") || Property<int>(copied, "Count") != 5 || Property<int>(copied, "Skipped") != 0)
        {
            Console.WriteLine("Combined ordinary geometry copy: " + DescribeCopy(copied));
            for (int i = 0; i < values.Length; i++)
                Console.WriteLine("Ordinary geometry row " + i + " (" + values[i]!.GetType().Name + "): " +
                    DescribeCopy(Copy(assembly, new[] { values[i] })));
            DiagnoseDictionaryArchive(assembly, nurbs);
        }
        Check(Property<bool>(copied, "Success") && Property<int>(copied, "Count") == 5 && Property<int>(copied, "Skipped") == 0,
            "explicit geometry copy retains all five ordinary native geometry rows: " + DescribeCopy(copied));
        using var file = ClipboardArchive();
        var geometries = ArchiveGeometry(file);
        Check(geometries.Length == 5 && geometries[0] is NurbsCurve && geometries[1] is PolyCurve &&
            geometries[2] is Brep && geometries[3] is Brep && geometries[4] is SubD,
            "native archive retains the original geometry types");
        Check(geometries[0].UserDictionary.Name == "Panel preview test" && geometries[0].UserDictionary.Version == 7 &&
            geometries[0].UserDictionary.GetInteger("revision") == 42 && geometries[0].UserDictionary.GetString("source") == "curve metadata" &&
            nurbs.UserDictionary.GetString("source") == "curve metadata", "clipboard round trip preserves source dictionary metadata");
        Check(geometries[2].GetBoundingBox(false).Equals(surface.GetBoundingBox(false)) &&
            geometries[3].GetBoundingBox(false).Equals(solid.GetBoundingBox(false)), "surface and solid clipboard geometry preserve their bounds");
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
    }

    private static void VerifyNurbsUserData(Assembly assembly, object highlight)
    {
        using var nurbs = RationalCurve();
        Check(nurbs.UserData.Add(new PolylineSafetyUserData()) && nurbs.HasUserData,
            "hostile NURBS UserData is attached before the plugin call");
        var holder = new GH_Curve { Value = nurbs };
        var group = new GH_GeometryGroup(); group.Objects.Add(holder);
        PolylineSafetyUserData.Reset(true);
        Set(highlight, new IGH_Goo?[] { holder, group }, new[] { 0, 1 }, new[] { 0 });
        Check(Count(highlight) == 2 && nurbs.HasUserData && PolylineSafetyUserData.Callbacks == 0,
            "standalone and grouped NURBS previews reconstruct values without custom UserData callbacks");
        object copied = Copy(assembly, new IGH_Goo?[] { holder });
        Check(!Property<bool>(copied, "Success") && Property<int>(copied, "Skipped") == 1 && nurbs.HasUserData &&
            Property<string>(copied, "Error").Contains("custom") && PolylineSafetyUserData.Callbacks > 0,
            "explicit copy reports the throwing custom callback and leaves the source metadata intact");
        PolylineSafetyUserData.Reset(false);
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
    }

    private static void VerifyNestedMetadata(Assembly assembly, object highlight)
    {
        using var brep = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
        brep.UserDictionary.Set("outer", "outer value");
        brep.Curves3D[0].UserDictionary.Set("inner", "curve value");
        var holder = new GH_Brep { Value = brep };
        Check(brep.HasUserData && brep.Curves3D[0].HasUserData, "root and nested SDK dictionary metadata exist on the input Brep");
        object result = Copy(assembly, new IGH_Goo?[] { holder });
        Check(Property<bool>(result, "Success") && Property<int>(result, "Count") == 1,
            "bounded SDK dictionaries on root and nested Brep objects are eligible for explicit copy");
        using var file = ClipboardArchive();
        var copied = ArchiveGeometry(file).OfType<Brep>().Single();
        Check(copied.UserDictionary.GetString("outer") == "outer value" &&
            copied.Curves3D[0].UserDictionary.GetString("inner") == "curve value" &&
            brep.Curves3D[0].UserDictionary.GetString("inner") == "curve value",
            "nested metadata survives clipboard serialization and the live source remains unchanged");
        Set(highlight, new IGH_Goo?[] { holder }, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 1 && Box(highlight).IsValid, "Brep with supported SDK dictionaries receives an owned preview");
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());

        using var customBrep = new Box(new BoundingBox(Point3d.Origin, new Point3d(3, 4, 5))).ToBrep();
        Check(customBrep.UserData.Add(new CopyRoundTripUserData { Value = 51 }) &&
            customBrep.Curves3D[0].UserData.Add(new CopyRoundTripUserData { Value = 52 }),
            "benign root and nested Brep custom metadata is attached");
        var customHolder = new GH_Brep { Value = customBrep };
        var customGroup = new GH_GeometryGroup(); customGroup.Objects.Add(customHolder);
        CopyRoundTripUserData.Callbacks = 0;
        Set(highlight, new IGH_Goo?[] { customHolder, customGroup }, new[] { 0, 1 }, new[] { 0 });
        Check(Count(highlight) == 2 && Box(highlight).Contains(new Point3d(3, 4, 5)) && CopyRoundTripUserData.Callbacks > 0 &&
            (customBrep.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 51 &&
            (customBrep.Curves3D[0].UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 52,
            "standalone and grouped Breps with benign custom metadata use owned compatibility previews without altering the source");
        object customCopy = Copy(assembly, new IGH_Goo?[] { customHolder, customGroup });
        Check(Property<bool>(customCopy, "Success") && Property<int>(customCopy, "Count") == 2 && Property<bool>(customCopy, "UnverifiedMetadata"),
            "standalone and grouped Breps with custom metadata copy with the incomplete-witness status");
        using (var customArchive = ClipboardArchive())
            Check(ArchiveGeometry(customArchive).OfType<Brep>().Count(value =>
                (value.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 51 &&
                (value.Curves3D[0].UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 52) == 2,
                "both clipboard Breps retain root and nested custom metadata");
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());

        using var customPoly = new PolyCurve();
        Check(customPoly.Append(new Line(Point3d.Origin, new Point3d(2, 0, 0))) &&
            customPoly.Append(new Line(new Point3d(2, 0, 0), new Point3d(3, 2, 0))) &&
            customPoly.UserData.Add(new CopyRoundTripUserData { Value = 61 }) &&
            customPoly.SegmentCurve(0).UserData.Add(new CopyRoundTripUserData { Value = 62 }),
            "benign root and nested polycurve metadata is attached");
        var customPolyHolder = new GH_Curve { Value = customPoly };
        var customPolyGroup = new GH_GeometryGroup(); customPolyGroup.Objects.Add(customPolyHolder);
        Set(highlight, new IGH_Goo?[] { customPolyHolder, customPolyGroup }, new[] { 0, 1 }, new[] { 0 });
        Check(Count(highlight) == 2 && Box(highlight).Contains(new Point3d(3, 2, 0)) &&
            (customPoly.SegmentCurve(0).UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 62,
            "standalone and grouped polycurves with benign nested metadata remain previewable and preserve the source");
        object customPolyCopy = Copy(assembly, new IGH_Goo?[] { customPolyHolder, customPolyGroup });
        Check(Property<bool>(customPolyCopy, "Success") && Property<int>(customPolyCopy, "Count") == 2 && Property<bool>(customPolyCopy, "UnverifiedMetadata"),
            "standalone and grouped polycurves with custom metadata copy with a visible incomplete-witness status");
        using (var customPolyArchive = ClipboardArchive())
            Check(ArchiveGeometry(customPolyArchive).OfType<PolyCurve>().Count(value =>
                (value.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 61 &&
                (value.SegmentCurve(0).UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 62) == 2,
                "both clipboard polycurves preserve root and nested custom metadata");
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());

        using var complexCurve = new LineCurve(Point3d.Origin, new Point3d(1, 0, 0));
        complexCurve.UserDictionary.Set("integer-array", new[] { 1, 2, 3 });
        var complex = new GH_Curve { Value = complexCurve };
        object partial = Copy(assembly, new IGH_Goo?[] { new GH_Point(Point3d.Origin), complex });
        Check(Property<bool>(partial, "Success") && Property<int>(partial, "Count") == 2 &&
            Property<int>(partial, "Skipped") == 0 && complexCurve.UserDictionary.ContainsKey("integer-array"),
            "common dictionary arrays and another selected point are copied without dropping metadata");
        using (var arrayArchive = ClipboardArchive())
        {
            var copiedCurve = ArchiveGeometry(arrayArchive).OfType<LineCurve>().Single();
            Check(copiedCurve.UserDictionary.TryGetValue("integer-array", out object values) && values is int[] integers &&
                integers.SequenceEqual(new[] { 1, 2, 3 }), "common dictionary arrays survive the clipboard archive");
        }

        using var withCache = new Box(new BoundingBox(Point3d.Origin, new Point3d(3, 3, 3))).ToBrep();
        using var cacheMesh = Mesh.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(1, 1, 1)), 1, 1, 1);
        Check(withCache.Faces[0].SetMesh(MeshType.Render, cacheMesh), "Brep face render cache fixture was installed");
        var attachedMesh = withCache.Faces[0].GetMesh(MeshType.Render);
        Check(attachedMesh != null && attachedMesh.UserData.Add(new PolylineSafetyUserData()) && attachedMesh.HasUserData,
            "hostile UserData exists on the source face render mesh");
        var cachedHolder = new GH_Brep { Value = withCache };
        PolylineSafetyUserData.Reset(true);
        Set(highlight, new IGH_Goo?[] { cachedHolder }, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && PolylineSafetyUserData.Callbacks > 0 && Status(highlight).Contains("partial") && attachedMesh!.HasUserData,
            "automatic compatibility preview visibly rejects a throwing cached-mesh callback and preserves the source");
        object cacheCopy = Copy(assembly, new IGH_Goo?[] { cachedHolder });
        Check(Count(highlight) == 0 && !Property<bool>(cacheCopy, "Success") && attachedMesh!.HasUserData &&
            PolylineSafetyUserData.Callbacks > 0, "explicit copy reports throwing cached-mesh metadata and leaves the source intact");
        PolylineSafetyUserData.Reset(false);
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
    }

    private static void VerifySizeAndEviction(Assembly assembly, object highlight)
    {
        using var tooLarge = new NurbsCurve(3, false, 2, 1025);
        for (int i = 0; i < tooLarge.Knots.Count; i++) tooLarge.Knots[i] = i;
        for (int i = 0; i < tooLarge.Points.Count; i++) tooLarge.Points.SetPoint(i, new Point3d(i, i % 2, 0));
        Check(tooLarge.IsValid, "NURBS just beyond narrow reconstruction is valid before preflight");
        var holder = new GH_Curve { Value = tooLarge };
        var largeGroup = new GH_GeometryGroup(); largeGroup.Objects.Add(holder);
        Set(highlight, new IGH_Goo?[] { holder, largeGroup }, new[] { 0, 1 }, new[] { 0 });
        Check(Count(highlight) == 2 && Box(highlight).Contains(new Point3d(1024, 0, 0)) &&
            Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
            "standalone and grouped NURBS beyond narrow reconstruction use bounded compatibility snapshots");
        object largeCopy = Copy(assembly, new IGH_Goo?[] { holder, largeGroup });
        Check(Property<bool>(largeCopy, "Success") && Property<int>(largeCopy, "Count") == 2 && Property<int>(largeCopy, "Skipped") == 0,
            "compatibility-size standalone and grouped NURBS copy without skips: " + DescribeCopy(largeCopy));
        using (var largeArchive = ClipboardArchive())
            Check(ArchiveGeometry(largeArchive).OfType<NurbsCurve>().Count(curve => curve.Points.Count == 1025) == 2,
                "both NURBS compatibility copies retain all control points in the clipboard archive");
        using var polyline = new PolylineCurve(Enumerable.Range(0, 2049).Select(i => new Point3d(i, i % 2, 0)));
        Check(polyline.SetUserString("long", "preserved"), "long polyline source metadata is attached");
        var polylineHolder = new GH_Curve { Value = polyline };
        var polylineGroup = new GH_GeometryGroup(); polylineGroup.Objects.Add(polylineHolder);
        Set(highlight, new IGH_Goo?[] { polylineHolder, polylineGroup }, new[] { 0, 1 }, new[] { 0 });
        Check(Count(highlight) == 2 && Box(highlight).Max.X == 2048 && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
            "standalone and grouped 2049-point polylines receive bounded compatibility previews");
        object polylineCopy = Copy(assembly, new IGH_Goo?[] { polylineHolder, polylineGroup });
        Check(Property<bool>(polylineCopy, "Success") && Property<int>(polylineCopy, "Count") == 2 && Property<int>(polylineCopy, "Skipped") == 0,
            "compatibility-size polylines copy without skips: " + DescribeCopy(polylineCopy));
        using (var polylineArchive = ClipboardArchive())
            Check(ArchiveGeometry(polylineArchive).OfType<PolylineCurve>().Count(value => value.PointCount == 2049 && value.GetUserString("long") == "preserved") == 2 &&
                polyline.GetUserString("long") == "preserved", "long polylines preserve point counts and user strings across clipboard serialization");
        using var brep = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 2, 2))).ToBrep();
        var group = new GH_GeometryGroup(); group.Objects.Add(new GH_Brep { Value = brep });
        var repeated = Enumerable.Repeat<IGH_Goo?>(group, 300).ToArray();
        Set(highlight, repeated, Enumerable.Range(0, repeated.Length).ToArray(), Array.Empty<int>());
        Check(Property<int>(highlight, "LastVisitedNodes") <= 20000 && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024 &&
            Property<string>(highlight, "Status").Contains("partial"), "repeated Breps obey aggregate inspection and retained-resource limits");
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
        var snapshot = new PanelSnapshot(Enumerable.Range(0, repeated.Length)
            .Select(i => new PanelRow(i, "{0}", "Group", "group", null)).ToArray(),
            new[] { new PanelBranch("{0}", repeated.Length) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Method(highlight, "SetScope").Invoke(highlight, new object[] { repeated, result, true, Array.Empty<int>() });
        for (int i = 280; i < 290; i++)
        {
            Method(highlight, "SetScope").Invoke(highlight, new object[] { repeated, result, true, new[] { i } });
            Check(Property<int>(highlight, "Count") == 1 && Property<int>(highlight, "LastVisitedRows") <= 1 &&
                Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
                "successive explicit Brep selections recover through bounded eviction without rescanning filtered rows");
        }
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
    }

    private static NurbsCurve RationalCurve()
    {
        var curve = new NurbsCurve(3, true, 3, 4);
        double[] knots = { 2, 2, 3, 5, 5 };
        for (int i = 0; i < knots.Length; i++) curve.Knots[i] = knots[i];
        Check(curve.Points.SetPoint(0, new Point4d(0, 0, 0, 1)) && curve.Points.SetPoint(1, new Point4d(4, 6, 0, 2)) &&
            curve.Points.SetPoint(2, new Point4d(10, 4, 0, 2)) && curve.Points.SetPoint(3, new Point4d(9, 0, 0, 1)),
            "rational NURBS control points were set");
        return curve;
    }

    private static async Task VerifyMetadataPaste(Assembly assembly)
    {
        var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Metadata paste fixture needs a Rhino document.");
        bool modified = doc.Modified;
        var originalIds = doc.Objects.Select(item => item.Id).ToHashSet();
        try
        {
            using var brep = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
            Check(brep.SetUserString("panel.owner", " façade 😀 ") && brep.Curves3D[0].SetUserString("edge.note", "nested value"),
                "ordinary root and nested user strings are attached before copying");
            brep.UserDictionary.Set("revision", 7);
            var group = new GH_GeometryGroup(); group.Objects.Add(new GH_Brep { Value = brep });
            byte[]? preparedDigest = null;
            int preparedLength = 0;
            uint beforeCopy = GetClipboardSequenceNumber();
            Action<byte[]> observeArchive = bytes => { preparedLength = bytes.Length; preparedDigest = SHA256.HashData(bytes); };
            object copy = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!
                .GetMethod("CopyGeometryWithArchiveObserver", Static)!.Invoke(null,
                    new object[] { new IGH_Goo?[] { group }, doc.ModelUnitSystem, doc.ModelAbsoluteTolerance, observeArchive })!;
            uint afterCopy = GetClipboardSequenceNumber();
            Check(Property<bool>(copy, "Success") && !Property<bool>(copy, "UnverifiedMetadata"),
                "ordinary root and nested user strings can be copied with the SDK dictionary");
            using (var archive = ClipboardArchive(preparedDigest, preparedLength, beforeCopy, afterCopy))
            {
                var item = ArchiveGeometry(archive).OfType<Brep>().Single();
                Check(item.GetUserString("panel.owner") == " façade 😀 " && item.Curves3D[0].GetUserString("edge.note") == "nested value" &&
                    item.UserDictionary.GetInteger("revision") == 7 && archive.Settings.ModelUnitSystem == doc.ModelUnitSystem &&
                    archive.Settings.ModelAbsoluteTolerance == doc.ModelAbsoluteTolerance,
                    "clipboard archive retains user strings, dictionary data and the source document unit context");
            }
            Check(RhinoApp.RunScript(doc.RuntimeSerialNumber, "_Paste", false), "Rhino accepts Paste for the user-string clipboard");
            await WaitForAddedObjects(doc, originalIds, 1);
            var pasted = doc.Objects.Single(item => !originalIds.Contains(item.Id)).Geometry as Brep;
            Check(pasted != null && pasted.GetUserString("panel.owner") == " façade 😀 " &&
                pasted.Curves3D[0].GetUserString("edge.note") == "nested value" &&
                pasted.GetBoundingBox(false).Equals(brep.GetBoundingBox(false)) &&
                brep.GetUserString("panel.owner") == " façade 😀 " && brep.Curves3D[0].GetUserString("edge.note") == "nested value",
                "real Rhino Paste preserves user strings without changing the live source");
            foreach (var item in doc.Objects.Where(item => !originalIds.Contains(item.Id)).ToArray()) doc.Objects.Delete(item.Id, true);

            using var largeNurbs = new NurbsCurve(3, false, 2, 1025);
            for (int i = 0; i < largeNurbs.Knots.Count; i++) largeNurbs.Knots[i] = i;
            for (int i = 0; i < largeNurbs.Points.Count; i++) largeNurbs.Points.SetPoint(i, new Point3d(i, i % 2, 0));
            using var curvedNurbs = new NurbsCurve(3, false, 3, 1025);
            for (int i = 0; i < curvedNurbs.Knots.Count; i++) curvedNurbs.Knots[i] = i;
            for (int i = 0; i < curvedNurbs.Points.Count; i++) curvedNurbs.Points.SetPoint(i, new Point3d(i, i % 2, 1));
            using var largePolyline = new PolylineCurve(Enumerable.Range(0, 2049).Select(i => new Point3d(i, i % 2, 0)));
            Check(largeNurbs.SetUserString("source", "large nurbs") && curvedNurbs.SetUserString("source", "curved large nurbs") &&
                largePolyline.SetUserString("source", "large polyline") && curvedNurbs.IsValid,
                "linear and curved compatibility-size sources have user strings before frame and copy");
            var nurbsHolder = new GH_Curve { Value = largeNurbs };
            var curvedHolder = new GH_Curve { Value = curvedNurbs };
            var polyHolder = new GH_Curve { Value = largePolyline };
            var nurbsGroup = new GH_GeometryGroup(); nurbsGroup.Objects.Add(nurbsHolder);
            var curvedGroup = new GH_GeometryGroup(); curvedGroup.Objects.Add(curvedHolder);
            var polyGroup = new GH_GeometryGroup(); polyGroup.Objects.Add(polyHolder);
            var largeSources = new IGH_Goo?[] { nurbsHolder, nurbsGroup, curvedHolder, curvedGroup, polyHolder, polyGroup };
            var actionType = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!;
            object frame = actionType.GetMethod("Frame", Static)!.Invoke(null, new object[] { largeSources })!;
            Check(Property<bool>(frame, "Success") && Property<int>(frame, "Count") == 6 && Property<int>(frame, "Skipped") == 0,
                "standalone and grouped compatibility-size curves frame without skips");
            copy = Copy(assembly, largeSources);
            Check(Property<bool>(copy, "Success") && Property<int>(copy, "Count") == 6 && Property<int>(copy, "Skipped") == 0,
                "standalone and grouped compatibility-size curves copy without skips: " + DescribeCopy(copy));
            using (var archive = ClipboardArchive())
            {
                var geometries = ArchiveGeometry(archive);
                Check(geometries.OfType<NurbsCurve>().Count(value => SameNurbs(value, largeNurbs) && value.GetUserString("source") == "large nurbs") == 2 &&
                    geometries.OfType<NurbsCurve>().Count(value => SameNurbs(value, curvedNurbs) && value.GetUserString("source") == "curved large nurbs") == 2 &&
                    geometries.OfType<PolylineCurve>().Count(value => SamePolyline(value, largePolyline) && value.GetUserString("source") == "large polyline") == 2,
                    "the native clipboard retains both NURBS types, polyline points, knots and user strings");
            }
            Check(RhinoApp.RunScript(doc.RuntimeSerialNumber, "_Paste", false), "Rhino accepts the compatibility-size curve clipboard");
            await WaitForAddedObjects(doc, originalIds, 6);
            var largePasted = doc.Objects.Where(item => !originalIds.Contains(item.Id)).Select(item => item.Geometry).ToArray();
            Check(largePasted.Count(value => value.GetUserString("source") == "large nurbs" && SameLinearNurbsGeometry(value, largeNurbs)) == 2 &&
                largePasted.OfType<NurbsCurve>().Count(value => value.GetUserString("source") == "curved large nurbs" && SameNurbs(value, curvedNurbs)) == 2 &&
                largePasted.OfType<PolylineCurve>().Count(value => value.GetUserString("source") == "large polyline" && SamePolyline(value, largePolyline)) == 2 &&
                largeNurbs.GetUserString("source") == "large nurbs" && curvedNurbs.GetUserString("source") == "curved large nurbs" &&
                largePolyline.GetUserString("source") == "large polyline" &&
                Enumerable.Range(0, 1025).All(i => largeNurbs.Points[i].Location == new Point3d(i, i % 2, 0) &&
                    curvedNurbs.Points[i].Location == new Point3d(i, i % 2, 1)) &&
                Enumerable.Range(0, 2049).All(i => largePolyline.Point(i) == new Point3d(i, i % 2, 0)),
                "real Paste preserves curve coordinates and metadata, including a higher-degree NURBS, while leaving all sources unchanged: " +
                string.Join("; ", largePasted.Select(value => value.GetType().Name + ", source=" + value.GetUserString("source") +
                    ", points=" + (value is NurbsCurve n ? n.Points.Count : value is PolylineCurve p ? p.PointCount : -1))));
            foreach (var item in doc.Objects.Where(item => !originalIds.Contains(item.Id)).ToArray()) doc.Objects.Delete(item.Id, true);

            using var curve = RationalCurve();
            Check(curve.SetUserString("label", "custom source") && curve.UserData.Add(new CopyRoundTripUserData { Value = 73 }),
                "serializable managed metadata and user strings are attached to the source");
            CopyRoundTripUserData.Callbacks = 0;
            copy = Copy(assembly, new IGH_Goo?[] { new GH_Curve { Value = curve } });
            Check(Property<bool>(copy, "Success") && Property<bool>(copy, "UnverifiedMetadata") && CopyRoundTripUserData.Callbacks > 0,
                "explicit copy invokes normal managed callbacks and visibly reports the incomplete custom-data witness");
            using (var archive = ClipboardArchive())
            {
                var item = ArchiveGeometry(archive).OfType<NurbsCurve>().Single();
                Check(item.GetUserString("label") == "custom source" &&
                    (item.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 73,
                    "the clipboard round trip preserves the instrumented custom data payload");
            }
            Check(RhinoApp.RunScript(doc.RuntimeSerialNumber, "_Paste", false), "Rhino accepts Paste for custom-data geometry");
            await WaitForAddedObjects(doc, originalIds, 1);
            var pastedCurve = doc.Objects.Single(item => !originalIds.Contains(item.Id)).Geometry;
            Check(pastedCurve.GetUserString("label") == "custom source" &&
                (pastedCurve.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 73 &&
                curve.GetUserString("label") == "custom source" &&
                (curve.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 73,
                "real Paste retains the custom payload and does not alter the source");

            var point = new GH_Point(Point3d.Origin);
            var groupWithPoint = new GH_GeometryGroup();
            groupWithPoint.Objects.Add(new GH_Curve { Value = curve }); groupWithPoint.Objects.Add(point);
            Forms.Clipboard.SetText("keep callback clipboard");
            CopyRoundTripUserData.DuplicateCalls = 0; CopyRoundTripUserData.DuplicateAt = 1; CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => point.Value = new Point3d(99, 0, 0);
            copy = Copy(assembly, new IGH_Goo?[] { groupWithPoint });
            Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "keep callback clipboard" &&
                point.Value.X == 99 && (curve.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 73,
                "an actual duplicate callback changing a grouped point cancels clipboard publication and preserves source metadata");
            point.Value = Point3d.Origin;
            CopyRoundTripUserData.DuplicateCalls = 0; CopyRoundTripUserData.DuplicateAt = 2; CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => point.Value = new Point3d(101, 0, 0);
            copy = Copy(assembly, new IGH_Goo?[] { groupWithPoint });
            Check(CopyRoundTripUserData.DuplicateCalls >= 2 && CopyRoundTripUserData.TriggeredGeneration == 2 && !Property<bool>(copy, "Success") &&
                Forms.Clipboard.GetText() == "keep callback clipboard" && point.Value.X == 101,
                "a File3dm Add duplication callback changing the group is rejected before clipboard publication");
            CopyRoundTripUserData.DuplicateAt = 1;

            var originalUnits = doc.ModelUnitSystem;
            double originalTolerance = doc.ModelAbsoluteTolerance;
            try
            {
                CopyRoundTripUserData.OnDuplicateAction = () => doc.ModelUnitSystem = originalUnits == UnitSystem.Millimeters ? UnitSystem.Inches : UnitSystem.Millimeters;
                copy = Copy(assembly, new IGH_Goo?[] { new GH_Curve { Value = curve } });
                Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "keep callback clipboard",
                    "changing model units in a duplicate callback cancels before replacing the clipboard");
                doc.ModelUnitSystem = originalUnits;
                CopyRoundTripUserData.OnDuplicateAction = () => doc.ModelAbsoluteTolerance = originalTolerance * 2;
                copy = Copy(assembly, new IGH_Goo?[] { new GH_Curve { Value = curve } });
                Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "keep callback clipboard",
                    "changing model tolerance in a duplicate callback cancels before replacing the clipboard");
                doc.ModelAbsoluteTolerance = originalTolerance;
                using var alternateDoc = RhinoDoc.CreateHeadless(null);
                RhinoDoc selectedDoc = doc;
                CopyRoundTripUserData.OnDuplicateAction = () => selectedDoc = alternateDoc;
                copy = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!.GetMethod("CopyGeometryWithContext", Static)!
                    .Invoke(null, new object[] { new IGH_Goo?[] { new GH_Curve { Value = curve } }, originalUnits, originalTolerance,
                        (Func<bool>)(() => true), (Func<RhinoDoc?>)(() => selectedDoc) })!;
                Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "keep callback clipboard" &&
                    alternateDoc.RuntimeSerialNumber != doc.RuntimeSerialNumber,
                    "switching the action context to another actual Rhino document cancels clipboard publication");
            }
            finally { doc.ModelUnitSystem = originalUnits; doc.ModelAbsoluteTolerance = originalTolerance; }
        }
        finally
        {
            CopyRoundTripUserData.OnDuplicateAction = null;
            foreach (var item in doc.Objects.Where(item => !originalIds.Contains(item.Id)).ToArray()) doc.Objects.Delete(item.Id, true);
            doc.Modified = modified;
        }
    }

    private static bool SameNurbs(NurbsCurve actual, NurbsCurve expected)
    {
        if (actual.Order != expected.Order || actual.Dimension != expected.Dimension || actual.IsRational != expected.IsRational ||
            actual.Points.Count != expected.Points.Count || actual.Knots.Count != expected.Knots.Count ||
            Math.Abs(actual.Domain.T0 - expected.Domain.T0) > 1e-9 || Math.Abs(actual.Domain.T1 - expected.Domain.T1) > 1e-9) return false;
        for (int i = 0; i < actual.Knots.Count; i++) if (Math.Abs(actual.Knots[i] - expected.Knots[i]) > 1e-9) return false;
        for (int i = 0; i < actual.Points.Count; i++)
            if (!actual.Points[i].Location.EpsilonEquals(expected.Points[i].Location, 1e-9) ||
                Math.Abs(actual.Points[i].Weight - expected.Points[i].Weight) > 1e-9) return false;
        return true;
    }

    private static bool SamePolyline(PolylineCurve actual, PolylineCurve expected)
    {
        if (actual.PointCount != expected.PointCount) return false;
        for (int i = 0; i < actual.PointCount; i++)
            if (!actual.Point(i).EpsilonEquals(expected.Point(i), 1e-9)) return false;
        return true;
    }

    private static bool SameLinearNurbsGeometry(GeometryBase actual, NurbsCurve expected)
    {
        if (actual is NurbsCurve nurbs) return SameNurbs(nurbs, expected);
        if (actual is not PolylineCurve polyline || expected.Order != 2 || expected.IsRational ||
            polyline.PointCount != expected.Points.Count) return false;
        for (int i = 0; i < polyline.PointCount; i++)
            if (!polyline.Point(i).EpsilonEquals(expected.Points[i].Location, 1e-9)) return false;
        return true;
    }

    private static async Task WaitForAddedObjects(RhinoDoc doc, System.Collections.Generic.HashSet<Guid> original, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (doc.Objects.Count(item => !original.Contains(item.Id)) != expected)
        { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Rhino Paste did not add the expected geometry."); await Task.Delay(25); }
    }

    private static void VerifyLoadedReference(Assembly assembly, object highlight)
    {
        var rhino = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Reference fixture needs an active Rhino document.");
        bool modified = rhino.Modified;
        var view = rhino.Views.ActiveView;
        bool createdView = view == null;
        if (view == null)
        {
            view = rhino.Views.Add("Referenced geometry fixture", Rhino.Display.DefinedViewportProjection.Top,
                new System.Drawing.Rectangle(0, 0, 640, 480), false);
            rhino.Views.ActiveView = view;
        }
        using var geometry = new LineCurve(new Point3d(100, 0, 0), new Point3d(105, 2, 0));
        Guid id = rhino.Objects.AddCurve(geometry);
        if (id == Guid.Empty) throw new InvalidOperationException("Could not add referenced curve fixture.");
        Guid alternate = rhino.Objects.AddCurve(new LineCurve(new Point3d(200, 0, 0), new Point3d(205, 2, 0)));
        if (alternate == Guid.Empty) throw new InvalidOperationException("Could not add alternate reference fixture.");
        try
        {
            var referenced = new GH_Curve(id);
            Check(referenced.IsReferencedGeometry && referenced.LoadGeometry(rhino) && referenced.IsGeometryLoaded,
                "a referenced Rhino curve is loaded before delivery to the plugin");
            var group = new GH_GeometryGroup(); group.Objects.Add(referenced);
            var wrapped = new GH_ObjectWrapper(group);
            Set(highlight, new IGH_Goo?[] { referenced, group, wrapped }, new[] { 0, 1, 2 }, new[] { 0, 1 });
            Check(Count(highlight) == 3 && Box(highlight).Contains(new Point3d(105, 2, 0)),
                "loaded referenced geometry previews as a standalone, group member and wrapped group");
            object actions = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!;
            var bounds = (BoundingBox)((Type)actions).GetMethod("GetBounds", Static)!.Invoke(null, new object[] { wrapped })!;
            object frame = ((Type)actions).GetMethod("Frame", Static)!.Invoke(null, new object[] { new IGH_Goo?[] { wrapped } })!;
            object copy = Copy(assembly, new IGH_Goo?[] { wrapped });
            Check(bounds.IsValid && bounds.Contains(new Point3d(105, 2, 0)) && Property<bool>(frame, "Success") &&
                Property<bool>(copy, "Success") && Property<int>(copy, "Count") == 1,
                "loaded referenced geometry inside a wrapped group remains frameable and copyable");
            using var archive = ClipboardArchive();
            Check(archive.Objects.Count == 1 && ArchiveGeometry(archive).Single().GetBoundingBox(false).Contains(new Point3d(105, 2, 0)) &&
                referenced.IsReferencedGeometry && referenced.ReferenceID == id,
                "referenced source stays linked while the clipboard receives its geometry");
            Curve captured = referenced.Value;
            var target = view.ActiveViewport.CameraTarget;
            frame = ((Type)actions).GetMethod("Frame", Static)!.Invoke(null,
                new object[] { ChangeReferenceAfterPreparation(referenced, alternate, captured) })!;
            Check(!Property<bool>(frame, "Success") && view.ActiveViewport.CameraTarget == target,
                "retargeting a loaded reference with the same inner curve cancels frame without camera motion");
            referenced.Value = captured; referenced.ReferenceID = id;
            Forms.Clipboard.SetText("keep reference clipboard");
            copy = Copy(assembly, ChangeReferenceAfterPreparation(referenced, alternate, captured));
            Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "keep reference clipboard",
                "retargeting a loaded reference with the same inner curve cancels copy without changing the clipboard");
            referenced.Value = captured; referenced.ReferenceID = id;
            referenced.ReferenceEdge = -1;
            copy = Copy(assembly, ChangeReferenceEdgeAfterPreparation(referenced, captured));
            Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "keep reference clipboard",
                "changing the referenced component on the same loaded holder cancels copy");
            referenced.ReferenceEdge = -1;
            Check(captured.UserData.Add(new CopyRoundTripUserData { Value = 41 }),
                "loaded reference fixture has serializable callback metadata");
            Check(referenced.IsReferencedGeometry && referenced.ReferenceID == id && referenced.IsGeometryLoaded &&
                ReferenceEquals(referenced.Value, captured), "the reference callback starts with the original ID and loaded curve");
            CopyRoundTripUserData.DuplicateCalls = 0; CopyRoundTripUserData.TriggeredGeneration = 0; CopyRoundTripUserData.DuplicateAt = 1;
            CopyRoundTripUserData.OnDuplicateAction = () => referenced.ReferenceID = alternate;
            copy = Copy(assembly, new IGH_Goo?[] { referenced });
            Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "keep reference clipboard" &&
                referenced.ReferenceID == alternate && referenced.IsGeometryLoaded && ReferenceEquals(referenced.Value, captured) &&
                CopyRoundTripUserData.DuplicateCalls > 0 && CopyRoundTripUserData.TriggeredGeneration > 0,
                $"a duplicate callback retargeting the same loaded holder and inner curve cancels clipboard publication " +
                $"({DescribeCopy(copy)}, callbacks={CopyRoundTripUserData.Callbacks}, id={referenced.ReferenceID}, " +
                $"alternate={alternate}, loaded={referenced.IsGeometryLoaded}, same={ReferenceEquals(referenced.Value, captured)}, " +
                $"clipboard={Forms.Clipboard.GetText()})");
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
        }
        finally
        {
            CopyRoundTripUserData.OnDuplicateAction = null;
            rhino.Objects.Delete(id, true);
            rhino.Objects.Delete(alternate, true);
            if (createdView) view.Close();
            rhino.Modified = modified;
        }
    }

    private static IEnumerable<IGH_Goo?> ChangeReferenceAfterPreparation(GH_Curve holder, Guid target, Curve captured)
    {
        yield return holder;
        holder.ReferenceID = target;
        Check(holder.IsReferencedGeometry && holder.IsGeometryLoaded && holder.ReferenceID == target && ReferenceEquals(holder.Value, captured),
            "reference retargeting keeps the same holder and loaded curve for the action-witness fixture");
    }

    private static IEnumerable<IGH_Goo?> ChangeReferenceEdgeAfterPreparation(GH_Curve holder, Curve captured)
    {
        yield return holder;
        holder.ReferenceEdge = 1;
        Check(holder.IsGeometryLoaded && holder.ReferenceEdge == 1 && ReferenceEquals(holder.Value, captured),
            "reference component changes while the same loaded curve remains attached");
    }

    private static File3dm ClipboardArchive(byte[]? expectedDigest = null, int expectedLength = 0,
        uint beforeCopy = 0, uint afterCopy = 0)
    {
        uint beforeRead = GetClipboardSequenceNumber();
        object data = Forms.Clipboard.GetData("Rhino 8 3DM Geometry")!;
        byte[] bytes;
        if (data is byte[] array) bytes = array;
        else if (data is Stream stream)
        {
            using var output = new MemoryStream(); stream.Position = 0; stream.CopyTo(output); bytes = output.ToArray();
        }
        else throw new InvalidOperationException("Native geometry clipboard has no bytes (type=" + (data?.GetType().FullName ?? "null") + ").");
        uint afterRead = GetClipboardSequenceNumber();
        byte[] digest = SHA256.HashData(bytes);
        bool matches = expectedDigest != null && expectedLength == bytes.Length && expectedDigest.SequenceEqual(digest);
        if (expectedDigest != null)
            Console.WriteLine($"Clipboard archive diagnostic: preparedLength={expectedLength}, clipboardLength={bytes.Length}, matches={matches}, " +
                $"preparedSha256={Convert.ToHexString(expectedDigest)}, clipboardSha256={Convert.ToHexString(digest)}, " +
                $"sequence={beforeCopy}/{afterCopy}/{beforeRead}/{afterRead}, valueType={data.GetType().FullName}");
        var file = File3dm.FromByteArray(bytes);
        if (file == null) throw new InvalidOperationException("Native geometry clipboard archive is invalid; see clipboard archive diagnostic when available.");
        if (expectedDigest != null) Check(matches, "the actual clipboard bytes match the verified production archive");
        return file;
    }
    private static GeometryBase[] ArchiveGeometry(File3dm file)
    {
        // Rhino's object table implements IList but has no component indices;
        // LINQ ToArray/First can take its invalid indexing fast path.
        var geometry = new List<GeometryBase>();
        foreach (var item in file.Objects)
            geometry.Add(item?.Geometry ?? throw new InvalidOperationException("Clipboard archive contains an inaccessible object."));
        return geometry.ToArray();
    }
    private static object Copy(Assembly assembly, IEnumerable<IGH_Goo?> values) => assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!
        .GetMethod("CopyGeometry", Static)!.Invoke(null, new object[] { values, RhinoDoc.ActiveDoc.ModelUnitSystem, RhinoDoc.ActiveDoc.ModelAbsoluteTolerance })!;
    private static string DescribeCopy(object result) =>
        $"success={Property<bool>(result, "Success")}, count={Property<int>(result, "Count")}, " +
        $"skipped={Property<int>(result, "Skipped")}, exhausted={Property<bool>(result, "Exhausted")}, " +
        $"detail={Property<string?>(result, "Detail") ?? "<none>"}, error={Property<string?>(result, "Error") ?? "<none>"}";
    private static void DiagnoseDictionaryArchive(Assembly assembly, GeometryBase source)
    {
        try
        {
            using var file = new File3dm();
            Guid sourceId = file.Objects.Add(source, null);
            string Dictionary(GeometryBase value)
            {
                bool hadUserData = value.HasUserData;
                int dataCount = value.UserData.Count;
                string types = string.Join(",", Enumerable.Range(0, dataCount).Select(i => value.UserData[i]?.GetType().FullName ?? "<native>"));
                var dictionary = value.UserDictionary;
                return $"hadData={hadUserData}, userdata={dataCount}, types=[{types}], name={dictionary.Name}, version={dictionary.Version}, " +
                    $"keys=[{string.Join(",", dictionary.Keys)}], " +
                    $"source={(dictionary.TryGetValue("source", out object src) ? src : "<missing>")}, " +
                    $"revision={(dictionary.TryGetValue("revision", out object rev) ? rev : "<missing>")}, strings={value.UserStringCount}";
            }
            using var duplicate = source.Duplicate();
            Console.WriteLine("Source dictionary: " + Dictionary(source));
            Console.WriteLine("Duplicate dictionary: " + Dictionary(duplicate));
            Console.WriteLine("File object before writing: " + Dictionary(file.Objects.FindId(sourceId).Geometry));
            byte[] data = file.ToByteArray(new File3dmWriteOptions { Version = 8, SaveUserData = true });
            if (data == null) throw new InvalidOperationException("Rhino returned a null archive");
            Console.WriteLine($"Archive diagnostic: id={sourceId}, source count={file.Objects.Count}, bytes={data.Length}, header=" +
                System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(32, data.Length)));
            using var fromBytes = File3dm.FromByteArray(data);
            Console.WriteLine("Archive diagnostic: FromByteArray=" + (fromBytes == null ? "null" : "ok"));
            string path = Path.Combine(Path.GetTempPath(), "panel-dictionary-" + Guid.NewGuid() + ".3dm");
            try
            {
                File.WriteAllBytes(path, data);
                using var fromFile = File3dm.Read(path);
                Console.WriteLine("Archive diagnostic: Read same bytes=" + (fromFile == null ? "null" : "ok"));
                bool wrote = file.Write(path, new File3dmWriteOptions { Version = 8, SaveUserData = true });
                using var nativeWritten = wrote ? File3dm.Read(path) : null;
                var diskObject = nativeWritten?.Objects.FindId(sourceId);
                Console.WriteLine("Native file writer: wrote=" + wrote + ", object=" + (diskObject == null ? "null" : Dictionary(diskObject.Geometry)));
            }
            finally { File.Delete(path); }
            using var roundTrip = fromBytes ?? throw new InvalidOperationException("Rhino could not reopen its byte archive");
            Console.WriteLine("Archive diagnostic: object count=" + roundTrip.Objects.Count);
            var first = roundTrip.Objects.FindId(sourceId);
            Console.WriteLine("Archive diagnostic: first object is " + (first == null ? "null" : "present"));
            var result = first?.Geometry ?? throw new InvalidOperationException("Archive object/geometry is null");
            Console.WriteLine("Archived dictionary: " + Dictionary(result));
            var scanType = assembly.GetType("BetterGhPanel.Runtime.PanelNativeGeometry", true)!;
            var budgetType = assembly.GetType("BetterGhPanel.Runtime.PanelGeometrySources+Budget", true)!;
            byte[] Scan(GeometryBase value)
            {
                object budget = Activator.CreateInstance(budgetType, true)!;
                object?[] args = { value, budget, 4096, (Func<bool>)(() => true), null };
                bool ok = (bool)scanType.GetMethod("TryScanForCopy", Static)!.Invoke(null, args)!;
                if (!ok || args[4] == null) throw new InvalidOperationException("Metadata scan failed");
                return Property<byte[]>(args[4]!, "Metadata");
            }
            var a = Scan(source); var b = Scan(result);
            int difference = 0; while (difference < Math.Min(a.Length, b.Length) && a[difference] == b[difference]) difference++;
            Console.WriteLine($"Metadata witness source/archive: lengths={a.Length}/{b.Length}, first difference={difference}, " +
                $"bytes={(difference < a.Length ? a[difference].ToString() : "end")}/{(difference < b.Length ? b[difference].ToString() : "end")}");
        }
        catch (Exception ex) { Console.WriteLine("Dictionary archive diagnostic failed: " + ex); }
    }
    private static void Set(object highlight, IGH_Goo?[] values, int[] positions, int[] selected) =>
        Method(highlight, "Set").Invoke(highlight, new object[] { values, positions, selected });
    private static int Count(object value) => Property<int>(value, "PreviewCount");
    private static string Status(object value) => Property<string>(value, "Status");
    private static BoundingBox Box(object value) => Property<BoundingBox>(value, "ClippingBox");
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name, Instance)!.GetValue(value)!;
    private static MethodInfo Method(object value, string name) => value.GetType().GetMethod(name, Instance)!;
    private static void Check(bool value, string message) => SmokeAssertions.Check(value, message);
}

[Guid("7f647aaa-e4d3-4b9f-ad13-82a5c6d7af9c")]
public sealed class CopyRoundTripUserData : UserData
{
    internal static int Callbacks;
    internal static Action? OnDuplicateAction;
    internal static int DuplicateCalls, DuplicateAt = 1, TriggeredGeneration;
    private int DuplicateGeneration { get; set; }
    public int Value { get; set; }
    public override string Description => "Explicit copy metadata fixture";
    public override bool ShouldWrite { get { Callbacks++; return true; } }
    protected override void OnDuplicate(UserData source)
    {
        Callbacks++; DuplicateCalls++;
        var original = (CopyRoundTripUserData)source;
        Value = original.Value; DuplicateGeneration = original.DuplicateGeneration + 1;
        if (OnDuplicateAction != null && DuplicateGeneration >= DuplicateAt)
        { TriggeredGeneration = DuplicateGeneration; var action = OnDuplicateAction; OnDuplicateAction = null; action(); }
    }
    protected override bool Write(BinaryArchiveWriter archive) { Callbacks++; archive.WriteInt(Value); return true; }
    protected override bool Read(BinaryArchiveReader archive) { Callbacks++; Value = archive.ReadInt(); return true; }
}
