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
        private const string HeaderDescription = "Optional column titles: item 0 = D, item 1 = F, then each added column. Empty items keep the input's nickname.";
        private Guid _headerInputId;
        private IGH_Param? _removingHeaderInput;
        private int _headerInputIndex = -1;
        private string?[] _headerTitles = Array.Empty<string?>();

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
            RecordUndoEvent("Panel+ Add column titles input");
            var input = new Param_GenericObject { Name = "Column titles", NickName = "H", Description = HeaderDescription,
                Access = GH_ParamAccess.tree, Optional = true, MutableNickName = true };
            Params.RegisterInputParam(input);
            _headerInputId = input.InstanceGuid; _headerInputIndex = Params.Input.IndexOf(input);
            HeaderInputsChanged();
            ExpireSolution(true);
        }
        internal void RemoveHeaderInput()
        {
            var input = HeaderInput;
            if (input == null) return;
            RecordUndoEvent("Panel+ Remove column titles input");
            _removingHeaderInput = input;
            try { Params.UnregisterInputParameter(input, true); }
            finally { _removingHeaderInput = null; }
            _headerInputId = Guid.Empty; _headerInputIndex = -1;
            HeaderInputsChanged();
            ExpireSolution(true);
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
            header.Name = "Column titles";
            header.Description = HeaderDescription;
            if (Params.Input.IndexOf(header) != Params.Input.Count - 1)
                Params.SortInput(Params.Input.Select((input, index) => ReferenceEquals(input, header) ? Params.Input.Count : index).ToArray());
            _headerInputIndex = Params.Input.IndexOf(header);
        }

        internal string TableHeaderTitle(int index)
        {
            var input = index == 0 ? Params.Input[0] : TableColumns[index - 1].Input;
            return HeaderTitleForInput(input, input.NickName);
        }

        private int HeaderSlot(IGH_Param input) => Params.Input.TakeWhile(value => !ReferenceEquals(value, input)).Count(IsTableDataInput);
        internal string HeaderTitleForInput(IGH_Param input, string? fallback = null) =>
            PanelTableText.HeaderTitle(_headerTitles, HeaderSlot(input), fallback ?? input.NickName);
        internal bool HasHeaderTitle(int index)
        {
            var input = index == 0 ? Params.Input[0] : TableColumns[index - 1].Input;
            int slot = HeaderSlot(input);
            return slot < _headerTitles.Length && !string.IsNullOrWhiteSpace(_headerTitles[slot]);
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
            int count = 2 + Params.Input.Skip(2).Count(IsTableDataInput);
            var values = HeaderInput?.VolatileData.AllData(false).Take(count).Select(HeaderText).ToArray() ?? Array.Empty<string?>();
            if (_headerTitles.SequenceEqual(values)) return;
            _headerTitles = values;
            CancelPendingCopy();
            var attributes = Attributes as BetterPanelAttributes;
            attributes?.ClearTableWidths();
            attributes?.InvalidatePaintedHits();
            Instances.ActiveCanvas?.Invalidate();
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
