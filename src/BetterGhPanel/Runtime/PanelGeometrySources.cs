using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // A short-lived inspection for one preview build or action. It never invokes
    // wrapper/group preview, validity, ScriptVariable, or metadata. Leaves hold
    // value types or the one exact inner reference read during this inspection;
    // callers must use them immediately and must not retain the holder.
    internal static class PanelGeometrySources
    {
        private static readonly object EndNode = new object(), NullSlot = new object();
        internal const int MaxNodesPerRoot = 4096;
        internal const int MaxNodesPerOperation = 20000;
        internal const int MaxLeavesPerRoot = 1024;
        internal const int MaxDepth = 64;
        internal const long MaxValueSnapshotWorkBytes = 8L * 1024 * 1024;

        internal sealed class Budget
        {
            internal int Remaining = MaxNodesPerOperation;
            internal int Visits { get; private set; }
            internal bool Limited { get; private set; }
            internal string? Reason { get; private set; }
            private long _valueSnapshotWork;
            internal void MarkUnavailable(string reason) { if (Reason == null) Reason = reason; }
            internal void MarkLimit() { Limited = true; MarkUnavailable("inspection or resource limit"); }
            internal bool TakeValueBytes(long bytes)
            {
                if (bytes < 0 || bytes > MaxValueSnapshotWorkBytes - _valueSnapshotWork) { MarkLimit(); return false; }
                _valueSnapshotWork += bytes;
                return true;
            }
            internal bool Take(ref int rootRemaining)
            {
                if (Remaining <= 0 || rootRemaining <= 0) { Limited = true; return false; }
                Remaining--; rootRemaining--; Visits++;
                return true;
            }
        }

        internal sealed class Inspection : IDisposable
        {
            private readonly Func<long, bool>? _reserve;
            private readonly Action<long>? _release;
            private long _reserved;
            internal Inspection(Func<long, bool>? reserve, Action<long>? release) { _reserve = reserve; _release = release; }
            internal readonly List<object> Leaves = new List<object>();
            // A bounded, action-local witness of wrapper targets, holder roles,
            // ordered group members and nesting. Never retained by a preview entry.
            internal readonly List<object> Graph = new List<object>();
            internal bool Unavailable;
            internal bool Exhausted;
            internal int Remaining = MaxNodesPerRoot;

            internal bool ReserveValue(long bytes, Budget budget)
            {
                if (!budget.TakeValueBytes(bytes)) return false;
                if (_reserve != null && !_reserve(bytes)) { budget.MarkLimit(); return false; }
                _reserved += bytes;
                return true;
            }

            public void Dispose()
            {
                long reserved = _reserved; _reserved = 0;
                if (reserved != 0) _release?.Invoke(reserved);
            }
        }

        internal static bool SameGraph(Inspection left, Inspection right)
        {
            return SameGraphCore(left, right, false);
        }

        // For an action, an unchanged skipped prefix is still meaningful: it
        // remains skipped. A truncated budget cannot establish that witness.
        internal static bool SameActionGraph(Inspection left, Inspection right) => SameGraphCore(left, right, true);

        private static bool SameGraphCore(Inspection left, Inspection right, bool allowUnavailable)
        {
            if (left.Exhausted || right.Exhausted || left.Unavailable != right.Unavailable ||
                (left.Unavailable && !allowUnavailable) || left.Graph.Count != right.Graph.Count || left.Leaves.Count != right.Leaves.Count) return false;
            for (int i = 0; i < left.Graph.Count; i++)
            {
                object a = left.Graph[i], b = right.Graph[i];
                if (a is PolylineSnapshot x && b is PolylineSnapshot y)
                { if (!x.SameValues(y)) return false; }
                else if (a.GetType().IsValueType ? !a.Equals(b) : !ReferenceEquals(a, b)) return false;
            }
            for (int i = 0; i < left.Leaves.Count; i++)
            {
                object a = left.Leaves[i], b = right.Leaves[i];
                if (a is StandalonePreview x && b is StandalonePreview y)
                { if (!ReferenceEquals(x.Value, y.Value)) return false; }
                else if (a is PolylineSnapshot p && b is PolylineSnapshot q)
                { if (!p.SameValues(q)) return false; }
                else if (a is GeometryBase || b is GeometryBase)
                { if (!ReferenceEquals(a, b)) return false; }
                else if (!a.Equals(b)) return false;
            }
            return true;
        }

        internal static Inspection Inspect(IGH_Goo? root, Budget budget, bool allowStandalonePreview,
            Func<long, bool>? reserveValue = null, Action<long>? releaseValue = null)
        {
            var result = new Inspection(reserveValue, releaseValue);
            if (root == null) return result;
            var stack = new Stack<Frame>();
            var active = new HashSet<object>(ReferenceComparer.Instance);
            int rootRemaining = MaxNodesPerRoot;
            stack.Push(new Frame(root, 0));
            try
            {
                while (stack.Count > 0)
                {
                    var frame = stack.Peek();
                    if (!frame.Entered)
                    {
                        frame.Entered = true;
                        if (frame.Depth >= MaxDepth || !budget.Take(ref rootRemaining))
                        { result.Unavailable = result.Exhausted = true; break; }
                        if (!active.Add(frame.Node)) { budget.MarkUnavailable("cyclic group or wrapper"); result.Unavailable = true; break; }
                        result.Graph.Add(frame.Node);
                        Type type = frame.Node.GetType();
                        if (TryReferenceState(frame.Node, out var reference)) result.Graph.Add(reference);
                        if (type == typeof(GH_ObjectWrapper))
                        {
                            object? child = ((GH_ObjectWrapper)frame.Node).Value;
                            if (child == null) { result.Unavailable = true; break; }
                            stack.Push(new Frame(child, frame.Depth + 1));
                            continue;
                        }
                        if (type == typeof(GH_GeometricGooWrapper))
                        {
                            object? child = ((GH_GeometricGooWrapper)frame.Node).InternalGoo;
                            if (child == null) { result.Unavailable = true; break; }
                            stack.Push(new Frame(child, frame.Depth + 1));
                            continue;
                        }
                        if (type != typeof(GH_GeometryGroup))
                        {
                            object? leaf = CaptureLeaf(frame.Node, allowStandalonePreview && frame.Depth == 0, frame.Depth > 0, result, budget,
                                out bool unavailable, out object? capturedValue);
                            if (capturedValue != null) result.Graph.Add(capturedValue);
                            if (unavailable) { budget.MarkUnavailable("unsupported geometry: " + type.Name); result.Unavailable = true; result.Exhausted |= budget.Limited; break; }
                            if (leaf != null)
                            {
                                if (result.Leaves.Count >= MaxLeavesPerRoot)
                                { result.Unavailable = result.Exhausted = true; break; }
                                result.Leaves.Add(leaf);
                            }
                        }
                    }
                    if (frame.Node.GetType() == typeof(GH_GeometryGroup))
                    {
                        var children = ((GH_GeometryGroup)frame.Node).Objects;
                        if (frame.Next < children.Count)
                        {
                            if (!budget.Take(ref rootRemaining))
                            { result.Unavailable = result.Exhausted = true; break; }
                            var child = children[frame.Next++];
                            if (child != null) stack.Push(new Frame(child, frame.Depth + 1));
                            else result.Graph.Add(NullSlot);
                            continue;
                        }
                    }
                    active.Remove(frame.Node);
                    result.Graph.Add(EndNode);
                    stack.Pop();
                }
            }
            catch (Exception) { result.Unavailable = true; }
            result.Remaining = rootRemaining;
            // A partially inspected group is never dispatched. This also makes a
            // cycle or unsupported member visibly skip the group as one row.
            if (result.Unavailable) result.Leaves.Clear();
            return result;
        }

        private static object? CaptureLeaf(object node, bool allowStandalonePreview, bool nested, Inspection inspection, Budget budget,
            out bool unavailable, out object? capturedValue)
        {
            unavailable = false;
            capturedValue = null;
            Type type = node.GetType();
            object? value;
            if (type == typeof(GH_Point)) value = ((GH_Point)node).Value;
            else if (type == typeof(GH_Line)) value = ((GH_Line)node).Value;
            else if (type == typeof(GH_Arc)) value = ((GH_Arc)node).Value;
            else if (type == typeof(GH_Circle)) value = ((GH_Circle)node).Value;
            else if (type == typeof(GH_Rectangle)) value = ((GH_Rectangle)node).Value;
            else if (type == typeof(GH_Box)) value = ((GH_Box)node).Value;
            else if (type == typeof(GH_SubD))
            {
                var subd = ((GH_SubD)node).Value;
                if (subd != null && subd.GetType() != typeof(SubD)) { capturedValue = subd; unavailable = true; return null; }
                value = subd;
            }
            else if (type == typeof(GH_Extrusion))
            {
                var extrusion = ((GH_Extrusion)node).Value;
                if (extrusion != null && extrusion.GetType() != typeof(Extrusion)) { capturedValue = extrusion; unavailable = true; return null; }
                value = extrusion;
            }
            else if (type == typeof(GH_PointCloud))
            {
                var cloud = ((GH_PointCloud)node).Value;
                if (cloud != null && cloud.GetType() != typeof(PointCloud)) { capturedValue = cloud; unavailable = true; return null; }
                value = cloud;
            }
            else if (node is IGH_Goo goo && SafeGooInspection.IsGeometryHolder(type))
            {
                if (!SafeGooInspection.TryGetTrustedGeometry(goo, out var geometry))
                { capturedValue = geometry ?? NullSlot; unavailable = true; return null; }
                value = geometry;
            }
            else if (node is GeometryBase geometry)
            {
                if (!SafeGooInspection.IsTrustedGeometry(geometry) && type != typeof(PointCloud) && type != typeof(Extrusion))
                { capturedValue = geometry; unavailable = true; return null; }
                value = geometry;
            }
            else if (type == typeof(BoundingBox)) value = new Box((BoundingBox)node);
            else if (type == typeof(Polyline))
            {
                if (!TryCapturePolyline((Polyline)node, inspection, budget, out var polyline)) { unavailable = true; return null; }
                value = polyline;
            }
            else if (node is Point3d || node is Line || node is Arc || node is Circle || node is Rectangle3d || node is Box || node is Sphere)
                value = node;
            else if (node is GH_ObjectWrapper || node is GH_GeometricGooWrapper || node is GH_GeometryGroup)
            {
                // Subclasses of recursive SDK containers are not independent
                // preview providers, even if they implement the preview interface.
                unavailable = true;
                return null;
            }
            else if (allowStandalonePreview && type.Assembly != typeof(GH_Point).Assembly && node is IGH_PreviewData custom &&
                (node is GH_Point || HasIndependentPreview(type)))
                return new StandalonePreview(custom);
            else
            {
                // Known non-geometry is not a preview failure. An unknown value,
                // including an SDK geometry type without a safe provider, is.
                unavailable = node is IGH_GeometricGoo || node is BoundingBox || node is Sphere || node is Polyline ||
                    SafeGooInspection.TrustedTypeName(type) == null;
                return null;
            }
            if (value is PointCloud cloudValue)
            {
                if (Rhino.Runtime.CommonObject.PerformCorruptionTesting)
                {
                    budget.MarkUnavailable("Rhino corruption testing is enabled");
                    capturedValue = cloudValue; unavailable = true; return null;
                }
                int count = cloudValue.Count;
                // A callback can set hidden flags while duplicating the same
                // native object. This small state witness lets reinspection
                // reject the stale generation before attachment/publication.
                inspection.Graph.Add(new CloudState(count,
                    count <= PanelNativeGeometry.MaxPointCloudPoints && cloudValue.ContainsHiddenFlags));
            }
            capturedValue = value ?? NullSlot;
            if (value == null) { unavailable = nested; return null; }
            // GeometryBase validity can traverse native topology or nested
            // payloads. Keep only the captured reference here; callers first
            // apply their own operation budget and structural preflight.
            bool valid = value is GeometryBase || value is Point3d p && p.IsValid || value is Line l && l.IsValid ||
                value is Arc a && a.IsValid || value is Circle c && c.IsValid || value is Rectangle3d r && r.IsValid ||
                value is Box b && b.IsValid || value is Sphere sphere && sphere.IsValid || value is PolylineSnapshot;
            if (!valid && nested) unavailable = true;
            return valid ? value : null;
        }

        private static bool TryCapturePolyline(Polyline source, Inspection inspection, Budget budget, out PolylineSnapshot? snapshot)
        {
            snapshot = null;
            int count = source.Count;
            if (count < 2) return false;
            if (count > PanelNativeGeometry.MaxLegacyCurvePoints) { budget.MarkLimit(); return false; }
            long bytes = 128L + count * 24L;
            if (!inspection.ReserveValue(bytes, budget)) return false;
            var points = new Point3d[count];
            for (int i = 0; i < count; i++)
            {
                points[i] = source[i];
                if (!points[i].IsValid || (i > 0 && points[i] == points[i - 1])) return false;
            }
            if (source.Count != count || (count == 3 && points[0] == points[count - 1])) return false;
            snapshot = new PolylineSnapshot(points);
            return true;
        }

        internal sealed class PolylineSnapshot
        {
            internal PolylineSnapshot(Point3d[] points) { Points = points; }
            internal Point3d[] Points { get; }
            internal bool SameValues(PolylineSnapshot other)
            {
                if (Points.Length != other.Points.Length) return false;
                for (int i = 0; i < Points.Length; i++) if (Points[i] != other.Points[i]) return false;
                return true;
            }
        }

        private readonly struct CloudState : IEquatable<CloudState>
        {
            internal CloudState(int count, bool hasHiddenFlags) { Count = count; HasHiddenFlags = hasHiddenFlags; }
            private int Count { get; }
            private bool HasHiddenFlags { get; }
            public bool Equals(CloudState other) => Count == other.Count && HasHiddenFlags == other.HasHiddenFlags;
            public override bool Equals(object? other) => other is CloudState state && Equals(state);
            public override int GetHashCode() => unchecked(Count * 397 ^ HasHiddenFlags.GetHashCode());
        }

        private static bool HasIndependentPreview(Type type)
        {
            var map = type.GetInterfaceMap(typeof(IGH_PreviewData));
            foreach (var method in map.TargetMethods)
                if (method.DeclaringType?.Assembly != typeof(GH_Point).Assembly) return true;
            return false;
        }

        private static bool IsKnownGeometryGoo(Type type) => type == typeof(GH_Point) || type == typeof(GH_Line) ||
            type == typeof(GH_Arc) || type == typeof(GH_Circle) || type == typeof(GH_Rectangle) || type == typeof(GH_Box) ||
            type == typeof(GH_Curve) || type == typeof(GH_Surface) || type == typeof(GH_Brep) || type == typeof(GH_Mesh) || type == typeof(GH_SubD) ||
            type == typeof(GH_Extrusion) || type == typeof(GH_PointCloud);

        internal static bool TryReferenceState(object node, out ReferenceState reference)
        {
            reference = default;
            Type type = node.GetType();
            if (!IsKnownGeometryGoo(type) || !(node is IGH_GeometricGoo known)) return false;
            int component = type == typeof(GH_Curve) ? ((GH_Curve)node).ReferenceEdge :
                type == typeof(GH_Point) ? ((GH_Point)node).ReferenceIndex() : -1;
            var point = type == typeof(GH_Point) ? (GH_Point)node : null;
            Guid id = known.ReferenceID;
            uint documentSerial = 0, objectSerial = 0;
            if (id != Guid.Empty)
            {
                var document = Rhino.RhinoDoc.ActiveDoc;
                documentSerial = document?.RuntimeSerialNumber ?? 0;
                objectSerial = document?.Objects.FindId(id)?.RuntimeSerialNumber ?? 0;
            }
            reference = new ReferenceState(id, known.IsReferencedGeometry, known.IsGeometryLoaded, component,
                point == null ? -1 : (int)point.ReferenceType(),
                point == null ? 0 : BitConverter.DoubleToInt64Bits(point.ReferenceParam(0)),
                point == null ? 0 : BitConverter.DoubleToInt64Bits(point.ReferenceParam(1)), documentSerial, objectSerial);
            return true;
        }

        internal readonly struct ReferenceState : IEquatable<ReferenceState>
        {
            internal ReferenceState(Guid id, bool referenced, bool loaded, int component, int pointType, long u, long v,
                uint documentSerial, uint objectSerial)
            { Id = id; Referenced = referenced; Loaded = loaded; Component = component; PointType = pointType; U = u; V = v;
                DocumentSerial = documentSerial; ObjectSerial = objectSerial; }
            private Guid Id { get; }
            private bool Referenced { get; }
            private bool Loaded { get; }
            private int Component { get; }
            private int PointType { get; }
            private long U { get; }
            private long V { get; }
            private uint DocumentSerial { get; }
            private uint ObjectSerial { get; }
            public bool Equals(ReferenceState other) => Id == other.Id && Referenced == other.Referenced && Loaded == other.Loaded &&
                Component == other.Component && PointType == other.PointType && U == other.U && V == other.V &&
                DocumentSerial == other.DocumentSerial && ObjectSerial == other.ObjectSerial;
            public override bool Equals(object? obj) => obj is ReferenceState other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Id.GetHashCode();
                    hash = hash * 397 ^ Referenced.GetHashCode(); hash = hash * 397 ^ Loaded.GetHashCode();
                    hash = hash * 397 ^ Component; hash = hash * 397 ^ PointType;
                    hash = hash * 397 ^ U.GetHashCode(); hash = hash * 397 ^ V.GetHashCode();
                    hash = hash * 397 ^ DocumentSerial.GetHashCode(); return hash * 397 ^ ObjectSerial.GetHashCode();
                }
            }
        }

        internal sealed class StandalonePreview
        {
            internal StandalonePreview(IGH_PreviewData value) { Value = value; }
            internal IGH_PreviewData Value { get; }
        }

        private sealed class Frame
        {
            internal Frame(object node, int depth) { Node = node; Depth = depth; }
            internal readonly object Node;
            internal readonly int Depth;
            internal bool Entered;
            internal int Next;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
