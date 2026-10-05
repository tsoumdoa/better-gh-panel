using System;
using System.Globalization;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.Runtime;
using Eto.Drawing;
using Eto.Forms;

namespace BetterGhPanel.UI
{
    /// <summary>One modeless, cross-platform inspector for a component's shared query session.</summary>
    public sealed class PanelInspector : Form
    {
        private readonly PanelSession _session;
        private readonly TextBox _search = new TextBox { PlaceholderText = "Search values, or try > 800", ToolTip = "Text search, or numeric comparisons: >, >=, <, <=, =" };
        private readonly DropDown _types = new DropDown { Width = 150 };
        private readonly DropDown _sort = new DropDown { Width = 160 };
        private readonly CheckBox _invalid = new CheckBox { Text = "Invalid only" };
        private readonly TextBox _branch = new TextBox { PlaceholderText = "All branches · enter {0;2;4}", ToolTip = "Enter an exact branch path and press Enter. Clear to show all branches." };
        private readonly Label _summary = new Label { Font = SystemFonts.Cached(SystemFont.Bold, 12) };
        private readonly Label _numbers = new Label { TextColor = Color.FromArgb(77, 97, 79) };
        private readonly Label _typeCounts = new Label { TextColor = Color.FromArgb(105, 119, 108) };
        private readonly Label _status = new Label { TextColor = Color.FromArgb(105, 119, 108) };
        private readonly Label _address = new Label { Text = "Select an item to inspect its original address and full value.", Font = SystemFonts.Cached(SystemFont.Bold, 11) };
        private readonly TextArea _detail = new TextArea { ReadOnly = true, Wrap = true, Height = 95 };
        private readonly Button _copy = new Button { Text = "Copy value", Enabled = false };
        private readonly VirtualRowView _rows = new VirtualRowView();
        private readonly UITimer _debounce = new UITimer { Interval = 0.15 };
        private readonly NumericStepper _position = new NumericStepper { MinValue = 1, MaxValue = 1, DecimalPlaces = 0, Width = 115, ToolTip = "Jump to a 1-based row in the current view." };
        private string[] _typeNames = new string[0];
        private PanelSnapshot? _typeSnapshot;
        private PanelSnapshot? _detailSnapshot;
        private PanelQueryResult? _lastResult;
        private long _observedQueryRevision;
        private bool _updating;
        private bool _closed;

        public PanelInspector(PanelSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            Title = "Panel+ · Data inspector";
            ClientSize = new Size(920, 690);
            MinimumSize = new Size(720, 520);
            Resizable = true;
            BackgroundColor = Color.FromArgb(246, 249, 243);
            Padding = new Padding(18);

            _sort.DataStore = new[] { "Source order", "Value ↑", "Value ↓" };
            _sort.SelectedIndex = 0;
            _types.DataStore = new[] { "All types" };
            _types.SelectedIndex = 0;

            var reset = new Button { Text = "Reset view", ToolTip = "Clear filters and sorting. Original data always passes through unchanged." };
            var jump = new Button { Text = "Go to branch" };
            var jumpRow = new Button { Text = "Go to row" };

            var toolbar = new TableLayout
            {
                Spacing = new Size(8, 8),
                Rows =
                {
                    new TableRow(new TableCell(_search, true), _types, _sort, reset),
                    new TableRow(new TableCell(_branch, true), jump, _invalid, null)
                }
            };
            var metrics = new TableLayout
            {
                Spacing = new Size(0, 6),
                Padding = new Padding(0, 8, 0, 6),
                Rows = { new TableRow(_summary), new TableRow(_numbers), new TableRow(_typeCounts) }
            };
            var navigation = new TableLayout
            {
                Spacing = new Size(8, 0),
                Rows = { new TableRow(new TableCell(_status, true), new Label { Text = "Row", VerticalAlignment = VerticalAlignment.Center }, _position, jumpRow) }
            };
            var detailTitle = new TableLayout
            {
                Spacing = new Size(8, 0),
                Rows = { new TableRow(new TableCell(_address, true), _copy) }
            };
            Content = new TableLayout
            {
                Spacing = new Size(0, 10),
                Rows =
                {
                    new TableRow(new Label { Text = "PANEL+   /   EXPLORE YOUR TREE", Font = SystemFonts.Cached(SystemFont.Bold, 11), TextColor = Color.FromArgb(77, 97, 79) }),
                    new TableRow(toolbar),
                    new TableRow(metrics),
                    new TableRow(_rows) { ScaleHeight = true },
                    new TableRow(navigation),
                    new TableRow(detailTitle),
                    new TableRow(_detail),
                    new TableRow(new Label { Text = "Filters and sorting affect this view. Output preserves the original typed tree.", Font = SystemFonts.Cached(SystemFont.Default, 10), TextColor = Color.FromArgb(105, 119, 108) })
                }
            };

            _search.TextChanged += (s, e) =>
            {
                if (_updating) return;
                _debounce.Stop();
                _debounce.Start();
            };
            _debounce.Elapsed += (s, e) => { _debounce.Stop(); ApplyQuery(); };
            _types.SelectedIndexChanged += (s, e) => { if (!_updating) ApplyQuery(); };
            _sort.SelectedIndexChanged += (s, e) => { if (!_updating) ApplyQuery(); };
            _invalid.CheckedChanged += (s, e) => { if (!_updating) ApplyQuery(); };
            _branch.KeyDown += (s, e) =>
            {
                if (e.Key == Keys.Enter) { ApplyQuery(); e.Handled = true; }
            };
            jump.Click += (s, e) => ApplyQuery();
            reset.Click += (s, e) => Reset();
            jumpRow.Click += (s, e) => _rows.JumpToRow((int)_position.Value - 1);
            _position.KeyDown += (s, e) =>
            {
                if (e.Key == Keys.Enter) { _rows.JumpToRow((int)_position.Value - 1); e.Handled = true; }
            };
            _rows.RowSelected += InspectRow;
            _rows.ViewportChanged += (s, e) => UpdatePosition();
            _copy.Click += (s, e) =>
            {
                using (var clipboard = new Clipboard()) clipboard.Text = _detail.Text ?? string.Empty;
            };
            _session.Changed += SessionChanged;
            Closed += (s, e) =>
            {
                _closed = true;
                _session.Changed -= SessionChanged;
                _debounce.Stop();
                _debounce.Dispose();
                _rows.Dispose();
            };

            ReadOptions();
            RefreshSession();
        }

