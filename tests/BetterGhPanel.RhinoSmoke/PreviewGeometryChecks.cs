using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
        Check(Property<bool>(copied, "Success") && Property<int>(copied, "Count") == 5 && Property<int>(copied, "Skipped") == 0,
            "explicit geometry copy retains all five ordinary native geometry rows");
        using var file = ClipboardArchive();
        var geometries = file.Objects.Select(item => item.Geometry).ToArray();
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
        var copied = file.Objects.Select(item => item.Geometry).OfType<Brep>().Single();
        Check(copied.UserDictionary.GetString("outer") == "outer value" &&
            copied.Curves3D[0].UserDictionary.GetString("inner") == "curve value" &&
            brep.Curves3D[0].UserDictionary.GetString("inner") == "curve value",
            "nested metadata survives clipboard serialization and the live source remains unchanged");
        Set(highlight, new IGH_Goo?[] { holder }, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 1 && Box(highlight).IsValid, "Brep with supported SDK dictionaries receives an owned preview");
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
            var copiedCurve = arrayArchive.Objects.Select(item => item.Geometry).OfType<LineCurve>().Single();
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
        Check(Count(highlight) == 0 && PolylineSafetyUserData.Callbacks == 0,
            "automatic preview does not call unknown cached-mesh metadata");
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
        Check(tooLarge.IsValid, "oversized NURBS fixture is valid before preflight");
        var holder = new GH_Curve { Value = tooLarge };
        Set(highlight, new IGH_Goo?[] { holder }, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && Property<long>(highlight, "RetainedBytes") == 0,
            "oversized NURBS does not create a preview snapshot");
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
            object copy = Copy(assembly, new IGH_Goo?[] { group });
            Check(Property<bool>(copy, "Success") && !Property<bool>(copy, "UnverifiedMetadata"),
                "ordinary root and nested user strings can be copied with the SDK dictionary");
            using (var archive = ClipboardArchive())
            {
                var item = archive.Objects.Select(value => value.Geometry).OfType<Brep>().Single();
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

            using var curve = RationalCurve();
            Check(curve.SetUserString("label", "custom source") && curve.UserData.Add(new CopyRoundTripUserData { Value = 73 }),
                "serializable managed metadata and user strings are attached to the source");
            CopyRoundTripUserData.Callbacks = 0;
            copy = Copy(assembly, new IGH_Goo?[] { new GH_Curve { Value = curve } });
            Check(Property<bool>(copy, "Success") && Property<bool>(copy, "UnverifiedMetadata") && CopyRoundTripUserData.Callbacks > 0,
                "explicit copy invokes normal managed callbacks and visibly reports the incomplete custom-data witness");
            using (var archive = ClipboardArchive())
            {
                var item = archive.Objects.Select(value => value.Geometry).OfType<NurbsCurve>().Single();
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
            Check(archive.Objects.Count == 1 && archive.Objects.First().Geometry.GetBoundingBox(false).Contains(new Point3d(105, 2, 0)) &&
                referenced.IsReferencedGeometry && referenced.ReferenceID == id,
                "referenced source stays linked while the clipboard receives its geometry");
            Curve captured = referenced.Value;
            var target = view.ActiveViewport.CameraTarget;
            frame = ((Type)actions).GetMethod("Frame", Static)!.Invoke(null,
                new object[] { ChangeReferenceAfterPreparation(referenced, alternate, captured) })!;
            Check(!Property<bool>(frame, "Success") && view.ActiveViewport.CameraTarget == target,
                "retargeting a loaded reference with the same inner curve cancels frame without camera motion");
            referenced.ReferenceID = id; referenced.Value = captured;
            Forms.Clipboard.SetText("keep reference clipboard");
            copy = Copy(assembly, ChangeReferenceAfterPreparation(referenced, alternate, captured));
            Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "keep reference clipboard",
                "retargeting a loaded reference with the same inner curve cancels copy without changing the clipboard");
            referenced.ReferenceID = id; referenced.Value = captured;
            referenced.ReferenceEdge = -1;
            copy = Copy(assembly, ChangeReferenceEdgeAfterPreparation(referenced, captured));
            Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "keep reference clipboard",
                "changing the referenced component on the same loaded holder cancels copy");
            referenced.ReferenceEdge = -1;
            Check(captured.UserData.Add(new CopyRoundTripUserData { Value = 41 }),
                "loaded reference fixture has serializable callback metadata");
            CopyRoundTripUserData.OnDuplicateAction = () => { referenced.ReferenceID = alternate; referenced.Value = captured; };
            copy = Copy(assembly, new IGH_Goo?[] { referenced });
            Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "keep reference clipboard" &&
                referenced.ReferenceID == alternate && referenced.IsGeometryLoaded && ReferenceEquals(referenced.Value, captured),
                "a duplicate callback retargeting the same loaded holder and inner curve cancels clipboard publication");
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
        holder.Value = captured;
        Check(holder.IsReferencedGeometry && holder.IsGeometryLoaded && holder.ReferenceID == target && ReferenceEquals(holder.Value, captured),
            "reference retargeting keeps the same holder and loaded curve for the action-witness fixture");
    }

    private static IEnumerable<IGH_Goo?> ChangeReferenceEdgeAfterPreparation(GH_Curve holder, Curve captured)
    {
        yield return holder;
        holder.ReferenceEdge = 1;
        holder.Value = captured;
        Check(holder.IsGeometryLoaded && holder.ReferenceEdge == 1 && ReferenceEquals(holder.Value, captured),
            "reference component changes while the same loaded curve remains attached");
    }

    private static File3dm ClipboardArchive()
    {
        object data = Forms.Clipboard.GetData("Rhino 8 3DM Geometry")!;
        byte[] bytes;
        if (data is byte[] array) bytes = array;
        else if (data is Stream stream)
        {
            using var output = new MemoryStream(); stream.Position = 0; stream.CopyTo(output); bytes = output.ToArray();
        }
        else throw new InvalidOperationException("Native geometry clipboard has no bytes.");
        return File3dm.FromByteArray(bytes) ?? throw new InvalidOperationException("Native geometry clipboard archive is invalid.");
    }
    private static object Copy(Assembly assembly, IEnumerable<IGH_Goo?> values) => assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!
        .GetMethod("CopyGeometry", Static)!.Invoke(null, new object[] { values, RhinoDoc.ActiveDoc.ModelUnitSystem, RhinoDoc.ActiveDoc.ModelAbsoluteTolerance })!;
    private static void Set(object highlight, IGH_Goo?[] values, int[] positions, int[] selected) =>
        Method(highlight, "Set").Invoke(highlight, new object[] { values, positions, selected });
    private static int Count(object value) => Property<int>(value, "PreviewCount");
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
