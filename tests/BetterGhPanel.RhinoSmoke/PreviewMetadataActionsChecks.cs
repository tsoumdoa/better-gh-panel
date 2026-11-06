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


internal static partial class PreviewSafetyChecks
{
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

    private static void VerifyReferencedPointFreshness(Assembly assembly, object highlight, RhinoDoc rhino)
    {
        var prior = Forms.Clipboard.GetDataObject(); bool modified = rhino.Modified;
        Point3d value = new Point3d(4, 5, 6);
        Guid first = rhino.Objects.AddPoint(value), second = rhino.Objects.AddPoint(value);
        var point = new GH_Point(value) { ReferenceID = first };
        point.ReferenceType(GH_PointRefType.point_object);
        var observerField = highlight.GetType().GetField("_admissionObserver", Instance)!;
        try
        {
            Check(point.IsReferencedGeometry && point.IsGeometryLoaded && point.ReferenceID == first,
                "the point reference fixture starts with a loaded same-value source");
            var witnessType = assembly.GetType("BetterGhPanel.Runtime.CaptureGraphWitness", true)!;
            object?[] captureArgs = { point, 20000, null };
            bool captured = (bool)witnessType.GetMethod("TryCapture", Static)!.Invoke(null, captureArgs)!;
            var witness = captureArgs[2]!;
            Method(witness, "ResolveContexts").Invoke(witness, Array.Empty<object>());
            var isCurrent = witnessType.GetMethod("IsCurrent", Instance, null, Type.EmptyTypes, null)!;
            Check(captured && (bool)isCurrent.Invoke(witness, Array.Empty<object>())!,
                "the captured point witness accepts the original reference");
            point.ReferenceID = second;
            Check(point.Value == value && !(bool)isCurrent.Invoke(witness, Array.Empty<object>())!,
                "the captured point witness rejects a same-value reference retarget");
            point.ReferenceID = first;
            observerField.SetValue(highlight, (Action<int, object>)((_, __) => point.ReferenceID = second));
            Set(highlight, new IGH_Goo?[] { point }, new[] { 0 }, new[] { 0 });
            observerField.SetValue(highlight, null);
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && point.Value == value,
                "preview admission rejects a same-value point retarget during construction");
            point.ReferenceID = first; Set(highlight, new IGH_Goo?[] { point }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 1 && Box(highlight).Contains(value), "an unchanged referenced point previews normally");

            var viewport = rhino.Views.ActiveView.ActiveViewport;
            var target = viewport.CameraTarget;
            var custom = new StandalonePointPreview();
            custom.OnBounds = () => point.ReferenceID = second;
            object framed = Frame(assembly, new IGH_Goo?[] { point, custom });
            Check(!Property<bool>(framed, "Success") && viewport.CameraTarget == target && point.Value == value,
                "a same-value point retarget during a later bounds callback cancels frame");
            point.ReferenceID = first;
            using var curve = new LineCurve(Point3d.Origin, new Point3d(3, 0, 0));
            Check(curve.UserData.Add(new CopyRoundTripUserData { Value = 11 }), "point-reference copy callback data is attached");
            Forms.Clipboard.SetText("keep point reference clipboard");
            CopyRoundTripUserData.DuplicateAt = 1; CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => point.ReferenceID = second;
            object copied = Copy(assembly, new IGH_Goo?[] { point, new GH_Curve { Value = curve } });
            Check(!Property<bool>(copied, "Success") && Forms.Clipboard.GetText() == "keep point reference clipboard" &&
                point.Value == value && CopyRoundTripUserData.TriggeredGeneration > 0,
                "a same-value point retarget during duplication cancels copy before publication");

            point.ReferenceID = first;
            uint serial = rhino.Objects.FindId(first).RuntimeSerialNumber;
            bool replaced = false;
            custom.OnBounds = () => replaced = rhino.Objects.Replace(first, value);
            target = viewport.CameraTarget;
            framed = Frame(assembly, new IGH_Goo?[] { point, custom });
            Check(replaced && rhino.Objects.FindId(first).RuntimeSerialNumber != serial && !Property<bool>(framed, "Success") &&
                viewport.CameraTarget == target && point.ReferenceID == first && point.Value == value,
                "replacing the referenced Rhino object under the same ID cancels frame");
            serial = rhino.Objects.FindId(first).RuntimeSerialNumber; replaced = false;
            CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => replaced = rhino.Objects.Replace(first, value);
            copied = Copy(assembly, new IGH_Goo?[] { point, new GH_Curve { Value = curve } });
            Check(replaced && rhino.Objects.FindId(first).RuntimeSerialNumber != serial && !Property<bool>(copied, "Success") &&
                Forms.Clipboard.GetText() == "keep point reference clipboard" && point.ReferenceID == first && point.Value == value,
                "replacing the referenced Rhino object under the same ID cancels copy");
        }
        finally
        {
            observerField.SetValue(highlight, null); CopyRoundTripUserData.OnDuplicateAction = null;
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            rhino.Objects.Delete(first, true); rhino.Objects.Delete(second, true); rhino.Modified = modified;
            if (prior == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(prior, true);
        }
    }

