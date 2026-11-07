using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;

#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private enum ControlPath { Click, Hover, Tooltip, Cursor }
        private enum ControlStep
        {
            None, Header, BeforeDetails, BeforeTrack, Main, AfterStaleList,
            Capsule, BeforeStatisticsMenu, BeforeRowActions
        }

        private sealed class ControlEntry
        {
            internal readonly string Id;
            internal readonly Func<PanelLayout, RectangleF> Rect;
            internal readonly ControlStep ClickStep, HoverStep, TooltipStep;
            internal readonly Func<GH_Canvas, GH_CanvasMouseEvent, GH_ObjectResponse?>? OnClick;
            internal readonly Action<PointF, GH_TooltipDisplayEventArgs>? Tooltip;
            internal readonly Func<ControlPath, bool> Visible;
            internal readonly Func<PointF, bool>? Hit, TooltipHit;
            internal readonly Cursor? Cursor;
            internal readonly Func<IEnumerable<RectangleF>>? HoverRects;

            internal ControlEntry(string id, Func<PanelLayout, RectangleF> rect,
                Func<GH_Canvas, GH_CanvasMouseEvent, GH_ObjectResponse?>? onClick,
                Action<PointF, GH_TooltipDisplayEventArgs>? onTooltip,
                ControlStep click = ControlStep.None, ControlStep hover = ControlStep.None,
                ControlStep tooltip = ControlStep.None,
                Func<ControlPath, bool>? visible = null, Func<PointF, bool>? hit = null,
                Func<PointF, bool>? tooltipHit = null, Cursor? cursor = null, Func<IEnumerable<RectangleF>>? hoverRects = null)
            {
                Id = id; Rect = rect;
                ClickStep = click; HoverStep = hover; TooltipStep = tooltip;
                OnClick = onClick; Tooltip = onTooltip; Visible = visible ?? (_ => true);
                Hit = hit; TooltipHit = tooltipHit; Cursor = cursor; HoverRects = hoverRects;
            }

            internal ControlStep Step(ControlPath path) => path == ControlPath.Click ? ClickStep :
                path == ControlPath.Hover ? HoverStep : path == ControlPath.Tooltip ? TooltipStep :
                Cursor == null ? ControlStep.None : ControlStep.Main;
        }

        private ControlEntry[]? _controlHits;
        private ControlEntry[] ControlHits => _controlHits ??= CreateControlHits();

        // All paths use this array order within their named insertion step.
        // Nested clear/filter controls precede their parent fields; SelectAll
        // precedes the statistics tooltip region; Footer follows its controls.
        // Resize precedes Move for cursor picking, independently of hover fill.
        private ControlEntry[] CreateControlHits() => new[]
        {
            new ControlEntry("Close", layout => layout.Close, ClickClose, TooltipClose, click: ControlStep.Header, hover: ControlStep.Header, tooltip: ControlStep.BeforeStatisticsMenu),
            new ControlEntry("Display", layout => layout.Display, ClickDisplay, TooltipDisplay, click: ControlStep.Header, hover: ControlStep.Header, tooltip: ControlStep.Main),
            new ControlEntry("Resize", layout => layout.Resize, ClickResize, TooltipResize, click: ControlStep.BeforeTrack, tooltip: ControlStep.Main, cursor: Cursors.SizeNWSE),
            new ControlEntry("Move", layout => layout.Move, ClickMove, TooltipMove, click: ControlStep.Main, tooltip: ControlStep.BeforeRowActions, cursor: Cursors.SizeAll),
            new ControlEntry("Warnings", layout => RectangleF.Empty, ClickWarnings, TooltipWarnings, click: ControlStep.AfterStaleList, hover: ControlStep.Main, tooltip: ControlStep.Main, hit: point => _warningHits.Any(hit => hit.Bounds.Contains(point)), hoverRects: () => _warningHits.Select(hit => hit.Bounds)),
            new ControlEntry("Reset", layout => layout.Reset, ClickReset, TooltipReset, click: ControlStep.BeforeDetails, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("Preview", layout => layout.Preview, ClickPreview, TooltipPreview, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("SearchClear", layout => layout.SearchClear, ClickSearchClear, TooltipSearchClear, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main, visible: path => SearchClearVisible),
            new ControlEntry("Filter", layout => layout.Filter, ClickFilter, TooltipFilter, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("Search", layout => layout.Search, ClickSearch, TooltipSearch, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("BranchClear", layout => layout.BranchClear, ClickBranchClear, TooltipBranchClear, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main, visible: path => BranchClearVisible),
            new ControlEntry("Type", layout => layout.Type, ClickType, TooltipQuery, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("Sort", layout => layout.Sort, ClickSort, TooltipQuery, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("Branch", layout => layout.Branch, ClickBranch, TooltipQuery, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("SelectAll", layout => layout.SelectAll, ClickSelectAll, TooltipSelectAll, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("Statistics", layout => layout.Statistics, ClickStatistics, TooltipStatistics, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main, tooltipHit: point => _layout.Statistics.Contains(point) || _layout.Footer.Contains(point) && point.Y < _layout.Units.Y && !_layout.OutLabel.Contains(point) && !_layout.OutItems.Contains(point) && !_layout.OutValues.Contains(point)),
            new ControlEntry("Trim", layout => layout.Trim, ClickTrim, TooltipTree, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("Simplify", layout => layout.Simplify, ClickTree, TooltipTree, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("Flatten", layout => layout.Flatten, ClickTree, TooltipTree, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("Reverse", layout => layout.Reverse, ClickTree, TooltipTree, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("CreateSet", layout => layout.CreateSet, ClickTree, TooltipTree, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("Units", layout => layout.Units, ClickUnits, TooltipUnits, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("OutLabel", layout => layout.OutLabel, null, TooltipOutput, tooltip: ControlStep.Main),
            new ControlEntry("OutItems", layout => layout.OutItems, ClickOutput, TooltipOutput, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main, visible: path => path == ControlPath.Tooltip || PanelOwner.CanOutputValues),
            new ControlEntry("OutValues", layout => layout.OutValues, ClickOutput, TooltipOutput, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main, visible: path => path == ControlPath.Tooltip || PanelOwner.CanOutputValues),
            new ControlEntry("Jump", _ => _jumpBounds, ClickJump, TooltipJump, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("ClearFilters", _ => _clearFiltersBounds, ClickClearFilters, TooltipClearFilters, click: ControlStep.Main, hover: ControlStep.Main, tooltip: ControlStep.Main),
            new ControlEntry("Footer", layout => layout.Footer, null, TooltipFooter, tooltip: ControlStep.Main),
            new ControlEntry("Restore", layout => layout.Restore, ClickRestore, TooltipRestore, click: ControlStep.Capsule, hover: ControlStep.Capsule, tooltip: ControlStep.Capsule)
        };

        private ControlEntry? ControlAt(ControlPath path, PointF point, ControlStep step)
        {
            foreach (var entry in ControlHits)
            {
                if (entry.Step(path) != step || !entry.Visible(path)) continue;
                var hit = path == ControlPath.Tooltip ? entry.TooltipHit ?? entry.Hit : entry.Hit;
                if (hit?.Invoke(point) ?? entry.Rect(_layout).Contains(point)) return entry;
            }
            return null;
        }

        private bool ControlClick(ControlStep step, GH_Canvas sender, GH_CanvasMouseEvent e, out GH_ObjectResponse response)
        {
            var entry = ControlAt(ControlPath.Click, e.CanvasLocation, step);
            var result = entry?.OnClick?.Invoke(sender, e);
            response = result.GetValueOrDefault();
            return result.HasValue;
        }

        private bool ControlTooltip(PointF point, GH_TooltipDisplayEventArgs e, ControlStep step)
        {
            var entry = ControlAt(ControlPath.Tooltip, point, step);
            if (entry == null) return false;
            entry.Tooltip!(point, e);
            return true;
        }

        private GH_ObjectResponse? ClickMove(GH_Canvas sender, GH_CanvasMouseEvent e) => base.RespondToMouseDown(sender, e);

        private GH_ObjectResponse? ClickWarnings(GH_Canvas sender, GH_CanvasMouseEvent e) =>
            TryWarningClick(e.CanvasLocation, sender) ? GH_ObjectResponse.Handled : (GH_ObjectResponse?)null;

        private GH_ObjectResponse? ClickClose(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            SuppressTrailingDoubleClick();
            PanelOwner.SetPanelMinimized(true);
            PerformLayout();
            CanvasDoubleClickGuard.Start(sender, e);
            sender.Invalidate();
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickDisplay(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            // Expanding Half moves this header away from the pointer and can
            // expose a row beneath the second click of the same gesture.
            SuppressTrailingDoubleClick();
            _menu.Close();
            CloseRowMenu();
            PanelOwner.SetCanvasDisplay(CanvasPanelSize.NextDisplay(PanelOwner.CanvasDisplay));
            PerformLayout();
            CanvasDoubleClickGuard.Start(sender, e);
            sender.Invalidate();
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickReset(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            bool recordedUndo = PanelOwner.ResetAll();
            ShowNotice(sender, recordedUndo ? "Reset all · Undo restores the previous settings" : "Reset all");
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickResize(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            var point = e.CanvasLocation;
            PanelOwner.RecordUndoEvent("Panel+ resize");
            _resizing = true; _dragStart = point; _startWidth = _layout.Panel.Width; _startHeight = _layout.Panel.Height;
            return GH_ObjectResponse.Capture;
        }

        private GH_ObjectResponse? ClickOutput(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            var point = e.CanvasLocation;
            PanelOwner.SetOutputValues(_layout.OutValues.Contains(point) ? PanelOutputValues.Values : PanelOutputValues.Items);
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickTrim(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            CanvasTextInput.Show(sender, PanelOwner, _layout.Trim, PanelOwner.TrimDepth.ToString(), text =>
            {
                if (int.TryParse(text.Trim(), out var depth) && depth >= 0)
                    PanelOwner.SetTreeOperations(depth, PanelOwner.FlattenTree, PanelOwner.ReverseTree, PanelOwner.SimplifyTree, PanelOwner.CreateSetTree);
                else ShowNotice(sender, "Trim depth must be a whole number, 0 or more (0 turns Trim off)");
            }, valid: text => int.TryParse(text.Trim(), out int depth) && depth >= 0);
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickTree(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            var point = e.CanvasLocation;
            PanelOwner.SetTreeOperations(PanelOwner.TrimDepth,
                _layout.Flatten.Contains(point) ? !PanelOwner.FlattenTree : PanelOwner.FlattenTree,
                _layout.Reverse.Contains(point) ? !PanelOwner.ReverseTree : PanelOwner.ReverseTree,
                _layout.Simplify.Contains(point) ? !PanelOwner.SimplifyTree : PanelOwner.SimplifyTree,
                _layout.CreateSet.Contains(point) ? !PanelOwner.CreateSetTree : PanelOwner.CreateSetTree);
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickSelectAll(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            var result = PanelOwner.Session.Result;
            if (RowsAreCurrent(result)) SelectAllRows(sender, result);
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickPreview(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.Preview, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickStatistics(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.Statistics, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickSearchClear(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            ClearSearch(sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickBranchClear(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            SetBranchFilter(null); sender.Invalidate(); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickFilter(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.SearchMode, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickSearch(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            EditSearch(sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickUnits(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.Units, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickType(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.Type, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickSort(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.Sort, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickBranch(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.Branch, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickClearFilters(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            PanelOwner.ClearFilters();
            ShowNotice(sender, "Filters cleared · Undo restores them");
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse? ClickJump(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            ShowJumpEditor(sender); return GH_ObjectResponse.Handled;
        }

        // Opens on the footer range it replaces; Ctrl/Cmd+G uses the footer's start when no range is painted.
        private void ShowJumpEditor(GH_Canvas sender)
        {
            var jumpResult = PanelOwner.Session.Result;
            bool branchView = DisplayListLengths;
            int firstItem = branchView ? _d.Offset : ViewItemAtOrAfter(jumpResult, _d.Offset);
            var anchor = _jumpBounds.IsEmpty ? new RectangleF(_layout.Footer.X, _layout.Units.Y, 60, _layout.Units.Height) : _jumpBounds;
            CanvasTextInput.Show(sender, PanelOwner, anchor, (Math.Min(firstItem, Math.Max(0, (branchView ? jumpResult.BranchPaths.Count : jumpResult.RowPositions.Count) - 1)) + 1).ToString(), text =>
            {
                if (!ResultIsCurrent(jumpResult) || DisplayListLengths != branchView) return;
                int itemCount = branchView ? jumpResult.BranchPaths.Count : jumpResult.RowPositions.Count;
                if (!int.TryParse(text.Trim(), out var row))
                {
                    ShowNotice(sender, System.FormattableString.Invariant($"Enter a row number from 1 to {Math.Max(1, itemCount):N0}"));
                    return;
                }
                // Put the requested item first; grouped branch headings can change
                // page capacity, so previous-page size must not hide the final item.
                int index = Math.Max(0, Math.Min(Math.Max(0, itemCount - 1), row <= 1 ? 0 : row - 1));
                int offset = itemCount == 0 ? 0 : branchView ? index : ViewOffset(jumpResult, index);
                bool changed = _d.Offset != offset;
                _d.Offset = offset;
                if (itemCount > 0 && (row < 1 || row > itemCount))
                    ShowNotice(sender, System.FormattableString.Invariant($"Row {row:N0} is out of range · showing row {index + 1:N0} of {itemCount:N0}"));
                if (changed) InvalidatePaintedHits();
                sender.Invalidate();
            }, valid: text => int.TryParse(text.Trim(), out _));
        }

        private void TooltipClose(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Minimize to capsule";
            e.Text = "Nothing is deleted";
            e.Description = "Shrink to a compact summary on the canvas. Restore reopens the same view.";
        }

        private void TooltipMove(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Move Panel+";
            e.Text = "Drag this header";
            e.Description = "Drag to move the component. Clicks here are ordinary Grasshopper gestures, so Ctrl+C copies the component.";
        }

        private void TooltipDisplay(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = PanelOwner.CanvasDisplay == CanvasPanelDisplay.Full ? "Show half panel" : "Show full panel";
            e.Text = "Full ↔ Half";
            e.Description = "Toggle between your saved height and half of it. Wires stay connected.";
        }

        private void TooltipSelectAll(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Select all rows";
            e.Text = !PanelOwner.Locked && PanelOwner.Session.HasCurrentResult
                ? System.FormattableString.Invariant($"{PanelOwner.Session.Result.RowPositions.Count:N0} results")
                : PanelOwner.Locked ? "Disabled" : PanelOwner.Session.IsBusy ? "Recomputing…" : "Selection unavailable";
            e.Description = "Select every result, including offscreen rows. Shortcut while focused: Ctrl+A.";
        }

        private void TooltipStatistics(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Tree statistics";
            e.Text = !PanelOwner.Locked && PanelOwner.Session.HasCurrentResult
                ? CanvasStatistics.Summary(PanelOwner.Session.Result)
                : PanelOwner.Locked ? "Disabled" : PanelOwner.Session.IsBusy ? "Recomputing…" : "Statistics unavailable";
            e.Description = "Minimum, maximum, average, counts and every unique value with its frequency.";
        }

        private void TooltipSearchClear(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Clear search";
            e.Description = CanvasMenu.IsComparisonMode(PanelOwner.Session.Options.SearchMode)
                ? "Clear the filter to show values, then clear values to return to Text search. Undo restores each step."
                : "Remove the search filter. Undo restores it.";
        }

        private void TooltipBranchClear(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Show all branches";
            e.Description = "Remove the branch filter. Undo restores it.";
        }

        private void TooltipPreview(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Geometry preview";
            e.Text = CanvasMenu.PreviewLabel(PanelOwner.PreviewMode);
            e.Description = "Selection: draws selected rows only.\nResults: draws every result and highlights the selection.\nOff: no Panel+ preview.\nItem order labels: " +
                (PanelOwner.ShowItemOrder ? "on" : "off") + ".";
            if (!string.IsNullOrEmpty(PanelOwner.PreviewStatus)) e.Description += "\n\n" + PanelOwner.PreviewStatus +
                "\nOversized geometry, unsupported nested types, custom UserData or mesh limits can prevent part of the preview.";
        }

        private void TooltipOutput(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Output: Items / Values";
            e.Description = _layout.OutValues.Contains(point) ? (PanelOwner.CreateSetTree ? "Sends the displayed set values." : PanelOwner.DecimalPlaces >= 0 ? "Sends the values exactly as shown." : PanelOwner.RoundUpFactor != 0 ? "Sends the rounded values at full precision. Rows may be abbreviated." : "Sends full-precision values. Rows may be abbreviated.") : "Items sends items with shown numeric rounding (full precision in Automatic). Sets group all members by set index. Values sends the displayed property. Unavailable values are null.";
        }

        private void TooltipUnits(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Units and decimals";
            string precision = (PanelOwner.RoundUpFactor > 0 ? "↑" + PanelUnits.FormatFactor(PanelOwner.RoundUpFactor, "G4") : "") +
                (PanelOwner.DecimalPlaces < 0 ? "" : (PanelOwner.RoundUpFactor > 0 ? " · " : "") + PanelOwner.DecimalPlaces + "dp");
            string conversion = PanelOwner.UnitConversion == PanelUnitConversion.None ? "" : PanelUnits.Label(PanelOwner.UnitConversion);
            string setting = conversion.Length > 0 && precision.Length > 0 ? conversion + " · " + precision : conversion + precision;
            e.Text = setting.Length > 0 ? setting + " · changes numeric output" : "Changes numeric output";
            e.Description = "Convert mm ↔ m, mm² ↔ m², mm³ ↔ m³ or rad ↔ deg. Set explicit decimals (0–12), shared with numeric output. Or round output up to a factor (factor 5: 12.1 → 15).";
        }

        private void TooltipTree(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = _layout.Trim.Contains(point) ? "Trim tree" : _layout.Simplify.Contains(point) ? "Simplify tree" : _layout.Flatten.Contains(point) ? "Flatten tree" : _layout.Reverse.Contains(point) ? "Reverse tree" : "Create set";
            e.Text = (_layout.Trim.Contains(point) ? "Depth: " + PanelOwner.TrimDepth + " · " : "") + "Changes output · Tree tools → Units → Filters → Create set";
            e.Description = _layout.Trim.Contains(point) ? "Remove this many trailing path levels. Matching branches merge. 0 turns Trim off." :
                _layout.Simplify.Contains(point) ? "Toggle removal of path indices shared by all branches." :
                _layout.Flatten.Contains(point) ? "Toggle merging all branches into {0}." :
                _layout.Reverse.Contains(point) ? "Toggle reversing items within each output branch." :
                "Group equal displayed values per branch. Add Flatten for one set. Out Items sends every member in its set branch. Out Values sends each distinct value once.";
        }

        private void TooltipQuery(PointF point, GH_TooltipDisplayEventArgs e)
        {
            var options = PanelOwner.Session.Options;
            e.Title = _layout.Type.Contains(point) ? "Filter by type" : _layout.Sort.Contains(point) ? "Sort this view" : "Filter by branch";
            e.Text = _layout.Type.Contains(point) ? PanelOwner.Session.HasCurrentResult ? CanvasMenu.TypeHint(PanelOwner.Session.Result, options) : "Types" :
                _layout.Sort.Contains(point) ? options.SortMode.ToString() : options.BranchPath ?? "All branches";
            e.Description = _layout.Sort.Contains(point)
                ? "Asc/Desc sorts items within each branch. Sorting changes the view only. Output keeps its tree order."
                : "Filters change both the view and the output wire.";
            if (_layout.Type.Contains(point) && PanelOwner.Session.HasCurrentResult)
                e.Description = (PanelOwner.Session.Result.RowPositions.Count > 0 ? "Visible types: " : "Source types: ") +
                    CanvasMenu.TypeDetails(PanelOwner.Session.Result) + "\n\n" + e.Description;
            else if (_layout.Branch.Contains(point))
            {
                e.Description = "Show one branch in the view and output. In the open menu, type digits and ; to find a path, then press Enter.";
                if (PanelOwner.Session.Error != null) e.Description = PanelOwner.Session.Error + "\n\n" + e.Description;
            }
            string inputDiagnostic = PanelQueryPolicy.InputDiagnostic(PanelOwner.Session.Snapshot, options);
            if (_layout.Type.Contains(point) && inputDiagnostic.Length > 0) e.Description += "\n\n" + inputDiagnostic;
        }

        private void TooltipReset(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Reset all";
            e.Description = "Clears filters, sorting and selection. Turns off tree tools and units. Sets Out to Items and B + F to Results. Keeps Preview, item order, D|F and size.";
        }

        private void TooltipWarnings(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Data checks";
            e.Description = "Counts the input before filters. Replaces the current filters. Undo restores them.";
        }

        private void TooltipSearch(PointF point, GH_TooltipDisplayEventArgs e)
        {
            var options = PanelOwner.Session.Options;
            e.Title = CanvasMenu.SearchModeLabel(options.SearchMode);
            e.Text = options.DisplayFilterValue ? "Showing values · click to filter" : string.IsNullOrEmpty(options.Search) ? "Click to search · the mode chip chooses what to match" : options.Search;
            e.Description = options.SearchMode == PanelSearchMode.Text
                ? "Search displayed values, paths and types. > 800 or <= 25 compares numbers."
                : options.SearchMode == PanelSearchMode.Regex
                ? "Case-insensitive regular expression on displayed text, such as ^Wall or beam-[0-9]+."
                : options.SearchMode == PanelSearchMode.ListLength
                ? "Compare each branch's item count with >, >=, <, <=, = or != (a bare number means =)."
                : PanelProperties.IsProperty(options.SearchMode) ? CanvasMenu.PropertyHelp(options.SearchMode)
                : "Compare the shown number with >, >=, <, <=, = or != (a bare number means =). Rows without this value are excluded.";
            if (options.DisplayFilterValue)
                e.Description = "Shows each item's value. n/a means none. Click the search field to filter by it.";
            else if (!CanvasMenu.HasValueChoices(options.SearchMode)) e.Description += " Rows update as you type. Enter applies, Esc cancels.";
            if (PanelOwner.Session.Error != null) e.Description = PanelOwner.Session.Error + "\n\n" + e.Description;
        }

        private void TooltipJump(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Jump to a row";
            e.Text = DisplayListLengths ? "Branch row in the list-length view" : "Row in the current view";
            e.Description = (DisplayListLengths ? "Enter a branch row number, starting at 1." : "Enter a row number, starting at 1 for the first row of the current view.") +
                " Shortcut while focused: Ctrl/Cmd+G.";
        }

        private void TooltipClearFilters(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Clear filters";
            e.Text = "Undo restores them";
            e.Description = "Clears search, mode, type, path, invalid-only, sorting, selection and scroll. Tree tools, units and decimals stay.";
        }

        private void TooltipFilter(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Search mode";
            e.Text = CanvasMenu.SearchModeLabel(PanelOwner.Session.Options.SearchMode);
            e.Description = "Choose what the search matches: displayed text, a regular expression, numbers, geometry metrics, list length or an input property.";
        }

        private void TooltipResize(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Resize Panel+";
            e.Text = "Drag this corner";
            e.Description = "The panel size is saved with the Grasshopper definition.";
        }

        private void TooltipFooter(PointF point, GH_TooltipDisplayEventArgs e)
        {
            if (PanelOwner.Locked)
            {
                e.Title = "Disabled";
                e.Description = "Enable the component to recompute its input.";
                return;
            }
            if (PanelOwner.Session.IsBusy)
            {
                e.Title = "Recomputing…";
                e.Description = "Rows and statistics will appear when the current data is ready.";
                return;
            }
            if (PanelOwner.Session.Error != null)
            {
                e.Title = "Filter needs attention";
                e.Text = CanvasMenu.SearchModeLabel(PanelOwner.Session.Options.SearchMode);
                e.Description = PanelOwner.Session.Error + "\n\nEdit the filter or use Clear filters (right-click menu).";
                return;
            }
            e.Title = "Row selection and copy";
            e.Text = _copyPending || DateTime.UtcNow < _copiedUntil ? _actionStatus : System.FormattableString.Invariant($"{_selection.Count:N0} selected");
            if (PanelOwner.Session.HasCurrentResult) e.Text += " · " + CanvasStatistics.Summary(PanelOwner.Session.Result);
            e.Description = "Click to select; Ctrl/Cmd toggles, Shift extends, Alt/Option picks the same index in every branch. Right-click a row to copy, frame or view the full value, or a branch heading for branch actions. Double-click clears selection.\n\n" + KeyboardHint;
        }

        private GH_ObjectResponse? ClickRestore(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Clicks > 1 && DateTime.UtcNow < _rowActionDismissedUntil) return GH_ObjectResponse.Handled;
            SuppressTrailingDoubleClick();
            PanelOwner.SetPanelMinimized(false);
            PerformLayout();
            CanvasDoubleClickGuard.Start(sender, e);
            sender.Invalidate();
            return GH_ObjectResponse.Handled;
        }

        private void TooltipRestore(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Restore panel";
            e.Text = CapsuleSummary();
            e.Description = "Click the window icon to reopen the " + PanelOwner.CanvasDisplay + " panel exactly as you left it.";
        }
    }
}
#pragma warning restore CA1416
