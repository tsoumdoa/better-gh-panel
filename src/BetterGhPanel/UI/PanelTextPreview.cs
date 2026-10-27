using System;
using System.Text;

namespace BetterGhPanel.UI
{
    // Native tooltips cannot scroll. Bound both explicit lines and text length;
    // never cut a UTF-16 surrogate pair or a CRLF between the two characters.
    internal static class PanelTextPreview
    {
        internal const int MaximumCharacters = 320;
        internal const int MaximumLines = 6;

        internal static string ForTooltip(string? value)
        {
            if (value == null || value.Length == 0) return string.Empty;
            var preview = new StringBuilder(Math.Min(value.Length, MaximumCharacters));
            int index = 0, lines = 1;
            while (index < value.Length)
            {
                char current = value[index];
                bool newline = current == '\r' || current == '\n' || current == '\u0085' || current == '\u2028' || current == '\u2029';
                int length = index + 1 < value.Length &&
                    (current == '\r' && value[index + 1] == '\n' ||
                     char.IsHighSurrogate(current) && char.IsLowSurrogate(value[index + 1])) ? 2 : 1;
                if (preview.Length + length > MaximumCharacters || newline && lines >= MaximumLines) break;
                preview.Append(value, index, length);
                index += length;
                if (newline) lines++;
            }
            return index < value.Length ? preview.Append('…').ToString() : preview.ToString();
        }
    }
}