    private static void VerifyLatePublicationContext(Assembly assembly, RhinoDoc rhino)
    {
        var previous = Forms.Clipboard.GetDataObject();
        var actionType = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!;
        var resultType = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActionResult", true)!;
        var completionType = typeof(Action<>).MakeGenericType(resultType);
        var viewport = rhino.Views.ActiveView.ActiveViewport;
        bool originalLock = viewport.LockedProjection;
        double tolerance = rhino.ModelAbsoluteTolerance;
        bool modified = rhino.Modified;
        try
        {
            viewport.LockedProjection = false;
            var item = new IGH_Goo?[] { new GH_Point(new Point3d(19, 0, 0)) };
            var normalFrame = Frame(assembly, item);
            Check(Property<bool>(normalFrame, "Success"), "unchanged frame publication context remains valid");
            var target = viewport.CameraTarget;
            bool dispatching = false, lockChanged = false;
            int checks = 0;
            object? frameCompletion = null, frameStarted = null;
            Action<object> frameDone = value => frameCompletion = value;
            var typedFrameDone = Delegate.CreateDelegate(completionType, frameDone.Target, frameDone.Method);
            Func<bool> frameCurrent = () =>
            {
                if (dispatching && ++checks == 4) { viewport.LockedProjection = true; lockChanged = true; }
                return true;
            };
            Action frameWriter = () =>
            {
                frameStarted = actionType.GetMethod("FrameWithCompletion", Static)!.Invoke(null,
                    new object?[] { new IGH_Goo?[] { new GH_Point(new Point3d(90, 0, 0)) }, frameCurrent, typedFrameDone });
                dispatching = true;
            };
            actionType.GetMethod("CommitFrame", Static)!.Invoke(null,
                new object?[] { viewport.Id, (Func<bool>)(() => true), frameWriter, null });
            Check(frameStarted != null && Property<bool>(frameStarted, "Deferred") && lockChanged && frameCompletion != null &&
                !Property<bool>(frameCompletion, "Success") && Property<string>(frameCompletion, "Error").Contains("cancelled") && viewport.CameraTarget == target,
                $"a deferred frame rechecks a viewport lock changed by its final source predicate (checks={checks})");
            viewport.LockedProjection = false;

            var normalCopy = Copy(assembly, item);
            Check(Property<bool>(normalCopy, "Success"), "unchanged clipboard publication context remains valid");
            Forms.Clipboard.SetText("keep late-context clipboard");
            dispatching = false; checks = 0;
            bool toleranceChanged = false;
            object? copyCompletion = null, copyStarted = null;
            Action<object> copyDone = value => copyCompletion = value;
            var typedCopyDone = Delegate.CreateDelegate(completionType, copyDone.Target, copyDone.Method);
            Func<bool> copyCurrent = () =>
            {
                if (dispatching && ++checks == 4) { rhino.ModelAbsoluteTolerance = tolerance * 2; toleranceChanged = true; }
                return true;
            };
            Action clipboardWriter = () =>
            {
                copyStarted = actionType.GetMethod("CopyGeometryWithCompletion", Static)!.Invoke(null,
                    new object?[] { item, rhino.ModelUnitSystem, tolerance, copyCurrent, typedCopyDone });
                dispatching = true;
            };
            actionType.GetMethod("CommitClipboard", Static)!.Invoke(null,
                new object?[] { (Func<bool>)(() => true), clipboardWriter, null });
            Check(copyStarted != null && Property<bool>(copyStarted, "Deferred") && toleranceChanged && copyCompletion != null &&
                !Property<bool>(copyCompletion, "Success") && Property<string>(copyCompletion, "Error").Contains("cancelled") && Forms.Clipboard.GetText() == "keep late-context clipboard",
                $"a deferred copy rechecks tolerance changed by its final source predicate (checks={checks})");
        }
        finally
        {
            viewport.LockedProjection = originalLock;
            rhino.ModelAbsoluteTolerance = tolerance; rhino.Modified = modified;
            if (previous == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(previous, true);
        }
    }

    private static async Task VerifyUnsupportedGeometry(Assembly assembly, object highlight, RhinoDoc rhino)
    {
        var prior = Forms.Clipboard.GetDataObject();
        bool modified = rhino.Modified;
        var originalIds = rhino.Objects.Select(item => item.Id).ToHashSet();
        var invalid = new GH_ObjectWrapper(new Polyline(new[] { Point3d.Origin, Point3d.Origin }));
        var unknown = new GH_ObjectWrapper(new object());
        var values = new IGH_Goo?[] { invalid, unknown };
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                Set(highlight, new[] { values[i] }, new[] { 0 }, new[] { 0 });
                Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && !Bounds(assembly, values[i]!).IsValid,
                    "unsupported standalone geometry " + i + " reports partial preview and unavailable bounds");
                object framed = Frame(assembly, new IGH_Goo?[] { values[i], new GH_Point(new Point3d(10, 0, 0)) });
                Check(Property<bool>(framed, "Success") && Property<int>(framed, "Skipped") == 1,
                    "frame visibly skips unsupported standalone geometry " + i);
                object copied = Copy(assembly, new IGH_Goo?[] { values[i], new GH_Point(new Point3d(10, 0, 0)) });
                Check(Property<bool>(copied, "Success") && Property<int>(copied, "Count") == 1 && Property<int>(copied, "Skipped") == 1,
                    "copy visibly skips unsupported standalone geometry " + i);
            }
            var group = new GH_GeometryGroup(); group.Objects.Add(new GH_Point(Point3d.Origin)); group.Objects.Add(new HostileCurveHolder());
            HostileCurveHolder.Calls = 0;
            Set(highlight, new IGH_Goo?[] { group }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && !Bounds(assembly, group).IsValid && HostileCurveHolder.Calls == 0,
                "a group with an unsupported hostile child is not silently admitted or dispatched");
            object groupFrame = Frame(assembly, new IGH_Goo?[] { group, new GH_Point(new Point3d(10, 0, 0)) });
            object groupCopy = Copy(assembly, new IGH_Goo?[] { group, new GH_Point(new Point3d(10, 0, 0)) });
            Check(Property<bool>(groupFrame, "Success") && Property<int>(groupFrame, "Skipped") == 1 &&
                Property<bool>(groupCopy, "Success") && Property<int>(groupCopy, "Skipped") == 1 && HostileCurveHolder.Calls == 0,
                "frame and copy report the skipped group and preserve the separate supported point");
            Check(RhinoApp.RunScript(rhino.RuntimeSerialNumber, "_Paste", false), "Rhino accepts the partial-action clipboard");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!rhino.Objects.Any(item => !originalIds.Contains(item.Id)))
            { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Partial-action Paste did not add its point."); await Task.Delay(25); }
            var pasted = rhino.Objects.Where(item => !originalIds.Contains(item.Id)).ToArray();
            Check(pasted.Length == 1 && pasted[0].Geometry is Rhino.Geometry.Point point && point.Location == new Point3d(10, 0, 0),
                "a partial action pastes only the explicitly supported point");
            var tooLong = new GH_ObjectWrapper(new Polyline(Enumerable.Range(0, 32769).Select(i => new Point3d(i, 0, 0))));
            Forms.Clipboard.SetText("keep oversized clipboard");
            Set(highlight, new IGH_Goo?[] { tooLong }, new[] { 0 }, new[] { 0 });
            object oversized = Copy(assembly, new IGH_Goo?[] { tooLong, new GH_Point(Point3d.Origin) });
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && !Property<bool>(oversized, "Success") &&
                Property<bool>(oversized, "Exhausted") && Forms.Clipboard.GetText() == "keep oversized clipboard",
                "an oversized wrapped polyline exhausts the action visibly without replacing the clipboard");
        }
        finally
        {
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            foreach (var item in rhino.Objects.Where(item => !originalIds.Contains(item.Id)).ToArray()) rhino.Objects.Delete(item.Id, true);
            rhino.Modified = modified;
            if (prior == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(prior, true);
        }
    }

    private static void VerifyDenseSelectionGestures(object highlight)
    {
        const int count = 5000;
        var snapshot = new PanelSnapshot(Enumerable.Range(0, count)
            .Select(i => new PanelRow(i, "{0}", "Point", i.ToString(), null)).ToArray(), new[] { new PanelBranch("{0}", count) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        var source = Enumerable.Range(0, count).Select(i => (IGH_Goo?)new GH_Point(new Point3d(i, 0, 0))).ToArray();
        var selectionType = typeof(BetterGhPanel.Core.PanelSnapshot).Assembly.GetType("BetterGhPanel.Core.PanelRowSelection", true)!;
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

}
