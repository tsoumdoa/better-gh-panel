using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    /// <summary>
    /// Capture-time inspection only. Exact wrapper access is nonrecursive; no delegated
    /// metadata, validity or ScriptVariable is used for a wrapper or group. Limits are
    /// per root and per capture, and classifications are never reused across roots.
    /// This is not a copy/witness API or a sandbox against concurrent native mutation.
    /// </summary>
    internal static class SafeGooInspection
    {
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
        internal static Identity ResolveIdentity(IGH_Goo? root, Budget budget)
        {
            if (root == null) return new Identity("Null", true, null);
            Type rootType = root.GetType();
            string? directName = TrustedTypeName(rootType);
            if (directName != null) return new Identity(directName, true, rootType == typeof(GH_GeometryGroup) ? (GH_GeometryGroup)root : null);
            if (rootType != typeof(GH_ObjectWrapper) && rootType != typeof(GH_GeometricGooWrapper))
                return new Identity(SafeName(rootType), false, null);
            object? current = root;
            var seen = new HashSet<object>(ReferenceComparer.Instance);
            var layers = new List<bool>(); // true: geometric, false: object
            bool known = true;
            string? name = null;
            GH_GeometryGroup? group = null;
            int identityRemaining = MaxNodesPerRoot;
            for (int depth = 0; depth < MaxDepth && current != null; depth++)
            {
                if (!seen.Add(current)) { known = false; break; }
                Type type = current.GetType();
                // One direct wrapper read per row retains known Goo/{Group} identities
                // even after aggregate inspection is exhausted. Deeper chains share the
                // per-capture work budget with graph validity inspection.
                if (depth > 0 && (type == typeof(GH_ObjectWrapper) || type == typeof(GH_GeometricGooWrapper)) &&
                    !budget.Take(ref identityRemaining)) { known = false; break; }
                if (type == typeof(GH_ObjectWrapper))
                {
                    layers.Add(false);
                    current = ((GH_ObjectWrapper)current).Value;
                }
                else if (type == typeof(GH_GeometricGooWrapper))
                {
                    layers.Add(true);
                    current = ((GH_GeometricGooWrapper)current).InternalGoo;
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

        internal static PanelValidity Validity(IGH_Goo? root, Budget budget, out string reason, out GeometryBase? capturedGeometry)
        {
            reason = "";
            capturedGeometry = null;
            if (root == null) return PanelValidity.VerifiedValid;
            Type rootType = root.GetType();
            if (rootType != typeof(GH_ObjectWrapper) && rootType != typeof(GH_GeometricGooWrapper) && rootType != typeof(GH_GeometryGroup))
            {
                PanelValidity direct = LeafValidity(root, out capturedGeometry);
                if (direct == PanelValidity.VerifiedInvalid) reason = "The value is invalid.";
                else if (direct == PanelValidity.Unverified) reason = "Validity could not be safely verified.";
                return direct;
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
                    if (type == typeof(GH_ObjectWrapper))
                    {
                        object? value = ((GH_ObjectWrapper)frame.Node).Value;
                        if (value == null) invalid = true;
                        else stack.Push(new Frame(value, frame.Depth + 1));
                        continue;
                    }
                    if (type == typeof(GH_GeometricGooWrapper))
                    {
                        var value = ((GH_GeometricGooWrapper)frame.Node).InternalGoo;
                        if (value == null) invalid = true;
                        else stack.Push(new Frame(value, frame.Depth + 1));
                        continue;
                    }
                    if (type == typeof(GH_GeometryGroup)) continue;
                    var status = LeafValidity(frame.Node, out GeometryBase? geometry);
                    if (ReferenceEquals(root, frame.Node)) capturedGeometry = geometry;
                    if (status == PanelValidity.VerifiedInvalid) invalid = true;
                    if (status == PanelValidity.Unverified) unknown = true;
                }
                if (frame.Node.GetType() == typeof(GH_GeometryGroup))
                {
                    var children = ((GH_GeometryGroup)frame.Node).Objects;
                    if (frame.NextChild < children.Count)
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

        private static PanelValidity LeafValidity(object node, out GeometryBase? capturedGeometry)
        {
            capturedGeometry = null;
            try
            {
                if (node is IGH_Goo goo)
                {
                    if (TrustedTypeName(node.GetType()) == null) return PanelValidity.Unverified;
                    // GH_Arc exposes a more specific validity check than the
                    // inherited IGH_Goo implementation on Rhino 8.
                    if (node.GetType() == typeof(GH_Arc))
                        return ((GH_Arc)goo).IsValid ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid;
                    if (IsGeometryHolder(node.GetType()))
                    {
                        if (!TryGetTrustedGeometry(goo, out GeometryBase? geometry)) return PanelValidity.Unverified;
                        capturedGeometry = geometry;
                        return geometry != null && geometry.IsValid ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid;
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
            Type inner = geometry.GetType();
            return inner == typeof(LineCurve) || inner == typeof(ArcCurve) || inner == typeof(PolylineCurve) ||
                inner == typeof(NurbsCurve) || inner == typeof(PolyCurve) || inner == typeof(Brep) || inner == typeof(Mesh);
        }

        internal static bool IsGeometryHolder(Type type) => type == typeof(GH_Curve) || type == typeof(GH_Surface) ||
            type == typeof(GH_Brep) || type == typeof(GH_Mesh);

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
            return null;
        }

        private static string SafeName(Type type) => type.Name.Length == 0 ? "Unknown" : type.Name;
        private sealed class Frame
        {
            internal Frame(object node, int depth) { Node = node; Depth = depth; }
            internal readonly object Node;
            internal readonly int Depth;
            internal bool Entered;
            internal int NextChild;
        }
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
