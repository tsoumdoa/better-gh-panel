using System;
using System.Collections.Generic;
using Rhino.Display;
using BetterGhPanel.Core;
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
        internal int PreviewLimit { get; private set; } = 5000;
        internal PanelDotSize DotSize { get => _orderLabels.DotSize; set => _orderLabels.DotSize = value; }
        internal void SetPreviewLimit(int limit)
        {
            if (!PanelDotSettings.ValidLimit(limit) || PreviewLimit == limit) return;
            PreviewLimit = limit;
            // Rebuild the scope on the next SetScopeChecked, including recovery and
            // attempted roots. Deferred native resource retirement remains guarded.
            _generation++;
            ClearGeometry();
        }
        internal const int MaxBuildRows = 20000;
        internal const int MaxUpdateRows = 128;
        internal const long MaxRetainedBytes = 64L * 1024 * 1024;
        private const int MinSelectionThickening = 2;
        private readonly SortedDictionary<int, Entry> _entries = new SortedDictionary<int, Entry>();
        private readonly Dictionary<int, Entry> _byPosition = new Dictionary<int, Entry>();
        private readonly SortedSet<int> _unselected = new SortedSet<int>();
        private readonly HashSet<int> _attempted = new HashSet<int>();
        private readonly HashSet<int> _unavailablePositions = new HashSet<int>();
        private readonly Queue<int> _attemptOrder = new Queue<int>();
        private readonly Queue<Entry> _retired = new Queue<Entry>();
        private readonly Queue<RetiredMeshes> _retiredMeshes = new Queue<RetiredMeshes>();
        private readonly Queue<RetiredCurves> _retiredCurves = new Queue<RetiredCurves>();
        private readonly PanelPreviewMemory _displayMemory;
        private bool _meshPriorityDirty;
        private readonly PanelPreviewMembership _membership = new PanelPreviewMembership();
        private readonly PanelPreviewRecovery _recovery = new PanelPreviewRecovery();
        private readonly PanelPreviewRecovery _filteredRecovery = new PanelPreviewRecovery();
        private bool _admissionBlockedByRetirement, _selectionRecoveryReady;
        private readonly PanelOrderLabels _orderLabels;
        private DisplayMaterial[]? _valueMaterials;
        private readonly Queue<DisplayMaterial> _retiredMaterials = new Queue<DisplayMaterial>();
        internal void ConfigureValues(PanelQueryResult result, bool order, bool values, PanelDotPlacement placement,
            PanelValuePalette palette, bool reverse, PanelSearchMode search, int decimals, double factor,
            IReadOnlyList<int> scope, Func<int, PanelPreviewValue> sample, Func<bool> current)
        {
            _activeDraws++;
            try
            {
                var labelResult = order || values ? result : null;
                bool attachLabels = labelResult != null && !ReferenceEquals(_orderResult, labelResult);
                SetOrderResult(labelResult);
                long generation = _generation;
                if (!current()) return;
                bool coloursChanged = _orderLabels.Configure(result, order, values, placement, palette, reverse, search, decimals, factor, scope, sample, current);
                if (generation != _generation || !current()) return;
                if (palette != PanelValuePalette.Off && _valueMaterials == null && !_displayFlushFailed)
                {
                    _valueMaterials = new DisplayMaterial[PanelPreviewValues.ColourCount];
                    for (int id = 0; id < _valueMaterials.Length; id++)
                    {
                        var colour = _orderLabels.Colour(id);
                        var half = System.Drawing.Color.FromArgb(colour.R / 2, colour.G / 2, colour.B / 2);
                        _valueMaterials[id] = new DisplayMaterial(half) { Emission = half, Specular = System.Drawing.Color.Black, Shine = 0 };
                    }
                }
                if (attachLabels || coloursChanged && palette != PanelValuePalette.Off)
                    foreach (var entry in _entries.Values)
                    {
                        if (attachLabels) AddLabel(entry, current);
                        else entry.ColourId = _orderLabels.ColourId(entry.Position);
                        if (generation != _generation || !current()) return;
                    }
            }
            finally { _activeDraws--; FlushRetired(); }
        }
        private void ValuesChanged()
        {
            foreach (var entry in _entries.Values) entry.ColourId = _orderLabels.ColourId(entry.Position);
            PreviewChanged?.Invoke();
        }
        internal void InvalidateValues() => _orderLabels.InvalidateValues();
        private void AddLabel(Entry entry, Func<bool> current)
        {
            entry.ColourId = _orderLabels.ColourId(entry.Position);
            if (_orderResult == null) return;
            var anchor = entry.Leaves.Count == 1 ? entry.Leaves[0].Anchor(current) : entry.Bounds.Center;
            if (current()) _orderLabels.Add(entry.Order, entry.Position, anchor, entry.Bounds);
        }
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
        private long _stagedBytes;
        private int _activeDraws;
        private bool _displayFlushFailed;
        // Test seam: runs inside the lifetime guard, immediately before Flush.
        internal Action? BeforeDisplayFlush { get; set; }
        internal int CompletedDisplayFlushes { get; private set; }
        private bool _flushingRetired;
        private bool _releaseScheduled;
        private bool _recoveryScheduled;
        private long _recoveryGeneration;
        private Func<bool>? _recoveryCurrent;
        internal Action? PreviewChanged { get; set; }
        internal Action<PanelQueryResult, IReadOnlyList<int>>? PendingMeasurements { get; set; }

        internal PanelGeometryHighlight() : this(null) { }
        // A fixture can inject a reentrant callback at the exact ownership seam.
        internal PanelGeometryHighlight(Action<int, object>? admissionObserver)
        {
            _admissionObserver = admissionObserver;
            _displayMemory = new PanelPreviewMemory(MaxRetainedBytes, () => SourceAndMaterialBytes);
            _orderLabels = new PanelOrderLabels(_membership);
            _orderLabels.ValuesChanged = ValuesChanged;
            _orderLabels.PendingChanged = (result, positions) => PendingMeasurements?.Invoke(result, positions);
        }
        internal int Count => _selectedCount;
        internal int PreviewCount => _entries.Count;
        private long SourceAndMaterialBytes => _bytes + _retiredBytes + _stagedBytes + ((_valueMaterials?.Length ?? 0) + _retiredMaterials.Count) * 512L;
        internal long RetainedBytes => SourceAndMaterialBytes + _displayMemory.UsedBytes;
        internal int LastVisitedRows { get; private set; }
        internal int LastVisitedNodes { get; private set; }
        internal int DrawWireCalls { get; private set; }
        internal int DrawMeshCalls { get; private set; }
        internal int IdleReleases { get; private set; }
        internal bool RetirementPending => _releaseScheduled;
        internal BoundingBox ClippingBox => _bounds.Box;
        internal string Status => _displayFlushFailed ? "Preview partial: display flush failed; drawn resources retained." :
            _partial || _recovery.Pending || _filteredRecovery.Pending || _unavailable > 0 || _meshFailures > 0
            ? "Preview partial: " + (_entries.Count >= PreviewLimit ? "memory or " + EntryLimitReason : _partialReason ?? (_recovery.Pending ? "selected geometry is awaiting admission" :
                _filteredRecovery.Pending ? "results are awaiting capacity" :
                "some geometry is unsupported or exceeds safety limits"))
            : string.Empty;
        private string EntryLimitReason => PreviewLimit.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + "-item limit";

        internal void SetOrderResult(PanelQueryResult? result)
        {
            if (ReferenceEquals(_orderResult, result)) return;
            _generation++;
            _orderResult = result;
            _orderLabels.ClearEntries();
            _orderLabels.SetResult(result);
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
            if (!filtered) { _partial = false; _partialReason = null; }
            LastVisitedRows = LastVisitedNodes = 0;
            var update = UpdateMembership(selectedPositions, result, selection);
            if (!Current(generation, current)) return;
            YieldUnselectedResources(generation, current);
            if (!Current(generation, current)) return;
            var budget = new PanelGeometrySources.OperationBudget();
            _recoveryGeneration = generation;
            _recoveryCurrent = current; _selectionRecoveryReady = true;
            // A newly selected row can evict an unselected preview. This allows
            // later selections to recover without another source solution.
            if (update.ScanSelection)
            {
                _recovery.Reset(selectedPositions);
                PumpRecovery(budget, generation, current);
                if (!Current(generation, current)) return;
            }
            else if (update.AddedPosition >= 0 && !_byPosition.ContainsKey(update.AddedPosition) && !_attempted.Contains(update.AddedPosition))
            {
                LastVisitedRows++;
                if (result.TryGetPreviewIndex(update.AddedPosition, out int order))
                    TryAdmit(update.AddedPosition, order, budget, true, generation, current);
                if (!Current(generation, current)) return;
                if (!_byPosition.ContainsKey(update.AddedPosition) && !_attempted.Contains(update.AddedPosition))
                    _recovery.Reset(selectedPositions);
            }
            if (fresh && filtered)
            {
                var positions = result.DisplaySet?.AllMembers ?? result.RowPositions;
                int end = Math.Min(positions.Count, MaxBuildRows);
                if (positions.Count > end) _partial = true;
                for (int index = 0; index < end; index++)
                {
                    if (_entries.Count >= PreviewLimit) { _partial = true; break; }
                    if (budget.Remaining <= 0)
                    { _filteredRecovery.Reset(positions, index, end); break; }
                    if (RetainedBytes >= MaxRetainedBytes)
                    {
                        if (RetiringBytes > 0) _filteredRecovery.Reset(positions, index, end);
                        else _partial = true;
                        break;
                    }
                    int position = positions[index];
                    LastVisitedRows++;
                    if (_byPosition.ContainsKey(position) || _attempted.Contains(position)) continue;
                    int visits = budget.Visits;
                    bool partial = _partial; string? reason = _partialReason;
                    if (result.TryGetPreviewIndex(position, out int order))
                        TryAdmit(position, order, budget, false, generation, current);
                    if (!Current(generation, current)) return;
                    if (!_byPosition.ContainsKey(position) && !_attempted.Contains(position) && budget.Limited && visits > 0)
                    {
                        // Preparation can consume the last node before committing this
                        // row. Retry the current row as well as the unvisited suffix.
                        _partial = partial; _partialReason = reason;
                        _filteredRecovery.Reset(positions, index, end); break;
                    }
                    if (_admissionBlockedByRetirement)
                    { _filteredRecovery.Reset(positions, index, end); break; }
                }
            }
            LastVisitedNodes = budget.Visits;
            ScheduleRecovery();
        }

        private bool PumpRecovery(PanelGeometrySources.OperationBudget budget, long generation, Func<bool> current)
        {
            bool progressed = false;
            bool more = _recovery.Pump(MaxUpdateRows, _membership.Contains,
                position => _byPosition.ContainsKey(position) || _attempted.Contains(position),
                position =>
                {
                    if (budget.Remaining <= 0 || !Current(generation, current) ||
                        (_entries.Count >= PreviewLimit && _unselected.Count == 0)) return PanelPreviewRecovery.Admission.Blocked;
                    LastVisitedRows++;
                    if (_result == null || !_result.TryGetPreviewIndex(position, out int order)) return PanelPreviewRecovery.Admission.Complete;
                    TryAdmit(position, order, budget, true, generation, current);
                    if (!Current(generation, current)) return PanelPreviewRecovery.Admission.Blocked;
                    if (_byPosition.ContainsKey(position) || _attempted.Contains(position))
                    { progressed = true; return PanelPreviewRecovery.Admission.Complete; }
                    return budget.Remaining <= 0 ? PanelPreviewRecovery.Admission.Blocked : PanelPreviewRecovery.Admission.Retry;
                });
            // A fresh per-turn budget can recover roots deferred after earlier
            // admissions. A pass with no progress sleeps until an external
            // selection/resource change, including for permanently costly roots.
            return more || (_recovery.Pending && progressed);
        }

        private bool PumpFilteredRecovery(PanelGeometrySources.OperationBudget budget, long generation, Func<bool> current)
        {
            int remaining = MaxUpdateRows - LastVisitedRows;
            if (remaining <= 0) return _filteredRecovery.Pending;
            bool budgetBlocked = false;
            bool more = _filteredRecovery.Pump(remaining, _ => true,
                position => _byPosition.ContainsKey(position) || _attempted.Contains(position),
                position =>
                {
                    if (!Current(generation, current)) return PanelPreviewRecovery.Admission.Blocked;
                    if (budget.Remaining <= 0) { budgetBlocked = true; return PanelPreviewRecovery.Admission.Blocked; }
                    if (_entries.Count >= PreviewLimit || RetainedBytes >= MaxRetainedBytes)
                    {
                        if (_entries.Count >= PreviewLimit || RetiringBytes == 0)
                        { _partial = true; _filteredRecovery.Clear(); }
                        return PanelPreviewRecovery.Admission.Blocked;
                    }
                    LastVisitedRows++;
                    if (_result == null || !_result.TryGetPreviewIndex(position, out int order)) return PanelPreviewRecovery.Admission.Complete;
                    int visits = budget.Visits;
                    bool partial = _partial;
                    string? reason = _partialReason;
                    TryAdmit(position, order, budget, false, generation, current);
                    if (!Current(generation, current) || _admissionBlockedByRetirement) return PanelPreviewRecovery.Admission.Blocked;
                    if (!_byPosition.ContainsKey(position) && !_attempted.Contains(position) && budget.Limited && visits > 0)
                    {
                        // This root may fit a fresh turn even though earlier admissions
                        // consumed this turn's budget. Retry it once before moving on.
                        _partial = partial; _partialReason = reason; budgetBlocked = true;
                        return PanelPreviewRecovery.Admission.Blocked;
                    }
                    return PanelPreviewRecovery.Admission.Complete;
                });
            return more || budgetBlocked && _filteredRecovery.Pending;
        }

        private void ScheduleRecovery()
        {
            if (_recoveryScheduled || ((!_selectionRecoveryReady || !_recovery.Pending) && !_filteredRecovery.Pending) || _recoveryCurrent == null) return;
            _recoveryScheduled = true;
            Rhino.RhinoApp.Idle += RecoverOnIdle;
        }

        private void RecoverOnIdle(object? sender, EventArgs args)
        {
            Rhino.RhinoApp.Idle -= RecoverOnIdle;
            _recoveryScheduled = false;
            long generation = _recoveryGeneration;
            var current = _recoveryCurrent;
            if (current == null || !Current(generation, current)) return;
            if (_displayFlushFailed) return;
            if (_activeDraws != 0 || _flushingRetired) { ScheduleRecovery(); return; }
            generation = ++_generation;
            _recoveryGeneration = generation;
            if (!_filtered) { _partial = false; _partialReason = null; }
            var budget = new PanelGeometrySources.OperationBudget();
            LastVisitedRows = LastVisitedNodes = 0;
            bool pending = _recovery.Pending || _filteredRecovery.Pending;
            bool more = _selectionRecoveryReady && PumpRecovery(budget, generation, current);
            if (!Current(generation, current)) return;
            // A stalled selected root sleeps until selection/capacity changes;
            // filtered continuation must not keep spending every fresh budget on it.
            _selectionRecoveryReady = more;
            more |= PumpFilteredRecovery(budget, generation, current);
            if (!Current(generation, current)) return;
            LastVisitedNodes = budget.Visits;
            if (LastVisitedRows > 0 || pending != (_recovery.Pending || _filteredRecovery.Pending)) PreviewChanged?.Invoke();
            if (Current(generation, current) && more) ScheduleRecovery();
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
            var budget = new PanelGeometrySources.OperationBudget();
            for (int i = 0; i < previewPositions.Count; i++)
            {
                if (i >= MaxBuildRows || budget.Remaining <= 0 || _entries.Count >= PreviewLimit) { _partial = true; break; }
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
                _meshPriorityDirty = true;
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

        private void TryAdmit(int position, int order, PanelGeometrySources.OperationBudget budget, bool mayEvict, long generation, Func<bool> current)
        {
            _admissionBlockedByRetirement = false;
            try { TryAdmitCore(position, order, budget, mayEvict, generation, current); }
            catch (Exception) { if (Current(generation, current)) RememberUnavailable(position); }
        }

        private void TryAdmitCore(int position, int order, PanelGeometrySources.OperationBudget budget, bool mayEvict, long generation, Func<bool> current)
        {
            if (_source == null || position < 0 || position >= _source.Count || _byPosition.ContainsKey(position)) return;
            var source = _source;
            var root = source[position];
            if (source is IGH_Goo?[] array && root?.GetType() == typeof(GH_Point) && ((GH_Point)root).ReferenceID == Guid.Empty && _admissionObserver == null &&
                TryAdmitPoint(array, position, order, budget, mayEvict, generation, current)) return;
            root = source[position];
            var scopeToken = _scopeToken;
            using var inspected = PanelGeometrySources.Inspect(root, budget, true,
                bytes => ReserveInspectionValue(bytes, mayEvict, generation, current), ReleaseInspectionValue);
            if (!Current(generation, current)) return;
            if (inspected.Unavailable)
            {
                if (inspected.Exhausted) _partial |= !_admissionBlockedByRetirement;
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
                bool estimated = PanelPreviewLeaf.TryEstimate(leaf, budget, ref remaining, out long bytes, out var scan,
                    () => Current(generation, current));
                if (!Current(generation, current)) return;
                if (!estimated || (estimate += bytes) > MaxRetainedBytes)
                {
                    _partialReason ??= budget.Reason;
                    if (budget.Limited) _partial = true;
                    else RememberUnavailable(position);
                    return;
                }
                estimates.Add(bytes);
                scans.Add(scan);
            }
            if (mayEvict && !MakeAdmissionRoom(estimate, needsEntry: true, generation, current)) return;
            if (_entries.Count >= PreviewLimit || !CanAdmitBytes(estimate))
            {
                if (!_admissionBlockedByRetirement) { _partial = true; _partialReason ??= "memory or " + EntryLimitReason; }
                return;
            }
            // Reserve the whole staged group before native construction. A
            // callback can build another generation while these leaves are
            // still alive; clearing the admitted cache must not release this.
            _stagedBytes += estimate;
            var leaves = new List<PanelPreviewLeaf>(inspected.Leaves.Count);
            var bounds = BoundingBox.Empty;
            bool admitted = false;
            bool reserved = true;
            try
            {
                for (int i = 0; i < inspected.Leaves.Count; i++)
                {
                    // Every leaf comes from this one bounded graph capture. Do
                    // not resolve a later member from the live group here.
                    var copy = PanelPreviewLeaf.Create(inspected.Leaves[i], estimates[i], scans[i], budget, ref remaining,
                        MeshStatusCallback(scopeToken),
                        () => Current(generation, current), _displayMemory, RetireMeshes, RetireCurves);
                    if (copy == null)
                    {
                        if (Current(generation, current))
                        {
                            _partialReason ??= budget.Reason ?? "native preparation failed";
                            if (budget.Limited) _partial = true; else RememberUnavailable(position);
                        }
                        return;
                    }
                    leaves.Add(copy);
                    if (!Current(generation, current)) return;
                    _admissionObserver?.Invoke(i, copy);
                    if (!Current(generation, current)) return;
                    bounds.Union(copy.Bounds);
                }
                if (_orderResult != null && leaves.Count == 1)
                {
                    leaves[0].Anchor(() => Current(generation, current));
                    if (!Current(generation, current)) return;
                }
                using var fresh = PanelGeometrySources.Inspect(root, budget, true,
                    bytes => ReserveInspectionValue(bytes, false, generation, current), ReleaseInspectionValue);
                if (!Current(generation, current)) return;
                if (!PanelGeometrySources.SameGraph(inspected, fresh))
                {
                    if (!_admissionBlockedByRetirement)
                    { _partial = true; _partialReason ??= fresh.Exhausted ? "inspection limit" : "group or wrapper changed during preparation"; }
                    return;
                }
                var entry = new Entry(position, order, bounds, estimate, leaves);
                _entries.Add(order, entry); _byPosition.Add(position, entry);
                _bytes += estimate; _stagedBytes -= estimate; reserved = false; admitted = true;
                _bounds.Add(bounds);
                if (_membership.Contains(position))
                { _selectedCount++; RequestMeshPriority(); }
                else _unselected.Add(order);
                AddLabel(entry, () => Current(generation, current));
                if (!Current(generation, current)) return;
            }
            finally
            {
                if (!admitted) foreach (var leaf in leaves) leaf.Dispose();
                if (reserved) _stagedBytes -= estimate;
            }
        }

        private bool CanAdmitBytes(long bytes)
        {
            if (bytes <= MaxRetainedBytes - RetainedBytes) return true;
            _admissionBlockedByRetirement |= RetiringBytes > 0;
            return false;
        }

        private bool ReserveInspectionValue(long bytes, bool mayEvict, long generation, Func<bool> current)
        {
            if (mayEvict && !MakeAdmissionRoom(bytes, needsEntry: false, generation, current)) return false;
            if (!Current(generation, current) || bytes < 0 || !CanAdmitBytes(bytes)) return false;
            _stagedBytes += bytes;
            return true;
        }

        private void ReleaseInspectionValue(long bytes) { _stagedBytes -= bytes; }

        // A direct exact GH_Point needs only managed value-field work after its
        // single graph inspection. Evict first, because releasing an old entry
        // can reenter; then capture, construct and attach without a callback in
        // between. Wrappers, groups, observer-injected tests and native geometry
        // still use the full post-preparation graph reinspection above.
        private bool TryAdmitPoint(IGH_Goo?[] source, int position, int order, PanelGeometrySources.OperationBudget budget,
            bool mayEvict, long generation, Func<bool> current)
        {
            const long estimate = 512;
            if (mayEvict && !MakeAdmissionRoom(estimate, needsEntry: true, generation, current)) return true;
            if (!Current(generation, current)) return true;
            if (source[position]?.GetType() != typeof(GH_Point) || ((GH_Point)source[position]!).ReferenceID != Guid.Empty) return false;
            if (!_orderLabels.CanAddWithoutEnabling) return false;
            if (_entries.Count >= PreviewLimit || !CanAdmitBytes(estimate))
            {
                if (!_admissionBlockedByRetirement) { _partial = true; _partialReason ??= "memory or " + EntryLimitReason; }
                return true;
            }
            using var inspected = PanelGeometrySources.Inspect(source[position], budget, true);
            if (inspected.Unavailable)
            {
                if (inspected.Exhausted) _partial = true; else RememberUnavailable(position);
                return true;
            }
            if (inspected.Leaves.Count != 1 || !(inspected.Leaves[0] is Point3d point))
            { Remember(position); return true; }
            int remaining = inspected.Remaining;
            var leaf = PanelPreviewLeaf.Create(point, estimate, null, budget, ref remaining, _ => { });
            if (leaf == null) { RememberUnavailable(position); return true; }
            var entry = new Entry(position, order, leaf.Bounds, estimate, new List<PanelPreviewLeaf> { leaf });
            _entries.Add(order, entry); _byPosition.Add(position, entry);
            _bytes += estimate; _bounds.Add(entry.Bounds);
            if (_membership.Contains(position))
            { _selectedCount++; RequestMeshPriority(); }
            else _unselected.Add(order);
            AddLabel(entry, () => Current(generation, current));
            return true;
        }

        // Leaves keep this delegate for their lifetime, so it must not share a closure
        // with admission locals that hold the expired source and query.
        private Action<int> MeshStatusCallback(object scopeToken) =>
            delta => { if (ReferenceEquals(scopeToken, _scopeToken)) MeshStatusChanged(delta); };

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

        private void Retire(Entry entry)
        {
            _retired.Enqueue(entry); _retiredBytes += entry.Bytes;
            if (entry.WasDrawn || _activeDraws != 0) ScheduleRelease();
        }

        private void RetireMeshes(Mesh[] meshes, int[][]? faces, PanelPreviewMemory.Lease? charge)
        {
            _retiredMeshes.Enqueue(new RetiredMeshes(meshes, faces, charge));
            ScheduleRelease();
        }

        private void RetireCurves(NurbsCurve[] curves, PanelPreviewMemory.Lease? charge)
        {
            _retiredCurves.Enqueue(new RetiredCurves(curves, charge));
            ScheduleRelease();
        }

        private long RetiringBytes
        {
            get
            {
                long bytes = _retiredBytes + _retiredMaterials.Count * 512L;
                foreach (var entry in _retired)
                    foreach (var leaf in entry.Leaves) bytes += leaf.DisplayBytes;
                foreach (var cache in _retiredMeshes) bytes += cache.Charge?.Bytes ?? 0;
                foreach (var cache in _retiredCurves) bytes += cache.Charge?.Bytes ?? 0;
                return bytes;
            }
        }

        private bool MakeAdmissionRoom(long bytes, bool needsEntry, long generation, Func<bool> current)
        {
            while (_unselected.Count > 0 && (needsEntry && _entries.Count >= PreviewLimit ||
                _displayMemory.RequiredRelease(bytes, RetiringBytes) > 0))
            {
                Remove(_entries[_unselected.Max]);
                if (!Current(generation, current)) return false;
            }
            return Current(generation, current);
        }

        private void RequestMeshPriority()
        {
            _meshPriorityDirty = true;
            ScheduleRelease();
        }

        private void YieldUnselectedResources(long generation, Func<bool> current)
        {
            if (!_meshPriorityDirty) return;
            if (_activeDraws != 0) { ScheduleRelease(); return; }
            long needed = 0;
            foreach (var entry in _entries.Values)
                if (_membership.Contains(entry.Position))
                    foreach (var leaf in entry.Leaves) needed += leaf.PendingDisplayBytes;
            // Do not consume the flag before examining newly admitted selected leaves.
            // Unknown sizes are learned by their first bounded preparation attempt.
            _meshPriorityDirty = false;
            long shortfall = _displayMemory.RequiredRelease(needed, RetiringBytes);
            if (needed == 0 || shortfall == 0) return;
            var entries = new List<Entry>(_entries.Values);
            // Keep the earliest previews. Retire only enough lower-priority caches,
            // outside drawing, to cover the selected resources' measured shortfall.
            _activeDraws++;
            try
            {
                for (int index = entries.Count - 1; index >= 0 && shortfall > 0; index--)
                {
                    var entry = entries[index];
                    if (_membership.Contains(entry.Position)) continue;
                    foreach (var leaf in entry.Leaves)
                    {
                        long bytes = leaf.DisplayBytes;
                        try { leaf.ReleaseDisplayResources(); } catch (Exception) { }
                        if (!Current(generation, current)) return;
                        shortfall -= bytes - leaf.DisplayBytes;
                        if (shortfall <= 0) break;
                    }
                }
            }
            finally { _activeDraws--; FlushRetired(); }
        }

        private void ScheduleRelease()
        {
            if (_releaseScheduled) return;
            _releaseScheduled = true;
            Rhino.RhinoApp.Idle += ReleaseOnIdle;
        }

        private void ReleaseOnIdle(object? sender, EventArgs args)
        {
            if (_activeDraws != 0 || _flushingRetired || _displayFlushFailed) return;
            Rhino.RhinoApp.Idle -= ReleaseOnIdle;
            _releaseScheduled = false;
            IdleReleases++;
            long before = RetainedBytes;
            if (_meshPriorityDirty && _recoveryCurrent != null) YieldUnselectedResources(_generation, _recoveryCurrent);
            FlushRetired();
            if (RetainedBytes < before)
            {
                _selectionRecoveryReady = true;
                if (_entries.Count > 0) PreviewChanged?.Invoke();
            }
            ScheduleRecovery();
        }

        private void FlushRetired()
        {
            if (_activeDraws != 0 || _flushingRetired || _releaseScheduled || _displayFlushFailed) return;
            _flushingRetired = true;
            try
            {
                // Reentrant cleanup can add newly drawn resources. Leave those
                // for a later idle turn rather than draining them immediately.
                int materials = _retiredMaterials.Count;
                while (materials-- > 0 && _retiredMaterials.Count > 0) _retiredMaterials.Dequeue().Dispose();
                int entries = _retired.Count, caches = _retiredMeshes.Count, curves = _retiredCurves.Count;
                while (entries-- > 0 && _retired.Count > 0 && _activeDraws == 0 && !_releaseScheduled)
                {
                    var entry = _retired.Dequeue();
                    try { foreach (var leaf in entry.Leaves) try { leaf.Dispose(); } catch (Exception) { } }
                    finally { _retiredBytes -= entry.Bytes; }
                }
                while (caches-- > 0 && _retiredMeshes.Count > 0 && _activeDraws == 0 && !_releaseScheduled)
                {
                    var cache = _retiredMeshes.Dequeue();
                    try { PanelPreviewLeaf.DisposeGeneratedMeshes(cache.Meshes); }
                    finally { GC.KeepAlive(cache.Faces); cache.Charge?.Dispose(); }
                }
                while (curves-- > 0 && _retiredCurves.Count > 0 && _activeDraws == 0 && !_releaseScheduled)
                {
                    var cache = _retiredCurves.Dequeue();
                    try { PanelPreviewLeaf.DisposeCurves(cache.Curves); }
                    finally { cache.Charge?.Dispose(); }
                }
            }
            finally
            {
                _flushingRetired = false;
                if ((_retired.Count > 0 || _retiredMeshes.Count > 0 || _retiredCurves.Count > 0 || _retiredMaterials.Count > 0) && !_releaseScheduled) ScheduleRelease();
            }
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

        internal void Clear() => ClearForQuery(false);
        internal bool ClearForQuery(bool keepValues)
        {
            _generation++;
            if (keepValues && _orderLabels.HasValuePreview)
            {
                // Keep the owned preview visible until SetScopeChecked replaces it.
                // No callback tied to the expired input may clear this retained generation.
                CancelRecovery(); _meshPriorityDirty = false;
                _source = null; _result = null; _orderResult = null; _legacyPositions = null;
                _membership.DetachResult();
                _orderLabels.SuspendValues();
                return true;
            }
            _orderLabels.Clear(); _orderResult = null; ClearGeometry();
            if (_valueMaterials != null)
            {
                foreach (var material in _valueMaterials) _retiredMaterials.Enqueue(material);
                _valueMaterials = null;
                ScheduleRelease();
            }
            return false;
        }
        private void CancelRecovery()
        {
            if (_recoveryScheduled) Rhino.RhinoApp.Idle -= RecoverOnIdle;
            _recoveryScheduled = false; _recoveryCurrent = null; _recovery.Clear(); _filteredRecovery.Clear();
            _admissionBlockedByRetirement = _selectionRecoveryReady = false;
        }

        private void ClearGeometry()
        {
            var old = new List<Entry>(_entries.Values);
            CancelRecovery();
            _scopeToken = new object();
            _entries.Clear(); _byPosition.Clear(); _unselected.Clear(); _attempted.Clear(); _unavailablePositions.Clear(); _attemptOrder.Clear(); _membership.Clear();
            _bounds.Clear(); _orderLabels.ClearEntries(); _selectedCount = _unavailable = _meshFailures = 0; _bytes = 0; _partial = false; _partialReason = null;
            _source = null; _result = null; _legacyPositions = null; _meshPriorityDirty = false;
            foreach (var entry in old) Retire(entry);
            FlushRetired();
        }

        private void PrepareSelectedMeshes(IGH_PreviewArgs args, long generation)
        {
            MeshingParameters? parameters = null;
            foreach (var entry in _entries.Values)
            {
                if (!_membership.Contains(entry.Position)) continue;
                foreach (var leaf in entry.Leaves)
                {
                    if (!leaf.UsesGeneratedMeshes) continue;
                    parameters ??= args.MeshingParameters;
                    if (parameters == null) return;
                    try { leaf.PrepareMeshes(parameters, () => generation == _generation); } catch (Exception) { }
                    if (generation != _generation) return;
                    if (leaf.PendingDisplayBytes > 0) RequestMeshPriority();
                }
            }
        }

        internal void DrawWires(IGH_PreviewArgs args, bool componentSelected)
        {
            // A nested display callback can expire or replace this generation.
            // Keep resources alive through our explicit pipeline flush, including
            // reentrant idle, and suppress nested draws that could reset a cache.
            if (_activeDraws != 0 || _displayFlushFailed) return;
            _activeDraws++;
            DrawWireCalls++;
            try
            {
            long generation = _generation;
            // GH can request wires first. Reserve selected shading first when shading is on.
            if (args.Document.PreviewMode == GH_PreviewMode.Shaded) PrepareSelectedMeshes(args, generation);
            if (generation != _generation) return;
            for (int pass = 0; pass < 2; pass++)
                foreach (var entry in _entries.Values)
                {
                    if (_membership.Contains(entry.Position) != (pass == 0)) continue;
                    foreach (var leaf in entry.Leaves)
                    {
                        try { leaf.PrepareWires(() => generation == _generation); } catch (Exception) { }
                        if (generation != _generation) return;
                        if (pass == 0 && leaf.PendingDisplayBytes > 0) RequestMeshPriority();
                    }
                }
            var normal = new GH_PreviewWireArgs(args.Viewport, args.Display, args.WireColour, args.DefaultCurveThickness);
            // Selected rows must stand out against the same geometry's upstream preview,
            // whether or not this component is selected on the canvas.
            int thickness = args.DefaultCurveThickness + Math.Max(MinSelectionThickening, componentSelected ? 0 : CentralSettings.PreviewSelectionThickening);
            var selected = new GH_PreviewWireArgs(args.Viewport, args.Display, args.WireColour_Selected, thickness);
            GH_PreviewWireArgs[]? colours = null;
            if (_orderLabels.Gradient && _valueMaterials != null)
            {
                colours = new GH_PreviewWireArgs[_valueMaterials.Length];
            }
            DrawWires(normal, false, colours, args.DefaultCurveThickness, args.Document.PreviewMode == GH_PreviewMode.Shaded);
            if (generation != _generation) return;
            bool pushed = false;
            try
            {
                if (_selectedCount > 0 && args.Display != null)
                {
                    PushPreviewDepthBias(args); pushed = true;
                }
                DrawWires(selected, true, colours, args.DefaultCurveThickness, args.Document.PreviewMode == GH_PreviewMode.Shaded);
            }
            finally { if (pushed) args.Display.PopModelTransform(); }
            if (generation != _generation) return;
            _orderLabels.Arm(args);
            }
            finally { EndDraw(args.Display); }
        }

        private void DrawWires(GH_PreviewWireArgs args, bool selected, GH_PreviewWireArgs[]? colours, int normalThickness, bool shaded)
        {
            long generation = _generation;
            foreach (var entry in _entries.Values)
            {
                if (_membership.Contains(entry.Position) != selected) continue;
                entry.WasDrawn = true;
                var draw = colours == null ? args : colours[entry.ColourId] ??=
                    new GH_PreviewWireArgs(args.Viewport, args.Pipeline, _orderLabels.Colour(entry.ColourId), normalThickness);
                foreach (var leaf in entry.Leaves)
                {
                    try
                    {
                        if (selected && colours != null)
                        {
                            args.Pipeline.PushDepthWriting(false);
                            try { leaf.DrawWires(args, () => generation == _generation, colourByValue: true, halo: true, shaded: shaded); }
                            finally { args.Pipeline.PopDepthWriting(); }
                        }
                        if (generation != _generation) return;
                        leaf.DrawWires(draw, () => generation == _generation, colourByValue: colours != null);
                    }
                    catch (Exception) { }
                    if (generation != _generation) return;
                }
            }
        }

        private static void PushPreviewDepthBias(IGH_PreviewArgs args)
        {
            var clipBias = Transform.Identity; clipBias.M22 = 0.995; clipBias.M23 = 0.01;
            var bias = args.Viewport.GetTransform(CoordinateSystem.Clip, CoordinateSystem.World) * clipBias *
                args.Viewport.GetTransform(CoordinateSystem.World, CoordinateSystem.Clip);
            args.Display.PushModelTransform(bias * args.Display.ModelTransform);
        }

        private const double GradientDepthOffset = 1e-3;
        private static bool PushGradientDepthOffset(IGH_PreviewArgs args)
        {
            var viewport = args.Viewport;
            Transform offset;
            if (viewport.IsParallelProjection)
            {
                // A small fraction of the visible world height, independent of clip depth.
                if (!viewport.GetFrustum(out _, out _, out double bottom, out double top, out _, out _)) return false;
                var direction = viewport.CameraDirection;
                double distance = (top - bottom) * GradientDepthOffset;
                if (!direction.Unitize() || distance <= 0 || double.IsNaN(distance) || double.IsInfinity(distance)) return false;
                offset = Transform.Translation(direction * -distance);
            }
            else offset = Transform.Scale(viewport.CameraLocation, 1 - GradientDepthOffset);
            args.Display.PushModelTransform(offset * args.Display.ModelTransform);
            return true;
        }

        internal void DrawMeshes(IGH_PreviewArgs args)
        {
            if (args.MeshingParameters == null) return;
            if (_activeDraws != 0 || _displayFlushFailed) return;
            _activeDraws++;
            DrawMeshCalls++;
            try
            {
            long generation = _generation;
            PrepareSelectedMeshes(args, generation);
            if (generation != _generation) return;
            var normal = new GH_PreviewMeshArgs(args.Viewport, args.Display, args.ShadeMaterial, args.MeshingParameters);
            var selected = new GH_PreviewMeshArgs(args.Viewport, args.Display, args.ShadeMaterial_Selected, args.MeshingParameters);
            var halo = _orderLabels.Gradient ? new GH_PreviewWireArgs(args.Viewport, args.Display, args.WireColour_Selected,
                args.DefaultCurveThickness + Math.Max(MinSelectionThickening, CentralSettings.PreviewSelectionThickening)) : null;
            // Fixed meshes need no remeshing. Brep/Surface/SubD entries manage
            // their own bounded meshes and invalidate them on setting changes.
            GH_PreviewMeshArgs[]? colours = null;
            if (_orderLabels.Gradient && _valueMaterials != null)
            {
                colours = new GH_PreviewMeshArgs[_valueMaterials.Length];
            }
            bool pushed = false;
            try
            {
                // Keep fills just ahead of coincident upstream preview, without
                // the wire bias's dependence on clipping-plane depth.
                if (colours != null && args.Display != null)
                    pushed = PushGradientDepthOffset(args);
                for (int pass = 0; pass < (colours == null ? 1 : 2); pass++)
                    foreach (var entry in _entries.Values)
                    {
                        bool isSelected = _membership.Contains(entry.Position);
                        if (colours != null && isSelected != (pass == 1)) continue;
                        entry.WasDrawn = true;
                        var draw = colours == null ? isSelected ? selected : normal : colours[entry.ColourId] ??=
                            new GH_PreviewMeshArgs(args.Viewport, args.Display, _valueMaterials![entry.ColourId], args.MeshingParameters);
                        foreach (var leaf in entry.Leaves)
                        {
                            try { leaf.DrawMeshes(draw, () => generation == _generation, colours != null, isSelected && colours != null ? halo : null); } catch (Exception) { }
                            if (generation != _generation) return;
                        }
                    }
            }
            finally { if (pushed) args.Display.PopModelTransform(); }
            }
            finally { EndDraw(args.Display); }
        }

        private void EndDraw(Rhino.Display.DisplayPipeline? pipeline)
        {
            try
            {
                if (pipeline != null)
                {
                    BeforeDisplayFlush?.Invoke();
                    // Flush drains RhinoCommon's managed MeshCache and invokes
                    // the native flush while every submitted resource is alive.
                    // Native consumption after return still needs host validation;
                    // idle alone is not an end-of-frame or GPU completion fence.
                    pipeline.Flush();
                    CompletedDisplayFlushes++;
                }
            }
            catch (Exception)
            {
                // A later pipeline cannot prove consumption by this pipeline.
                // Quarantine this owner's bounded resources, keep the idle root,
                // and stop drawing instead of retrying disposal after a delay.
                _displayFlushFailed = true;
                ScheduleRelease();
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
            internal bool WasDrawn;
            internal int ColourId;
        }

        private sealed class RetiredMeshes
        {
            internal RetiredMeshes(Mesh[] meshes, int[][]? faces, PanelPreviewMemory.Lease? charge) { Meshes = meshes; Faces = faces; Charge = charge; }
            internal readonly Mesh[] Meshes;
            internal readonly int[][]? Faces;
            internal readonly PanelPreviewMemory.Lease? Charge;
        }

        private sealed class RetiredCurves
        {
            internal RetiredCurves(NurbsCurve[] curves, PanelPreviewMemory.Lease? charge) { Curves = curves; Charge = charge; }
            internal readonly NurbsCurve[] Curves;
            internal readonly PanelPreviewMemory.Lease? Charge;
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
