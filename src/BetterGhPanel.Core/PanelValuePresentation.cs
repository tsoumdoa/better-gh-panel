using System;

namespace BetterGhPanel.Core
{
    /// <summary>Authoritative row text shared by the canvas and text/regex filtering.</summary>
    public static class PanelValuePresentation
    {
        public static string FormatDisplay(PanelRow row, PanelUnitConversion conversion, int decimals,
            PanelGeometryInfo? measuredGeometry = null, double roundUpFactor = 0, PanelFormula? formula = null)
            => DisplayText(row, conversion, decimals, measuredGeometry, roundUpFactor, formula).ToString();

        internal static PanelText DisplayText(PanelRow row, PanelUnitConversion conversion, int decimals,
            PanelGeometryInfo? measuredGeometry = null, double roundUpFactor = 0, PanelFormula? formula = null)
            => DisplayText(row, conversion, decimals, out _, measuredGeometry, roundUpFactor, formula);

        internal static PanelText DisplayText(PanelRow row, PanelUnitConversion conversion, int decimals, out bool unavailable,
            PanelGeometryInfo? measuredGeometry = null, double roundUpFactor = 0, PanelFormula? formula = null)
        {
            unavailable = false;
            if (row == null) throw new ArgumentNullException(nameof(row));
            if (row.Validity == PanelValidity.VerifiedInvalid) return row.GroupSummary != null
                ? new PanelText(row.GroupSummary, true) : new PanelText("Invalid · " + (row.NumericSummary?.Format(decimals) ?? row.DisplayValue));
            if (row.NumericSummary != null) return new PanelText(row.NumericSummary.Format(decimals));
            var geometry = measuredGeometry ?? row.GeometryInfo;
            if (geometry != null) return new PanelText(geometry.FormatCompact(conversion, decimals, roundUpFactor, formula, out unavailable) +
                (geometry.MeasurementUnavailable ? " · measurement unavailable" : ""));
            if (!row.IsNull && row.NumericValue.HasValue)
            {
                double number = PresentedNumber(row, decimals, roundUpFactor, formula);
                unavailable = double.IsNaN(number) || double.IsInfinity(number);
                return new PanelText(PanelUnits.Format(number, conversion, decimals));
            }
            return row.GroupSummary != null ? new PanelText(row.GroupSummary, false) : new PanelText(row.DisplayValue);
        }

        // Captured scalar input is already converted, but never rounded or
        // formula-adjusted. Queries can preview another formula on that input.
        public static double PresentedNumber(PanelRow row, int decimals, double roundUpFactor = 0, PanelFormula? formula = null) =>
            PanelUnits.Transform(row.RawNumericValue ?? double.NaN, PanelUnitConversion.None, roundUpFactor, decimals, formula);

        /// <summary>Visual indicator only; search, copy and set keys use the unmodified semantic value.</summary>
        public static string Visualize(string semanticValue) => semanticValue.Length == 0 ? "<empty>" : semanticValue;
    }
}
