using System;
using System.Globalization;
using System.Linq;
using BetterGhPanel.Core;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel
{
    public sealed partial class BetterPanelComponent
    {
        private const string HeaderDescription = "Optional column titles: D first, then F and each added column. Empty items keep the input's name.";
        private Guid _headerInputId;
        private IGH_Param? _removingHeaderInput;
        private int _headerInputIndex = -1;
        private string?[] _headerTitles = Array.Empty<string?>();
        private IGH_Param[] _headerDataInputs = Array.Empty<IGH_Param>();
        private (int, bool, bool, bool, bool, PanelUnitConversion, int, double)? _headerCaptureSettings;

        internal IGH_Param? HeaderInput
        {
            get
            {
                if (_removingHeaderInput != null) return _removingHeaderInput;
                if (_headerInputId == Guid.Empty && _headerInputIndex < 2) return null;
                var input = Params.Input.Skip(2).FirstOrDefault(param => param.InstanceGuid == _headerInputId);
                if (input == null && _headerInputIndex >= 2 && _headerInputIndex < Params.Input.Count)
                    input = Params.Input[_headerInputIndex];
                if (input != null) { _headerInputId = input.InstanceGuid; _headerInputIndex = Params.Input.IndexOf(input); }
                return input;
            }
        }
        private bool IsHeaderInput(IGH_Param input) => ReferenceEquals(input, HeaderInput);
        private bool IsTableDataInput(IGH_Param input) => !IsHeaderInput(input);

        internal void AddHeaderInput()
        {
            if (HeaderInput != null) return;
            RecordUndoEvent("Panel+ Add header input");
            var input = new Param_GenericObject { Name = "Headers", NickName = "H", Description = HeaderDescription,
                Access = GH_ParamAccess.tree, Optional = true, MutableNickName = true };
            Params.RegisterInputParam(input);
            _headerInputId = input.InstanceGuid; _headerInputIndex = Params.Input.IndexOf(input);
            HeaderInputsChanged();
        }
        internal void RemoveHeaderInput()
        {
            var input = HeaderInput;
            if (input == null) return;
            RecordUndoEvent("Panel+ Remove header input");
            _removingHeaderInput = input;
            try { Params.UnregisterInputParameter(input, true); }
            finally { _removingHeaderInput = null; }
            _headerInputId = Guid.Empty; _headerInputIndex = -1;
            HeaderInputsChanged();
            if (Phase == GH_SolutionPhase.Blank && _reuseQuery && _queryTree != null)
                OnPingDocument()?.ScheduleSolution(1, _ => { });
        }
        private void HeaderInputsChanged()
        {
            VariableParameterMaintenance();
            Params.OnParametersChanged();
            RefreshHeaderTitles();
            Attributes?.ExpireLayout();
            OnPingDocument()?.Modified();
            Instances.ActiveCanvas?.Invalidate();
        }

        private void MaintainHeaderInput()
        {
            var header = HeaderInput;
            if (header == null) return;
            header.Description = HeaderDescription;
            if (Params.Input.IndexOf(header) != Params.Input.Count - 1)
                Params.SortInput(Params.Input.Select((input, index) => ReferenceEquals(input, header) ? Params.Input.Count : index).ToArray());
            _headerInputIndex = Params.Input.IndexOf(header);
        }

        internal string TableHeaderTitle(int index)
        {
            var input = index == 0 ? Params.Input[0] : TableColumns[index - 1].Input;
            return PanelTableText.HeaderTitle(_headerTitles, index, input.NickName);
        }

        // Only text and exact stock scalar fields are read. H never inspects live
        // geometry or invokes arbitrary goo formatters just to obtain a title.
        private static string? HeaderText(IGH_Goo? item)
        {
            var type = item?.GetType();
            if (type == typeof(GH_String)) return ((GH_String)item!).Value;
            if (type == typeof(GH_Integer)) return ((GH_Integer)item!).Value.ToString(CultureInfo.InvariantCulture);
            if (type == typeof(GH_Number)) return PanelUnits.Format(((GH_Number)item!).Value, PanelUnitConversion.None, PanelUnits.AutomaticDecimals);
            if (type == typeof(GH_Boolean)) return ((GH_Boolean)item!).Value ? "True" : "False";
            if (type == typeof(GH_ObjectWrapper)) return ((GH_ObjectWrapper)item!).Value as string;
            return null;
        }
        internal void RefreshHeaderTitles()
        {
            int count = 1 + Params.Input.Skip(2).Count(IsTableDataInput) + (PairedInputPresent() ? 1 : 0);
            var values = HeaderInput?.VolatileData.AllData(false).Take(count).Select(HeaderText).ToArray() ?? Array.Empty<string?>();
            if (_headerTitles.SequenceEqual(values)) return;
            _headerTitles = values;
            CancelPendingCopy();
            var attributes = Attributes as BetterPanelAttributes;
            attributes?.ClearTableWidths();
            attributes?.InvalidatePaintedHits();
            Instances.ActiveCanvas?.Invalidate();
        }

        private (int, bool, bool, bool, bool, PanelUnitConversion, int, double) HeaderCaptureSettings =>
            (TrimDepth, SimplifyTree, FlattenTree, ReverseTree, CreateSetTree, UnitConversion, DecimalPlaces, RoundUpFactor);
        private void RememberHeaderDataInputs()
        {
            _headerDataInputs = Params.Input.Where(IsTableDataInput).ToArray();
            _headerCaptureSettings = HeaderCaptureSettings;
        }
        private bool IsHeaderOnlyExpiration()
        {
            var header = HeaderInput;
            if (header == null || header.Phase != GH_SolutionPhase.Blank || Phase != GH_SolutionPhase.Computed ||
                _solving || _preserveQuery || _discardViewRestore || Locked || _queryTree == null || _session == null || _session.IsDisposed ||
                !_headerCaptureSettings.HasValue || !_headerCaptureSettings.Value.Equals(HeaderCaptureSettings)) return false;
            var inputs = Params.Input.Where(IsTableDataInput).ToArray();
            return inputs.Length == _headerDataInputs.Length && inputs.Select((input, index) =>
                ReferenceEquals(input, _headerDataInputs[index]) &&
                (input.Phase == GH_SolutionPhase.Computed || input.Phase == GH_SolutionPhase.Collected)).All(current => current);
        }
        public override void ExpireSolution(bool recompute)
        {
            if (!IsHeaderOnlyExpiration() || recompute && OnPingDocument()?.SolutionState == GH_ProcessStep.Process)
            { base.ExpireSolution(recompute); return; }
            // Collect H through a normal GH solution, reusing the established
            // snapshot/query and its complete publication fence, like selection.
            _preserveQuery = true; _reuseQuery = true;
            try { base.ExpireSolution(recompute); }
            finally { _preserveQuery = false; }
        }

        private void WriteHeaderInput(GH_IWriter writer)
        {
            var header = HeaderInput;
            if (header == null) return;
            writer.SetGuid("PanelHeaderInput", header.InstanceGuid);
            writer.SetInt32("PanelHeaderInputIndex", Params.Input.IndexOf(header));
        }
        private void ReadHeaderInput(GH_IReader reader)
        {
            _removingHeaderInput = null;
            _headerInputId = Guid.Empty; _headerInputIndex = -1; _headerTitles = Array.Empty<string?>();
            reader.TryGetGuid("PanelHeaderInput", ref _headerInputId);
            reader.TryGetInt32("PanelHeaderInputIndex", ref _headerInputIndex);
        }
    }
}
