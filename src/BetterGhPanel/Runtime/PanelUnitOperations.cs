using System;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel.Runtime
{
    internal static class PanelUnitOperations
    {
        internal static GH_Structure<IGH_Goo> Apply(GH_Structure<IGH_Goo> source, PanelUnitConversion conversion)
            => conversion == PanelUnitConversion.None ? source : ApplyCaptured(source, conversion, CapturedScalar.Capture(source));

        internal static GH_Structure<IGH_Goo> ApplyCaptured(GH_Structure<IGH_Goo> source, PanelUnitConversion conversion,
            CapturedScalar[] captured, double roundUpFactor = 0, int decimals = PanelUnits.AutomaticDecimals)
        {
            bool transform = conversion != PanelUnitConversion.None || roundUpFactor != 0 || decimals >= 0;
            if (!transform && !Array.Exists(captured, value => value.IsNegativeZero)) return source;
            var result = new GH_Structure<IGH_Goo>();
            int position = 0;
            for (int b = 0; b < source.PathCount; b++)
            {
                var path = new GH_Path(source.Paths[b]);
                result.EnsurePath(path);
                foreach (var item in source.Branches[b])
                {
                    IGH_Goo converted = item;
                    // Invalid scalar goo can contain finite unset sentinels. Scaling
                    // those would turn an invalid input into an ordinary valid number.
                    var value = captured[position++];
                    if (value.IsNumeric && value.IsValid && (transform || value.IsNegativeZero) &&
                        !(value.IsInteger && conversion == PanelUnitConversion.None && roundUpFactor == 0))
                    {
                        double number = PanelUnits.Transform(value.NumericValue, conversion, roundUpFactor, decimals);
                        converted = double.IsNaN(number) || double.IsInfinity(number) ? null! : new GH_Number(number);
                    }
                    result.Append(converted, path);
                }
            }
            return result;
        }

        internal static bool InputsStillMatch(GH_Structure<IGH_Goo>? source, GH_Structure<IGH_Goo>? converted, PanelUnitConversion conversion, double roundUpFactor = 0, int decimals = PanelUnits.AutomaticDecimals)
            => InputsStillMatchWithVisits(source, converted, conversion, out _, roundUpFactor, decimals);

        internal static bool InputsStillMatchWithVisits(GH_Structure<IGH_Goo>? source, GH_Structure<IGH_Goo>? converted,
            PanelUnitConversion conversion, out int visits, double roundUpFactor = 0, int decimals = PanelUnits.AutomaticDecimals)
        {
            visits = 0;
            if (source == null || converted == null || source.PathCount != converted.PathCount) return false;
            for (int b = 0; b < source.PathCount; b++)
            {
                if (source.Paths[b].CompareTo(converted.Paths[b]) != 0 || source.Branches[b].Count != converted.Branches[b].Count) return false;
                for (int i = 0; i < source.Branches[b].Count; i++)
                {
                    visits++;
                    var original = source.Branches[b][i]; var result = converted.Branches[b][i];
                    if (conversion == PanelUnitConversion.None && roundUpFactor == 0 && decimals < 0 &&
                        !(original is GH_Number signed && signed.Value == 0 && BitConverter.DoubleToInt64Bits(signed.Value) < 0)) { if (!ReferenceEquals(original, result)) return false; continue; }
                    if (original?.GetType() == typeof(GH_Number))
                    {
                        var number = (GH_Number)original;
                        if (!number.IsValid) { if (!ReferenceEquals(original, result)) return false; continue; }
                        double expected = PanelUnits.Transform(number.Value, conversion, roundUpFactor, decimals);
                        if (double.IsNaN(expected) || double.IsInfinity(expected)) { if (result != null) return false; continue; }
                        if (result?.GetType() != typeof(GH_Number) ||
                            BitConverter.DoubleToInt64Bits(((GH_Number)result).Value) != BitConverter.DoubleToInt64Bits(expected)) return false;
                    }
                    else if (original?.GetType() == typeof(GH_Integer))
                    {
                        if (conversion == PanelUnitConversion.None && roundUpFactor == 0)
                        { if (!ReferenceEquals(original, result)) return false; continue; }
                        var integer = (GH_Integer)original;
                        if (!integer.IsValid) { if (!ReferenceEquals(original, result)) return false; continue; }
                        if (result?.GetType() != typeof(GH_Number) ||
                            BitConverter.DoubleToInt64Bits(((GH_Number)result).Value) != BitConverter.DoubleToInt64Bits(PanelUnits.Transform(integer.Value, conversion, roundUpFactor, decimals))) return false;
                    }
                    else if (!ReferenceEquals(original, result)) return false;
                }
            }
            return true;
        }
    }
}
