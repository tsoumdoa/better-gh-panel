using System;
using System.Linq;
using System.Collections.Generic;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;
using BetterGhPanel.UI;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Undo;

namespace BetterGhPanel
{
    public sealed partial class BetterPanelComponent
    {
        private PanelQueryOptions _savedOptions = new PanelQueryOptions();
        private bool _restoringView;
        // Tree tools and Reset change what each address holds; their recompute
        // starts at the top instead of keeping the scroll position.
        private bool _discardViewRestore;
        // A live query edit previews the view only. Wire outputs, document
        // modification and downstream solutions wait for Enter.
        internal bool QueryPreviewActive { get; private set; }
        private PanelQueryOptions? _queryPreviewOptions;
        internal int PreviewSize => CanvasPanelSize.PresetIndex(CanvasWidth, CanvasHeight);
        internal float CanvasWidth { get; private set; } = CanvasPanelSize.DefaultWidth;
        internal float CanvasHeight { get; private set; } = CanvasPanelSize.DefaultHeight;
        internal CanvasPanelDisplay CanvasDisplay { get; private set; }
        internal bool PanelMinimized { get; private set; }
        internal bool PanelSplit { get; private set; }
        internal PanelPreviewMode PreviewMode { get; private set; }
        internal bool ShowItemOrder { get; private set; }
        internal bool ShowValues { get; private set; }
        internal PanelDotPlacement DotPlacement { get; private set; }
        internal PanelValuePalette ValuePalette { get; private set; }
        internal bool ReversePalette { get; private set; }

        internal void SetShowValues(bool value) => SetPreviewSetting("values", ShowValues, value, (c, v) => c.ShowValues = v);
        internal void SetDotPlacement(PanelDotPlacement value)
        { if (Enum.IsDefined(typeof(PanelDotPlacement), value)) SetPreviewSetting("dot placement", DotPlacement, value, (c, v) => c.DotPlacement = v); }
        internal void SetValuePalette(PanelValuePalette value)
        { if (Enum.IsDefined(typeof(PanelValuePalette), value)) SetPreviewSetting("colour by value", ValuePalette, value, (c, v) => c.ValuePalette = v); }
        internal void SetReversePalette(bool value) => SetPreviewSetting("reverse palette", ReversePalette, value, (c, v) => c.ReversePalette = v);
        private void SetPreviewSetting<T>(string name, T before, T after, Action<BetterPanelComponent, T> apply)
        {
            if (EqualityComparer<T>.Default.Equals(before, after)) return;
            void Apply(BetterPanelComponent component, T value)
            {
                apply(component, value);
                (component.Attributes as BetterPanelAttributes)?.OnQueryCompleted();
                component.OnPingDocument()?.Modified();
                Instances.ActiveCanvas?.Invalidate();
            }
            RecordUndoEvent("Panel+ " + name, new PanelUndo<T>(InstanceGuid, before, after, Apply));
            Apply(this, after);
        }
        internal string PreviewStatus => PreviewHiddenReason ?? _highlight.Status;
        // Grasshopper skips this component's drawing in these states, so say why nothing appears.
        internal string? PreviewHiddenReason
        {
            get
            {
                if (PreviewMode == PanelPreviewMode.Off || _highlight.PreviewCount == 0) return null;
                var document = OnPingDocument();
                return document?.PreviewMode == GH_PreviewMode.Disabled ? "Preview hidden: Grasshopper preview is off." :
                    Hidden ? "Preview hidden: this component's preview is off." :
                    document?.PreviewFilter == GH_PreviewFilter.Selected && !Attributes.Selected ? "Preview hidden: Grasshopper previews selected components only." : null;
            }
        }
        internal float EffectiveCanvasHeight => CanvasPanelSize.FullHeightForInputs(CanvasHeight, Params.Input.Count);
        internal float DisplayHeight => CanvasDisplay == CanvasPanelDisplay.Half ? EffectiveCanvasHeight / 2 : EffectiveCanvasHeight;
        internal float TableWidthScale { get; private set; } = 2;
        internal float DisplayWidth => CanvasWidth * (PanelSplit ? TableWidthScale : 1);
        private sealed class TableViewState
        {
            internal readonly bool Split;
            internal float Scale;
            internal bool Fit;
            internal TableViewState(bool split, float scale, bool fit = false) { Split = split; Scale = scale; Fit = fit; }
        }
        private TableViewState? _fittingTableState;
        private bool _tableWidthFitted;
        internal void FitTableWidth(float desired)
        {
            TableWidthScale = Math.Max(1, Math.Min(2, desired / CanvasWidth));
            _tableWidthFitted = true;
            if (_fittingTableState != null) { _fittingTableState.Scale = TableWidthScale; _fittingTableState.Fit = false; }
            _fittingTableState = null;
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
        }
        private void RestoreTableView(TableViewState state)
        {
            _fittingTableState = null;
            (Attributes as BetterPanelAttributes)?.CancelTableFit();
            TableWidthScale = state.Scale;
            ApplyPanelSplit(state.Split);
            if (state.Split && state.Fit)
            {
                _fittingTableState = state;
                (Attributes as BetterPanelAttributes)?.RequestTableFit();
            }
        }
        internal int TrimDepth { get; private set; }
        internal bool FlattenTree { get; private set; }
        internal bool ReverseTree { get; private set; }
        internal bool SimplifyTree { get; private set; }
        internal bool CreateSetTree { get; private set; }
        internal bool BranchStatsTree { get; private set; }
        internal PanelStatKinds StatKinds { get; private set; } = PanelBranchStats.Default;
        internal bool HasTreeOperations => BranchStatsTree || TrimDepth > 0 || FlattenTree || ReverseTree || SimplifyTree || CreateSetTree;
        internal PanelUnitConversion UnitConversion { get; private set; }
        internal int DecimalPlaces { get; private set; } = PanelUnits.AutomaticDecimals;
        internal double RoundUpFactor { get; private set; }
        internal PanelFormula? Formula { get; private set; }
        internal bool FormulaEditing { get; private set; }
        private PanelFormula? _formulaPreview;
        internal PanelFormula? PresentedFormula => FormulaEditing ? _formulaPreview : Formula;

