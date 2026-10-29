using System;
using System.Globalization;

namespace BetterGhPanel.Core
{
    internal sealed class PanelPropertyFilter
    {
        private readonly string _operator, _text;
        private readonly double _number;
        private readonly DateTime _date;
        private readonly PanelPropertyKind _kind;
        internal PanelPropertyFilter(PanelSearchMode mode, string query)
        {
            _kind = PanelProperties.Kind(mode);
            string input = query.TrimStart();
            _operator = "=";
            foreach (string op in new[] { ">=", "<=", "!=", "==", ">", "<", "=" })
                if (input.StartsWith(op, StringComparison.Ordinal)) { _operator = op; input = input.Substring(op.Length); break; }
            _text = input.Trim();
            if (_kind == PanelPropertyKind.Text)
            {
                if (_operator != "=" && _operator != "==" && _operator != "!=") throw new ArgumentException("Text properties support = and !=. Enter the exact text, optionally in double quotes.");
                if (_text.Length >= 2 && _text[0] == '"' && _text[_text.Length - 1] == '"') _text = _text.Substring(1, _text.Length - 2).Replace("\"\"", "\"");
            }
            else if (_kind == PanelPropertyKind.Boolean)
            {
                if ((_operator != "=" && _operator != "==" && _operator != "!=") || !bool.TryParse(_text, out bool value))
                    throw new ArgumentException("Boolean properties support = True, = False, != True or != False.");
                _number = value ? 1 : 0;
            }
            else if (_kind == PanelPropertyKind.DateTime)
            {
                if (!DateTime.TryParseExact(_text, new[] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _date))
                    throw new ArgumentException("Enter a date as yyyy-MM-dd or yyyy-MM-dd HH:mm:ss (optional fractional seconds).");
            }
            else if (!double.TryParse(_text, NumberStyles.Float, CultureInfo.InvariantCulture, out _number) || double.IsNaN(_number) || double.IsInfinity(_number))
                throw new ArgumentException("Enter a finite number with >, >=, <, <=, = or !=.");
        }
        internal bool Matches(PanelPropertyValue value, PanelUnitConversion conversion)
        {
            if (value.Kind != _kind) return false;
            int comparison;
            if (_kind == PanelPropertyKind.Text) comparison = string.CompareOrdinal(value.Text, _text);
            else if (_kind == PanelPropertyKind.DateTime) comparison = value.Date.Ticks.CompareTo(_date.Ticks);
            else
            {
                double number = value.Converted(conversion);
                if (double.IsNaN(number) || double.IsInfinity(number)) return false;
                comparison = number.CompareTo(_number);
            }
            return _operator switch { ">" => comparison > 0, ">=" => comparison >= 0, "<" => comparison < 0, "<=" => comparison <= 0, "!=" => comparison != 0, _ => comparison == 0 };
        }
    }
}
