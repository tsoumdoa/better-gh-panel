using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Rhino.Runtime;

namespace BetterGhPanel.Runtime
{
    internal readonly struct MeasurementReferenceState : IEquatable<MeasurementReferenceState>
    {
        internal MeasurementReferenceState(Guid id, bool referenced, bool loaded, int edge, uint document, uint objectSerial)
        { Id = id; Referenced = referenced; Loaded = loaded; Edge = edge; Document = document; ObjectSerial = objectSerial; }
        internal Guid Id { get; }
        internal bool Referenced { get; }
        internal bool Loaded { get; }
        internal int Edge { get; }
        internal uint Document { get; }
        internal uint ObjectSerial { get; }
        public bool Equals(MeasurementReferenceState other) => Id == other.Id && Referenced == other.Referenced && Loaded == other.Loaded &&
            Edge == other.Edge && Document == other.Document && ObjectSerial == other.ObjectSerial;
        public override bool Equals(object? value) => value is MeasurementReferenceState other && Equals(other);
        public override int GetHashCode() => Id.GetHashCode();
    }

    internal readonly struct CapturedGeometryLeaf
    {
        internal CapturedGeometryLeaf(GeometryBase? geometry, bool trusted, bool readable, bool hasReference, MeasurementReferenceState reference)
        { Geometry = geometry; Trusted = trusted; Readable = readable; HasReference = hasReference; Reference = reference; }
        internal GeometryBase? Geometry { get; }
        internal bool Trusted { get; }
        internal bool Readable { get; }
        internal bool HasReference { get; }
        internal MeasurementReferenceState Reference { get; }
    }

    /// <summary>
    /// Capture-time inspection only. Exact wrapper access is nonrecursive; no delegated
    /// metadata, validity or ScriptVariable is used for a wrapper or group. Limits are
    /// per root and per capture, and classifications are never reused across roots.
    /// This is not a copy/witness API or a sandbox against concurrent native mutation.
    /// </summary>
    internal static class SafeGooInspection
    {
        // GH_Light exists in the SDK but is not publicly exposed to C#.
        private static readonly Type? GhLightType = typeof(IGH_Goo).Assembly.GetType("Grasshopper.Kernel.Types.GH_Light");
        private static readonly Type? ModelDataType = typeof(IGH_Goo).Assembly.GetType("Grasshopper.Rhinoceros.ModelData");
        private static readonly Type? ModelValueType = typeof(IGH_Goo).Assembly.GetType("Grasshopper.Rhinoceros.ModelValue");
        private static readonly Dictionary<Type, string> ModelTypeNames = new Dictionary<Type, string>();
        internal const int MaxDepth = 64;
        internal const int MaxNodesPerRoot = 4096;
        internal const int MaxNodesPerCapture = 100000;

        internal sealed class Budget
        {
            internal int Remaining = MaxNodesPerCapture;
            internal int Visits { get; private set; }
            internal bool Take(ref int rootRemaining)
            {
                if (Remaining <= 0 || rootRemaining <= 0) return false;
                Remaining--; rootRemaining--; Visits++;
                return true;
            }
        }

        internal readonly struct Identity
        {
            internal Identity(string name, bool available, GH_GeometryGroup? group)
            { Name = name; Available = available; Group = group; }
            internal string Name { get; }
            internal bool Available { get; }
            internal GH_GeometryGroup? Group { get; }
        }

