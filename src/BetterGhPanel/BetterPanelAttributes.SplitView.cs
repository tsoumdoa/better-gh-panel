using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;

#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private RectangleF _pairedList, _pairedTrack, _pairedTrackHit, _split, _scrollLock, _dataCaption, _pairedCaption;
        private int _pairedOffset, _pairedShown, _paintedPairedOffset;
        private bool _scrollLocked = true, _scrollingPaired;
        private PanelQueryResult? _captionResult;
        private PanelSnapshot? _captionPairedSnapshot;
        private string _pairedTypeSummary = "";
        private readonly Dictionary<string, List<int>> _pairedBranchRows = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        private RectangleF ListBounds => _pairedList.IsEmpty ? _list : RectangleF.Union(_list, _pairedList);
        private bool ListContains(PointF point) => _list.Contains(point) || _pairedList.Contains(point);

        private void LayoutSplitPanes()
        {
            _pairedList = _pairedTrack = _pairedTrackHit = _scrollLock = _dataCaption = _pairedCaption = RectangleF.Empty;
            if (!PanelOwner.PanelSplit) return;
            float middle = _panel.X + _panel.Width / 2;
            _dataCaption = new RectangleF(_list.X, _list.Y - 2, middle - _list.X - 16, 14);
            _pairedCaption = new RectangleF(middle + 14, _dataCaption.Y, _dataCaption.Width, 14);
            _scrollLock = new RectangleF(middle - 10, _dataCaption.Y, 20, 14);
            float height = Math.Max(RowHeight, _list.Height - 14);
            _list = new RectangleF(_list.X, _list.Y + 14, middle - _list.X - 19, height);
            _pairedList = new RectangleF(middle + 14, _list.Y, _list.Width, height);
            _track = new RectangleF(middle - 18, _list.Y, 4, height);
            _trackHit = new RectangleF(middle - 21, _list.Y, 8, height);
            _pairedTrack = new RectangleF(_panel.Right - 18, _list.Y, 4, height);
            _pairedTrackHit = new RectangleF(_panel.Right - 21, _list.Y, 8, height);
        }

        private string OutputPortLabel(IGH_Param parameter) => PanelOwner.OutputScope == PanelOutputScope.Selected &&
            (ReferenceEquals(parameter, Owner.Params.Output[2]) || ReferenceEquals(parameter, Owner.Params.Output[3]))
                ? parameter.NickName + "·S" : parameter.NickName;

        private void DrawSplitActions(Graphics graphics, Font small, StringFormat trim)
        {
            Text(graphics, "D|F", small, PanelOwner.PanelSplit ? Accent : PanelOwner.CanSplit ? Muted : PanelTheme.Rgb(180, 185, 178), _split, trim);
            if (!PanelOwner.PanelSplit) return;
            using (var pen = new Pen(_scrollLocked ? Accent : Muted, 1))
            {
                float x = _scrollLock.X + 6, y = _scrollLock.Y + 6;
                graphics.DrawRectangle(pen, x, y, 8, 6);
                graphics.DrawArc(pen, x + (_scrollLocked ? 1 : 4), y - 5, 6, 8, 180, _scrollLocked ? 180 : 130);
            }
            var session = PanelOwner.Session;
            bool current = session.HasCurrentResult && !PanelOwner.Locked;
            string dataSummary = current ? $"{session.Result.RowPositions.Count:N0} · {TypeSummary(session.Result.Statistics.TypeCounts.Keys)}" : "…";
            Text(graphics, "D · Data  " + dataSummary, small, Accent, _dataCaption, trim);
            string pairSummary = "…";
            if (current && PanelOwner.PairedSnapshot != null)
            {
                if (!ReferenceEquals(_captionResult, session.Result) || !ReferenceEquals(_captionPairedSnapshot, PanelOwner.PairedSnapshot))
                {
                    _captionResult = session.Result;
                    _captionPairedSnapshot = PanelOwner.PairedSnapshot;
                    _pairedBranchRows.Clear();
                    var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (int position in session.Result.RowPositions)
                    {
                        var row = PanelOwner.PairedSnapshot.Rows[position];
                        types.Add(row.TypeName);
                        if (!DisplayListLengths) continue;
                        if (!_pairedBranchRows.TryGetValue(row.Path, out var positions))
                            _pairedBranchRows.Add(row.Path, positions = new List<int>());
                        if (positions.Count < 5) positions.Add(position);
                    }
                    _pairedTypeSummary = TypeSummary(types);
                }
                pairSummary = $"{session.Result.RowPositions.Count:N0} · {_pairedTypeSummary}";
            }
            Text(graphics, "F · Paired" + (PanelOwner.OutputScope == PanelOutputScope.Selected ? " · selected" : "") + "  " + pairSummary,
                small, Accent, _pairedCaption, trim);
        }

        private static string TypeSummary(IEnumerable<string> types)
        {
            var names = types.Take(3).Select(CanvasMenu.ShortTypeName).ToArray();
            return names.Length == 0 ? "Empty" : string.Join(" / ", names);
        }

        private static string PairedDisplayValue(PanelRow row) =>
            PanelValuePresentation.FormatDisplay(row, PanelUnitConversion.None, PanelUnits.AutomaticDecimals);

        private string PairedBranchValue(string path)
        {
            var snapshot = PanelOwner.PairedSnapshot;
            if (snapshot == null || !_pairedBranchRows.TryGetValue(path, out var positions)) return "<empty branch>";
            // Branch-level Display value has one D row for an entire F branch.
            // Keep its raw values in order, bounded to the available cell.
            var values = positions.Take(4).Select(index => Short(PanelValuePresentation.Visualize(PairedDisplayValue(snapshot.Rows[index])), 120));
            return string.Join(" · ", values) + (positions.Count > 4 ? " …" : "");
        }

        private void ClearPaneSummary()
        {
            _captionResult = null;
            _captionPairedSnapshot = null;
            _pairedBranchRows.Clear();
        }

        private bool SplitTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            if (PanelOwner.PanelMinimized) return false;
            if (_split.Contains(point))
            {
                e.Title = "D | F split view";
                e.Text = PanelOwner.PanelSplit ? "On" : "Off";
                e.Description = PanelOwner.CanSplit || PanelOwner.PanelSplit ? "Show paired values beside D. The saved width is doubled; selection is shared." :
                    PanelOwner.PairedError ?? "Connect F with exactly the same paths and branch lengths as D to enable split view.";
                return true;
            }
            if (_scrollLock.Contains(point))
            {
                e.Title = "Scroll sync";
                e.Text = _scrollLocked ? "Locked" : "Unlocked";
                e.Description = "Click to " + (_scrollLocked ? "scroll each pane independently. Selecting a row reveals its partner." : "lock scrolling and snap F to D.");
                return true;
            }
            if (_pairedList.Contains(point) && PanelOwner.PairedError != null)
            {
                e.Title = "F does not match D";
                e.Text = PanelOwner.PairedError;
                e.Description = "Pairing uses original branch paths and item indices, before tree tools.";
                return true;
            }
            return false;
        }

        private void DrawPairedDiagnostic(Graphics graphics, Font small)
        {
            string message = PanelOwner.PairedError ?? (PanelOwner.Locked ? "Disabled. Enable the component to recompute." :
                PanelOwner.Session.IsBusy ? "Recomputing…" : "Connect matching F data to inspect paired values.");
            using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter };
            Text(graphics, message, small, Muted,
                new RectangleF(_pairedList.X + 6, _pairedList.Y + 2, _pairedList.Width - 12, _pairedList.Height - 2), format);
        }

        private bool HandleSplitClick(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            if (_split.Contains(e.CanvasLocation))
            {
                if (PanelOwner.CanSplit || PanelOwner.PanelSplit)
                {
                    SuppressTrailingDoubleClick();
                    PanelOwner.SetPanelSplit(!PanelOwner.PanelSplit);
                    PerformLayout();
                    CanvasDoubleClickGuard.Start(canvas, e);
                    canvas.Invalidate();
                }
                return true;
            }
            if (!_scrollLock.Contains(e.CanvasLocation)) return false;
            _scrollLocked = !_scrollLocked;
            if (_scrollLocked) _pairedOffset = _offset;
            InvalidatePaintedHits();
            canvas.Invalidate();
            return true;
        }

        private void RevealPartner(GH_Canvas canvas, int position, string? path, PointF point)
        {
            if (!PanelOwner.PanelSplit || _scrollLocked || !PanelOwner.CanSplit) return;
            bool paired = !_pairedList.Contains(point);
            var partner = paired ? _pairedList : _list;
            if (path == null && _rowHits.Any(hit => hit.SourcePosition == position && partner.Contains(hit.Bounds))) return;
            if (path != null && _branchHits.Any(hit => hit.Path == path && partner.Contains(hit.Bounds))) return;
            var result = PanelOwner.Session.Result;
            int target;
            if (DisplayListLengths) target = Array.IndexOf(result.BranchPaths.ToArray(), path!);
            else if (path == null) target = result.TryGetViewIndex(position, out int index) ? result.ViewIndex.OffsetOfItem(index) : -1;
            else
            {
                target = -1;
                for (int index = 0; index < result.ViewIndex.Count; index++)
                {
                    result.ViewIndex.TryGet(index, out int item, out string? empty);
                    if ((empty ?? result.Snapshot.Rows[result.RowPositions[item]].Path) != path) continue;
                    target = index;
                    break;
                }
            }
            if (target >= 0) SetPaneOffset(canvas, target, paired);
        }
    }
}
#pragma warning restore CA1416
