using System;
using System.Drawing;
using BetterGhPanel.Core;

namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private sealed class PanelLayout
        {
            internal RectangleF Panel { get; }
            internal RectangleF Search { get; }
            internal RectangleF Filter { get; }
            internal RectangleF Type { get; }
            internal RectangleF Sort { get; }
            internal RectangleF Branch { get; }
            internal RectangleF Reset { get; }
            internal RectangleF Resize { get; }
            internal RectangleF Footer { get; }
            internal RectangleF Trim { get; }
            internal RectangleF Simplify { get; }
            internal RectangleF Flatten { get; }
            internal RectangleF Reverse { get; }
            internal RectangleF CreateSet { get; }
            internal RectangleF Units { get; }
            internal RectangleF OutLabel { get; }
            internal RectangleF OutItems { get; }
            internal RectangleF OutValues { get; }
            internal RectangleF Display { get; }
            internal RectangleF Preview { get; }
            internal RectangleF Move { get; }
            internal RectangleF Statistics { get; }
            internal RectangleF SelectAll { get; }
            internal RectangleF Close { get; }
            internal RectangleF Restore { get; }
            internal RectangleF SearchClear { get; }
            internal RectangleF BranchClear { get; }
            internal RectangleF Split { get; }

            internal PanelLayout(
                RectangleF panel = default,
                RectangleF search = default,
                RectangleF filter = default,
                RectangleF type = default,
                RectangleF sort = default,
                RectangleF branch = default,
                RectangleF reset = default,
                RectangleF resize = default,
                RectangleF footer = default,
                RectangleF trim = default,
                RectangleF simplify = default,
                RectangleF flatten = default,
                RectangleF reverse = default,
                RectangleF createSet = default,
                RectangleF units = default,
                RectangleF outLabel = default,
                RectangleF outItems = default,
                RectangleF outValues = default,
                RectangleF display = default,
                RectangleF preview = default,
                RectangleF move = default,
                RectangleF statistics = default,
                RectangleF selectAll = default,
                RectangleF close = default,
                RectangleF restore = default,
                RectangleF searchClear = default,
                RectangleF branchClear = default,
                RectangleF split = default)
            {
                Panel = panel;
                Search = search;
                Filter = filter;
                Type = type;
                Sort = sort;
                Branch = branch;
                Reset = reset;
                Resize = resize;
                Footer = footer;
                Trim = trim;
                Simplify = simplify;
                Flatten = flatten;
                Reverse = reverse;
                CreateSet = createSet;
                Units = units;
                OutLabel = outLabel;
                OutItems = outItems;
                OutValues = outValues;
                Display = display;
                Preview = preview;
                Move = move;
                Statistics = statistics;
                SelectAll = selectAll;
                Close = close;
                Restore = restore;
                SearchClear = searchClear;
                BranchClear = branchClear;
                Split = split;
            }
        }

        private PanelLayout _layout = new PanelLayout();
        // Measured at paint: the search mode chip and the Units label widths.
        private float _searchChipWidth = 38, _unitsWidth = 48, _sortChipWidth = 43;

        // Units may widen until the two-line footer status keeps room for a
        // "D: 1 filter ×" chip (59 units; 300-wide panels keep the 48 minimum).
        private float UnitsWidth(float panelWidth) => Math.Max(48, Math.Min(_unitsWidth, panelWidth - 252));

        // Computes the immutable fixed-control layout and, in the original
        // calculation order, updates the list rectangle, _warningsHeight and
        // _singleLineFooter. This helper deliberately includes those layout side effects.
        private PanelLayout ComputePanelLayout()
        {
            if (PanelOwner.PanelMinimized)
            {
                var capsule = new RectangleF(Pivot.X, Pivot.Y, CanvasPanelSize.CapsuleWidth, CanvasPanelSize.CapsuleHeightForInputs(PanelOwner.Params.Input.Count));
                _list = _track = _trackHit = _tableHeader = _horizontalTrack = RectangleF.Empty;
                return new PanelLayout(panel: capsule,
                    restore: new RectangleF(capsule.Right - 36, capsule.Y + (capsule.Height - 22) / 2, 22, 22));
            }
            RectangleF panel;
            RectangleF search;
            RectangleF filter;
            RectangleF type;
            RectangleF sort;
            RectangleF branch;
            RectangleF reset;
            RectangleF resize;
            RectangleF footer;
            RectangleF trim;
            RectangleF simplify;
            RectangleF flatten;
            RectangleF reverse;
            RectangleF createSet;
            RectangleF units;
            RectangleF outLabel;
            RectangleF outItems;
            RectangleF outValues;
            RectangleF display;
            RectangleF preview;
            RectangleF move;
            RectangleF statistics;
            RectangleF selectAll;
            RectangleF close;
            RectangleF restore;
            RectangleF searchClear;
            RectangleF branchClear;
            RectangleF split;
            restore = RectangleF.Empty;
            panel = new RectangleF(Pivot.X,
                Pivot.Y + (PanelOwner.EffectiveCanvasHeight - PanelOwner.DisplayHeight) / 2,
                PanelOwner.DisplayWidth, PanelOwner.DisplayHeight);
            float x = panel.X + 4;
            // Narrow panels give up type and path width before the search field
            // drops below a readable query such as "Length > 800".
            float squeeze = Math.Max(0, Math.Min(34, 120 - (panel.Width - 212)));
            float typeWidth = 76 - Math.Min(20, squeeze), branchWidth = 64 - Math.Max(0, squeeze - 20);
            search = new RectangleF(x + 2, panel.Y + 25, panel.Width - 212 + squeeze - (_sortChipWidth - 43), 21);
            // The mode chip leads the field beyond the wire-grip strip and leaves
            // room for a short query; long property labels are ellipsized.
            filter = new RectangleF(panel.X + 14, search.Y,
                Math.Min(_searchChipWidth, Math.Max(56, Math.Min(120, search.Right - panel.X - 64))), search.Height);
            searchClear = new RectangleF(search.Right - 18, search.Y, 16, search.Height);
            type = new RectangleF(search.Right + 3, search.Y, typeWidth, 21);
            sort = new RectangleF(type.Right + 3, search.Y, _sortChipWidth, 21);
            branch = new RectangleF(sort.Right + 3, search.Y, branchWidth, 21);
            branchClear = new RectangleF(branch.Right - 30, branch.Y, 16, branch.Height);
            close = new RectangleF(panel.Right - 34, panel.Y + 2, 20, 18);
            display = new RectangleF(close.Left - 22, close.Y, 20, 18);
            // Wide enough for "Preview: Selection" at the default font without trimming.
            preview = new RectangleF(display.Left - 122, close.Y, 118, 18);
            split = PanelOwner.PanelSplit || PanelOwner.TableInputPresent ? new RectangleF(preview.Left - 42, preview.Y, 38, 18) : RectangleF.Empty;
            move = new RectangleF(panel.X + 14, panel.Y + 1, (split.IsEmpty ? preview.Left : split.Left) - panel.X - 18, 23);
            // Reserve the corner for resizing rather than overlaying footer controls.
            // Wide panels fit status, selection, statistics, tools and Units on one line.
            _singleLineFooter &= panel.Width >= 500;
            float footerHeight = FormulaFooterHeight(_singleLineFooter ? 20 : 36);
            // The footer starts at the rows' inset so its clickable status stays clear of the grips.
            footer = new RectangleF(panel.X + 14, panel.Bottom - footerHeight - 2, panel.Width - 42, footerHeight);
            // Keep the existing footer controls fixed when the output switch appears.
            // Active Units widens leftward, shifting Reset and the tree tools with it.
            float unitsWidth = UnitsWidth(panel.Width);
            units = new RectangleF(footer.Right - unitsWidth, footer.Bottom - 20, unitsWidth, 20);
            // Reset all sits beside Units, which it also resets, 8 units clear of the five tree tools.
            reset = new RectangleF(units.Left - 23, units.Y, 20, units.Height);
            trim = new RectangleF(reset.Left - 126, units.Y, 22, units.Height);
            simplify = new RectangleF(trim.Right + 2, trim.Y, 22, 20);
            flatten = new RectangleF(simplify.Right + 2, trim.Y, 22, 20);
            reverse = new RectangleF(flatten.Right + 2, trim.Y, 22, 20);
            createSet = new RectangleF(reverse.Right + 2, trim.Y, 22, 20);
            if (_singleLineFooter)
            {
                statistics = new RectangleF(trim.Left - 54, units.Y + 2, 46, 16);
                selectAll = new RectangleF(statistics.Left - 62, statistics.Y, 58, 16);
            }
            else
            {
                statistics = new RectangleF(footer.Right - 46, footer.Y, 46, 16);
                selectAll = new RectangleF(statistics.Left - 62, footer.Y, 58, 16);
            }
            outLabel = outItems = outValues = RectangleF.Empty;
            if (PanelOwner.CanOutputValues)
            {
                float outputLeft = selectAll.Left - 4 - OutputControlWidth;
                float outputTop = selectAll.Y;
                outLabel = new RectangleF(outputLeft, outputTop, 20, 16);
                outItems = new RectangleF(outLabel.Right, outputTop, 34, 16);
                outValues = new RectangleF(outItems.Right, outputTop, 38, 16);
            }
            _warningsHeight = WarningStripHeight(footerHeight, panel);
            // Keep interactive content beyond native grip picking's 12-unit
            // radius on both edges, including rows and the scroll track.
            _list = new RectangleF(panel.X + 14, panel.Y + 52 + _warningsHeight, panel.Width - 33, Math.Max(RowHeight, panel.Height - 56 - footerHeight - _warningsHeight - (_formulaDraft != null ? 24 : 0)));
            _track = new RectangleF(panel.Right - 18, _list.Y, 4, _list.Height);
            _trackHit = new RectangleF(panel.Right - 21, _list.Y, 8, _list.Height);
            LayoutTable();
            resize = new RectangleF(panel.Right - 24, panel.Bottom - 24, 24, 24);
            return new PanelLayout(panel, search, filter, type, sort, branch, reset, resize, footer, trim, simplify, flatten, reverse, createSet, units, outLabel, outItems, outValues, display, preview, move, statistics, selectAll, close, restore, searchClear, branchClear, split);
        }
    }
}