        // Exact GH_ObjectWrapper.Value and GH_GeometricGooWrapper.InternalGoo are
        // direct field-backed getters in the supported SDK. Their other members may
        // delegate and must not be used before classification.
        internal static Identity ResolveIdentity(IGH_Goo? root, CaptureGraphWitness? graph = null)
        {
            if (root == null) return new Identity("Null", true, null);
            Type rootType = root.GetType();
            string? directName = TrustedTypeName(rootType);
            if (directName != null) return new Identity(directName, true, rootType == typeof(GH_GeometryGroup) ? (GH_GeometryGroup)root : null);
            if (rootType != typeof(GH_ObjectWrapper) && rootType != typeof(GH_GeometricGooWrapper))
                return new Identity(SafeName(rootType), false, null);
            object? current = root;
            var captured = graph?.OpenInspection();
            var seen = new HashSet<object>(ReferenceComparer.Instance);
            var layers = new List<bool>(); // true: geometric, false: object
            bool known = true;
            string? name = null;
            GH_GeometryGroup? group = null;
            for (int depth = 0; depth < MaxDepth && current != null; depth++)
            {
                if (!seen.Add(current)) { known = false; break; }
                Type type = current.GetType();
                // Resolve only the captured exact chain. The existing depth limit
                // bounds this identity work per row independently of group traversal.
                if (type == typeof(GH_ObjectWrapper))
                {
                    layers.Add(false);
                    if (captured == null || !captured.TryGetValue(current, out var wrapper) || !wrapper.TryTarget(out current))
                    { known = false; break; }
                }
                else if (type == typeof(GH_GeometricGooWrapper))
                {
                    layers.Add(true);
                    if (captured == null || !captured.TryGetValue(current, out var wrapper) || !wrapper.TryTarget(out current))
                    { known = false; break; }
                }
                else
                {
                    if (type == typeof(GH_GeometryGroup)) group = (GH_GeometryGroup)current;
                    name = TrustedTypeName(type);
                    if (name == null) { name = SafeName(type); known = false; }
                    current = null;
                }
            }
            if (current != null) known = false; // depth exhausted or cycle
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                if (!layers[i]) { name = "Goo"; known = true; }
                else if (name == null) name = "Generic geometry";
                else name = "{" + name + "}";
            }
            return new Identity(name ?? SafeName(root.GetType()), known, group);
        }

        internal static PanelValidity Validity(IGH_Goo? root, Budget budget, out string reason, out GeometryBase? capturedGeometry,
            CaptureGraphWitness? graph = null, Action<object>? visitObserver = null, CapturedGeometryLeaf? directLeaf = null)
        {
            reason = "";
            capturedGeometry = null;
            if (root == null) return PanelValidity.VerifiedValid;
            Type rootType = root.GetType();
            if (rootType != typeof(GH_ObjectWrapper) && rootType != typeof(GH_GeometricGooWrapper) && rootType != typeof(GH_GeometryGroup))
            {
                PanelValidity direct;
                if (directLeaf.HasValue)
                {
                    var leaf = directLeaf.Value;
                    capturedGeometry = leaf.Geometry;
                    direct = CapturedGeometryValidity(rootType, leaf.Geometry, leaf.Trusted, leaf.HasReference, leaf.Reference, true, leaf.Readable);
                }
                else if (graph?.ManagedCopy(root) is IGH_Goo copy) direct = KnownManagedValidity(copy);
                else direct = LeafValidity(root, out capturedGeometry, true);
                if (direct == PanelValidity.VerifiedInvalid) reason = "The value is invalid.";
                else if (direct == PanelValidity.Unverified) reason = "Validity could not be safely verified.";
                return direct;
            }
            var captured = graph?.OpenInspection();
            if (captured == null)
            {
                reason = "Validity could not be safely verified (capture graph unavailable).";
                return PanelValidity.Unverified;
            }
            int remaining = MaxNodesPerRoot;
            var active = new HashSet<object>(ReferenceComparer.Instance);
            var stack = new Stack<Frame>();
            stack.Push(new Frame(root, 0));
            bool invalid = false, unknown = false;
            while (stack.Count != 0)
            {
                var frame = stack.Peek();
                if (!frame.Entered)
                {
                    frame.Entered = true;
                    if (remaining <= 0 || budget.Remaining <= 0) { unknown = true; break; }
                    if (frame.Depth >= MaxDepth || !budget.Take(ref remaining) || !active.Add(frame.Node))
                    { unknown = true; stack.Pop(); continue; }
                    Type type = frame.Node.GetType();
                    visitObserver?.Invoke(frame.Node);
                    if (type == typeof(GH_ObjectWrapper) || type == typeof(GH_GeometricGooWrapper))
                    {
                        if (!captured.TryGetValue(frame.Node, out var wrapper) || !wrapper.TryTarget(out object? value))
                        { unknown = true; active.Remove(frame.Node); stack.Pop(); continue; }
                        if (value == null) invalid = true;
                        else stack.Push(new Frame(value, frame.Depth + 1));
                        continue;
                    }
                    if (type == typeof(GH_GeometryGroup))
                    {
                        if (!captured.TryGetValue(frame.Node, out var group) || !group.TryChildren(out frame.Children))
                        { unknown = true; active.Remove(frame.Node); stack.Pop(); }
                        continue;
                    }
                    PanelValidity status;
                    GeometryBase? geometry = null;
                    if (captured.TryGetValue(frame.Node, out var leaf) && leaf.HasManagedValue) status = leaf.ManagedValidity;
                    else if (IsGeometryHolder(type) && captured.TryGetValue(frame.Node, out leaf))
                    {
                        if (!leaf.TryTarget(out var target)) status = PanelValidity.Unverified;
                        else
                        {
                            geometry = target as GeometryBase;
                            status = CapturedGeometryValidity(type, geometry, leaf.Trusted, leaf.HasReference, leaf.Reference, false, leaf.Readable);
                        }
                    }
                    else status = LeafValidity(frame.Node, out geometry, false);
                    if (ReferenceEquals(root, frame.Node)) capturedGeometry = geometry;
                    if (status == PanelValidity.VerifiedInvalid) invalid = true;
                    if (status == PanelValidity.Unverified) unknown = true;
                }
                if (frame.Node.GetType() == typeof(GH_GeometryGroup))
                {
                    var children = frame.Children!;
                    if (frame.NextChild < children.Length)
                    {
                        // Null slots count toward direct summaries and work limits too.
                        if (!budget.Take(ref remaining)) { unknown = true; break; }
                        var child = children[frame.NextChild++];
                        if (child != null) stack.Push(new Frame(child, frame.Depth + 1));
                        continue;
                    }
                }
                active.Remove(frame.Node);
                stack.Pop();
            }
            if (invalid) { reason = "A known value or group member is invalid."; return PanelValidity.VerifiedInvalid; }
            if (unknown) { reason = "Validity could not be safely verified (unsupported value, cycle or inspection limit)."; return PanelValidity.Unverified; }
            return PanelValidity.VerifiedValid;
        }

