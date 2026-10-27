using System;

namespace BetterGhPanel.Core
{
    /// <summary>Authoritative row text shared by the canvas and text/regex filtering.</summary>
    public static class PanelValuePresentation
    {
        public static string FormatDisplay(PanelRow row, PanelUnitConversion conversion, int decimals,
            PanelGeometryInfo? measuredGeometry = null, IFormatProvider? culture = null)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            if (row.Validity == PanelValidity.VerifiedInvalid) return "Invalid · " + row.DisplayValue;
            var geometry = measuredGeometry ?? row.GeometryInfo;
            if (geometry != null) return geometry.FormatCompact(conversion, decimals) +
                (geometry.MeasurementUnavailable ? " · measurement unavailable" : "");
            return !row.IsNull && row.NumericValue.HasValue && (conversion != PanelUnitConversion.None || decimals >= 0)
                ? PanelUnits.Format(row.NumericValue.Value, conversion, decimals, culture)
                : row.DisplayValue;
        }

        /// <summary>Visual indicator only; search, copy and set keys use the unmodified semantic value.</summary>
        public static string Visualize(string semanticValue) => semanticValue.Length == 0 ? "<empty>" : semanticValue;
    }
}
