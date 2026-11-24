using System;
using System.Drawing;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper.GUI.Canvas;

#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private static string SearchChip(PanelQueryOptions options) =>
            (options.SearchPaired ? "F · " : "") + CanvasMenu.SearchModeChip(options.SearchMode);

        private bool CanSearchPaired => PanelOwner.PairedData != null;

        private RectangleF SearchTargetButton(RectangleF row, bool paired)
        {
            float width = Math.Max(0, (row.Width - 59) / 2);
            return new RectangleF(row.X + 53 + (paired ? width + 2 : 0), row.Y + 2, width, row.Height - 4);
        }

        private void DrawSearchTarget(Graphics g, RectangleF row, Font font, StringFormat trim, bool searchPaired)
        {
            Text(g, "Search in", font, Muted, new RectangleF(row.X + 6, row.Y, 45, row.Height), trim);
            for (int i = 0; i <= 1; i++)
            {
                bool paired = i == 1, enabled = !paired || CanSearchPaired;
                var button = SearchTargetButton(row, paired);
                if (enabled && paired == searchPaired) Fill(g, PanelTheme.Rgb(226, 236, 226), button);
                var title = PanelOwner.HeaderTitleForInput(PanelOwner.Params.Input[i]);
                string port = paired ? "F" : "D";
                Text(g, title == port ? port : port + " · " + title, font, !enabled ? PanelTheme.Disabled : paired == searchPaired ? Accent : Ink,
                    new RectangleF(button.X + 4, button.Y, Math.Max(0, button.Width - 7), button.Height), trim);
            }
        }

        private void ChooseSearchTarget(PointF point, GH_Canvas canvas)
        {
            var row = new RectangleF(_menu.Bounds.X + 1, _menu.Bounds.Y - _menu.Offset * 20, _menu.Bounds.Width - 2, 20);
            bool paired = SearchTargetButton(row, paired: true).Contains(point);
            if (paired ? !CanSearchPaired : !SearchTargetButton(row, paired: false).Contains(point)) return;
            var before = PanelOwner.Session.Options;
            if (before.SearchPaired == paired) return;
            var options = before.Clone();
            options.SearchPaired = paired;
            if (!PanelOwner.SearchSnapshot(paired).SupportsSearchMode(options.SearchMode))
            {
                options.SearchMode = PanelSearchMode.Text;
                options.DisplayFilterValue = false;
            }
            _menu.Close();
            PanelOwner.RecordQueryUndo("Panel+ search input", before, options);
            _offset = 0;
            PanelOwner.UpdateSearchTarget(options);
            PanelOwner.OnPingDocument()?.Modified();
            canvas.Invalidate();
        }
    }
}
