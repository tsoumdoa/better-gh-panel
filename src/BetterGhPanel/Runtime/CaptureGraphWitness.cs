using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // Ephemeral structural evidence, used only while a capture is on the stack.
    // It never calls a wrapper/group validity, formatting, preview or copy method.
    internal sealed class CaptureGraphWitness
    {
        private readonly List<Node> _nodes = new List<Node>();

        internal static bool TryCapture(IGH_Goo? root, ref int captureRemaining, out CaptureGraphWitness? witness)
        {
            witness = null;
            if (root == null) return true;
            Type rootType = root.GetType();
            // These common scalar rows already retain immutable values in PanelRow;
            // do not allocate a witness object per number or text item.
            if (rootType == typeof(GH_Number) || rootType == typeof(GH_Integer) || rootType == typeof(GH_String) || rootType == typeof(GH_Boolean)) return true;
            if (rootType != typeof(GH_ObjectWrapper) && rootType != typeof(GH_GeometricGooWrapper) && rootType != typeof(GH_GeometryGroup) &&
                !IsManagedValueType(rootType)) return true;
            var result = new CaptureGraphWitness();
            var pending = new Stack<(object? value, int depth)>();
            var seen = new HashSet<object>(ReferenceComparer.Instance);
            int rootRemaining = SafeGooInspection.MaxNodesPerRoot;
            pending.Push((root, 0));
            try
            {
                while (pending.Count != 0)
                {
                    var (value, depth) = pending.Pop();
                    if (rootRemaining <= 0 || captureRemaining <= 0 || depth >= SafeGooInspection.MaxDepth) return false;
                    rootRemaining--; captureRemaining--;
                    if (value == null || !seen.Add(value)) continue;
                    Type type = value.GetType();
                    if (type == typeof(GH_ObjectWrapper) || type == typeof(GH_GeometricGooWrapper))
                    {
                        object? target = type == typeof(GH_ObjectWrapper) ? ((GH_ObjectWrapper)value).Value : ((GH_GeometricGooWrapper)value).InternalGoo;
                        result._nodes.Add(new Node(value, target));
                        pending.Push((target, depth + 1));
                    }
                    else if (type == typeof(GH_GeometryGroup))
                    {
                        var group = (GH_GeometryGroup)value;
                        string name = group.Name ?? string.Empty;
                        var members = group.Objects;
                        int count = members.Count;
                        // Pending siblings also consume visits. Reserve their budget
                        // before allocating this group's temporary member array.
                        if (count > rootRemaining - pending.Count || count > captureRemaining - pending.Count) return false;
                        var children = new object?[count];
                        for (int i = 0; i < count; i++) children[i] = members[i];
                        result._nodes.Add(new Node(value, name, children));
                        for (int i = count - 1; i >= 0; i--) pending.Push((children[i], depth + 1));
                    }
                    else if (value is IGH_Goo goo && SafeGooInspection.IsGeometryHolder(type))
                    {
                        bool trusted = SafeGooInspection.TryGetTrustedGeometry(goo, out GeometryBase? geometry);
                        bool hasReference = SafeGooInspection.TryGetMeasurementReference(goo, out var reference, false);
                        result._nodes.Add(new Node(value, trusted, geometry, hasReference, reference));
                    }
                    else if (TryCopyManagedValue(value, out var copy))
                        result._nodes.Add(new Node(value, SafeGooInspection.KnownManagedValidity(copy!), copy!));
                }
                witness = result;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal bool IsCurrent()
        {
            try
            {
                foreach (var node in _nodes) if (!node.IsCurrent()) return false;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal Dictionary<object, Node>? OpenInspection()
        {
            var nodes = new Dictionary<object, Node>(ReferenceComparer.Instance);
            foreach (var node in _nodes)
            {
                if (!node.TryValue(out var value)) return null;
                nodes[value] = node;
            }
            return nodes;
        }

        internal IGH_Goo? ManagedCopy(object original)
        {
            foreach (var node in _nodes)
                if (node.TryValue(out var value) && ReferenceEquals(value, original)) return node.ManagedCopy;
            return null;
        }

        internal bool TryGroupSummary(GH_GeometryGroup group, out string name, out int count)
        {
            foreach (var node in _nodes)
                if (node.TryValue(out var value) && ReferenceEquals(value, group)) return node.TryGroupSummary(out name, out count);
            name = ""; count = 0; return false;
        }

        internal sealed class Node
        {
            private readonly WeakReference<object> _value;
            private readonly WeakReference<object>? _target;
            private readonly bool _targetNull;
            private readonly bool _hasManagedValue;
            private readonly BetterGhPanel.Core.PanelValidity _managedValidity;
            private readonly IGH_Goo? _managedCopy;
            private readonly string? _name;
            private readonly WeakReference<object>?[]? _children;
            private readonly bool _trusted, _hasReference;
            private readonly MeasurementReferenceState _reference;
            internal Node(object value, object? target)
            { _value = new WeakReference<object>(value); _targetNull = target == null; if (target != null) _target = new WeakReference<object>(target); }
            internal Node(object value, BetterGhPanel.Core.PanelValidity validity, IGH_Goo copy)
            { _value = new WeakReference<object>(value); _hasManagedValue = true; _managedValidity = validity; _managedCopy = copy; }
            internal Node(object value, string name, object?[] children)
            {
                _value = new WeakReference<object>(value); _name = name;
                _children = new WeakReference<object>?[children.Length];
                for (int i = 0; i < children.Length; i++) if (children[i] != null) _children[i] = new WeakReference<object>(children[i]!);
            }
            internal Node(object value, bool trusted, GeometryBase? geometry, bool hasReference, MeasurementReferenceState reference)
            {
                _value = new WeakReference<object>(value); _trusted = trusted; _targetNull = geometry == null;
                if (geometry != null) _target = new WeakReference<object>(geometry);
                _hasReference = hasReference; _reference = reference;
            }
            internal bool IsCurrent()
            {
                if (!_value.TryGetTarget(out var value)) return false;
                if (_hasManagedValue) return SameManagedValue(value, _managedCopy!);
                Type type = value.GetType();
                if (type == typeof(GH_ObjectWrapper)) return TargetIs(((GH_ObjectWrapper)value).Value);
                if (type == typeof(GH_GeometricGooWrapper)) return TargetIs(((GH_GeometricGooWrapper)value).InternalGoo);
                if (type == typeof(GH_GeometryGroup))
                {
                    var group = (GH_GeometryGroup)value;
                    var members = group.Objects;
                    if (!string.Equals(_name, group.Name ?? string.Empty, StringComparison.Ordinal) || members.Count != _children!.Length) return false;
                    for (int i = 0; i < _children.Length; i++)
                    {
                        if (_children[i] == null) { if (members[i] != null) return false; }
                        else if (!_children[i]!.TryGetTarget(out var child) || !ReferenceEquals(child, members[i])) return false;
                    }
                    return true;
                }
                var goo = (IGH_Goo)value;
                bool trusted = SafeGooInspection.TryGetTrustedGeometry(goo, out GeometryBase? geometry);
                bool hasReference = SafeGooInspection.TryGetMeasurementReference(goo, out var reference, false);
                return trusted == _trusted && TargetIs(geometry) && hasReference == _hasReference &&
                    (!hasReference || reference.Equals(_reference));
            }
            private bool TargetIs(object? value) => _targetNull ? value == null :
                _target != null && _target.TryGetTarget(out var target) && ReferenceEquals(target, value);
            internal bool TryValue(out object value) => _value.TryGetTarget(out value!);
            internal bool TryTarget(out object? target)
            {
                target = null;
                return _targetNull || (_target != null && _target.TryGetTarget(out target));
            }
            internal bool TryChildren(out object?[] children)
            {
                children = new object?[_children!.Length];
                for (int i = 0; i < children.Length; i++)
                    if (_children[i] != null && !_children[i]!.TryGetTarget(out children[i])) return false;
                return true;
            }
            internal bool HasManagedValue => _hasManagedValue;
            internal BetterGhPanel.Core.PanelValidity ManagedValidity => _managedValidity;
            internal IGH_Goo? ManagedCopy => _managedCopy;
            internal bool TryGroupSummary(out string name, out int count)
            { name = _name ?? ""; count = _children?.Length ?? 0; return _children != null; }
            internal bool Trusted => _trusted;
            internal bool HasReference => _hasReference;
            internal MeasurementReferenceState Reference => _reference;
        }

        private static bool TryCopyManagedValue(object value, out IGH_Goo? copy)
        {
            Type type = value.GetType();
            if (type == typeof(GH_Number)) copy = new GH_Number(((GH_Number)value).Value);
            else if (type == typeof(GH_Integer)) copy = new GH_Integer(((GH_Integer)value).Value);
            else if (type == typeof(GH_String)) copy = new GH_String(((GH_String)value).Value);
            else if (type == typeof(GH_Boolean)) copy = new GH_Boolean(((GH_Boolean)value).Value);
            else if (type == typeof(GH_Point)) copy = new GH_Point(((GH_Point)value).Value);
            else if (type == typeof(GH_Vector)) copy = new GH_Vector(((GH_Vector)value).Value);
            else if (type == typeof(GH_Colour)) copy = new GH_Colour(((GH_Colour)value).Value);
            else if (type == typeof(GH_Interval)) copy = new GH_Interval(((GH_Interval)value).Value);
            else if (type == typeof(GH_Interval2D)) copy = new GH_Interval2D(((GH_Interval2D)value).Value);
            else if (type == typeof(GH_Guid)) copy = new GH_Guid(((GH_Guid)value).Value);
            else if (type == typeof(GH_Time)) copy = new GH_Time(((GH_Time)value).Value);
            else if (type == typeof(GH_Plane)) copy = new GH_Plane(((GH_Plane)value).Value);
            else if (type == typeof(GH_Line)) copy = new GH_Line(((GH_Line)value).Value);
            else if (type == typeof(GH_Arc)) copy = new GH_Arc(((GH_Arc)value).Value);
            else if (type == typeof(GH_Circle)) copy = new GH_Circle(((GH_Circle)value).Value);
            else if (type == typeof(GH_Rectangle)) copy = new GH_Rectangle(((GH_Rectangle)value).Value);
            else if (type == typeof(GH_Box)) copy = new GH_Box(((GH_Box)value).Value);
            else { copy = null; return false; }
            return true;
        }

        private static bool IsManagedValueType(Type type) => type == typeof(GH_Number) || type == typeof(GH_Integer) ||
            type == typeof(GH_String) || type == typeof(GH_Boolean) || type == typeof(GH_Point) || type == typeof(GH_Vector) ||
            type == typeof(GH_Colour) || type == typeof(GH_Interval) || type == typeof(GH_Interval2D) || type == typeof(GH_Guid) ||
            type == typeof(GH_Time) || type == typeof(GH_Plane) || type == typeof(GH_Line) || type == typeof(GH_Arc) ||
            type == typeof(GH_Circle) || type == typeof(GH_Rectangle) || type == typeof(GH_Box);

        private static bool SameManagedValue(object value, IGH_Goo copy)
        {
            Type type = value.GetType();
            if (type == typeof(GH_Number)) return Same(((GH_Number)value).Value, ((GH_Number)copy).Value);
            if (type == typeof(GH_Integer)) return ((GH_Integer)value).Value == ((GH_Integer)copy).Value;
            if (type == typeof(GH_String)) return string.Equals(((GH_String)value).Value, ((GH_String)copy).Value, StringComparison.Ordinal);
            if (type == typeof(GH_Boolean)) return ((GH_Boolean)value).Value == ((GH_Boolean)copy).Value;
            if (type == typeof(GH_Point)) return Same(((GH_Point)value).Value, ((GH_Point)copy).Value);
            if (type == typeof(GH_Vector)) return Same(((GH_Vector)value).Value, ((GH_Vector)copy).Value);
            if (type == typeof(GH_Colour)) return ((GH_Colour)value).Value.Equals(((GH_Colour)copy).Value);
            if (type == typeof(GH_Interval)) return Same(((GH_Interval)value).Value, ((GH_Interval)copy).Value);
            if (type == typeof(GH_Interval2D))
            { var a = ((GH_Interval2D)value).Value; var b = ((GH_Interval2D)copy).Value; return Same(a.U, b.U) && Same(a.V, b.V); }
            if (type == typeof(GH_Guid)) return ((GH_Guid)value).Value == ((GH_Guid)copy).Value;
            if (type == typeof(GH_Time)) return ((GH_Time)value).Value.ToBinary() == ((GH_Time)copy).Value.ToBinary();
            if (type == typeof(GH_Plane)) return Same(((GH_Plane)value).Value, ((GH_Plane)copy).Value);
            if (type == typeof(GH_Line))
            { var a = ((GH_Line)value).Value; var b = ((GH_Line)copy).Value; return Same(a.From, b.From) && Same(a.To, b.To); }
            if (type == typeof(GH_Arc))
            { var a = ((GH_Arc)value).Value; var b = ((GH_Arc)copy).Value; return Same(a.Plane, b.Plane) && Same(a.Radius, b.Radius) && Same(a.AngleDomain, b.AngleDomain); }
            if (type == typeof(GH_Circle))
            { var a = ((GH_Circle)value).Value; var b = ((GH_Circle)copy).Value; return Same(a.Plane, b.Plane) && Same(a.Radius, b.Radius); }
            if (type == typeof(GH_Rectangle))
            { var a = ((GH_Rectangle)value).Value; var b = ((GH_Rectangle)copy).Value; return Same(a.Plane, b.Plane) && Same(a.X, b.X) && Same(a.Y, b.Y); }
            if (type == typeof(GH_Box))
            { var a = ((GH_Box)value).Value; var b = ((GH_Box)copy).Value; return Same(a.Plane, b.Plane) && Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z); }
            return false;
        }

        private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
        private static bool Same(Point3d a, Point3d b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
        private static bool Same(Vector3d a, Vector3d b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
        private static bool Same(Interval a, Interval b) => Same(a.T0, b.T0) && Same(a.T1, b.T1);
        private static bool Same(Plane a, Plane b) => Same(a.Origin, b.Origin) && Same(a.XAxis, b.XAxis) && Same(a.YAxis, b.YAxis) && Same(a.ZAxis, b.ZAxis);

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
