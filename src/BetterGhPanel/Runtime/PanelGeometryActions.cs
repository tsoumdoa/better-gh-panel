using System;
using System.Collections.Generic;
using System.Threading;
using BetterGhPanel.UI;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Display;
using Rhino.FileIO;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    internal sealed class PanelGeometryActionResult
    {
        internal PanelGeometryActionResult(bool success, int count, int skipped, string? error = null, bool exhausted = false,
            string? detail = null, bool unverifiedMetadata = false, bool deferred = false)
        { Success = success; Count = count; Skipped = skipped; Error = error; Exhausted = exhausted; Detail = detail; UnverifiedMetadata = unverifiedMetadata; Deferred = deferred; }
        internal bool Success { get; }
        internal int Count { get; }
        internal int Skipped { get; }
        internal string? Error { get; }
        internal bool Exhausted { get; }
        internal string? Detail { get; }
        internal bool UnverifiedMetadata { get; }
        internal bool Deferred { get; }
    }

    // UI-thread actions inspect the current graph afresh. The limits apply to
    // the entire action as well as each group, even for repeated references.
    internal static class PanelGeometryActions
    {
        private const int MaxRowsPerAction = 2048;
        private const int MaxGeometryPerAction = 2048;
        private const long MaxPreparedBytes = 8L * 1024 * 1024;
        private static long _clipboardGeneration, _frameGeneration;
        private static readonly PanelActionCommit ClipboardCommit = new PanelActionCommit();
        private static readonly Dictionary<Guid, PanelActionCommit> FrameCommits = new Dictionary<Guid, PanelActionCommit>();
        internal static long ClipboardGeneration => Interlocked.Read(ref _clipboardGeneration);
        internal static long FrameGeneration => Interlocked.Read(ref _frameGeneration);
        internal static long BeginClipboard() => Interlocked.Increment(ref _clipboardGeneration);
        internal static bool ClipboardCurrent(long generation) => ClipboardGeneration == generation;
        internal static PanelCommitOutcome CommitClipboard(Func<bool> current, Action publish, Action<PanelCommitOutcome>? completed = null) =>
            ClipboardCommit.Run(current, publish, completed);
        internal static PanelCommitOutcome CommitFrame(Guid viewportId, Func<bool> current, Action publish, Action<PanelCommitOutcome>? completed = null)
        {
            if (!FrameCommits.TryGetValue(viewportId, out var gate)) FrameCommits.Add(viewportId, gate = new PanelActionCommit());
            var result = gate.Run(current, publish, completed);
            if (gate.IsIdle) FrameCommits.Remove(viewportId);
            return result;
        }

        internal static PanelGeometryActionResult Frame(IEnumerable<IGH_Goo?> items)
            => FrameChecked(items, () => true);

        internal static PanelGeometryActionResult FrameChecked(IEnumerable<IGH_Goo?> items, Func<bool> current)
            => FrameWithCompletion(items, current, null);

        internal static PanelGeometryActionResult FrameWithCompletion(IEnumerable<IGH_Goo?> items, Func<bool> current,
            Action<PanelGeometryActionResult>? completed)
        {
            long generation = Interlocked.Increment(ref _frameGeneration);
            var sourceCurrent = current;
            current = () => FrameGeneration == generation && sourceCurrent() && FrameGeneration == generation;
            int count = 0, skipped = 0, rows = 0;
            bool exhausted = false;
            var budget = new PanelGeometrySources.Budget();
            var sources = new List<ActionSource>();
            try
            {
                var document = RhinoDoc.ActiveDoc;
                var view = document?.Views.ActiveView;
                if (view == null) return Failure("No active Rhino viewport is available.");
                Guid? detailId = null;
                if (view is RhinoPageView page)
                {
                    if (page.PageIsActive) return Failure("Activate a model viewport or a layout detail to frame geometry.");
                    var details = page.GetDetailViews();
                    if (details != null)
                        foreach (var detail in details)
                            if (detail.IsActive)
                            {
                                if (detail.DetailGeometry.IsProjectionLocked) return Failure("The active layout detail is locked.");
                                detailId = detail.Id;
                                break;
                            }
                    if (!detailId.HasValue) return Failure("No active layout detail is available.");
                }
                var viewport = view.ActiveViewport;
                if (viewport.LockedProjection) return Failure("The active Rhino viewport is locked.");
                Guid viewportId = viewport.Id;
                var bounds = BoundingBox.Empty;
                foreach (var item in items)
                {
                    if (++rows > MaxRowsPerAction || budget.Remaining <= 0) { exhausted = true; break; }
                    var inspected = PanelGeometrySources.Inspect(item, budget, true);
                    if (!current()) return Stale();
                    sources.Add(new ActionSource(item, inspected));
                    if (inspected.Exhausted) exhausted = true;
                    BoundingBox box;
                    try { box = Bounds(inspected, budget); }
                    catch (Exception) { box = BoundingBox.Empty; }
                    if (!current()) return Stale();
                    if (budget.Limited) exhausted = true;
                    if (!box.IsValid) { skipped++; if (budget.Remaining <= 0) break; continue; }
                    bounds.Union(box);
                    count++;
                }
                if (count == 0 || !bounds.IsValid)
                    return Failure(exhausted ? "No geometry framed; inspection limit reached." : "No valid geometry to frame in the selected rows.", skipped, exhausted);
                if (!StillCurrent(sources, budget, true, current, out bool limit)) return limit ?
                    Failure("Frame cancelled; source reinspection limit reached.", skipped, true) : Stale();
                double span = Math.Max(bounds.Max.X - bounds.Min.X, Math.Max(bounds.Max.Y - bounds.Min.Y, bounds.Max.Z - bounds.Min.Z));
                if (!RhinoMath.IsValidDouble(span) || !bounds.Center.IsValid) return Failure("The selected geometry is too large to frame.", skipped, exhausted);
                if (span != 0)
                {
                    bounds.Inflate(span * 0.08);
                    if (!bounds.IsValid) return Failure("Rhino could not frame the selected geometry.", skipped, exhausted);
                }
                bool commitLimit = false;
                bool CanCommit()
                {
                    if (!current() || !FrameTargetCurrent(document!, view, viewportId, detailId)) return false;
                    bool valid = StillCurrent(sources, budget, true, current, out bool limited);
                    commitLimit |= limited;
                    // Source validation can reenter and change the active view or
                    // lock its projection; recheck the target after it returns.
                    return valid && current() && FrameTargetCurrent(document!, view, viewportId, detailId) && FrameGeneration == generation;
                }
                PanelGeometryActionResult Result(PanelCommitOutcome outcome) => outcome == PanelCommitOutcome.Deferred
                    ? new PanelGeometryActionResult(false, 0, 0, "Frame queued.", deferred: true)
                    : outcome == PanelCommitOutcome.Stale && commitLimit ? Failure("Frame cancelled; source reinspection limit reached.", skipped, true)
                    : outcome == PanelCommitOutcome.Stale ? Stale()
                    : outcome == PanelCommitOutcome.Committed && !current() ? Failure("Frame context changed during camera publication; viewport state is uncertain.")
                    : outcome == PanelCommitOutcome.Uncertain ? Failure("Rhino camera publication failed; viewport state is uncertain.")
                    : new PanelGeometryActionResult(true, count, skipped, exhausted: exhausted);
                var state = CommitFrame(viewportId,
                    CanCommit,
                    () =>
                    {
                        if (span == 0) viewport.SetCameraTarget(bounds.Center, true);
                        else if (!viewport.ZoomBoundingBox(bounds)) throw new InvalidOperationException("Rhino could not frame geometry.");
                        view.Redraw();
                    },
                    outcome => completed?.Invoke(Result(outcome)));
                return Result(state);
            }
            catch (Exception ex) { return Failure("Could not frame geometry: " + ex.Message, skipped, exhausted); }
        }

        internal static BoundingBox GetBounds(IGH_Goo? item)
        {
            try
            {
                var budget = new PanelGeometrySources.Budget();
                return Bounds(PanelGeometrySources.Inspect(item, budget, true), budget);
            }
            catch (Exception) { return BoundingBox.Empty; }
        }

        private static BoundingBox Bounds(PanelGeometrySources.Inspection inspected, PanelGeometrySources.Budget budget)
        {
            if (inspected.Unavailable) return BoundingBox.Empty;
            var bounds = BoundingBox.Empty;
            int remaining = inspected.Remaining;
            foreach (object value in inspected.Leaves)
            {
                // Bounds do not duplicate or serialize metadata. Inspect the
                // exposed geometry first, then read the current source bounds;
                // this preserves framing for values with custom UserData.
                if (value is GeometryBase geometry)
                {
                    if (!PanelNativeGeometry.TryScanForBounds(geometry, budget, ref remaining)) return BoundingBox.Empty;
                    var current = geometry.GetBoundingBox(false);
                    if (!current.IsValid) return BoundingBox.Empty;
                    bounds.Union(current);
                    continue;
                }
                if (!PanelPreviewLeaf.TryEstimate(value, budget, ref remaining, out long bytes, out var scan)) return BoundingBox.Empty;
                using var leaf = PanelPreviewLeaf.Create(value, bytes, scan, budget, ref remaining, _ => { });
                if (leaf == null || !leaf.Bounds.IsValid) return BoundingBox.Empty;
                bounds.Union(leaf.Bounds);
            }
            return bounds;
        }

        internal static PanelGeometryActionResult CopyGeometry(IEnumerable<IGH_Goo?> items, UnitSystem units, double tolerance)
            => CopyGeometryChecked(items, units, tolerance, () => true);

        internal static PanelGeometryActionResult CopyGeometryChecked(IEnumerable<IGH_Goo?> items, UnitSystem units, double tolerance, Func<bool> current)
            => CopyGeometryCore(items, units, tolerance, current, () => RhinoDoc.ActiveDoc, null);

        internal static PanelGeometryActionResult CopyGeometryWithCompletion(IEnumerable<IGH_Goo?> items, UnitSystem units,
            double tolerance, Func<bool> current, Action<PanelGeometryActionResult>? completed)
            => CopyGeometryCore(items, units, tolerance, current, () => RhinoDoc.ActiveDoc, completed);

        internal static PanelGeometryActionResult CopyGeometryWithContext(IEnumerable<IGH_Goo?> items, UnitSystem units, double tolerance,
            Func<bool> current, Func<RhinoDoc?> activeDocument)
            => CopyGeometryCore(items, units, tolerance, current, activeDocument, null);

        // Fixture-only observation of the exact archive handed to the production
        // clipboard writer. The observer runs before the final freshness checks.
        internal static PanelGeometryActionResult CopyGeometryWithArchiveObserver(IEnumerable<IGH_Goo?> items, UnitSystem units,
            double tolerance, Action<byte[]> observer)
            => CopyGeometryCore(items, units, tolerance, () => true, () => RhinoDoc.ActiveDoc, null, observer);

        private static PanelGeometryActionResult CopyGeometryCore(IEnumerable<IGH_Goo?> items, UnitSystem units, double tolerance,
            Func<bool> current, Func<RhinoDoc?> activeDocument, Action<PanelGeometryActionResult>? completed,
            Action<byte[]>? archiveObserver = null)
        {
            long generation = BeginClipboard();
            if (!Rhino.Runtime.HostUtils.RunningOnWindows) return Failure("Copy geometry into Rhino is currently supported on Windows only.");
            if (units == UnitSystem.CustomUnits) return Failure("Copy geometry does not yet support custom Rhino model units.");
            var document = activeDocument();
            if (document == null) return Failure("No active Rhino document is available for geometry copy.");
            uint documentSerial = document.RuntimeSerialNumber;
            UnitSystem documentUnits = document.ModelUnitSystem;
            long documentTolerance = BitConverter.DoubleToInt64Bits(document.ModelAbsoluteTolerance);
            if (units != documentUnits || BitConverter.DoubleToInt64Bits(tolerance) != documentTolerance)
                return Failure("Rhino model units or tolerance changed; recompute the input before copying.");
            var sourceCurrent = current;
            bool ContextCurrent()
            {
                var active = activeDocument();
                return active != null && active.RuntimeSerialNumber == documentSerial && active.ModelUnitSystem == documentUnits &&
                    BitConverter.DoubleToInt64Bits(active.ModelAbsoluteTolerance) == documentTolerance;
            }
            // The source predicate can itself reenter and change clipboard owner,
            // document, units or tolerance. Fence both sides of that callback.
            current = () => ClipboardCurrent(generation) && ContextCurrent() && sourceCurrent() &&
                ClipboardCurrent(generation) && ContextCurrent() && ClipboardCurrent(generation);
            if (!current()) return Stale();
            int count = 0, skipped = 0, rows = 0;
            long bytes = 0;
            bool exhausted = false;
            var budget = new PanelGeometrySources.Budget();
            var metadata = new List<PanelNativeGeometry.Scan?>();
            var archiveIds = new List<Guid>();
            var sources = new List<ActionSource>();
            try
            {
                byte[] archive;
                using (var file = new File3dm())
                {
                    file.Settings.ModelUnitSystem = units;
                    if (RhinoMath.IsValidDouble(tolerance) && tolerance > 0) file.Settings.ModelAbsoluteTolerance = tolerance;
                    foreach (var item in items)
                    {
                        if (++rows > MaxRowsPerAction || budget.Remaining <= 0) { exhausted = true; break; }
                        var inspected = PanelGeometrySources.Inspect(item, budget, false);
                        if (!current()) return Stale();
                        sources.Add(new ActionSource(item, inspected));
                        if (inspected.Exhausted) exhausted = true;
                        bool graphLimit = false, graphChanged = false;
                        bool CurrentGraph()
                        {
                            if (!current()) return false;
                            var fresh = PanelGeometrySources.Inspect(item, budget, false);
                            if (fresh.Exhausted) graphLimit = true;
                            bool same = !fresh.Exhausted && PanelGeometrySources.SameGraph(inspected, fresh) && current();
                            if (!same && !graphLimit) graphChanged = true;
                            return same;
                        }
                        PanelGeometryActionResult GraphFailure() => graphLimit
                            ? Failure("Copy cancelled; source reinspection limit reached; clipboard was not changed.", skipped, true) : Stale();
                        int remaining = inspected.Remaining;
                        long estimate = 0;
                        bool supported = !inspected.Unavailable && inspected.Leaves.Count > 0;
                        var scans = new List<PanelNativeGeometry.Scan?>(inspected.Leaves.Count);
                        foreach (object leaf in inspected.Leaves)
                        {
                            if (!TryEstimateCopy(leaf, budget, ref remaining, current, out long size, out var scan))
                            { if (!current()) return Stale(); supported = false; if (budget.Limited) exhausted = true; break; }
                            if (!current()) return Stale();
                            if (scan != null && !CurrentGraph()) return GraphFailure();
                            scans.Add(scan);
                            estimate += size;
                            if (estimate > MaxPreparedBytes) { supported = false; exhausted = true; break; }
                        }
                        if (!supported) { skipped++; if (budget.Remaining <= 0) break; continue; }
                        if (count + inspected.Leaves.Count > MaxGeometryPerAction || bytes + estimate > MaxPreparedBytes)
                        { skipped++; exhausted = true; break; }
                        // Prepare the entire group before adding any member, so a
                        // failed or metadata-bearing child cannot silently yield a partial group.
                        var copies = new List<GeometryBase>();
                        try
                        {
                            for (int i = 0; i < inspected.Leaves.Count; i++)
                            {
                                var copy = CopyLeaf(inspected.Leaves[i], scans[i], budget, ref remaining, current, CurrentGraph);
                                if (!current()) { copy?.Dispose(); return Stale(); }
                                if (copy == null && (graphLimit || graphChanged)) return GraphFailure();
                                if (copy == null && scans[i] != null && !CurrentGraph()) return GraphFailure();
                                if (copy == null) { supported = false; if (budget.Limited) exhausted = true; break; }
                                copies.Add(copy);
                            }
                            if (!supported) { skipped++; continue; }
                            for (int i = 0; i < copies.Count; i++)
                            {
                                Guid added = file.Objects.Add(copies[i], null);
                                if (!current()) return Stale();
                                // File3dmObjectTable.Add may duplicate user data,
                                // so re-inspect the captured group after it returns.
                                if (scans[i] != null && !CurrentGraph()) return GraphFailure();
                                if (added == Guid.Empty)
                                    return Failure("Rhino could not prepare one of the selected geometries for copying.", skipped, exhausted);
                                archiveIds.Add(added);
                                count++;
                            }
                            metadata.AddRange(scans);
                            bytes += estimate;
                        }
                        finally { foreach (var copy in copies) copy.Dispose(); }
                    }
                    if (count == 0)
                        return Failure(exhausted ? "No geometry copied; preparation limit reached." :
                            "No geometry copied; skipped " + (budget.Reason ?? "unsupported geometry or unverified metadata") + ".", skipped, exhausted);
                    archive = file.ToByteArray(new File3dmWriteOptions { Version = 8, SaveUserData = true });
                    if (!current()) return Stale();
                }
                if (archive == null || archive.Length == 0) return Failure("Rhino could not create the geometry clipboard archive.", skipped, exhausted);
                if (archive.Length > MaxPreparedBytes * 2)
                    return Failure("Geometry clipboard archive exceeded the preparation limit; clipboard was not changed.", skipped, true);
                bool verifyMetadata = false;
                foreach (var scan in metadata) if (scan?.HasMetadata == true) { verifyMetadata = true; break; }
                if (verifyMetadata && !VerifyArchiveMetadata(archive, metadata, archiveIds, budget, current, out string metadataError))
                {
                    if (!current()) return Stale();
                    return Failure(budget.Limited ? "Copy metadata verification limit reached; clipboard was not changed." :
                        "Rhino could not verify all copied metadata (" + metadataError + "); clipboard was not changed.", skipped, exhausted || budget.Limited);
                }
                archiveObserver?.Invoke(archive);
                if (!current()) return Stale();
                if (!StillCurrent(sources, budget, false, current, out bool limit)) return limit ?
                    Failure("Copy cancelled; source reinspection limit reached; clipboard was not changed.", skipped, true) : Stale();
                bool unverifiedCustom = false;
                foreach (var scan in metadata) if (scan?.UnverifiedCustom == true) { unverifiedCustom = true; break; }
                bool commitLimit = false;
                bool CanCommit()
                {
                    if (!current()) return false;
                    bool valid = StillCurrent(sources, budget, false, current, out bool limited);
                    commitLimit |= limited;
                    return valid && current();
                }
                PanelGeometryActionResult Result(PanelCommitOutcome outcome) => outcome == PanelCommitOutcome.Deferred
                    ? new PanelGeometryActionResult(false, 0, 0, "Copy queued.", deferred: true)
                    : outcome == PanelCommitOutcome.Stale && commitLimit ? Failure("Copy cancelled; source reinspection limit reached; clipboard was not changed.", skipped, true)
                    : outcome == PanelCommitOutcome.Stale ? Stale()
                    : outcome == PanelCommitOutcome.Committed && !current() ? Failure("Copy context changed during clipboard publication; clipboard state is uncertain.")
                    : outcome == PanelCommitOutcome.Uncertain ? Failure("Rhino clipboard publication failed; clipboard state is uncertain.")
                    : new PanelGeometryActionResult(true, count, skipped, exhausted: exhausted,
                        detail: budget.Reason, unverifiedMetadata: unverifiedCustom);
                var state = CommitClipboard(
                    CanCommit,
                    () => { using (var clipboard = new Eto.Forms.Clipboard()) clipboard.SetData(archive, "Rhino 8 3DM Geometry"); },
                    outcome => completed?.Invoke(Result(outcome)));
                return Result(state);
            }
            catch (Exception ex) { return Failure("Could not copy geometry: " + ex.Message, skipped, exhausted); }
        }

        private static bool TryEstimateCopy(object value, PanelGeometrySources.Budget budget, ref int remaining,
            Func<bool> current, out long bytes, out PanelNativeGeometry.Scan? scan)
        {
            bytes = 8192;
            scan = null;
            if (value is Point3d || value is Line || value is Arc || value is Circle || value is Rectangle3d || value is Box) return true;
            if (!(value is GeometryBase geometry)) return false;
            if (!PanelNativeGeometry.TryScanForCopy(geometry, budget, ref remaining, current, out scan) || scan == null) return false;
            bytes = 65536L + scan.NativeBytes * 8L;
            return bytes <= MaxPreparedBytes;
        }

        private static GeometryBase? CopyLeaf(object value, PanelNativeGeometry.Scan? scan,
            PanelGeometrySources.Budget budget, ref int remaining, Func<bool> current, Func<bool> currentGraph)
        {
            if (value is Point3d p) return new Point(p);
            if (value is Line l) return new LineCurve(l);
            if (value is Arc a) return new ArcCurve(a);
            if (value is Circle c) return new ArcCurve(c);
            if (value is Rectangle3d r) return r.ToNurbsCurve();
            if (value is Box b) return b.ToBrep();
            if (value is GeometryBase g && scan != null)
            {
                GeometryBase? copy = null;
                try
                {
                    copy = g.Duplicate();
                    if (!current() || !currentGraph()) return null;
                    if (copy != null && copy.GetType() == g.GetType() &&
                        PanelNativeGeometry.TryScanForCopy(copy, budget, ref remaining, current, out var copied) && copied != null &&
                        PanelNativeGeometry.SameMetadata(scan, copied) && (copy is Brep || copy.IsValid))
                    { var result = copy; copy = null; return result; }
                }
                catch (Exception) { }
                finally { copy?.Dispose(); }
            }
            return null;
        }

        private static bool VerifyArchiveMetadata(byte[] archive, IReadOnlyList<PanelNativeGeometry.Scan?> expected,
            IReadOnlyList<Guid> objectIds, PanelGeometrySources.Budget budget, Func<bool> current, out string reason)
        {
            reason = "archive could not be read";
            try
            {
                using var file = File3dm.FromByteArray(archive);
                if (file == null || file.Objects.Count != expected.Count || objectIds.Count != expected.Count) return false;
                for (int index = 0; index < expected.Count; index++)
                {
                    // Object-table enumeration/indexing can produce null even
                    // when Count is nonzero. Add returned the stable object ID;
                    // use it to match each archive object to its own witness.
                    var item = file.Objects.FindId(objectIds[index]);
                    if (item == null) { reason = "archive object " + (index + 1) + " is unavailable"; return false; }
                    var witness = expected[index];
                    if (witness?.HasMetadata != true) continue;
                    int remaining = PanelGeometrySources.MaxNodesPerRoot;
                    var geometry = item.Geometry;
                    if (geometry == null || !PanelNativeGeometry.TryScanForCopy(geometry, budget, ref remaining, current, out var actual) || actual == null)
                    { reason = "archive object " + (index + 1) + " metadata could not be inspected"; return false; }
                    if (!PanelNativeGeometry.SameMetadata(witness, actual))
                    { reason = "archive object " + (index + 1) + " metadata differs"; return false; }
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        private static PanelGeometryActionResult Failure(string error, int skipped = 0, bool exhausted = false) =>
            new PanelGeometryActionResult(false, 0, skipped, error, exhausted);

        private static PanelGeometryActionResult Stale() => Failure("Data, selection or Rhino document settings changed; action cancelled.");

        private static bool FrameTargetCurrent(RhinoDoc document, RhinoView intended, Guid viewportId, Guid? detailId)
        {
            if (!ReferenceEquals(RhinoDoc.ActiveDoc, document)) return false;
            var view = document.Views.ActiveView;
            if (view == null || view.MainViewport.Id != intended.MainViewport.Id || view.ActiveViewport.Id != viewportId ||
                view.ActiveViewport.LockedProjection) return false;
            if (view is not RhinoPageView page) return !detailId.HasValue;
            if (page.PageIsActive || !detailId.HasValue) return false;
            var details = page.GetDetailViews();
            if (details == null) return false;
            foreach (var detail in details)
                if (detail.IsActive) return detail.Id == detailId.Value && !detail.DetailGeometry.IsProjectionLocked;
            return false;
        }

        private sealed class ActionSource
        {
            internal ActionSource(IGH_Goo? root, PanelGeometrySources.Inspection inspection) { Root = root; Inspection = inspection; }
            internal IGH_Goo? Root { get; }
            internal PanelGeometrySources.Inspection Inspection { get; }
        }

        private static bool StillCurrent(IEnumerable<ActionSource> sources, PanelGeometrySources.Budget budget, bool standalone,
            Func<bool> current, out bool limit)
        {
            limit = false;
            foreach (var source in sources)
            {
                var fresh = PanelGeometrySources.Inspect(source.Root, budget, standalone);
                if (fresh.Exhausted) { limit = true; return false; }
                if (!current() || !PanelGeometrySources.SameActionGraph(source.Inspection, fresh)) return false;
            }
            return current();
        }
    }
}
