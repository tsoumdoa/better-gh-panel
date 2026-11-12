using System;
using System.Drawing;
using System.Linq;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Grasshopper.GUI.Canvas;

#pragma warning disable CA1416
namespace BetterGhPanel
{
    internal sealed partial class BetterPanelAttributes
    {
        private string? _formulaDraft;
        private string _formulaError = "";
        private PanelFormula? _draftFormula;
        private float _formulaEditorHeight;
        private int _formulaOffset;
        private PanelQueryResult? _formulaOpeningResult;

        private float FormulaFooterHeight(float height) => _formulaDraft == null ? height : Math.Max(height, _formulaEditorHeight);

        private void EditFormula(GH_Canvas canvas)
        {
            CanvasTextInput.CloseActive(PanelOwner);
            CanvasTextInput.CompletePending();
            _formulaOffset = _offset;
            _formulaOpeningResult = PanelOwner.Session.Result;
            PanelOwner.BeginFormulaEdit();
            _formulaDraft = PanelOwner.Formula?.Text ?? "x";
            // The native overlay has a minimum screen height and a two-pixel
            // border. Keep it below the strip at high DPI and low canvas zoom.
            _formulaEditorHeight = (Grasshopper.Global_Proc.UiAdjust(24) + 2) / canvas.Viewport.Zoom;
            PanelFormula.TryCompile(_formulaDraft, out _draftFormula, out _formulaError);
            Layout();
            // The chip keeps its committed width until the editor closes, so the
            // host overlay does not move when a live preview changes the rows.
            CanvasTextInput.Show(canvas, PanelOwner, _layout.Units, _formulaDraft, text =>
            {
                bool valid = PanelFormula.TryCompile(text, out var formula, out _);
                FinishFormulaEdit(formula, commit: valid);
            }, changed: text =>
            {
                _formulaDraft = text;
                if (PanelFormula.TryCompile(text, out _draftFormula, out _formulaError)) PanelOwner.PreviewFormula(_draftFormula!);
                canvas.Invalidate();
            }, abandoned: () => FinishFormulaEdit(null, commit: false),
                valid: text => PanelFormula.TryCompile(text, out _, out _), alignRight: true);
            canvas.Invalidate();
        }

        private void FinishFormulaEdit(PanelFormula? formula, bool commit)
        {
            _formulaDraft = null;
            _draftFormula = null;
            _formulaError = "";
            _formulaEditorHeight = 0;
            _formulaOpeningResult = null;
            if (!commit) _viewRestore = new ViewRestore(PanelOwner.Session.Snapshot, _formulaOffset, DisplayListLengths);
            PanelOwner.EndFormulaEdit(formula, commit);
            Layout();
            Grasshopper.Instances.ActiveCanvas?.Invalidate();
        }

        private void RenderFormulaFeedback(Graphics g, PanelQueryResult result, Font small, StringFormat trim)
        {
            if (_formulaDraft == null) return;
            var bounds = new RectangleF(_list.X, _layout.Footer.Top - 24, _list.Width, 23);
            bool valid = _draftFormula != null;
            Fill(g, valid ? PanelTheme.Rgb(247, 250, 248) : PanelTheme.Rgb(253, 243, 238), bounds);
            using var line = new Pen(PanelTheme.Rgb(199, 204, 199));
            g.DrawLine(line, bounds.Left, bounds.Top, bounds.Right, bounds.Top);
            string message = valid ? "ƒ " + _draftFormula!.Pretty : "⚠ " + _formulaError;
            if (valid && ResultIsCurrent(result))
            {
                var options = PanelOwner.Session.Options;
                // Read at most the selected rows and the painted viewport, never
                // scan a large hidden tree just to produce an example.
                foreach (int position in _selection.OrderedPositions(result).Take(1).Concat(_rowHits.OrderBy(hit => hit.Bounds.Top).Select(hit => hit.SourcePosition)).Distinct())
                {
                    var row = result.Snapshot.Rows[position];
                    var geometry = PanelOwner.Session.GetGeometryDetails(result.Snapshot, position);
                    if (!PanelFilterValue.TryFormulaInput(result.Snapshot, row, options, geometry, out double before, out string unit)) continue;
                    double after = PanelUnits.Transform(before, PanelUnitConversion.None, PanelOwner.RoundUpFactor, PanelOwner.DecimalPlaces, _draftFormula);
                    string shown = PanelUnits.Format(after, PanelUnitConversion.None, PanelOwner.DecimalPlaces);
                    message += " · " + PanelUnits.Format(before, PanelUnitConversion.None, -1) + " → " + shown + (unit.Length > 0 && shown != "unavailable" ? " " + unit : "");
                    break;
                }
            }
            string keys = valid ? "Enter applies · Esc restores" : PanelOwner.PresentedFormula != null ? "rows keep ƒ " + PanelOwner.PresentedFormula.Pretty : "rows unchanged";
            float keyWidth = Math.Min(bounds.Width * .46f, TightWidth(g, keys, small));
            var keyBounds = new RectangleF(bounds.Right - keyWidth - 4, bounds.Y, keyWidth, bounds.Height);
            using var right = new StringFormat(Tight) { Alignment = StringAlignment.Far };
            Text(g, keys, small, Muted, keyBounds, right);
            Text(g, message, small, valid ? Accent : PanelTheme.Warning,
                new RectangleF(bounds.X + 4, bounds.Y, Math.Max(0, keyBounds.Left - bounds.X - 12), bounds.Height), trim);
        }
    }
}
#pragma warning restore CA1416