        internal void BeginFormulaEdit()
        {
            _formulaPreview = Formula;
            FormulaEditing = true;
            BeginQueryPreview();
        }

        internal void PreviewFormula(PanelFormula formula)
        {
            if (!FormulaEditing) return;
            _formulaPreview = PanelFormula.NormalizeSetting(formula);
            var options = Session.Options;
            options.Formula = _formulaPreview;
            Session.UpdateQuery(options);
        }

        internal void EndFormulaEdit(PanelFormula? accepted, bool commit)
        {
            if (!FormulaEditing) return;
            var original = _queryPreviewOptions!;
            accepted = PanelFormula.NormalizeSetting(accepted);
            bool changed = commit && Formula?.Text != accepted?.Text;
            if (changed) RecordUndoEvent("Panel+ formula");
            FormulaEditing = false;
            _formulaPreview = null;
            if (changed)
            {
                Formula = accepted;
                CancelPendingCopy();
                RefreshFormulaItems();
                var options = Session.Options;
                options.Formula = Formula;
                Session.UpdateQuery(options);
            }
            else Session.UpdateQuery(original);
            // Keep the guard through query restoration/update, then publish a
            // changed setting once, through the same path as a search commit.
            EndQueryPreview(committed: changed);
            Attributes.ExpireLayout();
        }

        internal void SetFormula(PanelFormula? formula)
        {
            formula = PanelFormula.NormalizeSetting(formula);
            if (Formula?.Text == formula?.Text) return;
            RecordUndoEvent("Panel+ formula");
            Formula = formula;
            CancelPendingCopy();
            RefreshFormulaItems();
            var options = Session.Options;
            options.Formula = Formula;
            _restoringView = true;
            try { Session.UpdateQuery(options); }
            finally { _restoringView = false; }
            OnPingDocument()?.Modified();
            if (!Session.IsBusy) ScheduleOutput();
        }

        internal static bool HasFilters(PanelQueryOptions options) =>
            !string.IsNullOrEmpty(options.Search) || options.SearchMode != PanelSearchMode.Text || options.DisplayFilterValue ||
            options.TypeName != null || options.BranchPath != null || options.BranchPrefix || options.OnlyInvalid ||
            options.SortMode != PanelSortMode.SourceOrder;

        internal void ClearFilters()
        {
            CanvasTextInput.CloseActive(this);
            CanvasTextInput.CompletePending();
            if (HasFilters(Session.Options)) RecordUndoEvent("Panel+ clear filters");
            ClearFiltersCore();
        }

