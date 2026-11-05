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
                    else if (IsCapturedGeometryHolder(type) && captured.TryGetValue(frame.Node, out leaf))
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
                    Type leafType = node.GetType();
                    if (leafType == typeof(GH_Culture))
                    {
                        var culture = ((GH_Culture)goo).Value;
                        return culture == null ? PanelValidity.VerifiedInvalid : culture.GetType() == typeof(System.Globalization.CultureInfo) ? PanelValidity.VerifiedValid : PanelValidity.Unverified;
                    }
                    if (leafType == typeof(GH_Matrix))
                    {
                        var matrix = ((GH_Matrix)goo).Value;
                        if (matrix != null && (matrix.GetType() != typeof(Matrix) || (long)matrix.RowCount * matrix.ColumnCount > MaxNodesPerRoot)) return PanelValidity.Unverified;
                    }
                    if (leafType == typeof(GH_StructurePath))
                    {
                        var path = ((GH_StructurePath)goo).Value;
                        if (path != null && (path.GetType() != typeof(Grasshopper.Kernel.Data.GH_Path) || path.Length > MaxNodesPerRoot)) return PanelValidity.Unverified;
                    }
                    // A constant stock TypeName does not make a goo's validity path safe:
                    // some stock holders can contain externally supplied implementations.
                    if (!HasTrustedLeafValidity(node.GetType())) return PanelValidity.Unverified;
                    // GH_Arc exposes a more specific validity check than the
                    // inherited IGH_Goo implementation on Rhino 8.
                    if (node.GetType() == typeof(GH_Arc))
                        return ((GH_Arc)goo).IsValid ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid;
                    if (IsCapturedGeometryHolder(node.GetType()))
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
                if (!trusted || (IsCapturedGeometryHolder(holderType) && !hasReference)) return PanelValidity.Unverified;
                if (!standalone && hasReference && (reference.Referenced || reference.Id != Guid.Empty) &&
                    (StockGooSupport.Has(holderType, StockTypeFacets.UnverifiedGroupReference) || reference.Document == 0 || reference.ObjectSerial == 0))
                    return PanelValidity.Unverified;
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
            else if (IsCapturedGeometryHolder(type) && StockGooSupport.Has(type, StockTypeFacets.ShapeOnlyLoad) && goo is IGH_GeometricGoo metadata)
            {
                // Annotation, hatch and block IsGeometryLoaded may consult model
                // styles/definitions (including externally supplied subclasses).
                // Our metadata capture concerns only the native shape, not those
                // dependencies. Never load or inspect their model objects.
                id = metadata.ReferenceID; referenced = metadata.IsReferencedGeometry;
                TryGetTrustedGeometry(goo, out var shape); loaded = shape != null;
            }
            else if (IsCapturedGeometryHolder(type) && goo is IGH_GeometricGoo stock)
            { id = stock.ReferenceID; referenced = stock.IsReferencedGeometry; loaded = stock.IsGeometryLoaded; }
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
            if (!IsCapturedGeometryHolder(goo.GetType())) return false;
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
            try
            {
                if (TryResolveCapturedReference(reference, out var resolved))
                    return new CapturedGeometryLeaf(leaf.Geometry, leaf.Trusted, leaf.Readable, true, resolved);
            }
            catch (Exception) { /* The context is unavailable, not verified invalid. */ }
            return new CapturedGeometryLeaf(leaf.Geometry, leaf.Trusted, leaf.Readable, false, reference);
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
                return StockGooSupport.Has(holder.GetType(), StockTypeFacets.Measure) &&
                    TryGetTrustedGeometry(holder, out var current) && ReferenceEquals(current, source) && MeasurementSourceCanRead(source);
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
                if (holder == null || !IsCapturedGeometryHolder(holder.GetType())) return false;
                bool trusted = TryGetTrustedGeometry(holder, out var geometry);
                if (trusted != captured.Trusted || !ReferenceEquals(geometry, captured.Geometry)) return false;
                if (!TryGetMeasurementReference(holder, out var reference, captured.HasReference) || !reference.Equals(captured.Reference)) return false;
                if (!captured.HasReference)
                {
                    try { if (TryGetMeasurementReference(holder, out _, true)) return false; }
                    catch (Exception) { /* Still unresolved. Recheck on the next capture. */ }
                }
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
        internal static bool TryGetTrustedGeometry(IGH_Goo goo, out GeometryBase? geometry) => StockGooSupport.TryGeometry(goo, out geometry);
        internal static bool IsTrustedGeometry(GeometryBase geometry) => StockGooSupport.IsPreviewGeometry(geometry.GetType());
        internal static bool IsCapturedGeometryHolder(Type type) => StockGooSupport.Has(type, StockTypeFacets.Capture);
        private static bool HasTrustedLeafValidity(Type type) => StockGooSupport.Has(type, StockTypeFacets.Validity);
        internal static string? TrustedTypeName(Type type) => StockGooSupport.Find(type)?.Name ?? TrustedModelTypeName(type);

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
