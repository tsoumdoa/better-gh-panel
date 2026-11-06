using System;
using System.Linq;
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
        // A live search edit previews the view only. Wire outputs, document
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
        internal string PreviewStatus => _highlight.Status;
        internal float DisplayHeight => CanvasPanelSize.DisplayHeight(CanvasHeight, CanvasDisplay);
        internal float DisplayWidth => CanvasWidth * (PanelSplit ? 2 : 1);
        internal int TrimDepth { get; private set; }
        internal bool FlattenTree { get; private set; }
        internal bool ReverseTree { get; private set; }
        internal bool SimplifyTree { get; private set; }
        internal bool CreateSetTree { get; private set; }
        internal bool HasTreeOperations => TrimDepth > 0 || FlattenTree || ReverseTree || SimplifyTree || CreateSetTree;
        internal PanelUnitConversion UnitConversion { get; private set; }
        internal int DecimalPlaces { get; private set; } = PanelUnits.AutomaticDecimals;
        internal double RoundUpFactor { get; private set; }

        private static bool HasFilters(PanelQueryOptions options) =>
            !string.IsNullOrEmpty(options.Search) || options.SearchMode != PanelSearchMode.Text || options.DisplayFilterValue ||
            options.TypeName != null || options.BranchPath != null || options.BranchPrefix || options.OnlyInvalid ||
            options.SortMode != PanelSortMode.SourceOrder;

        internal void ClearFilters()
        {
            CanvasTextInput.CloseActive(this);
            CanvasTextInput.CompletePending();
            if (HasFilters(Session.Options)) RecordUndoEvent("Clear filters");
            ClearFiltersCore();
        }

        private void ClearFiltersCore()
        {
            CancelPendingCopy();
            CanvasTextInput.CloseActive(this);
            (Attributes as BetterPanelAttributes)?.ResetViewState();
            RowSelection.Clear();
            var previousResult = Session.Result;
            var options = new PanelQueryOptions { DecimalPlaces = DecimalPlaces, RoundUpFactor = RoundUpFactor };
            if (!CanOutputValuesFor(CreateSetTree, options)) OutputValues = PanelOutputValues.Items;
            Session.UpdateQuery(options);
            if (ReferenceEquals(previousResult, Session.Result)) (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
        }

        internal void SetPreviewMode(PanelPreviewMode mode)
        {
            if (!Enum.IsDefined(typeof(PanelPreviewMode), mode) || PreviewMode == mode) return;
            RecordUndoEvent("Panel preview mode");
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
                RecordUndoEvent(name, new PanelUndo<Tuple<PanelQueryOptions, PanelOutputValues>>(InstanceGuid,
                    Tuple.Create(before.Clone(), beforeValues), Tuple.Create(after.Clone(), afterValues),
                    (component, state) => { CanvasTextInput.CloseActive(component); (component.Attributes as BetterPanelAttributes)?.CancelQueryEditor(); component.OutputValues = state.Item2; component.Session.UpdateQuery(state.Item1); component.ScheduleOutput(); }));
            }
        }

        // Ends a live edit. A committed edit publishes like any other query change;
        // an abandoned one already restored the original query, so outputs and the
        // document never changed.
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
            ScheduleOutput();
        }

        internal bool ResetAll()
        {
            CanvasTextInput.CloseActive(this);
            CanvasTextInput.CompletePending();
            _discardViewRestore = true;
            var options = Session.Options;
            bool outputChanged = HasTreeOperations || UnitConversion != PanelUnitConversion.None || RoundUpFactor > 0 || DecimalPlaces >= 0;
            bool settingsChanged = outputChanged || OutputScope != PanelOutputScope.Filtered || OutputValues != PanelOutputValues.Items || DecimalPlaces != PanelUnits.AutomaticDecimals ||
                !string.IsNullOrEmpty(options.Search) || options.SearchMode != PanelSearchMode.Text || options.TypeName != null || options.BranchPath != null ||
                options.BranchPrefix || options.OnlyInvalid || options.SortMode != PanelSortMode.SourceOrder;
            if (settingsChanged) RecordUndoEvent("Reset all");
            TrimDepth = 0;
            FlattenTree = ReverseTree = SimplifyTree = CreateSetTree = false;
            UnitConversion = PanelUnitConversion.None;
            RoundUpFactor = 0;
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

        internal void SetCanvasDisplay(CanvasPanelDisplay display)
        {
            if (!Enum.IsDefined(typeof(CanvasPanelDisplay), display) || CanvasDisplay == display) return;
            (Attributes as BetterPanelAttributes)?.InvalidatePaintedHits();
            RecordUndoEvent("Panel+ display size");
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
            RecordUndoEvent(minimized ? "Minimize Panel+" : "Restore Panel+",
                new PanelUndo<bool>(InstanceGuid, PanelMinimized, minimized, (component, value) => component.ApplyPanelMinimized(value)));
            ApplyPanelMinimized(minimized);
        }

        internal void SetPanelSplit(bool split)
        {
            if (PanelSplit == split || split && !CanSplit) return;
            RecordUndoEvent("Panel+ D | F view", new PanelUndo<bool>(InstanceGuid, PanelSplit, split, (component, value) => component.ApplyPanelSplit(value)));
            ApplyPanelSplit(split);
        }

        private void ApplyPanelSplit(bool split)
        {
            CanvasTextInput.CloseActive(this);
            (Attributes as BetterPanelAttributes)?.ClosePresentationControls();
            PanelSplit = split;
            if (split && PairedSnapshot == null && _pairedInputTree != null)
            {
                PairedSnapshot = SnapshotCapture.CaptureForMeasurements(_pairedInputTree, out _pairedSourceItems,
                    out _pairedMeasurementSources, out _pairedCaptureGraphs, PanelUnitConversion.None, _geometryUnits, null, null, null, null, null);
                _pairedHasGeometry = HasGeometry(PairedSnapshot);
                // F geometry was labelled with the units of the last solve. If the document
                // units changed since, recompute now rather than show stale measurements.
                // This is a fresh capture attempt, so it gets its own single retry.
                if (_pairedHasGeometry && !GeometryContextCurrent(_geometryContext) && _session != null)
                {
                    _captureRetries = 0;
                    InvalidateStaleCapture(_session, OnPingDocument(), _solveGeneration);
                }
            }
            else if (!split)
            {
                // A closed split stops paying for F presentation and its freshness checks.
                PairedSnapshot = null;
                _pairedSourceItems = Array.Empty<IGH_Goo?>();
                _pairedMeasurementSources = Array.Empty<CapturedMeasurementSource?>();
                _pairedCaptureGraphs = Array.Empty<CaptureGraphWitness>();
                (Attributes as BetterPanelAttributes)?.ClearPaneSummary();
            }
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
            writer.SetInt32("PanelSort", (int)options.SortMode);
            // Retain the legacy key for older plug-in versions; dimensions take precedence.
            writer.SetInt32("PanelSize", PreviewSize < 0 ? 1 : PreviewSize);
            writer.SetSingle("PanelWidth", CanvasWidth);
            writer.SetSingle("PanelHeight", CanvasHeight);
            writer.SetInt32("PanelDisplay", (int)CanvasDisplay);
            writer.SetBoolean("PanelMinimized", PanelMinimized);
            writer.SetBoolean("PanelSplit", PanelSplit);
            writer.SetInt32("PanelPreviewMode", (int)PreviewMode);
            writer.SetBoolean("PanelShowItemOrder", ShowItemOrder);
            writer.SetInt32("PanelTrimDepth", TrimDepth);
            writer.SetBoolean("PanelFlatten", FlattenTree);
            writer.SetBoolean("PanelReverse", ReverseTree);
            writer.SetBoolean("PanelSimplify", SimplifyTree);
            writer.SetBoolean("PanelCreateSet", CreateSetTree);
            writer.SetInt32("PanelUnitConversion", (int)UnitConversion);
            writer.SetInt32("PanelDecimalPlaces", DecimalPlaces);
            writer.SetDouble("PanelRoundUpFactor", RoundUpFactor);
            writer.SetInt32("PanelOutputScope", (int)OutputScope);
            writer.SetInt32("PanelOutputValues", (int)OutputValues);
            writer.SetInt32("PanelOutputSchema", 1);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
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
            bool hasLegacySize = reader.TryGetInt32("PanelSize", ref size);
            _savedOptions = new PanelQueryOptions
            {
                Search = search,
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
            int trimDepth = 0;
            bool flatten = false, reverse = false, simplify = false, createSet = false;
            reader.TryGetInt32("PanelTrimDepth", ref trimDepth);
            reader.TryGetBoolean("PanelFlatten", ref flatten);
            reader.TryGetBoolean("PanelReverse", ref reverse);
            reader.TryGetBoolean("PanelSimplify", ref simplify);
            reader.TryGetBoolean("PanelCreateSet", ref createSet);
            TrimDepth = Math.Max(0, trimDepth);
            FlattenTree = flatten;
            ReverseTree = reverse;
            SimplifyTree = simplify;
            CreateSetTree = createSet;
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
            int outputScope = 0;
            reader.TryGetInt32("PanelOutputScope", ref outputScope);
            OutputScope = Enum.IsDefined(typeof(PanelOutputScope), outputScope) ? (PanelOutputScope)outputScope : PanelOutputScope.Filtered;
            _savedOptions.DecimalPlaces = DecimalPlaces;
            _savedOptions.RoundUpFactor = RoundUpFactor;
            if (!CanOutputValuesFor(CreateSetTree, _savedOptions)) OutputValues = PanelOutputValues.Items;
            _restoringView = true;
            try { _session?.UpdateQuery(_savedOptions); }
            finally { _restoringView = false; }
            var success = base.Read(UpgradeLegacyOutputParameters(reader));
            UpdateOutputDescriptions();
            (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
            Attributes?.ExpireLayout();
            return success;
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
            RecordUndoEvent("Panel+ output Items / Values", new PanelUndo<PanelOutputValues>(InstanceGuid, OutputValues, values, (component, value) => component.ApplyOutputValues(value)));
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