        private void ReadOptions()
        {
            var options = _session.Options;
            _updating = true;
            _search.Text = options.Search ?? string.Empty;
            _branch.Text = options.BranchPath ?? string.Empty;
            _sort.SelectedIndex = (int)options.SortMode;
            _invalid.Checked = options.OnlyInvalid;
            int typeIndex = options.TypeName == null ? -1 : Array.IndexOf(_typeNames, options.TypeName);
            _types.SelectedIndex = typeIndex < 0 ? 0 : typeIndex + 1;
            _observedQueryRevision = _session.QueryRevision;
            _updating = false;
        }

        private void ApplyQuery()
        {
            if (_closed || _updating) return;
            _debounce.Stop();
            var options = _session.Options;
            options.Search = _search.Text ?? string.Empty;
            options.TypeName = _types.SelectedIndex > 0 && _types.SelectedIndex <= _typeNames.Length ? _typeNames[_types.SelectedIndex - 1] : null;
            options.SortMode = (PanelSortMode)Math.Max(0, _sort.SelectedIndex);
            options.BranchPath = string.IsNullOrWhiteSpace(_branch.Text) ? null : _branch.Text.Trim();
            options.BranchPrefix = false;
            options.OnlyInvalid = _invalid.Checked == true;
            _session.UpdateQuery(options);
        }

        private void Reset()
        {
            _debounce.Stop();
            _session.ResetView();
        }

        private void SessionChanged(object? sender, EventArgs e)
        {
            if (!_closed) RefreshSession();
        }

