using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

internal static class CaptureStockGeometryChecks
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static object?[] Capture(Assembly assembly, GH_Structure<IGH_Goo> tree,
        Action<int, IGH_Goo?>? observer = null, Action<object>? visitor = null)
    {
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!;
        var args = new object?[] { tree, null, null, null, PanelUnitConversion.None, "mm", null, observer, visitor, null, null };
        var snapshot = (PanelSnapshot)capture.GetMethod("CaptureForMeasurements", Static)!.Invoke(null, args)!;
        return new object?[] { snapshot, args[1], args[2], args[3] };
    }

    private static bool HoldersCurrent(Assembly assembly, object?[] result) => (bool)assembly
        .GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("MeasurementSourcesCurrent", Static)!
        .Invoke(null, new[] { result[1], result[2] })!;

    private static bool GraphsCurrent(Assembly assembly, object?[] result) => (bool)assembly
        .GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("GraphSourcesCurrent", Static)!
        .Invoke(null, new[] { result[3] })!;

    internal static void VerifyManaged(Assembly assembly)
    {
        var unsupportedCloud = (PointCloud)RuntimeHelpers.GetUninitializedObject(typeof(CloudSubclass));
        var unsupportedExtrusion = (Extrusion)RuntimeHelpers.GetUninitializedObject(typeof(ExtrusionSubclass));
        GC.SuppressFinalize(unsupportedCloud); GC.SuppressFinalize(unsupportedExtrusion);
        var cloud = new GH_PointCloud(); var extrusion = new GH_Extrusion();
        var unresolvedCloud = new GH_PointCloud(Guid.NewGuid()); var unresolvedExtrusion = new GH_Extrusion(Guid.NewGuid());
        var innerCloud = new GH_PointCloud(unsupportedCloud); var innerExtrusion = new GH_Extrusion(unsupportedExtrusion);
        var groupCloud = Group("cloud", cloud); var groupExtrusion = Group("extrusion", extrusion);
        var groupUnresolved = Group("unresolved", unresolvedCloud); var groupUnresolvedExtrusion = Group("unresolved extrusion", unresolvedExtrusion);
        var groupUnsupported = Group("unsupported", innerExtrusion);
        var tree = new GH_Structure<IGH_Goo>();
        tree.AppendRange(new IGH_Goo[] { cloud, extrusion, unresolvedCloud, unresolvedExtrusion, innerCloud, innerExtrusion,
            new HostileCloudHolder(), new HostileExtrusionHolder(), groupCloud, new GH_GeometricGooWrapper(groupExtrusion),
            new GH_ObjectWrapper(groupUnresolved), groupUnsupported, new GH_ObjectWrapper(groupUnresolvedExtrusion) }, new GH_Path(0));
        HostileCalls = 0;
        var captured = Capture(assembly, tree);
        var snapshot = (PanelSnapshot)captured[0]!;
        int[] invalid = { 0, 1, 8, 9 };
        Check(snapshot.Rows.Select((row, i) => row.Validity == (invalid.Contains(i) ? PanelValidity.VerifiedInvalid : PanelValidity.Unverified)).All(value => value) &&
            snapshot.Rows[0].TypeName == "Point Cloud" && snapshot.Rows[1].TypeName == "Extrusion" &&
            snapshot.Rows[8].TypeName == "Group" && snapshot.Rows[9].TypeName == "{Group}" &&
            snapshot.Rows[0].TypeIdentityAvailable && snapshot.Rows[1].TypeIdentityAvailable && HostileCalls == 0 &&
            HoldersCurrent(assembly, captured) && GraphsCurrent(assembly, captured),
            "null stock holders and groups are invalid; unresolved references and unsupported holder/inner subclasses are unverified without callbacks");
        var all = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        var onlyInvalid = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { OnlyInvalid = true });
        Check(all.Statistics.InvalidCount == 4 && all.Statistics.UnverifiedCount == 9 &&
            onlyInvalid.RowPositions.SequenceEqual(invalid), "invalid filtering and statistics distinguish invalid from unverified stock geometry");
        foreach (var item in new[] { ("Point Cloud", 0), ("Extrusion", 1), ("Group", 8), ("{Group}", 9) })
            foreach (bool set in new[] { false, true })
            {
                var query = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = item.Item1, OnlyInvalid = true, CreateSet = set });
                Check(query.RowPositions.SequenceEqual(new[] { item.Item2 }) && query.Statistics.FilteredSourceStatuses.VerifiedInvalid == 1,
                    "saved stock type and invalid filter preserve the intended occurrence with set=" + set);
                VerifyOutputs(assembly, tree, snapshot, query, item.Item2, set);
            }
        foreach (IGH_Goo unresolved in new IGH_Goo[] { unresolvedCloud, unresolvedExtrusion })
        {
            var single = new GH_Structure<IGH_Goo>(); single.Append(unresolved, new GH_Path(0));
            var unresolvedCapture = Capture(assembly, single); var unresolvedSnapshot = (PanelSnapshot)unresolvedCapture[0]!;
            foreach (bool set in new[] { false, true })
            {
                var query = PanelQueryEngine.Execute(unresolvedSnapshot, new PanelQueryOptions { TypeName = unresolvedSnapshot.Rows[0].TypeName, CreateSet = set });
                Check(unresolvedSnapshot.Rows[0].Validity == PanelValidity.Unverified && query.RowPositions.SequenceEqual(new[] { 0 }) &&
                    query.Statistics.FilteredSourceStatuses.Unverified == 1 && HoldersCurrent(assembly, unresolvedCapture),
                    "an unresolved exact holder remains unverified and searchable with set=" + set);
                VerifyOutputs(assembly, single, unresolvedSnapshot, query, 0, set);
            }
        }
        cloud.ReferenceID = Guid.NewGuid();
        Check(!HoldersCurrent(assembly, captured) && !GraphsCurrent(assembly, captured),
            "retargeting a null cloud reference rejects standalone and nested witnesses");
        cloud.ReferenceID = Guid.Empty;
        Check(HoldersCurrent(assembly, captured) && GraphsCurrent(assembly, captured), "restoring an unchanged cloud reference restores its witnesses");
        extrusion.ReferenceID = Guid.NewGuid();
        Check(!HoldersCurrent(assembly, captured) && !GraphsCurrent(assembly, captured),
            "retargeting a null extrusion reference rejects standalone and wrapped-group witnesses");
        extrusion.ReferenceID = Guid.Empty;
        Check(HoldersCurrent(assembly, captured) && GraphsCurrent(assembly, captured), "restoring an unchanged extrusion reference restores its witnesses");
        cloud.Value = unsupportedCloud;
        Check(!HoldersCurrent(assembly, captured) && !GraphsCurrent(assembly, captured),
            "replacing a null cloud inner object rejects standalone and nested witnesses");
        cloud.Value = null;
        Check(HoldersCurrent(assembly, captured) && GraphsCurrent(assembly, captured), "restoring the unchanged cloud recovers its witnesses");
        extrusion.Value = unsupportedExtrusion;
        Check(!HoldersCurrent(assembly, captured) && !GraphsCurrent(assembly, captured),
            "replacing a null extrusion inner object rejects standalone and wrapped-group witnesses");
        extrusion.Value = null;
        Check(HoldersCurrent(assembly, captured) && GraphsCurrent(assembly, captured), "restoring the unchanged extrusion recovers its witnesses");
        VerifyManagedRetargetAndRestore(assembly, false, unsupportedCloud, unsupportedExtrusion);
        VerifyManagedRetargetAndRestore(assembly, true, unsupportedCloud, unsupportedExtrusion);
    }

    private static void VerifyManagedRetargetAndRestore(Assembly assembly, bool extrusion,
        PointCloud cloudReplacement, Extrusion extrusionReplacement)
    {
        IGH_GeometricGoo holder = extrusion ? new GH_Extrusion() : new GH_PointCloud();
        var tail = new GH_Point(Point3d.Origin); var group = Group("captured", holder); group.Objects.Add(tail);
        var tree = new GH_Structure<IGH_Goo>(); tree.Append(new GH_ObjectWrapper(group), new GH_Path(0));
        bool changed = false, restored = false;
        var captured = Capture(assembly, tree, visitor: node =>
        {
            if (ReferenceEquals(node, group))
            {
                if (extrusion) ((GH_Extrusion)holder).Value = extrusionReplacement;
                else ((GH_PointCloud)holder).Value = cloudReplacement;
                changed = true;
            }
            if (ReferenceEquals(node, tail))
            {
                if (extrusion) ((GH_Extrusion)holder).Value = null;
                else ((GH_PointCloud)holder).Value = null;
                restored = true;
            }
        });
        Check(changed && restored && ((PanelSnapshot)captured[0]!).Rows[0].Validity == PanelValidity.VerifiedInvalid &&
            GraphsCurrent(assembly, captured), "group validity uses the captured " + (extrusion ? "extrusion" : "point cloud") +
            " inner object during temporary replacement and restoration");
    }

    private static void VerifyOutputs(Assembly assembly, GH_Structure<IGH_Goo> tree, PanelSnapshot snapshot,
        PanelQueryResult query, int position, bool set)
    {
        var trees = assembly.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!;
        var outputs = assembly.GetType("BetterGhPanel.Runtime.PanelOutputOperations", true)!;
        object[] selectedArgs = { tree, query.RowPositions };
        object[] setArgs = { tree, query, query.RowPositions, query.BranchPaths };
        var data = (GH_Structure<IGH_Goo>)(set ? outputs.GetMethod("DisplaySet", Static)!.Invoke(null, setArgs)! :
            trees.GetMethod("Filter", Static)!.Invoke(null, new object[] { tree, query })!);
        var selection = (GH_Structure<IGH_Goo>)(set ? outputs.GetMethod("DisplaySet", Static)!.Invoke(null, setArgs)! :
            trees.GetMethod("Select", Static)!.Invoke(null, selectedArgs)!);
        var addresses = snapshot.Rows.Select((_, i) => snapshot.SourceAddressAt(i)).ToArray();
        var mask = (GH_Structure<GH_Boolean>)outputs.GetMethod("Mask", Static)!.Invoke(null, new object[] { tree, addresses, query.RowPositions })!;
        var paired = new GH_Structure<IGH_Goo>();
        for (int i = 0; i < tree.DataCount; i++) paired.Append(new GH_Integer(i), new GH_Path(0));
        var pair = (GH_Structure<IGH_Goo>)outputs.GetMethod("Pair", Static)!.Invoke(null, new object?[] { tree, paired, tree, addresses, null })!;
        var filteredPair = (GH_Structure<IGH_Goo>)trees.GetMethod("Filter", Static)!.Invoke(null, new object[] { pair, query })!;
        Check(data.DataCount == 1 && selection.DataCount == 1 &&
            (set ? data.Branches[0][0] is GH_String && selection.Branches[0][0] is GH_String :
                ReferenceEquals(data.Branches[0][0], tree.Branches[0][position]) && ReferenceEquals(selection.Branches[0][0], tree.Branches[0][position])) &&
            mask.Branches[0].Count(flag => flag.Value) == 1 && mask.Branches[0][position].Value &&
            ((GH_Integer)filteredPair.Branches[0][0]).Value == position,
            "D/S, original-shape B and typed F preserve the filtered occurrence with set=" + set);
    }

    internal static async Task RunNative(Assembly assembly)
    {
        var previous = Instances.DocumentEditor;
        using var owned = previous == null ? new GH_DocumentEditor() : null;
        var editor = previous ?? owned!; _ = editor.Handle;
        var setEditor = typeof(Instances).GetMethod("SetDocumentEditor", Static)!;
        setEditor.Invoke(null, new object?[] { editor });
        try
        {
            using var cloud = new PointCloud(new[] { Point3d.Origin, new Point3d(1, 2, 3) });
            using var profile = new Circle(Plane.WorldXY, 2).ToNurbsCurve();
            using var extrusion = Extrusion.Create(profile, 3, true) ?? throw new InvalidOperationException("Cannot create test extrusion");
            Check(cloud.IsValid && extrusion.IsValid, "native point-cloud and extrusion fixtures are valid");
            VerifyNativeCapture(assembly, cloud, extrusion);
            VerifyNativeReferences(assembly, cloud, extrusion);
            var failures = new List<Exception>();
            async Task Step(string name, Func<Task> run)
            {
                try { await run(); }
                catch (Exception error) { Console.Error.WriteLine("STOCK_STEP " + name + ": " + error); failures.Add(error); }
            }
            await Step("unresolved-component-seam", () => { VerifyNativeUnresolvedSolve(assembly); return Task.CompletedTask; });
            await Step("filter-and-output", () => VerifyNativeSolve(assembly, cloud, extrusion));
            await Step("standalone-cloud-replacement", () => VerifyNativeSolveReplacement(assembly, cloud, extrusion, false, false));
            await Step("standalone-extrusion-replacement", () => VerifyNativeSolveReplacement(assembly, cloud, extrusion, true, false));
            await Step("group-cloud-replacement", () => VerifyNativeSolveReplacement(assembly, cloud, extrusion, false, true));
            await Step("group-extrusion-replacement", () => VerifyNativeSolveReplacement(assembly, cloud, extrusion, true, true));
            if (failures.Count != 0) throw new AggregateException("One or more stock geometry solve checks failed.", failures);
        }
        finally { setEditor.Invoke(null, new object?[] { previous }); }
    }

    private static void VerifyNativeCapture(Assembly assembly, PointCloud cloud, Extrusion extrusion)
    {
        using var unsupportedCloud = new CloudSubclass(); using var unsupportedExtrusion = new ExtrusionSubclass();
        var validCloud = new GH_PointCloud(cloud); var validExtrusion = new GH_Extrusion(extrusion);
        var cloudGroup = Group("valid cloud", validCloud); var extrusionGroup = Group("valid extrusion", validExtrusion);
        var invalidGroup = Group("invalid", new GH_Extrusion());
        var unresolvedGroup = Group("unresolved", new GH_PointCloud(Guid.NewGuid()));
        var unsupportedGroup = Group("unsupported", new GH_Extrusion(unsupportedExtrusion));
        var tree = new GH_Structure<IGH_Goo>();
        tree.AppendRange(new IGH_Goo[] { validCloud, validExtrusion, new GH_PointCloud(), new GH_Extrusion(),
            cloudGroup, new GH_GeometricGooWrapper(extrusionGroup), invalidGroup, new GH_ObjectWrapper(unresolvedGroup),
            new GH_PointCloud(Guid.NewGuid()), new GH_Extrusion(Guid.NewGuid()), new GH_PointCloud(unsupportedCloud),
            new GH_ObjectWrapper(unsupportedGroup), new HostileCloudHolder(), new HostileExtrusionHolder() }, new GH_Path(0));
        HostileCalls = 0;
        var captured = Capture(assembly, tree); var snapshot = (PanelSnapshot)captured[0]!;
        Check(snapshot.Rows.Select(row => row.Validity).SequenceEqual(new[] { PanelValidity.VerifiedValid, PanelValidity.VerifiedValid,
            PanelValidity.VerifiedInvalid, PanelValidity.VerifiedInvalid, PanelValidity.VerifiedValid, PanelValidity.VerifiedValid,
            PanelValidity.VerifiedInvalid, PanelValidity.Unverified, PanelValidity.Unverified, PanelValidity.Unverified,
            PanelValidity.Unverified, PanelValidity.Unverified, PanelValidity.Unverified, PanelValidity.Unverified }) &&
            HostileCalls == 0 && HoldersCurrent(assembly, captured) && GraphsCurrent(assembly, captured),
            "native standalone and grouped cloud/extrusion validity matches valid, invalid and unresolved inputs");
        var filtered = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { OnlyInvalid = true });
        Check(filtered.RowPositions.SequenceEqual(new[] { 2, 3, 6 }) && filtered.Statistics.InvalidCount == 3 &&
            snapshot.Rows[0].TypeName == "Point Cloud" && snapshot.Rows[1].TypeName == "Extrusion" && snapshot.Rows[5].TypeName == "{Group}",
            "native invalid filter, statistics and saved identities include both stock geometry types");
        VerifyNativeGraphAba(assembly, validCloud, cloud, false);
        VerifyNativeGraphAba(assembly, validExtrusion, extrusion, true);
    }

    private static void VerifyNativeGraphAba(Assembly assembly, IGH_GeometricGoo holder, GeometryBase original, bool extrusion)
    {
        var tail = new GH_Point(Point3d.Origin); var group = Group("valid", holder); group.Objects.Add(tail);
        var tree = new GH_Structure<IGH_Goo>(); tree.Append(new GH_ObjectWrapper(group), new GH_Path(0));
        bool changed = false, restored = false;
        var captured = Capture(assembly, tree, visitor: node =>
        {
            if (ReferenceEquals(node, group))
            {
                if (extrusion) ((GH_Extrusion)holder).Value = null; else ((GH_PointCloud)holder).Value = null;
                changed = true;
            }
            if (ReferenceEquals(node, tail))
            {
                if (extrusion) ((GH_Extrusion)holder).Value = (Extrusion)original; else ((GH_PointCloud)holder).Value = (PointCloud)original;
                restored = true;
            }
        });
        Check(changed && restored && ((PanelSnapshot)captured[0]!).Rows[0].Validity == PanelValidity.VerifiedValid && GraphsCurrent(assembly, captured),
            "native group validity uses its captured valid " + (extrusion ? "extrusion" : "cloud") + " during replacement and restoration");
    }

    private static void VerifyNativeReferences(Assembly assembly, PointCloud cloud, Extrusion extrusion)
    {
        var document = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("An active Rhino document is required");
        bool modified = document.Modified;
        Guid[] ids = new Guid[4];
        try
        {
            ids[0] = document.Objects.AddPointCloud(cloud); ids[1] = document.Objects.AddPointCloud(cloud);
            ids[2] = document.Objects.AddExtrusion(extrusion); ids[3] = document.Objects.AddExtrusion(extrusion);
            Check(ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == 4, "native reference fixture creates distinct document objects");
            var referencedCloud = new GH_PointCloud(ids[0]); var referencedExtrusion = new GH_Extrusion(ids[2]);
            Check(referencedCloud.LoadGeometry(document) && referencedExtrusion.LoadGeometry(document), "native stock geometry references load from the active document");
            var group = Group("references", referencedCloud, referencedExtrusion);
            var tree = new GH_Structure<IGH_Goo>();
            tree.AppendRange(new IGH_Goo[] { referencedCloud, referencedExtrusion, new GH_ObjectWrapper(group) }, new GH_Path(0));
            var captured = Capture(assembly, tree); var snapshot = (PanelSnapshot)captured[0]!;
            Check(snapshot.Rows.All(row => row.Validity == PanelValidity.VerifiedValid) && HoldersCurrent(assembly, captured) && GraphsCurrent(assembly, captured),
                "loaded standalone and nested references bind valid source and active document context");
            var cloudValue = referencedCloud.Value; referencedCloud.ReferenceID = ids[1];
            Check(ReferenceEquals(cloudValue, referencedCloud.Value) && !HoldersCurrent(assembly, captured) && !GraphsCurrent(assembly, captured),
                "retargeting a loaded cloud with the same inner object invalidates standalone and group witnesses");
            referencedCloud.ReferenceID = ids[0];
            Check(referencedCloud.LoadGeometry(document), "cloud reference reloads after retargeting");
            captured = Capture(assembly, tree);
            var previousCloud = referencedCloud.Value; referencedCloud.Value = cloud;
            Check(!ReferenceEquals(previousCloud, referencedCloud.Value) && !HoldersCurrent(assembly, captured) && !GraphsCurrent(assembly, captured),
                "replacing a loaded cloud inner object invalidates standalone and group witnesses");
            referencedCloud.ReferenceID = ids[0];
            Check(referencedCloud.LoadGeometry(document), "cloud reference reloads after inner replacement");
            captured = Capture(assembly, tree);
            var extrusionValue = referencedExtrusion.Value; referencedExtrusion.ReferenceID = ids[3];
            Check(ReferenceEquals(extrusionValue, referencedExtrusion.Value) && !HoldersCurrent(assembly, captured) && !GraphsCurrent(assembly, captured),
                "retargeting a loaded extrusion with the same inner object invalidates standalone and group witnesses");
            referencedExtrusion.ReferenceID = ids[2];
            Check(referencedExtrusion.LoadGeometry(document), "extrusion reference reloads after retargeting");
            captured = Capture(assembly, tree);
            var previousExtrusion = referencedExtrusion.Value; referencedExtrusion.Value = extrusion;
            Check(!ReferenceEquals(previousExtrusion, referencedExtrusion.Value) && !HoldersCurrent(assembly, captured) && !GraphsCurrent(assembly, captured),
                "replacing a loaded extrusion inner object invalidates standalone and group witnesses");
            referencedExtrusion.Value = previousExtrusion;
            captured = Capture(assembly, tree);
            Check(HoldersCurrent(assembly, captured) && GraphsCurrent(assembly, captured), "unchanged reference context recovers");
            Check(document.Objects.Delete(ids[0], true), "the referenced cloud document object is deleted"); ids[0] = Guid.Empty;
            Check(!HoldersCurrent(assembly, captured) && !GraphsCurrent(assembly, captured), "deleting the referenced object invalidates both witnesses");
            captured = Capture(assembly, tree);
            Check(HoldersCurrent(assembly, captured) && GraphsCurrent(assembly, captured), "the unresolved cloud recaptures without stale context");
            Check(document.Objects.Delete(ids[2], true), "the referenced extrusion document object is deleted"); ids[2] = Guid.Empty;
            Check(!HoldersCurrent(assembly, captured) && !GraphsCurrent(assembly, captured), "deleting the referenced extrusion invalidates both witnesses");
        }
        finally
        {
            try { foreach (Guid id in ids) if (id != Guid.Empty) document.Objects.Delete(id, true); }
            finally { document.Modified = modified; }
        }
    }

    private static async Task VerifyNativeSolve(Assembly assembly, PointCloud cloud, Extrusion extrusion)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        var inputs = new IGH_Goo[] { new GH_PointCloud(cloud), new GH_PointCloud(), new GH_Extrusion(extrusion), new GH_Extrusion(),
            Group("valid", new GH_PointCloud(cloud), new GH_Extrusion(extrusion)), Group("invalid", new GH_PointCloud()),
            Group("unresolved", new GH_Extrusion(Guid.NewGuid())), new GH_PointCloud(Guid.NewGuid()), new GH_Extrusion(Guid.NewGuid()) };
        for (int i = 0; i < inputs.Length; i++)
        { fixture.Source.PersistentData.Append(inputs[i], new GH_Path(0)); fixture.Paired.PersistentData.Append(new GH_Integer(i), new GH_Path(0)); }
        fixture.Solve(); await fixture.Ready(() => fixture.Result.Snapshot.Rows.Count == inputs.Length && fixture.Sinks[0].VolatileData.DataCount == inputs.Length);
        var snapshot = fixture.Result.Snapshot;
        var live = fixture.Component.Params.Input[0].VolatileData.AllData(false).ToArray();
        for (int i = 0; i < snapshot.Rows.Count; i++)
        {
            string reference = live[i] is GH_PointCloud pointCloud ? " id=" + pointCloud.ReferenceID + " loaded=" + pointCloud.IsGeometryLoaded :
                live[i] is GH_Extrusion extrusionHolder ? " id=" + extrusionHolder.ReferenceID + " loaded=" + extrusionHolder.IsGeometryLoaded : "";
            Console.WriteLine("STOCK_ROW index=" + i + " type=" + snapshot.Rows[i].TypeName + " validity=" + snapshot.Rows[i].Validity +
                " holder=" + live[i]?.GetType().Name + reference + " reason=" + snapshot.Rows[i].InvalidReason);
        }
        Console.WriteLine("STOCK_STATS invalid=" + fixture.Result.Statistics.InvalidCount + " unverified=" + fixture.Result.Statistics.UnverifiedCount);
        bool initialStatusCorrect = snapshot.Rows.Select(row => row.Validity).SequenceEqual(new[] { PanelValidity.VerifiedValid, PanelValidity.VerifiedInvalid,
            PanelValidity.VerifiedValid, PanelValidity.VerifiedInvalid, PanelValidity.VerifiedValid, PanelValidity.VerifiedInvalid,
            PanelValidity.Unverified, PanelValidity.VerifiedValid, PanelValidity.VerifiedValid }) &&
            live[7] == null && live[8] == null && snapshot.Rows[7].IsNull && snapshot.Rows[8].IsNull &&
            fixture.Result.Statistics.InvalidCount == 3 && fixture.Result.Statistics.UnverifiedCount == 1 && fixture.Result.Statistics.NullCount == 2;
        var setTreeOperations = fixture.Component.GetType().GetMethod("SetTreeOperations", Instance)!;
        foreach (var item in new[] { ("Point Cloud", 1), ("Extrusion", 3), ("Group", 5) })
        foreach (bool set in new[] { false, true })
        {
            fixture.ClearSelection();
            await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 0);
            setTreeOperations.Invoke(fixture.Component, new object[] { 0, false, false, false, set });
            fixture.Query(new PanelQueryOptions { TypeName = item.Item1, OnlyInvalid = true, CreateSet = set });
            await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { item.Item2 }) &&
                (fixture.Result.DisplaySet != null) == set && fixture.Sinks[0].VolatileData.DataCount == 1 &&
                (fixture.Sinks[0].VolatileData.AllData(false).Single() is GH_String) == set);
            var selectedSnapshot = fixture.Result.Snapshot;
            fixture.Select(item.Item2);
            await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 1 && fixture.Sinks[3].VolatileData.DataCount == 1 &&
                (fixture.Sinks[1].VolatileData.AllData(false).Single() is GH_String) == set);
            var data = fixture.Sinks[0].VolatileData.AllData(false).Single(); var selection = fixture.Sinks[1].VolatileData.AllData(false).Single();
            var mask = fixture.Sinks[2].VolatileData.AllData(false).Cast<GH_Boolean>().Select(value => value.Value).ToArray();
            Console.WriteLine("STOCK_SOLVE type=" + item.Item1 + " set=" + set + " sameSnapshot=" + ReferenceEquals(selectedSnapshot, fixture.Result.Snapshot) +
                " data=" + data.GetType().Name + " selected=" + selection.GetType().Name + " mask=" + string.Join(",", mask) +
                " paired=" + ((GH_Integer)fixture.Sinks[3].VolatileData.AllData(false).Single()).Value +
                " invalidSources=" + fixture.Result.Statistics.FilteredSourceStatuses.VerifiedInvalid);
            Check(ReferenceEquals(selectedSnapshot, fixture.Result.Snapshot) && fixture.Result.Statistics.FilteredSourceStatuses.VerifiedInvalid == 1 &&
                (set ? data is GH_String && selection is GH_String : data.GetType() == inputs[item.Item2].GetType() && selection.GetType() == inputs[item.Item2].GetType()) &&
                mask.Count(value => value) == 1 && mask[item.Item2] && ((GH_Integer)fixture.Sinks[3].VolatileData.AllData(false).Single()).Value == item.Item2,
                "real solve keeps D/S/B/F and invalid source statistics for " + item.Item1 + " with set=" + set);
        }
        Check(initialStatusCorrect, "real solve preserves source statuses and reflects unresolved inputs normalized to null by Grasshopper");
    }

    private static void VerifyNativeUnresolvedSolve(Assembly assembly)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        var cloud = new GH_PointCloud(Guid.NewGuid()); var extrusion = new GH_Extrusion(Guid.NewGuid());
        var access = new NestedCaptureDataAccess();
        access.Primary.AppendRange(new IGH_Goo[] { cloud, extrusion, Group("unresolved", cloud, extrusion) }, new GH_Path(0));
        access.Paired.AppendRange(new IGH_Goo[] { new GH_Integer(0), new GH_Integer(1), new GH_Integer(2) }, new GH_Path(0));
        fixture.Component.GetType().GetMethod("SolveInstance", Instance)!.Invoke(fixture.Component, new object[] { access });
        var snapshot = (PanelSnapshot)fixture.Session.GetType().GetProperty("Snapshot")!.GetValue(fixture.Session)!;
        Check(snapshot.Rows.Count == 3 && snapshot.Rows.All(row => row.Validity == PanelValidity.Unverified) &&
            snapshot.Rows.Select(row => row.TypeName).SequenceEqual(new[] { "Point Cloud", "Extrusion", "Group" }) &&
            ReferenceEquals(access.Primary.Branches[0][0], cloud) && ReferenceEquals(access.Primary.Branches[0][1], extrusion),
            "controlled real component solve retains unverified status for exact unresolved standalone and grouped holders");
    }

    private static async Task VerifyNativeSolveReplacement(Assembly assembly, PointCloud cloud, Extrusion extrusion, bool isExtrusion, bool grouped)
    {
        using var fixture = new OutputChecks.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        IGH_GeometricGoo holder = isExtrusion ? new GH_Extrusion() : new GH_PointCloud();
        IGH_Goo input = grouped ? Group("replacement", holder) : holder;
        fixture.Source.PersistentData.Append(input, new GH_Path(0));
        fixture.Source.PersistentData.Append(new GH_Number(7), new GH_Path(0));
        fixture.Paired.PersistentData.Append(new GH_Integer(0), new GH_Path(0));
        fixture.Paired.PersistentData.Append(new GH_Integer(1), new GH_Path(0));
        fixture.Query(new PanelQueryOptions { OnlyInvalid = true });
        var hook = fixture.Component.GetType().GetProperty("CaptureObserverForTesting", Instance)!;
        bool changed = false;
        void Replace(IGH_Goo source)
        {
            var target = grouped ? ((GH_GeometryGroup)source).Objects[0] : source;
            if (isExtrusion) ((GH_Extrusion)target).Value = extrusion; else ((GH_PointCloud)target).Value = cloud;
        }
        hook.SetValue(fixture.Component, (Action<int, IGH_Goo?>)((position, _) =>
        {
            if (position != 1) return;
            hook.SetValue(fixture.Component, null); changed = true;
            Replace(fixture.Component.Params.Input[0].VolatileData.AllData(false).First());
            Replace(fixture.Source.PersistentData.AllData(false).First());
        }));
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.Snapshot.Rows.Count == 2 && fixture.Result.RowPositions.Count == 0 &&
            fixture.Sinks[0].VolatileData.DataCount == 0);
        var snapshot = fixture.Result.Snapshot;
        Check(changed && snapshot.Rows[0].Validity == PanelValidity.VerifiedValid && fixture.Result.Statistics.InvalidCount == 0 &&
            fixture.Sinks[2].VolatileData.AllData(false).Cast<GH_Boolean>().All(flag => !flag.Value),
            "real solve discards a stale invalid " + (grouped ? "grouped " : "standalone ") +
            (isExtrusion ? "extrusion" : "cloud") + " after its inner object is replaced");
        fixture.Query(new PanelQueryOptions { TypeName = grouped ? "Group" : isExtrusion ? "Extrusion" : "Point Cloud" });
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 0 }) && fixture.Sinks[0].VolatileData.DataCount == 1);
        fixture.Select(0); await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 1 && fixture.Sinks[3].VolatileData.DataCount == 1);
        var mask = fixture.Sinks[2].VolatileData.AllData(false).Cast<GH_Boolean>().Select(flag => flag.Value).ToArray();
        Check(ReferenceEquals(snapshot, fixture.Result.Snapshot) && fixture.Sinks[0].VolatileData.AllData(false).Single().GetType() == input.GetType() &&
            fixture.Sinks[1].VolatileData.AllData(false).Single().GetType() == input.GetType() && mask.SequenceEqual(new[] { true, false }) &&
            ((GH_Integer)fixture.Sinks[3].VolatileData.AllData(false).Single()).Value == 0,
            "unchanged replacement snapshot reuses typed D/S/B/F output for " + (grouped ? "grouped " : "standalone ") +
            (isExtrusion ? "extrusion" : "cloud"));
    }

    private static GH_GeometryGroup Group(string name, params IGH_GeometricGoo[] values)
    { var group = new GH_GeometryGroup { Name = name }; foreach (var value in values) group.Objects.Add(value); return group; }
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
    private static int HostileCalls;
    private sealed class CloudSubclass : PointCloud { }
    private sealed class ExtrusionSubclass : Extrusion { }
    private sealed class HostileCloudHolder : GH_PointCloud
    {
        public override bool IsValid { get { HostileCalls++; throw new InvalidOperationException("unexpected cloud validity callback"); } }
        public override PointCloud Value
        { get { HostileCalls++; throw new InvalidOperationException("unexpected cloud value callback"); } set { HostileCalls++; throw new InvalidOperationException("unexpected cloud value callback"); } }
    }
    private sealed class HostileExtrusionHolder : GH_Extrusion
    {
        public override bool IsValid { get { HostileCalls++; throw new InvalidOperationException("unexpected extrusion validity callback"); } }
        public override Extrusion Value
        { get { HostileCalls++; throw new InvalidOperationException("unexpected extrusion value callback"); } set { HostileCalls++; throw new InvalidOperationException("unexpected extrusion value callback"); } }
    }
}
