using System.Globalization;
using System.Text;
using BetterGhPanel.Core;

namespace BetterGhPanel.UI
{
    // Presentation reads completed aggregates only. Opening or painting Stats never scans rows.
    internal static class CanvasStatistics
    {
        internal const int OverviewRows = 8;
        internal const string Scope = "Statistics describe the current filtered tree, after tree tools and unit conversion. " +
            "Totals describe that tree before filters. Item counts include all data types.";
        internal const string UniqueScope = "Unique values include valid finite Number/Integer values and Text values. " +
            "Numbers use exact numeric equality (including 1 = 1.0 and +0 = -0). Text preserves case and whitespace; text and numbers stay separate. " +
            "Null, invalid, nonfinite and other data types are excluded. Values appear in first source occurrence order. " +
            "Exact values ignore display rounding; hover a value for its complete text.";

        internal static string Summary(PanelQueryResult result) =>
            Count(result.Statistics.ItemCount, result.Snapshot.Rows.Count) + " items · " +
            Count(result.Statistics.BranchCount, result.Snapshot.Branches.Count) + " branches";

        internal static string BranchCount(PanelQueryResult result, string path)
        {
            result.Statistics.BranchItemCounts.TryGetValue(path, out var matched);
            result.Snapshot.BranchItemCounts.TryGetValue(path, out var total);
            return Count(matched, total) + " items";
        }

        internal static string Label(PanelQueryResult result, int index, int decimals, bool full = false)
        {
            var stats = result.Statistics;
            switch (index)
            {
                case 0: return $"Items: {stats.ItemCount:N0} of {result.Snapshot.Rows.Count:N0}";
                case 1: return $"Branches: {stats.BranchCount:N0} of {result.Snapshot.Branches.Count:N0}";
                case 2: return $"Numbers: {stats.NumericCount:N0} · Min: {Number(stats.Minimum, result, decimals)}";
                case 3: return "Max: " + Number(stats.Maximum, result, decimals);
                case 4: return "Average: " + Number(stats.Mean, result, decimals);
                case 5: return $"{stats.NullCount:N0} null · {stats.InvalidCount:N0} invalid · {stats.TypeCounts.Count:N0} types";
                case 6: return $"Unique: {stats.UniqueValues.Count:N0} values in {stats.ScalarCount:N0} number/text items";
                case 7: return $"Excluded from unique values: {stats.UniqueExcludedCount:N0}";
            }
            var value = stats.UniqueValues[index - OverviewRows];
            string text = value.NumericValue.HasValue
                ? "Number: " + PanelUnits.Format(value.NumericValue.Value, result.Snapshot.UnitConversion,
                    PanelUnits.AutomaticDecimals, CultureInfo.InvariantCulture, "R")
                : "Text: " + Quote(value.StringValue!, full);
            return $"{value.Count:N0}×  {text}";
        }

        private static string Count(int matched, int total) => matched == total ? $"{total:N0}" : $"{matched:N0}/{total:N0}";
        private static string Number(double? value, PanelQueryResult result, int decimals) => value.HasValue
            ? PanelUnits.Format(value.Value, result.Snapshot.UnitConversion, decimals, automaticFormat: "G5") : "—";

        private static string Quote(string value, bool full)
        {
            // Bound per-frame text work, while the tooltip can inspect the exact full string on demand.
            bool shortened = !full && value.Length > 120;
            int length = shortened ? 120 : value.Length;
            var text = new StringBuilder("\"");
            for (int index = 0; index < length; index++)
            {
                char character = value[index];
                switch (character)
                {
                    case '\\': text.Append("\\\\"); break;
                    case '\"': text.Append("\\\""); break;
                    case '\r': text.Append("\\r"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (char.IsControl(character)) text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else text.Append(character);
                        break;
                }
            }
            if (shortened) text.Append('…');
            return text.Append('\"').ToString();
        }
    }
}
