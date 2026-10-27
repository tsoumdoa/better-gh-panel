using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    internal readonly struct CapturedManagedValue
    {
        internal CapturedManagedValue(IGH_Goo? copy, object? raw, BetterGhPanel.Core.PanelValidity validity, bool validityReady,
            bool hasReference, bool referenceResolved, MeasurementReferenceState reference)
        { Copy = copy; Raw = raw; Validity = validity; ValidityReady = validityReady; HasReference = hasReference; ReferenceResolved = referenceResolved; Reference = reference; }
        internal IGH_Goo? Copy { get; }
        internal object? Raw { get; }
        internal bool Available => Copy != null || Raw != null;
        internal BetterGhPanel.Core.PanelValidity Validity { get; }
        internal bool ValidityReady { get; }
        internal bool HasReference { get; }
        internal bool ReferenceResolved { get; }
        internal MeasurementReferenceState Reference { get; }
    }

    // Structural evidence retained with a snapshot. Group and wrapper references
    // are weak; managed row copies share the snapshot's source-holder array.
    // It never calls a wrapper/group validity, formatting, preview or copy method.
    internal sealed class CaptureGraphWitness
    {
        private readonly List<Node> _nodes = new List<Node>();
        private readonly IGH_Goo?[]? _managedHolders;
        private readonly CapturedManagedValue[]? _managedCopies;

        private CaptureGraphWitness() { }
        private CaptureGraphWitness(IGH_Goo?[] holders, CapturedManagedValue[] copies)
        { _managedHolders = holders; _managedCopies = copies; }

        // Direct managed values already occur once per input row. Keep their
        // immutable copies in one linear witness, outside the group traversal
        // budget, rather than allocating a graph per value.
        internal static CaptureGraphWitness ForManagedValues(IGH_Goo?[] holders, CapturedManagedValue[] copies) =>
            new CaptureGraphWitness(holders, copies);

        internal static bool TryCopyStandaloneValue(IGH_Goo? value, out CapturedManagedValue captured)
        {
            captured = default;
            if (value == null) return false;
            Type type = value.GetType();
            if (type == typeof(GH_Number) || type == typeof(GH_Integer) || type == typeof(GH_String) || type == typeof(GH_Boolean)) return false;
            return TryCaptureManagedValue(value, out captured);
        }

        internal static bool ManagedValueCurrent(IGH_Goo? holder, CapturedManagedValue captured)
        {
            try
            {
                var copy = captured.Copy;
                if (!captured.Available) return true;
                if (captured.Raw is Box box)
                {
                    if (holder == null || holder.GetType() != typeof(GH_Box) || !Same(((GH_Box)holder).Value, box)) return false;
                }
                else if (holder == null || copy == null || holder.GetType() != copy.GetType() || !SameManagedValue(holder, copy)) return false;
                if (holder.GetType() == typeof(GH_Transform) && SafeGooInspection.KnownManagedValidity(holder) != captured.Validity) return false;
                if (!captured.HasReference) return true;
                bool available = SafeGooInspection.TryGetMeasurementReference(holder, out var reference,
                    captured.ReferenceResolved, deferManagedLoad: !captured.ReferenceResolved);
                if (!available || !reference.Equals(captured.Reference)) return false;
                if (!captured.ReferenceResolved && (reference.Referenced || reference.Id != Guid.Empty))
                {
                    try { if (SafeGooInspection.TryGetMeasurementReference(holder, out _, true)) return false; }
                    catch (Exception) { /* Still unresolved; the captured value remains unverified. */ }
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool TryCaptureManagedValue(IGH_Goo value, out CapturedManagedValue captured)
        {
            captured = default;
            object? rawValue = null;
            IGH_Goo? copy = null;
            if (value.GetType() == typeof(GH_Box)) rawValue = ((GH_Box)value).Value;
            else if (!TryCopyManagedValue(value, out copy) || copy == null) return false;
            bool hasReference = SafeGooInspection.TryGetMeasurementReference(value, out var raw, false, deferManagedLoad: true);
            // Plane/box load and validity checks may enter native code. Bind every
            // row and graph before evaluating them or querying a Rhino document.
            bool validityReady = value.GetType() == typeof(GH_Transform);
            var validity = validityReady ? SafeGooInspection.KnownManagedValidity(value) : BetterGhPanel.Core.PanelValidity.Unverified;
            captured = new CapturedManagedValue(copy, rawValue, validity, validityReady, hasReference, false, raw);
            return true;
        }

        internal static CapturedManagedValue ResolveManagedContext(CapturedManagedValue captured)
        {
            var copy = captured.Copy;
            if (!captured.Available) return captured;
            var validity = captured.Validity;
            if (captured.Raw is Box box)
            {
                try
                {
                    validity = box.IsValid ? BetterGhPanel.Core.PanelValidity.VerifiedValid : BetterGhPanel.Core.PanelValidity.VerifiedInvalid;
                    var boxedCopy = new GH_Box(box);
                    if (Same(boxedCopy.Value, box)) copy = boxedCopy;
                }
                catch (Exception) { validity = BetterGhPanel.Core.PanelValidity.Unverified; }
            }
            else if (!captured.ValidityReady && copy != null) validity = SafeGooInspection.KnownManagedValidity(copy);
            var reference = captured.Reference;
            bool resolved = false;
            if (captured.HasReference && (reference.Referenced || reference.Id != Guid.Empty))
            {
                try
                {
                    // Load state must come from the captured value, not a live
                    // holder which a callback could temporarily retarget.
                    bool loadedValue = captured.Raw is Box boxValue ? boxValue.IsValid :
                        copy != null && SafeGooInspection.TryGetMeasurementReference(copy, out var copiedReference, false) && copiedReference.Loaded;
                    var loaded = new MeasurementReferenceState(reference.Id, reference.Referenced, loadedValue,
                        reference.Edge, 0, 0);
                    if (SafeGooInspection.TryResolveCapturedReference(loaded, out var full))
                    { reference = full; resolved = true; }
                }
                catch (Exception) { /* An unresolved reference stays unverified. */ }
                if (!resolved) validity = BetterGhPanel.Core.PanelValidity.Unverified;
            }
            return new CapturedManagedValue(copy, captured.Raw, validity, true, captured.HasReference, resolved, reference);
        }

        internal void ResolveManagedContexts()
        {
            foreach (var node in _nodes) node.ResolveManagedContext();
        }

        // When deeper traversal is unavailable, preserve an exact wrapper chain
        // up to the existing per-root depth limit and an exact group's name/count,
        // without retaining its member graph.
        internal static CaptureGraphWitness? CaptureShallowIdentity(IGH_Goo? root)
        {
            if (root == null) return null;
            Type rootType = root.GetType();
            if (rootType != typeof(GH_ObjectWrapper) && rootType != typeof(GH_GeometricGooWrapper) &&
                rootType != typeof(GH_GeometryGroup)) return null;
            try
            {
                var result = new CaptureGraphWitness();
                object? value = root;
                HashSet<object>? seen = null;
                int depth = 0;
                while (value != null && depth < SafeGooInspection.MaxDepth - 1)
                {
                    Type type = value.GetType();
                    if (type != typeof(GH_ObjectWrapper) && type != typeof(GH_GeometricGooWrapper)) break;
                    seen ??= new HashSet<object>(ReferenceComparer.Instance);
                    if (!seen.Add(value)) break;
                    object? target = type == typeof(GH_ObjectWrapper) ? ((GH_ObjectWrapper)value).Value : ((GH_GeometricGooWrapper)value).InternalGoo;
                    result._nodes.Add(new Node(value, target));
                    value = target;
                    depth++;
                }
                if (value?.GetType() == typeof(GH_GeometryGroup))
                {
                    var group = (GH_GeometryGroup)value;
                    result._nodes.Add(new Node(group, group.Name ?? string.Empty, group.Objects.Count));
                }
                return result._nodes.Count == 0 ? null : result;
            }
            catch (Exception) { return null; }
        }

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
                        bool readable = geometry == null || !trusted || SafeGooInspection.MeasurementSourceCanRead(geometry);
                        result._nodes.Add(new Node(value, trusted, geometry, readable, hasReference, reference));
                    }
                    else if (value is IGH_Goo managed && TryCaptureManagedValue(managed, out var captured))
                        result._nodes.Add(new Node(value, captured));
                }
                witness = result;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal bool IsCurrent() => IsCurrent(out _);

        internal bool IsCurrent(out int visits)
        {
            visits = 0;
            try
            {
                if (_managedCopies != null)
                {
                    if (_managedHolders == null || _managedHolders.Length != _managedCopies.Length) return false;
                    for (int i = 0; i < _managedCopies.Length; i++)
                    {
                        visits++;
                        if (!ManagedValueCurrent(_managedHolders[i], _managedCopies[i])) return false;
                    }
                }
                foreach (var node in _nodes)
                {
                    bool current = node.IsCurrent(out int examined);
                    visits += examined;
                    if (!current) return false;
                }
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
            private CapturedManagedValue _managedValue;
            private readonly string? _name;
            private readonly WeakReference<object>?[]? _children;
            private readonly int _groupCount = -1;
            private readonly bool _trusted, _readable, _hasReference;
            private readonly MeasurementReferenceState _reference;
            internal Node(object value, object? target)
            { _value = new WeakReference<object>(value); _targetNull = target == null; if (target != null) _target = new WeakReference<object>(target); }
            internal Node(object value, CapturedManagedValue captured)
            { _value = new WeakReference<object>(value); _hasManagedValue = true; _managedValue = captured; }
            internal Node(object value, string name, object?[] children)
            {
                _value = new WeakReference<object>(value); _name = name; _groupCount = children.Length;
                _children = new WeakReference<object>?[children.Length];
                for (int i = 0; i < children.Length; i++) if (children[i] != null) _children[i] = new WeakReference<object>(children[i]!);
            }
            internal Node(object value, string name, int count)
            { _value = new WeakReference<object>(value); _name = name; _groupCount = count; }
            internal Node(object value, bool trusted, GeometryBase? geometry, bool readable, bool hasReference, MeasurementReferenceState reference)
            {
                _value = new WeakReference<object>(value); _trusted = trusted; _readable = readable; _targetNull = geometry == null;
                if (geometry != null) _target = new WeakReference<object>(geometry);
                _hasReference = hasReference; _reference = reference;
            }
            internal bool IsCurrent(out int visits)
            {
                visits = 1;
                if (!_value.TryGetTarget(out var value)) return false;
                if (_hasManagedValue) return ManagedValueCurrent(value as IGH_Goo, _managedValue);
                Type type = value.GetType();
                if (type == typeof(GH_ObjectWrapper)) return TargetIs(((GH_ObjectWrapper)value).Value);
                if (type == typeof(GH_GeometricGooWrapper)) return TargetIs(((GH_GeometricGooWrapper)value).InternalGoo);
                if (type == typeof(GH_GeometryGroup))
                {
                    var group = (GH_GeometryGroup)value;
                    var members = group.Objects;
                    if (!string.Equals(_name, group.Name ?? string.Empty, StringComparison.Ordinal) || members.Count != _groupCount) return false;
                    if (_children == null) return true;
                    for (int i = 0; i < _children.Length; i++)
                    {
                        visits++;
                        if (_children[i] == null) { if (members[i] != null) return false; }
                        else if (!_children[i]!.TryGetTarget(out var child) || !ReferenceEquals(child, members[i])) return false;
                    }
                    return true;
                }
                var goo = (IGH_Goo)value;
                bool trusted = SafeGooInspection.TryGetTrustedGeometry(goo, out GeometryBase? geometry);
                bool hasReference = SafeGooInspection.TryGetMeasurementReference(goo, out var reference, false);
                return trusted == _trusted && TargetIs(geometry) && hasReference == _hasReference &&
                    (!hasReference || reference.Equals(_reference)) &&
                    (geometry == null || !trusted || SafeGooInspection.MeasurementSourceCanRead(geometry) == _readable);
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
                if (_children == null) { children = Array.Empty<object?>(); return false; }
                children = new object?[_children.Length];
                for (int i = 0; i < children.Length; i++)
                    if (_children[i] != null && !_children[i]!.TryGetTarget(out children[i])) return false;
                return true;
            }
            internal bool HasManagedValue => _hasManagedValue;
            internal void ResolveManagedContext()
            { if (_hasManagedValue) _managedValue = CaptureGraphWitness.ResolveManagedContext(_managedValue); }
            internal BetterGhPanel.Core.PanelValidity ManagedValidity => _managedValue.Validity;
            internal IGH_Goo? ManagedCopy => _managedValue.Copy;
            internal bool TryGroupSummary(out string name, out int count)
            { name = _name ?? ""; count = _groupCount; return _groupCount >= 0; }
            internal bool Trusted => _trusted;
            internal bool Readable => _readable;
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
            else if (type == typeof(GH_Point))
            {
                var point = (GH_Point)value;
                var original = point.ReferenceData;
                GH_PointRefData? reference = null;
                if (original != null)
                {
                    if (original.m_RefParam == null || original.m_RefParam.Length != 2) { copy = null; return false; }
                    reference = new GH_PointRefData { m_RefID = original.m_RefID, m_RefIndex = original.m_RefIndex,
                        m_RefType = original.m_RefType, m_RefParam = new[] { original.m_RefParam[0], original.m_RefParam[1] } };
                }
                copy = new GH_Point(point.Value) { ReferenceData = reference };
            }
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
            else if (type == typeof(GH_MeshFace)) copy = new GH_MeshFace(((GH_MeshFace)value).Value);
            else if (type == typeof(GH_Transform))
            {
                var transforms = ((GH_Transform)value).CompoundTransforms;
                if (transforms.Count == 0) copy = new GH_Transform();
                else if (transforms.Count == 1 && transforms[0]?.GetType() == typeof(Grasshopper.Kernel.Types.Transforms.Generic))
                    copy = new GH_Transform(((Grasshopper.Kernel.Types.Transforms.Generic)transforms[0]).Transform);
                else { copy = null; return false; }
            }
            else { copy = null; return false; }
            return true;
        }

        internal static bool IsManagedValueType(Type type) => type == typeof(GH_Number) || type == typeof(GH_Integer) ||
            type == typeof(GH_String) || type == typeof(GH_Boolean) || type == typeof(GH_Point) || type == typeof(GH_Vector) ||
            type == typeof(GH_Colour) || type == typeof(GH_Interval) || type == typeof(GH_Interval2D) || type == typeof(GH_Guid) ||
            type == typeof(GH_Time) || type == typeof(GH_Plane) || type == typeof(GH_Line) || type == typeof(GH_Arc) ||
            type == typeof(GH_Circle) || type == typeof(GH_Rectangle) || type == typeof(GH_Box) || type == typeof(GH_MeshFace);

        private static bool SameManagedValue(object value, IGH_Goo copy)
        {
            Type type = value.GetType();
            if (type == typeof(GH_Number)) return Same(((GH_Number)value).Value, ((GH_Number)copy).Value);
            if (type == typeof(GH_Integer)) return ((GH_Integer)value).Value == ((GH_Integer)copy).Value;
            if (type == typeof(GH_String)) return string.Equals(((GH_String)value).Value, ((GH_String)copy).Value, StringComparison.Ordinal);
            if (type == typeof(GH_Boolean)) return ((GH_Boolean)value).Value == ((GH_Boolean)copy).Value;
            if (type == typeof(GH_Point)) return Same(((GH_Point)value).Value, ((GH_Point)copy).Value) &&
                SamePointReference(((GH_Point)value).ReferenceData, ((GH_Point)copy).ReferenceData);
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
            if (type == typeof(GH_MeshFace))
            { var a = ((GH_MeshFace)value).Value; var b = ((GH_MeshFace)copy).Value; return a.A == b.A && a.B == b.B && a.C == b.C && a.D == b.D; }
            if (type == typeof(GH_Transform))
            {
                var a = ((GH_Transform)value).CompoundTransforms; var b = ((GH_Transform)copy).CompoundTransforms;
                if (a.Count != b.Count) return false;
                if (a.Count == 0) return true;
                if (a.Count != 1 || a[0]?.GetType() != typeof(Grasshopper.Kernel.Types.Transforms.Generic) ||
                    b[0]?.GetType() != typeof(Grasshopper.Kernel.Types.Transforms.Generic)) return false;
                var x = ((Grasshopper.Kernel.Types.Transforms.Generic)a[0]).Transform;
                var y = ((Grasshopper.Kernel.Types.Transforms.Generic)b[0]).Transform;
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) if (!Same(x[r, c], y[r, c])) return false;
                return true;
            }
            return false;
        }

        private static bool SamePointReference(GH_PointRefData? a, GH_PointRefData? b)
        {
            if (a == null || b == null) return a == null && b == null;
            return a.m_RefID == b.m_RefID && a.m_RefIndex == b.m_RefIndex && a.m_RefType == b.m_RefType &&
                a.m_RefParam != null && b.m_RefParam != null && a.m_RefParam.Length == 2 && b.m_RefParam.Length == 2 &&
                Same(a.m_RefParam[0], b.m_RefParam[0]) && Same(a.m_RefParam[1], b.m_RefParam[1]);
        }

        private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
        private static bool Same(Point3d a, Point3d b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
        private static bool Same(Vector3d a, Vector3d b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
        private static bool Same(Interval a, Interval b) => Same(a.T0, b.T0) && Same(a.T1, b.T1);
        private static bool Same(Plane a, Plane b) => Same(a.Origin, b.Origin) && Same(a.XAxis, b.XAxis) && Same(a.YAxis, b.YAxis) && Same(a.ZAxis, b.ZAxis);
        private static bool Same(Box a, Box b) => Same(a.Plane, b.Plane) && Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
