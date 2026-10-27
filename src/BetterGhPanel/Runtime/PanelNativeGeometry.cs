using System;
using System.Collections.Generic;
using System.IO;
using Rhino.DocObjects.Custom;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // Inspect the exposed components of exact SDK geometry before native work.
    // Explicit copy can also attempt managed serializable UserData using Rhino's
    // normal callbacks; its complete contents cannot be universally witnessed.
    // The public API does not expose every native topology or metadata detail.
    internal static class PanelNativeGeometry
    {
        internal const int MaxNativeBytes = 1024 * 1024;
        internal const int MaxComponents = 1024;
        internal const int MaxCollection = 256;
        internal const int MaxMetadataBytes = 64 * 1024;
        internal const int MaxLegacyCurvePoints = 32768;
        internal const int MaxPointCloudPoints = 32768;
        private const int MaxVertexEstimate = 16 * 1024;
        private static readonly Type? SharedDictionaryType = typeof(UserData).Assembly.GetType("Rhino.DocObjects.Custom.SharedUserDictionary");

        internal sealed class Scan
        {
            internal Scan(long nativeBytes, byte[] metadata, bool hasMetadata, bool unverifiedCustom = false)
            { NativeBytes = nativeBytes; Metadata = metadata; HasMetadata = hasMetadata; UnverifiedCustom = unverifiedCustom; }
            internal long NativeBytes { get; }
            internal byte[] Metadata { get; }
            internal bool HasMetadata { get; }
            internal bool UnverifiedCustom { get; }
        }

        // Compatibility preview uses Rhino's normal duplication callbacks after
        // bounded structural preflight. Individual native/callback work is not
        // a hard sandbox; the retained snapshot still has a reserved size cap.
        internal static bool TryScanForPreview(GeometryBase geometry, PanelGeometrySources.Budget budget, ref int rootRemaining,
            Func<bool>? current, out Scan? scan)
            => TryScan(geometry, budget, ref rootRemaining, false, true, current, out scan);

        internal static bool TryScanForCopy(GeometryBase geometry, PanelGeometrySources.Budget budget, ref int rootRemaining,
            Func<bool> current, out Scan? scan)
            => TryScan(geometry, budget, ref rootRemaining, false, true, current, out scan);

        internal static bool TryScanForBounds(GeometryBase geometry, PanelGeometrySources.Budget budget, ref int rootRemaining)
            => TryScan(geometry, budget, ref rootRemaining, true, false, null, out _);

        private static bool TryScan(GeometryBase geometry, PanelGeometrySources.Budget budget, ref int rootRemaining,
            bool forBounds, bool allowCustom, Func<bool>? current, out Scan? scan)
        {
            scan = null;
            try
            {
                // Corruption testing can implicitly run native validity from a
                // const-pointer getter before this preflight has applied limits.
                if (Rhino.Runtime.CommonObject.PerformCorruptionTesting)
                { budget.MarkUnavailable("Rhino corruption testing is enabled"); return false; }
                var pending = new Stack<Node>();
                var observed = new List<GeometryBase>();
                pending.Push(new Node(geometry, false, 0));
                int nodes = 0, metadataSize = 0;
                bool hasMetadata = false, unverifiedCustom = false;
                using var stream = new MemoryStream();
                using var writer = new BinaryWriter(stream);
                while (pending.Count > 0)
                {
                    if (++nodes > MaxComponents) { budget.MarkLimit(); return false; }
                    if (!budget.Take(ref rootRemaining)) return false;
                    Node node = pending.Pop();
                    if (node.Depth >= PanelGeometrySources.MaxDepth) { budget.MarkLimit(); return false; }
                    var value = node.Value;
                    Type type = value.GetType();
                    // Reject large clouds/profile tables before inspecting
                    // metadata or invoking any duplication/serialization path.
                    if (!node.Component && type == typeof(PointCloud))
                    {
                        int count = ((PointCloud)value).Count;
                        if (count <= 0) return false;
                        if (count > MaxPointCloudPoints) { budget.MarkLimit(); return false; }
                    }
                    if (!node.Component && type == typeof(Extrusion))
                    {
                        int count = ((Extrusion)value).ProfileCount;
                        if (count <= 0) return false;
                        if (count > MaxCollection || count > rootRemaining || count > budget.Remaining)
                        { budget.MarkLimit(); return false; }
                    }
                    observed.Add(value);
                    if (!forBounds && node.Optional && value.HasUserData)
                    {
                        if (metadataSize > MaxMetadataBytes - 5) { budget.MarkLimit(); return false; }
                        writer.Write((byte)2); writer.Write(node.OptionalTag); metadataSize += 5;
                    }
                    if (!forBounds && (!node.Optional || value.HasUserData) &&
                        !WriteMetadata(value, writer, budget, allowCustom, current, ref metadataSize, ref hasMetadata, ref unverifiedCustom)) return false;
                    if (node.Component)
                    {
                        if (!forBounds && value is BrepFace face)
                        {
                            foreach (var meshType in new[] { MeshType.Render, MeshType.Analysis, MeshType.Preview })
                            {
                                var mesh = face.GetMesh(meshType);
                                if (mesh != null) pending.Push(new Node(mesh, false, node.Depth + 1, true, (int)meshType));
                            }
                        }
                        continue;
                    }
                    if (type == typeof(Brep))
                    {
                        var brep = (Brep)value;
                        int c2 = brep.Curves2D.Count, c3 = brep.Curves3D.Count, surfaces = brep.Surfaces.Count;
                        int vertices = brep.Vertices.Count, edges = brep.Edges.Count, trims = brep.Trims.Count;
                        int loops = brep.Loops.Count, faces = brep.Faces.Count;
                        int total = c2 + c3 + surfaces + vertices + edges + trims + loops + faces;
                        if (c2 > MaxCollection || c3 > MaxCollection || surfaces > MaxCollection || vertices > MaxCollection ||
                            edges > MaxCollection || trims > MaxCollection || loops > MaxCollection || faces > MaxCollection ||
                            total > MaxComponents - nodes || total > rootRemaining || total > budget.Remaining)
                        { budget.MarkLimit(); return false; }
                        if (faces == 0 && edges == 0) return false;
                        // Collection indexers may allocate wrappers for their whole
                        // collection; counts and the shared visit limit precede access.
                        for (int i = faces - 1; i >= 0; i--) pending.Push(new Node(brep.Faces[i], true, node.Depth + 1));
                        for (int i = loops - 1; i >= 0; i--) pending.Push(new Node(brep.Loops[i], true, node.Depth + 1));
                        for (int i = trims - 1; i >= 0; i--) pending.Push(new Node(brep.Trims[i], true, node.Depth + 1));
                        for (int i = edges - 1; i >= 0; i--) pending.Push(new Node(brep.Edges[i], true, node.Depth + 1));
                        for (int i = vertices - 1; i >= 0; i--) pending.Push(new Node(brep.Vertices[i], true, node.Depth + 1));
                        for (int i = surfaces - 1; i >= 0; i--) pending.Push(new Node(brep.Surfaces[i], false, node.Depth + 1));
                        for (int i = c3 - 1; i >= 0; i--) pending.Push(new Node(brep.Curves3D[i], false, node.Depth + 1));
                        for (int i = c2 - 1; i >= 0; i--) pending.Push(new Node(brep.Curves2D[i], false, node.Depth + 1));
                    }
                    else if (type == typeof(PolyCurve))
                    {
                        var poly = (PolyCurve)value;
                        int count = poly.SegmentCount;
                        if (count > MaxCollection || count > MaxComponents - nodes || count > rootRemaining || count > budget.Remaining)
                        { budget.MarkLimit(); return false; }
                        for (int i = count - 1; i >= 0; i--)
                        {
                            var child = poly.SegmentCurve(i);
                            if (child == null) return false;
                            pending.Push(new Node(child, false, node.Depth + 1));
                        }
                    }
                    else if (type == typeof(RevSurface))
                    {
                        var curve = ((RevSurface)value).Curve;
                        if (curve == null) return false;
                        pending.Push(new Node(curve, false, node.Depth + 1));
                    }
                    else if (type == typeof(SubD))
                    {
                        var subd = (SubD)value;
                        if (subd.Vertices.Count > MaxComponents || subd.Edges.Count > MaxComponents || subd.Faces.Count > MaxComponents)
                        { budget.MarkLimit(); return false; }
                    }
                    else if (type == typeof(PointCloud))
                    {
                        // Count was checked before metadata inspection above.
                    }
                    else if (type == typeof(Extrusion))
                    {
                        var extrusion = (Extrusion)value;
                        // Public cached meshes can carry UserData independently
                        // of the extrusion. Profile internals are not exposed as
                        // borrowed geometry; native duplication remains best effort.
                        if (!forBounds)
                            foreach (var meshType in new[] { MeshType.Render, MeshType.Analysis, MeshType.Preview })
                            {
                                var mesh = extrusion.GetMesh(meshType);
                                if (mesh != null) pending.Push(new Node(mesh, false, node.Depth + 1, true, (int)meshType));
                            }
                    }
                    else if (type == typeof(NurbsCurve))
                    {
                        var curve = (NurbsCurve)value;
                        int dimension = curve.Dimension, order = curve.Order, count = curve.Points.Count, knots = curve.Knots.Count;
                        if ((dimension != 2 && dimension != 3) || order < 2 || order > 64 || count < order ||
                            count > MaxLegacyCurvePoints || (long)knots != (long)count + order - 2)
                        { budget.MarkLimit(); return false; }
                    }
                    else if (type == typeof(NurbsSurface))
                    {
                        var surface = (NurbsSurface)value;
                        // These count/order getters use const pointers in the
                        // inspected Rhino 8.36 build. Do not use PointSize on a
                        // borrowed Brep surface: it can create a native duplicate.
                        if (!PanelNurbsLimits.Surface(surface.OrderU, surface.OrderV, surface.Points.CountU, surface.Points.CountV,
                            surface.KnotsU.Count, surface.KnotsV.Count))
                        { budget.MarkLimit(); return false; }
                    }
                    else if (type == typeof(PolylineCurve))
                    {
                        int count = ((PolylineCurve)value).PointCount;
                        if (count < 2 || count > MaxLegacyCurvePoints) { budget.MarkLimit(); return false; }
                    }
                    else if (type == typeof(Mesh))
                    {
                        var mesh = (Mesh)value;
                        if (mesh.Vertices.Count > PanelPreviewLeaf.MaxMeshVertices || mesh.Faces.Count > PanelPreviewLeaf.MaxMeshFaces ||
                            mesh.Normals.Count > PanelPreviewLeaf.MaxMeshVertices || mesh.VertexColors.Count > PanelPreviewLeaf.MaxMeshVertices ||
                            mesh.TextureCoordinates.Count > PanelPreviewLeaf.MaxMeshVertices)
                        { budget.MarkLimit(); return false; }
                    }
                    else if (type != typeof(Point) && type != typeof(LineCurve) && type != typeof(ArcCurve) &&
                        type != typeof(PlaneSurface))
                    { budget.MarkUnavailable("unsupported nested geometry: " + type.Name); return false; }
                }
                // SizeOf may enter native code, so call it only after all exposed
                // nested UserData is classified and before duplicating geometry.
                if (forBounds) { scan = new Scan(0, Array.Empty<byte>(), false); return true; }
                long nativeBytes = 0;
                foreach (var item in observed)
                {
                    long size = item.MemoryEstimate();
                    if (size < 0) return false;
                    // This is an additional heuristic, not proof of a bound on
                    // malformed hidden adjacency: SizeOf is not a count API and
                    // its native implementation may overflow or change.
                    if (item is BrepVertex && size > MaxVertexEstimate) { budget.MarkLimit(); return false; }
                    if (nativeBytes > MaxNativeBytes - size) { budget.MarkLimit(); return false; }
                    nativeBytes += size;
                }
                if (nativeBytes < 0 || nativeBytes > MaxNativeBytes) return false;
                scan = new Scan(nativeBytes, stream.ToArray(), hasMetadata, unverifiedCustom);
                return true;
            }
            catch (Exception) { budget.MarkUnavailable("native geometry inspection failed"); return false; }
        }

        internal static bool SameMetadata(Scan a, Scan b)
        {
            if (a.Metadata.Length != b.Metadata.Length) return false;
            for (int i = 0; i < a.Metadata.Length; i++) if (a.Metadata[i] != b.Metadata[i]) return false;
            return true;
        }

        private static bool WriteMetadata(GeometryBase geometry, BinaryWriter writer, PanelGeometrySources.Budget budget,
            bool allowCustom, Func<bool>? current, ref int bytes, ref bool hasMetadata, ref bool unverifiedCustom)
        {
            if (!geometry.HasUserData)
            {
                if (bytes >= MaxMetadataBytes) { budget.MarkLimit(); return false; }
                writer.Write((byte)0); bytes++; return true;
            }
            int count = geometry.UserData.Count, stringCount = geometry.UserStringCount;
            if (count < 0 || count > 128 || stringCount < 0 || stringCount > 128) { budget.MarkLimit(); return false; }
            var dataItems = new List<UserData>();
            int nativeCount = 0;
            for (int i = 0; i < count; i++)
            {
                // Rhino returns null for native UserData without a managed serial.
                var item = geometry.UserData[i];
                if (item == null) nativeCount++; else dataItems.Add(item);
            }
            int opaqueNative = nativeCount - (stringCount > 0 ? 1 : 0);
            if (opaqueNative < 0) { budget.MarkUnavailable("inconsistent native user data"); return false; }
            if (opaqueNative > 0 && !allowCustom)
            { budget.MarkUnavailable("native user data requires explicit copy"); return false; }
            if (opaqueNative > 0) unverifiedCustom = true;
            dataItems.Sort((a, b) => string.CompareOrdinal(a.GetType().AssemblyQualifiedName, b.GetType().AssemblyQualifiedName));
            if ((long)bytes + 13 > MaxMetadataBytes) { budget.MarkLimit(); return false; }
            writer.Write((byte)3); writer.Write(stringCount); writer.Write(dataItems.Count); writer.Write(opaqueNative); bytes += 13; hasMetadata = true;
            if (stringCount > 0)
            {
                // The API materializes all strings before lengths can be checked;
                // this is a best-effort compatibility path, not a peak bound.
                var strings = geometry.GetUserStrings();
                var keys = strings.AllKeys;
                if (keys.Length != stringCount) return false;
                Array.Sort(keys, StringComparer.Ordinal);
                foreach (string? key in keys)
                {
                    if (key == null) return false;
                    var values = strings.GetValues(key);
                    if (values == null || values.Length != 1 || values[0] == null ||
                        !WriteString(writer, key, budget, ref bytes) || !WriteString(writer, values[0], budget, ref bytes)) return false;
                }
            }
            foreach (var data in dataItems)
            {
                if (data.GetType() == SharedDictionaryType)
                {
                    if (!WriteDictionary(geometry, data, writer, budget, allowCustom, ref bytes, ref unverifiedCustom)) return false;
                    continue;
                }
                if (!allowCustom) { budget.MarkUnavailable("custom user data requires explicit copy"); return false; }
                // This intentionally permits Rhino's normal managed callbacks for
                // an explicit copy. Content beyond identity/transform is not a
                // complete witness, so success carries an explicit warning.
                bool shouldWrite;
                try { shouldWrite = data.ShouldWrite; }
                catch (Exception) { budget.MarkUnavailable("custom user data callback failed"); return false; }
                if (current != null && !current()) return false;
                if (!shouldWrite) { budget.MarkUnavailable("non-serializable custom user data"); return false; }
                writer.Write((byte)4); bytes++;
                if (!WriteString(writer, data.GetType().AssemblyQualifiedName ?? data.GetType().FullName ?? "unknown", budget, ref bytes) ||
                    !WriteTransform(data, writer, budget, ref bytes)) return false;
                unverifiedCustom = true;
            }
            return true;
        }

        private static bool WriteDictionary(GeometryBase geometry, UserData data, BinaryWriter writer,
            PanelGeometrySources.Budget budget, bool allowCustom, ref int bytes, ref bool unverifiedCustom)
        {
            var dictionary = geometry.UserDictionary;
            if (dictionary == null) return false;
            if (dictionary.Count > 128) { budget.MarkLimit(); return false; }
            var keys = new List<string>(dictionary.Count);
            foreach (string key in dictionary.Keys)
            {
                if (key == null) return false;
                if (key.Length > 512) { budget.MarkLimit(); return false; }
                keys.Add(key);
            }
            keys.Sort(StringComparer.Ordinal);
            if (bytes > MaxMetadataBytes - 10) { budget.MarkLimit(); return false; }
            writer.Write((byte)1); writer.Write(keys.Count); writer.Write(dictionary.Version); bytes += 9;
            string? name = dictionary.Name;
            writer.Write(name != null); bytes++;
            if (name != null && !WriteString(writer, name, budget, ref bytes)) return false;
            if (!WriteTransform(data, writer, budget, ref bytes)) return false;
            foreach (string key in keys)
            {
                if (!WriteString(writer, key, budget, ref bytes) || !dictionary.TryGetValue(key, out object value) || value == null ||
                    !WriteValue(writer, value, budget, allowCustom, ref bytes, ref unverifiedCustom)) return false;
            }
            return bytes <= MaxMetadataBytes;
        }

        private static bool WriteTransform(UserData data, BinaryWriter writer, PanelGeometrySources.Budget budget, ref int bytes)
        {
            if ((long)bytes + 16 * sizeof(double) > MaxMetadataBytes) { budget.MarkLimit(); return false; }
            var transform = data.Transform;
            for (int row = 0; row < 4; row++) for (int column = 0; column < 4; column++) writer.Write(transform[row, column]);
            bytes += 16 * sizeof(double);
            return true;
        }

        private static bool WriteString(BinaryWriter writer, string value, PanelGeometrySources.Budget budget, ref int bytes)
        {
            if (value.Length > 4096 || (long)bytes + value.Length * 3L + 8 > MaxMetadataBytes)
            { budget.MarkLimit(); return false; }
            writer.Write(value); bytes += value.Length * 3 + 8;
            return true;
        }

        private static bool WriteValue(BinaryWriter writer, object value, PanelGeometrySources.Budget budget, bool allowCustom,
            ref int bytes, ref bool unverifiedCustom)
        {
            Type type = value.GetType();
            if (type == typeof(string)) { writer.Write((byte)1); bytes++; return WriteString(writer, (string)value, budget, ref bytes); }
            if (type == typeof(byte[]))
            {
                var data = (byte[])value;
                if (data.Length > 4096 || (long)bytes + data.Length + 5 > MaxMetadataBytes)
                { budget.MarkLimit(); return false; }
                writer.Write((byte)2); writer.Write(data.Length); writer.Write(data); bytes += data.Length + 5; return true;
            }
            if (value is int[] integers)
            {
                if (integers.Length > 4096 || (long)bytes + 5L + integers.Length * sizeof(int) > MaxMetadataBytes)
                { budget.MarkLimit(); return false; }
                writer.Write((byte)9); writer.Write(integers.Length); foreach (int item in integers) writer.Write(item);
                bytes += 5 + integers.Length * sizeof(int); return true;
            }
            if (value is double[] doubles)
            {
                if (doubles.Length > 4096 || (long)bytes + 5L + doubles.Length * sizeof(double) > MaxMetadataBytes)
                { budget.MarkLimit(); return false; }
                writer.Write((byte)10); writer.Write(doubles.Length); foreach (double item in doubles) writer.Write(item);
                bytes += 5 + doubles.Length * sizeof(double); return true;
            }
            if (value is string[] strings)
            {
                if (strings.Length > 128 || (long)bytes + 5 > MaxMetadataBytes) { budget.MarkLimit(); return false; }
                writer.Write((byte)11); writer.Write(strings.Length); bytes += 5;
                foreach (string item in strings) if (item == null || !WriteString(writer, item, budget, ref bytes)) return false;
                return true;
            }
            if ((long)bytes + 17 > MaxMetadataBytes) { budget.MarkLimit(); return false; }
            if (type == typeof(bool)) { writer.Write((byte)3); writer.Write((bool)value); }
            else if (type == typeof(int)) { writer.Write((byte)4); writer.Write((int)value); }
            else if (type == typeof(long)) { writer.Write((byte)5); writer.Write((long)value); }
            else if (type == typeof(double)) { writer.Write((byte)6); writer.Write((double)value); }
            else if (type == typeof(float)) { writer.Write((byte)7); writer.Write((float)value); }
            else if (type == typeof(Guid)) { writer.Write((byte)8); writer.Write(((Guid)value).ToByteArray()); }
            else
            {
                if (!allowCustom) { budget.MarkUnavailable("dictionary value requires explicit copy: " + type.Name); return false; }
                writer.Write((byte)12); bytes++; unverifiedCustom = true;
                return WriteString(writer, type.AssemblyQualifiedName ?? type.FullName ?? "unknown", budget, ref bytes);
            }
            bytes += 17;
            return true;
        }

        private readonly struct Node
        {
            internal Node(GeometryBase value, bool component, int depth, bool optional = false, int optionalTag = 0)
            { Value = value; Component = component; Depth = depth; Optional = optional; OptionalTag = optionalTag; }
            internal GeometryBase Value { get; }
            internal bool Component { get; }
            internal int Depth { get; }
            internal bool Optional { get; }
            internal int OptionalTag { get; }
        }
    }
}
