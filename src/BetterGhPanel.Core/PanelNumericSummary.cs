using System;
using System.Text;

namespace BetterGhPanel.Core
{
    /// <summary>Bounded numeric summary data, formatted at the active display precision.</summary>
    public sealed class PanelNumericSummary
    {
        private enum Shape { Complex, Location, Matrix }
        private readonly Shape _shape;
        private readonly double[] _values;
        private readonly int _rows, _columns;
        private readonly bool _identity;
        private PanelNumericSummary(Shape shape, double[] values, int rows = 0, int columns = 0, bool identity = false)
        { _shape = shape; _values = values; _rows = rows; _columns = columns; _identity = identity; }
        public static PanelNumericSummary Complex(double real, double imaginary) => new PanelNumericSummary(Shape.Complex, new[] { real, imaginary });
        public static PanelNumericSummary Location(double latitude, double longitude) => new PanelNumericSummary(Shape.Location, new[] { latitude, longitude });
        public static PanelNumericSummary Matrix(int rows, int columns, Func<int, int, double> cell)
        {
            bool identity = rows == columns && rows > 0;
            for (int r = 0; identity && r < rows; r++) for (int c = 0; c < columns; c++)
                if (cell(r, c) != (r == c ? 1 : 0)) { identity = false; break; }
            var values = new double[(int)Math.Min(16, (long)rows * columns)];
            for (int i = 0; i < values.Length; i++) values[i] = cell(i / columns, i % columns);
            return new PanelNumericSummary(Shape.Matrix, values, rows, columns, identity);
        }
        public string Format(int decimals)
        {
            string N(double value) => PanelUnits.Format(PanelUnits.Transform(value, PanelUnitConversion.None, decimals: decimals), PanelUnitConversion.None, decimals);
            if (_shape == Shape.Complex)
                return N(_values[0]) + (PanelUnits.Transform(_values[1], PanelUnitConversion.None, decimals: decimals) < 0 ? " - " : " + ") + N(Math.Abs(_values[1])) + "i";
            if (_shape == Shape.Location)
                return N(Math.Abs(_values[0])) + "° " + (_values[0] < 0 ? "S" : "N") + ", " + N(Math.Abs(_values[1])) + "° " + (_values[1] < 0 ? "W" : "E");
            if (_identity) return "Identity";
            var text = new StringBuilder(_rows + " × " + _columns + " [");
            for (int i = 0; i < _values.Length; i++)
            {
                if (i > 0) text.Append(i % _columns == 0 ? "; " : ", ");
                text.Append(N(_values[i]));
            }
            if (_values.Length < (long)_rows * _columns) text.Append("; …");
            return text.Append(']').ToString();
        }
    }
}
