using System;
using System.Globalization;
using System.Text;

namespace BetterGhPanel.Core
{
    // Presentation reads completed aggregates only. Opening or painting Stats never scans rows.
    internal static class CanvasStatistics
    {
        internal const int OverviewRows = 9;
        internal const string Scope = "Statistics describe the current results, after tree tools, unit conversion and presentation rounding. " +
            "With Create set, statistics describe the displayed output values; source counts after filters are listed separately. " +
            "Orange row marks mean invalid; blue means unverified. Set row marks describe their source occurrences. " +
            "Totals describe the tree before filters and sets. Item counts include all data types.";
        internal const string UniqueScope = "Unique values include valid finite Number/Integer values and Text values. " +
            "Numbers group by presented text. Text preserves case and whitespace; text and numbers stay separate. " +
            "Null, invalid, nonfinite and other data types are excluded. Values appear in first source occurrence order. " +
            "Numeric equality and frequency use presented numbers, including explicit decimal places. Hover a value for its first raw value and merge count.";

        internal static string Summary(PanelQueryResult result) =>
            Count(result.Statistics.ItemCount, Total(result)) + (result.DisplaySet?.IsBranchSet == true ? " values · " : " items · ") +
            Count(result.Statistics.BranchCount, result.Snapshot.Branches.Count) + (result.Snapshot.Branches.Count == 1 ? " branch" : " branches");

        internal static string BranchCount(PanelQueryResult result, string path)
        {
            result.Statistics.BranchItemCounts.TryGetValue(path, out var matched);
            result.Snapshot.BranchItemCounts.TryGetValue(path, out var total);
            return Count(matched, total) + (total == 1 ? " item" : " items");
        }

        internal static string SourceStatuses(PanelSourceStatusCounts counts) =>
            FormattableString.Invariant($"{counts.VerifiedValid:N0} valid · {counts.VerifiedInvalid:N0} invalid · {counts.Unverified:N0} unverified");

        internal static PanelSourceStatusCounts RowSourceStatuses(PanelQueryResult result, int position)
        {
            var occurrences = result.SetOccurrencesAt(position);
            if (occurrences != null) return occurrences.SourceStatuses;
            var validity = result.Snapshot.Rows[position].Validity;
            return new PanelSourceStatusCounts(validity == PanelValidity.VerifiedValid ? 1 : 0,
                validity == PanelValidity.VerifiedInvalid ? 1 : 0, validity == PanelValidity.Unverified ? 1 : 0);
        }

        internal static string Label(PanelQueryResult result, int index, int decimals, bool full = false)
        {
            var stats = result.Statistics;
            switch (index)
            {
                case 0: return (result.DisplaySet?.IsBranchSet == true ? "Values: " : "Items: ") + FormattableString.Invariant($"{stats.ItemCount:N0} of {Total(result):N0}");
                case 1: return FormattableString.Invariant($"Branches: {stats.BranchCount:N0} of {result.Snapshot.Branches.Count:N0}");
                case 2: return FormattableString.Invariant($"Numbers: {stats.NumericCount:N0} · Min: {Number(stats.Minimum, result, decimals)}");
                case 3: return "Max: " + Number(stats.Maximum, result, decimals);
                case 4: return "Average: " + Number(stats.Mean, result, decimals);
                case 5: return FormattableString.Invariant($"{stats.NullCount:N0} null · {stats.InvalidCount:N0} invalid · {stats.UnverifiedCount:N0} unverified · {stats.TypeCounts.Count:N0} types");
                case 6: return FormattableString.Invariant($"Unique: {stats.UniqueValues.Count:N0} values in {stats.ScalarCount:N0} number/text items");
                case 7: return FormattableString.Invariant($"Excluded from unique values: {stats.UniqueExcludedCount:N0}");
                case 8: return "Sources after filters: " + SourceStatuses(stats.FilteredSourceStatuses);
            }
            var value = stats.UniqueValues[index - OverviewRows];
            string text = value.NumericValue.HasValue
                ? "Number: " + (full ? FormatNumber(value.NumericValue.Value, result, decimals, unique: true) : value.PresentedText ?? FormatNumber(value.NumericValue.Value, result, decimals))
                : "Text: " + Quote(value.StringValue!, full);
            if (full && value.DistinctCount > 1) text = "Merged from " + value.DistinctCount + " distinct values; first raw value: " + text;
            return FormattableString.Invariant($"{value.Count:N0}×  {text}");
        }

        private static string Count(int matched, int total) => matched == total ? FormattableString.Invariant($"{total:N0}") : FormattableString.Invariant($"{matched:N0}/{total:N0}");
        private static int Total(PanelQueryResult result) => result.DisplaySet?.IsBranchSet == true ? result.Snapshot.Branches.Count : result.Snapshot.Rows.Count;
        private static string Number(double? value, PanelQueryResult result, int decimals) => value.HasValue
            ? FormatNumber(value.Value, result, decimals) : "—";
        private static string FormatNumber(double value, PanelQueryResult result, int decimals, bool unique = false) =>
            (unique && decimals < 0 ? PanelUnits.FormatRoundTrip(value) : PanelUnits.Format(value, PanelUnitConversion.None, decimals, result.Statistics.AutomaticFormat)) + (result.Statistics.NumericUnits.Length == 0 ? "" : " " + result.Statistics.NumericUnits);

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
