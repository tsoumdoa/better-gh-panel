using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;
using Forms = System.Windows.Forms;

// Every assertion in this fixture is plugin-controlled except the separately
// identified real-solve/document-cache probe at the end.
internal static class PreviewSafetyChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal static async Task Run(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool previousModified = rhino.Modified;
        var view = rhino.Views.ActiveView ?? rhino.Views.Add("Preview safety", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        rhino.Views.ActiveView = view;
        try
        {
            object highlight = Activator.CreateInstance(assembly.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!, true)!;
            VerifyMutation(assembly, highlight);
            VerifyBudgets(highlight);
            VerifyActions(assembly);
            VerifyResources(highlight);
            await VerifyDocumentCacheAndDrawing(assembly, rhino);
        }
        finally { rhino.Modified = previousModified; }
    }

    internal static async Task RunMetadata(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        if (rhino.Views.ActiveView == null)
            rhino.Views.ActiveView = rhino.Views.Add("Preview metadata", Rhino.Display.DefinedViewportProjection.Top,
                new Rectangle(0, 0, 640, 480), false);
        object highlight = Activator.CreateInstance(assembly.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!, true)!;
        VerifyMetadataAndActions(assembly, highlight);
        VerifyReentrancy(assembly, highlight);
        VerifyDenseSelectionGestures(assembly, highlight);
        await VerifyCrossComponentActions(assembly, rhino);
    }

    private static async Task VerifyCrossComponentActions(Assembly assembly, RhinoDoc rhino)
    {
        var prior = Forms.Clipboard.GetDataObject();
        using var document = new GH_Document { Enabled = true };
        try
        {
            document.Properties.KeepOpen = true;
            GH_Document.EnableSolutions = true;
            var componentType = assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!;
            var first = (GH_Component)Activator.CreateInstance(componentType)!;
            var second = (GH_Component)Activator.CreateInstance(componentType)!;
            first.CreateAttributes(); second.CreateAttributes();
            var source = new Param_GenericObject { Hidden = true }; source.CreateAttributes();
            source.PersistentData.Append(new GH_Point(Point3d.Origin), new GH_Path(0));
            source.PersistentData.Append(new GH_Point(new Point3d(70, 0, 0)), new GH_Path(0));
            document.AddObject(source, false); document.AddObject(first, false); document.AddObject(second, false);
            first.Params.Input[0].AddSource(source); second.Params.Input[0].AddSource(source);
            document.NewSolution(false, GH_SolutionMode.Silent);
            var firstSession = PanelTestAdapter.Session(first); var secondSession = PanelTestAdapter.Session(second);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!(bool)firstSession.GetType().GetProperty("HasCurrentResult")!.GetValue(firstSession)! ||
                   !(bool)secondSession.GetType().GetProperty("HasCurrentResult")!.GetValue(secondSession)!)
            { if (DateTime.UtcNow > deadline) throw new TimeoutException("Cross-component action queries did not complete"); await Task.Delay(25); }
            var firstSnapshot = (PanelSnapshot)firstSession.GetType().GetProperty("Snapshot")!.GetValue(firstSession)!;
            var secondSnapshot = (PanelSnapshot)secondSession.GetType().GetProperty("Snapshot")!.GetValue(secondSession)!;
            var firstSource = Field<IGH_Goo?[]>(first, "_sourceItems");
            var custom = new StandalonePointPreview(); firstSource[0] = custom;
            string? newerFrame = null;
            custom.OnBounds = () => newerFrame = (string?)Method(second, "FrameRows").Invoke(second, new object[] { secondSnapshot, new[] { 1 } });
            string? olderFrame = (string?)Method(first, "FrameRows").Invoke(first, new object[] { firstSnapshot, new[] { 0 } });
            Check(olderFrame != null && olderFrame.Contains("cancelled") && newerFrame == null &&
                rhino.Views.ActiveView.ActiveViewport.CameraTarget.DistanceTo(new Point3d(70, 0, 0)) < 1e-8,
                "a newer frame from another component supersedes the reentrant older frame and retains its camera target");

            using var curve = new LineCurve(Point3d.Origin, new Point3d(2, 0, 0));
            Check(curve.UserData.Add(new CopyRoundTripUserData { Value = 19 }), "cross-component copy callback metadata is attached");
            firstSource[0] = new GH_Curve { Value = curve };
            Forms.Clipboard.SetText("old cross-component clipboard");
            bool newerCopied = false;
            CopyRoundTripUserData.OnDuplicateAction = () =>
            {
                object?[] nested = { secondSnapshot, new[] { 1 }, null };
                newerCopied = (bool)Method(second, "TryCopyGeometry").Invoke(second, nested)!;
            };
            object?[] outer = { firstSnapshot, new[] { 0 }, null };
            bool olderCopied = (bool)Method(first, "TryCopyGeometry").Invoke(first, outer)!;
            using var archive = ClipboardArchive();
            var copiedPoint = archive.Objects.Select(item => item.Geometry).OfType<Rhino.Geometry.Point>().Single();
            Check(newerCopied && !olderCopied && ((string)outer[2]!).Contains("cancelled") && copiedPoint.Location == new Point3d(70, 0, 0),
                "a newer copy from another component remains on the clipboard after an older duplicate callback returns");

            var actionType = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!;
            var clipboardGate = actionType.GetMethod("CommitClipboard", Static)!;
            string? deferredStatus = null, initialStatus = null;
            Action olderWriter = () =>
            {
                object?[] nested = { secondSnapshot, new[] { 1 }, (Action<string>)(value => deferredStatus = value), null };
                Method(second, "TryCopyGeometryWithCompletion").Invoke(second, nested);
                initialStatus = (string?)nested[3];
                Forms.Clipboard.SetText("older writer returned last");
            };
            clipboardGate.Invoke(null, new object?[] { (Func<bool>)(() => true), olderWriter, null });
            using (var currentArchive = ClipboardArchive())
            {
                var point = currentArchive.Objects.Select(item => item.Geometry).OfType<Rhino.Geometry.Point>().Single();
                Check(initialStatus == "Copy queued." && deferredStatus != null && deferredStatus.Contains("geometries copied") &&
                    point.Location == new Point3d(70, 0, 0),
                    "a copy reentered inside the older clipboard writer is deferred and its data and status ultimately win");
            }
            string? firstDeferred = null, secondDeferred = null;
            olderWriter = () =>
            {
                object?[] firstQueued = { secondSnapshot, new[] { 1 }, (Action<string>)(value => firstDeferred = value), null };
                object?[] secondQueued = { secondSnapshot, new[] { 1 }, (Action<string>)(value => secondDeferred = value), null };
                Method(second, "TryCopyGeometryWithCompletion").Invoke(second, firstQueued);
                Method(second, "TryCopyGeometryWithCompletion").Invoke(second, secondQueued);
                Forms.Clipboard.SetText("older generation writer");
            };
            clipboardGate.Invoke(null, new object?[] { (Func<bool>)(() => true), olderWriter, null });
            using (var currentArchive = ClipboardArchive())
            {
                Check(firstDeferred != null && firstDeferred.Contains("cancelled") && secondDeferred != null &&
                    secondDeferred.Contains("geometries copied") && currentArchive.Objects.Count == 1,
                    "a newer queued clipboard generation cancels the prior queued status and publishes last");
            }

            var secondSource = Field<IGH_Goo?[]>(second, "_sourceItems");
            var mutablePoint = (GH_Point)secondSource[1]!;
            Point3d original = mutablePoint.Value;
            deferredStatus = null;
            olderWriter = () =>
            {
                object?[] nested = { secondSnapshot, new[] { 1 }, (Action<string>)(value => deferredStatus = value), null };
                Method(second, "TryCopyGeometryWithCompletion").Invoke(second, nested);
                mutablePoint.Value = new Point3d(90, 0, 0);
                Forms.Clipboard.SetText("older source mutation");
            };
            clipboardGate.Invoke(null, new object?[] { (Func<bool>)(() => true), olderWriter, null });
            Check(deferredStatus != null && deferredStatus.Contains("cancelled") && Forms.Clipboard.GetText() == "older source mutation",
                "a queued copy rechecks the captured source graph when dispatched");
            mutablePoint.Value = original;

            var secondSelection = Property<object>(second, "RowSelection");
            var secondResult = (PanelQueryResult)secondSession.GetType().GetProperty("Result")!.GetValue(secondSession)!;
            deferredStatus = null;
            olderWriter = () =>
            {
                object?[] nested = { secondSnapshot, new[] { 1 }, (Action<string>)(value => deferredStatus = value), null };
                Method(second, "TryCopyGeometryWithCompletion").Invoke(second, nested);
                Method(secondSelection, "SelectAt").Invoke(secondSelection, new object[] { secondResult, 1, 1, true, false, false });
                Forms.Clipboard.SetText("older selection mutation");
            };
            clipboardGate.Invoke(null, new object?[] { (Func<bool>)(() => true), olderWriter, null });
            Check(deferredStatus != null && deferredStatus.Contains("cancelled") && Forms.Clipboard.GetText() == "older selection mutation",
                "a queued copy rechecks the selection generation when dispatched");

            double tolerance = rhino.ModelAbsoluteTolerance;
            deferredStatus = null;
            try
            {
                olderWriter = () =>
                {
                    object?[] nested = { secondSnapshot, new[] { 1 }, (Action<string>)(value => deferredStatus = value), null };
                    Method(second, "TryCopyGeometryWithCompletion").Invoke(second, nested);
                    rhino.ModelAbsoluteTolerance = tolerance * 2;
                    Forms.Clipboard.SetText("older document mutation");
                };
                clipboardGate.Invoke(null, new object?[] { (Func<bool>)(() => true), olderWriter, null });
                Check(deferredStatus != null && deferredStatus.Contains("cancelled") && Forms.Clipboard.GetText() == "older document mutation",
                    "a queued copy rechecks Rhino document settings when dispatched");
            }
            finally { rhino.ModelAbsoluteTolerance = tolerance; }

            var viewport = rhino.Views.ActiveView.ActiveViewport;
            var frameGate = actionType.GetMethod("CommitFrame", Static)!;
            string? frameStatus = null, queuedFrame = null;
            Action olderCamera = () =>
            {
                queuedFrame = (string?)Method(second, "FrameRowsWithCompletion").Invoke(second,
                    new object?[] { secondSnapshot, new[] { 1 }, (Action<string>)(value => frameStatus = value) });
                viewport.SetCameraTarget(new Point3d(-50, 0, 0), true);
            };
            frameGate.Invoke(null, new object?[] { viewport.Id, (Func<bool>)(() => true), olderCamera, null });
            Check(queuedFrame == "Frame queued." && frameStatus == "Selection framed" &&
                viewport.CameraTarget.DistanceTo(new Point3d(70, 0, 0)) < 1e-8,
                "a frame reentered inside the older camera writer ultimately wins with its completion status");
            frameStatus = null;
            try
            {
                olderCamera = () =>
                {
                    Method(second, "FrameRowsWithCompletion").Invoke(second,
                        new object?[] { secondSnapshot, new[] { 1 }, (Action<string>)(value => frameStatus = value) });
                    viewport.LockedProjection = true;
                };
                frameGate.Invoke(null, new object?[] { viewport.Id, (Func<bool>)(() => true), olderCamera, null });
                Check(frameStatus != null && frameStatus.Contains("cancelled") && viewport.CameraTarget.DistanceTo(new Point3d(70, 0, 0)) < 1e-8,
                    "a queued frame rechecks the viewport lock before its deferred publication");
            }
            finally { viewport.LockedProjection = false; }
        }
        finally
        {
            CopyRoundTripUserData.OnDuplicateAction = null;
            document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (prior == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(prior, true);
        }
    }

    private static void VerifyDenseSelectionGestures(Assembly assembly, object highlight)
    {
        const int count = 5000;
        var snapshot = new PanelSnapshot(Enumerable.Range(0, count)
            .Select(i => new PanelRow(i, "{0}", "Point", i.ToString(), null)).ToArray(), new[] { new PanelBranch("{0}", count) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        var source = Enumerable.Range(0, count).Select(i => (IGH_Goo?)new GH_Point(new Point3d(i, 0, 0))).ToArray();
        var selectionType = assembly.GetType("BetterGhPanel.UI.PanelRowSelection", true)!;
        foreach (bool filtered in new[] { false, true })
        foreach (bool labels in new[] { false, true })
        {
            int visits = 0;
            var selection = Activator.CreateInstance(selectionType, Instance, null, new object[] { (Action)(() => visits++) }, null)!;
            Method(selection, "SelectAll").Invoke(selection, new object?[] { result, null, false });
            var ordered = (IReadOnlyList<int>)Method(selection, "OrderedPositions").Invoke(selection, new object[] { result })!;
            Method(highlight, "SetOrderResult").Invoke(highlight, new object?[] { labels ? result : null });
            Method(highlight, "SetScopeChecked").Invoke(highlight, new object[] { source, result, filtered, ordered, (Func<bool>)(() => true), selection });
            visits = 0;
            Method(selection, "SelectAt").Invoke(selection, new object[] { result, 4000, 4000, true, false, false });
            ordered = (IReadOnlyList<int>)Method(selection, "OrderedPositions").Invoke(selection, new object[] { result })!;
            Method(highlight, "SetScopeChecked").Invoke(highlight, new object[] { source, result, filtered, ordered, (Func<bool>)(() => true), selection });
            Check(visits <= 2 && Property<int>(highlight, "LastVisitedRows") <= 1 && ordered.Count == count - 1,
                "dense deselection avoids full-result ordering and preview visits in both preview modes and label settings");
            visits = 0;
            Method(selection, "SelectAt").Invoke(selection, new object[] { result, 4000, 4000, true, false, false });
            ordered = (IReadOnlyList<int>)Method(selection, "OrderedPositions").Invoke(selection, new object[] { result })!;
            Method(highlight, "SetScopeChecked").Invoke(highlight, new object[] { source, result, filtered, ordered, (Func<bool>)(() => true), selection });
            Check(visits <= 2 && Property<int>(highlight, "LastVisitedRows") <= 1 && ordered.Count == count,
                "reselecting one dense member updates only that preview classification");
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
        }
    }

    internal static Task RunReentrantDraw(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified;
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Reentrant preview fixture", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        try
        {
            rhino.Views.ActiveView = view;
            document.Properties.KeepOpen = true;
            document.PreviewMeshType = GH_PreviewMesh.LowQuality;
            var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            component.CreateAttributes(); document.AddObject(component, false);
            object highlight = Field<object>(component, "_highlight");
            using var conduit = new PreviewConduit(component, document) { Enabled = true };
            using var polyline = new PolylineCurve(new[] { Point3d.Origin, new Point3d(2, 0, 0), new Point3d(2, 3, 0) });
            var group = new GH_GeometryGroup(); group.Objects.Add(new GH_Curve { Value = polyline });
            var custom = new StandalonePointPreview();
            Set(highlight, new IGH_Goo?[] { group, custom }, new[] { 0, 1 }, new[] { 0, 1 });
            var ownedCurve = OwnedGeometry(highlight).OfType<PolylineCurve>().Single();
            bool wireCalled = false, wireHeld = false;
            custom.OnWire = () =>
            {
                wireCalled = true;
                Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());
                wireHeld = !ownedCurve.Disposed;
            };
            view.Redraw();
            Check(wireCalled && wireHeld && ownedCurve.Disposed && Count(highlight) == 0,
                "clearing during a standalone wire callback keeps grouped geometry alive until the draw exits");

            using var brep = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
            group = new GH_GeometryGroup(); group.Objects.Add(new GH_Brep { Value = brep });
            Set(highlight, new IGH_Goo?[] { group, custom }, new[] { 0, 1 }, new[] { 0, 1 });
            var ownedBrep = OwnedGeometry(highlight).OfType<Brep>().Single();
            var live = assembly.GetType("BetterGhPanel.Runtime.PanelPreviewLeaf", true)!.GetProperty("LiveGeneratedMeshes", Static)!;
            bool meshCalled = false, meshHeld = false;
            custom.OnMesh = () =>
            {
                meshCalled = true;
                document.PreviewMeshType = GH_PreviewMesh.HighQuality;
                Set(highlight, new IGH_Goo?[] { new GH_Point(new Point3d(50, 0, 0)) }, new[] { 0 }, new[] { 0 });
                component.ExpirePreview(false);
                meshHeld = !ownedBrep.Disposed && (int)live.GetValue(null)! > 0;
            };
            view.Redraw();
            Check(meshCalled && meshHeld && ownedBrep.Disposed && (int)live.GetValue(null)! == 0 &&
                Count(highlight) == 1 && Box(highlight).Contains(new Point3d(50, 0, 0)),
                "meshing reset and replacement defer grouped Brep and generated mesh disposal until shading exits");
            Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());

            var originalPoint = new GH_Point(new Point3d(5, 0, 0));
            var wrapper = new GH_GeometricGooWrapper(originalPoint);
            var wrappedValue = typeof(GH_GeometricGooWrapper).GetField("m_goo", Instance)!;
            var capturedGroup = new GH_GeometryGroup(); capturedGroup.Objects.Add(new GH_Curve { Value = polyline }); capturedGroup.Objects.Add(wrapper);
            GeometryBase? capturedOwned = null;
            bool replacedAndRestored = false;
            Action<int, object> observer = (index, leaf) =>
            {
                if (index != 0) return;
                capturedOwned = Field<GeometryBase?>(leaf, "_owned");
                wrappedValue.SetValue(wrapper, new GH_Point(new Point3d(300, 0, 0)));
                capturedGroup.Objects[1] = new GH_Point(new Point3d(400, 0, 0));
                capturedGroup.Objects[1] = wrapper; wrappedValue.SetValue(wrapper, originalPoint);
                replacedAndRestored = true;
            };
            highlight.GetType().GetField("_admissionObserver", Instance)!.SetValue(highlight, observer);
            Set(highlight, new IGH_Goo?[] { capturedGroup }, new[] { 0 }, new[] { 0 });
            highlight.GetType().GetField("_admissionObserver", Instance)!.SetValue(highlight, null);
            Check(replacedAndRestored && capturedOwned != null && !capturedOwned.Disposed && Count(highlight) == 1 &&
                Box(highlight).Contains(new Point3d(5, 0, 0)) && !Box(highlight).Contains(new Point3d(300, 0, 0)),
                "preview construction uses captured leaves after a group replacement and wrapper retarget are restored");
            var entries = (IEnumerable)Field<object>(highlight, "_entries");
            var pair = entries.Cast<object>().Single();
            var entry = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            var leaves = ((IEnumerable)Field<object>(entry, "Leaves")).Cast<object>().ToArray();
            Check(Field<object>(leaves[1], "_value") is Point3d point && point == new Point3d(5, 0, 0),
                "the drawable point and captured bounds come from the same graph generation");
            component.ExpirePreview(false);
            Method(document, "CreatePreviewCaches").Invoke(document, Array.Empty<object>());
            Check(document.PreviewBoundingBox.Contains(new Point3d(5, 0, 0)) &&
                !document.PreviewBoundingBox.Contains(new Point3d(300, 0, 0)), "host bounds refresh to the captured generation");
            int previousDraws = conduit.Draws; view.Redraw();
            Check(conduit.Draws > previousDraws, "the captured generation reaches the actual viewport draw callback");
            Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());
            Check(capturedOwned!.Disposed && !Box(highlight).IsValid, "clearing the captured generation disposes its owned geometry");
            return Task.CompletedTask;
        }
        finally
        {
            document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
    }

    private static IEnumerable<GeometryBase> OwnedGeometry(object highlight)
    {
        foreach (object pair in (IEnumerable)Field<object>(highlight, "_entries"))
        {
            object entry = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            foreach (object leaf in (IEnumerable)Field<object>(entry, "Leaves"))
                if (Field<object?>(leaf, "_owned") is GeometryBase owned) yield return owned;
        }
    }

    private static void VerifyReentrancy(Assembly assembly, object highlight)
    {
        var previous = Forms.Clipboard.GetDataObject();
        try
        {
            var replacement = new GH_Point(new Point3d(80, 0, 0));
            var custom = new StandalonePointPreview();
            custom.OnBounds = () => Set(highlight, new IGH_Goo?[] { replacement }, new[] { 0 }, new[] { 0 });
            Set(highlight, new IGH_Goo?[] { custom }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 1 && Box(highlight).Contains(new Point3d(80, 0, 0)) &&
                !Box(highlight).Contains(new Point3d(7, 0, 0)),
                "a reentrant preview callback cannot publish the superseded generation");

            bool current = true;
            custom.OnBounds = () => current = false;
            var viewport = RhinoDoc.ActiveDoc!.Views.ActiveView.ActiveViewport;
            var target = viewport.CameraTarget;
            object frame = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!.GetMethod("FrameChecked", Static)!
                .Invoke(null, new object[] { new IGH_Goo?[] { custom }, (Func<bool>)(() => current) })!;
            Check(!Property<bool>(frame, "Success") && Property<string>(frame, "Error").Contains("cancelled") &&
                viewport.CameraTarget == target, "a reentrant bounds callback cancels framing before camera publication");

            var doc = RhinoDoc.ActiveDoc!;
            var firstView = doc.Views.ActiveView;
            var secondView = doc.Views.Add("Reentrant frame target", Rhino.Display.DefinedViewportProjection.Top,
                new Rectangle(0, 0, 320, 240), false);
            try
            {
                doc.Views.ActiveView = firstView;
                target = viewport.CameraTarget;
                var otherTarget = secondView.ActiveViewport.CameraTarget;
                custom.OnBounds = () => doc.Views.ActiveView = secondView;
                frame = Frame(assembly, new IGH_Goo?[] { custom });
                Check(!Property<bool>(frame, "Success") && Property<string>(frame, "Error").Contains("cancelled") &&
                    viewport.CameraTarget == target && secondView.ActiveViewport.CameraTarget == otherTarget,
                    "a callback switching the active view cancels without moving either camera");

                doc.Views.ActiveView = firstView;
                custom.OnBounds = () => viewport.LockedProjection = true;
                frame = Frame(assembly, new IGH_Goo?[] { custom });
                Check(!Property<bool>(frame, "Success") && Property<string>(frame, "Error").Contains("cancelled") &&
                    viewport.CameraTarget == target, "a callback locking the target viewport cancels before camera motion");
                viewport.LockedProjection = false;
                frame = Frame(assembly, new IGH_Goo?[] { new GH_Point(new Point3d(120, 0, 0)) });
                Check(Property<bool>(frame, "Success") && viewport.CameraTarget.DistanceTo(new Point3d(120, 0, 0)) < 1e-8,
                    "an unchanged unlocked target still frames normally");
            }
            finally
            {
                viewport.LockedProjection = false;
                doc.Views.ActiveView = firstView;
                secondView.Close();
            }

            Forms.Clipboard.SetText("preserve reentrant clipboard");
            int checks = 0;
            using var curve = new LineCurve(Point3d.Origin, new Point3d(2, 0, 0));
            var holder = new GH_Curve { Value = curve };
            object copy = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!.GetMethod("CopyGeometryChecked", Static)!
                .Invoke(null, new object[] { new IGH_Goo?[] { holder }, doc.ModelUnitSystem, doc.ModelAbsoluteTolerance,
                    (Func<bool>)(() => ++checks < 5) })!;
            Check(!Property<bool>(copy, "Success") && Property<string>(copy, "Error").Contains("cancelled") &&
                Forms.Clipboard.GetText() == "preserve reentrant clipboard",
                "a changed generation during native copy/serialization leaves the clipboard unchanged");

            var group = new GH_GeometryGroup(); group.Objects.Add(new GH_Point(Point3d.Origin));
            copy = Copy(assembly, RetargetAfterPreparation(group));
            Check(!Property<bool>(copy, "Success") && Property<string>(copy, "Error").Contains("cancelled") &&
                Forms.Clipboard.GetText() == "preserve reentrant clipboard",
                "a group retargeted after preparation is reinspected before clipboard publication");

            var equalGroup = new GH_GeometryGroup(); equalGroup.Objects.Add(new GH_Point(Point3d.Origin));
            target = viewport.CameraTarget;
            frame = Frame(assembly, RetargetAfterPreparation(equalGroup, true));
            Check(!Property<bool>(frame, "Success") && viewport.CameraTarget == target,
                "replacing a group member with an equal-valued holder cancels frame before camera publication");
            var wrapper = new GH_ObjectWrapper(new GH_Point(Point3d.Origin));
            copy = Copy(assembly, RetargetWrapperAfterPreparation(wrapper));
            Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "preserve reentrant clipboard",
                "retargeting a wrapper to an equal-valued holder cancels before clipboard publication");
            var mutable = new GH_Point(Point3d.Origin);
            target = viewport.CameraTarget;
            frame = Frame(assembly, ChangePointAfterPreparation(mutable));
            Check(!Property<bool>(frame, "Success") && viewport.CameraTarget == target,
                "changing a captured point value on the same holder cancels frame before publication");
            mutable.Value = Point3d.Origin;
            var pointGroup = new GH_GeometryGroup(); pointGroup.Objects.Add(mutable);
            custom.OnBounds = () => mutable.Value = new Point3d(39, 0, 0);
            frame = Frame(assembly, new IGH_Goo?[] { pointGroup, custom });
            Check(!Property<bool>(frame, "Success") && viewport.CameraTarget == target,
                "a reentrant bounds callback changing a grouped point cancels frame");
            mutable.Value = Point3d.Origin;
            copy = Copy(assembly, ChangePointAfterPreparation(mutable));
            Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "preserve reentrant clipboard",
                "changing a captured point value on the same holder cancels copy before publication");
        }
        finally
        {
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            if (previous == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(previous, true);
        }
    }

    private static IEnumerable<IGH_Goo?> RetargetAfterPreparation(GH_GeometryGroup group, bool equal = false)
    {
        yield return group;
        group.Objects.Clear();
        group.Objects.Add(new GH_Point(equal ? Point3d.Origin : new Point3d(90, 0, 0)));
    }

    private static IEnumerable<IGH_Goo?> RetargetWrapperAfterPreparation(GH_ObjectWrapper wrapper)
    {
        yield return wrapper;
        wrapper.Value = new GH_Point(Point3d.Origin);
    }

    private static IEnumerable<IGH_Goo?> ChangePointAfterPreparation(GH_Point point)
    {
        yield return point;
        point.Value = new Point3d(37, 0, 0);
    }

    internal static Task RunNativeResources(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified;
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Preview resources fixture", Rhino.Display.DefinedViewportProjection.Top, new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        try
        {
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            document.Properties.KeepOpen = true; document.PreviewColourSelected = Color.FromArgb(255, 0, 230, 40);
            var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            component.CreateAttributes(); document.AddObject(component, false);
            var highlight = Field<object>(component, "_highlight");
            var wrappedPoint = new GH_ObjectWrapper(new Point3d(0, 0, 0));
            Set(highlight, new IGH_Goo?[] { wrappedPoint }, new[] { 0 }, new[] { 0 });
            component.ExpirePreview(false);
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-4, -4, -1), new Point3d(4, 4, 1)));
            view.Redraw();
            using var pointBitmap = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
            int pointGreen = 0;
            for (int y = 0; y < pointBitmap.Height; y++) for (int x = 0; x < pointBitmap.Width; x++)
            { var c = pointBitmap.GetPixel(x, y); if (c.G > c.R + 40 && c.G > c.B + 40) pointGreen++; }
            Check(pointGreen > 0, "an object-wrapped point actually draws in the Rhino viewport");

            var box = new GH_Box(new Box(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1))));
            var boxGroup = new GH_GeometryGroup(); boxGroup.Objects.Add(box);
            Set(highlight, new IGH_Goo?[] { box, boxGroup }, new[] { 0, 1 }, new[] { 0 });
            long bytes = Property<long>(highlight, "RetainedBytes");
            foreach (var mode in new[] { GH_PreviewMesh.LowQuality, GH_PreviewMesh.HighQuality, GH_PreviewMesh.Custom })
            {
                if (mode == GH_PreviewMesh.Custom) document.PreviewCustomMeshParameters = new MeshingParameters(0.4);
                document.PreviewMeshType = mode;
                component.ExpirePreview(false);
                view.Redraw();
                using var meshBitmap = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
                Check(meshBitmap != null && Count(highlight) == 2 && Property<long>(highlight, "RetainedBytes") == bytes &&
                    Box(highlight).Contains(new Point3d(2, 2, 1)),
                    "mesh-setting changes retain bounded standalone and grouped box snapshots without regenerating caches");
            }
            using var nativeBrep = new Box(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1))).ToBrep();
            var brep = new GH_Brep { Value = nativeBrep };
            var brepGroup = new GH_GeometryGroup(); brepGroup.Objects.Add(brep);
            Set(highlight, new IGH_Goo?[] { brep, brepGroup }, new[] { 0, 1 }, new[] { 0 });
            var leafType = assembly.GetType("BetterGhPanel.Runtime.PanelPreviewLeaf", true)!;
            var live = leafType.GetProperty("LiveGeneratedMeshes", Static)!;
            var builds = leafType.GetProperty("GeneratedMeshBuilds", Static)!;
            var shaded = Rhino.Display.DisplayModeDescription.FindByName("Shaded");
            if (shaded != null) view.ActiveViewport.DisplayMode = shaded;
            document.PreviewMode = GH_PreviewMode.Shaded;
            foreach (var mode in new[] { GH_PreviewMesh.LowQuality, GH_PreviewMesh.HighQuality, GH_PreviewMesh.Custom })
            {
                if (mode == GH_PreviewMesh.Custom) document.PreviewCustomMeshParameters = new MeshingParameters(0.35);
                document.PreviewMeshType = mode;
                component.ExpirePreview(false);
                int before = (int)builds.GetValue(null)!;
                view.Redraw();
                using var bitmap = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
                Check(bitmap != null && Count(highlight) == 2 && (int)builds.GetValue(null)! > before &&
                    (int)live.GetValue(null)! > 0 && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
                    "standalone and grouped Breps regenerate bounded shaded meshes after a meshing setting change");
                int cachedBuilds = (int)builds.GetValue(null)!;
                int cachedMeshes = (int)live.GetValue(null)!;
                view.Redraw();
                using var repeat = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
                Check(repeat != null && (int)builds.GetValue(null)! == cachedBuilds && (int)live.GetValue(null)! == cachedMeshes,
                    "unchanged meshing settings reuse owned meshes instead of regenerating them every frame");
            }
            var oldBounds = Box(highlight);
            Check(nativeBrep.Transform(Transform.Translation(30, 0, 0)), "the live Brep mutates after preview admission");
            Check(Box(highlight).Equals(oldBounds), "standalone and grouped Brep bounds remain on the old generation without expiration");
            component.ExpirePreview(false); view.Redraw();
            using (var oldImage = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480))
                Check(oldImage != null && GreenPixels(oldImage) > 0 && Count(highlight) == 2 && Box(highlight).Equals(oldBounds),
                    "the old Brep generation still draws with its original bounds after source mutation");
            Set(highlight, new IGH_Goo?[] { brep, brepGroup }, new[] { 0, 1 }, new[] { 0 });
            Check(Box(highlight).Min.X > oldBounds.Max.X, "a rebuilt Brep generation uses the new source geometry and bounds");
            view.ActiveViewport.ZoomBoundingBox(Box(highlight));
            component.ExpirePreview(false); view.Redraw();
            using (var newImage = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480))
                Check(newImage != null && GreenPixels(newImage) > 0 && Count(highlight) == 2,
                    "the rebuilt Brep generation draws after expiration");
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            Check((int)live.GetValue(null)! == 0, "clearing a preview disposes every generated Brep mesh");
            component.DocumentContextChanged(document, GH_DocumentContext.Close);
        }
        finally
        {
            document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
        return Task.CompletedTask;
    }

    // Directly exercise the plugin's preview callbacks in a Rhino display
    // conduit as well as retaining the separate GH-document draw fixture above.
    internal static Task RunConduitResources(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified;
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Preview conduit fixture", Rhino.Display.DefinedViewportProjection.Top, new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document();
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes(); component.Attributes.Selected = true;
        using var conduit = new PreviewConduit(component, document);
        try
        {
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-4, -4, -1), new Point3d(4, 4, 1)));
            var highlight = Field<object>(component, "_highlight");
            var wrapper = new GH_ObjectWrapper(new Point3d(0, 0, 0));
            Set(highlight, new IGH_Goo?[] { wrapper }, new[] { 0 }, new[] { 0 });
            wrapper.Value = wrapper;
            conduit.Enabled = true;
            using (var bitmap = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480))
            {
                int green = 0;
                for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
                { var c = bitmap.GetPixel(x, y); if (c.G > c.R + 40 && c.G > c.B + 40) green++; }
                Check(green > 0 && conduit.Draws > 0 && Box(highlight).Contains(Point3d.Origin),
                    "a wrapped point actually draws from its old coherent generation after its wrapper becomes cyclic");
            }
            var box = new GH_Box(new Box(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1))));
            var group = new GH_GeometryGroup(); group.Objects.Add(box);
            Set(highlight, new IGH_Goo?[] { box, group }, new[] { 0, 1 }, new[] { 0 });
            long bytes = Property<long>(highlight, "RetainedBytes");
            foreach (var mode in new[] { GH_PreviewMesh.LowQuality, GH_PreviewMesh.HighQuality, GH_PreviewMesh.Custom })
            {
                if (mode == GH_PreviewMesh.Custom) document.PreviewCustomMeshParameters = new MeshingParameters(0.4);
                document.PreviewMeshType = mode;
                int before = conduit.Draws;
                using var bitmap = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
                Check(bitmap != null && conduit.Draws > before && Count(highlight) == 2 &&
                    Property<long>(highlight, "RetainedBytes") == bytes && Box(highlight).Contains(new Point3d(2, 2, 1)),
                    "wire and shaded callbacks retain bounded standalone and grouped boxes across mesh resets");
            }
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
        }
        finally
        {
            conduit.Enabled = false;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
        return Task.CompletedTask;
    }

    // A stock-only diagnostic distinguishes a native display failure from a
    // Panel+ callback; it never turns a failed required preview fixture into a pass.
    internal static Task RunStockPointDiagnostic(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified;
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Stock point diagnostic", Rhino.Display.DefinedViewportProjection.Top, new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        try
        {
            rhino.Views.ActiveView = view;
            document.Properties.KeepOpen = true;
            var point = new Param_Point(); point.CreateAttributes();
            point.PersistentData.Append(new GH_Point(Point3d.Origin), new GH_Path(0));
            document.AddObject(point, false);
            document.NewSolution(false, GH_SolutionMode.Silent);
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-4, -4, -1), new Point3d(4, 4, 1)));
            view.Redraw();
            using var bitmap = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
            Check(bitmap != null, "stock Grasshopper point preview can be captured in this Rhino host");
        }
        finally
        {
            document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
        return Task.CompletedTask;
    }

    private static void VerifyMutation(Assembly assembly, object highlight)
    {
        var point = new GH_Point(new Point3d(1, 2, 3));
        var group = new GH_GeometryGroup { Name = "safe" };
        group.Objects.Add(point);
        var wrapper = new GH_GeometricGooWrapper(group);
        IGH_Goo?[] source = { wrapper };
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Box(highlight).Contains(new Point3d(1, 2, 3)) && Count(highlight) == 1,
            "wrapped group snapshots one bounded point and one group label target");
        point.Value = new Point3d(100, 0, 0);
        group.Objects.Clear(); group.Objects.Add(group);
        Check(Box(highlight).Contains(new Point3d(1, 2, 3)) && !Box(highlight).Contains(new Point3d(100, 0, 0)),
            "mutating a leaf and making the source group cyclic leaves the admitted bounds unchanged");
        Check(!Bounds(assembly, wrapper).IsValid, "frame inspection rejects the group's current cycle");
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && Status(highlight).Contains("partial"),
            "a new generation detects the cycle without dispatching to the group");
        group.Objects.Clear(); group.Objects.Add(new GH_Point(new Point3d(25, 0, 0)));
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Box(highlight).Contains(new Point3d(25, 0, 0)), "source expiration admits the replacement child");

        var objectWrapper = new GH_ObjectWrapper(new Point3d(4, 0, 0));
        source[0] = objectWrapper;
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        objectWrapper.Value = objectWrapper;
        Check(Box(highlight).Contains(new Point3d(4, 0, 0)) && !Bounds(assembly, objectWrapper).IsValid,
            "retargeting a wrapper into itself cannot retarget its preview and current frame rejects it");
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && Status(highlight).Contains("partial"), "self-wrapped objects are unavailable after refresh");
        var geometricSelf = new GH_GeometricGooWrapper();
        typeof(GH_GeometricGooWrapper).GetField("m_goo", Instance)!.SetValue(geometricSelf, geometricSelf);
        source[0] = geometricSelf; Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && !Bounds(assembly, geometricSelf).IsValid, "geometric self-wrapper is bounded before any delegated call");
        var deep = new GH_GeometryGroup(); var current = deep;
        for (int i = 0; i < 80; i++) { var child = new GH_GeometryGroup(); current.Objects.Add(child); current = child; }
        current.Objects.Add(new GH_Point(Point3d.Origin));
        source[0] = deep; Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && Status(highlight).Contains("partial"), "deep acyclic groups stop at the depth budget");
        var hostileHolder = new HostileCurveHolder();
        source[0] = hostileHolder; HostileCurveHolder.Calls = 0;
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && !Bounds(assembly, hostileHolder).IsValid && HostileCurveHolder.Calls == 0,
            "a hostile holder subclass is not treated as an independent standalone preview");

        using var native = new LineCurve(new Point3d(0, 0, 0), new Point3d(3, 0, 0));
        var holder = new GH_Curve(native);
        source[0] = holder; Set(highlight, source, new[] { 0 }, new[] { 0 });
        holder.Value.SetEndPoint(new Point3d(30, 0, 0));
        Check(Box(highlight).Max.X == 3, "in-place curve mutation does not change the old preview generation");
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Box(highlight).Max.X == 30, "refresh reads the newly mutated exact inner object");
        var curveGroup = new GH_GeometryGroup(); curveGroup.Objects.Add(holder);
        source[0] = curveGroup; Set(highlight, source, new[] { 0 }, new[] { 0 });
        holder.Value.SetEndPoint(new Point3d(60, 0, 0));
        Check(Box(highlight).Max.X == 30, "group-derived in-place curve mutation retains coherent admitted bounds");
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Box(highlight).Max.X == 60, "group refresh rebuilds its curve snapshot and bounds together");
        using var hostile = new HostileLineCurve();
        holder.Value = hostile;
        HostileLineCurve.Calls = 0;
        Check(Box(highlight).Max.X == 60 && !Bounds(assembly, holder).IsValid && HostileLineCurve.Calls == 0,
            "replacement with an untrusted inner subclass never reaches its validity callback");
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && HostileLineCurve.Calls == 0 && Status(highlight).Contains("partial"),
            "refresh visibly skips the hostile inner object");
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
    }

    private static void VerifyBudgets(object highlight)
    {
        const int total = 1100;
        var values = Enumerable.Range(0, total).Select(i => (IGH_Goo?)new GH_Point(new Point3d(i, 0, 0))).ToArray();
        var snapshot = new PanelSnapshot(Enumerable.Range(0, total).Select(i => new PanelRow(i, "{0}", "Point", "", null)).ToArray(),
            new[] { new PanelBranch("{0}", total) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        foreach (bool labels in new[] { false, true })
        {
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            Method(highlight, "SetOrderResult").Invoke(highlight, new object?[] { labels ? result : null });
            Scope(highlight, values, result, Array.Empty<int>());
            Check(Count(highlight) == 1024 && Status(highlight).Contains("partial") && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
                "filtered preview enforces bounded admission and reports partial results");
            for (int i = 1024; i < total; i++)
            {
                Scope(highlight, values, result, new[] { i });
                Check(Box(highlight).Contains(new Point3d(i, 0, 0)) && Property<int>(highlight, "LastVisitedRows") <= 1 &&
                    Property<int>(highlight, "LastVisitedNodes") <= 1 && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
                    "successive sparse selections evict and recover without walking filtered rows");
            }
        }
        var huge = new GH_GeometryGroup { Name = "many" };
        for (int i = 0; i < 5000; i++) huge.Objects.Add(new GH_Point(new Point3d(i, 0, 0)));
        Set(highlight, new IGH_Goo?[] { huge }, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && Property<int>(highlight, "LastVisitedNodes") <= 4096 && Status(highlight).Contains("partial"),
            "a group exceeding the per-root budget is unavailable rather than partially dispatched");
        var shared = new GH_GeometryGroup();
        for (int i = 0; i < 100; i++) shared.Objects.Add(new GH_Point(new Point3d(i, 0, 0)));
        var repeated = Enumerable.Repeat<IGH_Goo?>(shared, 1000).ToArray();
        Set(highlight, repeated, Enumerable.Range(0, repeated.Length).ToArray(), Array.Empty<int>());
        Check(Property<int>(highlight, "LastVisitedNodes") <= 20000 && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024 &&
            Status(highlight).Contains("partial"), "repeated groups share an aggregate visit and retained-byte limit");
        using var oversized = new PolylineCurve(Enumerable.Range(0, 2049).Select(i => new Point3d(i, i % 2, 0)));
        Set(highlight, new IGH_Goo?[] { new GH_Curve(oversized) }, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && Property<long>(highlight, "RetainedBytes") == 0 && Status(highlight).Contains("partial"),
            "oversize geometry is rejected at preflight before constructing a preview copy");
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
    }

    private static void VerifyMetadataAndActions(Assembly assembly, object highlight)
    {
        var prior = Forms.Clipboard.GetDataObject();
        try
        {
            using var poly = new PolylineCurve(new[] { Point3d.Origin, new Point3d(2, 0, 0), new Point3d(2, 3, 0) });
            var holder = new GH_Curve(poly);
            var source = (PolylineCurve)holder.Value;
            Check(source.UserData.Add(new PolylineSafetyUserData()) && source.HasUserData, "polyline UserData is attached before plugin work");
            PolylineSafetyUserData.Reset(true);
            Set(highlight, new IGH_Goo?[] { holder }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 1 && Box(highlight).Max.Y == 3 && source.HasUserData && PolylineSafetyUserData.Callbacks == 0,
                "value-only polyline preview preserves source UserData without invoking it");
            object copied = Copy(assembly, new IGH_Goo?[] { new GH_Point(new Point3d(9, 0, 0)), holder });
            Check(Property<bool>(copied, "Success") && Property<int>(copied, "Count") == 1 && Property<int>(copied, "Skipped") == 1 &&
                source.HasUserData && PolylineSafetyUserData.Callbacks > 0 && Property<string>(copied, "Detail").Contains("callback failed"),
                "explicit copy reports a throwing custom callback and preserves the skipped source metadata");
            PolylineSafetyUserData.Reset(false);

            using var polyCurve = new PolyCurve();
            Check(polyCurve.Append(new Line(Point3d.Origin, new Point3d(1, 0, 0))) &&
                polyCurve.Append(new Line(new Point3d(1, 0, 0), new Point3d(2, 1, 0))), "nested polycurve fixture is constructed");
            var polyHolder = new GH_Curve(polyCurve);
            var segment = ((PolyCurve)polyHolder.Value).SegmentCurve(0);
            Check(!polyHolder.Value.HasUserData && segment.UserData.Add(new PolylineSafetyUserData()) && segment.HasUserData,
                "nested segment UserData exists while the outer curve has none");
            PolylineSafetyUserData.Reset(true);
            Set(highlight, new IGH_Goo?[] { polyHolder }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 0 && PolylineSafetyUserData.Callbacks == 0,
                "automatic polycurve preview does not invoke nested custom-data callbacks");
            object polyCopy = Copy(assembly, new IGH_Goo?[] { polyHolder });
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && !Property<bool>(polyCopy, "Success") &&
                segment.HasUserData && !polyHolder.Value.HasUserData && PolylineSafetyUserData.Callbacks > 0,
                "throwing nested custom data is visibly skipped for explicit copy without source metadata loss");
            PolylineSafetyUserData.Reset(false);

            using var brep = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
            Check(brep.UserData.Count == 0 && brep.Curves3D[0].UserData.Add(new CaptureCountingUserData()) &&
                brep.Surfaces[0].UserData.Add(new CaptureBenignUserData()) && brep.Surfaces[0].UserData.Add(new CaptureNonSerializableUserData()) &&
                brep.Curves3D[0].HasUserData && brep.Surfaces[0].UserData.Count == 2,
                "nested hostile, benign and non-serializable Brep data exists without outer data");
            var brepHolder = new GH_Brep { Value = brep };
            var group = new GH_GeometryGroup(); group.Objects.Add(brepHolder);
            CaptureCountingUserData.Callbacks = CaptureBenignUserData.Callbacks = CaptureNonSerializableUserData.Callbacks = 0;
            Set(highlight, new IGH_Goo?[] { brepHolder, group }, new[] { 0, 1 }, new[] { 0, 1 });
            var frame = Bounds(assembly, group);
            Check(Count(highlight) == 0 && frame.IsValid && CaptureCountingUserData.Callbacks == 0 &&
                CaptureBenignUserData.Callbacks == 0 && CaptureNonSerializableUserData.Callbacks == 0,
                "preview and framing avoid nested custom-data callbacks");
            object result = Copy(assembly, new IGH_Goo?[] { brepHolder, group });
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && frame.IsValid && !Property<bool>(result, "Success") &&
                Property<int>(result, "Skipped") == 2 && brep.UserData.Count == 0 && brep.Curves3D[0].HasUserData &&
                brep.Surfaces[0].UserData.Count == 2 && CaptureNonSerializableUserData.Callbacks > 0,
                "explicit copy reports non-serializable nested UserData and preserves the source; current bounds remain frameable");

        }
        finally
        {
            PolylineSafetyUserData.Reset(false);
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            if (prior == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(prior, true);
        }
    }

    private static void VerifyActions(Assembly assembly)
    {
        var prior = Forms.Clipboard.GetDataObject();
        try
        {
            var cycle = new GH_GeometryGroup(); cycle.Objects.Add(cycle);
            object framed = Frame(assembly, new IGH_Goo?[] { new GH_Point(new Point3d(1, 2, 3)), cycle });
            Check(Property<bool>(framed, "Success") && Property<int>(framed, "Skipped") == 1,
                "frame reports a partial action when a safe point accompanies an unsafe cycle");
            var standalone = new StandalonePointPreview();
            Check(Bounds(assembly, standalone).Contains(new Point3d(7, 0, 0)) &&
                Property<bool>(Frame(assembly, new IGH_Goo?[] { standalone }), "Success") && standalone.BoundsCalls > 0,
                "standalone custom point preview and frame retain their best-effort behavior");
            var unsafeGroup = new GH_GeometryGroup(); unsafeGroup.Objects.Add(standalone);
            standalone.BoundsCalls = 0;
            Check(!Bounds(assembly, unsafeGroup).IsValid && standalone.BoundsCalls == 0,
                "recursive group inspection never dispatches a custom preview child");
            var wrappedStandalone = new GH_GeometricGooWrapper(standalone);
            Check(!Bounds(assembly, wrappedStandalone).IsValid && standalone.BoundsCalls == 0,
                "a wrapper cannot turn its custom child into standalone preview dispatch");
            framed = Frame(assembly, Enumerable.Range(0, 2200).Select(i => (IGH_Goo?)new GH_Point(new Point3d(i, 0, 0))));
            Check(Property<bool>(framed, "Success") && Property<bool>(framed, "Exhausted"), "frame reports action-wide exhaustion even after success");
            var toCopy = Enumerable.Range(0, 2200).Select(i => (IGH_Goo?)new GH_Point(new Point3d(i, 0, 0)));
            object limited = Copy(assembly, toCopy);
            Check(Property<bool>(limited, "Success") && Property<bool>(limited, "Exhausted"), "copy reports action-wide exhaustion even after success");

            using var withDictionary = new LineCurve(Point3d.Origin, new Point3d(3, 0, 0));
            var dictionaryHolder = new GH_Curve(withDictionary);
            dictionaryHolder.Value.UserDictionary.Set("panel-test", "preserve");
            Check(dictionaryHolder.Value.HasUserData, "copy fixture has real source metadata");
            object highlight = Activator.CreateInstance(assembly.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!, true)!;
            Set(highlight, new IGH_Goo?[] { dictionaryHolder }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 1 && dictionaryHolder.Value.HasUserData,
                "value-only line preview preserves the source SDK dictionary");
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            object metadataResult = Copy(assembly, new IGH_Goo?[] { new GH_Point(new Point3d(5, 0, 0)), dictionaryHolder });
            Check(Property<bool>(metadataResult, "Success") && Property<int>(metadataResult, "Count") == 2 &&
                Property<int>(metadataResult, "Skipped") == 0 && dictionaryHolder.Value.UserDictionary.GetString("panel-test") == "preserve",
                "geometry copy admits a bounded SDK dictionary without changing the source");
            using var archive = ClipboardArchive();
            var copiedLine = archive.Objects.Select(item => item.Geometry).OfType<LineCurve>().Single();
            Check(copiedLine.UserDictionary.GetString("panel-test") == "preserve",
                "the native clipboard archive preserves the SDK dictionary value");
        }
        finally { if (prior == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(prior, true); }
    }

    private static void VerifyResources(object highlight)
    {
        using var poly = new PolylineCurve(new[] { Point3d.Origin, new Point3d(1, 0, 0), new Point3d(1, 1, 0) });
        using var mesh = Mesh.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(1, 1, 1)), 1, 1, 1);
        var meshHolder = new GH_Mesh(mesh);
        meshHolder.Value.VertexColors.CreateMonotoneMesh(Color.Aqua);
        var source = new IGH_Goo?[] { new GH_Curve(poly), meshHolder, new GH_Box(new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 2, 2)))) };
        Set(highlight, source, new[] { 0, 1, 2 }, new[] { 0 });
        var entries = (IEnumerable)Field<object>(highlight, "_entries");
        var resources = new List<GeometryBase>();
        foreach (object pair in entries)
        {
            object entry = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            foreach (object leaf in (IEnumerable)Field<object>(entry, "Leaves"))
            {
                if (Field<object?>(leaf, "_owned") is GeometryBase owned) resources.Add(owned);
                if (Field<object?>(leaf, "_boxMesh") is GeometryBase boxMesh) resources.Add(boxMesh);
            }
        }
        Check(resources.Count == 3 && resources.All(value => !value.Disposed), "bounded curve, mesh and box resources are owned during preview");
        var copiedMesh = resources.OfType<Mesh>().Single(value => value.VertexColors.Count > 0);
        Check(copiedMesh.VertexColors.Count == meshHolder.Value.VertexColors.Count &&
            copiedMesh.VertexColors[0].ToArgb() == meshHolder.Value.VertexColors[0].ToArgb(),
            "owned mesh preview preserves the source vertex colors");
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
        Check(resources.All(value => value.Disposed) && Property<long>(highlight, "RetainedBytes") == 0 && !Box(highlight).IsValid,
            "clearing a generation disposes each native resource and its bounds");
    }

    private static async Task VerifyDocumentCacheAndDrawing(Assembly assembly, RhinoDoc rhino)
    {
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Preview cache fixture", Rhino.Display.DefinedViewportProjection.Top, new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        var priorClipboard = Forms.Clipboard.GetDataObject();
        try
        {
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            document.Properties.KeepOpen = true; document.PreviewColourSelected = Color.FromArgb(255, 0, 230, 40);
            GH_Document.EnableSolutions = true;
            var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            component.CreateAttributes();
            var source = new Param_GenericObject { Hidden = true };
            source.CreateAttributes();
            source.PersistentData.Append(new GH_Point(new Point3d(-20, 0, 0)), new GH_Path(0));
            source.PersistentData.Append(new GH_Line(new Line(new Point3d(20, -5, 0), new Point3d(20, 5, 0))), new GH_Path(0));
            source.PersistentData.Append(new GH_Number(42), new GH_Path(0));
            document.AddObject(source, false); document.AddObject(component, false);
            component.Params.Input[0].AddSource(source);
            document.NewSolution(false, GH_SolutionMode.Silent);
            object session = PanelTestAdapter.Session(component);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!(bool)session.GetType().GetProperty("HasCurrentResult")!.GetValue(session)!)
            { if (DateTime.UtcNow > deadline) throw new TimeoutException("Preview fixture query did not complete"); await Task.Delay(25); }
            var snapshot = (PanelSnapshot)session.GetType().GetProperty("Snapshot")!.GetValue(session)!;
            var beforeActions = component.Params.Output[0].VolatileData.AllData(true).ToArray();
            string frameStatus = (string)Method(component, "FrameRows").Invoke(component, new object[] { snapshot, new[] { 0, 2 } })!;
            Check(frameStatus.Contains("1 skipped"), "actual frame row action reports partial success");
            object?[] copyArgs = { snapshot, new[] { 0, 2 }, null };
            bool copied = (bool)Method(component, "TryCopyGeometry").Invoke(component, copyArgs)!;
            Check(copied && ((string)copyArgs[2]!).Contains("1 skipped"), "actual geometry-copy row action reports partial success");
            Check(beforeActions.SequenceEqual(component.Params.Output[0].VolatileData.AllData(true)),
                "framing and geometry copy leave the typed output unchanged");
            Method(component, "HighlightRows").Invoke(component, new object[] { snapshot, new[] { 0 } });
            Method(document, "CreatePreviewCaches").Invoke(document, Array.Empty<object>());
            var first = document.PreviewBoundingBox;
            Check(first.Contains(new Point3d(-20, 0, 0)), "host preview cache is primed with the first selected bounds");
            document.IsModified = false;
            Method(component, "HighlightRows").Invoke(component, new object[] { snapshot, new[] { 1 } });
            Check(!document.IsModified, "selection-only preview cache expiration does not mark the document modified");
            bool hasCaches = (bool)Method(document, "HasPreviewCaches").Invoke(document, Array.Empty<object>())!;
            Check(!hasCaches || document.PreviewBoundingBox.Contains(new Point3d(20, 5, 0)),
                "selection invalidates the old host cache or the redraw has already rebuilt it");
            Method(document, "CreatePreviewCaches").Invoke(document, Array.Empty<object>());
            var second = document.PreviewBoundingBox;
            Check(second.Contains(new Point3d(20, 5, 0)) && !second.Contains(new Point3d(-20, 0, 0)),
                "selection expires component and document preview bounds without a source solution");
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(15, -8, -1), new Point3d(25, 8, 1)));
            view.Redraw();
            using var bitmap = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
            int green = 0;
            for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
            { var c = bitmap.GetPixel(x, y); if (c.G > c.R + 40 && c.G > c.B + 40) green++; }
            Check(green > 25, "actual viewport drawing uses the newly admitted selected geometry outside the old bounds");

            component.DocumentContextChanged(document, GH_DocumentContext.Close);
        }
        finally
        {
            document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close();
            if (priorClipboard == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(priorClipboard, true);
        }
    }

    private static object Copy(Assembly assembly, IEnumerable<IGH_Goo?> values) => assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!
        .GetMethod("CopyGeometry", Static)!.Invoke(null, new object[] { values, RhinoDoc.ActiveDoc.ModelUnitSystem, RhinoDoc.ActiveDoc.ModelAbsoluteTolerance })!;
    private static File3dm ClipboardArchive()
    {
        object data = Forms.Clipboard.GetData("Rhino 8 3DM Geometry")!;
        byte[] bytes;
        if (data is byte[] array) bytes = array;
        else if (data is Stream input)
        {
            using var output = new MemoryStream(); input.Position = 0; input.CopyTo(output); bytes = output.ToArray();
        }
        else throw new InvalidOperationException("Native geometry clipboard has no byte payload.");
        return File3dm.FromByteArray(bytes) ?? throw new InvalidOperationException("Invalid geometry clipboard archive.");
    }
    private static object Frame(Assembly assembly, IEnumerable<IGH_Goo?> values) => assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!
        .GetMethod("Frame", Static)!.Invoke(null, new object[] { values })!;
    private static BoundingBox Bounds(Assembly assembly, IGH_Goo value) => (BoundingBox)assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!
        .GetMethod("GetBounds", Static)!.Invoke(null, new object[] { value })!;
    private static void Set(object highlight, IGH_Goo?[] values, int[] positions, int[] selected) =>
        Method(highlight, "Set").Invoke(highlight, new object[] { values, positions, selected });
    private static void Scope(object highlight, IGH_Goo?[] values, PanelQueryResult result, int[] selected) =>
        Method(highlight, "SetScope").Invoke(highlight, new object[] { values, result, true, selected });
    private static BoundingBox Box(object highlight) => Property<BoundingBox>(highlight, "ClippingBox");
    private static int Count(object highlight) => Property<int>(highlight, "PreviewCount");
    private static string Status(object highlight) => Property<string>(highlight, "Status");
    private static int GreenPixels(Bitmap bitmap)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
        { var color = bitmap.GetPixel(x, y); if (color.G > color.R + 40 && color.G > color.B + 40) count++; }
        return count;
    }
    private static MethodInfo Method(object value, string name) => value.GetType().GetMethod(name, Instance)!;
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name, Instance)!.GetValue(value)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Instance)!.GetValue(value)!;
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);

    private sealed class HostileLineCurve : LineCurve
    {
        internal static int Calls;
        public override bool IsValid { get { Calls++; throw new InvalidOperationException("Unexpected hostile validity"); } }
    }

    private sealed class HostileCurveHolder : GH_Curve
    {
        internal static int Calls;
        public override Curve Value { get { Calls++; throw new InvalidOperationException(); } set { Calls++; throw new InvalidOperationException(); } }
        public override bool IsValid { get { Calls++; throw new InvalidOperationException(); } }
        public override object ScriptVariable() { Calls++; throw new InvalidOperationException(); }
    }

    private sealed class StandalonePointPreview : GH_Point, IGH_PreviewData
    {
        internal int BoundsCalls;
        internal Action? OnBounds;
        internal Action? OnWire, OnMesh;
        BoundingBox IGH_PreviewData.ClippingBox
        {
            get
            {
                BoundsCalls++;
                var callback = OnBounds; OnBounds = null; callback?.Invoke();
                return new BoundingBox(new Point3d(7, 0, 0), new Point3d(7, 0, 0));
            }
        }
        void IGH_PreviewData.DrawViewportWires(GH_PreviewWireArgs args)
        { var callback = OnWire; OnWire = null; callback?.Invoke(); }
        void IGH_PreviewData.DrawViewportMeshes(GH_PreviewMeshArgs args)
        { var callback = OnMesh; OnMesh = null; callback?.Invoke(); }
    }

    private sealed class PreviewConduit : Rhino.Display.DisplayConduit, IDisposable
    {
        private readonly GH_Component _component;
        private readonly GH_Document _document;
        private readonly Rhino.Display.DisplayMaterial _normal = new Rhino.Display.DisplayMaterial(Color.Red);
        private readonly Rhino.Display.DisplayMaterial _selected = new Rhino.Display.DisplayMaterial(Color.Lime);
        internal PreviewConduit(GH_Component component, GH_Document document) { _component = component; _document = document; }
        internal int Draws;
        protected override void DrawForeground(Rhino.Display.DrawEventArgs e)
        {
            Draws++;
            var args = new Arguments(_document, e, _normal, _selected);
            _component.DrawViewportMeshes(args);
            _component.DrawViewportWires(args);
        }
        public void Dispose() { Enabled = false; _normal.Dispose(); _selected.Dispose(); }

        private sealed class Arguments : IGH_PreviewArgs
        {
            private readonly Rhino.Display.DrawEventArgs _args;
            internal Arguments(GH_Document document, Rhino.Display.DrawEventArgs args,
                Rhino.Display.DisplayMaterial normal, Rhino.Display.DisplayMaterial selected)
            { Document = document; _args = args; ShadeMaterial = normal; ShadeMaterial_Selected = selected; }
            public int DefaultCurveThickness => 3;
            public Color WireColour => Color.Red;
            public Color WireColour_Selected => Color.Lime;
            public Rhino.Display.DisplayMaterial ShadeMaterial { get; }
            public Rhino.Display.DisplayMaterial ShadeMaterial_Selected { get; }
            public Rhino.Display.RhinoViewport Viewport => _args.Viewport;
            public Rhino.Display.DisplayPipeline Display => _args.Display;
            public GH_Document Document { get; }
            public MeshingParameters MeshingParameters => Document.PreviewCurrentMeshParameters();
        }
    }
}
