using System;

namespace BetterGhPanel.Core
{
    /// <summary>Authoritative row text shared by the canvas and text/regex filtering.</summary>
    public static class PanelValuePresentation
    {
        public static string FormatDisplay(PanelRow row, PanelUnitConversion conversion, int decimals,
            PanelGeometryInfo? measuredGeometry = null, IFormatProvider? culture = null, double roundUpFactor = 0)
            => DisplayText(row, conversion, decimals, measuredGeometry, culture, roundUpFactor).ToString();

        internal static PanelText DisplayText(PanelRow row, PanelUnitConversion conversion, int decimals,
            PanelGeometryInfo? measuredGeometry = null, IFormatProvider? culture = null, double roundUpFactor = 0)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            if (row.Validity == PanelValidity.VerifiedInvalid) return row.GroupSummary != null
                ? new PanelText(row.GroupSummary, true) : new PanelText("Invalid · " + row.DisplayValue);
            var geometry = measuredGeometry ?? row.GeometryInfo;
            if (geometry != null) return new PanelText(geometry.FormatCompact(conversion, decimals, roundUpFactor) +
                (geometry.MeasurementUnavailable ? " · measurement unavailable" : ""));
            if (!row.IsNull && row.NumericValue.HasValue)
                return new PanelText(PanelUnits.Format(row.NumericValue.Value, conversion, decimals, culture));
            return row.GroupSummary != null ? new PanelText(row.GroupSummary, false) : new PanelText(row.DisplayValue);
        }

        /// <summary>Visual indicator only; search, copy and set keys use the unmodified semantic value.</summary>
        public static string Visualize(string semanticValue) => semanticValue.Length == 0 ? "<empty>" : semanticValue;
    }
}
