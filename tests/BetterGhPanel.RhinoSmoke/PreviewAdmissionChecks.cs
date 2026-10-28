using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;
using Forms = System.Windows.Forms;

internal static partial class PreviewSafetyChecks
{
    internal static Task RunAdmissionFreshness(Assembly assembly) => PanelHostFixture.RunWithEditor(assembly, async loaded =>
    {
        FixtureUserData.EnsureRegistered();
        var rhino = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Admission fixture needs a Rhino document.");
        bool modified = rhino.Modified;
        var previous = rhino.Views.ActiveView;
        var prior = Forms.Clipboard.GetDataObject();
        Rhino.Display.RhinoView? view = null;
        object? highlight = null;
        try
        {
            view = rhino.Views.Add("Admission and action freshness", Rhino.Display.DefinedViewportProjection.Top,
                new Rectangle(0, 0, 640, 480), false);
            rhino.Views.ActiveView = view;
            highlight = Activator.CreateInstance(loaded.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!, true)!;
            VerifyInvalidNativeAdmission(loaded, highlight, rhino);
            VerifySelectedRecovery(loaded, highlight);
            foreach (bool copy in new[] { false, true })
            foreach (bool set in new[] { false, true }) await VerifySemanticAction(loaded, copy, set, rhino);
        }
        finally
        {
            CopyRoundTripUserData.OnDuplicateAction = null;
            try
            {
                try
                {
                    Console.WriteLine("Admission cleanup: clearing preview resources.");
                    if (highlight != null) Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
                }
                finally
                {
                    try
                    {
                        Console.WriteLine("Admission cleanup: restoring prior active view.");
                        if (previous != null) rhino.Views.ActiveView = previous;
                    }
                    finally
                    {
                        Console.WriteLine("Admission cleanup: closing fixture view.");
                        view?.Close();
                    }
                }
            }
            finally
            {
                try
                {
                    rhino.Modified = modified;
                    Console.WriteLine($"Admission cleanup: RhinoDoc.Modified before fixture={modified}, restored={rhino.Modified}.");
                }
                finally
                {
                    // Restore the document before entering the system writer,
                    // including on an assertion failure or a view-close error.
                    Console.WriteLine("Admission cleanup: restoring prior clipboard.");
                    if (prior == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(prior, true);
                    Console.WriteLine("Admission cleanup completed.");
                }
            }
        }
    });

    private static void VerifyInvalidNativeAdmission(Assembly assembly, object highlight, RhinoDoc rhino)
    {
        using var mesh = new Mesh();
        mesh.Vertices.Add(0, 0, 0); mesh.Vertices.Add(3, 0, 0); mesh.Vertices.Add(0, 3, 0);
        mesh.Faces.AddFace(0, 0, 2);
        using var brep = new Box(new BoundingBox(Point3d.Origin, new Point3d(3, 3, 3))).ToBrep()
            ?? throw new InvalidOperationException("Invalid Brep fixture could not create its initial box.");
        // BrepFaceList.Add(int) explicitly creates an incomplete face whose
        // caller must supply its loops. Reuse an existing finite surface and
        // deliberately leave that new face without its required outer loop.
        // Editing a vertex location does not reliably make Brep.IsValid false.
        int originalFaces = brep.Faces.Count;
        var incompleteFace = brep.Faces.Add(0);
        using var validMesh = new Mesh();
        validMesh.Vertices.Add(0, 0, 0); validMesh.Vertices.Add(3, 0, 0); validMesh.Vertices.Add(0, 3, 0);
        validMesh.Faces.AddFace(0, 1, 2);
        using var validBrep = new Box(new BoundingBox(Point3d.Origin, new Point3d(3, 3, 3))).ToBrep()
            ?? throw new InvalidOperationException("Valid Brep control could not create its box.");
        Console.WriteLine($"Invalid Brep construction: faces before={originalFaces}, after={brep.Faces.Count}, " +
            $"new face present={incompleteFace != null}, new face loops={incompleteFace?.Loops.Count}.");
        // Evaluate and print every condition before the first assertion, so a
        // failed control does not hide the other native outcomes in the report.
        bool meshValid = InspectNativeAdmissionControl("invalid mesh", mesh, out var meshBounds);
        bool brepValid = InspectNativeAdmissionControl("invalid Brep", brep, out var brepBounds);
        bool validMeshValid = InspectNativeAdmissionControl("valid mesh", validMesh, out var validMeshBounds);
        bool validBrepValid = InspectNativeAdmissionControl("valid Brep", validBrep, out var validBrepBounds);
        Check(incompleteFace != null && brep.Faces.Count == originalFaces + 1 && incompleteFace.Loops.Count == 0,
            "invalid Brep fixture contains an added incomplete face with no boundary loop");
        Check(!meshValid, "invalid mesh control reports IsValid=false");
        Check(meshBounds.IsValid, "invalid mesh control retains finite valid bounds");
        Check(!brepValid, "incomplete nonnull Brep control reports IsValid=false");
        Check(brepBounds.IsValid, "incomplete nonnull Brep control retains finite valid bounds");
        Check(validMeshValid, "unchanged valid mesh control reports IsValid=true");
        Check(validMeshBounds.IsValid, "unchanged valid mesh control retains finite valid bounds");
        Check(validBrepValid, "unchanged valid Brep control reports IsValid=true");
        Check(validBrepBounds.IsValid, "unchanged valid Brep control retains finite valid bounds");
        var actions = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!;
        var values = new IGH_GeometricGoo[] { new GH_Mesh { Value = mesh }, new GH_Brep { Value = brep } };
        for (int i = 0; i < values.Length; i++)
        {
            var group = new GH_GeometryGroup(); group.Objects.Add(new GH_Point(Point3d.Origin)); group.Objects.Add(values[i]);
            foreach (var value in new IGH_Goo[] { values[i], group })
            {
                Set(highlight, new IGH_Goo?[] { value }, new[] { 0 }, new[] { 0 });
                Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && !Bounds(assembly, value).IsValid,
                    "finite-bounds invalid native geometry cannot enter standalone or grouped preview/bounds " + i);
                var camera = rhino.Views.ActiveView.ActiveViewport.CameraTarget;
                var framed = actions.GetMethod("Frame", Static)!.Invoke(null, new object[] { new[] { value } })!;
                Check(!Property<bool>(framed, "Success") && rhino.Views.ActiveView.ActiveViewport.CameraTarget == camera,
                    "invalid native frame preserves the camera " + i);
                Forms.Clipboard.SetText("invalid admission sentinel");
                var copied = Copy(assembly, new[] { value });
                Check(!Property<bool>(copied, "Success") && Forms.Clipboard.GetText() == "invalid admission sentinel",
                    "invalid native copy preserves the clipboard before any writer starts " + i);
            }
        }
        foreach (var value in new IGH_Goo[] { new GH_Mesh { Value = validMesh }, new GH_Brep { Value = validBrep } })
        {
            Set(highlight, new IGH_Goo?[] { value }, new[] { 0 }, new[] { 0 });
            var framed = actions.GetMethod("Frame", Static)!.Invoke(null, new object[] { new[] { value } })!;
            var copied = Copy(assembly, new[] { value });
            Check(Count(highlight) == 1 && !Status(highlight).Contains("partial") && Bounds(assembly, value).IsValid &&
                Property<bool>(framed, "Success") && Property<bool>(copied, "Success"),
                "valid native control retains ordinary preview, frame and copy: " + value.GetType().Name);
        }
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
        Check(!mesh.Disposed && !brep.Disposed && !validMesh.Disposed && !validBrep.Disposed,
            "preview/frame/copy admission never disposes source native geometry");
    }

