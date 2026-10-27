using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BetterGhPanel.Core;
using GH_IO.Serialization;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Types.Transforms;
using Rhino.Geometry;

// Managed-only regressions: no RhinoCore, document, clipboard or viewport is opened.
internal static class CaptureBudgetChecks
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    internal static void Verify(Assembly assembly)
    {
        VerifyManyValues(assembly);
        VerifyIncompleteGroups(assembly);
        VerifyStockValues(assembly);
        VerifyReferenceState(assembly);
    }

    private static object?[] Capture(Assembly assembly, GH_Structure<IGH_Goo> tree, Action<int, IGH_Goo?>? observer = null)
    {
        var method = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("CaptureForMeasurements", Static)!;
        var args = new object?[] { tree, null, null, null, PanelUnitConversion.None, "mm", null, observer, null, null, null };
        var snapshot = (PanelSnapshot)method.Invoke(null, args)!;
        return new object?[] { snapshot, args[1], args[2], args[3] };
    }

    private static bool GraphsCurrent(Assembly assembly, object?[] capture) =>
        (bool)assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("GraphSourcesCurrent", Static)!
            .Invoke(null, new[] { capture[3] })!;

    private static bool HoldersCurrent(Assembly assembly, object?[] capture) =>
        (bool)assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("MeasurementSourcesCurrent", Static)!
            .Invoke(null, new[] { capture[1], capture[2] })!;

    private static void VerifyManyValues(Assembly assembly)
    {
        const int count = 100001;
        var tree = new GH_Structure<IGH_Goo>();
        var branch = tree.EnsurePath(new GH_Path(0));
        for (int i = 0; i < count; i++) branch.Add(new GH_Point(new Point3d(i, 1, 2)));
        branch.Add(new GH_Line(new Line(Point3d.Origin, new Point3d(3, 4, 0))));
        var later = (GH_Point)branch[50000];
        bool changed = false, restored = false;
        Action<int, IGH_Goo?> observer = (position, _) =>
        {
            if (position == 0) { later.Value = new Point3d(999999, 1, 2); changed = true; }
            if (position == count) { later.Value = new Point3d(50000, 1, 2); restored = true; }
        };
        var timer = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var captured = Capture(assembly, tree, observer);
        var snapshot = (PanelSnapshot)captured[0]!;
        Check(changed && restored && snapshot.Rows.Count == count + 1 && snapshot.Rows.Take(count).All(row => row.IsValid && row.TypeName == "Point") &&
            snapshot.Rows[50000].DisplayValue == "(50000, 1, 2)" && snapshot.Rows[count - 1].DisplayValue == "(100000, 1, 2)" &&
            snapshot.Rows[count].IsValid && snapshot.SupportsSearchMode(PanelSearchMode.Length) && snapshot.Rows[count].GeometryInfo?.Value == 5 &&
            ((Array)captured[3]!).Length == 1 && snapshot.InspectionVisitCount <= 100000 && GraphsCurrent(assembly, captured),
            "more than 100k exact points and a following line retain captured values and length capability with one linear witness");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated < 350_000_000, "large managed-value capture uses bounded linear allocation");
        Console.WriteLine("100001 point capture and witness: " + timer.ElapsedMilliseconds + " ms; allocated " + allocated + " bytes on the fixture thread.");

        var query = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = "Point", Search = "100000" });
        Check(query.RowPositions.SequenceEqual(new[] { count - 1 }), "the last point remains searchable after the group budget threshold");
        VerifyOutputs(assembly, tree, snapshot, query, count - 1);
        later.Value = new Point3d(7, 8, 9);
        Check(!GraphsCurrent(assembly, captured), "a later point mutation invalidates the linear witness");
        later.Value = new Point3d(50000, 1, 2);
        Check(GraphsCurrent(assembly, captured), "unchanged same-snapshot point values reuse the witness");
        later.Value = new Point3d(7, 8, 9);
        var recovered = Capture(assembly, tree);
        Check(GraphsCurrent(assembly, recovered) && ((PanelSnapshot)recovered[0]!).Rows[50000].DisplayValue == "(7, 8, 9)",
            "a fresh capture recovers after a point value changes");
    }

    private static void VerifyIncompleteGroups(Assembly assembly)
    {
        GH_GeometryGroup Make(string name)
        {
            var group = new GH_GeometryGroup { Name = name };
            for (int i = 0; i < 4096; i++) group.Objects.Add(null!);
            return group;
        }
        var alpha = Make("Alpha"); var beta = Make("Beta");
        var nested = new GH_GeometricGooWrapper(new GH_GeometricGooWrapper(beta));
        var objectNested = new GH_ObjectWrapper(new GH_GeometricGooWrapper(alpha));
        var tree = new GH_Structure<IGH_Goo>();
        tree.AppendRange(new IGH_Goo[] { alpha, beta, new GH_ObjectWrapper(alpha), new GH_GeometricGooWrapper(beta), nested, objectNested }, new GH_Path(0));
        bool changed = false, restored = false;
        var captured = Capture(assembly, tree, (position, _) =>
        {
            if (position == 0) { beta.Name = "temporary"; changed = true; }
            if (position == 5) { beta.Name = "Beta"; restored = true; }
        });
        var snapshot = (PanelSnapshot)captured[0]!;
        Check(changed && restored && snapshot.Rows.Select(row => row.TypeName).SequenceEqual(new[] { "Group", "Group", "Goo", "{Group}", "{{Group}}", "Goo" }) &&
            snapshot.Rows.All(row => row.TypeIdentityAvailable && row.Validity == PanelValidity.Unverified) &&
            snapshot.Rows[0].DisplayValue == "Group (Alpha) with 4096 objects" &&
            snapshot.Rows[1].DisplayValue == "Group (Beta) with 4096 objects" &&
            snapshot.Rows[2].DisplayValue == snapshot.Rows[0].DisplayValue && snapshot.Rows[3].DisplayValue == snapshot.Rows[1].DisplayValue &&
            snapshot.Rows[4].DisplayValue == snapshot.Rows[1].DisplayValue && snapshot.Rows[5].DisplayValue == snapshot.Rows[0].DisplayValue &&
            snapshot.InspectionVisitCount <= 100000 && GraphsCurrent(assembly, captured),
            "oversized groups retain direct and wrapped identities and witnessed names/counts while validity remains unverified");
        var set = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = "Group", CreateSet = true });
        Check(set.RowPositions.SequenceEqual(new[] { 0, 1 }), "oversized Alpha and Beta remain distinct displayed-set values");
        var wrapped = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = "{Group}", Search = "Beta" });
        Check(wrapped.RowPositions.SequenceEqual(new[] { 3 }), "saved wrapped-group type and name filters survive exhausted traversal");
        VerifyOutputs(assembly, tree, snapshot, wrapped, 3);
        var nestedFilter = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = "{{Group}}", Search = "Beta" });
        Check(nestedFilter.RowPositions.SequenceEqual(new[] { 4 }), "nested geometric wrapper identity remains searchable after the per-root limit");
        VerifyOutputs(assembly, tree, snapshot, nestedFilter, 4);
        var originalTarget = objectNested.Value;
        objectNested.Value = new GH_GeometricGooWrapper(beta);
        Check(!GraphsCurrent(assembly, captured), "retargeting an incomplete nested wrapper invalidates its shallow chain witness");
        objectNested.Value = originalTarget;
        Check(GraphsCurrent(assembly, captured), "restoring the captured nested wrapper chain restores its witness");
        beta.Name = "changed";
        Check(!GraphsCurrent(assembly, captured), "a changed shallow group name invalidates its summary witness");
        beta.Name = "Beta";
        Check(GraphsCurrent(assembly, captured), "unchanged shallow group summaries reuse their witness");
        beta.Objects.Add(null!);
        Check(!GraphsCurrent(assembly, captured), "a changed shallow group count invalidates its summary witness");
        var recovered = Capture(assembly, tree);
        Check(GraphsCurrent(assembly, recovered) && ((PanelSnapshot)recovered[0]!).Rows[1].DisplayValue.Contains("4097 objects"),
            "a fresh capture recovers a changed oversized group summary");

        // Consume the aggregate graph budget first, then resolve a nested chain.
        var aggregate = new GH_Structure<IGH_Goo>();
        var shared = new GH_GeometryGroup { Name = "shared" };
        for (int i = 0; i < 120; i++) shared.Objects.Add(null!);
        for (int i = 0; i < 900; i++) aggregate.Append(shared, new GH_Path(0));
        aggregate.Append(nested, new GH_Path(0));
        var aggregateCapture = Capture(assembly, aggregate);
        var aggregateSnapshot = (PanelSnapshot)aggregateCapture[0]!;
        Check(aggregateSnapshot.InspectionVisitCount <= 100000 && aggregateSnapshot.Rows[900].Validity == PanelValidity.Unverified &&
            aggregateSnapshot.Rows[900].TypeName == "{{Group}}" && aggregateSnapshot.Rows[900].TypeIdentityAvailable &&
            aggregateSnapshot.Rows[900].DisplayValue == "Group (Beta) with 4097 objects" && GraphsCurrent(assembly, aggregateCapture),
            "nested wrapper identity and direct group summary survive exhausted aggregate graph traversal");
    }

    private static void VerifyStockValues(Assembly assembly)
    {
        var invalid = Transform.Identity; invalid.M00 = double.NaN;
        var hostile = new HostileTransform();
        var tree = new GH_Structure<IGH_Goo>();
        tree.AppendRange(new IGH_Goo[] { new GH_Transform(Transform.Identity), new GH_Transform(invalid),
            new GH_MeshFace(0, 1, 2), new GH_MeshFace(), new GH_Transform(hostile) }, new GH_Path(0));
        var captured = Capture(assembly, tree);
        var snapshot = (PanelSnapshot)captured[0]!;
        Check(snapshot.Rows.Select(row => row.Validity).SequenceEqual(new[] { PanelValidity.VerifiedValid, PanelValidity.VerifiedInvalid,
            PanelValidity.VerifiedValid, PanelValidity.VerifiedInvalid, PanelValidity.Unverified }) && hostile.Calls == 0,
            "simple stock transform and mesh-face values retain valid/invalid status without dispatching third-party transforms");
        var invalidResult = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { OnlyInvalid = true });
        Check(invalidResult.RowPositions.SequenceEqual(new[] { 1, 3 }), "invalid-only filtering finds the exact invalid stock values");
        VerifyOutputs(assembly, tree, snapshot, invalidResult, 1);
    }

    private static void VerifyReferenceState(Assembly assembly)
    {
        var point = new GH_Point(new Point3d(1, 2, 3));
        point.ReferenceData = new GH_PointRefData { m_RefID = Guid.NewGuid(), m_RefIndex = 4, m_RefType = GH_PointRefType.point_object };
        var tree = new GH_Structure<IGH_Goo>(); tree.Append(point, new GH_Path(0));
        var captured = Capture(assembly, tree);
        Check(GraphsCurrent(assembly, captured), "unchanged point reference metadata matches its captured value witness");
        var value = point.Value; point.ReferenceID = Guid.NewGuid();
        Check(point.Value == value && !GraphsCurrent(assembly, captured), "retargeting a point reference with identical coordinates invalidates the witness");
        var recovered = Capture(assembly, tree);
        point.ReferenceData.m_RefIndex++;
        Check(!GraphsCurrent(assembly, recovered), "changing a point reference component invalidates the witness");
        var group = new GH_GeometryGroup { Name = "referenced point" }; group.Objects.Add(point);
        var nestedTree = new GH_Structure<IGH_Goo>(); nestedTree.Append(new GH_ObjectWrapper(group), new GH_Path(0));
        var nested = Capture(assembly, nestedTree);
        Check(GraphsCurrent(assembly, nested), "unchanged nested point reference metadata matches its graph witness");
        point.ReferenceData.m_RefParam[1] = 0.75;
        Check(!GraphsCurrent(assembly, nested), "changing a nested point reference parameter invalidates its graph witness");

        var plane = new GH_Plane(Plane.WorldXY) { ReferenceID = Guid.NewGuid() };
        var planeTree = new GH_Structure<IGH_Goo>(); planeTree.Append(plane, new GH_Path(0));
        var planeCapture = Capture(assembly, planeTree);
        var planeValue = plane.Value; plane.ReferenceID = Guid.NewGuid();
        bool planeSame = plane.Value == planeValue, planeCurrent = GraphsCurrent(assembly, planeCapture);
        Check(planeSame && !planeCurrent,
            "retargeting a plane reference without changing its value invalidates its witness");
        var box = new GH_Box { ReferenceID = Guid.NewGuid() };
        var boxTree = new GH_Structure<IGH_Goo>(); boxTree.Append(box, new GH_Path(0));
        var boxCapture = Capture(assembly, boxTree);
        box.ReferenceID = Guid.NewGuid();
        Check(!GraphsCurrent(assembly, boxCapture), "retargeting an invalid box reference invalidates its witness");

        var curve = new GH_Curve();
        var curves = new GH_Structure<IGH_Goo>(); curves.Append(curve, new GH_Path(0));
        var curveCapture = Capture(assembly, curves);
        Check(HoldersCurrent(assembly, curveCapture), "an unchanged null curve holder remains current");
        curve.ReferenceID = Guid.NewGuid();
        Check(curve.Value == null && !HoldersCurrent(assembly, curveCapture), "changing an invalid curve reference with null geometry invalidates capture");
    }

    private static void VerifyOutputs(Assembly assembly, GH_Structure<IGH_Goo> input, PanelSnapshot snapshot, PanelQueryResult query, int selected)
    {
        var treeOps = assembly.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!;
        var outputOps = assembly.GetType("BetterGhPanel.Runtime.PanelOutputOperations", true)!;
        var data = (GH_Structure<IGH_Goo>)treeOps.GetMethod("Filter", Static)!.Invoke(null, new object[] { input, query })!;
        var selection = (GH_Structure<IGH_Goo>)treeOps.GetMethod("Select", Static)!.Invoke(null, new object[] { input, new[] { selected } })!;
        var addresses = Enumerable.Range(0, input.DataCount).Select(i => new PanelSourceAddress("{0}", i)).ToArray();
        var mask = (GH_Structure<GH_Boolean>)outputOps.GetMethod("Mask", Static)!.Invoke(null, new object[] { input, addresses, query.RowPositions })!;
        var paired = new GH_Structure<IGH_Goo>();
        var values = paired.EnsurePath(new GH_Path(0));
        for (int i = 0; i < input.DataCount; i++) values.Add(new GH_Integer(i));
        var pairedArgs = new object?[] { input, paired, input, addresses, null };
        var pair = (GH_Structure<IGH_Goo>)outputOps.GetMethod("Pair", Static)!.Invoke(null, pairedArgs)!;
        var filteredPair = (GH_Structure<IGH_Goo>)treeOps.GetMethod("Filter", Static)!.Invoke(null, new object[] { pair, query })!;
        var position = query.RowPositions.ToList().IndexOf(selected);
        Check(position >= 0 && ReferenceEquals(data.Branches[0][position], input.Branches[0][selected]) &&
            ReferenceEquals(selection.Branches[0][0], input.Branches[0][selected]) && mask.Branches[0][selected].Value &&
            ((GH_Integer)filteredPair.Branches[0][position]).Value == selected,
            "filtered D, selected S, original-shape B and paired F preserve typed source membership");
    }

    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);

    private sealed class HostileTransform : ITransform
    {
        internal int Calls;
        private Exception Called() { Calls++; return new InvalidOperationException("hostile transform invoked"); }
        public string Name => throw Called();
        public Transform ToMatrix() => throw Called();
        public ITransform Reverse() => throw Called();
        public ITransform Duplicate() => throw Called();
        public bool Write(GH_IWriter writer) => throw Called();
        public bool Read(GH_IReader reader) => throw Called();
    }
}
