using System;
using System.Threading;
using Grasshopper;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // Owned, bounded drawing values. No GH holder, wrapper or group survives a
    // generation. Simple geometry is reconstructed from bounded value fields;
    // other exact SDK geometry is duplicated only after nested-data inspection.
    internal sealed class PanelPreviewLeaf : IDisposable
    {
        internal const int MaxPolylinePoints = 2048;
        internal const int MaxMeshVertices = 8192;
        internal const int MaxMeshFaces = 8192;
        internal const int MaxGeneratedBytes = 2 * 1024 * 1024;
        internal const int MaxGeneratedFaces = 16384;
        private static int _liveGeneratedMeshes, _generatedMeshBuilds;
        internal static int LiveGeneratedMeshes => Volatile.Read(ref _liveGeneratedMeshes);
        internal static int GeneratedMeshBuilds => Volatile.Read(ref _generatedMeshBuilds);
        private readonly object _value;
        private readonly GeometryBase? _owned;
        private readonly Mesh? _boxMesh;
        private readonly Action<int> _meshStatus;
        private readonly Func<long, bool>? _canRetireMeshes;
        private readonly Action<Mesh[], int[][]?, long>? _retireMeshes;
        private Mesh[]? _generatedMeshes;
        private int[][]? _generatedFaces;
        private MeshingParameters? _meshParameters;
        private int _meshDensity = -1;
        private long _generatedBytes;
        private bool _meshUnavailable, _cacheLimited;
        private bool _disposed;
        internal int DisposeCount { get; private set; }

        private PanelPreviewLeaf(object value, BoundingBox bounds, long bytes, GeometryBase? owned, Mesh? boxMesh, Action<int> meshStatus,
            Func<long, bool>? canRetireMeshes, Action<Mesh[], int[][]?, long>? retireMeshes)
        { _value = value; Bounds = bounds; Bytes = bytes; _owned = owned; _boxMesh = boxMesh; _meshStatus = meshStatus;
            _canRetireMeshes = canRetireMeshes; _retireMeshes = retireMeshes; }
        internal BoundingBox Bounds { get; }
        internal long Bytes { get; }

        // Estimates include managed/native preparation, display topology and
        // retained storage. Check the aggregate before allocating arrays or
        // invoking any native construction. Individual Rhino calls can still
        // have a platform-dependent peak; this is not a native sandbox.
        internal static bool TryEstimate(object value, PanelGeometrySources.Budget budget, ref int remaining,
            out long bytes, out PanelNativeGeometry.Scan? scan, Func<bool>? current = null)
        {
            bytes = 512;
            scan = null;
            if (value is PanelGeometrySources.StandalonePreview) return true;
            if (value is Point3d || value is Line || value is Arc || value is Circle || value is Rectangle3d) return true;
            if (value is Box) { bytes = 8192; return true; }
            if (value is GeometryBase && Rhino.Runtime.CommonObject.PerformCorruptionTesting) return false;
            if (value is Point) return true;
            if (value is LineCurve || value is ArcCurve) return true;
            if (value is NurbsCurve nurbs)
            {
                bool admitted = PanelNurbsSnapshot.TryEstimate(nurbs, out bytes);
                if (admitted) return true;
            }
            if (value is PolylineCurve poly)
            {
                int count = poly.PointCount;
                if (count < 2 || poly.Dimension != 3) return false;
                if (count <= MaxPolylinePoints) { bytes = 4096L + count * 256L; return true; }
            }
            if (value is Mesh mesh)
            {
                int vertices = mesh.Vertices.Count, faces = mesh.Faces.Count;
                if (vertices > MaxMeshVertices || faces > MaxMeshFaces || mesh.Normals.Count > MaxMeshVertices ||
                    mesh.VertexColors.Count > MaxMeshVertices || mesh.TextureCoordinates.Count > MaxMeshVertices)
                { budget.MarkLimit(); return false; }
                if (vertices <= 0 || faces <= 0) return false;
                bytes = 8192L + (vertices + (long)faces) * 384L;
                return true;
            }
            if (!(value is GeometryBase geometry) || !PanelNativeGeometry.TryScanForPreview(geometry, budget, ref remaining, current, out scan) || scan == null)
                return false;
            // Reserve for source duplication and the largest generated mesh.
            // A single native meshing call can exceed its estimate; the created
            // mesh is checked before it enters the bounded result-local cache.
            bytes = 65536L + scan.NativeBytes * 8L + (geometry is Brep || geometry is Surface || geometry is SubD ? MaxGeneratedBytes + 131072L : 0);
            return bytes <= PanelGeometryHighlight.MaxRetainedBytes;
        }

        internal static PanelPreviewLeaf? Create(object value, long bytes, PanelNativeGeometry.Scan? scan,
            PanelGeometrySources.Budget budget, ref int remaining, Action<int> meshStatus, Func<bool>? current = null,
            Func<long, bool>? canRetireMeshes = null, Action<Mesh[], int[][]?, long>? retireMeshes = null)
        {
            GeometryBase? owned = null;
            Mesh? boxMesh = null;
            try
            {
                if (value is PanelGeometrySources.StandalonePreview custom)
                {
                    var customBounds = custom.Value.ClippingBox;
                    return customBounds.IsValid ? new PanelPreviewLeaf(custom, customBounds, bytes, null, null, meshStatus, canRetireMeshes, retireMeshes) : null;
                }
                if (value is Point point) value = point.Location;
                else if (value is LineCurve lineCurve) value = lineCurve.Line;
                else if (value is ArcCurve arcCurve) value = arcCurve.Arc;
                else if (value is NurbsCurve nurbs && scan == null)
                {
                    owned = PanelNurbsSnapshot.Create(nurbs, bytes);
                    if (owned == null) return null;
                    value = owned;
                }
                else if (value is PolylineCurve poly && scan == null)
                {
                    int count = poly.PointCount;
                    if (count < 2 || count > MaxPolylinePoints || poly.Dimension != 3 || 4096L + count * 256L > bytes) return null;
                    var points = new Point3d[count];
                    for (int i = 0; i < count; i++)
                    {
                        points[i] = poly.Point(i);
                        if (!points[i].IsValid) return null;
                    }
                    owned = new PolylineCurve(points);
                    value = owned;
                }
                else if (value is Mesh source)
                {
                    int vertexCount = source.Vertices.Count, faceCount = source.Faces.Count;
                    int normalCount = source.Normals.Count, colorCount = source.VertexColors.Count, textureCount = source.TextureCoordinates.Count;
                    if (vertexCount <= 0 || vertexCount > MaxMeshVertices || faceCount <= 0 || faceCount > MaxMeshFaces ||
                        normalCount > MaxMeshVertices || colorCount > MaxMeshVertices || textureCount > MaxMeshVertices ||
                        8192L + (vertexCount + (long)faceCount) * 384L > bytes) return null;
                    var copy = new Mesh();
                    owned = copy;
                    copy.Vertices.UseDoublePrecisionVertices = source.Vertices.UseDoublePrecisionVertices;
                    for (int i = 0; i < vertexCount; i++) copy.Vertices.Add(source.Vertices.Point3dAt(i));
                    for (int i = 0; i < faceCount; i++) copy.Faces.AddFace(source.Faces[i]);
                    for (int i = 0; i < normalCount; i++) copy.Normals.Add(source.Normals[i]);
                    for (int i = 0; i < colorCount; i++) copy.VertexColors.Add(source.VertexColors[i]);
                    for (int i = 0; i < textureCount; i++) copy.TextureCoordinates.Add(source.TextureCoordinates[i]);
                    if (!copy.IsValid) return null;
                    if (copy.Normals.Count == 0) copy.Normals.ComputeNormals();
                    value = copy;
                }
                else if (value is Box box)
                {
                    // Six planar faces require a fixed mesh, independent of GH's
                    // meshing settings. No generated mesh cache is retained.
                    boxMesh = Mesh.CreateFromBox(box, 1, 1, 1);
                    if (boxMesh == null) return null;
                }
                else if (value is GeometryBase geometry)
                {
                    if (scan == null) return null;
                    owned = geometry.Duplicate();
                    if (current != null && !current()) return null;
                    if (owned == null || owned.GetType() != geometry.GetType() ||
                        !PanelNativeGeometry.TryScanForPreview(owned, budget, ref remaining, current, out var copiedScan) || copiedScan == null ||
                        !PanelNativeGeometry.SameMetadata(scan, copiedScan)) return null;
                    if (current != null && !current()) return null;
                    // Brep validity can scan unbounded hidden vertex adjacency;
                    // the public managed API does not provide a bounded count.
                    if (owned is not Brep && !owned.IsValid) return null;
                    value = owned;
                }
                BoundingBox bounds = value is Point3d p ? new BoundingBox(p, p) :
                    value is Line l ? l.BoundingBox : value is Arc a ? a.BoundingBox() :
                    value is Circle c ? c.BoundingBox : value is Rectangle3d r ? r.BoundingBox :
                    value is Box b ? b.BoundingBox : value is GeometryBase g ? g.GetBoundingBox(false) : BoundingBox.Empty;
                if (!bounds.IsValid) return null;
                var result = new PanelPreviewLeaf(value, bounds, bytes, owned, boxMesh, meshStatus, canRetireMeshes, retireMeshes);
                owned = null; boxMesh = null;
                return result;
            }
            catch (Exception) { return null; }
            finally { owned?.Dispose(); boxMesh?.Dispose(); }
        }

        internal void DrawWires(GH_PreviewWireArgs args, Func<bool> current)
        {
            if (_value is PanelGeometrySources.StandalonePreview custom) { custom.Value.DrawViewportWires(args); return; }
            if (args.Pipeline == null) return;
            if (_value is Point3d p) args.Pipeline.DrawPoint(p, CentralSettings.PreviewPointStyle, CentralSettings.PreviewPointRadius, args.Color);
            else if (_value is Line line) args.Pipeline.DrawLine(line, args.Color, args.Thickness);
            else if (_value is Arc arc) args.Pipeline.DrawArc(arc, args.Color, args.Thickness);
            else if (_value is Circle circle) args.Pipeline.DrawCircle(circle, args.Color, args.Thickness);
            else if (_value is Rectangle3d rect)
                for (int i = 0; i < 4 && current(); i++) args.Pipeline.DrawLine(rect.Corner(i), rect.Corner((i + 1) % 4), args.Color, args.Thickness);
            else if (_value is Box box) args.Pipeline.DrawBox(box, args.Color, args.Thickness);
            else if (_value is Curve curve) args.Pipeline.DrawCurve(curve, args.Color, args.Thickness);
            else if (_value is Mesh mesh && CentralSettings.PreviewMeshEdges) args.Pipeline.DrawMeshWires(mesh, args.Color, 1);
            else if (_value is Brep brep)
                for (int i = 0; current() && i < brep.Edges.Count; i++) args.Pipeline.DrawCurve(brep.Edges[i], args.Color, 1);
            else if (_value is Surface surface) args.Pipeline.DrawSurface(surface, args.Color, args.Thickness);
            else if (_value is SubD subd)
            {
                try { args.Pipeline.DrawSubDWires(subd, args.Color, args.Thickness); }
                finally { subd.ClearEvaluationCache(); }
            }
        }

        internal void DrawMeshes(GH_PreviewMeshArgs args, Func<bool> current)
        {
            if (_value is PanelGeometrySources.StandalonePreview custom) { custom.Value.DrawViewportMeshes(args); return; }
            if (args.Pipeline == null) return;
            if (_value is Mesh mesh)
            {
                if (mesh.VertexColors.Count > 0) args.Pipeline.DrawMeshFalseColors(mesh);
                else args.Pipeline.DrawMeshShaded(mesh, args.Material);
            }
            else if (_boxMesh != null) args.Pipeline.DrawMeshShaded(_boxMesh, args.Material);
            else if (_value is Brep || _value is Surface || _value is SubD) DrawGeneratedMesh(args, current);
        }

        private void DrawGeneratedMesh(GH_PreviewMeshArgs args, Func<bool> current)
        {
            Mesh[]? meshes = null;
            bool limited = false, counted = false;
            try
            {
                int requestedDensity = -1;
                if (_value is SubD currentSubd)
                {
                    using var settings = args.MeshingParameters.SubDDisplayParameters();
                    requestedDensity = (int)settings.DisplayDensity(currentSubd);
                }
                if (!current()) return;
                if (_generatedMeshes != null && _meshParameters != null && _meshParameters.Equals(args.MeshingParameters) &&
                    (_value is not SubD || requestedDensity == _meshDensity))
                {
                    DrawCachedMeshes(args, current);
                    if (current()) SetMeshUnavailable(_cacheLimited);
                    return;
                }
                if (!current()) return;
                if (_generatedMeshes != null && _retireMeshes != null && (_canRetireMeshes == null || !_canRetireMeshes(_generatedBytes)))
                { SetMeshUnavailable(true); return; }
                ClearGeneratedMeshes(defer: true);
                if (!current()) return;
                using var parameters = new MeshingParameters(args.MeshingParameters);
                // Avoid pathological custom minimum grids and edge lengths before
                // a native meshing allocation. Compare a private parameter copy
                // on every draw to invalidate a cache when GH settings change.
                int requestedMax = parameters.GridMaxCount;
                if (parameters.GridMinCount > 128) { parameters.GridMinCount = 128; limited = true; }
                if (requestedMax <= 0 || requestedMax > 128) { parameters.GridMaxCount = 128; limited |= requestedMax > 128; }
                double diagonal = Bounds.Diagonal.Length;
                if (parameters.MaximumEdgeLength > 0 && diagonal / parameters.MaximumEdgeLength > 512)
                { SetMeshUnavailable(true); return; }
                if (parameters.Tolerance > 0 && diagonal / parameters.Tolerance > 65536)
                { SetMeshUnavailable(true); return; }
                if (_value is Brep brep) meshes = Mesh.CreateFromBrep(brep, parameters);
                else if (_value is Surface surface)
                {
                    var mesh = Mesh.CreateFromSurface(surface, parameters);
                    if (mesh != null) meshes = new[] { mesh };
                }
                else if (_value is SubD subd)
                {
                    int density = Math.Max(0, Math.Min(requestedDensity, 3));
                    while (density > 0 && subd.Faces.Count * (1L << (2 * density)) > MaxGeneratedFaces) density--;
                    limited |= density < requestedDensity;
                    var mesh = Mesh.CreateFromSubD(subd, density);
                    if (mesh != null) meshes = new[] { mesh };
                }
                if (!current()) return;
                if (meshes == null || meshes.Length == 0 || meshes.Length > PanelNativeGeometry.MaxCollection)
                { SetMeshUnavailable(true); return; }
                foreach (var mesh in meshes) if (mesh != null) Interlocked.Increment(ref _liveGeneratedMeshes);
                counted = true;
                long bytes = 0, faces = 0;
                foreach (var mesh in meshes)
                {
                    if (mesh == null) { SetMeshUnavailable(true); return; }
                    bytes += mesh.MemoryEstimate(); faces += mesh.Faces.Count;
                    if (bytes < 0 || bytes > MaxGeneratedBytes || faces > MaxGeneratedFaces || mesh.Vertices.Count > MaxGeneratedFaces * 2)
                    { SetMeshUnavailable(true); return; }
                }
                if (faces == 0) { SetMeshUnavailable(true); return; }
                if (bytes + faces * sizeof(int) > MaxGeneratedBytes) { SetMeshUnavailable(true); return; }
                var faceIndices = new int[meshes.Length][];
                for (int index = 0; index < meshes.Length; index++)
                {
                    var facesToDraw = new int[meshes[index].Faces.Count];
                    for (int i = 0; i < facesToDraw.Length; i++) facesToDraw[i] = i;
                    faceIndices[index] = facesToDraw;
                }
                var requestedParameters = new MeshingParameters(args.MeshingParameters);
                _meshParameters = requestedParameters; _meshDensity = requestedDensity;
                _generatedMeshes = meshes; _generatedFaces = faceIndices; _cacheLimited = limited;
                _generatedBytes = bytes + faces * sizeof(int);
                meshes = null; counted = false; // ownership moved to this entry
                Interlocked.Increment(ref _generatedMeshBuilds);
                SetMeshUnavailable(limited);
                if (current()) DrawCachedMeshes(args, current);
            }
            catch (Exception) { SetMeshUnavailable(true); }
            finally
            {
                if (meshes != null) foreach (var mesh in meshes)
                {
                    if (mesh == null) continue;
                    try { mesh.Dispose(); } catch (Exception) { }
                    finally { if (counted) Interlocked.Decrement(ref _liveGeneratedMeshes); }
                }
                if (_value is SubD subd) try { subd.ClearEvaluationCache(); } catch (Exception) { }
            }
        }

        private void DrawCachedMeshes(GH_PreviewMeshArgs args, Func<bool> current)
        {
            if (_generatedMeshes == null || _generatedFaces == null) return;
            for (int i = 0; current() && i < _generatedMeshes.Length; i++)
            {
                // Use the face-index overload for the owned cache. The native
                // pipeline's lifetime across a later Flush still needs host proof.
                args.Pipeline.DrawMeshShaded(_generatedMeshes[i], args.Material, _generatedFaces[i]);
            }
        }

        private void ClearGeneratedMeshes(bool defer)
        {
            var meshes = _generatedMeshes;
            var faces = _generatedFaces;
            long bytes = _generatedBytes;
            _generatedMeshes = null; _generatedFaces = null; _meshDensity = -1; _cacheLimited = false;
            _generatedBytes = 0;
            _meshParameters?.Dispose(); _meshParameters = null;
            if (meshes == null) return;
            if (defer && _retireMeshes != null) { _retireMeshes(meshes, faces, bytes); return; }
            DisposeGeneratedMeshes(meshes);
        }

        internal static void DisposeGeneratedMeshes(Mesh[] meshes)
        {
            foreach (var mesh in meshes)
            {
                try { mesh.Dispose(); } catch (Exception) { }
                finally { Interlocked.Decrement(ref _liveGeneratedMeshes); }
            }
        }

        private void SetMeshUnavailable(bool unavailable)
        {
            if (_meshUnavailable == unavailable) return;
            _meshUnavailable = unavailable;
            _meshStatus(unavailable ? 1 : -1);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisposeCount++;
            SetMeshUnavailable(false);
            ClearGeneratedMeshes(defer: false);
            try { _owned?.Dispose(); }
            finally { _boxMesh?.Dispose(); }
        }
    }
}