    private static bool InspectNativeAdmissionControl(string name, GeometryBase geometry, out BoundingBox bounds)
    {
        if (geometry == null) throw new InvalidOperationException("Admission control " + name + " is unexpectedly null.");
        bool valid = geometry.IsValid;
        bounds = geometry.GetBoundingBox(false);
        bool loggedValid = geometry.IsValidWithLog(out string validityLog);
        Console.WriteLine($"Admission control {name}: nonnull=true, type={geometry.GetType().Name}, " +
            $"IsValid={valid}, IsValidWithLog={loggedValid}, bounds valid={bounds.IsValid}, bounds={bounds}, validity log={validityLog}");
        if (geometry is Mesh mesh)
            Console.WriteLine($"Admission control {name}: vertices={mesh.Vertices.Count}, faces={mesh.Faces.Count}.");
        if (geometry is Brep brep)
        {
            bool topology = brep.IsValidTopology(out string topologyLog);
            Console.WriteLine($"Admission control {name}: surfaces={brep.Surfaces.Count}, faces={brep.Faces.Count}, " +
                $"loops={brep.Loops.Count}, IsValidTopology={topology}, topology log={topologyLog}");
        }
        Console.Out.Flush();
        return valid;
    }

    private static void VerifySelectedRecovery(Assembly assembly, object highlight)
    {
        foreach (int count in new[] { 200, 1100 })
        {
            var source = Enumerable.Range(0, count).Select(i => (IGH_Goo?)new GH_Point(new Point3d(i, 0, 0))).ToArray();
            var snapshot = new PanelSnapshot(Enumerable.Range(0, count).Select(i => new PanelRow(i, "{0}", "Point", i.ToString(), null)).ToArray(),
                new[] { new PanelBranch("{0}", count) });
            var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
            object selection = Activator.CreateInstance(assembly.GetType("BetterGhPanel.UI.PanelRowSelection", true)!, Instance,
                null, new object?[] { null }, null)!;
            Method(selection, "SelectAll").Invoke(selection, new object?[] { result, null, false });
            void Update()
            {
                var ordered = (IReadOnlyList<int>)Method(selection, "OrderedPositions").Invoke(selection, new object[] { result })!;
                Method(highlight, "SetScopeChecked").Invoke(highlight, new object[] { source, result, false, ordered, (Func<bool>)(() => true), selection });
            }
            bool Pending() => Property<bool>(Field<object>(highlight, "_recovery"), "Pending");
            void Drain()
            {
                for (int turn = 0; turn < 32 && Field<bool>(highlight, "_recoveryScheduled"); turn++)
                {
                    Method(highlight, "RecoverOnIdle").Invoke(highlight, new object?[] { null, EventArgs.Empty });
                    Check(Property<int>(highlight, "LastVisitedRows") <= 128 && Property<int>(highlight, "LastVisitedNodes") <= 20000 &&
                        Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024, "each preview recovery turn obeys row/graph/resource budgets");
                }
                Check(!Field<bool>(highlight, "_recoveryScheduled"), "preview recovery stops instead of spinning at capacity");
            }
            Update();
            Check(Count(highlight) == 128 && Pending() && Status(highlight).Contains("partial"),
                "select all retains and reports its unadmitted suffix above 128 rows");
            Method(selection, "SelectAt").Invoke(selection, new object[] { result, count - 1, count - 1, true, false, false });
            Update();
            Check(Status(highlight).Contains("partial") && Property<int>(highlight, "LastVisitedRows") <= 1,
                "a sparse deselection preserves partial status without scanning the dense selection");
            Drain();
            if (count == 200)
                Check(Count(highlight) == 199 && !Pending() && Status(highlight) == string.Empty,
                    "bounded idle recovery admits all remaining selected rows and clears partial status");
            else
            {
                Check(Count(highlight) == 1024 && Pending() && Status(highlight).Contains("partial"),
                    "selected demand at capacity stays partial and dormant");
                for (int i = 0; i < 100; i++)
                {
                    Method(selection, "SelectAt").Invoke(selection, new object[] { result, i, i, true, false, false });
                    Update();
                    Check(Property<int>(highlight, "LastVisitedRows") <= 1, "capacity-release toggles remain sparse");
                }
                Drain();
                Check(Count(highlight) == 999 && !Pending() && Status(highlight) == string.Empty &&
                    Box(highlight).Contains(new Point3d(1098, 0, 0)), "capacity release recovers deferred selected rows without another solve");
                Method(selection, "SelectAt").Invoke(selection, new object[] { result, 0, 0, true, false, false }); Update();
                Check(Count(highlight) == 1000 && Property<int>(highlight, "LastVisitedRows") <= 1,
                    "a previously evicted/deselected row can be selected again with one admission");
            }
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            Check(!Pending() && !Field<bool>(highlight, "_recoveryScheduled"), "clearing cancels deferred preview recovery");
        }
    }