        private void ClearFiltersCore()
        {
            CancelPendingCopy();
            CanvasTextInput.CloseActive(this);
            (Attributes as BetterPanelAttributes)?.ResetViewState();
            RowSelection.Clear();
            var previousResult = Session.Result;
            var options = new PanelQueryOptions { DecimalPlaces = DecimalPlaces, RoundUpFactor = RoundUpFactor, Formula = Formula };
            if (!CanOutputValuesFor(CreateSetTree, options)) OutputValues = PanelOutputValues.Items;
            Session.UpdateQuery(options);
            if (ReferenceEquals(previousResult, Session.Result)) (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
        }

        internal void SetPreviewMode(PanelPreviewMode mode)
        {
            if (!Enum.IsDefined(typeof(PanelPreviewMode), mode) || PreviewMode == mode) return;
            RecordUndoEvent("Panel+ preview mode");
            PreviewMode = mode;
            (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetShowItemOrder(bool show)
        {
            if (ShowItemOrder == show) return;
            RecordUndoEvent("Panel+ item order", new PanelUndo<bool>(InstanceGuid, ShowItemOrder, show, (component, value) => component.ApplyShowItemOrder(value)));
            ApplyShowItemOrder(show);
        }

        private void ApplyShowItemOrder(bool show)
        {
            ShowItemOrder = show;
            (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        private sealed class PanelUndo<T> : GH_UndoAction
        {
            private readonly Guid _component;
            private readonly T _before, _after;
            private readonly Action<BetterPanelComponent, T> _apply;
            internal PanelUndo(Guid component, T before, T after, Action<BetterPanelComponent, T> apply)
            { _component = component; _before = before; _after = after; _apply = apply; }
            public override bool ExpiresSolution => false;
            public override bool ExpiresDisplay => false;
            protected override void Internal_Undo(GH_Document doc) => Apply(doc, _before);
            protected override void Internal_Redo(GH_Document doc) => Apply(doc, _after);
            private void Apply(GH_Document doc, T value)
            {
                if (doc.FindObject(_component, false) is BetterPanelComponent component) _apply(component, value);
            }
        }

        internal void BeginQueryPreview()
        {
            _queryPreviewOptions = Session.Options;
            _queryPreviewSelection = _committedSelection.CopyForPreview();
            QueryPreviewActive = true;
            (Attributes as BetterPanelAttributes)?.UseRowSelection(RowSelection);
        }

        internal void RecordQueryUndo(string name, PanelQueryOptions before, PanelQueryOptions after)
        {
            if (!PanelQueryPolicy.SameQuery(before, after))
            {
                var beforeValues = OutputValues;
                var afterValues = CanOutputValuesFor(CreateSetTree, after) ? beforeValues : PanelOutputValues.Items;
                var beforeSettings = before.Clone(); beforeSettings.ColumnSortKeys = null; beforeSettings.TableSource = null;
                var afterSettings = after.Clone(); afterSettings.ColumnSortKeys = null; afterSettings.TableSource = null;
                RecordUndoEvent(name, new PanelUndo<Tuple<PanelQueryOptions, PanelOutputValues>>(InstanceGuid,
                    Tuple.Create(beforeSettings, beforeValues), Tuple.Create(afterSettings, afterValues),
                    (component, state) => { CanvasTextInput.CloseActive(component); (component.Attributes as BetterPanelAttributes)?.CancelQueryEditor(); component.OutputValues = state.Item2; component.Session.UpdateQuery(component.WithCurrentColumnSort(state.Item1)); component.ScheduleOutput(); }));
            }
        }

        // Ends a live edit. A committed edit publishes like any other query change;
        // an abandoned one restores the original query without marking the document.
        // Either can publish an output that was already pending before the preview.
        internal void EndQueryPreview(bool committed)
        {
            if (!QueryPreviewActive) return;
            _queryPreviewSelection = null;
            _queryPreviewOptions = null;
            QueryPreviewActive = false;
            if (committed && !CanOutputValues) OutputValues = PanelOutputValues.Items;
            (Attributes as BetterPanelAttributes)?.UseRowSelection(RowSelection);
            (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
            if (committed) OnPingDocument()?.Modified();
            if (!Session.IsBusy) ScheduleOutput();
        }

        internal bool ResetAll()
        {
            CanvasTextInput.CloseActive(this);
            CanvasTextInput.CompletePending();
            _discardViewRestore = true;
            var options = Session.Options;
            bool outputChanged = HasTreeOperations || UnitConversion != PanelUnitConversion.None || RoundUpFactor > 0 || Formula != null || DecimalPlaces >= 0;
            bool settingsChanged = outputChanged || OutputScope != PanelOutputScope.Filtered || OutputValues != PanelOutputValues.Items || DecimalPlaces != PanelUnits.AutomaticDecimals ||
                !string.IsNullOrEmpty(options.Search) || options.SearchMode != PanelSearchMode.Text || options.TypeName != null || options.BranchPath != null ||
                options.BranchPrefix || options.OnlyInvalid || options.SortMode != PanelSortMode.SourceOrder;
            if (settingsChanged) RecordUndoEvent("Panel+ reset all");
            TrimDepth = 0;
            FlattenTree = ReverseTree = SimplifyTree = CreateSetTree = BranchStatsTree = false;
            UnitConversion = PanelUnitConversion.None;
            RoundUpFactor = 0;
            Formula = null;
            DecimalPlaces = PanelUnits.AutomaticDecimals;
            OutputScope = PanelOutputScope.Filtered;
            UpdateOutputDescriptions();
            OutputValues = PanelOutputValues.Items;
            ClearFiltersCore();
            if (settingsChanged) OnPingDocument()?.Modified();
            try
            {
                if (outputChanged)
                {
                    if (!_completingInClearData) ExpireSolution(true);
                }
                else ScheduleOutput();
            }
            finally { _discardViewRestore = false; }
            Instances.ActiveCanvas?.Invalidate();
            return settingsChanged;
        }

        internal void SetCanvasSize(float width, float height)
        {
            (Attributes as BetterPanelAttributes)?.InvalidatePaintedHits();
            (Attributes as BetterPanelAttributes)?.CancelTableFit();
            if (_fittingTableState != null) _fittingTableState.Fit = false;
            _fittingTableState = null;
            CanvasWidth = CanvasPanelSize.Width(width);
            CanvasHeight = CanvasPanelSize.Height(height);
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetTreeOperations(int trimDepth, bool flatten, bool reverse, bool simplify, bool createSet)
        {
            trimDepth = Math.Max(0, trimDepth);
            if (TrimDepth == trimDepth && FlattenTree == flatten && ReverseTree == reverse && SimplifyTree == simplify && CreateSetTree == createSet) return;
            bool pathsChanged = TrimDepth != trimDepth || FlattenTree != flatten || SimplifyTree != simplify;
            RecordUndoEvent("Panel+ tree tools");
            TrimDepth = trimDepth;
            FlattenTree = flatten;
            ReverseTree = reverse;
            SimplifyTree = simplify;
            CreateSetTree = createSet;
            if (createSet) BranchStatsTree = false;
            if (!CanOutputValues) OutputValues = PanelOutputValues.Items;
            if (pathsChanged)
            {
                // Reverse and Create set preserve paths, so branch selection stays valid.
                var options = Session.Options;
                options.BranchPath = null;
                options.BranchPrefix = false;
                Session.UpdateQuery(options);
            }
            else Session.UpdateQuery(Session.Options);
            OnPingDocument()?.Modified();
            _discardViewRestore = true;
            try { if (!_completingInClearData) ExpireSolution(true); }
            finally { _discardViewRestore = false; }
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetBranchStats(bool enabled, PanelStatKinds? picks = null)
        {
            var kinds = PanelBranchStats.Normalize(picks ?? StatKinds);
            if (enabled == BranchStatsTree && kinds == StatKinds) return;
            RecordUndoEvent("Panel+ branch stats");
            BranchStatsTree = enabled;
            StatKinds = kinds;
            if (enabled) CreateSetTree = false;
            RowSelection.Clear();
            Session.UpdateQuery(Session.Options);
            OnPingDocument()?.Modified();
            if (!Session.IsBusy) ScheduleOutput();
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetCanvasDisplay(CanvasPanelDisplay display)
        {
            if (!Enum.IsDefined(typeof(CanvasPanelDisplay), display) || CanvasDisplay == display) return;
            (Attributes as BetterPanelAttributes)?.InvalidatePaintedHits();
            RecordUndoEvent("Panel+ full/half");
            CanvasTextInput.CloseActive();
            CanvasDisplay = display;
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetPanelMinimized(bool minimized)
        {
            if (PanelMinimized == minimized) return;
            // A generic component undo reloads the data and clears transient row
            // state. This action changes presentation only, including on undo.
            RecordUndoEvent(minimized ? "Panel+ minimize" : "Panel+ restore",
                new PanelUndo<bool>(InstanceGuid, PanelMinimized, minimized, (component, value) => component.ApplyPanelMinimized(value)));
            ApplyPanelMinimized(minimized);
        }

        internal readonly System.Collections.Generic.Dictionary<Guid, float> ColumnWidths = new System.Collections.Generic.Dictionary<Guid, float>();
        internal void AddColumn()
        {
            RecordUndoEvent("Panel+ Add column");
            int index = HeaderInput is { } header ? Params.Input.IndexOf(header) : Params.Input.Count;
            Params.RegisterInputParam(CreateParameter(GH_ParameterSide.Input, index), index);
            ColumnsChanged();
        }
        internal void RemoveColumn(IGH_Param input)
        {
            if (Params.Input.IndexOf(input) < 2 || IsHeaderInput(input)) return;
            RecordUndoEvent("Panel+ Remove column");
            bool sorted = Session.Options.SortColumn == input.InstanceGuid;
            ColumnWidths.Remove(input.InstanceGuid); _columnInputIndices.Clear();
            if (sorted)
            {
                _sortInputIndex = -1;
                var options = Session.Options; options.SortColumn = Guid.Empty; options.SortMode = PanelSortMode.SourceOrder; options.ColumnSortKeys = null;
                Session.PrepareColumnSort(Guid.Empty, null); Session.UpdateQuery(options);
            }
            else if (_sortInputIndex > Params.Input.IndexOf(input)) _sortInputIndex--;
            Params.UnregisterInputParameter(input, true);
            ColumnsChanged();
        }
        private void ColumnsChanged()
        {
            Params.OnParametersChanged();
            VariableParameterMaintenance();
            Attributes.ExpireLayout();
            ExpireSolution(true);
        }
        private readonly Dictionary<Guid, int> _columnInputIndices = new Dictionary<Guid, int>();
        private int _sortInputIndex = -1;
        internal float ColumnWidthLimit(Guid id) => id == Params.Input[0].InstanceGuid
            ? (Attributes as BetterPanelAttributes)?.DColumnWidthLimit ?? Math.Max(PanelTableText.MinimumColumnWidth, DisplayWidth - 33 - PanelTableText.MinimumColumnWidth - 24)
            : PanelTableText.MaximumColumnWidth;
        internal void ResolveColumnWidths()
        {
            foreach (var entry in ColumnWidths.ToArray())
            {
                if (Params.Input.Any(input => input.InstanceGuid == entry.Key && IsTableDataInput(input))) continue;
                ColumnWidths.Remove(entry.Key);
                if (HeaderInput?.InstanceGuid != entry.Key && _columnInputIndices.TryGetValue(entry.Key, out int index) &&
                    index >= 0 && index < Params.Input.Count && IsTableDataInput(Params.Input[index]))
                { var id = Params.Input[index].InstanceGuid; ColumnWidths[id] = entry.Value; _columnInputIndices[id] = index; }
            }
        }
        private void ResolveSortInput(PanelQueryOptions options)
        {
            if (options.SortColumn == Guid.Empty) return;
            options.SortColumn = HeaderInput?.InstanceGuid == options.SortColumn ? Guid.Empty :
                PanelTableInputIdentity.Resolve(options.SortColumn, Params.Input.Select(input => IsHeaderInput(input) ? Guid.Empty : input.InstanceGuid).ToArray(),
                    _sortInputIndex >= 1 ? _sortInputIndex : -1);
            if (options.SortColumn == Guid.Empty) { options.SortMode = PanelSortMode.SourceOrder; options.ColumnSortKeys = null; }
        }

        private float RequestedColumnWidth(Guid id, float width) => Math.Max(PanelTableText.MinimumColumnWidth,
            id == Params.Input[0].InstanceGuid ? width : Math.Min(PanelTableText.MaximumColumnWidth, width));

        internal void SetColumnWidth(Guid id, float? width)
        {
            if (HeaderInput?.InstanceGuid == id) return;
            var before = ColumnWidths.TryGetValue(id, out float value) ? (float?)value : null;
            if (before == width) return;
            RecordUndoEvent("Panel+ Column width", new PanelUndo<float?>(InstanceGuid, before, width,
                (component, next) => component.ApplyColumnWidth(id, next)));
            ApplyColumnWidth(id, width);
        }
        private void ApplyColumnWidth(Guid id, float? width)
        {
            (Attributes as BetterPanelAttributes)?.ClearHover();
            (Attributes as BetterPanelAttributes)?.InvalidatePaintedHits();
            if (width.HasValue) { ColumnWidths[id] = RequestedColumnWidth(id, width.Value);
                _columnInputIndices[id] = Params.Input.FindIndex(input => input.InstanceGuid == id); }
            else ColumnWidths.Remove(id);
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }
        internal void SetColumnSort(Guid id, PanelSortMode mode)
        {
            if (HeaderInput?.InstanceGuid == id) return;
            var before = Session.Options;
            if (id == Params.Input[0].InstanceGuid || mode == PanelSortMode.SourceOrder) id = Guid.Empty;
            if (before.SortColumn == id && before.SortMode == mode) return;
            var options = before.Clone();
            options.SortColumn = id; options.SortMode = mode;
            _sortInputIndex = id == Guid.Empty ? -1 : Params.Input.FindIndex(input => input.InstanceGuid == id);
            options = WithCurrentColumnSort(options);
            RecordQueryUndo("Panel+ Sort", before, options);
            Session.UpdateQuery(options);
        }

        internal void SetPanelSplit(bool split)
        {
            if (PanelSplit == split || split && !CanSplit) return;
            var before = new TableViewState(PanelSplit, TableWidthScale);
            var after = new TableViewState(split, TableWidthScale, split && !_tableWidthFitted && TableWidthScale == 2); // fit once; later toggles keep the chosen width
            RecordUndoEvent("Panel+ Table view", new PanelUndo<TableViewState>(InstanceGuid, before, after,
                (component, state) => component.RestoreTableView(state)));
            RestoreTableView(after);
        }

        private void ApplyPanelSplit(bool split)
        {
            CanvasTextInput.CloseActive(this);
            (Attributes as BetterPanelAttributes)?.ClosePresentationControls();
            PanelSplit = split;
            if (!split)
            {
                (Attributes as BetterPanelAttributes)?.ClearTableWidths();
            }
            foreach (var column in _tableColumns)
            {
                if (split || Session.Options.SortColumn == column.Input.InstanceGuid) column.Capture(_geometryUnits);
                else column.ReleasePresentation();
            }
            if (ContextFenced && !GeometryContextCurrent(_geometryContext) && _session != null)
            {
                _captureRetries = 0;
                InvalidateStaleCapture(_session, OnPingDocument(), _solveGeneration);
            }
            ConfigureColumnGeometry();
            RefreshTableSource();
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        private static bool HasGeometry(PanelSnapshot snapshot) =>
            snapshot.Rows.Any(row => row.GeometryInfo != null || row.Components != null);

        private void ApplyPanelMinimized(bool minimized)
        {
            CanvasTextInput.CloseActive(this);
            (Attributes as BetterPanelAttributes)?.ClosePresentationControls();
            PanelMinimized = minimized;
            if (PanelSplit) RefreshTableSource();
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetUnits(PanelUnitConversion conversion, int decimals, double? roundUpFactor = null)
        {
            if (!Enum.IsDefined(typeof(PanelUnitConversion), conversion)) conversion = PanelUnitConversion.None;
            decimals = Math.Max(PanelUnits.AutomaticDecimals, Math.Min(PanelUnits.MaximumDecimals, decimals));
            double factor = roundUpFactor ?? RoundUpFactor;
            if (!PanelUnits.IsRoundingFactor(factor)) factor = 0;
            if (UnitConversion == conversion && RoundUpFactor == factor && DecimalPlaces == decimals) return;
            CancelPendingCopy();
            RecordUndoEvent("Panel+ units, rounding and decimals");
            UnitConversion = conversion;
            RoundUpFactor = factor;
            DecimalPlaces = decimals;
            OnPingDocument()?.Modified();
            _retainGeometryMeasurements = true;
            try { if (!_completingInClearData) ExpireSolution(true); }
            finally { _retainGeometryMeasurements = false; }
            Instances.ActiveCanvas?.Invalidate();
        }

        public override bool Write(GH_IWriter writer)
        {
            var options = _queryPreviewOptions ?? _session?.Options ?? _savedOptions;
            writer.SetString("PanelSearch", options.Search ?? "");
            writer.SetInt32("PanelSearchMode", (int)options.SearchMode);
            writer.SetBoolean("PanelDisplayFilterValue", options.DisplayFilterValue);
            writer.SetString("PanelType", options.TypeName ?? "");
            writer.SetString("PanelBranch", options.BranchPath ?? "");
            writer.SetBoolean("PanelPrefix", options.BranchPrefix);
            writer.SetBoolean("PanelInvalid", options.OnlyInvalid);
            ResolveColumnWidths(); ResolveSortInput(options);
            writer.SetInt32("PanelSort", (int)options.SortMode);
            if (options.SortColumn != Guid.Empty)
            {
                writer.SetGuid("PanelSortColumn", options.SortColumn);
                writer.SetInt32("PanelSortInputIndex", Params.Input.FindIndex(input => input.InstanceGuid == options.SortColumn));
            }
            var savedWidths = ColumnWidths.Where(entry => Params.Input.Any(input => input.InstanceGuid == entry.Key && IsTableDataInput(input))).ToArray();
            if (savedWidths.Length > 0)
            {
                var widths = writer.CreateChunk("PanelColumnWidths");
                foreach (var entry in savedWidths)
                {
                    float width = RequestedColumnWidth(entry.Key, entry.Value);
                    widths.SetSingle(entry.Key.ToString("D"), width);
                    widths.SetSingle("InputIndex" + Params.Input.FindIndex(input => input.InstanceGuid == entry.Key), width);
                }
            }
            // Retain the legacy key for older plug-in versions; dimensions take precedence.
            writer.SetInt32("PanelSize", PreviewSize < 0 ? 1 : PreviewSize);
            writer.SetSingle("PanelWidth", CanvasWidth);
            if (TableWidthScale != 2) writer.SetSingle("PanelTableWidthScale", TableWidthScale);
            writer.SetSingle("PanelHeight", CanvasHeight);
            writer.SetInt32("PanelDisplay", (int)CanvasDisplay);
            writer.SetBoolean("PanelMinimized", PanelMinimized);
            writer.SetBoolean("PanelSplit", PanelSplit);
            writer.SetInt32("PanelPreviewMode", (int)PreviewMode);
            writer.SetBoolean("PanelShowItemOrder", ShowItemOrder);
            writer.SetBoolean("PanelShowValues", ShowValues);
            writer.SetInt32("PanelDotPlacement", (int)DotPlacement);
            writer.SetInt32("PanelValuePalette", (int)ValuePalette);
            writer.SetBoolean("PanelReversePalette", ReversePalette);
            writer.SetInt32("PanelTrimDepth", TrimDepth);
            writer.SetBoolean("PanelFlatten", FlattenTree);
            writer.SetBoolean("PanelReverse", ReverseTree);
            writer.SetBoolean("PanelSimplify", SimplifyTree);
            writer.SetBoolean("PanelCreateSet", CreateSetTree);
            writer.SetBoolean("PanelBranchStats", BranchStatsTree);
            writer.SetInt32("PanelStatKinds", (int)StatKinds);
            writer.SetInt32("PanelUnitConversion", (int)UnitConversion);
            writer.SetInt32("PanelDecimalPlaces", DecimalPlaces);
            writer.SetDouble("PanelRoundUpFactor", RoundUpFactor);
            writer.SetString("PanelFormula", Formula?.Text ?? "");
            writer.SetInt32("PanelOutputScope", (int)OutputScope);
            writer.SetInt32("PanelOutputValues", (int)OutputValues);
            writer.SetInt32("PanelOutputSchema", 1);
            WriteHeaderInput(writer);
            bool success = base.Write(writer);
            // 0.2.8 and earlier have a fixed parameter server. Keep its base-port
            // chunks alongside GH's variable archive so it can ignore added columns.
            for (int i = 0; i < Math.Min(2, Params.Input.Count); i++)
                success &= Params.Input[i].Write(writer.CreateChunk("param_input", i));
            for (int i = 0; i < Math.Min(4, Params.Output.Count); i++)
                success &= Params.Output[i].Write(writer.CreateChunk("param_output", i));
            return success;
        }

        // Also usable without Grasshopper's parameter/plugin loader.
        internal bool ReadTreeTools(GH_IReader reader)
        {
            int trimDepth = 0;
            bool flatten = false, reverse = false, simplify = false, createSet = false;
            reader.TryGetInt32("PanelTrimDepth", ref trimDepth);
            reader.TryGetBoolean("PanelFlatten", ref flatten);
            reader.TryGetBoolean("PanelReverse", ref reverse);
            reader.TryGetBoolean("PanelSimplify", ref simplify);
            reader.TryGetBoolean("PanelCreateSet", ref createSet);
            bool branchStats = false; int statKinds = (int)PanelBranchStats.Default;
            reader.TryGetBoolean("PanelBranchStats", ref branchStats);
            reader.TryGetInt32("PanelStatKinds", ref statKinds);
            BranchStatsTree = branchStats; StatKinds = PanelBranchStats.Normalize((PanelStatKinds)statKinds);
            TrimDepth = Math.Max(0, trimDepth);
            FlattenTree = flatten;
            ReverseTree = reverse;
            SimplifyTree = simplify;
            CreateSetTree = createSet && !BranchStatsTree;
            return createSet;
        }

        public override bool Read(GH_IReader reader)
        {
            ReadHeaderInput(reader);
            ++_solveGeneration;
            ++_outputRequest;
            _queryTree = null;
            _scalarInputs = _presentedScalars = Array.Empty<CapturedScalar>();
            _reuseQuery = false;
            ClearOutputSources();
            _sourceItems = Array.Empty<IGH_Goo?>();
            _measurementSources = Array.Empty<CapturedMeasurementSource?>();
            _measurementSnapshot = null;
            _staleMeasurementSnapshot = null;
            _session?.InvalidateInput(false, suspended: Locked);
            CancelPendingCopy();
            ClearHighlight();
            (Attributes as BetterPanelAttributes)?.ResetViewState();
            RowSelection.Clear();
            var search = ""; var type = ""; var branch = "";
            var prefix = false; var invalid = false; var sort = 0; var size = 1; var searchMode = 0;
            reader.TryGetString("PanelSearch", ref search);
            reader.TryGetInt32("PanelSearchMode", ref searchMode);
            bool displayFilterValue = false;
            reader.TryGetBoolean("PanelDisplayFilterValue", ref displayFilterValue);
            reader.TryGetString("PanelType", ref type);
            reader.TryGetString("PanelBranch", ref branch);
            reader.TryGetBoolean("PanelPrefix", ref prefix);
            reader.TryGetBoolean("PanelInvalid", ref invalid);
            reader.TryGetInt32("PanelSort", ref sort);
            Guid sortColumn = Guid.Empty;
            reader.TryGetGuid("PanelSortColumn", ref sortColumn);
            _sortInputIndex = -1;
            reader.TryGetInt32("PanelSortInputIndex", ref _sortInputIndex);
            bool hasLegacySize = reader.TryGetInt32("PanelSize", ref size);
            _savedOptions = new PanelQueryOptions
            {
                Search = search,
                SortColumn = sortColumn,
                TypeName = string.IsNullOrEmpty(type) ? null : type,
                DisplayFilterValue = displayFilterValue && searchMode >= (int)PanelSearchMode.Value && Enum.IsDefined(typeof(PanelSearchMode), searchMode),
                SearchMode = Enum.IsDefined(typeof(PanelSearchMode), searchMode) ? (PanelSearchMode)searchMode : PanelSearchMode.Text,
                BranchPath = string.IsNullOrEmpty(branch) ? null : branch,
                BranchPrefix = prefix,
                OnlyInvalid = invalid,
                SortMode = Enum.IsDefined(typeof(PanelSortMode), sort) ? (PanelSortMode)sort : PanelSortMode.SourceOrder
            };
            size = Math.Max(0, Math.Min(CanvasPanelSize.PresetNames.Length - 1, size));
            float width = hasLegacySize ? CanvasPanelSize.PresetWidth(size) : CanvasPanelSize.DefaultWidth;
            float height = hasLegacySize ? CanvasPanelSize.PresetHeight(size) : CanvasPanelSize.DefaultHeight;
            reader.TryGetSingle("PanelWidth", ref width);
            reader.TryGetSingle("PanelHeight", ref height);
            CanvasWidth = CanvasPanelSize.Width(width);
            CanvasHeight = CanvasPanelSize.Height(height);
            float tableScale = 2;
            reader.TryGetSingle("PanelTableWidthScale", ref tableScale);
            TableWidthScale = float.IsNaN(tableScale) || float.IsInfinity(tableScale) ? 2 : Math.Max(1, Math.Min(2, tableScale));
            _fittingTableState = null;
            (Attributes as BetterPanelAttributes)?.CancelTableFit();
            int display = 0;
            reader.TryGetInt32("PanelDisplay", ref display);
            CanvasDisplay = Enum.IsDefined(typeof(CanvasPanelDisplay), display)
                ? (CanvasPanelDisplay)display : CanvasPanelDisplay.Full;
            bool minimized = false;
            reader.TryGetBoolean("PanelMinimized", ref minimized);
            PanelMinimized = minimized;
            bool split = false;
            reader.TryGetBoolean("PanelSplit", ref split);
            PanelSplit = split;
            int previewMode = 0;
            reader.TryGetInt32("PanelPreviewMode", ref previewMode);
            PreviewMode = Enum.IsDefined(typeof(PanelPreviewMode), previewMode)
                ? (PanelPreviewMode)previewMode : PanelPreviewMode.Selected;
            bool showOrder = false;
            reader.TryGetBoolean("PanelShowItemOrder", ref showOrder);
            ShowItemOrder = showOrder;
            bool showValues = false, reversePalette = false;
            int dotPlacement = 0, valuePalette = 0;
            reader.TryGetBoolean("PanelShowValues", ref showValues);
            reader.TryGetBoolean("PanelReversePalette", ref reversePalette);
            reader.TryGetInt32("PanelDotPlacement", ref dotPlacement);
            reader.TryGetInt32("PanelValuePalette", ref valuePalette);
            ShowValues = showValues; ReversePalette = reversePalette;
            DotPlacement = Enum.IsDefined(typeof(PanelDotPlacement), dotPlacement) ? (PanelDotPlacement)dotPlacement : PanelDotPlacement.Centre;
            ValuePalette = Enum.IsDefined(typeof(PanelValuePalette), valuePalette) ? (PanelValuePalette)valuePalette : PanelValuePalette.Off;
            bool createSet = ReadTreeTools(reader);
            int outputValues = createSet ? (int)PanelOutputValues.Values : (int)PanelOutputValues.Items;
            reader.TryGetInt32("PanelOutputValues", ref outputValues);
            OutputValues = Enum.IsDefined(typeof(PanelOutputValues), outputValues) ? (PanelOutputValues)outputValues : PanelOutputValues.Items;
            int conversion = 0, decimals = PanelUnits.AutomaticDecimals;
            reader.TryGetInt32("PanelUnitConversion", ref conversion);
            reader.TryGetInt32("PanelDecimalPlaces", ref decimals);
            UnitConversion = Enum.IsDefined(typeof(PanelUnitConversion), conversion)
                ? (PanelUnitConversion)conversion : PanelUnitConversion.None;
            DecimalPlaces = Math.Max(PanelUnits.AutomaticDecimals, Math.Min(PanelUnits.MaximumDecimals, decimals));
            double roundUpFactor = 0;
            reader.TryGetDouble("PanelRoundUpFactor", ref roundUpFactor);
            RoundUpFactor = PanelUnits.IsRoundingFactor(roundUpFactor) ? roundUpFactor : 0;
            string formulaText = "";
            reader.TryGetString("PanelFormula", ref formulaText);
            PanelFormula.TryCompile(formulaText, out var formula, out _);
            Formula = PanelFormula.NormalizeSetting(formula);
            FormulaEditing = false;
            _formulaPreview = null;
            int outputScope = 0;
            reader.TryGetInt32("PanelOutputScope", ref outputScope);
            OutputScope = Enum.IsDefined(typeof(PanelOutputScope), outputScope) ? (PanelOutputScope)outputScope : PanelOutputScope.Filtered;
            _savedOptions.DecimalPlaces = DecimalPlaces;
            _savedOptions.RoundUpFactor = RoundUpFactor;
            _savedOptions.Formula = Formula;
            if (!CanOutputValuesFor(CreateSetTree, _savedOptions)) OutputValues = PanelOutputValues.Items;
            var parameters = UpgradeLegacyOutputParameters(reader);
            if (!parameters.ChunkExists("ParameterData") && !parameters.ChunkExists("ParamTypeData"))
            {
                // Fixed-port files predate IGH_VariableParameterComponent. Adapt their
                // chunks to GH's variable archive without losing parameter/wire GUIDs.
                for (int i = 0; i < 2; i++)
                    if (!parameters.ChunkExists("param_input", i))
                    { reader.AddMessage("Input parameter chunk is missing. Archive is corrupt.", GH_Message_Type.error); return false; }
                for (int i = 0; i < 4; i++)
                    if (!parameters.ChunkExists("param_output", i))
                    { reader.AddMessage("Output parameter chunk is missing. Archive is corrupt.", GH_Message_Type.error); return false; }
                parameters = UpgradeFixedParameterArchive(parameters);
            }
            var success = base.Read(parameters);
            VariableParameterMaintenance();
            ColumnWidths.Clear(); _columnInputIndices.Clear();
            var widths = reader.FindChunk("PanelColumnWidths");
            for (int index = 0; index < Params.Input.Count; index++)
            {
                var input = Params.Input[index];
                if (IsHeaderInput(input)) continue;
                float value = 0;
                if (widths != null && (widths.TryGetSingle(input.InstanceGuid.ToString("D"), ref value) || widths.TryGetSingle("InputIndex" + index, ref value)) &&
                    !float.IsNaN(value) && !float.IsInfinity(value))
                {
                    ColumnWidths[input.InstanceGuid] = RequestedColumnWidth(input.InstanceGuid, value);
                    _columnInputIndices[input.InstanceGuid] = index;
                }
            }
            ResolveSortInput(_savedOptions);
            _restoringView = true;
            try { _session?.UpdateQuery(_savedOptions); }
            finally { _restoringView = false; }
            UpdateOutputDescriptions();
            (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
            Attributes?.ExpireLayout();
            return success;
        }
        private static GH_IReader UpgradeFixedParameterArchive(GH_IReader reader)
        {
            if (reader is not GH_Chunk original) return reader;
            var upgraded = GH_Chunk.InstantiateRoot(original.Archive ?? new GH_Archive());
            upgraded.CopyValuesFromChunk(original);
            var parameters = upgraded.CreateChunk("ParameterData");
            parameters.SetInt32("InputCount", 2);
            parameters.SetInt32("OutputCount", 4);
            var defaults = new BetterPanelComponent();
            for (int i = 0; i < 2; i++)
            {
                parameters.SetGuid("InputId", i, defaults.Params.Input[i].ComponentGuid);
                ((GH_Chunk)parameters.CreateChunk("InputParam", i)).CopyValuesFromChunk((GH_Chunk)reader.FindChunk("param_input", i));
            }
            for (int i = 0; i < 4; i++)
            {
                parameters.SetGuid("OutputId", i, defaults.Params.Output[i].ComponentGuid);
                ((GH_Chunk)parameters.CreateChunk("OutputParam", i)).CopyValuesFromChunk((GH_Chunk)reader.FindChunk("param_output", i));
            }
            return upgraded;
        }

        private static GH_IReader UpgradeLegacyOutputParameters(GH_IReader reader)
        {
            // Recognize only the known one-input/one-output schema. Missing
            // chunks in a modern or partial archive must still report corruption.
            if (reader.ItemExists("PanelOutputSchema") || reader.ItemExists("PanelOutputScope") ||
                !reader.ChunkExists("param_input", 0) || !reader.ChunkExists("param_output", 0) ||
                reader.ChunkExists("param_input", 1) || reader.ChunkExists("param_output", 1) ||
                reader.ChunkExists("param_output", 2) || reader.ChunkExists("param_output", 3) || reader is not GH_Chunk original)
                return reader;

            // A detached copy preserves D chunks and their wire identities without
            // editing the source archive. Preserve the owner for relative paths
            // and genuine archive diagnostics; loose undo chunks have no owner.
            var upgraded = GH_Chunk.InstantiateRoot(original.Archive ?? new GH_Archive());
            upgraded.CopyValuesFromChunk(original);
            var defaults = new BetterPanelComponent();
            defaults.Params.Input[1].Write(upgraded.CreateChunk("param_input", 1));
            for (int i = 1; i < defaults.Params.Output.Count; i++)
                defaults.Params.Output[i].Write(upgraded.CreateChunk("param_output", i));
            return upgraded;
        }
        internal PanelOutputScope OutputScope { get; private set; }
        internal PanelOutputValues OutputValues { get; private set; }
        internal void SetOutputValues(PanelOutputValues values)
        {
            if (!Enum.IsDefined(typeof(PanelOutputValues), values) || OutputValues == values) return;
            RecordUndoEvent("Panel+ output items/values", new PanelUndo<PanelOutputValues>(InstanceGuid, OutputValues, values, (component, value) => component.ApplyOutputValues(value)));
            ApplyOutputValues(values);
        }

        private void ApplyOutputValues(PanelOutputValues values)
        {
            OutputValues = values;
            _cachedOutputResult = null;
            Session.UpdateQuery(Session.Options);
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            ScheduleOutput();
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetOutputScope(PanelOutputScope scope)
        {
            if (!Enum.IsDefined(typeof(PanelOutputScope), scope) || OutputScope == scope) return;
            // Scope changes must not reload the component and discard its selection.
            RecordUndoEvent("Panel+ B + F follow", new PanelUndo<PanelOutputScope>(InstanceGuid, OutputScope, scope, (component, value) => component.ApplyOutputScope(value)));
            ApplyOutputScope(scope);
        }

        private void ApplyOutputScope(PanelOutputScope scope)
        {
            OutputScope = scope;
            UpdateOutputDescriptions();
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            ScheduleOutput();
            Instances.ActiveCanvas?.Invalidate();
        }

    }
}
