using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;
using BetterGhPanel.UI;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel
{
    public sealed class BetterPanelComponent : GH_Component
    {
        private PanelSession? _session;
        private readonly PanelGeometryHighlight _highlight = new PanelGeometryHighlight();
        private bool _previewSuspended;
        private IGH_Goo?[] _sourceItems = Array.Empty<IGH_Goo?>();
        private string _geometryUnits = "units";
        private Rhino.UnitSystem _geometryUnitSystem = Rhino.UnitSystem.None;
        private double _geometryTolerance = 0.001;
        private CancellationTokenSource? _copyCancellation;
        private bool _retainGeometryMeasurements;
        private GH_Structure<IGH_Goo>? _queryTree;
        private bool _solving, _preserveQuery, _reuseQuery;
        private long _outputRequest;
        private PanelQueryOptions? _outputOptions;
        private bool _outputPending;
        private PanelQueryOptions _savedOptions = new PanelQueryOptions();
        private bool _restoringView;
        internal int PreviewSize => CanvasPanelSize.PresetIndex(CanvasWidth, CanvasHeight);
        internal float CanvasWidth { get; private set; } = CanvasPanelSize.DefaultWidth;
        internal float CanvasHeight { get; private set; } = CanvasPanelSize.DefaultHeight;
        internal CanvasPanelDisplay CanvasDisplay { get; private set; }
        internal float DisplayHeight => CanvasPanelSize.DisplayHeight(CanvasHeight, CanvasDisplay);
        internal int TrimDepth { get; private set; }
        internal bool FlattenTree { get; private set; }
        internal bool ReverseTree { get; private set; }
        internal bool SimplifyTree { get; private set; }
        internal bool HasTreeOperations => TrimDepth > 0 || FlattenTree || ReverseTree || SimplifyTree;
        internal PanelUnitConversion UnitConversion { get; private set; }
        internal int DecimalPlaces { get; private set; } = PanelUnits.AutomaticDecimals;

        public BetterPanelComponent() : base("Panel+", "Panel+",
            "Inspect and manipulate typed data trees on the canvas. Tree tools, unit conversion and filters change the output; sorting and decimal formatting affect the view.",
            "Params", "Util") { }

        public override Guid ComponentGuid => new Guid("51DC646E-5F72-496A-8B40-628478541F44");
        protected override Bitmap Icon => PanelIcon.Create();
        public override GH_Exposure Exposure => GH_Exposure.primary;

        // Grasshopper's default preview also draws internalised input. Preview
        // only output so excluded items cannot bypass the panel's filters.
        private IEnumerable<IGH_PreviewObject> OutputPreviews => Params.Output.OfType<IGH_PreviewObject>()
            .Where(preview => !preview.Hidden && preview.IsPreviewCapable);

        public override bool IsPreviewCapable => _highlight.HasSelection || OutputPreviews.Any();

        public override Rhino.Geometry.BoundingBox ClippingBox
        {
            get
            {
                var bounds = Rhino.Geometry.BoundingBox.Empty;
                foreach (var preview in OutputPreviews) bounds.Union(preview.ClippingBox);
                bounds.Union(_highlight.ClippingBox);
                return bounds;
            }
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            if (Hidden || Locked || _previewSuspended) return;
            if (_highlight.HasSelection) _highlight.DrawWires(args, Attributes.Selected);
            else foreach (var preview in OutputPreviews) preview.DrawViewportWires(args);
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            if (Hidden || Locked || _previewSuspended) return;
            if (_highlight.HasSelection) _highlight.DrawMeshes(args);
            else foreach (var preview in OutputPreviews) preview.DrawViewportMeshes(args);
        }

        internal PanelSession Session
        {
            get
            {
                if (_session == null)
                {
                    _session = new PanelSession(() => Instances.ActiveCanvas?.Invalidate());
                    if (Locked) _session.InvalidateInput(false, suspended: true);
                    _session.SetDetailResolver(position => position < _sourceItems.Length ? _sourceItems[position]?.ToString() ?? "<null>" : null);
                    _session.SetGeometryDetailResolver(position => position < _sourceItems.Length
                        ? GeometryHints.PrepareMeasurement(_sourceItems[position], _geometryUnits) : null);
                    _session.UpdateQuery(_savedOptions);
                    _session.QueryChanged += (_, __) =>
                    {
                        CancelPendingCopy();
                        (Attributes as BetterPanelAttributes)?.OnQueryChanged();
                        ClearHighlight();
                        if (!_restoringView)
                        {
                            var document = OnPingDocument();
                            document?.Modified();
                            ScheduleOutput();
                        }
                    };
                    _session.ResultChanged += (_, __) =>
                    {
                        (Attributes as BetterPanelAttributes)?.OnQueryCompleted();
                        if (!_solving && !_restoringView) ScheduleOutput();
                    };
                }
                return _session;
            }
        }

        public override void CreateAttributes() => m_attributes = new BetterPanelAttributes(this);

        internal void ResetPanelView()
        {
            CancelPendingCopy();
            CanvasTextInput.CloseActive(this);
            (Attributes as BetterPanelAttributes)?.ResetViewState();
            Session.ResetView();
        }

        internal void ResetPanel()
        {
            var options = Session.Options;
            bool outputChanged = HasTreeOperations || UnitConversion != PanelUnitConversion.None;
            bool settingsChanged = outputChanged || DecimalPlaces != PanelUnits.AutomaticDecimals ||
                !string.IsNullOrEmpty(options.Search) || options.SearchMode != PanelSearchMode.Text || options.TypeName != null || options.BranchPath != null ||
                options.BranchPrefix || options.OnlyInvalid || options.SortMode != PanelSortMode.SourceOrder;
            if (settingsChanged) RecordUndoEvent("Reset Panel+");
            TrimDepth = 0;
            FlattenTree = ReverseTree = SimplifyTree = false;
            UnitConversion = PanelUnitConversion.None;
            DecimalPlaces = PanelUnits.AutomaticDecimals;
            ResetPanelView();
            if (settingsChanged) OnPingDocument()?.Modified();
            if (outputChanged) ExpireSolution(true);
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetCanvasSize(float width, float height)
        {
            CanvasWidth = CanvasPanelSize.Width(width);
            CanvasHeight = CanvasPanelSize.Height(height);
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetTreeOperations(int trimDepth, bool flatten, bool reverse, bool simplify)
        {
            trimDepth = Math.Max(0, trimDepth);
            if (TrimDepth == trimDepth && FlattenTree == flatten && ReverseTree == reverse && SimplifyTree == simplify) return;
            bool pathsChanged = TrimDepth != trimDepth || FlattenTree != flatten || SimplifyTree != simplify;
            RecordUndoEvent("Panel+ tree tools");
            TrimDepth = trimDepth;
            FlattenTree = flatten;
            ReverseTree = reverse;
            SimplifyTree = simplify;
            if (pathsChanged)
            {
                // Reverse only changes item order, so its branch selection stays valid.
                var options = Session.Options;
                options.BranchPath = null;
                options.BranchPrefix = false;
                Session.UpdateQuery(options);
            }
            OnPingDocument()?.Modified();
            ExpireSolution(true);
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetCanvasDisplay(CanvasPanelDisplay display)
        {
            if (!Enum.IsDefined(typeof(CanvasPanelDisplay), display) || CanvasDisplay == display) return;
            RecordUndoEvent("Panel+ display size");
            CanvasTextInput.CloseActive();
            CanvasDisplay = display;
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        internal void SetUnits(PanelUnitConversion conversion, int decimals)
        {
            if (!Enum.IsDefined(typeof(PanelUnitConversion), conversion)) conversion = PanelUnitConversion.None;
            decimals = Math.Max(PanelUnits.AutomaticDecimals, Math.Min(PanelUnits.MaximumDecimals, decimals));
            bool conversionChanged = UnitConversion != conversion;
            if (!conversionChanged && DecimalPlaces == decimals) return;
            CancelPendingCopy();
            RecordUndoEvent("Panel+ units and decimals");
            UnitConversion = conversion;
            DecimalPlaces = decimals;
            OnPingDocument()?.Modified();
            if (conversionChanged)
            {
                _retainGeometryMeasurements = true;
                try { ExpireSolution(true); }
                finally { _retainGeometryMeasurements = false; }
            }
            Instances.ActiveCanvas?.Invalidate();
        }

        internal string DisplayValue(PanelSnapshot snapshot, PanelRow row, int sourcePosition = -1)
        {
            if (!row.IsValid) return "Invalid · " + row.DisplayValue;
            var geometry = Session.GetGeometryDetails(snapshot, sourcePosition) ?? row.GeometryInfo;
            if (geometry != null)
                return geometry.FormatCompact(snapshot.UnitConversion, DecimalPlaces) +
                    (Session.HasGeometryMeasurement(snapshot, sourcePosition) && Session.GetGeometryDetails(snapshot, sourcePosition) == null
                        ? " · measurement unavailable" : Session.IsGeometryPending(snapshot, sourcePosition) ? " · measuring…" : "");
            return row.NumericValue.HasValue && (snapshot.UnitConversion != PanelUnitConversion.None || DecimalPlaces >= 0)
                ? PanelUnits.Format(row.NumericValue.Value, snapshot.UnitConversion, DecimalPlaces) : row.DisplayValue;
        }

        internal string CopyValue(PanelSnapshot snapshot, int position)
        {
            if (!ReferenceEquals(snapshot, Session.Snapshot) || position < 0 || position >= snapshot.Rows.Count)
                throw new InvalidOperationException("The selected data has changed. Select the rows again.");
            var row = snapshot.Rows[position];
            if (row.NumericValue.HasValue)
                return PanelUnits.Format(row.NumericValue.Value, PanelUnitConversion.None, DecimalPlaces, CultureInfo.InvariantCulture);
            var geometry = Session.GetGeometryDetails(snapshot, position) ?? row.GeometryInfo;
            if (row.IsValid && geometry?.Value != null)
            {
                var value = PanelUnits.FormatGeometry(geometry.Value.Value, geometry.Power, geometry.Units,
                    snapshot.UnitConversion, DecimalPlaces, includeUnits: false);
                if (value == "unavailable") throw new InvalidOperationException("A selected measurement is unavailable.");
                return value;
            }
            if (row.IsValid && geometry?.CanMeasure == true)
                throw new InvalidOperationException("A selected measurement is unavailable.");
            return Session.GetDisplayValue(snapshot, position);
        }

        internal void CancelPendingCopy()
        {
            var cancellation = _copyCancellation;
            _copyCancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
        }

        private bool TryGetActionPositions(PanelSnapshot snapshot, IReadOnlyList<int> positions, out int[] ordered)
        {
            ordered = Array.Empty<int>();
            var session = _session;
            if (session == null || session.IsBusy || session.Error != null || !ReferenceEquals(snapshot, session.Snapshot) ||
                !ReferenceEquals(snapshot, session.Result.Snapshot) || positions.Count == 0) return false;
            var selected = new HashSet<int>(positions);
            if (selected.Any(position => position < 0 || position >= _sourceItems.Length || position >= snapshot.Rows.Count)) return false;
            ordered = session.Result.RowPositions.Where(selected.Contains).ToArray();
            return ordered.Length == selected.Count;
        }

        internal string? FrameRows(PanelSnapshot snapshot, IReadOnlyList<int> positions)
        {
            if (_previewSuspended) return "Activate this Grasshopper document to frame geometry";
            if (positions.Count == 0) { ClearHighlight(); return null; }
            if (!TryGetActionPositions(snapshot, positions, out var ordered)) return "Data changed; select rows again";
            HighlightRows(snapshot, ordered);
            var result = PanelGeometryActions.Frame(ordered.Select(position => _sourceItems[position]));
            return result.Success ? null : result.Error;
        }

        internal void HighlightRows(PanelSnapshot snapshot, IReadOnlyList<int> positions)
        {
            if (_previewSuspended || !TryGetActionPositions(snapshot, positions, out var ordered))
            {
                ClearHighlight();
                return;
            }
            _highlight.Set(_sourceItems, ordered, Session.Result.RowPositions);
            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void ClearHighlight()
        {
            if (!_highlight.HasSelection) return;
            _highlight.Clear();
            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal async Task<string> CopyValuesAsync(PanelSnapshot snapshot, IReadOnlyList<int> positions)
        {
            CancelPendingCopy();
            if (!TryGetActionPositions(snapshot, positions, out var ordered)) return "Data changed; select rows again";
            var session = _session!;
            var cancellation = new CancellationTokenSource();
            _copyCancellation = cancellation;
            bool measured = await session.MeasureGeometryAsync(snapshot, ordered, cancellation.Token);
            var completion = new TaskCompletionSource<string>();
            // Rhino does not guarantee a SynchronizationContext for every canvas
            // callback. Live goo and the system clipboard belong to its UI thread.
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    if (!measured || !ReferenceEquals(_copyCancellation, cancellation) || !ReferenceEquals(_session, session) ||
                        !TryGetActionPositions(snapshot, ordered, out _)) completion.SetResult("Copy cancelled");
                    else
                    {
                        var text = string.Join(Environment.NewLine, ordered.Select(position => CopyValue(snapshot, position)));
                        using (var clipboard = new Eto.Forms.Clipboard()) clipboard.Text = text;
                        completion.SetResult(ordered.Length == 1 ? "Value copied" : ordered.Length + " values copied");
                    }
                }
                catch (Exception ex) { completion.SetResult("Copy failed: " + ex.Message); }
                finally
                {
                    if (ReferenceEquals(_copyCancellation, cancellation))
                    {
                        _copyCancellation = null;
                        cancellation.Dispose();
                    }
                }
            }));
            return await completion.Task;
        }

        internal bool TryCopyGeometry(PanelSnapshot snapshot, IReadOnlyList<int> positions, out string status)
        {
            CancelPendingCopy();
            if (!TryGetActionPositions(snapshot, positions, out var ordered))
            {
                status = "Data changed; select rows again";
                return false;
            }
            var result = PanelGeometryActions.CopyGeometry(ordered.Select(position => _sourceItems[position]),
                _geometryUnitSystem, _geometryTolerance);
            status = result.Success ? result.Count + " geometries copied" +
                (result.Skipped > 0 ? "; " + result.Skipped + " skipped" : "") : result.Error ?? "Copy geometry failed";
            return result.Success;
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Data", "D", "Any typed data tree to inspect", GH_ParamAccess.tree);
            p[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddGenericParameter("Data", "D", "Matching typed items after Simplify → Trim → Flatten → Reverse → unit conversion → filters. Sorting and decimal formatting affect only the view.", GH_ParamAccess.tree);

        private void ScheduleOutput()
        {
            var document = OnPingDocument();
            var session = _session;
            if (document == null || session == null || _queryTree == null) return;
            var options = session.Options;
            if (!_outputPending && _outputOptions != null && SameOutputFilter(_outputOptions, options)) return;
            var snapshot = session.Snapshot;
            long request = ++_outputRequest;
            // Never solve recursively from a query callback. Publish through a
            // normal GH solution so downstream components collect the new tree.
            document.ScheduleSolution(1, _ =>
            {
                if (request != _outputRequest || !ReferenceEquals(document, OnPingDocument()) ||
                    !ReferenceEquals(session, _session) || !ReferenceEquals(snapshot, session.Snapshot) || _queryTree == null) return;
                _preserveQuery = true;
                _reuseQuery = true;
                try { ExpireSolution(false); }
                finally { _preserveQuery = false; }
            });
        }

        private static bool SameOutputFilter(PanelQueryOptions left, PanelQueryOptions right) =>
            left.Search == right.Search && left.SearchMode == right.SearchMode && left.TypeName == right.TypeName &&
            left.OnlyInvalid == right.OnlyInvalid && left.BranchPath == right.BranchPath && left.BranchPrefix == right.BranchPrefix;

        private static bool HasOutputFilter(PanelQueryOptions options) =>
            (options.SearchMode == PanelSearchMode.Regex ? !string.IsNullOrEmpty(options.Search) : !string.IsNullOrWhiteSpace(options.Search)) ||
            options.TypeName != null || options.OnlyInvalid || !string.IsNullOrWhiteSpace(options.BranchPath);

        public override void ClearData()
        {
            // Grasshopper expires the component before collecting its next input.
            // Invalidate now, even when solving is deferred or an upstream node fails.
            if (!_preserveQuery)
            {
                ++_outputRequest;
                _queryTree = null;
                _reuseQuery = false;
                CancelPendingCopy();
                ClearHighlight();
                (Attributes as BetterPanelAttributes)?.ResetViewState();
                _session?.InvalidateInput(_retainGeometryMeasurements, suspended: Locked);
            }
            base.ClearData();
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            if (!da.GetDataTree(0, out GH_Structure<IGH_Goo> tree)) tree = new GH_Structure<IGH_Goo>();
            // Any upstream expiration clears this flag through ClearData. GH may
            // duplicate input goo during collection even when sources are unchanged,
            // so ownership follows the captured solution, not recollected identity.
            bool reuse = _reuseQuery && _queryTree != null;
            _reuseQuery = false;
            _solving = true;
            try
            {
                if (!reuse)
                {
                    CancelPendingCopy();
                    ClearHighlight();
                    tree = PanelTreeOperations.Apply(tree, TrimDepth, SimplifyTree, FlattenTree, ReverseTree);
                    _queryTree = PanelTreeOperations.CopyStructure(PanelUnitOperations.Apply(tree, UnitConversion));
                    var previousSources = _sourceItems;
                    var previousUnits = _geometryUnits;
                    var rhinoDocument = Rhino.RhinoDoc.ActiveDoc;
                    _geometryUnitSystem = rhinoDocument?.ModelUnitSystem ?? Rhino.UnitSystem.None;
                    _geometryTolerance = rhinoDocument?.ModelAbsoluteTolerance ?? 0.001;
                    _geometryUnits = rhinoDocument == null || rhinoDocument.ModelUnitSystem == Rhino.UnitSystem.None
                        ? "units" : rhinoDocument.GetUnitSystemName(true, false, false, true);
                    if (string.IsNullOrWhiteSpace(_geometryUnits)) _geometryUnits = "units";
                    var snapshot = SnapshotCapture.Capture(_queryTree, out _sourceItems, UnitConversion, _geometryUnits);
                    if (_retainGeometryMeasurements && previousUnits == _geometryUnits)
                        Session.AttachConvertedSnapshot(snapshot, position => position < previousSources.Length &&
                            position < _sourceItems.Length && ReferenceEquals(previousSources[position], _sourceItems[position]));
                    else Session.AttachSnapshot(snapshot);
                }
                // Pending/failed filters expose no stale or unfiltered items. The
                // completed query schedules another solution using this same tree.
                _outputOptions = Session.Options;
                bool filtering = HasOutputFilter(_outputOptions);
                _outputPending = filtering && Session.IsBusy;
                da.SetDataTree(0, _queryTree == null ? new GH_Structure<IGH_Goo>() : !filtering ? _queryTree :
                    Session.HasCurrentResult ? PanelTreeOperations.Filter(_queryTree, Session.Result) : new GH_Structure<IGH_Goo>());
                if (Session.Error != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Filter: " + Session.Error);
            }
            finally { _solving = false; }
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            ReleaseSession();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close) ReleaseSession();
            else if (context == GH_DocumentContext.Unloaded)
            {
                _previewSuspended = true;
                ClearHighlight();
                (Attributes as BetterPanelAttributes)?.OnQueryChanged();
                CancelPendingCopy();
                CanvasTextInput.CloseActive();
                // Unloaded is a canvas/tab switch, not document destruction.
                if (_session != null)
                {
                    _session.AnimateBusyState(false);
                    _session.RequestGeometryMeasurements(_session.Snapshot, Array.Empty<int>());
                }
            }
            else if (context == GH_DocumentContext.Loaded || context == GH_DocumentContext.Open)
                _previewSuspended = false;
            base.DocumentContextChanged(document, context);
        }

        private void ReleaseSession()
        {
            ++_outputRequest;
            _queryTree = null;
            _reuseQuery = false;
            CancelPendingCopy();
            ClearHighlight();
            (Attributes as BetterPanelAttributes)?.ResetViewState();
            CanvasTextInput.CloseActive();
            if (_session != null) _savedOptions = _session.Options;
            _session?.Dispose();
            _session = null;
            _sourceItems = Array.Empty<IGH_Goo?>();
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Reset panel", (_, __) => ResetPanel());
            Menu_AppendItem(menu, "Reset panel view", (_, __) => ResetPanelView());
            Menu_AppendItem(menu, "Reset tree tools", (_, __) => SetTreeOperations(0, false, false, false), HasTreeOperations);
            var sizeMenu = Menu_AppendItem(menu, "Canvas panel size");
            var names = CanvasPanelSize.PresetNames;
            for (var i = 0; i < names.Length; i++)
            {
                var size = i;
                Menu_AppendItem(sizeMenu.DropDown, names[i], (_, __) =>
                {
                    RecordUndoEvent("Panel preview size");
                    SetCanvasSize(CanvasPanelSize.PresetWidth(size), CanvasPanelSize.PresetHeight(size));
                }, true, PreviewSize == size);
            }
            Menu_AppendItem(sizeMenu.DropDown, "Custom", (_, __) => { }, false, PreviewSize < 0);
        }

        public override bool Write(GH_IWriter writer)
        {
            var options = _session?.Options ?? _savedOptions;
            writer.SetString("PanelSearch", options.Search ?? "");
            writer.SetInt32("PanelSearchMode", (int)options.SearchMode);
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
            writer.SetInt32("PanelTrimDepth", TrimDepth);
            writer.SetBoolean("PanelFlatten", FlattenTree);
            writer.SetBoolean("PanelReverse", ReverseTree);
            writer.SetBoolean("PanelSimplify", SimplifyTree);
            writer.SetInt32("PanelUnitConversion", (int)UnitConversion);
            writer.SetInt32("PanelDecimalPlaces", DecimalPlaces);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            CancelPendingCopy();
            ClearHighlight();
            var search = ""; var type = ""; var branch = "";
            var prefix = false; var invalid = false; var sort = 0; var size = 1; var searchMode = 0;
            reader.TryGetString("PanelSearch", ref search);
            reader.TryGetInt32("PanelSearchMode", ref searchMode);
            reader.TryGetString("PanelType", ref type);
            reader.TryGetString("PanelBranch", ref branch);
            reader.TryGetBoolean("PanelPrefix", ref prefix);
            reader.TryGetBoolean("PanelInvalid", ref invalid);
            reader.TryGetInt32("PanelSort", ref sort);
            bool hasLegacySize = reader.TryGetInt32("PanelSize", ref size);
            _savedOptions = new PanelQueryOptions { Search = search, TypeName = string.IsNullOrEmpty(type) ? null : type,
                SearchMode = Enum.IsDefined(typeof(PanelSearchMode), searchMode) ? (PanelSearchMode)searchMode : PanelSearchMode.Text,
                BranchPath = string.IsNullOrEmpty(branch) ? null : branch, BranchPrefix = prefix, OnlyInvalid = invalid,
                SortMode = Enum.IsDefined(typeof(PanelSortMode), sort) ? (PanelSortMode)sort : PanelSortMode.SourceOrder };
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
            int trimDepth = 0;
            bool flatten = false, reverse = false, simplify = false;
            reader.TryGetInt32("PanelTrimDepth", ref trimDepth);
            reader.TryGetBoolean("PanelFlatten", ref flatten);
            reader.TryGetBoolean("PanelReverse", ref reverse);
            reader.TryGetBoolean("PanelSimplify", ref simplify);
            TrimDepth = Math.Max(0, trimDepth);
            FlattenTree = flatten;
            ReverseTree = reverse;
            SimplifyTree = simplify;
            int conversion = 0, decimals = PanelUnits.AutomaticDecimals;
            reader.TryGetInt32("PanelUnitConversion", ref conversion);
            reader.TryGetInt32("PanelDecimalPlaces", ref decimals);
            UnitConversion = Enum.IsDefined(typeof(PanelUnitConversion), conversion)
                ? (PanelUnitConversion)conversion : PanelUnitConversion.None;
            DecimalPlaces = Math.Max(PanelUnits.AutomaticDecimals, Math.Min(PanelUnits.MaximumDecimals, decimals));
            _restoringView = true;
            try { _session?.UpdateQuery(_savedOptions); }
            finally { _restoringView = false; }
            var success = base.Read(reader);
            Attributes?.ExpireLayout();
            return success;
        }
    }
}