    private static async Task VerifySemanticAction(Assembly assembly, bool copy, bool set, RhinoDoc rhino)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        using var curve = new LineCurve(new Point3d(20, 0, 0), new Point3d(22, 0, 0));
        Check(curve.UserData.Add(new CopyRoundTripUserData { Value = 701 }), "semantic action callback metadata is attached");
        var earlier = new GH_GeometryGroup { Name = "earlier" }; earlier.Objects.Add(new GH_Point(Point3d.Origin));
        var group = new GH_GeometryGroup { Name = "keep" }; group.Objects.Add(new GH_Curve { Value = curve });
        var custom = new ActionPointPreview();
        var input = set ? new IGH_Goo[] { earlier, group, custom } : new IGH_Goo[] { group, custom };
        // Persistent generic collection duplicates groups and can erase Name.
        // Reuse the stock-group fixture's verified volatile delivery path, so
        // both initial capture and a real recapture receive the intended names.
        var producer = new NativeGroupFixture.VolatileSource(input);
        producer.CreateAttributes(); fixture.Document.AddObject(producer, false);
        fixture.Component.Params.Input[0].RemoveAllSources();
        fixture.Component.Params.Input[0].AddSource(producer);
        for (int i = 0; i < input.Length; i++) fixture.Paired.PersistentData.Append(new GH_String("paired " + i), new GH_Path(0));
        if (set) PanelTestAdapter.TreeTools(fixture.Component, 0, false, false, false, true);
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "^(?!.*drop)", CreateSet = set });
        void Solve(string phase)
        {
            producer.ExpireSolution(false);
            fixture.Solve();
            DumpSemanticDelivery(fixture, producer, copy, set, phase);
        }
        async Task Ready(Func<bool> condition)
        {
            try { await fixture.Ready(condition); }
            catch { DumpSemanticDelivery(fixture, producer, copy, set, "wait failed"); throw; }
        }
        Solve("initial solve");
        await Ready(() => fixture.Result.Snapshot.Rows.Count == input.Length &&
            fixture.Sinks[0].VolatileData.DataCount == fixture.Result.RowPositions.Count);
        int position = set ? 1 : 0;
        var delivered = fixture.Component.Params.Input[0].VolatileData.AllData(false).ToArray();
        Check(fixture.Result.RowPositions.SequenceEqual(Enumerable.Range(0, input.Length)) &&
            delivered[position] is GH_GeometryGroup named && named.Name == "keep" &&
            fixture.Result.Snapshot.Rows[position].DisplayValue == "Group (keep) with 1 object" &&
            (!set || delivered[0] is GH_GeometryGroup first && first.Name == "earlier" &&
                fixture.Result.Snapshot.Rows[0].DisplayValue == "Group (earlier) with 1 object"),
            "initial delivered group names and captured summaries survive real host collection");
        fixture.Select(position);
        if (!copy) fixture.Select(input.Length - 1, toggle: true);
        await Ready(() => fixture.Sinks[1].VolatileData.DataCount == (copy ? 1 : 2));
        var snapshot = fixture.Result.Snapshot;
        var source = Field<IGH_Goo?[]>(fixture.Component, "_sourceItems");
        var currentGroup = (GH_GeometryGroup)source[position]!;
        var currentEarlier = set ? (GH_GeometryGroup)source[0]! : null;
        var currentPreview = (ActionPointPreview)source[input.Length - 1]!;
        var positions = copy ? new[] { position } : new[] { position, input.Length - 1 };
        string? Frame() => (string?)Method(fixture.Component, "FrameRows").Invoke(fixture.Component, new object[] { snapshot, positions });
        bool Copy(out string status)
        {
            object?[] args = { snapshot, positions, null };
            bool success = (bool)Method(fixture.Component, "TryCopyGeometry").Invoke(fixture.Component, args)!;
            status = (string)args[2]!; return success;
        }
        // The unchanged control must succeed before the mutation is armed, so a
        // broken source/selection witness cannot masquerade as this regression.
        Check(copy ? Copy(out _) : Frame() == null, "unchanged semantic action control succeeds");
        var camera = rhino.Views.ActiveView.ActiveViewport.CameraTarget;
        Forms.Clipboard.SetText("semantic freshness sentinel");
        bool called = false;
        void Mutate()
        {
            called = true;
            if (set) currentEarlier!.Name = earlier.Name = currentGroup.Name; // An unselected earlier row now represents this displayed set value.
            else currentGroup.Name = group.Name = "drop";
        }
        if (copy) CopyRoundTripUserData.OnDuplicateAction = Mutate;
        else currentPreview.OnBounds = Mutate;
        bool success; string status;
        if (copy) success = Copy(out status);
        else { status = Frame() ?? string.Empty; success = status.Length == 0; }
        CopyRoundTripUserData.OnDuplicateAction = null;
        DumpSemanticDelivery(fixture, producer, copy, set, "after callback mutation");
        Check(called && !success && status.Contains("cancelled") &&
            (copy ? Forms.Clipboard.GetText() == "semantic freshness sentinel" : rhino.Views.ActiveView.ActiveViewport.CameraTarget == camera),
            (set ? "a changed earlier set representative" : "a group rename changing text-filter membership") +
            " during preparation cancels " + (copy ? "copy without replacing the clipboard" : "frame without moving the camera"));
        Check(ReferenceEquals(snapshot, fixture.Result.Snapshot) && ReferenceEquals(source[position], currentGroup),
            "the action detects semantic mutation with unchanged snapshot, selected root and selection");
        // Explicitly expire the volatile producer. A persistent-data re-solve
        // would normalize names again and would not test the changed semantics.
        int[] expectedPositions = set ? new[] { 0, 2 } : new[] { 1 };
        Solve("recapture solve");
        await Ready(() => !ReferenceEquals(snapshot, fixture.Result.Snapshot) &&
            fixture.Result.Snapshot.Rows.Count == input.Length &&
            fixture.Sinks[0].VolatileData.DataCount == fixture.Result.RowPositions.Count &&
            fixture.Sinks[1].VolatileData.DataCount == 0 &&
            fixture.Sinks[2].VolatileData.DataCount == input.Length &&
            fixture.Sinks[3].VolatileData.DataCount == fixture.Result.RowPositions.Count);
        DumpSemanticDelivery(fixture, producer, copy, set, "recapture completed");
        var recovered = fixture.Result.Snapshot;
        delivered = fixture.Component.Params.Input[0].VolatileData.AllData(false).ToArray();
        Check(delivered[position] is GH_GeometryGroup changed && changed.Name == (set ? "keep" : "drop") &&
            recovered.Rows[position].DisplayValue == "Group (" + (set ? "keep" : "drop") + ") with 1 object" &&
            (!set || delivered[0] is GH_GeometryGroup representative && representative.Name == "keep" &&
                recovered.Rows[0].DisplayValue == "Group (keep) with 1 object"),
            "recapture receives and witnesses the changed group names after real volatile collection");
        Check(fixture.Result.RowPositions.SequenceEqual(expectedPositions) && !fixture.Result.RowPositions.Contains(position) &&
            (!set || fixture.Result.SetOccurrencesAt(0)?.Count == 2),
            "recapture removes the renamed filtered group or switches the equal-summary set to the earlier representative");
        Check(fixture.SelectedCount == 0 && fixture.Sinks[1].VolatileData.DataCount == 0,
            "recapture clears stale selected S membership");
        bool DataCurrent()
        {
            var data = fixture.Sinks[0].VolatileData.AllData(false).ToArray();
            return data.Length == expectedPositions.Length && (set
                ? data.All(value => value is GH_String) && ((GH_String)data[0]).Value == "Group (keep) with 1 object" &&
                    ((GH_String)data[1]).Value == PanelTestAdapter.CopyValue(fixture.Component, recovered, 2)
                : data.Single() is ActionPointPreview);
        }
        bool PairingCurrent(IReadOnlyList<int> retained)
        {
            var mask = fixture.Sinks[2].VolatileData;
            var paired = fixture.Sinks[3].VolatileData;
            return mask.PathCount == 1 && mask.Paths[0].CompareTo(new GH_Path(0)) == 0 && mask.DataCount == input.Length &&
                mask.AllData(false).Cast<GH_Boolean>().Select(value => value.Value)
                    .SequenceEqual(Enumerable.Range(0, input.Length).Select(retained.Contains)) &&
                paired.PathCount == 1 && paired.Paths[0].CompareTo(new GH_Path(0)) == 0 &&
                paired.AllData(false).All(value => value is GH_String) &&
                paired.AllData(false).Cast<GH_String>().Select(value => value.Value).SequenceEqual(retained.Select(i => "paired " + i));
        }
        Check(DataCurrent() && PairingCurrent(expectedPositions),
            "recovery publishes coherent typed D, original-shape B and correctly paired F with set=" + set);
        bool SelectedCurrent()
        {
            var selected = fixture.Sinks[1].VolatileData.AllData(false).ToArray();
            return selected.Length == 1 && (set
                ? selected[0] is GH_String text && text.Value == "Group (keep) with 1 object"
                : selected[0] is ActionPointPreview);
        }
        int survivor = expectedPositions[0];
        fixture.Select(survivor);
        await Ready(() => fixture.Sinks[1].VolatileData.DataCount == 1);
        Check(SelectedCurrent() &&
            DataCurrent() && PairingCurrent(expectedPositions) && ReferenceEquals(recovered, fixture.Result.Snapshot),
            "selecting a recovered row publishes typed nonempty S and retains Filtered B/F and the same snapshot with set=" + set);
        fixture.Scope(PanelOutputScope.Selected);
        await Ready(() => fixture.Sinks[3].VolatileData.DataCount == 1 && fixture.Values(3) == "paired " + survivor);
        Check(DataCurrent() && SelectedCurrent() && PairingCurrent(new[] { survivor }) &&
            ReferenceEquals(recovered, fixture.Result.Snapshot),
            "Selected scope binds recovered typed D/S and original-shape B/F to the selected representative with set=" + set);
    }

    private static void DumpSemanticDelivery(PanelHostFixture.Fixture fixture, NativeGroupFixture.VolatileSource producer,
        bool copy, bool set, string phase)
    {
        static string Describe(IGH_Goo? item) => item is GH_GeometryGroup group
            ? $"{item.GetType().Name}(Name={JsonSerializer.Serialize(group.Name)}, directCount={group.Objects.Count})"
            : item?.GetType().Name ?? "<null>";
        var delivered = fixture.Component.Params.Input[0].VolatileData.AllData(false).ToArray();
        var captured = Field<IGH_Goo?[]>(fixture.Component, "_sourceItems");
        var snapshot = fixture.Result.Snapshot;
        Console.WriteLine($"SEMANTIC_DELIVERY phase={JsonSerializer.Serialize(phase)} action={(copy ? "copy" : "frame")} set={set} " +
            $"snapshotRows={snapshot.Rows.Count} matches=[{string.Join(",", fixture.Result.RowPositions)}]");
        Console.WriteLine("SEMANTIC_DELIVERY produced=" + string.Join("; ", producer.VolatileData.AllData(false).Select(Describe)));
        Console.WriteLine("SEMANTIC_DELIVERY delivered=" + string.Join("; ", delivered.Select(Describe)));
        Console.WriteLine("SEMANTIC_DELIVERY captured=" + string.Join("; ", captured.Select(Describe)));
        for (int i = 0; i < snapshot.Rows.Count; i++)
            Console.WriteLine($"SEMANTIC_DELIVERY snapshot[{i}] type={snapshot.Rows[i].TypeName} validity={snapshot.Rows[i].Validity} " +
                $"display={JsonSerializer.Serialize(snapshot.Rows[i].DisplayValue)}");
        Console.WriteLine("SEMANTIC_DELIVERY outputs=" + string.Join("; ", Enumerable.Range(0, 4).Select(i => i + "=" + fixture.Values(i))));
        Console.Out.Flush();
    }

    private sealed class ActionPointPreview : GH_Point, IGH_Goo, IGH_PreviewData
    {
        internal Action? OnBounds;
        IGH_Goo IGH_Goo.Duplicate() => this;
        BoundingBox IGH_PreviewData.ClippingBox
        {
            get { var callback = OnBounds; OnBounds = null; callback?.Invoke(); return new BoundingBox(new Point3d(30, 0, 0), new Point3d(30, 0, 0)); }
        }
        void IGH_PreviewData.DrawViewportWires(GH_PreviewWireArgs args) { }
        void IGH_PreviewData.DrawViewportMeshes(GH_PreviewMeshArgs args) { }
    }
}