        internal static PanelValidity KnownManagedValidity(IGH_Goo goo) => LeafValidity(goo, out _, false);

        private static PanelValidity LeafValidity(object node, out GeometryBase? capturedGeometry, bool standalone)
        {
            capturedGeometry = null;
            try
            {
                if (node is IGH_Goo goo)
                {
                    if (node.GetType() == typeof(GH_Transform))
                    {
                        // GH_Transform can contain arbitrary ITransform objects.
                        // Only the empty or single exact stock matrix form is safe.
                        var transforms = ((GH_Transform)goo).CompoundTransforms;
                        if (transforms.Count > 1 || (transforms.Count == 1 &&
                            transforms[0]?.GetType() != typeof(Grasshopper.Kernel.Types.Transforms.Generic))) return PanelValidity.Unverified;
                        return goo.IsValid ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid;
                    }
                    // A constant stock TypeName does not make a goo's validity path safe:
                    // some stock holders can contain externally supplied implementations.
                    if (!HasTrustedLeafValidity(node.GetType())) return PanelValidity.Unverified;
                    // GH_Arc exposes a more specific validity check than the
                    // inherited IGH_Goo implementation on Rhino 8.
                    if (node.GetType() == typeof(GH_Arc))
                        return ((GH_Arc)goo).IsValid ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid;
                    if (IsGeometryHolder(node.GetType()))
                    {
                        bool hasReferenceState = TryGetMeasurementReference(goo, out var reference);
                        bool trusted = TryGetTrustedGeometry(goo, out GeometryBase? geometry);
                        capturedGeometry = geometry;
                        return CapturedGeometryValidity(node.GetType(), geometry, trusted, hasReferenceState, reference, standalone);
                    }
                    return goo.IsValid ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid;
                }
                if (node is Point3d point) return point.IsValid ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid;
                if (node is Line line) return line.IsValid ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid;
                if (node is Arc arc) return arc.IsValid ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid;
                if (node is Circle circle) return circle.IsValid ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid;
                return PanelValidity.Unverified;
            }
            catch (Exception) { return PanelValidity.Unverified; }
        }

