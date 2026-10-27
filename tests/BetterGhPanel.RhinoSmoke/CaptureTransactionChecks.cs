using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

internal static class CaptureTransactionChecks
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void VerifyNestedDataAccess()
    {
        IGH_DataAccess access = new NestedCaptureDataAccess();
        var nested = (NestedCaptureDataAccess)access;
        nested.Primary.Append(new GH_Number(99), new GH_Path(0));
        nested.Paired.Append(new GH_String("new"), new GH_Path(0));
        Check(access.GetDataTree(0, out GH_Structure<IGH_Goo> primary) && ReferenceEquals(primary, nested.Primary) &&
            access.GetDataTree(1, out GH_Structure<IGH_Goo> paired) && ReferenceEquals(paired, nested.Paired) &&
            access.SetDataTree(0, primary) && nested.Outputs.TryGetValue(0, out var output) && ReferenceEquals(output, primary),
            "nested solve seam supplies independent GH input trees and records its output");
    }

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
            VerifyGraphMutations(assembly);
            VerifyRetargetAndRestore(assembly);
            VerifyScalarRetargetAndRestore(assembly);
            VerifyLargeScalarCapture(assembly);
            VerifyStructureWitness(assembly);
            await VerifyScalarMutationAndOutputs(assembly);
            await VerifyScalarRetargetFilter(assembly);
            await VerifyStructureSolve(assembly, 0);
            await VerifyStructureSolve(assembly, 1);
            await VerifyStructureSolve(assembly, 2);
            await VerifyRetryLimit(assembly);
            await VerifyReplacementSolve(assembly, false);
            await VerifyReplacementSolve(assembly, true);
            await VerifyRemoval(assembly, false, false);
            await VerifyRemoval(assembly, true, false);
            await VerifyRemoval(assembly, false, true);
            await VerifyRemoval(assembly, true, true);
            await VerifyLock(assembly);
            await VerifyUnchanged(assembly);
        }
        finally { setEditor.Invoke(null, new object?[] { previous }); }
    }

    private static void VerifyScalarRetargetAndRestore(Assembly assembly)
    {
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!;
        var method = capture.GetMethod("CaptureForMeasurements", Static)!;
        var current = capture.GetMethod("ManagedRowsCurrent", Static)!;
        var number = new GH_Number(1); var text = new GH_String("before");
        var tree = new GH_Structure<IGH_Goo>();
        tree.AppendRange(new IGH_Goo[] { new GH_Curve { Value = new LineCurve(Point3d.Origin, new Point3d(1, 0, 0)) },
            number, text, new GH_Curve { Value = new LineCurve(Point3d.Origin, new Point3d(2, 0, 0)) } }, new GH_Path(0));
        Action<int, IGH_Goo?> observer = (position, _) =>
        {
            if (position == 0) { number.Value = 100; text.Value = "temporary"; }
            if (position == 3) { number.Value = 1; text.Value = "before"; }
        };
        var args = new object?[] { tree, null, null, null, PanelUnitConversion.None, "mm", null, observer, null, null, null };
        var snapshot = (PanelSnapshot)method.Invoke(null, args)!;
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = ">= 50" });
        Check(snapshot.Rows[1].NumericValue == 1 && snapshot.Rows[2].StringValue == "before" && result.RowPositions.Count == 0 &&
            (bool)current.Invoke(null, new[] { snapshot, args[1] })!,
            "temporary scalar replacement and restoration cannot change values consumed by capture or numeric filtering");
    }

    private static async Task VerifyScalarRetargetFilter(Assembly assembly)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Curve { Value = new LineCurve(Point3d.Origin, new Point3d(1, 0, 0)) }, new GH_Path(0));
        fixture.Source.PersistentData.Append(new GH_Number(1), new GH_Path(0));
        fixture.Source.PersistentData.Append(new GH_Number(75), new GH_Path(0));
        fixture.Source.PersistentData.Append(new GH_Curve { Value = new LineCurve(Point3d.Origin, new Point3d(2, 0, 0)) }, new GH_Path(0));
        foreach (string value in new[] { "curve", "one", "seventy-five", "curve2" }) fixture.Paired.PersistentData.Append(new GH_String(value), new GH_Path(0));
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = ">= 50" });
        GH_Number? target = null; bool changed = false, restored = false;
        SetObserver(fixture, (position, _) =>
        {
            if (position == 0)
            {
                target = fixture.Component.Params.Input[0].VolatileData.AllData(false).OfType<GH_Number>().First(value => value.Value == 1);
                target.Value = 100; changed = true;
            }
            if (position == 3 && target != null) { target.Value = 1; restored = true; SetObserver(fixture, null); }
        });
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 2 }) && fixture.Sinks[0].VolatileData.DataCount == 1 &&
            fixture.Sinks[2].VolatileData.DataCount == 4 && fixture.Sinks[3].VolatileData.DataCount == 1);
        fixture.Select(2); await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 1);
        var flags = fixture.Sinks[2].VolatileData.AllData(false).Cast<GH_Boolean>().Select(value => value.Value).ToArray();
        Check(changed && restored && fixture.Result.Snapshot.Rows[1].NumericValue == 1 &&
            ((GH_Number)fixture.Sinks[0].VolatileData.AllData(false).Single()).Value == 75 &&
            ((GH_Number)fixture.Sinks[1].VolatileData.AllData(false).Single()).Value == 75 &&
            flags.SequenceEqual(new[] { false, false, true, false }) &&
            ((GH_String)fixture.Sinks[3].VolatileData.AllData(false).Single()).Value == "seventy-five",
            "a temporary numeric change cannot alter numeric-filter D/S/B/F membership in a real solve");
    }

    private static void VerifyStructureWitness(Assembly assembly)
    {
        var operations = assembly.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!;
        var copy = operations.GetMethod("CopyStructure", Static)!;
        var matches = operations.GetMethod("StructureStillMatches", Static)!;
        GH_Structure<IGH_Goo> Make()
        {
            var tree = new GH_Structure<IGH_Goo>();
            tree.Append(new GH_Number(1), new GH_Path(0)); tree.Append(new GH_String("x"), new GH_Path(0));
            tree.EnsurePath(new GH_Path(1)); return tree;
        }
        bool Same(GH_Structure<IGH_Goo> live, GH_Structure<IGH_Goo> captured) =>
            (bool)matches.Invoke(null, new object[] { live, captured })!;
        var d = Make(); var dSnapshot = (GH_Structure<IGH_Goo>)copy.Invoke(null, new object[] { d })!;
        Check(Same(d, dSnapshot), "unchanged D structure, empty paths and ordered identities match");
        d.EnsurePath(new GH_Path(2));
        Check(!Same(d, dSnapshot), "adding an empty D path invalidates structure evidence");
        d = Make(); dSnapshot = (GH_Structure<IGH_Goo>)copy.Invoke(null, new object[] { d })!;
        d.Branches[0][0] = new GH_Number(1);
        Check(!Same(d, dSnapshot), "replacing a D branch member invalidates identity evidence even with the same value");
        var f = Make(); var fSnapshot = (GH_Structure<IGH_Goo>)copy.Invoke(null, new object[] { f })!;
        f.EnsurePath(new GH_Path(9));
        Check(!Same(f, fSnapshot), "adding an empty F path invalidates paired structure evidence");
    }

    private static async Task VerifyStructureSolve(Assembly assembly, int mode)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Number(1), new GH_Path(0));
        fixture.Source.PersistentData.Append(new GH_Curve { Value = new PolylineCurve(new[] { Point3d.Origin,
            new Point3d(2, 0, 0), new Point3d(2, 3, 0) }) }, new GH_Path(0));
        fixture.Paired.PersistentData.Append(new GH_String("one"), new GH_Path(0));
        fixture.Paired.PersistentData.Append(new GH_String("two"), new GH_Path(0));
        int captures = 0; bool changed = false;
        SetObserver(fixture, (position, _) => { if (position == 0) captures++; });
        SetStructureObserver(fixture, (d, f) =>
        {
            SetStructureObserver(fixture, null); changed = true;
            if (mode == 0) d.EnsurePath(new GH_Path(99));
            else if (mode == 1) d.Branches[0][0] = new GH_Number(99);
            else f!.EnsurePath(new GH_Path(99));
        });
        fixture.Solve();
        await fixture.Ready(() => captures >= 2 && fixture.Result.Snapshot.Rows.Count == 2);
        SetObserver(fixture, null);
        var operations = assembly.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!;
        var matches = operations.GetMethod("StructureStillMatches", Static)!;
        bool dCurrent = (bool)matches.Invoke(null, new[] { Field(fixture, "_liveInputTree"), Field(fixture, "_inputTree") })!;
        bool fCurrent = (bool)matches.Invoke(null, new[] { Field(fixture, "_livePairedTree"), Field(fixture, "_pairedInputTree") })!;
        Check(changed && dCurrent && fCurrent && fixture.Component.Params.Output[0].VolatileData.DataCount == 2,
            "capture discards and recovers after " + (mode == 0 ? "an added empty D path" : mode == 1 ? "a D member replacement" : "an F shape change"));
    }

    private static async Task VerifyRetryLimit(Assembly assembly)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Number(1), new GH_Path(0));
        int attempts = 0;
        SetStructureObserver(fixture, (d, _) => { attempts++; d.EnsurePath(new GH_Path(100 + attempts)); });
        fixture.Solve();
        await Wait(() => attempts >= 2);
        await Task.Delay(100);
        Check(attempts == 2 && Field(fixture, "_measurementSnapshot") == null,
            "continuously changing input is left unavailable after one retry instead of spinning");
        SetStructureObserver(fixture, null);
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.Snapshot.Rows.Count == 1);
        Check(fixture.Result.Snapshot.Rows[0].NumericValue == 1, "a later explicit source expiration recovers from the retry limit");
    }

    private static void VerifyRetargetAndRestore(Assembly assembly)
    {
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!;
        var method = capture.GetMethod("CaptureForMeasurements", Static)!;
        var current = capture.GetMethod("GraphSourcesCurrent", Static)!;
        PanelSnapshot Capture(IGH_Goo root, Action<object>? visitor, out object? graphs)
        {
            var tree = new GH_Structure<IGH_Goo>(); tree.Append(root, new GH_Path(0));
            var args = new object?[] { tree, null, null, null, PanelUnitConversion.None, "mm", null, null, visitor, null, null };
            var snapshot = (PanelSnapshot)method.Invoke(null, args)!; graphs = args[3]; return snapshot;
        }

        var first = new GH_Point(new Point3d(1, 0, 0));
        var invalid = new GH_Point(Point3d.Unset);
        var last = new GH_Point(new Point3d(3, 0, 0));
        var replacement = new GH_Point(new Point3d(2, 0, 0));
        var group = new GH_GeometryGroup { Name = "ABA" };
        group.Objects.Add(first); group.Objects.Add(invalid); group.Objects.Add(last);
        bool changed = false, restored = false;
        var snapshot = Capture(group, node =>
        {
            if (ReferenceEquals(node, first)) { group.Objects[1] = replacement; changed = true; }
            if (ReferenceEquals(node, last)) { group.Objects[1] = invalid; restored = true; }
        }, out var graphs);
        Check(changed && restored && (bool)current.Invoke(null, new[] { graphs })! && snapshot.Rows[0].Validity == PanelValidity.VerifiedInvalid,
            "group replacement and restoration cannot make validity inspect a different child from the captured graph");
        var originalGroup = new GH_GeometryGroup { Name = "original" }; originalGroup.Objects.Add(new GH_Point(Point3d.Unset));
        var otherGroup = new GH_GeometryGroup { Name = "other" }; otherGroup.Objects.Add(new GH_Point(Point3d.Origin));
        var wrapper = new GH_ObjectWrapper(originalGroup);
        bool retargeted = false, returned = false;
        snapshot = Capture(wrapper, node =>
        {
            if (ReferenceEquals(node, wrapper)) { wrapper.Value = otherGroup; retargeted = true; }
            else if (node is GH_GeometryGroup) { wrapper.Value = originalGroup; returned = true; }
        }, out graphs);
        Check(retargeted && returned && (bool)current.Invoke(null, new[] { graphs })! && snapshot.Rows[0].Validity == PanelValidity.VerifiedInvalid,
            "wrapper retargeting and restoration cannot redirect validity away from the captured target");
        snapshot = Capture(new GH_ObjectWrapper(Group()), null, out graphs);
        Check((bool)current.Invoke(null, new[] { graphs })! && snapshot.Rows[0].Validity == PanelValidity.VerifiedValid,
            "unchanged captured wrapper targets and group members remain verified");

        var laterInvalid = new GH_Point(Point3d.Unset);
        var laterGroup = new GH_GeometryGroup { Name = "later" }; laterGroup.Objects.Add(laterInvalid);
        var laterPoint = new GH_Point(Point3d.Unset);
        var tree = new GH_Structure<IGH_Goo>();
        tree.AppendRange(new IGH_Goo[] { new GH_Curve { Value = new LineCurve(Point3d.Origin, new Point3d(1, 0, 0)) },
            laterGroup, laterPoint, new GH_Curve { Value = new LineCurve(Point3d.Origin, new Point3d(2, 0, 0)) } }, new GH_Path(0));
        Action<int, IGH_Goo?> changeLater = (position, _) =>
        {
            if (position == 0) { laterGroup.Objects[0] = new GH_Point(Point3d.Origin); laterPoint.Value = Point3d.Origin; }
            if (position == 3) { laterGroup.Objects[0] = laterInvalid; laterPoint.Value = Point3d.Unset; }
        };
        var args = new object?[] { tree, null, null, null, PanelUnitConversion.None, "mm", null, changeLater, null, null, null };
        snapshot = (PanelSnapshot)method.Invoke(null, args)!;
        Check((bool)current.Invoke(null, new[] { args[3] })! && snapshot.Rows[1].Validity == PanelValidity.VerifiedInvalid &&
            snapshot.Rows[2].Validity == PanelValidity.VerifiedInvalid,
            "earlier callbacks cannot replace later group members or point values and restore them after capture");
    }

    private static void VerifyGraphMutations(Assembly assembly)
    {
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!;
        var captureMethod = capture.GetMethod("CaptureForMeasurements", Static)!;
        var graphsCurrent = capture.GetMethod("GraphSourcesCurrent", Static)!;
        var sourcesCurrent = capture.GetMethod("MeasurementSourcesCurrent", Static)!;
        void CheckGraph(string description, Action<GH_GeometryGroup, GH_ObjectWrapper, GH_Curve> mutate)
        {
            var group = Group();
            var curve = new GH_Curve { Value = new LineCurve(Point3d.Origin, new Point3d(3, 4, 0)) };
            group.Objects.Add(curve);
            var wrapper = new GH_ObjectWrapper(group);
            var tree = new GH_Structure<IGH_Goo>(); tree.Append(wrapper, new GH_Path(0));
            Action<int, IGH_Goo?> observer = (_, __) => mutate(group, wrapper, curve);
            var args = new object?[] { tree, null, null, null, PanelUnitConversion.None, "mm", null, observer, null, null, null };
            var snapshot = (PanelSnapshot)captureMethod.Invoke(null, args)!;
            Check(snapshot.Rows[0].Validity == PanelValidity.Unverified && snapshot.Rows[0].DisplayValue == "<unavailable value>" &&
                !(bool)graphsCurrent.Invoke(null, new[] { args[3] })!, description);
        }
        CheckGraph("adding a group child invalidates its captured summary", (group, _, __) => group.Objects.Add(new GH_Point(new Point3d(4, 0, 0))));
        CheckGraph("removing a group child invalidates its captured summary", (group, _, __) => group.Objects.RemoveAt(0));
        CheckGraph("reordering group members invalidates its captured summary", (group, _, __) =>
        { var first = group.Objects[0]; group.Objects[0] = group.Objects[1]; group.Objects[1] = first; });
        CheckGraph("renaming a group invalidates its captured summary", (group, _, __) => group.Name = "changed");
        CheckGraph("retargeting a wrapper invalidates its captured identity", (_, wrapper, __) => wrapper.Value = Group());
        CheckGraph("replacing a terminal holder's inner geometry invalidates the group witness", (_, __, curve) =>
            curve.Value = new LineCurve(Point3d.Origin, new Point3d(8, 0, 0)));
        CheckGraph("mutating a group point invalidates the witnessed leaf value", (group, _, __) =>
            ((GH_Point)group.Objects[0]).Value = Point3d.Unset);

        var holder = new GH_Curve { Value = new PolylineCurve(new[] { Point3d.Origin, new Point3d(2, 0, 0), new Point3d(2, 3, 0) }) };
        var directTree = new GH_Structure<IGH_Goo>(); directTree.Append(holder, new GH_Path(0));
        Action<int, IGH_Goo?> replace = (_, __) => holder.Value = new LineCurve(Point3d.Origin, new Point3d(9, 0, 0));
        var directArgs = new object?[] { directTree, null, null, null, PanelUnitConversion.None, "mm", null, replace, null, null, null };
        captureMethod.Invoke(null, directArgs);
        Check(!(bool)sourcesCurrent.Invoke(null, new[] { directArgs[1], directArgs[2] })!,
            "a direct holder replacement after description rejects the captured inner source");

        var number = new GH_Number(1); var text = new GH_String("before");
        var scalarTree = new GH_Structure<IGH_Goo>();
        scalarTree.AppendRange(new IGH_Goo[] { number, text, new GH_Curve { Value = new LineCurve(Point3d.Origin, new Point3d(3, 0, 0)) } }, new GH_Path(0));
        Action<int, IGH_Goo?> changeEarlier = (position, _) => { if (position == 2) { number.Value = 100; text.Value = "after"; } };
        var scalarArgs = new object?[] { scalarTree, null, null, null, PanelUnitConversion.None, "mm", null, changeEarlier, null, null, null };
        var scalarSnapshot = (PanelSnapshot)captureMethod.Invoke(null, scalarArgs)!;
        var managedCurrent = capture.GetMethod("ManagedRowsCurrent", Static)!;
        Check(!(bool)managedCurrent.Invoke(null, new[] { scalarSnapshot, scalarArgs[1] })!,
            "later geometry inspection cannot leave earlier numeric or text values stale");

        var arc = new GH_Arc(new Arc(Plane.WorldXY, 2, Math.PI / 2));
        var arcTree = new GH_Structure<IGH_Goo>(); arcTree.Append(arc, new GH_Path(0));
        Action<int, IGH_Goo?> adjustArc = (_, __) => arc.Value = new Arc(Plane.WorldXY, 2 + 1e-12, Math.PI / 2);
        var arcArgs = new object?[] { arcTree, null, null, null, PanelUnitConversion.None, "mm", null, adjustArc, null, null, null };
        var arcSnapshot = (PanelSnapshot)captureMethod.Invoke(null, arcArgs)!;
        Check(arcSnapshot.Rows[0].Validity == PanelValidity.Unverified && !(bool)graphsCurrent.Invoke(null, new[] { arcArgs[3] })!,
            "bit-level arc changes invalidate the witness even when Rhino's tolerant Arc.Equals reports equality");

        var stableTree = new GH_Structure<IGH_Goo>(); stableTree.Append(new GH_ObjectWrapper(Group()), new GH_Path(0));
        var stableArgs = new object?[] { stableTree, null, null, null, PanelUnitConversion.None, "mm", null, null, null, null, null };
        var stable = (PanelSnapshot)captureMethod.Invoke(null, stableArgs)!;
        Check(stable.Rows[0].Validity == PanelValidity.VerifiedValid && stable.Rows[0].DisplayValue.Contains("with 2 objects") &&
            (bool)graphsCurrent.Invoke(null, new[] { stableArgs[3] })!, "unchanged bounded group capture retains its summary and validity");
    }

    private static void VerifyLargeScalarCapture(Assembly assembly)
    {
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!;
        var captureMethod = capture.GetMethod("CaptureForMeasurements", Static)!;
        var current = capture.GetMethod("ManagedRowsCurrentWithVisits", Static)!;
        var tree = new GH_Structure<IGH_Goo>();
        string text = new string('x', 256);
        for (int i = 0; i < 50000; i++) tree.Append(new GH_Number(i), new GH_Path(0));
        for (int i = 0; i < 50000; i++) tree.Append(new GH_String(text), new GH_Path(0));
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        object?[] Capture() => new object?[] { tree, null, null, null, PanelUnitConversion.None, "mm", null, null, null, null, null };
        var args = Capture();
        var snapshot = (PanelSnapshot)captureMethod.Invoke(null, args)!;
        var verify = new object?[] { snapshot, args[1], 0 };
        Check((bool)current.Invoke(null, verify)! && (int)verify[2]! == 100000 && ((Array)args[3]!).Length == 0 &&
            snapshot.Rows.Count == 100000 && snapshot.Rows.All(row => row.IsValid) && ReferenceEquals(snapshot.Rows[50000].StringValue, text),
            "100k ordinary scalar rows use one linear comparison without graph witnesses or full-string copies");
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        var filter = assembly.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!.GetMethod("Filter", Static)!;
        var output = (GH_Structure<IGH_Goo>)filter.Invoke(null, new object[] { tree, result })!;
        Check(output.DataCount == 100000 && ReferenceEquals(output.Branches[0][0], tree.Branches[0][0]) &&
            ReferenceEquals(output.Branches[0][99999], tree.Branches[0][99999]), "unchanged large scalar output preserves all typed input values");
        ((GH_Number)tree.Branches[0][0]).Value = 100;
        verify = new object?[] { snapshot, args[1], 0 };
        Check(!(bool)current.Invoke(null, verify)!, "a changed scalar rejects the old 100k-row capture");
        args = Capture(); snapshot = (PanelSnapshot)captureMethod.Invoke(null, args)!;
        verify = new object?[] { snapshot, args[1], 0 };
        Check((bool)current.Invoke(null, verify)! && snapshot.Rows[0].NumericValue == 100, "large scalar capture recovers after a value changes");
        Console.WriteLine("100k scalar capture/check/recovery: " + timer.ElapsedMilliseconds + " ms; allocated " +
            (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) + " bytes on the fixture thread.");
    }

    private static async Task VerifyScalarMutationAndOutputs(Assembly assembly)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        fixture.Source.PersistentData.Append(new GH_Number(1), new GH_Path(0));
        fixture.Source.PersistentData.Append(new GH_Curve { Value = new PolylineCurve(new[] { Point3d.Origin,
            new Point3d(2, 0, 0), new Point3d(2, 3, 0) }) }, new GH_Path(0));
        fixture.Paired.PersistentData.Append(new GH_String("number"), new GH_Path(0));
        fixture.Paired.PersistentData.Append(new GH_String("curve"), new GH_Path(0));
        PanelTestAdapter.Units(fixture.Component, PanelUnitConversion.MillimetersToMeters, -1);
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = ">= 0.05" });
        bool mutated = false;
        SetObserver(fixture, (position, _) =>
        {
            if (position != 1) return;
            SetObserver(fixture, null); mutated = true;
            foreach (var number in fixture.Source.PersistentData.AllData(false).OfType<GH_Number>()
                .Concat(fixture.Source.VolatileData.AllData(false).OfType<GH_Number>())
                .Concat(fixture.Component.Params.Input[0].VolatileData.AllData(false).OfType<GH_Number>()).Distinct()) number.Value = 100;
        });
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 0 }) && fixture.Sinks[0].VolatileData.DataCount == 1 &&
            fixture.Sinks[2].VolatileData.DataCount == 2 && fixture.Sinks[3].VolatileData.DataCount == 1);
        var snapshot = fixture.Result.Snapshot;
        fixture.Select(0);
        await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 1);
        var d = (GH_Number)fixture.Sinks[0].VolatileData.AllData(false).Single();
        var s = (GH_Number)fixture.Sinks[1].VolatileData.AllData(false).Single();
        var mask = fixture.Sinks[2].VolatileData.AllData(false).Cast<GH_Boolean>().Select(value => value.Value).ToArray();
        Check(mutated && Math.Abs(d.Value - 0.1) < 1e-12 && Math.Abs(s.Value - 0.1) < 1e-12 &&
            mask.SequenceEqual(new[] { true, false }) && ((GH_String)fixture.Sinks[3].VolatileData.AllData(false).Single()).Value == "number" &&
            ReferenceEquals(snapshot, fixture.Result.Snapshot),
            "scalar mutation during later capture is re-solved before numeric filtering and D/S/B/F publication; output-only solve reuses its snapshot");
    }

    private static async Task VerifyReplacementSolve(Assembly assembly, bool wrappedGroup)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        fixture.Source.PersistentData.Append(wrappedGroup ? new GH_ObjectWrapper(Group()) : new GH_Curve
        { Value = new PolylineCurve(new[] { Point3d.Origin, new Point3d(2, 0, 0), new Point3d(2, 3, 0) }) }, new GH_Path(0));
        bool called = false;
        Exception? callbackError = null;
        PanelSnapshot? nestedSnapshot = null;
        NestedCaptureDataAccess? nestedAccess = null;
        SetObserver(fixture, (_, __) =>
        {
            SetObserver(fixture, null);
            called = true;
            try
            {
                int number = wrappedGroup ? 77 : 99;
                fixture.Source.PersistentData.Clear();
                fixture.Source.PersistentData.Append(new GH_Number(number), new GH_Path(0));
                Console.WriteLine("CAPTURE_REENTRY: clearing the older component capture"); Console.Out.Flush();
                fixture.Component.ClearData();
                var access = new NestedCaptureDataAccess();
                nestedAccess = access;
                nestedAccess.Primary.Append(new GH_Number(number), new GH_Path(0));
                nestedAccess.Paired.Append(new GH_String("new"), new GH_Path(0));
                // Exercise the component's real SolveInstance and publication logic
                // reentrantly, without recursively entering GH_Document.NewSolution.
                Console.WriteLine("CAPTURE_REENTRY: starting the newer component solve"); Console.Out.Flush();
                fixture.Component.GetType().GetMethod("SolveInstance", Private)!.Invoke(fixture.Component, new object[] { access });
                nestedSnapshot = (PanelSnapshot)fixture.Session.GetType().GetProperty("Snapshot")!.GetValue(fixture.Session)!;
                Console.WriteLine("CAPTURE_REENTRY: newer component solve returned"); Console.Out.Flush();
            }
            catch (Exception error) { callbackError = error; }
        });
        fixture.Solve();
        Check(called && callbackError == null, "reentrant component solve completed: " + (callbackError?.ToString() ?? "no error"));
        Check(nestedSnapshot != null && nestedSnapshot.Rows.Count == 1 && nestedSnapshot.Rows[0].NumericValue == (wrappedGroup ? 77 : 99) &&
            ReferenceEquals(nestedSnapshot, Field(fixture, "_measurementSnapshot")) &&
            ReferenceEquals(nestedSnapshot, fixture.Session.GetType().GetProperty("Snapshot")!.GetValue(fixture.Session)) &&
            nestedAccess != null && nestedAccess.Outputs.TryGetValue(0, out var nestedOutput) &&
            nestedOutput is GH_Structure<IGH_Goo> nestedTree && nestedTree.AllData(false).Single() is GH_Number nestedNumber &&
            nestedNumber.Value == (wrappedGroup ? 77 : 99) &&
            !fixture.Component.Params.Output[0].VolatileData.AllData(false).Any(item => item is GH_Curve || item is GH_ObjectWrapper || item is GH_GeometryGroup),
            "a newer component solve during " + (wrappedGroup ? "wrapper/group" : "standalone holder") +
            " capture commits its own output and the older capture cannot replace it");
        // Once the older GH solution has returned, use the ordinary scheduler to
        // prove that the replacement also reaches the actual downstream output.
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.Snapshot.Rows.Count == 1 && fixture.Result.Snapshot.Rows[0].NumericValue == (wrappedGroup ? 77 : 99));
        Check(called && fixture.Component.Params.Output[0].VolatileData.AllData(false).Single() is GH_Number number &&
            number.Value == (wrappedGroup ? 77 : 99),
            "expiration and a newer solve during " + (wrappedGroup ? "wrapper/group" : "standalone holder") + " capture cannot publish the older input");
    }

    private static async Task VerifyRemoval(Assembly assembly, bool wrappedGroup, bool closeDocument)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        fixture.Source.PersistentData.Clear();
        fixture.Source.PersistentData.Append(wrappedGroup ? new GH_ObjectWrapper(Group()) : new GH_Curve
        { Value = new PolylineCurve(new[] { Point3d.Origin, new Point3d(1, 0, 0), new Point3d(1, 1, 0) }) }, new GH_Path(0));
        bool called = false;
        object? oldSession = null;
        SetObserver(fixture, (_, __) =>
        {
            SetObserver(fixture, null); called = true;
            oldSession = Field(fixture, "_session");
            // Avoid changing GH's document object list during its solution. Invoke
            // the actual component lifecycle callback here, then remove the object
            // from the document after the older solve returns.
            Console.WriteLine("CAPTURE_LIFECYCLE: invoking " + (closeDocument ? "document close" : "removal") + " callback"); Console.Out.Flush();
            if (closeDocument) fixture.Component.DocumentContextChanged(fixture.Document, GH_DocumentContext.Close);
            else fixture.Component.RemovedFromDocument(fixture.Document);
            Console.WriteLine("CAPTURE_LIFECYCLE: callback returned"); Console.Out.Flush();
        });
        fixture.Solve();
        await Task.Delay(30);
        Check(called && oldSession != null && (bool)oldSession.GetType().GetProperty("IsDisposed", Private)!.GetValue(oldSession)! &&
            Field(fixture, "_session") == null && Field(fixture, "_queryTree") == null && Field(fixture, "_measurementSnapshot") == null &&
            !fixture.Component.Params.Output[0].VolatileData.AllData(false).Any(item => item is GH_Curve || item is GH_ObjectWrapper || item is GH_GeometryGroup),
            (closeDocument ? "document close" : "removal") + " while " + (wrappedGroup ? "wrapper/group" : "standalone holder") +
            " capture is on-stack cannot recreate a disposed session or publish old input");
        Check(fixture.Document.RemoveObject(fixture.Component, false), "component can be removed from the GH document after the solution returns");
    }

    private static async Task VerifyLock(Assembly assembly)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Source.PersistentData.Append(new GH_Curve
        { Value = new PolylineCurve(new[] { Point3d.Origin, new Point3d(1, 0, 0), new Point3d(1, 1, 0) }) }, new GH_Path(0));
        bool called = false;
        SetObserver(fixture, (_, __) => { SetObserver(fixture, null); called = true; fixture.Component.Locked = true; });
        fixture.Solve();
        await Task.Delay(30);
        Check(called && fixture.Component.Locked && Field(fixture, "_measurementSnapshot") == null,
            "locking during holder capture does not attach or publish its stale snapshot");
    }

    private static async Task VerifyUnchanged(Assembly assembly)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Source.PersistentData.Append(new GH_ObjectWrapper(Group()), new GH_Path(0));
        fixture.Paired.PersistentData.Clear();
        int callbacks = 0; SetObserver(fixture, (_, __) => callbacks++);
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.Snapshot.Rows.Count == 1);
        var snapshot = fixture.Result.Snapshot;
        Check(callbacks > 0 && snapshot.Rows[0].IsValid && snapshot.Rows[0].DisplayValue.Contains("with 2 objects"),
            "unchanged wrapper/group capture commits normally");
        SetObserver(fixture, null);
    }

    private static GH_GeometryGroup Group()
    {
        var group = new GH_GeometryGroup { Name = "fixture" };
        group.Objects.Add(new GH_Point(new Point3d(1, 0, 0)));
        group.Objects.Add(new GH_Point(new Point3d(2, 0, 0)));
        return group;
    }
    private static void SetObserver(OutputChecks.Fixture f, Action<int, IGH_Goo?>? observer) =>
        f.Component.GetType().GetProperty("CaptureObserverForTesting", Private)!.SetValue(f.Component, observer);
    private static void SetStructureObserver(OutputChecks.Fixture f, Action<GH_Structure<IGH_Goo>, GH_Structure<IGH_Goo>?>? observer) =>
        f.Component.GetType().GetProperty("StructureObserverForTesting", Private)!.SetValue(f.Component, observer);
    private static async Task Wait(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Capture fixture timed out"); await Task.Delay(10); }
    }
    private static object? Field(OutputChecks.Fixture f, string name) => f.Component.GetType().GetField(name, Private)!.GetValue(f.Component);
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
}

