using System;

namespace BetterGhPanel.Core
{
    internal enum PanelPreviewMode { Selected, FilteredResults, Off, PassFail }
    internal enum PanelNoMatchStyle { Red, Grey, Ghosted }

    internal static class PanelMatchLegend
    {
        internal static (string Match, string NoMatch) Format(int matches, int total)
        {
            string match = FormattableString.Invariant($"Match {matches:N0}");
            string noMatch = FormattableString.Invariant($"No match {total - matches:N0}");
            if (total == 0) return (match, noMatch);

            int percent = (int)Math.Round(100m * matches / total, MidpointRounding.AwayFromZero);
            if (percent == 0 && matches > 0) return ($"{match} · <1%", $"{noMatch} · >99%");
            if (percent == 100 && matches < total) return ($"{match} · >99%", $"{noMatch} · <1%");
            return (FormattableString.Invariant($"{match} · {percent}%"), FormattableString.Invariant($"{noMatch} · {100 - percent}%"));
        }
    }
}