        private static PanelValidity CapturedGeometryValidity(Type holderType, GeometryBase? geometry, bool trusted,
            bool hasReference, MeasurementReferenceState reference, bool standalone, bool capturedReadable = true)
        {
            try
            {
                if (!trusted || ((holderType == typeof(GH_Curve) || holderType == typeof(GH_Surface) || holderType == typeof(GH_Brep)) &&
                    !hasReference)) return PanelValidity.Unverified;
                if (!standalone && hasReference && (reference.Referenced || reference.Id != Guid.Empty)) return PanelValidity.Unverified;
                if (geometry == null) return PanelValidity.VerifiedInvalid;
                if (!standalone && (geometry.GetType() == typeof(PolylineCurve) || geometry.GetType() == typeof(NurbsCurve)) &&
                    !CurveCandidatePreflight(geometry)) return PanelValidity.Unverified;
                if (!capturedReadable || !MeasurementSourceCanRead(geometry)) return PanelValidity.Unverified;
                return geometry.IsValid ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid;
            }
            catch (Exception) { return PanelValidity.Unverified; }
            finally { GC.KeepAlive(geometry); }
        }

        internal static bool CurveCandidatePreflight(GeometryBase source)
        {
            try
            {
                if (source.GetType() != typeof(PolylineCurve) && source.GetType() != typeof(NurbsCurve)) return false;
                if (!CurveSourceCanRead(source)) return false;
                if (source.GetType() == typeof(PolylineCurve))
                {
                    var curve = (PolylineCurve)source;
                    return NurbsCandidateLimits.Polyline(curve.Dimension, curve.PointCount, out _);
                }
                if (source.GetType() == typeof(NurbsCurve))
                {
                    var curve = (NurbsCurve)source;
                    return NurbsCandidateLimits.Curve(curve.Dimension, curve.Order, curve.Points.Count, curve.Knots.Count, out _);
                }
                return false;
            }
            finally { GC.KeepAlive(source); }
        }

        internal static bool CurveSourceCanRead(GeometryBase source)
        {
            try { return !CommonObject.PerformCorruptionTesting && !source.Disposed && !source.IsDocumentControlled; }
            finally { GC.KeepAlive(source); }
        }

        // Only exact built-in holders are inspected. Never dispatch through an
        // arbitrary IGH_GeometricGoo or unwrap a group to prepare a measurement.
        internal static bool TryGetMeasurementReference(IGH_Goo goo, out MeasurementReferenceState state,
            bool requireDocumentContext = true, bool deferManagedLoad = false)
        {
            state = default;
            Guid id; bool referenced, loaded; int edge = -1;
            Type type = goo.GetType();
            if (type == typeof(GH_Curve))
            { var holder = (GH_Curve)goo; id = holder.ReferenceID; referenced = holder.IsReferencedGeometry; loaded = holder.IsGeometryLoaded; edge = holder.ReferenceEdge; }
            else if (type == typeof(GH_Surface))
            { var holder = (GH_Surface)goo; id = holder.ReferenceID; referenced = holder.IsReferencedGeometry; loaded = holder.IsGeometryLoaded; }
            else if (type == typeof(GH_Brep))
            { var holder = (GH_Brep)goo; id = holder.ReferenceID; referenced = holder.IsReferencedGeometry; loaded = holder.IsGeometryLoaded; }
            else if (type == typeof(GH_Mesh))
            { var holder = (GH_Mesh)goo; id = holder.ReferenceID; referenced = holder.IsReferencedGeometry; loaded = holder.IsGeometryLoaded; }
            else if (type == typeof(GH_Point))
            { var holder = (GH_Point)goo; id = holder.ReferenceID; referenced = holder.IsReferencedGeometry; loaded = !deferManagedLoad && holder.IsGeometryLoaded; }
            else if (type == typeof(GH_Plane))
            { var holder = (GH_Plane)goo; id = holder.ReferenceID; referenced = holder.IsReferencedGeometry; loaded = !deferManagedLoad && holder.IsGeometryLoaded; }
            else if (type == typeof(GH_Box))
            { var holder = (GH_Box)goo; id = holder.ReferenceID; referenced = holder.IsReferencedGeometry; loaded = !deferManagedLoad && holder.IsGeometryLoaded; }
            else return false;
            state = new MeasurementReferenceState(id, referenced, loaded, edge, 0, 0);
            return !requireDocumentContext || TryResolveCapturedReference(state, out state);
        }

        internal static bool TryResolveCapturedReference(MeasurementReferenceState raw, out MeasurementReferenceState state)
        {
            state = raw;
            if (raw.Referenced || raw.Id != Guid.Empty)
            {
                // There is no public holder API exposing which document supplied
                // a loaded value. Admit references only while the active document
                // has the referenced object; record both runtime identities.
                var document = Rhino.RhinoDoc.ActiveDoc;
                var obj = document?.Objects.FindId(raw.Id);
                if (!raw.Loaded || raw.Id == Guid.Empty || document == null || obj == null) return false;
                state = new MeasurementReferenceState(raw.Id, raw.Referenced, raw.Loaded, raw.Edge,
                    document.RuntimeSerialNumber, obj.RuntimeSerialNumber);
            }
            return true;
        }

