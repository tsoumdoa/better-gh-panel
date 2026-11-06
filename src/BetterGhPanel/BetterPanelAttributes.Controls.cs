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

        private sealed class ControlEntry
        {
            internal readonly string Id;
            internal readonly Func<PanelLayout, RectangleF> Rect;
            internal readonly int ClickOrder, HoverOrder, TooltipOrder, CursorOrder;
            internal readonly Func<GH_Canvas, GH_CanvasMouseEvent, GH_ObjectResponse>? OnClick;
            internal readonly Action<PointF, GH_TooltipDisplayEventArgs>? Tooltip;
            internal readonly Func<ControlPath, bool> Visible;
            internal readonly Func<PointF, bool>? Hit, TooltipHit;
            internal readonly Cursor? Cursor;
            internal readonly Func<IEnumerable<RectangleF>>? HoverRects;

            internal ControlEntry(string id, Func<PanelLayout, RectangleF> rect,
                int clickOrder, int hoverOrder, int tooltipOrder,
                Func<GH_Canvas, GH_CanvasMouseEvent, GH_ObjectResponse>? onClick,
                Action<PointF, GH_TooltipDisplayEventArgs>? tooltip,
                Func<ControlPath, bool>? visible = null, Func<PointF, bool>? hit = null,
                Func<PointF, bool>? tooltipHit = null, Cursor? cursor = null, int cursorOrder = -1, Func<IEnumerable<RectangleF>>? hoverRects = null)
            {
                Id = id; Rect = rect;
                ClickOrder = clickOrder; HoverOrder = hoverOrder; TooltipOrder = tooltipOrder; CursorOrder = cursorOrder;
                OnClick = onClick; Tooltip = tooltip; Visible = visible ?? (_ => true);
                Hit = hit; TooltipHit = tooltipHit; Cursor = cursor; HoverRects = hoverRects;
            }

            internal int Order(ControlPath path) => path == ControlPath.Click ? ClickOrder :
                path == ControlPath.Hover ? HoverOrder : path == ControlPath.Tooltip ? TooltipOrder : CursorOrder;
        }

        private ControlEntry[]? _controlHits;
        private ControlEntry[] ControlHits => _controlHits ??= CreateControlHits();

        // Path-specific ranks preserve overlap winners and the existing insertion
        // points around split panes, menus, details, scrollbars and warning hits.
        private ControlEntry[] CreateControlHits() => new[]
        {
            new ControlEntry("Close", layout => layout.Close, 0, 0, 0, ClickClose, TooltipClose),
            new ControlEntry("Display", layout => layout.Display, 1, 1, 2, ClickDisplay, TooltipDisplay),
            new ControlEntry("Reset", layout => layout.Reset, 2, 4, 20, ClickReset, TooltipReset),
            new ControlEntry("Resize", layout => layout.Resize, 3, -1, 24, ClickResize, TooltipResize, cursor: Cursors.SizeNWSE, cursorOrder: 0),
            new ControlEntry("OutItems", layout => layout.OutItems, 4, 21, 9, ClickOutput, TooltipOutput, visible: path => path == ControlPath.Tooltip || PanelOwner.CanOutputValues),
            new ControlEntry("OutValues", layout => layout.OutValues, 5, 22, 10, ClickOutput, TooltipOutput, visible: path => path == ControlPath.Tooltip || PanelOwner.CanOutputValues),
            new ControlEntry("Trim", layout => layout.Trim, 6, 15, 12, ClickTrim, TooltipTree),
            new ControlEntry("Simplify", layout => layout.Simplify, 7, 16, 13, ClickTree, TooltipTree),
            new ControlEntry("Flatten", layout => layout.Flatten, 8, 17, 14, ClickTree, TooltipTree),
            new ControlEntry("Reverse", layout => layout.Reverse, 9, 18, 15, ClickTree, TooltipTree),
            new ControlEntry("CreateSet", layout => layout.CreateSet, 10, 19, 16, ClickTree, TooltipTree),
            new ControlEntry("SelectAll", layout => layout.SelectAll, 11, 13, 3, ClickSelectAll, TooltipSelectAll),
            new ControlEntry("Preview", layout => layout.Preview, 12, 5, 7, ClickPreview, TooltipPreview),
            new ControlEntry("Statistics", layout => layout.Statistics, 13, 14, 4, ClickStatistics, TooltipStatistics, tooltipHit: point => _layout.Statistics.Contains(point) || _layout.Footer.Contains(point) && point.Y < _layout.Units.Y && !_layout.OutLabel.Contains(point) && !_layout.OutItems.Contains(point) && !_layout.OutValues.Contains(point)),
            new ControlEntry("SearchClear", layout => layout.SearchClear, 14, 6, 5, ClickSearchClear, TooltipSearchClear, visible: path => SearchClearVisible),
            new ControlEntry("BranchClear", layout => layout.BranchClear, 15, 9, 6, ClickBranchClear, TooltipBranchClear, visible: path => BranchClearVisible),
            new ControlEntry("Filter", layout => layout.Filter, 16, 7, -1, ClickFilter, null),
            new ControlEntry("Search", layout => layout.Search, 17, 8, 22, ClickSearch, TooltipSearch),
            new ControlEntry("Units", layout => layout.Units, 18, 20, 11, ClickUnits, TooltipUnits),
            new ControlEntry("Type", layout => layout.Type, 19, 10, 17, ClickType, TooltipQuery),
            new ControlEntry("Sort", layout => layout.Sort, 20, 11, 18, ClickSort, TooltipQuery),
            new ControlEntry("Branch", layout => layout.Branch, 21, 12, 19, ClickBranch, TooltipQuery),
            new ControlEntry("Jump", layout => layout.Jump, 22, 3, 23, ClickJump, TooltipJump),
            new ControlEntry("Move", layout => layout.Move, 23, -1, 1, ClickMove, TooltipMove, cursor: Cursors.SizeAll, cursorOrder: 1),
            new ControlEntry("OutLabel", layout => layout.OutLabel, -1, -1, 8, null, TooltipOutput),
            new ControlEntry("Footer", layout => layout.Footer, -1, -1, 25, null, TooltipFooter),
            new ControlEntry("Restore", layout => layout.Restore, 100, 100, 100, ClickRestore, TooltipRestore),
            new ControlEntry("Warnings", layout => RectangleF.Empty, 24, 2, 21, ClickWarnings, TooltipWarnings, visible: path => path != ControlPath.Click || RowsAreCurrent(PanelOwner.Session.Result), hit: point => _warningHits.Any(hit => hit.Bounds.Contains(point)), hoverRects: () => _warningHits.Select(hit => hit.Bounds))
        };

        private ControlEntry? ControlAt(ControlPath path, PointF point, int first = 0, int last = int.MaxValue)
        {
            ControlEntry? best = null;
            int order = int.MaxValue;
            foreach (var entry in ControlHits)
            {
                int candidate = entry.Order(path);
                if (candidate < first || candidate > last || candidate >= order || !entry.Visible(path)) continue;
                var hit = path == ControlPath.Tooltip ? entry.TooltipHit ?? entry.Hit : entry.Hit;
                if (!(hit?.Invoke(point) ?? entry.Rect(_layout).Contains(point))) continue;
                best = entry; order = candidate;
            }
            return best;
        }

        private bool ControlTooltip(PointF point, GH_TooltipDisplayEventArgs e, int first, int last)
        {
            var entry = ControlAt(ControlPath.Tooltip, point, first, last);
            if (entry == null) return false;
            entry.Tooltip!(point, e);
            return true;
        }

        private GH_ObjectResponse ClickMove(GH_Canvas sender, GH_CanvasMouseEvent e) => base.RespondToMouseDown(sender, e);

        private GH_ObjectResponse ClickWarnings(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            TryWarningClick(e.CanvasLocation, sender);
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickClose(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            SuppressTrailingDoubleClick();
            PanelOwner.SetPanelMinimized(true);
            PerformLayout();
            CanvasDoubleClickGuard.Start(sender, e);
            sender.Invalidate();
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickDisplay(GH_Canvas sender, GH_CanvasMouseEvent e)
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

        private GH_ObjectResponse ClickReset(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            bool recordedUndo = PanelOwner.ResetAll();
            ShowNotice(sender, recordedUndo ? "Reset all · Undo restores the previous settings" : "Reset all");
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickResize(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            var point = e.CanvasLocation;
            PanelOwner.RecordUndoEvent("Resize Panel+");
            _resizing = true; _dragStart = point; _startWidth = _layout.Panel.Width; _startHeight = _layout.Panel.Height;
            return GH_ObjectResponse.Capture;
        }

        private GH_ObjectResponse ClickOutput(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            var point = e.CanvasLocation;
            PanelOwner.SetOutputValues(_layout.OutValues.Contains(point) ? PanelOutputValues.Values : PanelOutputValues.Items);
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickTrim(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            CanvasTextInput.Show(sender, PanelOwner, _layout.Trim, PanelOwner.TrimDepth.ToString(), text =>
            {
                if (int.TryParse(text.Trim(), out var depth) && depth >= 0)
                    PanelOwner.SetTreeOperations(depth, PanelOwner.FlattenTree, PanelOwner.ReverseTree, PanelOwner.SimplifyTree, PanelOwner.CreateSetTree);
                else ShowNotice(sender, "Trim depth must be a whole number, 0 or more (0 turns Trim off)");
            }, valid: text => int.TryParse(text.Trim(), out int depth) && depth >= 0);
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickTree(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            var point = e.CanvasLocation;
            PanelOwner.SetTreeOperations(PanelOwner.TrimDepth,
                _layout.Flatten.Contains(point) ? !PanelOwner.FlattenTree : PanelOwner.FlattenTree,
                _layout.Reverse.Contains(point) ? !PanelOwner.ReverseTree : PanelOwner.ReverseTree,
                _layout.Simplify.Contains(point) ? !PanelOwner.SimplifyTree : PanelOwner.SimplifyTree,
                _layout.CreateSet.Contains(point) ? !PanelOwner.CreateSetTree : PanelOwner.CreateSetTree);
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickSelectAll(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            var result = PanelOwner.Session.Result;
            if (RowsAreCurrent(result)) SelectAllRows(sender, result);
            return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickPreview(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.Preview, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickStatistics(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.Statistics, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickSearchClear(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            ClearSearch(sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickBranchClear(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            SetBranchFilter(null); sender.Invalidate(); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickFilter(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.SearchMode, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickSearch(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            EditSearch(sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickUnits(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.Units, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickType(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.Type, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickSort(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.Sort, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickBranch(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            OpenMenu(CanvasMenuKind.Branch, sender); return GH_ObjectResponse.Handled;
        }

        private GH_ObjectResponse ClickJump(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            var jumpResult = PanelOwner.Session.Result;
            bool branchView = DisplayListLengths;
            int firstItem = branchView ? _d.Offset : ViewItemAtOrAfter(jumpResult, _d.Offset);
            CanvasTextInput.Show(sender, PanelOwner, _layout.Jump, (Math.Min(firstItem, Math.Max(0, (branchView ? jumpResult.BranchPaths.Count : jumpResult.RowPositions.Count) - 1)) + 1).ToString(), text =>
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
            return GH_ObjectResponse.Handled;
        }

        private void TooltipClose(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Minimize to capsule";
            e.Text = "Nothing is deleted";
            e.Description = "Shrink to a compact summary on the canvas. Restore reopens the same view.";
            return;
        }

        private void TooltipMove(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Move Panel+";
            e.Text = "Drag this header";
            e.Description = "Drag to move the component. Clicks here are ordinary Grasshopper gestures, so Ctrl+C copies the component.";
            return;
        }

        private void TooltipDisplay(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = PanelOwner.CanvasDisplay == CanvasPanelDisplay.Full ? "Show half panel" : "Show full panel";
            e.Text = "Full ↔ Half";
            e.Description = "Toggle between your saved height and half of it. Wires stay connected.";
            return;
        }

        private void TooltipSelectAll(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Select all rows";
            e.Text = !PanelOwner.Locked && PanelOwner.Session.HasCurrentResult
                ? System.FormattableString.Invariant($"{PanelOwner.Session.Result.RowPositions.Count:N0} results")
                : PanelOwner.Locked ? "Disabled" : PanelOwner.Session.IsBusy ? "Recomputing…" : "Selection unavailable";
            e.Description = "Select every result, including offscreen rows. Shortcut while focused: Ctrl+A.";
            return;
        }

        private void TooltipStatistics(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Tree statistics";
            e.Text = !PanelOwner.Locked && PanelOwner.Session.HasCurrentResult
                ? CanvasStatistics.Summary(PanelOwner.Session.Result)
                : PanelOwner.Locked ? "Disabled" : PanelOwner.Session.IsBusy ? "Recomputing…" : "Statistics unavailable";
            e.Description = "Minimum, maximum, average, counts and every unique value with its frequency.";
            return;
        }

        private void TooltipSearchClear(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Clear search";
            e.Description = CanvasMenu.IsComparisonMode(PanelOwner.Session.Options.SearchMode)
                ? "Clear the filter to show values, then clear values to return to Text search. Undo restores each step."
                : "Remove the search filter. Undo restores it.";
            return;
        }

        private void TooltipBranchClear(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Show all branches";
            e.Description = "Remove the branch filter. Undo restores it.";
            return;
        }

        private void TooltipPreview(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Geometry preview";
            e.Text = CanvasMenu.PreviewLabel(PanelOwner.PreviewMode);
            e.Description = "Selection: draw selected rows only. Results: draw every result, highlighting selected rows. Off: no Panel+ preview. Item order labels: " +
                (PanelOwner.ShowItemOrder ? "on" : "off") + ".";
            if (!string.IsNullOrEmpty(PanelOwner.PreviewStatus)) e.Description += " " + PanelOwner.PreviewStatus +
                " Oversized geometry, unsupported nested types, custom UserData, or mesh limits can prevent part of the preview.";
        }

        private void TooltipOutput(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Output: Items / Values";
            e.Description = _layout.OutValues.Contains(point) ? (PanelOwner.CreateSetTree ? "Sends the displayed set values." : PanelOwner.DecimalPlaces >= 0 ? "Sends the values exactly as shown." : PanelOwner.RoundUpFactor != 0 ? "Sends the rounded values at full precision; rows may be abbreviated." : "Sends full-precision values; rows may be abbreviated.") : "Items sends the items; numbers use the shown rounding (full precision in Automatic). Values sends the displayed property. Sets group all member items by set index; unavailable values are null.";
        }

        private void TooltipUnits(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Units and decimals";
            e.Text = "Changes numeric output";
            e.Description = "Convert mm ↔ m, mm² ↔ m², mm³ ↔ m³ or rad ↔ deg; set explicit decimals (0–12), shared with numeric output; or round output up to a factor (factor 5: 12.1 → 15).";
        }

        private void TooltipTree(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = _layout.Trim.Contains(point) ? "Trim tree" : _layout.Simplify.Contains(point) ? "Simplify tree" : _layout.Flatten.Contains(point) ? "Flatten tree" : _layout.Reverse.Contains(point) ? "Reverse tree" : "Create set";
            e.Text = (_layout.Trim.Contains(point) ? "Depth: " + PanelOwner.TrimDepth + " · " : "") + "Changes output · Tree tools → Units → Filters → Create set";
            e.Description = _layout.Trim.Contains(point) ? "Remove this many trailing path levels; matching branches merge. 0 turns Trim off." :
                _layout.Simplify.Contains(point) ? "Toggle removal of path indices shared by all branches." :
                _layout.Flatten.Contains(point) ? "Toggle merging all branches into {0}." :
                _layout.Reverse.Contains(point) ? "Toggle reversing items within each output branch." :
                "Group equal displayed values per branch; add Flatten for one set. Out Items sends every member in its set branch; Out Values sends each distinct value once.";
        }

        private void TooltipQuery(PointF point, GH_TooltipDisplayEventArgs e)
        {
            var options = PanelOwner.Session.Options;
            e.Title = _layout.Type.Contains(point) ? "Filter by type" : _layout.Sort.Contains(point) ? "Sort this view" : "Filter by branch";
            e.Text = _layout.Type.Contains(point) ? PanelOwner.Session.HasCurrentResult ? CanvasMenu.TypeHint(PanelOwner.Session.Result, options) : "Types" :
                _layout.Sort.Contains(point) ? options.SortMode.ToString() : options.BranchPath ?? "All branches";
            e.Description = _layout.Sort.Contains(point)
                ? "Asc/Desc sorts items within each branch. Sorting changes the view only; output keeps its tree order."
                : "Filters change both the view and the output wire.";
            if (_layout.Type.Contains(point) && PanelOwner.Session.HasCurrentResult)
                e.Description = (PanelOwner.Session.Result.RowPositions.Count > 0 ? "Visible types: " : "Source types: ") +
                    CanvasMenu.TypeDetails(PanelOwner.Session.Result) + "\n\n" + e.Description;
            else if (_layout.Branch.Contains(point))
            {
                e.Description = "Show one branch. While the menu is open, type digits and ; to find a path, then press Enter. " + e.Description;
                if (PanelOwner.Session.Error != null) e.Description = PanelOwner.Session.Error + "\n\n" + e.Description;
            }
            string inputDiagnostic = PanelQueryPolicy.InputDiagnostic(PanelOwner.Session.Snapshot, options);
            if (_layout.Type.Contains(point) && inputDiagnostic.Length > 0) e.Description += "\n\n" + inputDiagnostic;
        }

        private void TooltipReset(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Reset all";
            e.Description = "Clears filters, sorting and selection, turns off tree tools and units, and sets Out to Items and B + F to Results. Keeps Preview, item order, D|F and size. Undo restores the settings.";
        }

        private void TooltipWarnings(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Data checks";
            e.Description = "Counts the input before filters. Replaces the current filters; Undo restores them.";
        }

        private void TooltipSearch(PointF point, GH_TooltipDisplayEventArgs e)
        {
            var options = PanelOwner.Session.Options;
            e.Title = CanvasMenu.SearchModeLabel(options.SearchMode);
            e.Text = options.DisplayFilterValue ? "Showing values · click to filter" : string.IsNullOrEmpty(options.Search) ? "Click to search · arrow chooses the mode" : options.Search;
            e.Description = options.SearchMode == PanelSearchMode.Text
                ? "Search displayed values, paths and types; > 800 or <= 25 compares numbers."
                : options.SearchMode == PanelSearchMode.Regex
                ? "Case-insensitive regular expression on displayed text, e.g. ^Wall or beam-[0-9]+."
                : options.SearchMode == PanelSearchMode.ListLength
                ? "Compare each branch's item count with >, >=, <, <=, = or != (a bare number means =)."
                : PanelProperties.IsProperty(options.SearchMode) ? CanvasMenu.PropertyHelp(options.SearchMode)
                : "Compare with >, >=, <, <=, = or != (a bare number means =) against the shown number. Rows without this value are excluded.";
            if (options.DisplayFilterValue)
                e.Description = "Shows each item's value; n/a means none. Click the search field to filter by it.";
            else if (!CanvasMenu.HasValueChoices(options.SearchMode)) e.Description += " Rows update as you type; Enter applies, Esc cancels.";
            if (PanelOwner.Session.Error != null) e.Description = PanelOwner.Session.Error + "\n\n" + e.Description;
        }

        private void TooltipJump(PointF point, GH_TooltipDisplayEventArgs e)
        {
            e.Title = "Jump to a row";
            e.Text = DisplayListLengths ? "Branch row in the list-length view" : "Row in the current view";
            e.Description = DisplayListLengths ? "Enter a branch row number, starting at 1." : "Enter a row number, starting at 1 for the first row of the current view.";
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
            e.Description = FooterStatus(PanelOwner.Session.Result, _singleLineFooter) + "\n\nClick to select; Ctrl/Cmd toggles, Shift extends, Alt/Option picks the same index in every branch. Right-click to copy or frame.\n\n" + KeyboardHint;
        }

        private GH_ObjectResponse ClickRestore(GH_Canvas sender, GH_CanvasMouseEvent e)
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