// Supplies a second set of real GH trees to the real component solve while the
// first solve remains on-stack. It intentionally does not enter the GH scheduler.
internal sealed class NestedCaptureDataAccess : IGH_DataAccess
{
    internal readonly GH_Structure<IGH_Goo> Primary = new GH_Structure<IGH_Goo>();
    internal readonly GH_Structure<IGH_Goo> Paired = new GH_Structure<IGH_Goo>();
    internal readonly Dictionary<int, IGH_Structure> Outputs = new Dictionary<int, IGH_Structure>();

    public bool GetDataTree<T>(int index, out GH_Structure<T> tree) where T : IGH_Goo
    {
        if (index != 0 && index != 1) throw new InvalidOperationException("Unexpected input index " + index);
        IGH_Structure input = index == 0 ? Primary : Paired;
        if (input is GH_Structure<T> typed)
        {
            tree = typed;
            return true;
        }
        tree = null!;
        return false;
    }
    public bool SetDataTree(int output, IGH_Structure tree) { Outputs[output] = tree; return true; }
    public bool GetDataTree<T>(string name, out GH_Structure<T> tree) where T : IGH_Goo => throw Unexpected();
    public bool SetDataTree(int index, IGH_DataTree tree) => throw Unexpected();
    public bool GetData<T>(int index, ref T destination) => throw Unexpected();
    public bool GetData<T>(string name, ref T destination) => throw Unexpected();
    public bool GetDataList<T>(int index, List<T> list) => throw Unexpected();
    public bool GetDataList<T>(string name, List<T> list) => throw Unexpected();
    public bool SetData(int index, object data) => throw Unexpected();
    public bool SetData(int index, object data, int itemIndexOverride) => throw Unexpected();
    public bool SetData(string name, object data) => throw Unexpected();
    public bool SetDataList(int index, IEnumerable data) => throw Unexpected();
    public bool SetDataList(int index, IEnumerable data, int listIndexOverride) => throw Unexpected();
    public bool SetDataList(string name, IEnumerable data) => throw Unexpected();
    public bool BlitData<T>(int index, GH_Structure<T> tree, bool overwrite) where T : IGH_Goo => throw Unexpected();
    public int Iteration => throw Unexpected();
    public void IncrementIteration() => throw Unexpected();
    public void DisableGapLogic() => throw Unexpected();
    public void DisableGapLogic(int index) => throw Unexpected();
    public GH_Path ParameterTargetPath(int index) => throw Unexpected();
    public int ParameterTargetIndex(int index) => throw Unexpected();
    public void AbortComponentSolution() => throw Unexpected();
    public List<T> Util_RemoveNullRefs<T>(List<T> list) => throw Unexpected();
    public int Util_CountNullRefs<T>(List<T> list) => throw Unexpected();
    public int Util_CountNonNullRefs<T>(List<T> list) => throw Unexpected();
    public bool Util_EnsureNonNullCount<T>(List<T> list, int count) => throw Unexpected();
    public int Util_FirstNonNullItem<T>(List<T> list) => throw Unexpected();
    private static Exception Unexpected() => new InvalidOperationException("Unexpected nested solve data access call");
}