        internal static bool MeasurementSourceCanRead(GeometryBase source)
        {
            try { return !CommonObject.PerformCorruptionTesting && !source.Disposed; }
            finally { GC.KeepAlive(source); }
        }

        internal static bool TryCaptureStandaloneGeometryRaw(IGH_Goo goo, out CapturedGeometryLeaf leaf)
        {
            leaf = default;
            if (!IsGeometryHolder(goo.GetType())) return false;
            // Read the exact holder's fields before any document or geometry call
            // that might reenter. Validation will use this object, never reread it.
            bool trusted = TryGetTrustedGeometry(goo, out var geometry);
            bool hasReference = TryGetMeasurementReference(goo, out var reference, false);
            bool readable = geometry == null || !trusted || MeasurementSourceCanRead(geometry);
            leaf = new CapturedGeometryLeaf(geometry, trusted, readable, hasReference, reference);
            return true;
        }

        internal static CapturedGeometryLeaf ResolveCapturedGeometryContext(CapturedGeometryLeaf leaf)
        {
            var reference = leaf.Reference;
            if (!leaf.HasReference || (!reference.Referenced && reference.Id == Guid.Empty)) return leaf;
            var document = Rhino.RhinoDoc.ActiveDoc;
            var obj = document?.Objects.FindId(reference.Id);
            if (!reference.Loaded || reference.Id == Guid.Empty || document == null || obj == null)
                return new CapturedGeometryLeaf(leaf.Geometry, leaf.Trusted, leaf.Readable, false, reference);
            var resolved = new MeasurementReferenceState(reference.Id, reference.Referenced, reference.Loaded, reference.Edge,
                document.RuntimeSerialNumber, obj.RuntimeSerialNumber);
            return new CapturedGeometryLeaf(leaf.Geometry, leaf.Trusted, leaf.Readable, true, resolved);
        }

        internal static bool TryCaptureStandaloneGeometry(IGH_Goo goo, out CapturedGeometryLeaf leaf)
        {
            if (!TryCaptureStandaloneGeometryRaw(goo, out leaf)) return false;
            leaf = ResolveCapturedGeometryContext(leaf);
            return true;
        }

        internal static bool IsCurrentMeasurementSource(IGH_Goo? holder, GeometryBase source, MeasurementReferenceState reference)
        {
            try
            {
                if (holder == null || source == null || !TryGetMeasurementReference(holder, out var currentReference) ||
                    !reference.Equals(currentReference)) return false;
                Type type = holder.GetType(), inner = source.GetType();
                bool current = type == typeof(GH_Curve) &&
                    (inner == typeof(LineCurve) || inner == typeof(ArcCurve) || inner == typeof(PolylineCurve) ||
                     inner == typeof(NurbsCurve) || inner == typeof(PolyCurve)) && ReferenceEquals(((GH_Curve)holder).Value, source);
                if (type == typeof(GH_Surface) && inner == typeof(Brep)) current = ReferenceEquals(((GH_Surface)holder).Value, source);
                if (type == typeof(GH_Brep) && inner == typeof(Brep)) current = ReferenceEquals(((GH_Brep)holder).Value, source);
                return current && MeasurementSourceCanRead(source);
            }
            catch (Exception) { return false; }
            finally { GC.KeepAlive(source); }
        }

        // Capture freshness applies to every exact geometry holder, including
        // meshes and null, invalid or unsupported inner values. This check does
        // not make those values eligible for measurement.
        internal static bool IsCurrentCapturedGeometry(IGH_Goo? holder, CapturedGeometryLeaf captured)
        {
            try
            {
                if (holder == null || !IsGeometryHolder(holder.GetType())) return false;
                bool trusted = TryGetTrustedGeometry(holder, out var geometry);
                if (trusted != captured.Trusted || !ReferenceEquals(geometry, captured.Geometry)) return false;
                if (!TryGetMeasurementReference(holder, out var reference, captured.HasReference) || !reference.Equals(captured.Reference)) return false;
                if (!captured.HasReference && TryGetMeasurementReference(holder, out _, true)) return false;
                return geometry == null || !trusted || MeasurementSourceCanRead(geometry) == captured.Readable;
            }
            catch (Exception) { return false; }
            finally { GC.KeepAlive(captured.Geometry); }
        }

