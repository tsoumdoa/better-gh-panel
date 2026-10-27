using System;
using System.Collections.Generic;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // One result-local generation supplies bounds, labels and drawing. Only
    // independent third-party preview is borrowed (the GH best-effort contract).
    internal sealed class PanelGeometryHighlight
    {
        internal const int MaxEntries = 1024;
        internal const int MaxBuildRows = 20000;
        internal const int MaxUpdateRows = 128;
        internal const long MaxRetainedBytes = 16L * 1024 * 1024;
        private readonly SortedDictionary<int, Entry> _entries = new SortedDictionary<int, Entry>();
        private readonly Dictionary<int, Entry> _byPosition = new Dictionary<int, Entry>();
        private readonly SortedSet<int> _unselected = new SortedSet<int>();
        private readonly HashSet<int> _attempted = new HashSet<int>();
        private readonly HashSet<int> _unavailablePositions = new HashSet<int>();
        private readonly Queue<int> _attemptOrder = new Queue<int>();
        private readonly Queue<Entry> _retired = new Queue<Entry>();
        private readonly PanelPreviewMembership _membership = new PanelPreviewMembership();
        private readonly PanelOrderLabels _orderLabels;
        private readonly BoundsIndex _bounds = new BoundsIndex();
        private Action<int, object>? _admissionObserver;
        private IReadOnlyList<IGH_Goo?>? _source;
        private IReadOnlyList<int>? _legacyPositions;
        private PanelQueryResult? _result, _orderResult;
        private bool _filtered, _partial;
        private string? _partialReason;
        private long _generation;
        private object _scopeToken = new object();
        private int _selectedCount, _unavailable, _meshFailures;
        private long _bytes;
        private long _retiredBytes;
        private int _activeDraws;
        private bool _flushingRetired;

        internal PanelGeometryHighlight() : this(null) { }
        // A fixture can inject a reentrant callback at the exact ownership seam.
        internal PanelGeometryHighlight(Action<int, object>? admissionObserver)
        { _admissionObserver = admissionObserver; _orderLabels = new PanelOrderLabels(_membership); }
        internal int Count => _selectedCount;
        internal int PreviewCount => _entries.Count;
        internal long RetainedBytes => _bytes + _retiredBytes;
        internal int LastVisitedRows { get; private set; }
        internal int LastVisitedNodes { get; private set; }
        internal BoundingBox ClippingBox => _bounds.Box;
        internal string Status => _partial || _unavailable > 0 || _meshFailures > 0
            ? "Preview partial: " + (_partialReason ?? "some geometry is unsupported or exceeds safety limits") + "."
            : string.Empty;

        internal void SetOrderResult(PanelQueryResult? result)
        {
            if (ReferenceEquals(_orderResult, result)) return;
            _generation++;
            _orderResult = result;
            _orderLabels.ClearEntries();
            _orderLabels.SetResult(result);
            if (result != null)
                foreach (var entry in _entries.Values) _orderLabels.Add(entry.Order, entry.Position, entry.Bounds.Center);
        }

        internal void SetScope(IReadOnlyList<IGH_Goo?> sourceItems, PanelQueryResult result, bool filtered, IReadOnlyList<int> selectedPositions)
            => SetScopeChecked(sourceItems, result, filtered, selectedPositions, () => true);

        internal void SetScopeChecked(IReadOnlyList<IGH_Goo?> sourceItems, PanelQueryResult result, bool filtered,
            IReadOnlyList<int> selectedPositions, Func<bool> current, PanelRowSelection? selection = null)
        {
            long generation = ++_generation;
            bool fresh = !ReferenceEquals(_source, sourceItems) || !ReferenceEquals(_result, result) || _filtered != filtered || _legacyPositions != null;
            if (fresh)
            {
                ClearGeometry();
                if (!Current(generation, current)) return;
                _source = sourceItems; _result = result; _filtered = filtered; _legacyPositions = null;
            }
            if (!filtered) _partial = false;
            LastVisitedRows = LastVisitedNodes = 0;
            var update = UpdateMembership(selectedPositions, result, selection);
            if (!Current(generation, current)) return;
            var budget = new PanelGeometrySources.Budget();
            int visited = 0;
            // A newly selected row can evict an unselected preview. This allows
            // later selections to recover without another source solution.
            if (update.ScanSelection) foreach (int position in selectedPositions)
            {
                if (_byPosition.ContainsKey(position) || _attempted.Contains(position)) continue;
                if (++visited > MaxUpdateRows || budget.Remaining <= 0) { _partial = true; break; }
                LastVisitedRows++;
                if (result.TryGetViewIndex(position, out int order)) TryAdmit(position, order, budget, true, generation, current);
                if (!Current(generation, current)) return;
            }
            else if (update.AddedPosition >= 0 && !_byPosition.ContainsKey(update.AddedPosition) && !_attempted.Contains(update.AddedPosition))
            {
                LastVisitedRows++;
                if (result.TryGetViewIndex(update.AddedPosition, out int order))
                    TryAdmit(update.AddedPosition, order, budget, true, generation, current);
                if (!Current(generation, current)) return;
            }
            if (fresh && filtered)
            {
                int rows = 0;
                foreach (int position in result.RowPositions)
                {
                    if (++rows > MaxBuildRows || budget.Remaining <= 0 || _entries.Count >= MaxEntries || _bytes >= MaxRetainedBytes)
                    { _partial = true; break; }
                    LastVisitedRows++;
                    if (_byPosition.ContainsKey(position) || _attempted.Contains(position)) continue;
                    TryAdmit(position, rows - 1, budget, false, generation, current);
                    if (!Current(generation, current)) return;
                }
            }
            LastVisitedNodes = budget.Visits;
        }

        // Kept for native component-contract fixtures and callers without a query result.
        internal void Set(IReadOnlyList<IGH_Goo?> sourceItems, IReadOnlyList<int> previewPositions, IReadOnlyList<int> selectedPositions)
        {
            long generation = ++_generation;
            ClearGeometry();
            if (generation != _generation) return;
            _source = sourceItems; _legacyPositions = previewPositions; _filtered = true;
            LastVisitedRows = LastVisitedNodes = 0;
            UpdateMembership(selectedPositions, null, null);
            var budget = new PanelGeometrySources.Budget();
            for (int i = 0; i < previewPositions.Count; i++)
            {
                if (i >= MaxBuildRows || budget.Remaining <= 0 || _entries.Count >= MaxEntries) { _partial = true; break; }
                LastVisitedRows++;
                TryAdmit(previewPositions[i], i, budget, false, generation, () => true);
                if (generation != _generation) return;
            }
            LastVisitedNodes = budget.Visits;
        }

        private PanelPreviewMembership.SelectionUpdate UpdateMembership(IReadOnlyList<int> selected, PanelQueryResult? result,
            PanelRowSelection? selection)
        {
            var removed = new List<Entry>();
            void Changed(int position, bool isSelected)
            {
                if (_byPosition.TryGetValue(position, out var entry))
                {
                    if (isSelected) { _unselected.Remove(entry.Order); _selectedCount++; }
                    else
                    {
                        if (_filtered) { _selectedCount--; _unselected.Add(entry.Order); }
                        else { Remove(entry, false); removed.Add(entry); }
                    }
                }
                if (!isSelected && !_filtered && _unavailablePositions.Remove(position))
                { _attempted.Remove(position); _unavailable--; }
            }
            PanelPreviewMembership.SelectionUpdate update;
            if (result == null) { _membership.Update(selected, Changed); update = new PanelPreviewMembership.SelectionUpdate(true, -1); }
            else update = _membership.UpdateSelection(selection, result, selected, Changed);
            // Disposal can call external code. Complete the membership update
            // first, then release detached entries without enumerating live state.
            foreach (var entry in removed) Retire(entry);
            FlushRetired();
            return update;
        }

        private bool Current(long generation, Func<bool> current)
        {
            if (generation != _generation) return false;
            if (current()) return true;
            // The component can change selection or source while a native or
            // standalone callback is running, without starting a nested build.
            Clear();
            return false;
        }

        private void TryAdmit(int position, int order, PanelGeometrySources.Budget budget, bool mayEvict, long generation, Func<bool> current)
        {
            try { TryAdmitCore(position, order, budget, mayEvict, generation, current); }
            catch (Exception) { if (Current(generation, current)) RememberUnavailable(position); }
        }

        private void TryAdmitCore(int position, int order, PanelGeometrySources.Budget budget, bool mayEvict, long generation, Func<bool> current)
        {
            if (_source == null || position < 0 || position >= _source.Count || _byPosition.ContainsKey(position)) return;
            var source = _source;
            var root = source[position];
            var scopeToken = _scopeToken;
            var inspected = PanelGeometrySources.Inspect(root, budget, true);
            if (!Current(generation, current)) return;
            if (inspected.Unavailable)
            {
                if (inspected.Exhausted) _partial = true;
                else RememberUnavailable(position);
                return;
            }
            if (inspected.Leaves.Count == 0) { Remember(position); return; }
            long estimate = 0;
            int remaining = inspected.Remaining;
            var estimates = new List<long>(inspected.Leaves.Count);
            var scans = new List<PanelNativeGeometry.Scan?>(inspected.Leaves.Count);
            foreach (object leaf in inspected.Leaves)
            {
                if (!PanelPreviewLeaf.TryEstimate(leaf, budget, ref remaining, out long bytes, out var scan) || (estimate += bytes) > MaxRetainedBytes)
                {
                    _partialReason ??= budget.Reason;
                    if (budget.Limited) _partial = true;
                    else RememberUnavailable(position);
                    return;
                }
                if (!Current(generation, current)) return;
                estimates.Add(bytes);
                scans.Add(scan);
            }
            while (mayEvict && (_entries.Count >= MaxEntries || _bytes + _retiredBytes + estimate > MaxRetainedBytes) && _unselected.Count > 0)
            {
                Remove(_entries[_unselected.Max]);
                if (!Current(generation, current)) return;
            }
            if (_entries.Count >= MaxEntries || _bytes + _retiredBytes + estimate > MaxRetainedBytes)
            { _partial = true; _partialReason ??= "preview memory or entry limit"; return; }
            var leaves = new List<PanelPreviewLeaf>(inspected.Leaves.Count);
            var bounds = BoundingBox.Empty;
            bool admitted = false;
            try
            {
                for (int i = 0; i < inspected.Leaves.Count; i++)
                {
                    // Every leaf comes from this one bounded graph capture. Do
                    // not resolve a later member from the live group here.
                    var copy = PanelPreviewLeaf.Create(inspected.Leaves[i], estimates[i], scans[i], budget, ref remaining,
                        delta => { if (ReferenceEquals(scopeToken, _scopeToken)) MeshStatusChanged(delta); },
                        () => Current(generation, current));
                    if (copy == null)
                    {
                        if (Current(generation, current))
                        {
                            _partialReason ??= budget.Reason ?? "native preview preparation failed";
                            if (budget.Limited) _partial = true; else RememberUnavailable(position);
                        }
                        return;
                    }
                    leaves.Add(copy);
                    _admissionObserver?.Invoke(i, copy);
                    if (!Current(generation, current)) return;
                    bounds.Union(copy.Bounds);
                }
                var fresh = PanelGeometrySources.Inspect(root, budget, true);
                if (!Current(generation, current)) return;
                if (!PanelGeometrySources.SameGraph(inspected, fresh))
                { _partial = true; _partialReason ??= fresh.Exhausted ? "inspection limit" : "group or wrapper changed during preparation"; return; }
                var entry = new Entry(position, order, bounds, estimate, leaves);
                _entries.Add(order, entry); _byPosition.Add(position, entry);
                _bytes += estimate; _bounds.Add(bounds);
                if (_membership.Contains(position)) _selectedCount++; else _unselected.Add(order);
                _orderLabels.Add(order, position, bounds.Center);
                admitted = true;
            }
            finally
            {
                if (!admitted) foreach (var leaf in leaves) leaf.Dispose();
            }
        }

        private void MeshStatusChanged(int delta)
        {
            _meshFailures += delta;
            Instances.ActiveCanvas?.Invalidate();
        }

        private void Remove(Entry entry, bool dispose = true)
        {
            _entries.Remove(entry.Order); _byPosition.Remove(entry.Position); _unselected.Remove(entry.Order);
            _orderLabels.Remove(entry.Order);
            if (_membership.Contains(entry.Position)) _selectedCount--;
            _bytes -= entry.Bytes; _bounds.Remove(entry.Bounds);
            if (dispose) { Retire(entry); FlushRetired(); }
        }

        private void Retire(Entry entry) { _retired.Enqueue(entry); _retiredBytes += entry.Bytes; }

        private void FlushRetired()
        {
            if (_activeDraws != 0 || _flushingRetired) return;
            _flushingRetired = true;
            try
            {
                while (_retired.Count > 0 && _activeDraws == 0)
                {
                    var entry = _retired.Dequeue();
                    try { foreach (var leaf in entry.Leaves) try { leaf.Dispose(); } catch (Exception) { } }
                    finally { _retiredBytes -= entry.Bytes; }
                }
            }
            finally { _flushingRetired = false; }
        }

        private void RememberUnavailable(int position)
        {
            if (_unavailablePositions.Add(position)) _unavailable++;
            Remember(position);
        }

        private void Remember(int position)
        {
            if (!_attempted.Add(position)) return;
            _attemptOrder.Enqueue(position);
            while (_attemptOrder.Count > MaxBuildRows)
            {
                int old = _attemptOrder.Dequeue();
                if (_attempted.Remove(old) && _unavailablePositions.Remove(old)) _unavailable--;
            }
        }

        internal void Clear() { _generation++; _orderLabels.Clear(); _orderResult = null; ClearGeometry(); }
        private void ClearGeometry()
        {
            var old = new List<Entry>(_entries.Values);
            _scopeToken = new object();
            _entries.Clear(); _byPosition.Clear(); _unselected.Clear(); _attempted.Clear(); _unavailablePositions.Clear(); _attemptOrder.Clear(); _membership.Clear();
            _bounds.Clear(); _orderLabels.ClearEntries(); _selectedCount = _unavailable = _meshFailures = 0; _bytes = 0; _partial = false; _partialReason = null;
            _source = null; _result = null; _legacyPositions = null;
            foreach (var entry in old) Retire(entry);
            FlushRetired();
        }

        internal void DrawWires(IGH_PreviewArgs args, bool componentSelected)
        {
            // A nested display callback can expire or replace this generation.
            // Keep its owned resources alive until the outer callback returns,
            // and suppress a nested draw which could reset an in-use mesh cache.
            if (_activeDraws != 0) return;
            _activeDraws++;
            try
            {
            long generation = _generation;
            var normal = new GH_PreviewWireArgs(args.Viewport, args.Display, args.WireColour, args.DefaultCurveThickness);
            int thickness = args.DefaultCurveThickness + (componentSelected ? 0 : CentralSettings.PreviewSelectionThickening);
            var selected = new GH_PreviewWireArgs(args.Viewport, args.Display, args.WireColour_Selected, thickness);
            DrawWires(normal, false);
            if (generation != _generation) return;
            bool pushed = false;
            try
            {
                if (!componentSelected && _selectedCount > 0 && args.Display != null)
                {
                    var clipBias = Transform.Identity; clipBias.M22 = 0.995; clipBias.M23 = 0.01;
                    var bias = args.Viewport.GetTransform(CoordinateSystem.Clip, CoordinateSystem.World) * clipBias *
                        args.Viewport.GetTransform(CoordinateSystem.World, CoordinateSystem.Clip);
                    args.Display.PushModelTransform(bias * args.Display.ModelTransform); pushed = true;
                }
                DrawWires(selected, true);
            }
            finally { if (pushed) args.Display.PopModelTransform(); }
            if (generation != _generation) return;
            _orderLabels.Arm(args);
            }
            finally { _activeDraws--; FlushRetired(); }
        }

        private void DrawWires(GH_PreviewWireArgs args, bool selected)
        {
            long generation = _generation;
            foreach (var entry in _entries.Values)
            {
                if (_membership.Contains(entry.Position) != selected) continue;
                foreach (var leaf in entry.Leaves)
                {
                    try { leaf.DrawWires(args, () => generation == _generation); } catch (Exception) { }
                    if (generation != _generation) return;
                }
            }
        }

        internal void DrawMeshes(IGH_PreviewArgs args)
        {
            if (args.MeshingParameters == null) return;
            if (_activeDraws != 0) return;
            _activeDraws++;
            try
            {
            long generation = _generation;
            var normal = new GH_PreviewMeshArgs(args.Viewport, args.Display, args.ShadeMaterial, args.MeshingParameters);
            var selected = new GH_PreviewMeshArgs(args.Viewport, args.Display, args.ShadeMaterial_Selected, args.MeshingParameters);
            // Fixed meshes need no remeshing. Brep/Surface/SubD entries manage
            // their own bounded meshes and invalidate them on setting changes.
            foreach (var entry in _entries.Values)
                foreach (var leaf in entry.Leaves)
                {
                    try { leaf.DrawMeshes(_membership.Contains(entry.Position) ? selected : normal, () => generation == _generation); } catch (Exception) { }
                    if (generation != _generation) return;
                }
            }
            finally { _activeDraws--; FlushRetired(); }
        }

        private sealed class Entry
        {
            internal Entry(int position, int order, BoundingBox bounds, long bytes, List<PanelPreviewLeaf> leaves)
            { Position = position; Order = order; Bounds = bounds; Bytes = bytes; Leaves = leaves; }
            internal readonly int Position, Order;
            internal readonly BoundingBox Bounds;
            internal readonly long Bytes;
            internal readonly List<PanelPreviewLeaf> Leaves;
        }

        // Incremental extrema avoid a full admitted-row scan on a sparse eviction.
        private sealed class BoundsIndex
        {
            private readonly Axis _x = new Axis(), _y = new Axis(), _z = new Axis();
            internal BoundingBox Box => _x.Empty ? BoundingBox.Empty : new BoundingBox(
                new Point3d(_x.Min, _y.Min, _z.Min), new Point3d(_x.Max, _y.Max, _z.Max));
            internal void Add(BoundingBox b) { _x.Add(b.Min.X, b.Max.X); _y.Add(b.Min.Y, b.Max.Y); _z.Add(b.Min.Z, b.Max.Z); }
            internal void Remove(BoundingBox b) { _x.Remove(b.Min.X, b.Max.X); _y.Remove(b.Min.Y, b.Max.Y); _z.Remove(b.Min.Z, b.Max.Z); }
            internal void Clear() { _x.Clear(); _y.Clear(); _z.Clear(); }
            private sealed class Axis
            {
                private readonly SortedSet<double> _values = new SortedSet<double>();
                private readonly Dictionary<double, int> _counts = new Dictionary<double, int>();
                internal bool Empty => _values.Count == 0;
                internal double Min => _values.Min;
                internal double Max => _values.Max;
                internal void Add(double a, double b) { Add(a); Add(b); }
                internal void Remove(double a, double b) { Remove(a); Remove(b); }
                internal void Clear() { _values.Clear(); _counts.Clear(); }
                private void Add(double value) { if (!_counts.TryGetValue(value, out int count)) _values.Add(value); _counts[value] = count + 1; }
                private void Remove(double value) { int count = _counts[value]; if (count == 1) { _counts.Remove(value); _values.Remove(value); } else _counts[value] = count - 1; }
            }
        }
    }
}