        private void RefreshSession()
        {
            var result = _session.Result;
            var snapshot = _session.Snapshot;
            // Synchronize externally changed queries (including the canvas reset menu),
            // while preserving drafts when only a worker/busy notification arrives.
            bool optionsChanged = _observedQueryRevision != _session.QueryRevision;
            if (optionsChanged)
            {
                _debounce.Stop();
                ReadOptions();
            }
            if (snapshot != _typeSnapshot || optionsChanged)
            {
                _typeSnapshot = snapshot;
                _updating = true;
                string? selectedType = _session.Options.TypeName;
                var sourceTypes = snapshot.TypeCounts.Keys;
                // A saved filter can legitimately disappear from a later solution. Keep
                // the selected key visible so an empty result never masquerades as All types.
                if (selectedType != null && !sourceTypes.Contains(selectedType, StringComparer.Ordinal))
                    sourceTypes = sourceTypes.Concat(new[] { selectedType });
                _typeNames = sourceTypes.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                _types.DataStore = new[] { "All types" }.Concat(_typeNames.Select(x => Abbreviate(x, 48) +
                    (snapshot.TypeCounts.ContainsKey(x) ? string.Empty : " (0 in source)"))).ToArray();
                int typeIndex = Array.IndexOf(_typeNames, selectedType);
                _types.SelectedIndex = typeIndex < 0 ? 0 : typeIndex + 1;
                _updating = false;
            }
            if (_detailSnapshot != snapshot)
            {
                _detailSnapshot = snapshot;
                ClearDetail();
            }
            if (result != _lastResult)
            {
                _lastResult = result;
                _rows.SetResult(result, true);
                if (_rows.SelectedSourcePosition < 0) ClearDetail();
            }
            var stats = result.Statistics;
            _summary.Text = string.Format(CultureInfo.CurrentCulture, "{0:N0} items   ·   {1:N0} branches   ·   {2:N0} null   ·   {3:N0} invalid", stats.ItemCount, stats.BranchCount, stats.NullCount, stats.InvalidCount);
            _numbers.Text = stats.NumericCount == 0
                ? "Numeric statistics appear when this view contains numbers."
                : string.Format(CultureInfo.CurrentCulture, "{0:N0} numbers   ·   Min {1}   ·   Max {2}   ·   Mean {3}", stats.NumericCount, FormatNumber(stats.Minimum), FormatNumber(stats.Maximum), FormatNumber(stats.Mean));
            var topTypes = stats.TypeCounts.OrderByDescending(x => x.Value).Take(8).ToArray();
            var typeLines = topTypes.Select(x => Abbreviate(x.Key, 12) + " " + x.Value.ToString("N0")).ToArray();
            _typeCounts.Text = string.Join("   ·   ", typeLines.Take(4));
            if (typeLines.Length > 4) _typeCounts.Text += "\n" + string.Join("   ·   ", typeLines.Skip(4));
            if (stats.TypeCounts.Count > 8)
                _typeCounts.Text += "\n… + " + (stats.TypeCounts.Count - 8).ToString("N0") + " more types";
            _typeCounts.ToolTip = string.Join("\n", topTypes.Select(x => Abbreviate(x.Key, 128) + ": " + x.Value.ToString("N0")));
            _position.MaxValue = Math.Max(1, result.RowIds.Count);
            _position.Enabled = result.RowIds.Count > 0;
            UpdatePosition();
        }

        private void UpdatePosition()
        {
            int count = _lastResult?.RowIds.Count ?? 0;
            int start = count == 0 ? 0 : _rows.FirstVisibleRow + 1;
            int end = Math.Min(count, _rows.FirstVisibleRow + _rows.VisibleRowCount);
            string? error = _session.Error;
            _status.ToolTip = error ?? string.Empty;
            _status.Text = error != null && error.Length > 0
                ? "View failed: " + (error.Length > 130 ? error.Substring(0, 127) + "…" : error)
                : _session.IsBusy ? "Updating view…" : string.Format(CultureInfo.CurrentCulture, "Showing {0:N0}–{1:N0} of {2:N0}   ·   Scroll / Home / End", start, end, count);
        }

        private void InspectRow(PanelRow row)
        {
            _detailSnapshot = _lastResult?.Snapshot;
            _address.Text = Abbreviate(row.Path, 48) + " [" + row.OriginalIndex + "]   ·   " + Abbreviate(row.TypeName, 24) + (row.IsValid ? string.Empty : "   ·   invalid");
            _address.ToolTip = row.Path + " [" + row.OriginalIndex + "]   ·   " + row.TypeName;
            _detail.Text = _session.GetDisplayValue(_detailSnapshot!, _rows.SelectedSourcePosition);
            _copy.Enabled = true;
        }

        private void ClearDetail()
        {
            _rows.ClearSelection();
            _address.Text = "Select an item to inspect its original address and full value.";
            _address.ToolTip = string.Empty;
            _detail.Text = string.Empty;
            _copy.Enabled = false;
        }

        private static string FormatNumber(double? value) => value?.ToString("G6", CultureInfo.CurrentCulture) ?? "—";

        private static string Abbreviate(string text, int maximumLength) => text.Length <= maximumLength ? text : text.Substring(0, maximumLength - 1) + "…";
    }
}