        /// <summary>
        /// Reads a known exact holder once and accepts only enumerated exact Rhino
        /// implementations. Null is a recognized invalid value. No copy, metadata,
        /// serialization, user-data traversal or retained ownership is implied.
        /// </summary>
        internal static bool TryGetTrustedGeometry(IGH_Goo goo, out GeometryBase? geometry)
        {
            geometry = null;
            Type type = goo.GetType();
            if (type == typeof(GH_Curve)) geometry = ((GH_Curve)goo).Value;
            else if (type == typeof(GH_Surface)) geometry = ((GH_Surface)goo).Value;
            else if (type == typeof(GH_Brep)) geometry = ((GH_Brep)goo).Value;
            else if (type == typeof(GH_Mesh)) geometry = ((GH_Mesh)goo).Value;
            else return false;
            if (geometry == null) return true;
            return IsTrustedGeometry(geometry);
        }

        internal static bool IsTrustedGeometry(GeometryBase geometry)
        {
            Type inner = geometry.GetType();
            return inner == typeof(Point) || inner == typeof(LineCurve) || inner == typeof(ArcCurve) ||
                inner == typeof(PolylineCurve) || inner == typeof(NurbsCurve) || inner == typeof(PolyCurve) ||
                inner == typeof(Brep) || inner == typeof(Mesh) || inner == typeof(SubD) ||
                inner == typeof(NurbsSurface) || inner == typeof(PlaneSurface) || inner == typeof(RevSurface);
        }

        internal static bool IsGeometryHolder(Type type) => type == typeof(GH_Curve) || type == typeof(GH_Surface) ||
            type == typeof(GH_Brep) || type == typeof(GH_Mesh);

        // Keep the previously audited validity allowlist independent of the broader
        // identity table. Newly recognized identities are not evidence of safe validity.
        private static bool HasTrustedLeafValidity(Type type) =>
            type == typeof(GH_Number) || type == typeof(GH_Integer) || type == typeof(GH_String) ||
            type == typeof(GH_Boolean) || type == typeof(GH_Colour) || type == typeof(GH_Interval) ||
            type == typeof(GH_Interval2D) || type == typeof(GH_Guid) || type == typeof(GH_Time) ||
            type == typeof(GH_Point) || type == typeof(GH_Plane) || type == typeof(GH_Vector) ||
            type == typeof(GH_Line) || type == typeof(GH_Arc) || type == typeof(GH_Circle) ||
            type == typeof(GH_Rectangle) || type == typeof(GH_Box) || type == typeof(GH_MeshFace) || IsGeometryHolder(type);

