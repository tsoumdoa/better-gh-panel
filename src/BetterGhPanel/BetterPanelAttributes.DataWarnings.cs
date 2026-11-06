using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper.GUI.Canvas;

#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private float _warningsHeight;
        private readonly List<(RectangleF Bounds, int Kind)> _warningHits = new List<(RectangleF, int)>();

        private float WarningStripHeight(float footerHeight) => WarningStripHeight(footerHeight, _layout.Panel);

        private float WarningStripHeight(float footerHeight, RectangleF panel) => PanelOwner.Session.HasCurrentResult &&
            PanelOwner.Session.Result.Warnings.Any && panel.Height - 56 - footerHeight - (PanelOwner.PanelSplit ? 14 : 0) >= 34 ? 17 : 0;

        private void RenderWarnings(Graphics graphics, PanelQueryResult result, Font small)
        {
            _warningHits.Clear();
            if (_warningsHeight == 0 || !ResultIsCurrent(result)) return;
            var warnings = result.Warnings;
            using var textFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
            textFormat.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            textFormat.LineAlignment = StringAlignment.Center;
            textFormat.Trimming = StringTrimming.EllipsisCharacter;
            float separatorWidth = graphics.MeasureString(" · ", small, int.MaxValue, textFormat).Width;
            float x = _d.List.X + 2, y = _layout.Panel.Y + 48;
            float right = PanelOwner.PanelSplit ? _layout.Panel.X + _layout.Panel.Width / 2 - 19 : _d.List.Right;
            bool hasSegment = false;
            void Segment(string text, int kind)
            {
                if (hasSegment)
                {
                    Text(graphics, " · ", small, Muted, new RectangleF(x, y, separatorWidth, 16), textFormat);
                    x += separatorWidth;
                }
                float width = System.Math.Min(graphics.MeasureString(text, small, int.MaxValue, textFormat).Width, right - x);
                if (width <= 0) return;
                hasSegment = true;
                var bounds = new RectangleF(x, y, width, 16);
                using var link = new Font(small, bounds == _hoverBounds ? FontStyle.Underline : FontStyle.Regular);
                Text(graphics, text, link, PanelTheme.Warning, bounds, textFormat);
                if (TargetPainted(graphics, bounds)) _warningHits.Add((bounds, kind));
                x += width;
            }
            float prefixWidth = graphics.MeasureString("Check: ", small, int.MaxValue, textFormat).Width;
            Text(graphics, "Check:", small, Muted, new RectangleF(x, y, prefixWidth, 16), textFormat);
            x += prefixWidth;
            if (warnings.NullCount > 0) Segment(warnings.NullCount.ToString(CultureInfo.InvariantCulture) + (warnings.NullCount == 1 ? " null" : " nulls"), 0);
            if (warnings.InvalidCount > 0) Segment(warnings.InvalidCount.ToString(CultureInfo.InvariantCulture) + " invalid", 1);
            if (warnings.UnverifiedCount > 0) Segment(warnings.UnverifiedCount.ToString(CultureInfo.InvariantCulture) + " unverified", 2);
            if (warnings.OutlierBranches > 0) Segment(warnings.OutlierBranches.ToString(CultureInfo.InvariantCulture) +
                (warnings.OutlierBranches == 1 ? " branch ≠ " : " branches ≠ ") + warnings.MajorityLength!.Value.ToString(CultureInfo.InvariantCulture) + (warnings.MajorityLength == 1 ? " item" : " items"), 3);
        }

        private bool TryWarningClick(PointF point, GH_Canvas canvas)
        {
            if (!RowsAreCurrent(PanelOwner.Session.Result)) return false;
            foreach (var hit in _warningHits)
            {
                if (!hit.Bounds.Contains(point)) continue;
                var options = PanelOwner.Session.Options;
                var before = options.Clone();
                options.Search = "";
                options.SearchMode = PanelSearchMode.Text;
                options.DisplayFilterValue = false;
                options.TypeName = null;
                options.OnlyInvalid = false;
                options.BranchPath = null;
                if (hit.Kind == 0) { options.SearchMode = PanelSearchMode.Validity; options.Search = "= Null"; }
                else if (hit.Kind == 1) { options.SearchMode = PanelSearchMode.Validity; options.Search = "= Invalid"; }
                else if (hit.Kind == 2) { options.SearchMode = PanelSearchMode.Validity; options.Search = "= Unverified"; options.DisplayFilterValue = false; }
                else { options.SearchMode = PanelSearchMode.ListLength; options.Search = "!= " + PanelOwner.Session.Result.Warnings.MajorityLength!.Value.ToString(CultureInfo.InvariantCulture); options.DisplayFilterValue = false; }
                PanelOwner.RecordQueryUndo("Panel+ Data checks", before, options);
                ClearRowSelection(canvas);
                PanelOwner.Session.UpdateQuery(options);
                PanelOwner.OnPingDocument()?.Modified();
                InvalidatePaintedHits();
                canvas.Invalidate();
                return true;
            }
            return false;
        }
    }
}
#pragma warning restore CA1416
