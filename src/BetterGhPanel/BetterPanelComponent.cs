using System;
using System.Drawing;
using System.Windows.Forms;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;
using BetterGhPanel.UI;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;

namespace BetterGhPanel
{
    public sealed class BetterPanelComponent : GH_Component
    {
        private PanelSession? _session;
        private PanelInspector? _inspector;
        private IGH_Goo?[] _sourceItems = Array.Empty<IGH_Goo?>();
        private PanelQueryOptions _savedOptions = new PanelQueryOptions();
        private long _version;
        private bool _restoringView;
        internal int PreviewSize { get; private set; } = 1;
        internal float CanvasWidth { get; private set; } = 360;
        internal float CanvasHeight { get; private set; } = 280;

        public BetterPanelComponent() : base("Panel+", "Panel+",
            "Inspect data trees quickly. Filters and sorting only affect the view; the typed tree passes through unchanged.",
            "Params", "Util") { }

        public override Guid ComponentGuid => new Guid("51DC646E-5F72-496A-8B40-628478541F44");
        protected override Bitmap Icon => PanelIcon.Create();
        public override GH_Exposure Exposure => GH_Exposure.primary;

        internal PanelSession Session
        {
            get
            {
                if (_session == null)
                {
                    _session = new PanelSession(() => Instances.ActiveCanvas?.Invalidate());
                    _session.SetDetailResolver(row => row.Id < _sourceItems.Length ? _sourceItems[row.Id]?.ToString() ?? "<null>" : row.DisplayValue);
                    _session.UpdateQuery(_savedOptions);
                    _session.QueryChanged += (_, __) =>
                    {
                        if (!_restoringView)
                        {
                            var document = OnPingDocument();
                            document?.Modified();
                        }
                    };
                }
                return _session;
            }
        }

        public override void CreateAttributes() => m_attributes = new BetterPanelAttributes(this);

        internal void SetCanvasSize(float width, float height)
        {
            CanvasWidth = float.IsNaN(width) || float.IsInfinity(width) ? 360 : Math.Max(300, Math.Min(900, width));
            CanvasHeight = float.IsNaN(height) || float.IsInfinity(height) ? 280 : Math.Max(220, Math.Min(900, height));
            Attributes.ExpireLayout();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Data", "D", "Any typed data tree to inspect", GH_ParamAccess.tree);
            p[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddGenericParameter("Data", "D", "Original typed data tree, unchanged by the inspector", GH_ParamAccess.tree);

        protected override void SolveInstance(IGH_DataAccess da)
        {
            if (!da.GetDataTree(0, out GH_Structure<IGH_Goo> tree)) tree = new GH_Structure<IGH_Goo>();
            da.SetDataTree(0, tree);
            var snapshot = SnapshotCapture.Capture(tree, ++_version, out _sourceItems);
            Session.AttachSnapshot(snapshot);
        }

        internal void OpenInspector()
        {
            if (_inspector != null) { _inspector.BringToFront(); return; }
            _inspector = new PanelInspector(Session);
            _inspector.Closed += (_, __) => _inspector = null;
            _inspector.Owner = Rhino.UI.RhinoEtoApp.MainWindow;
            _inspector.Show();
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
                CanvasTextInput.CloseActive();
                // Unloaded is a canvas/tab switch, not document destruction.
                _inspector?.Close();
                _inspector = null;
            }
            base.DocumentContextChanged(document, context);
        }

        private void ReleaseSession()
        {
            CanvasTextInput.CloseActive();
            if (_session != null) _savedOptions = _session.Options;
            _inspector?.Close();
            _inspector = null;
            _session?.Dispose();
            _session = null;
            _sourceItems = Array.Empty<IGH_Goo?>();
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Open data inspector…", (_, __) => OpenInspector());
            Menu_AppendItem(menu, "Reset inspector view", (_, __) => Session.ResetView());
            var sizeMenu = Menu_AppendItem(menu, "Canvas panel size");
            var names = new[] { "Small", "Medium", "Large" };
            for (var i = 0; i < names.Length; i++)
            {
                var size = i;
                Menu_AppendItem(sizeMenu.DropDown, names[i], (_, __) =>
                {
                    RecordUndoEvent("Panel preview size");
                    PreviewSize = size;
                    SetCanvasSize(320 + size * 60, 240 + size * 80);
                }, true, PreviewSize == size);
            }
        }

        public override bool Write(GH_IWriter writer)
        {
            var options = _session?.Options ?? _savedOptions;
            writer.SetString("PanelSearch", options.Search ?? "");
            writer.SetString("PanelType", options.TypeName ?? "");
            writer.SetString("PanelBranch", options.BranchPath ?? "");
            writer.SetBoolean("PanelPrefix", options.BranchPrefix);
            writer.SetBoolean("PanelInvalid", options.OnlyInvalid);
            writer.SetInt32("PanelSort", (int)options.SortMode);
            writer.SetInt32("PanelSize", PreviewSize);
            writer.SetSingle("PanelWidth", CanvasWidth);
            writer.SetSingle("PanelHeight", CanvasHeight);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            var search = ""; var type = ""; var branch = "";
            var prefix = false; var invalid = false; var sort = 0; var size = 1;
            reader.TryGetString("PanelSearch", ref search);
            reader.TryGetString("PanelType", ref type);
            reader.TryGetString("PanelBranch", ref branch);
            reader.TryGetBoolean("PanelPrefix", ref prefix);
            reader.TryGetBoolean("PanelInvalid", ref invalid);
            reader.TryGetInt32("PanelSort", ref sort);
            reader.TryGetInt32("PanelSize", ref size);
            _savedOptions = new PanelQueryOptions { Search = search, TypeName = string.IsNullOrEmpty(type) ? null : type,
                BranchPath = string.IsNullOrEmpty(branch) ? null : branch, BranchPrefix = prefix, OnlyInvalid = invalid,
                SortMode = Enum.IsDefined(typeof(PanelSortMode), sort) ? (PanelSortMode)sort : PanelSortMode.SourceOrder };
            PreviewSize = Math.Max(0, Math.Min(2, size));
            float width = 360, height = 280;
            reader.TryGetSingle("PanelWidth", ref width);
            reader.TryGetSingle("PanelHeight", ref height);
            CanvasWidth = float.IsNaN(width) || float.IsInfinity(width) ? 360 : Math.Max(300, Math.Min(900, width));
            CanvasHeight = float.IsNaN(height) || float.IsInfinity(height) ? 280 : Math.Max(220, Math.Min(900, height));
            _restoringView = true;
            try { _session?.UpdateQuery(_savedOptions); }
            finally { _restoringView = false; }
            var success = base.Read(reader);
            Attributes?.ExpireLayout();
            return success;
        }
    }
}