        internal static string? TrustedTypeName(Type type)
        {
            if (type == typeof(GH_GeometryGroup)) return "Group";
            if (type == typeof(GH_Number)) return "Number";
            if (type == typeof(GH_Integer)) return "Integer";
            if (type == typeof(GH_String)) return "Text";
            if (type == typeof(GH_Boolean)) return "Boolean";
            if (type == typeof(GH_Colour)) return "Colour";
            if (type == typeof(GH_Interval)) return "Domain";
            if (type == typeof(GH_Interval2D)) return "Domain²";
            if (type == typeof(GH_Guid)) return "Guid";
            if (type == typeof(GH_Time)) return "Time";
            if (type == typeof(GH_Point)) return "Point";
            if (type == typeof(GH_Plane)) return "Plane";
            if (type == typeof(GH_Vector)) return "Vector";
            if (type == typeof(GH_Line)) return "Line";
            if (type == typeof(GH_Arc)) return "Arc";
            if (type == typeof(GH_Circle)) return "Circle";
            if (type == typeof(GH_Rectangle)) return "Rectangle";
            if (type == typeof(GH_Box)) return "Box";
            if (type == typeof(GH_Curve)) return "Curve";
            if (type == typeof(GH_Surface)) return "Surface";
            if (type == typeof(GH_Brep)) return "Brep";
            if (type == typeof(GH_Mesh)) return "Mesh";
            // These are constant TypeName getters in the Grasshopper 8 SDK and the
            // installed Rhino 8 assembly. Do not call the getters on arbitrary goo.
            if (type == typeof(GH_Transform)) return "Transform";
            if (type == typeof(GH_MeshFace)) return "Meshface";
            if (type == typeof(GH_ComplexNumber)) return "Complex";
            if (type == typeof(GH_SubD)) return "SubD";
            if (type == typeof(GH_Extrusion)) return "Extrusion";
            if (type == typeof(GH_AngularDimension)) return "Angular Dimension";
            if (type == typeof(GH_Centermark)) return "Radial Centermark";
            if (type == typeof(GH_Hatch)) return "Hatch";
            if (type == typeof(GH_InstanceReference)) return "Block Instance";
            if (type == typeof(GH_Leader)) return "Leader";
            if (type == GhLightType) return "Light";
            if (type == typeof(GH_LinearDimension)) return "Linear Dimension";
            if (type == typeof(GH_OrdinateDimension)) return "Ordinate Dimension";
            if (type == typeof(GH_RadialDimension)) return "Radial Dimension";
            if (type == typeof(GH_TextDot)) return "Text Dot";
            if (type == typeof(GH_TextEntity)) return "Text Entity";
            if (type == typeof(GH_Culture)) return "Culture";
            if (type == typeof(GH_DetailView)) return "Detail View";
            if (type == typeof(GH_Field)) return "Field";
            if (type == typeof(GH_LonLatCoordinate)) return "Location";
            if (type == typeof(GH_Material)) return "Material";
            if (type == typeof(GH_Matrix)) return "Matrix";
            if (type == typeof(GH_MeshingParameters)) return "MeshParameters";
            if (type == typeof(GH_PointCloud)) return "Point Cloud";
            if (type == typeof(GH_Predicate)) return "Predicate";
            if (type == typeof(GH_StructurePath)) return "Path";
            return TrustedModelTypeName(type);
        }

        // Rhino model goo names are either constant getters or attributes consumed
        // by the stock ModelData/ModelValue getter. SDK releases change some of
        // those attributes, so read their metadata without constructing attributes
        // or invoking a goo. Dynamic model object/material getters remain unknown.
        private static string? TrustedModelTypeName(Type type)
        {
            if (type.Assembly != typeof(IGH_Goo).Assembly || type.FullName == null ||
                !type.FullName.StartsWith("Grasshopper.Rhinoceros.", StringComparison.Ordinal)) return null;
            lock (ModelTypeNames)
            {
                if (ModelTypeNames.TryGetValue(type, out string? cached)) return string.IsNullOrEmpty(cached) ? null : cached;
                string? name = null;
                switch (type.FullName)
                {
                    case "Grasshopper.Rhinoceros.Annotations.AnnotationDateTimeFormat": name = "Text"; break;
                    case "Grasshopper.Rhinoceros.Display.DisplayColorGradient": name = "Colour Gradient"; break;
                    case "Grasshopper.Rhinoceros.Display.DisplayColorStop": name = "Colour Stop"; break;
                    case "Grasshopper.Rhinoceros.Display.ModelView": name = "View"; break;
                    case "Grasshopper.Rhinoceros.Drafting.ModelLineWidth": name = "Print Width"; break;
                    case "Grasshopper.Rhinoceros.ModelColor": name = "Model Colour"; break;
                    case "Grasshopper.Rhinoceros.ModelFont": name = "Font"; break;
                    default:
                        try
                        {
                            Type? declaring = type.GetProperty("TypeName")?.GetMethod?.DeclaringType;
                            if (declaring != null && (declaring == ModelDataType || declaring == ModelValueType))
                            {
                                foreach (var attribute in type.GetCustomAttributesData())
                                    if (attribute.AttributeType.Assembly == typeof(IGH_Goo).Assembly &&
                                        attribute.AttributeType.FullName == "Grasshopper.Kernel.Types.TypeNameAttribute" &&
                                        attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is string value)
                                    { name = value; break; }
                            }
                        }
                        catch (Exception) { /* Treat inaccessible or malformed metadata as unknown. */ }
                        break;
                }
                ModelTypeNames[type] = name ?? "";
                return name;
            }
        }

        private static string SafeName(Type type) => type.Name.Length == 0 ? "Unknown" : type.Name;
        private sealed class Frame
        {
            internal Frame(object node, int depth) { Node = node; Depth = depth; }
            internal readonly object Node;
            internal readonly int Depth;
            internal bool Entered;
            internal int NextChild;
            internal object?[]? Children;
        }
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
