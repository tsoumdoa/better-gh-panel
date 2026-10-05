using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

// Grasshopper's canvas drawing contract is supplied by Rhino on Windows and Mac.
#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed class BetterPanelAttributes : GH_ComponentAttributes
    {
        private const float RowHeight = 18, BranchHeight = 16, HeaderHeight = 25;
        private static readonly Color Ink = Color.FromArgb(49, 55, 53);
        private static readonly Color Muted = Color.FromArgb(120, 127, 123);
        private static readonly Color Accent = Color.FromArgb(77, 115, 94);
        private RectangleF _panel, _search, _type, _sort, _branch, _jump, _reset, _resize, _list, _track, _inspect, _footer;
        private RectangleF _menuRect;
        private readonly List<RowHit> _rowHits = new List<RowHit>();
        private int _offset, _shown, _selectedPosition = -1, _menuOffset;
        private PanelQueryResult? _lastResult;
        private PanelSnapshot? _selectedSnapshot;
        private PanelSnapshot? _menuSnapshot;
        private string[] _types = Array.Empty<string>();
        private MenuKind _menu;
        private bool _resizing, _scrolling;
        private PointF _dragStart;
        private float _startWidth, _startHeight, _thumbGrab;
        private DateTime _copiedUntil;
        private BetterPanelComponent PanelOwner => (BetterPanelComponent)Owner;
        private int Count => PanelOwner.Session.Result.RowIds.Count;
        private int MaximumOffset => Math.Max(0, Count - Math.Max(1, _shown));

        internal BetterPanelAttributes(BetterPanelComponent owner) : base(owner) { }

        protected override void Layout()
        {
            _panel = new RectangleF(Pivot.X, Pivot.Y, PanelOwner.CanvasWidth, PanelOwner.CanvasHeight);
            m_innerBounds = _panel;
            float x = _panel.X + 9, right = _panel.Right - 9;
            _inspect = new RectangleF(right - 17, _panel.Y + 4, 17, 17);
            _search = new RectangleF(x, _panel.Y + 33, _panel.Width - 174, 22);
            _type = new RectangleF(_search.Right + 5, _search.Y, 75, 22);
            _sort = new RectangleF(_type.Right + 5, _search.Y, 71, 22);
            _branch = new RectangleF(x, _panel.Y + 60, _panel.Width - 134, 22);
            _jump = new RectangleF(_branch.Right + 5, _branch.Y, 60, 22);
            _reset = new RectangleF(_jump.Right + 5, _branch.Y, 46, 22);
            _footer = new RectangleF(x, _panel.Bottom - 37, _panel.Width - 18, 30);
            _list = new RectangleF(x, _panel.Y + 90, _panel.Width - 28, Math.Max(18, _panel.Height - 134));
            _track = new RectangleF(_panel.Right - 14, _list.Y, 8, _list.Height);
            _resize = new RectangleF(_panel.Right - 13, _panel.Bottom - 13, 13, 13);
            // Linked parameter attributes keep Grasshopper's wire/tooltip behavior, but
            // expose actual wire grips at our rectangle instead of a hidden default capsule.
            PositionPorts(Owner.Params.Input, true);
            PositionPorts(Owner.Params.Output, false);
            Bounds = new RectangleF(_panel.X - 6, _panel.Y, _panel.Width + 12, _panel.Height);
            LayoutMenu();
        }

        private void PositionPorts(IList<IGH_Param> parameters, bool input)
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                var port = parameters[i].Attributes as PanelPortAttributes;
                if (port == null)
                {
                    port = new PanelPortAttributes(parameters[i], this, input);
                    parameters[i].Attributes = port;
                }
                var point = new PointF(input ? _panel.Left : _panel.Right, _panel.Y + HeaderHeight + (_panel.Height - HeaderHeight) * (i + 1) / (parameters.Count + 1));
                port.Place(point);
            }
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            if (channel == GH_CanvasChannel.Wires) { base.Render(canvas, graphics, channel); return; }
            if (channel != GH_CanvasChannel.Objects) return;
            CanvasTextInput.Maintain(canvas);
            var session = PanelOwner.Session;
            var result = session.Result;
            if (!ReferenceEquals(result, _lastResult))
            {
                if (_selectedPosition >= 0 && (_selectedSnapshot != result.Snapshot || !result.RowIds.Contains(_selectedPosition))) _selectedPosition = -1;
                _lastResult = result;
                _offset = 0;
                _menu = MenuKind.None;
            }
            if (_menuSnapshot != session.Snapshot) _menu = MenuKind.None;
            // Layout is measured in canvas units. Pixel fonts follow the canvas
            // transform without adding the display DPI a second time.
            using (var font = new Font("Arial", 11f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var small = new Font("Arial", 10f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var bold = new Font("Arial", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var mono = new Font(FontFamily.GenericMonospace, 10f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var trim = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
            {
                Fill(graphics, Color.FromArgb(248, 249, 247), _panel);
                Fill(graphics, Color.FromArgb(45, 50, 48), new RectangleF(_panel.X, _panel.Y, _panel.Width, HeaderHeight));
                Text(graphics, "Panel+", bold, Color.FromArgb(241, 245, 239), new RectangleF(_panel.X + 10, _panel.Y + 2, 85, 21), trim);
                var itemLabel = result.Statistics.ItemCount == 1 ? "item" : "items";
                var branchLabel = result.Statistics.BranchCount == 1 ? "branch" : "branches";
                Text(graphics, $"{result.Statistics.ItemCount:N0} {itemLabel}  /  {result.Statistics.BranchCount:N0} {branchLabel}", small, Color.FromArgb(181, 194, 184), new RectangleF(_panel.X + 94, _panel.Y + 2, _panel.Width - 128, 21), trim);
                Text(graphics, "↗", bold, Color.FromArgb(202, 213, 204), _inspect, trim);
                if (canvas.Viewport.Zoom >= 0.5f)
                {
                    var options = session.Options;
                    Control(graphics, _search, string.IsNullOrEmpty(options.Search) ? "Search values or > 800…" : options.Search, small, trim, !string.IsNullOrEmpty(options.Search));
                    Control(graphics, _type, Short(options.OnlyInvalid ? "Invalid" : options.TypeName ?? "All types", 60) + " ▾", small, trim, options.TypeName != null || options.OnlyInvalid);
                    Control(graphics, _sort, options.SortMode == PanelSortMode.SourceOrder ? "Source ▾" : options.SortMode == PanelSortMode.ValueAscending ? "Value ↑ ▾" : "Value ↓ ▾", small, trim, options.SortMode != PanelSortMode.SourceOrder);
                    Control(graphics, _branch, Short(options.BranchPath ?? "All branches", 80) + " ▾", mono, trim, options.BranchPath != null);
                    Control(graphics, _jump, "Jump row", small, trim, false);
                    Control(graphics, _reset, "Reset", small, trim, false);
                    RenderRows(graphics, result, font, small, bold, mono, trim);
                    RenderFooter(graphics, result, small, mono, trim);
                    if (_menu != MenuKind.None) RenderMenu(graphics, small, mono, trim);
                    using (var gripPen = new Pen(Color.FromArgb(165, 173, 166), 1))
                    {
                        graphics.DrawLine(gripPen, _panel.Right - 10, _panel.Bottom - 4, _panel.Right - 4, _panel.Bottom - 10);
                        graphics.DrawLine(gripPen, _panel.Right - 6, _panel.Bottom - 4, _panel.Right - 4, _panel.Bottom - 6);
                    }
                }
                else { _rowHits.Clear(); _menu = MenuKind.None; }
                using (var border = new Pen(Selected ? Accent : Color.FromArgb(164, 171, 164), Selected ? 1.5f : 1))
                    graphics.DrawRectangle(border, _panel.X, _panel.Y, _panel.Width, _panel.Height);
                foreach (var parameter in Owner.Params.Input)
                    GH_CapsuleRenderEngine.RenderInputGrip(graphics, canvas.Viewport.Zoom, parameter.Attributes.InputGrip, false);
                foreach (var parameter in Owner.Params.Output)
                    GH_CapsuleRenderEngine.RenderOutputGrip(graphics, canvas.Viewport.Zoom, parameter.Attributes.OutputGrip, false);
            }
        }

        private void RenderRows(Graphics g, PanelQueryResult result, Font font, Font small, Font bold, Font mono, StringFormat trim)
        {
            _rowHits.Clear();
            _shown = 0;
            float y = _list.Y;
            string? previousPath = null;
            bool grouped = PanelOwner.Session.Options.SortMode == PanelSortMode.SourceOrder;
            _offset = Math.Max(0, Math.Min(Math.Max(0, result.RowIds.Count - 1), _offset));
            var clip = g.Save();
            try
            {
                g.SetClip(_list, CombineMode.Intersect);
                for (int i = _offset; i < result.RowIds.Count; i++)
                {
                    int sourcePosition = result.RowIds[i];
                    var row = result.Snapshot.Rows[sourcePosition];
                    if (grouped && row.Path != previousPath)
                    {
                        if (y + BranchHeight + RowHeight > _list.Bottom) break;
                        Fill(g, Color.FromArgb(231, 235, 229), new RectangleF(_list.X, y, _list.Width, BranchHeight));
                        Text(g, row.Path, mono, Accent, new RectangleF(_list.X + 6, y, _list.Width - 12, BranchHeight), trim);
                        y += BranchHeight;
                        previousPath = row.Path;
                    }
                    if (y + RowHeight > _list.Bottom) break;
                    var bounds = new RectangleF(_list.X, y, _list.Width, RowHeight);
                    Fill(g, sourcePosition == _selectedPosition ? Color.FromArgb(216, 232, 219) : (_shown & 1) == 0 ? Color.FromArgb(252, 253, 251) : Color.FromArgb(243, 246, 241), bounds);
                    if (!row.IsValid) Fill(g, Color.FromArgb(180, 106, 67), new RectangleF(bounds.X, bounds.Y + 3, 2, bounds.Height - 6));
                    Text(g, row.OriginalIndex.ToString(CultureInfo.InvariantCulture), mono, Muted, new RectangleF(bounds.X + 6, y, 37, RowHeight), trim);
                    float valueStart = 47;
                    if (!grouped)
                    {
                        Text(g, row.Path, mono, Accent, new RectangleF(bounds.X + 44, y, 68, RowHeight), trim);
                        valueStart = 116;
                    }
                    Text(g, row.DisplayValue, font, Ink, new RectangleF(bounds.X + valueStart, y, bounds.Width - valueStart - 4, RowHeight), trim);
                    _rowHits.Add(new RowHit(bounds, sourcePosition));
                    _shown++;
                    y += RowHeight;
                }
                if (result.RowIds.Count == 0)
                {
                    Text(g, result.Snapshot.Rows.Count == 0 ? "Connect a tree to start exploring." : "No matching items. Clear a filter or Reset.", small, Muted, new RectangleF(_list.X + 6, _list.Y + 12, _list.Width - 12, 22), trim);
                }
            }
            finally { g.Restore(clip); }
            if (Count > _shown)
            {
                Fill(g, Color.FromArgb(232, 236, 229), _track);
                Fill(g, Color.FromArgb(153, 174, 156), Thumb());
            }
        }

        private void RenderFooter(Graphics g, PanelQueryResult result, Font small, Font mono, StringFormat trim)
        {
            var stats = result.Statistics;
            string metrics = stats.NumericCount > 0
                ? $"min {Number(stats.Minimum)}   max {Number(stats.Maximum)}   avg {Number(stats.Mean)}"
                : $"{stats.TypeCounts.Count:N0} types   ·   {stats.NullCount:N0} null   ·   {stats.InvalidCount:N0} invalid";
            Text(g, metrics, mono, Ink, new RectangleF(_footer.X, _footer.Y, _footer.Width - 5, 14), trim);
            var session = PanelOwner.Session;
            string status = session.Error != null ? "Query failed: " + Short(session.Error, 150) : session.IsBusy ? "Updating…" : DateTime.UtcNow < _copiedUntil ? "Value copied" : $"{(Count == 0 ? 0 : _offset + 1):N0}–{Math.Min(Count, _offset + _shown):N0} / {Count:N0}  ·  view only";
            Text(g, status, small, Muted, new RectangleF(_footer.X, _footer.Y + 15, _footer.Width - 5, 14), trim);
        }

        private RectangleF Thumb()
        {
            float height = Math.Min(_track.Height, Math.Max(20, _track.Height * Math.Max(1, _shown) / Math.Max(1, Count)));
            float y = _track.Y + (_track.Height - height) * _offset / Math.Max(1, MaximumOffset);
            return new RectangleF(_track.X, y, _track.Width, height);
        }

        private void SetOffset(GH_Canvas canvas, int value)
        {
            _offset = Math.Max(0, Math.Min(MaximumOffset, value));
            canvas.Invalidate();
        }

        private void LayoutMenu()
        {
            if (_menu == MenuKind.None) return;
            var anchor = _menu == MenuKind.Type ? _type : _menu == MenuKind.Sort ? _sort : _branch;
            float width = Math.Min(_panel.Width - 18, _menu == MenuKind.Sort ? 145 : 235);
            int visible = Math.Max(2, Math.Min(8, (int)((_panel.Bottom - 9 - anchor.Bottom - 19) / 20)));
            _menuRect = new RectangleF(Math.Min(anchor.X, _panel.Right - width - 9), anchor.Bottom + 2, width, Math.Min(visible, MenuCount) * 20 + (MenuCount > visible ? 20 : 0));
        }

        private int MenuVisible => Math.Min(MenuCount, Math.Max(2, (int)(_menuRect.Height / 20) - (MenuCount * 20 > _menuRect.Height ? 1 : 0)));
        private int MenuCount => _menu == MenuKind.Type ? _types.Length + 2 : _menu == MenuKind.Sort ? 3 : _menu == MenuKind.Branch ? PanelOwner.Session.Snapshot.Branches.Count + 2 : 0;

        private string MenuLabel(int index)
        {
            if (_menu == MenuKind.Type)
                return index == 0 ? "All types" : index == 1 ? (PanelOwner.Session.Options.OnlyInvalid ? "✓ Invalid only" : "Invalid only") : _types[index - 2];
            if (_menu == MenuKind.Sort) return new[] { "Source order", "Value ascending", "Value descending" }[index];
            if (index == 0) return "All branches";
            if (index == 1) return "Jump to exact path…";
            var branch = PanelOwner.Session.Snapshot.Branches[index - 2];
            return Short(branch.Path, 150) + "   ·   " + branch.ItemCount.ToString("N0");
        }

        private void RenderMenu(Graphics g, Font small, Font mono, StringFormat trim)
        {
            LayoutMenu();
            Fill(g, Color.FromArgb(226, 230, 223), new RectangleF(_menuRect.X + 2, _menuRect.Y + 2, _menuRect.Width, _menuRect.Height));
            Fill(g, Color.FromArgb(253, 254, 251), _menuRect);
            using (var pen = new Pen(Color.FromArgb(143, 158, 142))) g.DrawRectangle(pen, _menuRect.X, _menuRect.Y, _menuRect.Width, _menuRect.Height);
            int visible = MenuVisible;
            _menuOffset = Math.Max(0, Math.Min(Math.Max(0, MenuCount - visible), _menuOffset));
            for (int i = 0; i < visible && i + _menuOffset < MenuCount; i++)
                Text(g, MenuLabel(i + _menuOffset), _menu == MenuKind.Branch ? mono : small, Ink, new RectangleF(_menuRect.X + 7, _menuRect.Y + i * 20, _menuRect.Width - 14, 20), trim);
            if (MenuCount > visible)
            {
                float y = _menuRect.Bottom - 20;
                Fill(g, Color.FromArgb(235, 241, 232), new RectangleF(_menuRect.X, y, _menuRect.Width, 20));
                Text(g, "‹ Previous", small, Accent, new RectangleF(_menuRect.X + 7, y, _menuRect.Width / 2 - 7, 20), trim);
                Text(g, "Next ›", small, Accent, new RectangleF(_menuRect.X + _menuRect.Width / 2, y, _menuRect.Width / 2 - 7, 20), trim);
            }
        }

        private void OpenMenu(MenuKind kind, GH_Canvas canvas)
        {
            _menu = _menu == kind ? MenuKind.None : kind;
            _menuSnapshot = PanelOwner.Session.Snapshot;
            _menuOffset = 0;
            if (_menu == MenuKind.Type)
            {
                var keys = PanelOwner.Session.Snapshot.TypeCounts.Keys;
                var active = PanelOwner.Session.Options.TypeName;
                _types = keys.Concat(active != null && !keys.Contains(active) ? new[] { active } : Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            }
            LayoutMenu();
            canvas.Invalidate();
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (sender.Viewport.Zoom < 0.5f) return base.RespondToMouseDown(sender, e);
            if (e.Button != MouseButtons.Left) return base.RespondToMouseDown(sender, e);
            var point = e.CanvasLocation;
            if (_menu != MenuKind.None)
            {
                if (_menuSnapshot != PanelOwner.Session.Snapshot)
                {
                    _menu = MenuKind.None;
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
                if (_menuRect.Contains(point))
                {
                    int row = (int)((point.Y - _menuRect.Y) / 20);
                    if (MenuCount > MenuVisible && row >= MenuVisible)
                    {
                        _menuOffset += point.X < _menuRect.X + _menuRect.Width / 2 ? -MenuVisible : MenuVisible;
                        sender.Invalidate();
                        return GH_ObjectResponse.Handled;
                    }
                    ChooseMenu(_menuOffset + row, sender);
                }
                else { _menu = MenuKind.None; sender.Invalidate(); }
                return GH_ObjectResponse.Handled;
            }
            if (_resize.Contains(point))
            {
                PanelOwner.RecordUndoEvent("Resize Panel+");
                _resizing = true; _dragStart = point; _startWidth = _panel.Width; _startHeight = _panel.Height;
                return GH_ObjectResponse.Capture;
            }
            if (_track.Contains(point) && Count > _shown)
            {
                var thumb = Thumb();
                if (thumb.Contains(point)) { _scrolling = true; _thumbGrab = point.Y - thumb.Y; return GH_ObjectResponse.Capture; }
                SetOffset(sender, _offset + (point.Y < thumb.Y ? -Math.Max(1, _shown) : Math.Max(1, _shown)));
                return GH_ObjectResponse.Handled;
            }
            if (_inspect.Contains(point)) { PanelOwner.OpenInspector(); return GH_ObjectResponse.Handled; }
            if (_search.Contains(point)) { EditSearch(sender); return GH_ObjectResponse.Handled; }
            if (_type.Contains(point)) { OpenMenu(MenuKind.Type, sender); return GH_ObjectResponse.Handled; }
            if (_sort.Contains(point)) { OpenMenu(MenuKind.Sort, sender); return GH_ObjectResponse.Handled; }
            if (_branch.Contains(point)) { OpenMenu(MenuKind.Branch, sender); return GH_ObjectResponse.Handled; }
            if (_jump.Contains(point))
            {
                CanvasTextInput.Show(sender, _jump, (_offset + 1).ToString(), text =>
                {
                    if (!int.TryParse(text, out var row)) return;
                    // Put the requested item first; grouped branch headings can change
                    // page capacity, so previous-page size must not hide the final item.
                    _offset = Math.Max(0, Math.Min(Math.Max(0, Count - 1), row <= 1 ? 0 : row - 1));
                    sender.Invalidate();
                });
                return GH_ObjectResponse.Handled;
            }
            if (_reset.Contains(point)) { PanelOwner.Session.ResetView(); return GH_ObjectResponse.Handled; }
            var currentResult = PanelOwner.Session.Result;
            if (_list.Contains(point) && !ReferenceEquals(_lastResult, currentResult))
            {
                _rowHits.Clear();
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            foreach (var hit in _rowHits)
                if (hit.Bounds.Contains(point) && hit.SourcePosition >= 0 && hit.SourcePosition < currentResult.Snapshot.Rows.Count)
                { _selectedPosition = hit.SourcePosition; _selectedSnapshot = currentResult.Snapshot; sender.Invalidate(); return GH_ObjectResponse.Handled; }
            return base.RespondToMouseDown(sender, e);
        }

        private void ChooseMenu(int index, GH_Canvas canvas)
        {
            if (index < 0 || index >= MenuCount) return;
            var kind = _menu;
            _menu = MenuKind.None;
            var options = PanelOwner.Session.Options;
            if (kind == MenuKind.Type)
            {
                if (index == 1) options.OnlyInvalid = !options.OnlyInvalid;
                else { options.TypeName = index == 0 ? null : _types[index - 2]; if (index == 0) options.OnlyInvalid = false; }
            }
            else if (kind == MenuKind.Sort) options.SortMode = (PanelSortMode)index;
            else if (index == 1)
            {
                CanvasTextInput.Show(canvas, _branch, options.BranchPath ?? "{0}", text => { var query = PanelOwner.Session.Options; query.BranchPath = string.IsNullOrWhiteSpace(text) ? null : text.Trim(); query.BranchPrefix = false; PanelOwner.Session.UpdateQuery(query); });
                canvas.Invalidate();
                return;
            }
            else { options.BranchPath = index == 0 ? null : PanelOwner.Session.Snapshot.Branches[index - 2].Path; options.BranchPrefix = false; }
            PanelOwner.Session.UpdateQuery(options);
            canvas.Invalidate();
        }

        private void EditSearch(GH_Canvas canvas) => CanvasTextInput.Show(canvas, _search, PanelOwner.Session.Options.Search, text => { var options = PanelOwner.Session.Options; options.Search = text; PanelOwner.Session.UpdateQuery(options); });

        public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (_resizing)
            {
                PanelOwner.SetCanvasSize(_startWidth + e.CanvasX - _dragStart.X, _startHeight + e.CanvasY - _dragStart.Y);
                PerformLayout();
                sender.Invalidate();
                return GH_ObjectResponse.Capture;
            }
            if (_scrolling)
            {
                float travel = _track.Height - Thumb().Height;
                if (travel > 0) SetOffset(sender, (int)Math.Round((e.CanvasY - _track.Y - _thumbGrab) / travel * MaximumOffset));
                return GH_ObjectResponse.Capture;
            }
            return base.RespondToMouseMove(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (_resizing || _scrolling) { _resizing = false; _scrolling = false; return GH_ObjectResponse.Release; }
            return base.RespondToMouseUp(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (sender.Viewport.Zoom < 0.5f) return base.RespondToMouseDoubleClick(sender, e);
            var result = PanelOwner.Session.Result;
            if (_list.Contains(e.CanvasLocation) && !ReferenceEquals(_lastResult, result))
            {
                _rowHits.Clear();
                sender.Invalidate();
                return GH_ObjectResponse.Handled;
            }
            foreach (var hit in _rowHits)
                if (hit.Bounds.Contains(e.CanvasLocation) && hit.SourcePosition >= 0 && hit.SourcePosition < result.Snapshot.Rows.Count)
                {
                    using (var clipboard = new Eto.Forms.Clipboard()) clipboard.Text = PanelOwner.Session.GetDisplayValue(result.Snapshot.Rows[hit.SourcePosition]);
                    _copiedUntil = DateTime.UtcNow.AddSeconds(3);
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
            if (_search.Contains(e.CanvasLocation) || _list.Contains(e.CanvasLocation)) { EditSearch(sender); return GH_ObjectResponse.Handled; }
            return base.RespondToMouseDoubleClick(sender, e);
        }

        public override void SetupTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            base.SetupTooltip(point, e);
            if (_search.Contains(point))
            {
                e.Title = "Search this view";
                e.Text = "Press Enter to apply";
                e.Description = "Search values, paths and types, or use numeric comparisons such as > 800 or <= 25.";
            }
            else if (_jump.Contains(point))
            {
                e.Title = "Jump to a row";
                e.Text = "Position in the current filtered and sorted view";
                e.Description = "Enter a row number starting at 1. The index beside each value remains its original index within its branch.";
            }
            else if (_resize.Contains(point))
            {
                e.Title = "Resize Panel+";
                e.Text = "Drag this corner";
                e.Description = "The panel size is saved with the Grasshopper definition.";
            }
            else if (ReferenceEquals(_lastResult, PanelOwner.Session.Result))
            {
                foreach (var hit in _rowHits)
                {
                    if (!hit.Bounds.Contains(point)) continue;
                    var row = PanelOwner.Session.Result.Snapshot.Rows[hit.SourcePosition];
                    e.Title = row.Path + " [" + row.OriginalIndex + "]";
                    e.Text = row.TypeName;
                    var value = row.DisplayValue;
                    e.Description = (value.Length > 1000 ? value.Substring(0, 997) + "…" : value) + "\n\nDouble-click to copy the full value.";
                    break;
                }
            }
        }

        private static void Fill(Graphics g, Color color, RectangleF bounds) { using (var brush = new SolidBrush(color)) g.FillRectangle(brush, bounds); }
        private static void Text(Graphics g, string value, Font font, Color color, RectangleF bounds, StringFormat trim)
        {
            value = value ?? string.Empty;
            if (value.Length > 256) value = value.Substring(0, 253) + "…";
            value = value.Replace('\r', ' ').Replace('\n', ' ');
            using (var brush = new SolidBrush(color)) g.DrawString(value, font, brush, bounds, trim);
        }
        private static void Control(Graphics g, RectangleF bounds, string value, Font font, StringFormat trim, bool active)
        {
            Fill(g, active ? Color.FromArgb(227, 237, 223) : Color.White, bounds);
            using (var pen = new Pen(Color.FromArgb(212, 219, 208))) g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            Text(g, value, font, active ? Accent : Muted, new RectangleF(bounds.X + 6, bounds.Y, bounds.Width - 12, bounds.Height), trim);
        }
        private static string Number(double? value) => value?.ToString("G5", CultureInfo.CurrentCulture) ?? "—";
        private static string Short(string text, int maximum) => text.Length <= maximum ? text : text.Substring(0, maximum - 1) + "…";
        private enum MenuKind { None, Type, Sort, Branch }
        private readonly struct RowHit
        {
            internal RowHit(RectangleF bounds, int sourcePosition) { Bounds = bounds; SourcePosition = sourcePosition; }
            internal RectangleF Bounds { get; }
            internal int SourcePosition { get; }
        }

        private sealed class PanelPortAttributes : GH_LinkedParamAttributes
        {
            private PointF _grip;
            private readonly bool _input;
            internal PanelPortAttributes(IGH_Param owner, IGH_Attributes parent, bool input) : base(owner, parent) { _input = input; }
            internal void Place(PointF grip) { _grip = grip; Pivot = grip; Bounds = new RectangleF(grip.X - 6, grip.Y - 9, 12, 18); }
            protected override void Layout() => Bounds = new RectangleF(_grip.X - 6, _grip.Y - 9, 12, 18);
            public override PointF InputGrip => _input ? _grip : base.InputGrip;
            public override PointF OutputGrip => !_input ? _grip : base.OutputGrip;
        }
    }
}
#pragma warning restore CA1416
