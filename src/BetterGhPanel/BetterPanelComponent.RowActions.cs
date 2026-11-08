using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel
{
    public sealed partial class BetterPanelComponent
    {
        internal string DisplayValue(PanelSnapshot snapshot, PanelRow row, int sourcePosition = -1)
        {
            if (ReferenceEquals(snapshot, Session.Result.Snapshot) && Session.Result.DisplaySet?.Values.TryGetValue(sourcePosition, out var projected) == true)
                return projected.Text;
            var geometry = Session.GetGeometryDetails(snapshot, sourcePosition);
            if (geometry == null)
                geometry = Session.HasGeometryMeasurement(snapshot, sourcePosition)
                    ? row.GeometryInfo?.WithUnavailableMeasurement() : row.GeometryInfo;
            var options = Session.Options;
            if (options.DisplayFilterValue)
                return PanelFilterValue.Format(snapshot, row, options.SearchMode, DecimalPlaces, geometry, roundUpFactor: RoundUpFactor);
            var display = PanelValuePresentation.FormatDisplay(row, snapshot.UnitConversion, DecimalPlaces, geometry, roundUpFactor: RoundUpFactor);
            if (row.IsValid && geometry != null)
                return display + (Session.IsGeometryPending(snapshot, sourcePosition) ? " · measuring…" : "");
            return display;
        }

        internal string CopyValue(PanelSnapshot snapshot, int position)
        {
            if (!ReferenceEquals(snapshot, Session.Snapshot) || position < 0 || position >= snapshot.Rows.Count)
                throw new InvalidOperationException("The selected data has changed. Select the rows again.");
            var row = snapshot.Rows[position];
            if (Session.Result.DisplaySet?.Values.TryGetValue(position, out var projected) == true) return projected.CopyText;
            var options = Session.Options;
            if (options.DisplayFilterValue)
            {
                var metric = Session.GetGeometryDetails(snapshot, position);
                if (metric == null && Session.HasGeometryMeasurement(snapshot, position)) metric = row.GeometryInfo?.WithUnavailableMeasurement();
                var value = PanelFilterValue.Format(snapshot, row, options.SearchMode, DecimalPlaces, metric, includeUnits: false, roundUpFactor: RoundUpFactor);
                bool literalProperty = PanelProperties.IsProperty(options.SearchMode) &&
                    PanelProperties.TryGet(row, options.SearchMode, metric, out var property) && property != null && property.Kind != PanelPropertyKind.Number;
                if (!literalProperty && (value == "measuring…" || value == "unavailable")) throw new InvalidOperationException("A selected measurement is unavailable.");
                return value;
            }
            if (!row.IsValid)
                return PanelValuePresentation.FormatDisplay(row, snapshot.UnitConversion, DecimalPlaces);
            if (row.NumericValue.HasValue)
                return PanelUnits.Format(row.NumericValue.Value, PanelUnitConversion.None, DecimalPlaces);
            var geometry = Session.GetGeometryDetails(snapshot, position) ?? row.GeometryInfo;
            if (row.IsValid && geometry?.Value != null)
            {
                var value = PanelUnits.FormatGeometry(geometry.Value.Value, geometry.Power, geometry.Units,
                    snapshot.UnitConversion, DecimalPlaces, includeUnits: false, roundUpFactor: RoundUpFactor);
                if (value == "unavailable") throw new InvalidOperationException("A selected measurement is unavailable.");
                return value;
            }
            if (row.IsValid && geometry?.CanMeasure == true)
                throw new InvalidOperationException("A selected measurement is unavailable.");
            return PanelValuePresentation.FormatDisplay(row, snapshot.UnitConversion, DecimalPlaces, geometry, roundUpFactor: RoundUpFactor);
        }

        internal void CancelPendingCopy()
        {
            var cancellation = _copyCancellation;
            _copyCancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
        }

        private bool TryGetActionPositions(PanelSnapshot snapshot, IReadOnlyList<int> positions, out IReadOnlyList<int> ordered)
        {
            ordered = Array.Empty<int>();
            var session = _session;
            if (session == null || !session.HasCurrentResult || !ReferenceEquals(snapshot, session.Snapshot) || positions.Count == 0) return false;
            return PanelActionPositions.TryGet(session.Result, _sourceItems.Length, RowSelection, positions, out ordered);
        }

        private readonly struct ActionStamp
        {
            private readonly BetterPanelComponent _owner;
            private readonly PanelSession _session;
            private readonly PanelSnapshot _snapshot;
            private readonly PanelQueryResult _result;
            private readonly IGH_Goo?[] _source;
            private readonly long? _selection;
            internal ActionStamp(BetterPanelComponent owner, PanelSession session, PanelSnapshot snapshot,
                PanelQueryResult result, IGH_Goo?[] source, long? selection)
            { _owner = owner; _session = session; _snapshot = snapshot; _result = result; _source = source; _selection = selection; }
            internal bool IsCurrent() => ReferenceEquals(_owner._session, _session) && _session.HasCurrentResult &&
                ReferenceEquals(_session.Snapshot, _snapshot) && ReferenceEquals(_session.Result, _result) &&
                ReferenceEquals(_owner._sourceItems, _source) && (!_selection.HasValue || _owner._selectionVersion == _selection.Value);
        }

        internal string? FrameRows(PanelSnapshot snapshot, IReadOnlyList<int> positions)
            => FrameRowsWithCompletion(snapshot, positions, null);

        internal string? FrameRowsWithCompletion(PanelSnapshot snapshot, IReadOnlyList<int> positions, Action<string>? completed)
        {
            if (_previewSuspended) return "Activate this Grasshopper document to frame geometry";
            if (!TryGetActionPositions(snapshot, positions, out var ordered)) return "Data changed; select rows again";
            var session = _session!;
            var expectedResult = session.Result;
            var source = _sourceItems;
            var stamp = new ActionStamp(this, session, snapshot, expectedResult, source, _selectionVersion);
            bool Current() => !_previewSuspended && stamp.IsCurrent();
            string? Format(PanelGeometryActionResult result) => !result.Success ? result.Error :
                result.Skipped > 0 || result.Exhausted
                ? "Selection framed" + (result.Skipped > 0 ? "; " + result.Skipped + " skipped" : "") +
                    (result.Exhausted ? "; inspection limit reached" : "") : null;
            ordered = ExpandSetMembers(ordered);
            var result = PanelGeometryActions.FrameWithSnapshot(ordered.Select(position => source[position]).ToArray(), Current,
                () => Current() && CapturedSnapshotCurrent(snapshot) && Current(),
                value => completed?.Invoke(Format(value) ?? "Selection framed"));
            return Format(result);
        }

        internal void HighlightRows(PanelSnapshot snapshot, IReadOnlyList<int> positions)
        {
            var session = _session;
            if (_previewSuspended || Locked || PreviewMode == PanelPreviewMode.Off || session == null || !session.HasCurrentResult ||
                !ReferenceEquals(snapshot, session.Snapshot))
            {
                ClearHighlight();
                return;
            }
            IReadOnlyList<int> ordered = Array.Empty<int>();
            if (positions.Count > 0 && !TryGetActionPositions(snapshot, positions, out ordered))
            {
                ClearHighlight();
                return;
            }
            ordered = ExpandSetMembers(ordered);
            var previewPositions = PreviewMode == PanelPreviewMode.FilteredResults ? session.Result.DisplaySet?.AllMembers ?? session.Result.RowPositions : ordered;
            _highlight.SetOrderResult(ShowItemOrder && previewPositions.Count > 0 ? session.Result : null);
            var source = _sourceItems;
            var expectedResult = session.Result;
            var mode = PreviewMode;
            var stamp = new ActionStamp(this, session, snapshot, expectedResult, source, _selectionVersion);
            bool Current() => !_previewSuspended && !Locked && PreviewMode == mode &&
                stamp.IsCurrent();
            _highlight.SetScopeChecked(source, expectedResult, mode == PanelPreviewMode.FilteredResults, ordered, Current, RowSelection);
            InvalidatePreviewCaches();
            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        private IReadOnlyList<int> ExpandSetMembers(IReadOnlyList<int> positions) =>
            Session.Result.DisplaySet?.ExpandMembers(Session.Snapshot, positions) ?? positions;

        internal void ClearHighlight()
        {
            bool visible = _highlight.PreviewCount > 0;
            _highlight.Clear();
            if (visible) { InvalidatePreviewCaches(); Rhino.RhinoDoc.ActiveDoc?.Views.Redraw(); }
        }

        private void InvalidatePreviewCaches()
        {
            // GH's registered PreviewExpired handler also destroys the document
            // draw list and clipping box. Calling GH_Document.ExpirePreview here
            // would additionally mark a selection-only gesture as a document edit.
            ExpirePreview(false);
        }

        internal Task<string> CopyValuesAsync(PanelSnapshot snapshot, IReadOnlyList<int> positions) => CopyValuesCoreAsync(snapshot, positions);

        internal Task<string> CopyAddressesAsync(PanelSnapshot snapshot, IReadOnlyList<int> positions, PanelRowAction action, string? path = null)
            => CopyValuesCoreAsync(snapshot, positions, action, path);

        internal Task<string> CopyColumnValuesAsync(PanelSnapshot snapshot, IReadOnlyList<int> positions, int column, string? branch = null) =>
            CopyValuesCoreAsync(snapshot, positions, column: column, columnBranch: branch);

        private async Task<string> CopyValuesCoreAsync(PanelSnapshot snapshot, IReadOnlyList<int> positions,
            PanelRowAction action = PanelRowAction.CopyValues, string? path = null, int column = -1, string? columnBranch = null)
        {
            CancelPendingCopy();
            bool includeColumns = EffectiveTableVisible && action == PanelRowAction.CopyTable;
            var tableColumn = EffectiveTableVisible && column >= 0 && column < TableColumns.Count ? TableColumns[column] : null;
            if (column >= 0 && tableColumn == null) return "The column is no longer visible";
            var session = Session;
            var result = session.Result;
            if ((includeColumns || tableColumn != null) && !SummariesCurrent(result)) return "Column summaries are recomputing. Try copying again.";
            bool listLengthCopy = path == null && (action == PanelRowAction.CopyValues || includeColumns) && session.Options.DisplayFilterValue && session.Options.SearchMode == PanelSearchMode.ListLength;
            bool branchSet = path == null && (action == PanelRowAction.CopyValues || includeColumns) && result.DisplaySet?.IsBranchSet == true;
            IReadOnlyList<int> ordered;
            if (path != null || columnBranch != null || action == PanelRowAction.CopyPaths && RowSelection.Count > 0) ordered = positions;
            else if (branchSet || listLengthCopy) ordered = Array.Empty<int>();
            else if (!TryGetActionPositions(snapshot, positions, out ordered)) return "Data changed; select rows again";
            string? pathText = path == null ? null : action == PanelRowAction.CopyPathBare ? path.Trim('{', '}') : path;
            var branchSelection = branchSet || listLengthCopy ? new HashSet<string>(RowSelection.BranchPaths(result), StringComparer.Ordinal) : null;
            var branches = branchSet || listLengthCopy ? result.BranchPaths.Where(branchSelection!.Contains).ToArray() : Array.Empty<string>();
            if ((branchSet || listLengthCopy) && (branches.Length == 0 || !ReferenceEquals(snapshot, session.Snapshot))) return "Data changed; select branches again";
            if (columnBranch != null && (!result.ContainsBranchPath(columnBranch) || !ReferenceEquals(snapshot, session.Snapshot))) return "Data changed; select branches again";
            long clipboardGeneration = ClipboardGeneration.Begin();
            var source = _sourceItems;
            var stamp = new ActionStamp(this, session, snapshot, result, source, selection: null);
            long selectionVersion = RowSelection.Version;
            var cancellation = new CancellationTokenSource();
            _copyCancellation = cancellation;
            var measurementPositions = session.Options.DisplayFilterValue && PanelProperties.IsProperty(session.Options.SearchMode)
                ? ordered.Where(position => PanelProperties.TryGet(snapshot.Rows[position], session.Options.SearchMode,
                    session.GetGeometryDetails(snapshot, position), out var property) && property == null).ToArray() : ordered;
            bool addressOnly = action != PanelRowAction.CopyValues && action != PanelRowAction.CopyTable;
            var copyToken = cancellation.Token;
            var columnMeasurements = (includeColumns ? TableColumns : tableColumn != null ? new[] { tableColumn } : Array.Empty<PanelTableColumn>())
                .Select(value => (Column: value, Snapshot: value.Snapshot, Queue: value.Geometry,
                    Items: ColumnCopyItems(value, result, ordered, columnBranch != null ? new[] { columnBranch } : branches))).ToArray();
            bool measured = addressOnly || tableColumn != null || listLengthCopy || await session.MeasureGeometryAsync(snapshot, measurementPositions, copyToken);
            if (!addressOnly)
                foreach (var request in columnMeasurements)
                    if (request.Snapshot != null && request.Queue != null)
                        measured &= await request.Queue.MeasureAsync(request.Snapshot, request.Items, copyToken);
            var completion = new TaskCompletionSource<string>();
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                void Finish(PanelCommitOutcome outcome)
                {
                    string status = outcome == PanelCommitOutcome.Uncertain ? "Copy failed; clipboard state is uncertain." :
                        outcome == PanelCommitOutcome.Committed && !ClipboardGeneration.IsCurrent(clipboardGeneration)
                            ? "Copy context changed during clipboard publication; clipboard state is uncertain." :
                        outcome != PanelCommitOutcome.Committed ? "Copy cancelled" :
                        path != null ? "Copied " + pathText : addressOnly ? "Addresses copied" : action == PanelRowAction.CopyTable ? "Table copied" :
                        tableColumn != null ? ordered.Count <= 1 ? "Value copied" : ordered.Count + " values copied" :
                        branchSet || listLengthCopy ? branches.Length + (branches.Length == 1 ? " count copied" : " counts copied") :
                        ordered.Count == 1 ? "Value copied" : ordered.Count + " values copied";
                    completion.TrySetResult(status);
                    if (ReferenceEquals(_copyCancellation, cancellation)) { _copyCancellation = null; cancellation.Dispose(); }
                }
                try
                {
                    bool SelectionCurrent() => ClipboardGeneration.IsCurrent(clipboardGeneration) &&
                        ReferenceEquals(_copyCancellation, cancellation) && !cancellation.IsCancellationRequested && stamp.IsCurrent() &&
                        (action != PanelRowAction.CopyTable || includeColumns == EffectiveTableVisible) &&
                        (tableColumn == null || EffectiveTableVisible && TableColumns.Contains(tableColumn)) &&
                        (!(includeColumns || tableColumn != null) || SummariesCurrent(result)) &&
                        (path != null || columnBranch != null || action == PanelRowAction.CopyPaths || branchSet || listLengthCopy
                            ? RowSelection.Version == selectionVersion && (path == null || result.ContainsBranchPath(path)) && (columnBranch == null || result.ContainsBranchPath(columnBranch))
                            : PanelActionPositions.IsCurrent(result, source.Length, RowSelection, ordered, selectionVersion));
                    if (!measured || !SelectionCurrent()) Finish(PanelCommitOutcome.Stale);
                    else
                    {
                        if (!includeColumns)
                            foreach (var request in columnMeasurements) request.Column.RequireMeasurements(request.Items);
                        string text;
                        if (includeColumns) text = TableTsv(result, ordered);
                        else if (tableColumn != null && action == PanelRowAction.CopyValues)
                            text = columnBranch != null ? tableColumn.BranchCellText(result, columnBranch, UnitConversion, DecimalPlaces, RoundUpFactor, HeaderTitleForInput(tableColumn.Input)) :
                                string.Join(Environment.NewLine, ordered.Select(position => tableColumn.Cell(result, position, UnitConversion, DecimalPlaces, RoundUpFactor)));
                        else if (path != null) text = pathText!;
                        else if (action == PanelRowAction.CopyPaths)
                        {
                            var paths = new HashSet<string>(RowSelection.BranchPaths(result), StringComparer.Ordinal);
                            text = string.Join(Environment.NewLine, snapshot.Branches.Where(branch => paths.Contains(branch.Path)).Select(branch => branch.Path));
                        }
                        else if (action == PanelRowAction.CopyAddresses)
                            text = string.Join(Environment.NewLine, ordered.Select(position => PanelItemOrder.Address(result, position, OutputValues == PanelOutputValues.Items, FlattenTree)));
                        else if (action == PanelRowAction.CopyTable)
                        {
                            string title = HeaderTitleForInput(Params.Input[0], "Value");
                            title = PanelTableTsv.Escape(title);
                            text = "Path\tIndex\t" + title + Environment.NewLine + string.Join(Environment.NewLine, ordered.Select(position =>
                            {
                                PanelItemOrder.TryGetNumber(result, position, out int index);
                                string value = DisplayValue(snapshot, snapshot.Rows[position], position);
                                value = PanelTableTsv.Escape(value);
                                return snapshot.Rows[position].Path + "\t" + index.ToString(CultureInfo.InvariantCulture) + "\t" + value;
                            }));
                        }
                        else if (branchSet || listLengthCopy)
                            text = string.Join(Environment.NewLine, branches.Select(branch => snapshot.BranchItemCounts[branch].ToString(CultureInfo.InvariantCulture)));
                        else text = string.Join(Environment.NewLine, ordered.Select(position => CopyValue(snapshot, position)));
                        bool Current() => SelectionCurrent() && CapturedSnapshotCurrent(snapshot) && SelectionCurrent() &&
                            session.GeometrySourcesCurrent(snapshot) && SelectionCurrent() && columnMeasurements.All(request =>
                                request.Snapshot == null || ReferenceEquals(request.Column.Snapshot, request.Snapshot) &&
                                request.Queue?.SourcesCurrent(request.Snapshot) == true) && SelectionCurrent();
                        var state = PanelGeometryActions.CommitClipboard(Current,
                            () => { using (var clipboard = new Eto.Forms.Clipboard()) clipboard.Text = text; }, Finish);
                        if (state != PanelCommitOutcome.Deferred) Finish(state);
                    }
                }
                catch (Exception ex)
                {
                    completion.TrySetResult("Copy failed: " + ex.Message);
                    if (ReferenceEquals(_copyCancellation, cancellation)) { _copyCancellation = null; cancellation.Dispose(); }
                }
            }));
            return await completion.Task;
        }

        internal bool TryCopyGeometry(PanelSnapshot snapshot, IReadOnlyList<int> positions, out string status)
            => TryCopyGeometryWithCompletion(snapshot, positions, null, out status);

        internal bool TryCopyGeometryWithCompletion(PanelSnapshot snapshot, IReadOnlyList<int> positions,
            Action<string>? completed, out string status)
        {
            CancelPendingCopy();
            if (!TryGetActionPositions(snapshot, positions, out var ordered))
            {
                status = "Data changed; select rows again";
                return false;
            }
            var session = _session!;
            var expectedResult = session.Result;
            var source = _sourceItems;
            var stamp = new ActionStamp(this, session, snapshot, expectedResult, source, _selectionVersion);
            bool Current() => stamp.IsCurrent();
            string Format(PanelGeometryActionResult result) => result.Success ? result.Count + " geometries copied" +
                (result.Skipped > 0 ? "; " + result.Skipped + " skipped (" + (result.Detail ?? "unsupported geometry or unverified metadata") + ")" : "") +
                (result.Exhausted ? "; preparation limit reached" : "") +
                (result.UnverifiedMetadata ? "; custom metadata content not fully verified" : "") : result.Error ?? "Copy geometry failed";
            ordered = ExpandSetMembers(ordered);
            var result = PanelGeometryActions.CopyGeometryWithSnapshot(ordered.Select(position => source[position]).ToArray(),
                _geometryUnitSystem, _geometryTolerance, Current, () => Current() && CapturedSnapshotCurrent(snapshot) && Current(),
                value => completed?.Invoke(Format(value)));
            status = Format(result);
            return result.Success;
        }


        internal bool SelectionHasGeometry(IReadOnlyList<int> positions)
        {
            var source = _sourceItems;
            var budget = new PanelGeometrySources.OperationBudget();
            foreach (int position in ExpandSetMembers(positions))
            {
                if (position < 0 || position >= source.Length) continue;
                var item = source[position];
                if (item is GH_ObjectWrapper)
                {
                    using var inspected = PanelGeometrySources.Inspect(item, budget, allowStandalonePreview: true);
                    // Uncertain wrappers stay available; the action performs its
                    // full freshness and capability checks when invoked.
                    if (inspected.Unavailable || inspected.Leaves.Count > 0) return true;
                }
                else if (item is IGH_GeometricGoo || item is IGH_PreviewData) return true;
            }
            return false;
        }
    }
}
