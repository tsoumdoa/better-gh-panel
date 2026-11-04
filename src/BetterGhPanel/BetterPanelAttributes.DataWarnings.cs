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

        private float WarningStripHeight(float footerHeight) => PanelOwner.Session.HasCurrentResult &&
            PanelOwner.Session.Result.Warnings.Any && _panel.Height - 56 - footerHeight - (PanelOwner.PanelSplit ? 14 : 0) >= 34 ? 17 : 0;

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
            float x = _list.X + 2, y = _panel.Y + 48;
            float right = PanelOwner.PanelSplit ? _panel.X + _panel.Width / 2 - 19 : _list.Right;
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
            if (!ResultIsCurrent(PanelOwner.Session.Result)) return false;
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
                PanelOwner.RecordQueryUndo("Panel+ check data", before, options);
                ClearRowSelection(canvas);
                PanelOwner.Session.UpdateQuery(options);
                PanelOwner.OnPingDocument()?.Modified();
                InvalidatePaintedHits();
                canvas.Invalidate();
                return true;
            }
            return false;
        }

        private void DrawBranchExtras(Graphics graphics, PanelQueryResult result, RectangleF bounds, string path,
            Font mono, Font small, StringFormat trim, string? countText = null, bool paired = false)
        {
            float countWidth = countText == null ? 96 : System.Math.Min(bounds.Width / 2, graphics.MeasureString(countText, small).Width + 6);
            float x = bounds.X + 8 + System.Math.Min(graphics.MeasureString(path, mono).Width, System.Math.Max(0, bounds.Width - countWidth - 42));
            var copy = x + 17 < bounds.Right - countWidth - 8 ? new RectangleF(x, bounds.Y, 17, bounds.Height) : RectangleF.Empty;
            if (_hoverBounds == bounds && !copy.IsEmpty)
            {
                using var pen = new Pen(Accent, 1);
                using var back = new System.Drawing.Drawing2D.GraphicsPath();
                using var front = new System.Drawing.Drawing2D.GraphicsPath();
                void Square(System.Drawing.Drawing2D.GraphicsPath shape, float left, float top)
                {
                    shape.AddArc(left, top, 2, 2, 180, 90); shape.AddArc(left + 6, top, 2, 2, 270, 90);
                    shape.AddArc(left + 6, top + 6, 2, 2, 0, 90); shape.AddArc(left, top + 6, 2, 2, 90, 90); shape.CloseFigure();
                }
                Square(back, x + 5, bounds.Y + 3); Square(front, x + 2, bounds.Y + 6);
                graphics.DrawPath(pen, back);
                using var brush = new SolidBrush(PanelTheme.Rgb(236, 241, 235));
                graphics.FillPath(brush, front); graphics.DrawPath(pen, front);
            }
            if (!paired && countText != null && result.Warnings.IsOutlier(result.Snapshot, path))
                Text(graphics, countText, small, PanelTheme.Warning, new RectangleF(bounds.Right - countWidth - 6, bounds.Y, countWidth, bounds.Height), trim);
            if (TargetPainted(graphics, bounds)) AddBranchHit(bounds, path, copy);
        }
    }
}
#pragma warning restore CA1416
