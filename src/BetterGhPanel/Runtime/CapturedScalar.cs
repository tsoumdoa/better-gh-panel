using System;
using System.Globalization;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;

namespace BetterGhPanel.Runtime
{
    // Temporary immutable scalar values taken before callback-bearing capture.
    // Strings are referenced, never copied; no scalar array survives the solve.
    internal readonly struct CapturedScalar
    {
        private const byte None = 0, Number = 1, Integer = 2, Text = 3, Boolean = 4;
        private readonly byte _kind;
        private readonly double _number;
        private readonly string? _text;
        private readonly bool _valid;
        private CapturedScalar(byte kind, double number, string? text, bool valid)
        { _kind = kind; _number = number; _text = text; _valid = valid; }
        internal bool Available => _kind != None;
        internal bool IsNumeric => _kind == Number || _kind == Integer;
        internal bool IsValid => _valid;
        internal double NumericValue => _number;
        internal PanelValidity Validity => _valid ? PanelValidity.VerifiedValid : PanelValidity.VerifiedInvalid;

        internal static CapturedScalar[] Capture(GH_Structure<IGH_Goo> tree)
        {
            var values = new CapturedScalar[tree.DataCount];
            int position = 0;
            for (int b = 0; b < tree.PathCount; b++)
                foreach (var item in tree.Branches[b])
                {
                    if (item == null) { position++; continue; }
                    Type type = item.GetType();
                    if (type == typeof(GH_Number))
                    {
                        double value = ((GH_Number)item).Value;
                        values[position] = new CapturedScalar(Number, value, null, RhinoMath.IsValidDouble(value));
                    }
                    else if (type == typeof(GH_Integer)) values[position] = new CapturedScalar(Integer, ((GH_Integer)item).Value, null, true);
                    else if (type == typeof(GH_String))
                    {
                        string? value = ((GH_String)item).Value;
                        values[position] = new CapturedScalar(Text, 0, value, value != null);
                    }
                    else if (type == typeof(GH_Boolean)) values[position] = new CapturedScalar(Boolean, ((GH_Boolean)item).Value ? 1 : 0, null, true);
                    position++;
                }
            return values;
        }

        internal static CapturedScalar[] Convert(CapturedScalar[] source, PanelUnitConversion conversion)
        {
            if (conversion == PanelUnitConversion.None) return source;
            var result = (CapturedScalar[])source.Clone();
            for (int i = 0; i < result.Length; i++)
            {
                var value = source[i];
                if (!value.IsNumeric || !value.IsValid) continue;
                double converted = PanelUnits.Convert(value._number, conversion);
                result[i] = new CapturedScalar(Number, converted, null, RhinoMath.IsValidDouble(converted));
            }
            return result;
        }

        internal string Format(out double? numeric, out string? text)
        {
            numeric = null; text = null;
            if (_kind == Number) { numeric = _number; return _number.ToString("G12", CultureInfo.InvariantCulture); }
            if (_kind == Integer) { numeric = _number; return ((int)_number).ToString(CultureInfo.InvariantCulture); }
            if (_kind == Text) { text = _text; return _text ?? "<null text>"; }
            if (_kind == Boolean) return _number != 0 ? "True" : "False";
            return "<unavailable value>";
        }
    }
}
