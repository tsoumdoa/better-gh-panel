using static PanelTestAdapter;
using static SmokeAssertions;
using System;
using System.Linq;
using System.Reflection;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;


internal static partial class PreviewSafetyChecks
{
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
        typeof(GH_GeometricGooWrapper).GetField("m_goo", AnyInstance)!.SetValue(geometricSelf, geometricSelf);
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
            Check(Count(highlight) == 1024 && Status(highlight).Contains("partial") && PropertyValue<long>(highlight, "RetainedBytes") <= 64L * 1024 * 1024,
                "filtered preview enforces bounded admission and reports partial results");
            for (int i = 1024; i < total; i++)
            {
                Scope(highlight, values, result, new[] { i });
                Check(Box(highlight).Contains(new Point3d(i, 0, 0)) && PropertyValue<int>(highlight, "LastVisitedRows") <= 1 &&
                    PropertyValue<int>(highlight, "LastVisitedNodes") <= 1 && PropertyValue<long>(highlight, "RetainedBytes") <= 64L * 1024 * 1024,
                    $"successive sparse selections evict and recover without walking filtered rows " +
                    $"(position={i}, labels={labels}, bounds={Box(highlight)}, rows={PropertyValue<int>(highlight, "LastVisitedRows")}, " +
                    $"nodes={PropertyValue<int>(highlight, "LastVisitedNodes")}, bytes={PropertyValue<long>(highlight, "RetainedBytes")}, status={Status(highlight)})");
            }
        }
        var huge = new GH_GeometryGroup { Name = "many" };
        for (int i = 0; i < 5000; i++) huge.Objects.Add(new GH_Point(new Point3d(i, 0, 0)));
        Set(highlight, new IGH_Goo?[] { huge }, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && PropertyValue<int>(highlight, "LastVisitedNodes") <= 4096 && Status(highlight).Contains("partial"),
            "a group exceeding the per-root budget is unavailable rather than partially dispatched");
        var shared = new GH_GeometryGroup();
        for (int i = 0; i < 100; i++) shared.Objects.Add(new GH_Point(new Point3d(i, 0, 0)));
        var repeated = Enumerable.Repeat<IGH_Goo?>(shared, 1000).ToArray();
        Set(highlight, repeated, Enumerable.Range(0, repeated.Length).ToArray(), Array.Empty<int>());
        Check(PropertyValue<int>(highlight, "LastVisitedNodes") <= 20000 && PropertyValue<long>(highlight, "RetainedBytes") <= 64L * 1024 * 1024 &&
            Status(highlight).Contains("partial"), "repeated groups share an aggregate visit and retained-byte limit");
        using var beyondNarrow = new PolylineCurve(Enumerable.Range(0, 2049).Select(i => new Point3d(i, i % 2, 0)));
        Set(highlight, new IGH_Goo?[] { new GH_Curve { Value = beyondNarrow } }, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 1 && Box(highlight).Max.X == 2048 && PropertyValue<long>(highlight, "RetainedBytes") <= 64L * 1024 * 1024,
            "a 2049-point polyline outside the narrow reconstruction uses the bounded compatibility provider");
        using var oversized = new PolylineCurve(Enumerable.Range(0, 32769).Select(i => new Point3d(i, i % 2, 0)));
        Set(highlight, new IGH_Goo?[] { new GH_Curve { Value = oversized } }, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && PropertyValue<long>(highlight, "RetainedBytes") == 0 && Status(highlight).Contains("partial"),
            "geometry beyond the compatibility provider limit is rejected before constructing a preview copy");
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
    }

    private static void VerifyStagedAdmission(object highlight)
    {
        using var brep = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
        var holder = new GH_Brep { Value = brep };
        var outer = new GH_GeometryGroup(); var nested = new GH_GeometryGroup();
        for (int i = 0; i < 5; i++) { outer.Objects.Add(holder); nested.Objects.Add(holder); }
        object? stagedLeaf = null; GeometryBase? stagedGeometry = null;
        bool aliveDuringCallback = false, cappedDuringCallback = false, nestedRejected = false;
        var observerField = highlight.GetType().GetField("_admissionObserver", AnyInstance)!;
        observerField.SetValue(highlight, (Action<int, object>)((index, leaf) =>
        {
            if (index != 0) return;
            observerField.SetValue(highlight, null);
            stagedLeaf = leaf; stagedGeometry = FieldValue<GeometryBase?>(leaf, "_owned");
            long reserved = PropertyValue<long>(highlight, "RetainedBytes");
            // Fill remaining capacity with a simulated staging reservation; source-only
            // admission no longer includes the previous worst-case display allowance.
            var stagingField = highlight.GetType().GetField("_stagedBytes", AnyInstance)!;
            long added = 64L * 1024 * 1024 - reserved;
            stagingField.SetValue(highlight, FieldValue<long>(highlight, "_stagedBytes") + added);
            try { Set(highlight, new IGH_Goo?[] { nested }, new[] { 0 }, new[] { 0 }); }
            finally { stagingField.SetValue(highlight, FieldValue<long>(highlight, "_stagedBytes") - added); }
            aliveDuringCallback = stagedGeometry != null && !stagedGeometry.Disposed;
            cappedDuringCallback = reserved > 0 && PropertyValue<long>(highlight, "RetainedBytes") <= 64L * 1024 * 1024;
            nestedRejected = Count(highlight) == 0 && Status(highlight).Contains("partial");
        }));
        try
        {
            Set(highlight, new IGH_Goo?[] { outer }, new[] { 0 }, new[] { 0 });
            Check(aliveDuringCallback && cappedDuringCallback && nestedRejected,
                "reentrant admission counts staged geometry and rejects a nested build over the live-memory cap");
            Check(stagedLeaf != null && stagedGeometry != null && stagedGeometry.Disposed && PropertyValue<int>(stagedLeaf, "DisposeCount") == 1 &&
                PropertyValue<long>(highlight, "RetainedBytes") == 0, "the abandoned staged leaf is disposed exactly once after reentry");
            Set(highlight, new IGH_Goo?[] { nested }, new[] { 0 }, new[] { 0 });
            var recovered = OwnedGeometry(highlight).ToArray();
            Check(Count(highlight) == 1 && recovered.Length == 5 && recovered.All(item => !item.Disposed) &&
                PropertyValue<long>(highlight, "RetainedBytes") <= 64L * 1024 * 1024, "a later build recovers after the staged reservation is released");
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            Check(recovered.All(item => item.Disposed) && PropertyValue<long>(highlight, "RetainedBytes") == 0,
                "clearing the recovered generation releases every owned geometry");
        }
        finally { observerField.SetValue(highlight, null); Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>()); }
    }

}
